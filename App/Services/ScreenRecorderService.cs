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
using Windows.Security.Authorization.AppCapabilityAccess;
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
        internal const string DefaultMicrophoneDeviceId = "{default}";
        private const int MaxMicQueueDepth = 64;

        private sealed record CapturedFrame(long Id, Direct3D11CaptureFrame Frame);

        private readonly IDirect3DDevice _device;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private CapturedFrame? _latestFrame;
        private readonly SemaphoreSlim _frameAvailable = new(0, 1);
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
        private long _throttledFrames = 0;
        private long _nextFrameId = 0;
        private TimeSpan _minimumFrameInterval;
        private TimeSpan? _lastAcceptedFrameTime;
        private System.Diagnostics.Stopwatch? _frameStopwatch;
        private string? _videoTempPath;
        private string? _audioBaseTempPath;
        private long _micSamplesReceived = 0;
        private long _micSamplesQueued = 0;
        private long _micSamplesProcessed = 0;
        private long _micQueuedBytes = 0;
        private long _micDroppedBuffers = 0;
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
        private WaveFormat? _micWaveFormat;
        private ConcurrentQueue<byte[]>? _micAudioQueue;
        private Task? _micProcessingTask;

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

        public static async Task<bool> EnsureMicrophoneAccessAsync(string? microphoneDeviceId)
        {
            if (string.IsNullOrWhiteSpace(microphoneDeviceId))
                return true;

            try
            {
                var accessStatus = await AppCapability.Create("microphone").RequestAccessAsync();
                return accessStatus == AppCapabilityAccessStatus.Allowed;
            }
            catch (Exception)
            {
                // Cancelling the Windows permission prompt is a normal user outcome.
                return false;
            }
        }


        public async Task RecordAsync(
            GraphicsCaptureItem item,
            string outputFilePath,
            uint bitrateBps = 12_000_000,
            uint frameRate = 30,
            bool includeCursor = true,
            string? microphoneDeviceId = null)
        {
            lock (_lock)
            {
                if (_isRecording)
                    throw new InvalidOperationException("A recording is already in progress.");
                _isRecording = true;
                _cts = new CancellationTokenSource();
            }

            _item = item ?? throw new ArgumentNullException(nameof(item));
            if (frameRate == 0)
                throw new ArgumentOutOfRangeException(nameof(frameRate), "Frame rate must be greater than zero.");

            _startTime = null;
            _minimumFrameInterval = TimeSpan.FromSeconds(1d / frameRate);
            _lastAcceptedFrameTime = null;

            uint width = (uint)item.Size.Width;
            uint height = (uint)item.Size.Height;
            width = (width % 2 == 0) ? width : width + 1;
            height = (height % 2 == 0) ? height : height + 1;

            string videoTempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_video.mp4");
            string audioTempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_audio.wav");
            _videoTempPath = videoTempPath;
            _audioBaseTempPath = audioTempPath;

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

                _session = _framePool.CreateCaptureSession(item);
                _session.IsCursorCaptureEnabled = includeCursor;

                _transcoder = new MediaTranscoder();
                _transcoder.HardwareAccelerationEnabled = true;

                using var stream = await tempVideoFile.OpenAsync(FileAccessMode.ReadWrite);
                _transcodeOp = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(
                    _mediaStreamSource, stream, profile);

                if (!_transcodeOp.CanTranscode)
                    throw new InvalidOperationException("MFT Encoder could not initialize with these configurations.");

                RecordingStarted?.Invoke(this, outputFilePath);

                _frameStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _frameCount = 0;
                _droppedFrames = 0;
                _throttledFrames = 0;
                _micSamplesReceived = 0;
                _micSamplesQueued = 0;
                _micSamplesProcessed = 0;
                _micQueuedBytes = 0;
                _micDroppedBuffers = 0;
                _loopbackSamplesReceived = 0;
                _loopbackSamplesWritten = 0;
                _micEmptyCallbacks = 0;

                // Start loopback + mic audio recording
                StartAudioRecording(audioTempPath, microphoneDeviceId);
                _session.StartCapture();

                await _transcodeOp.TranscodeAsync().AsTask();
            }
            catch (Exception ex)
            {
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
                            RecordingStopped?.Invoke(this, outputFilePath);
                        }
                    });
                }
            }
        }

        public void Stop()
        {
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
            if (_framePool != null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
            }
            _session = null;
            _framePool = null;
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
                return;
            }

            var frameId = Interlocked.Increment(ref _nextFrameId);

            CapturedFrame? previousFrame;
            lock (_lock)
            {
                if (!_isRecording)
                {
                    frame.Dispose();
                    return;
                }

                if (_lastAcceptedFrameTime.HasValue && frame.SystemRelativeTime - _lastAcceptedFrameTime.Value < _minimumFrameInterval)
                {
                    frame.Dispose();
                    _throttledFrames++;
                    return;
                }

                _lastAcceptedFrameTime = frame.SystemRelativeTime;
                previousFrame = Interlocked.Exchange(ref _latestFrame, new CapturedFrame(frameId, frame));
                if (previousFrame is null)
                {
                    _frameAvailable.Release();
                }
            }

            if (previousFrame != null)
            {
                previousFrame.Frame.Dispose();
                _droppedFrames++;
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

                await _frameAvailable.WaitAsync(token);

                var capturedFrame = Interlocked.Exchange(ref _latestFrame, null);
                if (capturedFrame != null)
                {
                    // The sample holds the Direct3D surface until the encoder has consumed it.
                    // _pendingSamples owns the frame until the sample is processed.
                    var frameHandedOff = false;
                    MediaStreamSample? sample = null;
                    try
                    {
                        _startTime ??= capturedFrame.Frame.SystemRelativeTime;
                        var timeStamp = capturedFrame.Frame.SystemRelativeTime - _startTime.Value;
                        sample = MediaStreamSample.CreateFromDirect3D11Surface(
                            capturedFrame.Frame.Surface, timeStamp);
                        sample.Processed += _sampleProcessedHandler;
                        if (!_pendingSamples.TryAdd(sample, capturedFrame))
                            throw new InvalidOperationException("Unable to track the video sample.");

                        args.Request.Sample = sample;
                        frameHandedOff = true;

                        _frameCount++;
                    }
                    finally
                    {
                        if (!frameHandedOff)
                        {
                            if (sample != null)
                            {
                                sample.Processed -= _sampleProcessedHandler;
                                _pendingSamples.TryRemove(sample, out _);
                            }

                            capturedFrame.Frame.Dispose();
                        }
                    }
                }
                else
                {
                    args.Request.Sample = null;
                }
            }
            catch (OperationCanceledException)
            {
                args.Request.Sample = null;
            }
            catch
        {
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
                capturedFrame.Frame.Dispose();
            }

            sender.Processed -= _sampleProcessedHandler;
        }

        private void Cleanup()
        {
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
            if (_framePool != null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
            }

            var queuedFrame = Interlocked.Exchange(ref _latestFrame, null);
            if (queuedFrame != null)
            {
                queuedFrame.Frame.Dispose();
            }

            foreach (var entry in _pendingSamples)
            {
                if (_pendingSamples.TryRemove(entry.Key, out var capturedFrame))
                {
                    entry.Key.Processed -= _sampleProcessedHandler;
                    capturedFrame.Frame.Dispose();
                }
            }

            // Reset the one-frame slot signal.
            while (_frameAvailable.CurrentCount > 0)
            {
                _frameAvailable.Wait(0);
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
        }

        // ==========================================
        // Audio Capture & Mixing Implementation (NAudio)
        // ==========================================

        private void StartAudioRecording(string audioBasePath, string? microphoneDeviceId)
        {
            _isAudioRecording = true;
            string loopbackPath = audioBasePath.Replace(".wav", "_loopback.wav");
            string micPath = audioBasePath.Replace(".wav", "_mic.wav");
            _micAudioQueue = new ConcurrentQueue<byte[]>();
            _micQueuedBytes = 0;
            _micDroppedBuffers = 0;

            // 1. Device Sound Loopback - Independent writer
            try
            {
                _loopbackCapture = new WasapiLoopbackCapture();
                var waveFormat = _loopbackCapture.WaveFormat;
                _loopbackWriter = new WaveFileWriter(loopbackPath, waveFormat);

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
                            }
                        };

                        _loopbackCapture.StartRecording();
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
                _loopbackCapture?.Dispose();
                _loopbackCapture = null;
            }

            // 2. Microphone Capture - Independent writer. An empty id explicitly disables mic input.
            if (string.IsNullOrWhiteSpace(microphoneDeviceId)) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var microphone = ResolveMicrophoneDevice(enumerator, microphoneDeviceId);
                _micCapture = new WasapiCapture(microphone);
                _micWaveFormat = _micCapture.WaveFormat;
                _micWriter = new WaveFileWriter(micPath, _micWaveFormat);

                // Start background processing task for mic audio
                _micProcessingTask = Task.Run(async () =>
                {
                    try
                    {
                        while (_isAudioRecording)
                        {
                            if (_micAudioQueue != null && _micAudioQueue.TryDequeue(out var buffer))
                            {
                                Interlocked.Add(ref _micQueuedBytes, -buffer.Length);
                                lock (_lock)
                                {
                                    _micWriter?.Write(buffer, 0, buffer.Length);
                                    _micSamplesProcessed++;
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

                _micCapture.DataAvailable += (s, e) =>
                {
                    if (_isAudioRecording)
                    {
                        if (e.BytesRecorded > 0)
                        {
                            _micSamplesReceived++;
                            const float volumeBoost = 2.0f;
                            var boostedBuffer = new byte[e.BytesRecorded];
                            System.Buffer.BlockCopy(e.Buffer, 0, boostedBuffer, 0, e.BytesRecorded);

                            // WasapiCapture can expose either 32-bit IEEE float or 32-bit PCM.
                            // Only reinterpret IEEE float samples as floats; treating PCM bytes
                            // as floats produces invalid audio and can result in silence.
                            if (_micWaveFormat?.Encoding == WaveFormatEncoding.IeeeFloat && _micWaveFormat.BitsPerSample == 32)
                            {
                                for (int i = 0; i < e.BytesRecorded; i += 4)
                                {
                                    float sample = BitConverter.ToSingle(boostedBuffer, i);
                                    sample *= volumeBoost;
                                    sample = Math.Max(-1.0f, Math.Min(1.0f, sample));
                                    BitConverter.TryWriteBytes(boostedBuffer.AsSpan(i, sizeof(float)), sample);
                                }
                            }

                            var queue = _micAudioQueue;
                            if (queue != null)
                            {
                                while (queue.Count >= MaxMicQueueDepth && queue.TryDequeue(out var droppedBuffer))
                                {
                                    Interlocked.Add(ref _micQueuedBytes, -droppedBuffer.Length);
                                    Interlocked.Increment(ref _micDroppedBuffers);
                                }

                                Interlocked.Add(ref _micQueuedBytes, boostedBuffer.Length);
                                queue.Enqueue(boostedBuffer);
                            }
                            _micSamplesQueued++;
                        }
                        else
                        {
                            _micEmptyCallbacks++;
                        }
                    }
                };

                _micCapture.StartRecording();
            }
            catch (Exception exception)
            {
                _micCapture?.Dispose();
                _micCapture = null;
                throw new InvalidOperationException("The configured microphone could not be started.", exception);
            }
        }

        private static MMDevice ResolveMicrophoneDevice(MMDeviceEnumerator enumerator, string microphoneDeviceId)
        {
            if (string.Equals(microphoneDeviceId, DefaultMicrophoneDeviceId, StringComparison.Ordinal))
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }

            try
            {
                return enumerator.GetDevice(microphoneDeviceId);
            }
            catch (Exception lookupException)
            {
                // Older builds stored Windows.Devices.Enumeration IDs, which wrap the
                // Core Audio endpoint ID. Match that legacy form before giving up.
                string? matchingId = null;
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    using (device)
                    {
                        if (EndpointIdsMatch(microphoneDeviceId, device.ID))
                        {
                            matchingId = device.ID;
                            break;
                        }
                    }
                }

                if (matchingId != null)
                {
                    return enumerator.GetDevice(matchingId);
                }

                throw new InvalidOperationException("The selected microphone is no longer available.", lookupException);
            }
        }

        private static bool EndpointIdsMatch(string requestedId, string endpointId) =>
            string.Equals(requestedId, endpointId, StringComparison.OrdinalIgnoreCase) ||
            requestedId.Contains(endpointId, StringComparison.OrdinalIgnoreCase) ||
            endpointId.Contains(requestedId, StringComparison.OrdinalIgnoreCase);

        private async Task StopAudioRecordingAsync()
        {
            _isAudioRecording = false;

            if (_loopbackRecordTask != null)
            {
                try { await _loopbackRecordTask; }
                catch { }
                _loopbackRecordTask = null;
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
                    Interlocked.Add(ref _micQueuedBytes, -buffer.Length);
                    Array.Clear(buffer, 0, buffer.Length);
                }
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
                string loopbackPath = audioPath.Replace(".wav", "_loopback.wav");
                string micPath = audioPath.Replace(".wav", "_mic.wav");
                string mixedAudioPath = audioPath.Replace(".wav", "_mixed.wav");

                bool hasVideo = File.Exists(videoPath) && new FileInfo(videoPath).Length > 1000;
                bool hasLoopback = File.Exists(loopbackPath) && new FileInfo(loopbackPath).Length > 1000;
                bool hasMic = File.Exists(micPath) && new FileInfo(micPath).Length > 1000;

                if (!hasVideo)
                {
                    throw new FileNotFoundException("Video file was not recorded successfully.");
                }

                // Fast path: no audio, just copy video
                if (!hasLoopback && !hasMic)
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Copy(videoPath, outputPath);
                    return;
                }

                global::Windows.Media.Editing.MediaComposition? composition = new();

                var videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
                global::Windows.Media.Editing.MediaClip? videoClip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(videoFile);
                composition.Clips.Add(videoClip);

                string? audioTrackPath = null;
                if (hasLoopback && hasMic)
                {
                    await MixWavFilesAsync(loopbackPath, micPath, mixedAudioPath);
                    audioTrackPath = mixedAudioPath;
                }
                else if (hasLoopback)
                {
                    audioTrackPath = loopbackPath;
                }
                else if (hasMic)
                {
                    audioTrackPath = micPath;
                }

                global::Windows.Media.Editing.BackgroundAudioTrack? audioTrack = null;
                try
                {
                    if (audioTrackPath != null)
                    {
                        var audioFile = await StorageFile.GetFileFromPathAsync(audioTrackPath);
                        audioTrack = await global::Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(audioFile);
                        composition.BackgroundAudioTracks.Add(audioTrack);
                    }

                    await RenderCompositionAsync(composition, outputPath, width, height, bitrateBps, frameRate);
                }
                finally
                {
                    // These WinRT media-editing objects have no IDisposable implementation. Remove
                    // their references promptly, then collect only at the post-recording mux boundary.
                    composition.BackgroundAudioTracks.Clear();
                    composition.Clips.Clear();
                    audioTrack = null;
                    videoClip = null;
                    composition = null;

                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                }
            }
            catch (OperationCanceledException)
            {
                if (File.Exists(videoPath) && !File.Exists(outputPath))
                {
                    File.Copy(videoPath, outputPath, true);
                }
            }
            catch
            {
                if (File.Exists(videoPath) && !File.Exists(outputPath))
                {
                    File.Copy(videoPath, outputPath, true);
                }
            }
            finally
            {
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
            }
        }

        private async Task RenderCompositionAsync(
            global::Windows.Media.Editing.MediaComposition composition,
            string outputPath,
            uint width,
            uint height,
            uint bitrateBps,
            uint frameRate)
        {
            var destFile = await StorageFile.GetFileFromPathAsync(outputPath);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = width;
            profile.Video.Height = height;
            profile.Video.Bitrate = bitrateBps;
            profile.Video.FrameRate.Numerator = frameRate;
            profile.Video.FrameRate.Denominator = 1;
            profile.Audio = AudioEncodingProperties.CreateAac(44100, 2, 192000);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            var saveOp = composition.RenderToFileAsync(destFile, global::Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
            await saveOp.AsTask(cts.Token);
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



