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
        private readonly IDirect3DDevice _device;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Direct3D11CaptureFrame> _frameQueue = new();
        private readonly SemaphoreSlim _frameSemaphore = new(0);
        private CancellationTokenSource? _cts;
        private readonly object _lock = new();

        private MediaStreamSource? _mediaStreamSource;
        private MediaTranscoder? _transcoder;
        private PrepareTranscodeResult? _transcodeOp;

        private bool _isRecording;
        private TimeSpan? _startTime;
        private long _frameCount = 0;
        private long _droppedFrames = 0;
        private System.Diagnostics.Stopwatch? _frameStopwatch;
        private long _micSamplesReceived = 0;
        private long _micSamplesQueued = 0;
        private long _micSamplesProcessed = 0;

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

        public bool IsRecording => _isRecording;

        public event EventHandler<string>? RecordingStarted;
        public event EventHandler<string>? RecordingStopped;
        public event EventHandler<string>? RecordingFailed;

        public ScreenRecorderService(IDirect3DDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public async Task RecordAsync(
            GraphicsCaptureItem item,
            string outputFilePath,
            uint bitrateBps = 12_000_000,
            uint frameRate = 30,
            bool includeCursor = true)
        {
            Debug.WriteLine($"[RecordAsync] Called. outputFilePath={outputFilePath}, size={item.Size.Width}x{item.Size.Height}, fps={frameRate}, bitrate={bitrateBps}, cursor={includeCursor}");

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

                Debug.WriteLine("[RecordAsync] Transcode prepared. Starting capture, audio recording, and transcode.");
                RecordingStarted?.Invoke(this, outputFilePath);

                _frameStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _frameCount = 0;
                _droppedFrames = 0;
                _micSamplesReceived = 0;
                _micSamplesQueued = 0;
                _micSamplesProcessed = 0;

                // Start loopback + mic audio recording
                StartAudioRecording(audioTempPath);

                _session.StartCapture();

                await _transcodeOp.TranscodeAsync().AsTask();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RecordAsync] Exception: {ex}");
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
                    _isRecording = false;
                    Cleanup();
                    await StopAudioRecordingAsync();
                    
                    Debug.WriteLine($"[RecordAsync] Final stats - Frames: {_frameCount}, Dropped: {_droppedFrames}, Mic received: {_micSamplesReceived}, Queued: {_micSamplesQueued}, Processed: {_micSamplesProcessed}");
                    
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
            _framePool?.Dispose();
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
                if (_droppedFrames % 100 == 0)
                    Debug.WriteLine($"[OnFrameArrived] Dropped {_droppedFrames} total frames");
                return;
            }

            lock (_lock)
            {
                if (!_isRecording)
                {
                    frame.Dispose();
                    return;
                }
            }

            // Keep the queue size small (e.g., max 2 frames) to avoid latency/lag and memory growth
            while (_frameQueue.Count >= 2)
            {
                if (_frameQueue.TryDequeue(out var oldFrame))
                {
                    oldFrame.Dispose();
                    _frameSemaphore.Wait(0);
                }
            }

            _frameQueue.Enqueue(frame);
            _frameSemaphore.Release();
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

                if (_frameQueue.TryDequeue(out var frame))
                {
                    _startTime ??= frame.SystemRelativeTime;
                    var timeStamp = frame.SystemRelativeTime - _startTime.Value;
                    var sample = MediaStreamSample.CreateFromDirect3D11Surface(
                        frame.Surface, timeStamp);
                    sample.Processed += (s, e) => frame.Dispose();
                    args.Request.Sample = sample;
                    
                    _frameCount++;
                    if (_frameCount % 300 == 0)
                    {
                        var fps = (double)_frameCount / _frameStopwatch?.Elapsed.TotalSeconds ?? 0;
                        Debug.WriteLine($"[OnSampleRequested] Frame {_frameCount}, FPS: {fps:F1}, Queue: {_frameQueue.Count}, Dropped: {_droppedFrames}");
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnSampleRequested] Exception: {ex}");
                args.Request.Sample = null;
            }
            finally
            {
                deferral.Complete();
            }
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
            _framePool?.Dispose();

            // Drain queue
            while (_frameQueue.TryDequeue(out var frame))
            {
                frame.Dispose();
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

            // 1. Device Sound Loopback - Independent writer
            try
            {
                _loopbackCapture = new WasapiLoopbackCapture();
                var waveFormat = _loopbackCapture.WaveFormat;
                Debug.WriteLine($"[StartAudioRecording] Loopback format: {waveFormat}");

                _loopbackWriter = new WaveFileWriter(loopbackPath, waveFormat);

                _loopbackRecordTask = Task.Run(() =>
                {
                    try
                    {
                        _loopbackCapture.DataAvailable += (s, e) =>
                        {
                            if (_isAudioRecording && e.BytesRecorded > 0)
                            {
                                lock (_lock)
                                {
                                    _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                                }
                            }
                        };

                        _loopbackCapture.StartRecording();
                        Debug.WriteLine("[StartAudioRecording] Loopback capture started.");

                        // Keep task alive while recording
                        while (_isAudioRecording)
                        {
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[LoopbackRecordTask] Exception: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartAudioRecording] Failed to start loopback: {ex.Message}");
                _loopbackCapture?.Dispose();
                _loopbackCapture = null;
            }

            // 2. Microphone Capture - Independent writer
            try
            {
                _micCapture = new WasapiCapture();
                _micWaveFormat = _micCapture.WaveFormat;
                Debug.WriteLine($"[StartAudioRecording] Mic format: {_micWaveFormat}");
                Debug.WriteLine($"[StartAudioRecording] Mic in use: Default capture device");

                _micWriter = new WaveFileWriter(micPath, _micWaveFormat);

                // Start background processing task for mic audio
                _micProcessingTask = Task.Run(async () =>
                {
                    try
                    {
                        while (_isAudioRecording)
                        {
                            if (_micAudioQueue.TryDequeue(out var buffer))
                            {
                                lock (_lock)
                                {
                                    _micWriter?.Write(buffer, 0, buffer.Length);
                                    _micSamplesProcessed++;
                                }
                                
                                if (_micSamplesProcessed % 100 == 0)
                                {
                                    var queueDepth = _micAudioQueue?.Count ?? 0;
                                    Debug.WriteLine($"[MicProcessingTask] Processed {_micSamplesProcessed}, Queue depth: {queueDepth}, Received: {_micSamplesReceived}");
                                }
                            }
                            else
                            {
                                await Task.Delay(1);
                            }
                        }
                        Debug.WriteLine($"[MicProcessingTask] Stopped. Total processed: {_micSamplesProcessed}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MicProcessingTask] Exception: {ex.Message}");
                    }
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
                                    float volumeBoost = 6.0f;
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
                                    
                                    _micAudioQueue?.Enqueue(boostedBuffer);
                                    _micSamplesQueued++;
                                    
                                    if (_micSamplesReceived % 100 == 0)
                                    {
                                        var queueDepth = _micAudioQueue?.Count ?? 0;
                                        Debug.WriteLine($"[MicRecordTask] Received {_micSamplesReceived} callbacks, Queued {_micSamplesQueued}, Queue depth: {queueDepth}\");
                                    }
                                }
                                else
                                {
                                    Debug.WriteLine($"[MicRecordTask] Empty data callback (muted?)\");
                                }
                            }
                        };

                        _micCapture.StartRecording();
                        Debug.WriteLine("[StartAudioRecording] Mic capture started. Volume boost: 6x");

                        // Keep task alive while recording
                        while (_isAudioRecording)
                        {
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MicRecordTask] Exception: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartAudioRecording] Failed to start mic: {ex.Message}");
                _micCapture?.Dispose();
                _micCapture = null;
            }
        }

        private async Task StopAudioRecordingAsync()
        {
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[StopAudioRecording] Loopback stop error: {ex.Message}");
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

            try
            {
                _micCapture?.StopRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StopAudioRecording] Mic stop error: {ex.Message}");
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

                bool hasVideo = File.Exists(videoPath) && new FileInfo(videoPath).Length > 1000;
                bool hasLoopback = File.Exists(loopbackPath) && new FileInfo(loopbackPath).Length > 1000;
                bool hasMic = File.Exists(micPath) && new FileInfo(micPath).Length > 1000;

                if (!hasVideo)
                {
                    throw new FileNotFoundException("Video file was not recorded successfully.");
                }

                Debug.WriteLine($"[MuxAudioVideoAsync] Video: {hasVideo}, Loopback: {hasLoopback}, Mic: {hasMic}");

                // Fast path: no audio, just copy video
                if (!hasLoopback && !hasMic)
                {
                    Debug.WriteLine("[MuxAudioVideoAsync] No audio, copying video directly (fast).");
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Copy(videoPath, outputPath);
                    return;
                }

                var composition = new global::Windows.Media.Editing.MediaComposition();

                var videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
                var videoClip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(videoFile);
                composition.Clips.Add(videoClip);

                // Mix audio tracks
                if (hasLoopback)
                {
                    var loopbackFile = await StorageFile.GetFileFromPathAsync(loopbackPath);
                    var loopbackTrack = await global::Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(loopbackFile);
                    composition.BackgroundAudioTracks.Add(loopbackTrack);
                    Debug.WriteLine("[MuxAudioVideoAsync] Added loopback track.");
                }

                if (hasMic)
                {
                    var micFile = await StorageFile.GetFileFromPathAsync(micPath);
                    var micTrack = await global::Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(micFile);
                    micTrack.Volume = 1.5;
                    composition.BackgroundAudioTracks.Add(micTrack);
                    Debug.WriteLine("[MuxAudioVideoAsync] Added mic track (150% volume).");
                }

                var destFile = await StorageFile.GetFileFromPathAsync(outputPath);

                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
                profile.Video.Width = width;
                profile.Video.Height = height;
                profile.Video.Bitrate = bitrateBps;
                profile.Video.FrameRate.Numerator = frameRate;
                profile.Video.FrameRate.Denominator = 1;
                profile.Audio = AudioEncodingProperties.CreateAac(44100, 2, 192000);

                Debug.WriteLine("[MuxAudioVideoAsync] Starting render (timeout 300s)...");
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
                var saveOp = composition.RenderToFileAsync(destFile, global::Windows.Media.Editing.MediaTrimmingPreference.Fast, profile);
                await saveOp.AsTask(cts.Token);
                Debug.WriteLine("[MuxAudioVideoAsync] Render complete.");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[MuxAudioVideoAsync] Render timeout.");
                if (File.Exists(videoPath) && !File.Exists(outputPath))
                {
                    File.Copy(videoPath, outputPath, true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MuxAudioVideoAsync] Error: {ex.Message}");
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
                    if (File.Exists(loopbackPath)) File.Delete(loopbackPath);
                    if (File.Exists(micPath)) File.Delete(micPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MuxAudioVideoAsync] Temp cleanup error: {ex.Message}");
                }
            }
        }
    }
}



