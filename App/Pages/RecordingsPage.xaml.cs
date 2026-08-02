using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using App.Services;
using App.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class RecordingsPage : Page
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOP = new(0);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;
        private readonly IDirect3DDevice _device;
        private ScreenRecorderService? _recorder;
        private RecordingSessionController? _recordingSessionController;
        private RecordingToolbarWindow? _recordingToolbarWindow;
        private string? _currentTempOutputPath;
        private string? _pendingFinalPath;
        private string? _completedTempOutputPath;
        private string? _latestSavedRecordingPath;
        private string _latestSavedDurationText = "00:00";
        private string _latestSavedFpsText = "0.0";
        private string _latestSavedFileSizeText = "0 MB";
        private bool _isMainWindowVisible;
        private bool _isRecordingActive;
        private bool _isAwaitingSavePath;
        private bool _isFinalVideoReady;
        private bool _isFinalizingSave;
        private readonly DispatcherTimer _finalizingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private int _estimatedFinalizingSeconds;

        public RecordingsPage()
        {
            this.InitializeComponent();
            _device = Direct3D11Helper.CreateDevice();
            _finalizingTimer.Tick += FinalizingTimer_Tick;
            Unloaded += RecordingsPage_Unloaded;
        }

        // Section: Resource Cleanup
        private void RecordingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Unloaded -= RecordingsPage_Unloaded;
            _finalizingTimer.Stop();
            _finalizingTimer.Tick -= FinalizingTimer_Tick;
            CloseRecordingToolbar();
            if (_isRecordingActive) _recorder?.Stop();
            DetachRecorderEvents();
            _recorder?.Dispose();
            _recorder = null;
            _recordingSessionController = null;
            LatestRecordingImage.Source = null;
            (_device as IDisposable)?.Dispose();
        }

        private void DetachRecorderEvents()
        {
            if (_recorder is null) return;
            _recorder.RecordingStarted -= Recorder_RecordingStarted;
            _recorder.RecordingStopped -= Recorder_RecordingStopped;
            _recorder.RecordingFailed -= Recorder_RecordingFailed;
        }

        private async void RecordingButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingActive)
            {
                await StopRecordingAsync();
                return;
            }

            await StartRecordingAsync();
        }

        private async void NewRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            await StartRecordingAsync();
        }

        private async Task StartRecordingAsync()
        {
            if (App.MainWindow is null)
                throw new InvalidOperationException("MainWindow is not available");

            var microphoneDeviceId = App.Settings.Current.RecordingMicrophoneDeviceId;
            try
            {
                if (!await ScreenRecorderService.EnsureMicrophoneAccessAsync(microphoneDeviceId))
                    return;
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
                return;
            }

            var item = await ScreenCaptureService.GetMainDisplayItemAsync();
            if (item == null)
                return;

            _currentTempOutputPath = Path.Combine(
                Path.GetTempPath(),
                $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4");

            _recorder = new ScreenRecorderService(_device);
            _recordingSessionController = new RecordingSessionController(_recorder, StopRecordingAsync);
            _recorder.RecordingStarted += Recorder_RecordingStarted;
            _recorder.RecordingStopped += Recorder_RecordingStopped;
            _recorder.RecordingFailed += Recorder_RecordingFailed;

            _isRecordingActive = true;
            _isAwaitingSavePath = false;
            _isFinalVideoReady = false;
            _isFinalizingSave = false;
            _pendingFinalPath = null;
            _completedTempOutputPath = null;
            HideMainWindowForRecording();
            OpenRecordingToolbar();
            RecordingButtonLabel.Text = "Stop Recording";
            RecordingButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            LatestRecordingCard.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = Visibility.Collapsed;
            StartRecordingWave();
            RecordingButton.IsEnabled = true;

            try
            {
                await _recorder.RecordAsync(
                    item,
                    _currentTempOutputPath,
                    bitrateBps: 12_000_000,
                    frameRate: 30,
                    includeCursor: true,
                    microphoneDeviceId: microphoneDeviceId);
            }
            catch (Exception ex)
            {
                CloseRecordingToolbar();
                RestoreMainWindowAfterRecording();
                ResetUI();
                ShowError($"Recording failed: {ex.Message}");
            }
            finally
            {
                // RecordingStopped event will fire when mux is done
            }
        }

        private void Recorder_RecordingStarted(object? sender, string path)
        {
        }

        private void Recorder_RecordingStopped(object? sender, string path)
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    _completedTempOutputPath = path;
                    _isFinalVideoReady = true;
                    await TryCompleteRecordingAsync();
                }
                catch (Exception ex)
                {
                    CloseRecordingToolbar();
                    RestoreMainWindowAfterRecording();
                    ShowError($"Recording finished, but saving failed: {ex.Message}");
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    ResetUI();
                }
            });
        }

        private void Recorder_RecordingFailed(object? sender, string message)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                CloseRecordingToolbar();
                RestoreMainWindowAfterRecording();
                ResetUI();
                ShowError(message);
            });
        }

        private void ResetUI(bool showInfoPanel = true)
        {
            _isRecordingActive = false;
            _isAwaitingSavePath = false;
            _isFinalVideoReady = false;
            _isFinalizingSave = false;
            // Keep the initial call to action concise, but make the post-preview
            // action explicit so it is clear that the saved recording is retained.
            RecordingButtonLabel.Text = showInfoPanel
                ? "Start Recording"
                : "Start a new recording";
            RecordingButton.Background = GetAccentButtonBrush();
            RecordingButton.IsEnabled = true;
            InfoPanel.Visibility = showInfoPanel ? Visibility.Visible : Visibility.Collapsed;
            RecordingActionPanel.Visibility = showInfoPanel ? Visibility.Visible : Visibility.Collapsed;
            FinalizingPanel.Visibility = Visibility.Collapsed;
            _finalizingTimer.Stop();
            StopRecordingWave();
            DetachRecorderEvents();
            _recorder?.Dispose();
            _recorder = null;
            _recordingSessionController = null;
            _recordingToolbarWindow = null;
            _currentTempOutputPath = null;
            _pendingFinalPath = null;
            _completedTempOutputPath = null;
            _isMainWindowVisible = false;
        }

        private async void ShowError(string message)
        {
            if (XamlRoot is null)
                return;

            var dialog = new ContentDialog
            {
                Title = "Recording error",
                Content = message,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };

            await dialog.ShowAsync();
        }

        private async Task<string?> PromptForFinalPathAsync()
        {
            if (App.MainWindow is null)
                throw new InvalidOperationException("MainWindow is not available");

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SuggestedFileName = $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".mp4"
            };
            savePicker.FileTypeChoices.Add("MP4 video", new[] { ".mp4" });
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var outputFile = await savePicker.PickSaveFileAsync();
            return outputFile?.Path;
        }

        private async Task LoadThumbnailAsync(Image imageControl, string videoPath)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(videoPath);
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 320);
                if (thumb is null)
                {
                    imageControl.Source = null;
                    return;
                }

                var image = new BitmapImage();
                await image.SetSourceAsync(thumb);
                imageControl.Source = image;
            }
            catch
            {
                imageControl.Source = null;
            }
        }

        private void StartRecordingWave()
        {
            if (Resources["RecordingWaveStoryboard"] is Microsoft.UI.Xaml.Media.Animation.Storyboard storyboard)
            {
                storyboard.Begin();
            }
        }

        private void StopRecordingWave()
        {
            if (Resources["RecordingWaveStoryboard"] is Microsoft.UI.Xaml.Media.Animation.Storyboard storyboard)
            {
                storyboard.Stop();
            }

            RecordingWave.Opacity = 0;
            RecordingWaveTransform.X = -150;
        }

        private Microsoft.UI.Xaml.Media.Brush GetAccentButtonBrush()
        {
            if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accentBrush) &&
                accentBrush is Microsoft.UI.Xaml.Media.Brush brush)
            {
                return brush;
            }

            return new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 0, 120, 212));
        }

        private static string FormatBytes(long bytes)
        {
            const double kilobyte = 1024d;
            const double megabyte = kilobyte * 1024d;
            const double gigabyte = megabyte * 1024d;

            if (bytes >= gigabyte)
                return $"{bytes / gigabyte:0.00} GB";

            if (bytes >= megabyte)
                return $"{bytes / megabyte:0.0} MB";

            if (bytes >= kilobyte)
                return $"{bytes / kilobyte:0.0} KB";

            return $"{bytes} B";
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            return elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        }

        private async Task TryCompleteRecordingAsync()
        {
            if (_isFinalizingSave || !_isFinalVideoReady || _isAwaitingSavePath)
                return;

            if (string.IsNullOrWhiteSpace(_completedTempOutputPath))
                return;

            if (string.IsNullOrWhiteSpace(_pendingFinalPath))
            {
                try
                {
                    if (File.Exists(_completedTempOutputPath))
                    {
                        File.Delete(_completedTempOutputPath);
                    }
                }
                catch
                {
                }

                ResetUI();
                return;
            }

            _isFinalizingSave = true;

            try
            {
                if (File.Exists(_pendingFinalPath))
                {
                    File.Delete(_pendingFinalPath);
                }

                _recordingSessionController?.RefreshStats();
                _latestSavedDurationText = _recordingSessionController?.DurationText ?? "00:00";
                _latestSavedFpsText = _recordingSessionController?.FpsText ?? "0.0";
                await Task.Run(() => File.Move(_completedTempOutputPath, _pendingFinalPath));
                _latestSavedRecordingPath = _pendingFinalPath;
                _latestSavedFileSizeText = FormatBytes(new FileInfo(_pendingFinalPath).Length);
                await LoadThumbnailAsync(LatestRecordingImage, _pendingFinalPath);
                LatestRecordingDurationText.Text = _latestSavedDurationText;
                LatestRecordingFpsText.Text = _latestSavedFpsText;
                LatestRecordingFileSizeText.Text = _latestSavedFileSizeText;
                LatestRecordingCard.Visibility = LatestRecordingImage.Source is null
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                CloseRecordingToolbar();
                ResetUI(showInfoPanel: LatestRecordingCard.Visibility != Visibility.Visible);
            }
            catch (Exception ex)
            {
                _isFinalizingSave = false;
                ShowError($"Saving failed: {ex.Message}");
            }
        }

        private async void LatestRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_latestSavedRecordingPath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_latestSavedRecordingPath);
                await Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                ShowError($"Unable to open recording: {ex.Message}");
            }
        }

        private async Task StopRecordingAsync()
        {
            if (!_isRecordingActive)
                return;

            _isRecordingActive = false;
            _isAwaitingSavePath = true;
            RecordingButton.IsEnabled = false;
            RecordingButtonLabel.Text = "Finishing...";
            StopRecordingWave();
            CloseRecordingToolbar();
            RestoreMainWindowAfterRecording();
            ShowFinalizingState();
            _recorder?.Stop();
            _pendingFinalPath = await PromptForFinalPathAsync();
            _isAwaitingSavePath = false;
            await TryCompleteRecordingAsync();
        }

        private void ShowFinalizingState()
        {
            var recordingDuration = _recorder?.Elapsed ?? TimeSpan.Zero;
            _estimatedFinalizingSeconds = Math.Max(5, (int)Math.Ceiling(recordingDuration.TotalSeconds * 0.15));
            FinalizingPanel.Visibility = Visibility.Visible;
            InfoPanel.Visibility = Visibility.Collapsed;
            RecordingActionPanel.Visibility = Visibility.Collapsed;
            LatestRecordingCard.Visibility = Visibility.Collapsed;
            FinalizingStatusText.Text = "Writing your video and mixing audio…";
            UpdateFinalizingCountdown();
            _finalizingTimer.Start();
        }

        private void FinalizingTimer_Tick(object? sender, object e)
        {
            if (_estimatedFinalizingSeconds > 0)
                _estimatedFinalizingSeconds--;

            UpdateFinalizingCountdown();
        }

        private void UpdateFinalizingCountdown()
        {
            FinalizingCountdownText.Text = _estimatedFinalizingSeconds > 0
                ? $"About {_estimatedFinalizingSeconds} second{(_estimatedFinalizingSeconds == 1 ? string.Empty : "s")} remaining"
                : "Almost done…";
        }

        private void OpenRecordingToolbar()
        {
            if (_recordingSessionController is null)
                return;

            _recordingToolbarWindow = new RecordingToolbarWindow(_recordingSessionController);
            _recordingToolbarWindow.Closed += RecordingToolbarWindow_Closed;
            _recordingToolbarWindow.Activate();
        }

        private void RecordingToolbarWindow_Closed(object sender, WindowEventArgs args)
        {
            if (ReferenceEquals(sender, _recordingToolbarWindow))
            {
                _recordingToolbarWindow = null;
            }
        }

        private void CloseRecordingToolbar()
        {
            if (_recordingToolbarWindow is null)
                return;

            _recordingToolbarWindow.Closed -= RecordingToolbarWindow_Closed;
            _recordingToolbarWindow.CloseProgrammatically();
            _recordingToolbarWindow = null;
        }

        private void HideMainWindowForRecording()
        {
            if (App.MainWindow is null)
                return;

            _isMainWindowVisible = false;
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            if (App.MainWindow.Visible)
            {
                _isMainWindowVisible = true;
                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_HIDEWINDOW);
                Thread.Sleep(100);
            }
        }

        private void RestoreMainWindowAfterRecording()
        {
            if (!_isMainWindowVisible || App.MainWindow is null)
                return;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
            App.MainWindow.Activate();
            _isMainWindowVisible = false;
        }
    }
}
