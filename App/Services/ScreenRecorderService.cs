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

                RecordingStarted?.Invoke(this, outputFilePath);

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

            // 2. Microphone Capture - Independent writer
            try
            {
                _micCapture = new WasapiCapture();
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
                                    
                                    _micAudioQueue?.Enqueue(boostedBuffer);
                                    _micSamplesQueued++;
                                    
                                }
                                else
                                {
                                    _micEmptyCallbacks++;
                                }
                            }
                        };

                        _micCapture.StartRecording();
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

                var composition = new global::Windows.Media.Editing.MediaComposition();

                var videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
                var videoClip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(videoFile);
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

                if (audioTrackPath != null)
                {
                    var audioFile = await StorageFile.GetFileFromPathAsync(audioTrackPath);
                    var audioTrack = await global::Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(audioFile);
                    composition.BackgroundAudioTracks.Add(audioTrack);
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
                var saveOp = composition.RenderToFileAsync(destFile, global::Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
                await saveOp.AsTask(cts.Token);
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

    }
}



