using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Controls;
using WinRT;
using WinRT.Interop;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;

namespace App.Services
{
    public sealed class ScreenRecorderService : IDisposable
    {
        private const int MaxVideoQueueDepth = 1;
        private const int MaxMicQueueDepth = 64;
        private const int TraceSampleModulo = 60;
        private const int DiagnosticIntervalMs = 2000;

        private sealed record CapturedFrame(long Id, Direct3D11CaptureFrame Frame);

        private readonly IDirect3DDevice _device;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private readonly System.Collections.Concurrent.ConcurrentQueue<CapturedFrame> _frameQueue = new();
        private readonly SemaphoreSlim _frameSemaphore = new(0);
        private CancellationTokenSource? _cts;
        private readonly object _lock = new();
        private readonly TypedEventHandler<MediaStreamSample, object> _sampleProcessedHandler;
        private readonly ConcurrentDictionary<MediaStreamSample, CapturedFrame> _pendingSamples = new();

        private MediaStreamSource? _mediaStreamSource;
        private MediaTranscoder? _transcoder;
        private PrepareTranscodeResult? _transcodeOp;

        private bool _isRecording;
        private TimeSpan? _startTime;
        private TimeSpan _finalElapsed = TimeSpan.Zero;
        private long _frameCount = 0;
        private long _droppedFrames = 0;
        private long _nextFrameId = 0;
        private System.Diagnostics.Stopwatch? _frameStopwatch;
        private string? _videoTempPath;
        private string? _audioBaseTempPath;
        private long _micSamplesReceived = 0;
        private long _micSamplesQueued = 0;
        private long _micSamplesProcessed = 0;
        private long _loopbackSamplesReceived = 0;
        private long _loopbackSamplesWritten = 0;
        private long _micEmptyCallbacks = 0;

        // Audio Recording Fields
        private WasapiLoopbackCapture? _loopbackCapture;
        private WasapiCapture? _micCapture;
        private WaveFileWriter? _loopbackWriter;
        private WaveFileWriter? _micWriter;
        private bool _isAudioRecording;
        private Task? _loopbackRecordTask;
        private Task? _micRecordTask;
        private WaveFormat? _micWaveFormat;
        private ConcurrentQueue<byte[]>? _micAudioQueue;
        private Task? _micProcessingTask;
        private Task? _diagnosticTask;

        public bool IsRecording => _isRecording;
        public TimeSpan Elapsed => _frameStopwatch?.Elapsed ?? TimeSpan.Zero;
        public double CurrentFps => Elapsed.TotalSeconds > 0 ? _frameCount / Elapsed.TotalSeconds : 0;
        public TimeSpan FinalElapsed => _finalElapsed;
        public double FinalFps => _finalElapsed.TotalSeconds > 0 ? _frameCount / _finalElapsed.TotalSeconds : 0;
        public long CurrentRecordingSizeBytes => GetCurrentRecordingSizeBytes();

        public event EventHandler<string>? RecordingStarted;
        public event EventHandler<string>? RecordingStopped;
        public event EventHandler<string>? RecordingFailed;

        public ScreenRecorderService(IDirect3DDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _sampleProcessedHandler = OnSampleProcessed;
        }

        private static void Trace(string message)
        {
            Debug.WriteLine($"[ScreenRecorder] {DateTime.Now:HH:mm:ss.fff} {message}");
        }

        private void TraceMemorySnapshot(string reason)
        {
            var process = Process.GetCurrentProcess();
            Trace(
                $"{reason} memManaged={GC.GetTotalMemory(false):N0}B " +
                $"private={process.PrivateMemorySize64:N0}B workingSet={process.WorkingSet64:N0}B " +
                $"frameQueue={_frameQueue.Count} micQueue={_micAudioQueue?.Count ?? 0} " +
                $"frames={_frameCount} dropped={_droppedFrames} micReceived={_micSamplesReceived} " +
                $"micQueued={_micSamplesQueued} micProcessed={_micSamplesProcessed} loopbackReceived={_loopbackSamplesReceived} " +
                $"loopbackWritten={_loopbackSamplesWritten}");
        }

