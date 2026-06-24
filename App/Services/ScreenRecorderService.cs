using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

        // Audio Recording Fields
        private WasapiLoopbackCapture? _loopbackCapture;
        private WasapiCapture? _micCapture;
        private bool _isAudioRecording;
        private Task? _audioRecordTask;

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
                    await MuxAudioVideoAsync(videoTempPath, audioTempPath, outputFilePath, width, height, bitrateBps, frameRate);
                    RecordingStopped?.Invoke(this, outputFilePath);
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
            if (frame == null) return;

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

        private void StartAudioRecording(string audioFilePath)
        {
            _isAudioRecording = true;
            var providers = new System.Collections.Generic.List<ISampleProvider>();

            // 1. Device Sound Loopback
            try
            {
                _loopbackCapture = new WasapiLoopbackCapture();
                var loopbackProvider = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                _loopbackCapture.DataAvailable += (s, e) => loopbackProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);

                var loopbackSampleProvider = new WaveToSampleProvider(loopbackProvider);
                var resampledLoopback = new WdlResamplingSampleProvider(loopbackSampleProvider, 44100);
                providers.Add(MakeStereo(resampledLoopback, _loopbackCapture.WaveFormat.Channels));

                _loopbackCapture.StartRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartAudioRecording] Failed to start loopback capture: {ex.Message}");
                _loopbackCapture?.Dispose();
                _loopbackCapture = null;
            }

            // 2. Microphone Capture
            try
            {
                _micCapture = new WasapiCapture();
                var micProvider = new BufferedWaveProvider(_micCapture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                _micCapture.DataAvailable += (s, e) => micProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);

                var micSampleProvider = new WaveToSampleProvider(micProvider);
                var resampledMic = new WdlResamplingSampleProvider(micSampleProvider, 44100);
                providers.Add(MakeStereo(resampledMic, _micCapture.WaveFormat.Channels));

                _micCapture.StartRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartAudioRecording] Failed to start mic capture: {ex.Message}");
                _micCapture?.Dispose();
                _micCapture = null;
            }

            // 3. Spawning the background writer task
            if (providers.Count > 0)
            {
                var mixer = new MixingSampleProvider(providers);
                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

                _audioRecordTask = Task.Run(async () =>
                {
                    try
                    {
                        using var writer = new WaveFileWriter(audioFilePath, waveFormat);
                        var sampleBuffer = new float[4096];

                        while (_isAudioRecording)
                        {
                            int samplesRead = mixer.Read(sampleBuffer, 0, sampleBuffer.Length);
                            if (samplesRead > 0)
                            {
                                writer.WriteSamples(sampleBuffer, 0, samplesRead);
                            }
                            else
                            {
                                await Task.Delay(10);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioRecordTask] Exception: {ex.Message}");
                    }
                });
            }
        }

        private async Task StopAudioRecordingAsync()
        {
            _isAudioRecording = false;

            if (_audioRecordTask != null)
            {
                await _audioRecordTask;
                _audioRecordTask = null;
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
                bool hasAudio = File.Exists(audioPath) && new FileInfo(audioPath).Length > 1000;
                bool hasVideo = File.Exists(videoPath) && new FileInfo(videoPath).Length > 1000;

                if (!hasVideo)
                {
                    throw new FileNotFoundException("Video file was not recorded successfully.");
                }

                if (!hasAudio)
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Copy(videoPath, outputPath);
                    return;
                }

                var composition = new global::Windows.Media.Editing.MediaComposition();

                var videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
                var videoClip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(videoFile);
                composition.Clips.Add(videoClip);

                var audioFile = await StorageFile.GetFileFromPathAsync(audioPath);
                var audioTrack = await global::Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(audioFile);
                composition.BackgroundAudioTracks.Add(audioTrack);

                var destFile = await StorageFile.GetFileFromPathAsync(outputPath);

                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
                profile.Video.Width = width;
                profile.Video.Height = height;
                profile.Video.Bitrate = bitrateBps;
                profile.Video.FrameRate.Numerator = frameRate;
                profile.Video.FrameRate.Denominator = 1;
                profile.Audio = AudioEncodingProperties.CreateAac(44100, 2, 192000);

                var saveOp = composition.RenderToFileAsync(destFile, global::Windows.Media.Editing.MediaTrimmingPreference.Fast, profile);
                await saveOp.AsTask();
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
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MuxAudioVideoAsync] Temp cleanup error: {ex.Message}");
                }
            }
        }
    }
}