        private void StartDiagnosticsLoop()
        {
            var token = _cts?.Token;
            if (token == null)
            {
                Trace("Diagnostics loop not started because cancellation token is unavailable");
                return;
            }

            _diagnosticTask = Task.Run(async () =>
            {
                Trace("Diagnostics loop started");
                try
                {
                    while (!token.Value.IsCancellationRequested)
                    {
                        TraceMemorySnapshot("Diagnostics");
                        await Task.Delay(DiagnosticIntervalMs, token.Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    Trace("Diagnostics loop canceled");
                }
                catch (Exception ex)
                {
                    Trace($"Diagnostics loop failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        public async Task RecordAsync(
            GraphicsCaptureItem item,
            string outputFilePath,
            uint bitrateBps = 12_000_000,
            uint frameRate = 30,
            bool includeCursor = true)
        {
            lock (_lock)
            {
                if (_isRecording)
                    throw new InvalidOperationException("A recording is already in progress.");
                _isRecording = true;
                _cts = new CancellationTokenSource();
            }

            _item = item ?? throw new ArgumentNullException(nameof(item));
            _startTime = null;

            uint width = (uint)item.Size.Width;
            uint height = (uint)item.Size.Height;
            width = (width % 2 == 0) ? width : width + 1;
            height = (height % 2 == 0) ? height : height + 1;

            string videoTempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_video.mp4");
            string audioTempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_audio.wav");
            _videoTempPath = videoTempPath;
            _audioBaseTempPath = audioTempPath;
            Trace($"RecordAsync start output='{outputFilePath}' size={width}x{height} bitrate={bitrateBps} fps={frameRate} cursor={includeCursor}");
            Trace($"Temp paths video='{videoTempPath}' audio='{audioTempPath}'");

            try
            {
                // Ensure the output folder and final destination file exist
                var folderPath = Path.GetDirectoryName(outputFilePath);
                if (folderPath == null)
                    throw new ArgumentException("Invalid output file path.", nameof(outputFilePath));

                Directory.CreateDirectory(folderPath);

                var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(folderPath);
                var fileName = Path.GetFileName(outputFilePath);
                await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                // Create the temp video file
                var tempFolder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
                var tempVideoFileName = Path.GetFileName(videoTempPath);
                var tempVideoFile = await tempFolder.CreateFileAsync(tempVideoFileName, CreationCollisionOption.ReplaceExisting);

                // Source stream descriptor: uncompressed BGRA8 matching the capture format.
                var inputProperties = VideoEncodingProperties.CreateUncompressed(
                    MediaEncodingSubtypes.Bgra8, width, height);
                inputProperties.FrameRate.Numerator = frameRate;
                inputProperties.FrameRate.Denominator = 1;

                var videoDescriptor = new VideoStreamDescriptor(inputProperties);

                _mediaStreamSource = new MediaStreamSource(videoDescriptor)
                {
                    BufferTime = TimeSpan.FromSeconds(0)
                };
                _mediaStreamSource.SampleRequested += OnSampleRequested;
                Trace("MediaStreamSource created and SampleRequested handler attached");

                // Output profile: MP4 with H.264 video at the exact capture size.
                var outputProperties = new VideoEncodingProperties
                {
                    Subtype = MediaEncodingSubtypes.H264,
                    Width = width,
                    Height = height,
                    Bitrate = bitrateBps,
                    FrameRate = { Numerator = frameRate, Denominator = 1 }
                };

                var profile = new MediaEncodingProfile
                {
                    Container = { Subtype = MediaEncodingSubtypes.Mpeg4 },
                    Video = outputProperties
                };

                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    item.Size);

                _framePool.FrameArrived += OnFrameArrived;
                Trace("Frame pool created and FrameArrived handler attached");

                _session = _framePool.CreateCaptureSession(item);
                _session.IsCursorCaptureEnabled = includeCursor;
                Trace("Capture session created");

                _transcoder = new MediaTranscoder();
                _transcoder.HardwareAccelerationEnabled = true;
                Trace("MediaTranscoder created with hardware acceleration enabled");

                using var stream = await tempVideoFile.OpenAsync(FileAccessMode.ReadWrite);
                _transcodeOp = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(
                    _mediaStreamSource, stream, profile);
                Trace($"PrepareMediaStreamSourceTranscodeAsync completed canTranscode={_transcodeOp.CanTranscode}");

                if (!_transcodeOp.CanTranscode)
                    throw new InvalidOperationException("MFT Encoder could not initialize with these configurations.");

                RecordingStarted?.Invoke(this, outputFilePath);
                Trace("RecordingStarted event fired");

                _frameStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _frameCount = 0;
                _droppedFrames = 0;
                _micSamplesReceived = 0;
                _micSamplesQueued = 0;
                _micSamplesProcessed = 0;
                _loopbackSamplesReceived = 0;
                _loopbackSamplesWritten = 0;
                _micEmptyCallbacks = 0;

                // Start loopback + mic audio recording
                StartAudioRecording(audioTempPath);
                StartDiagnosticsLoop();

                _session.StartCapture();
                Trace("Capture started; waiting for transcoder completion");

                await _transcodeOp.TranscodeAsync().AsTask();
                Trace("TranscodeAsync completed");
            }
            catch (Exception ex)
            {
                Trace($"RecordAsync exception: {ex.GetType().Name}: {ex.Message}");
                _isRecording = false;
                Cleanup();
                await StopAudioRecordingAsync();
                
                // Clean up temp files on failure
                try
                {
                    if (File.Exists(videoTempPath)) File.Delete(videoTempPath);
                    if (File.Exists(audioTempPath)) File.Delete(audioTempPath);
                }
                catch { }

                RecordingFailed?.Invoke(this, ex.Message);
                throw;
            }
            finally
            {
                if (_isRecording)
                {
                    _finalElapsed = _frameStopwatch?.Elapsed ?? TimeSpan.Zero;
                    _isRecording = false;
                    Trace($"RecordAsync finalizing elapsed={_finalElapsed} frames={_frameCount} dropped={_droppedFrames} micReceived={_micSamplesReceived} micQueued={_micSamplesQueued} micProcessed={_micSamplesProcessed} loopbackReceived={_loopbackSamplesReceived} loopbackWritten={_loopbackSamplesWritten}");
                    Cleanup();
                    await StopAudioRecordingAsync();
                    
                    // Run mux async in background WITHOUT blocking UI
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await MuxAudioVideoAsync(videoTempPath, audioTempPath, outputFilePath, width, height, bitrateBps, frameRate);
                        }
                        finally
                        {
                            Trace("Mux task finished, firing RecordingStopped");
                            RecordingStopped?.Invoke(this, outputFilePath);
                        }
                    });
                }
            }
        }

        public void Stop()
        {
            Trace("Stop requested");
            lock (_lock)
            {
                if (_isRecording)
                {
                    try
                    {
                        _cts?.Cancel();
                    }
                    catch (ObjectDisposedException) { }
                }
            }

            _session?.Dispose();
            _framePool?.Dispose();
            _session = null;
            _framePool = null;
            Trace("Stop completed");
        }

        public void Dispose()
        {
            Stop();
            Cleanup();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            var frame = sender.TryGetNextFrame();
            if (frame == null)
            {
                _droppedFrames++;
                Trace($"FrameArrived with null frame; dropped={_droppedFrames}");
                return;
            }

            var frameId = Interlocked.Increment(ref _nextFrameId);

            lock (_lock)
            {
                if (!_isRecording)
                {
                    frame.Dispose();
                    Trace($"FrameArrived ignored because recording is no longer active frameId={frameId}");
                    return;
                }
            }

            // Keep only the freshest frame to avoid GPU memory buildup when encoding lags behind capture.
            while (_frameQueue.Count >= MaxVideoQueueDepth)
            {
                if (_frameQueue.TryDequeue(out var oldFrame))
                {
                    oldFrame.Frame.Dispose();
                    _frameSemaphore.Wait(0);
                    _droppedFrames++;
                    Trace($"Video queue full; dropped stale frame id={oldFrame.Id} queueCount={_frameQueue.Count} dropped={_droppedFrames}");
                }
            }

            _frameQueue.Enqueue(new CapturedFrame(frameId, frame));
            _frameSemaphore.Release();
            if (frameId % TraceSampleModulo == 0)
            {
                Trace($"Frame enqueued frameId={frameId} queueCount={_frameQueue.Count} frameCount={_frameCount} dropped={_droppedFrames}");
            }
        }

        private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                CancellationToken token;
                lock (_lock)
                {
                    if (!_isRecording || _cts == null)
                    {
                        args.Request.Sample = null;
                        return;
                    }
                    token = _cts.Token;
                }

                await _frameSemaphore.WaitAsync(token);

                if (_frameQueue.TryDequeue(out var capturedFrame))
                {
                    _startTime ??= capturedFrame.Frame.SystemRelativeTime;
                    var timeStamp = capturedFrame.Frame.SystemRelativeTime - _startTime.Value;
                    var sample = MediaStreamSample.CreateFromDirect3D11Surface(
                        capturedFrame.Frame.Surface, timeStamp);
                    _pendingSamples[sample] = capturedFrame;
                    sample.Processed += _sampleProcessedHandler;
                    args.Request.Sample = sample;
                    
                    _frameCount++;
                    if (_frameCount % TraceSampleModulo == 0)
                    {
                        Trace($"SampleRequested produced frameId={capturedFrame.Id} frameCount={_frameCount} queueCount={_frameQueue.Count} elapsed={Elapsed}");
                    }
                }
                else
                {
                    args.Request.Sample = null;
                    Trace("SampleRequested had no frame available");
                }
            }
            catch (OperationCanceledException)
            {
                Trace("SampleRequested canceled");
                args.Request.Sample = null;
            }
            catch
        {
            Trace("SampleRequested failed");
            args.Request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

        private void OnSampleProcessed(MediaStreamSample sender, object args)
        {
            if (_pendingSamples.TryRemove(sender, out var capturedFrame))
            {
                Trace($"Sample processed frameId={capturedFrame.Id}");
                capturedFrame.Frame.Dispose();
            }

            sender.Processed -= _sampleProcessedHandler;
        }

        private void Cleanup()
        {
            Trace("Cleanup start");
            TraceMemorySnapshot("Cleanup-begin");
            lock (_lock)
            {
                _isRecording = false;
                try
                {
                    _cts?.Cancel();
                }
                catch (ObjectDisposedException) { }
                _cts?.Dispose();
                _cts = null;
            }

            _session?.Dispose();
            _framePool?.Dispose();

            // Drain queue
            while (_frameQueue.TryDequeue(out var frame))
            {
                Trace($"Cleanup disposing queued frame id={frame.Id}");
                frame.Frame.Dispose();
            }

            foreach (var entry in _pendingSamples)
            {
                if (_pendingSamples.TryRemove(entry.Key, out var capturedFrame))
                {
                    Trace($"Cleanup disposing pending sample frame id={capturedFrame.Id}");
                    capturedFrame.Frame.Dispose();
                }
            }

            // Reset semaphore count
            while (_frameSemaphore.CurrentCount > 0)
            {
                _frameSemaphore.Wait(0);
            }

            if (_mediaStreamSource != null)
            {
                _mediaStreamSource.SampleRequested -= OnSampleRequested;
            }

            _session = null;
            _framePool = null;
            _mediaStreamSource = null;
            _transcoder = null;
            _transcodeOp = null;
            _frameStopwatch = null;
            _videoTempPath = null;
            _audioBaseTempPath = null;
            TraceMemorySnapshot("Cleanup-end");
            Trace("Cleanup complete");
        }

        // ==========================================
        // Audio Capture & Mixing Implementation (NAudio)
        // ==========================================

        private void StartAudioRecording(string audioBasePath)
        {
            _isAudioRecording = true;
            string loopbackPath = audioBasePath.Replace(".wav", "_loopback.wav");
            string micPath = audioBasePath.Replace(".wav", "_mic.wav");
            _micAudioQueue = new ConcurrentQueue<byte[]>();
            Trace($"StartAudioRecording base='{audioBasePath}' loopback='{loopbackPath}' mic='{micPath}'");

            // 1. Device Sound Loopback - Independent writer
            try
            {
                _loopbackCapture = new WasapiLoopbackCapture();
                var waveFormat = _loopbackCapture.WaveFormat;
                _loopbackWriter = new WaveFileWriter(loopbackPath, waveFormat);
                Trace($"Loopback capture created format={waveFormat.Encoding} {waveFormat.SampleRate}Hz {waveFormat.Channels}ch {waveFormat.BitsPerSample}bit");

                _loopbackRecordTask = Task.Run(() =>
                {
                    try
                    {
                        _loopbackCapture.DataAvailable += (s, e) =>
                        {
                            if (_isAudioRecording && e.BytesRecorded > 0)
                            {
                                _loopbackSamplesReceived++;
                                lock (_lock)
                                {
                                    _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                                    _loopbackSamplesWritten++;
                                }
                                if (_loopbackSamplesReceived % TraceSampleModulo == 0)
                                {
                                    Trace($"Loopback callback bytes={e.BytesRecorded} received={_loopbackSamplesReceived} written={_loopbackSamplesWritten}");
                                }
                            }
                        };

                        _loopbackCapture.StartRecording();
                        Trace("Loopback recording started");
                        // Keep task alive while recording
                        while (_isAudioRecording)
                        {
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                    catch { }
                });
            }
            catch
            {
                Trace("Loopback recording setup failed");
                _loopbackCapture?.Dispose();
                _loopbackCapture = null;
            }

            // 2. Microphone Capture - Independent writer
            try
            {
                _micCapture = new WasapiCapture();
                _micWaveFormat = _micCapture.WaveFormat;
                _micWriter = new WaveFileWriter(micPath, _micWaveFormat);
                Trace($"Mic capture created format={_micWaveFormat.Encoding} {_micWaveFormat.SampleRate}Hz {_micWaveFormat.Channels}ch {_micWaveFormat.BitsPerSample}bit");

                // Start background processing task for mic audio
                _micProcessingTask = Task.Run(async () =>
                {
                    try
                    {
                        while (_isAudioRecording)
                        {
                            if (_micAudioQueue != null && _micAudioQueue.TryDequeue(out var buffer))
                            {
                                lock (_lock)
                                {
                                    _micWriter?.Write(buffer, 0, buffer.Length);
                                    _micSamplesProcessed++;
                                }
                                if (_micSamplesProcessed % TraceSampleModulo == 0)
                                {
                                    Trace($"Mic processing wrote buffer len={buffer.Length} processed={_micSamplesProcessed} queued={_micSamplesQueued}");
                                }
                                
                            }
                            else
                            {
                                await Task.Delay(1);
                            }
                        }
                    }
                    catch { }
                });

                _micRecordTask = Task.Run(() =>
                {
                    try
                    {
                        _micCapture.DataAvailable += (s, e) =>
                        {
                            if (_isAudioRecording)
                            {
                                if (e.BytesRecorded > 0)
                                {
                                    _micSamplesReceived++;
                                    // NO LOCK HERE - Queue the audio for async processing
                                    float volumeBoost = 2.0f;
                                    var boostedBuffer = new byte[e.BytesRecorded];
                                    System.Buffer.BlockCopy(e.Buffer, 0, boostedBuffer, 0, e.BytesRecorded);
                                    
                                    // If 32-bit float format, amplify the samples
                                    if (_micWaveFormat != null && _micWaveFormat.BitsPerSample == 32)
                                    {
                                        for (int i = 0; i < e.BytesRecorded; i += 4)
                                        {
                                            float sample = BitConverter.ToSingle(boostedBuffer, i);
                                            sample *= volumeBoost;
                                            sample = Math.Max(-1.0f, Math.Min(1.0f, sample));
                                            byte[] boosted = BitConverter.GetBytes(sample);
                                            Array.Copy(boosted, 0, boostedBuffer, i, 4);
                                        }
                                    }

                                    var queue = _micAudioQueue;
                                    if (queue != null)
                                    {
                                        while (queue.Count >= MaxMicQueueDepth && queue.TryDequeue(out _))
                                        {
                                        }

                                        queue.Enqueue(boostedBuffer);
                                    }
                                    _micSamplesQueued++;
                                    if (_micSamplesReceived % TraceSampleModulo == 0)
                                    {
                                        Trace($"Mic callback bytes={e.BytesRecorded} received={_micSamplesReceived} queued={_micSamplesQueued} queueDepth={_micAudioQueue?.Count ?? 0}");
                                    }
                                    
                                }
                                else
                                {
                                    _micEmptyCallbacks++;
                                    if (_micEmptyCallbacks % TraceSampleModulo == 0)
                                    {
                                        Trace($"Mic callback empty count={_micEmptyCallbacks}");
                                    }
                                }
                            }
                        };

                        _micCapture.StartRecording();
                        Trace("Mic recording started");
                        // Keep task alive while recording
                        while (_isAudioRecording)
                        {
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                    catch { }
                });
            }
            catch
            {
                Trace("Mic recording setup failed");
                _micCapture?.Dispose();
                _micCapture = null;
            }
        }

        private async Task StopAudioRecordingAsync()
        {
            Trace("StopAudioRecordingAsync start");
            TraceMemorySnapshot("StopAudio-begin");
            _isAudioRecording = false;

            if (_loopbackRecordTask != null)
            {
                try { await _loopbackRecordTask; }
                catch { }
                _loopbackRecordTask = null;
            }

            if (_micRecordTask != null)
            {
                try { await _micRecordTask; }
                catch { }
                _micRecordTask = null;
            }

            if (_micProcessingTask != null)
            {
                try { await _micProcessingTask; }
                catch { }
                _micProcessingTask = null;
            }

            try
            {
                _loopbackCapture?.StopRecording();
            }
            catch
            {
            }
            finally
            {
                lock (_lock)
                {
                    _loopbackWriter?.Dispose();
                    _loopbackWriter = null;
                }
                _loopbackCapture?.Dispose();
                _loopbackCapture = null;
            }

            if (_micAudioQueue != null)
            {
                while (_micAudioQueue.TryDequeue(out var buffer))
                {
                    Array.Clear(buffer, 0, buffer.Length);
                }
                Trace("Mic audio queue drained");
                TraceMemorySnapshot("StopAudio-afterMicDrain");
            }

            try
            {
                _micCapture?.StopRecording();
            }
            catch
            {
            }
            finally
            {
                lock (_lock)
                {
                    _micWriter?.Dispose();
                    _micWriter = null;
                }
                _micCapture?.Dispose();
                _micCapture = null;
            }
            TraceMemorySnapshot("StopAudio-end");
            Trace("StopAudioRecordingAsync complete");
        }

        private static ISampleProvider MakeStereo(ISampleProvider source, int sourceChannels)
        {
            if (sourceChannels == 1)
            {
                return new MonoToStereoSampleProvider(source);
            }
            if (sourceChannels > 2)
            {
                var multiplexer = new MultiplexingSampleProvider(new[] { source }, 2);
                multiplexer.ConnectInputToOutput(0, 0);
                multiplexer.ConnectInputToOutput(1, 1);
                return multiplexer;
            }
            return source;
        }

        private async Task MuxAudioVideoAsync(string videoPath, string audioPath, string outputPath, uint width, uint height, uint bitrateBps, uint frameRate)
        {
            try
            {
                Trace($"MuxAudioVideoAsync start video='{videoPath}' audio='{audioPath}' output='{outputPath}'");
                TraceMemorySnapshot("Mux-start");
                string loopbackPath = audioPath.Replace(".wav", "_loopback.wav");
                string micPath = audioPath.Replace(".wav", "_mic.wav");
                string mixedAudioPath = audioPath.Replace(".wav", "_mixed.wav");

                bool hasVideo = File.Exists(videoPath) && new FileInfo(videoPath).Length > 1000;
                bool hasLoopback = File.Exists(loopbackPath) && new FileInfo(loopbackPath).Length > 1000;
                bool hasMic = File.Exists(micPath) && new FileInfo(micPath).Length > 1000;

                if (!hasVideo)
                {
                    Trace("MuxAudioVideoAsync missing video file");
                    throw new FileNotFoundException("Video file was not recorded successfully.");
                }

                // Fast path: no audio, just copy video
                if (!hasLoopback && !hasMic)
                {
                    Trace("MuxAudioVideoAsync no audio tracks found; copying video only");
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Copy(videoPath, outputPath);
                    TraceMemorySnapshot("Mux-copy-video-only");
                    return;
                }

                var composition = new global::Windows.Media.Editing.MediaComposition();
                TraceMemorySnapshot("Mux-composition-created");

                var videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
                var videoClip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(videoFile);
                composition.Clips.Add(videoClip);
                TraceMemorySnapshot("Mux-video-clip-added");

                string? audioTrackPath = null;
                if (hasLoopback && hasMic)
                {
                    await MixWavFilesAsync(loopbackPath, micPath, mixedAudioPath);
                    audioTrackPath = mixedAudioPath;
                    TraceMemorySnapshot("Mux-mixed-audio-created");
                }
                else if (hasLoopback)
                {
                    audioTrackPath = loopbackPath;
                }
                else if (hasMic)
                {
                    audioTrackPath = micPath;
                }

                if (audioTrackPath != null)
                {
                    Trace($"MuxAudioVideoAsync using audioTrackPath='{audioTrackPath}'");
                    var audioFile = await StorageFile.GetFileFromPathAsync(audioTrackPath);
                    var audioTrack = await global::Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(audioFile);
                    composition.BackgroundAudioTracks.Add(audioTrack);
                    TraceMemorySnapshot("Mux-audio-track-added");
                }

                var destFile = await StorageFile.GetFileFromPathAsync(outputPath);

                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
                profile.Video.Width = width;
                profile.Video.Height = height;
                profile.Video.Bitrate = bitrateBps;
                profile.Video.FrameRate.Numerator = frameRate;
                profile.Video.FrameRate.Denominator = 1;
                profile.Audio = AudioEncodingProperties.CreateAac(44100, 2, 192000);

                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
                // Prefer preserving the full capture length over a faster trimmed render.
                TraceMemorySnapshot("Mux-before-render");
                var saveOp = composition.RenderToFileAsync(destFile, global::Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
                await saveOp.AsTask(cts.Token);
                TraceMemorySnapshot("Mux-after-render");
                Trace("MuxAudioVideoAsync render complete");
            }
            catch (OperationCanceledException)
            {
                Trace("MuxAudioVideoAsync canceled");
                if (File.Exists(videoPath) && !File.Exists(outputPath))
                {
                    File.Copy(videoPath, outputPath, true);
                }
            }
            catch
            {
                Trace("MuxAudioVideoAsync failed; falling back to raw video");
                if (File.Exists(videoPath) && !File.Exists(outputPath))
                {
                    File.Copy(videoPath, outputPath, true);
                }
            }
            finally
            {
                Trace("MuxAudioVideoAsync cleanup start");
                TraceMemorySnapshot("Mux-cleanup-start");
                try
                {
                    if (File.Exists(videoPath)) File.Delete(videoPath);
                    if (File.Exists(audioPath)) File.Delete(audioPath);
                    string loopbackPath = audioPath.Replace(".wav", "_loopback.wav");
                    string micPath = audioPath.Replace(".wav", "_mic.wav");
                    string mixedAudioPath = audioPath.Replace(".wav", "_mixed.wav");
                    if (File.Exists(loopbackPath)) File.Delete(loopbackPath);
                    if (File.Exists(micPath)) File.Delete(micPath);
                    if (File.Exists(mixedAudioPath)) File.Delete(mixedAudioPath);
                }
                catch { }
                TraceMemorySnapshot("Mux-cleanup-end");
                Trace("MuxAudioVideoAsync cleanup complete");
            }
        }

        private static async Task MixWavFilesAsync(string firstPath, string secondPath, string outputPath)
        {
            using var firstReader = new AudioFileReader(firstPath);
            using var secondReader = new AudioFileReader(secondPath);

            var mixer = new MixingSampleProvider(new[] { firstReader, secondReader })
            {
                ReadFully = false
            };

            await Task.Run(() => WaveFileWriter.CreateWaveFile16(outputPath, mixer));
        }

        private long GetCurrentRecordingSizeBytes()
        {
            long totalBytes = 0;

            totalBytes += GetExistingFileLength(_videoTempPath);

            if (!string.IsNullOrWhiteSpace(_audioBaseTempPath))
            {
                totalBytes += GetExistingFileLength(_audioBaseTempPath.Replace(".wav", "_loopback.wav"));
                totalBytes += GetExistingFileLength(_audioBaseTempPath.Replace(".wav", "_mic.wav"));
                totalBytes += GetExistingFileLength(_audioBaseTempPath);
            }

            return totalBytes;
        }

        private static long GetExistingFileLength(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return 0;

            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

    }
}



