using System;
using System.IO;
using System.Threading.Tasks;
using App.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class RecordingsPage : Page
    {
        private readonly IDirect3DDevice _device;
        private ScreenRecorderService? _recorder;
        private string? _currentOutputPath;

        public RecordingsPage()
        {
            this.InitializeComponent();
            _device = Direct3D11Helper.CreateDevice();
        }

        private async void StartRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is null)
                throw new InvalidOperationException("MainWindow is not available");

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);

            var picker = new GraphicsCapturePicker();
            InitializeWithWindow.Initialize(picker, hwnd);

            var item = await picker.PickSingleItemAsync();
            if (item == null)
                return;

            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SuggestedFileName = $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".mp4"
            };
            savePicker.FileTypeChoices.Add("MP4 video", new[] { ".mp4" });
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var outputFile = await savePicker.PickSaveFileAsync();
            if (outputFile == null)
                return;

            _currentOutputPath = outputFile.Path;

            _recorder = new ScreenRecorderService(_device);
            _recorder.RecordingStarted += Recorder_RecordingStarted;
            _recorder.RecordingStopped += Recorder_RecordingStopped;
            _recorder.RecordingFailed += Recorder_RecordingFailed;

            StartRecordingButton.IsEnabled = false;
            StopRecordingButton.IsEnabled = true;
            StatusCard.Visibility = Visibility.Visible;
            RecordingProgressRing.IsActive = true;
            RecordingStatusText.Text = "Recording...";
            RecordingPathText.Text = "Starting...";

            try
            {
                await _recorder.RecordAsync(
                    item,
                    _currentOutputPath,
                    bitrateBps: 12_000_000,
                    frameRate: 30,
                    includeCursor: true);
            }
            catch (Exception ex)
            {
                ResetUI();
                ShowError($"Recording failed: {ex.Message}");
            }
        }

        private void StopRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            _recorder?.Stop();
        }

        private void Recorder_RecordingStarted(object? sender, string path)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                RecordingStatusText.Text = "Recording...";
                RecordingPathText.Text = path;
            });
        }

        private void Recorder_RecordingStopped(object? sender, string path)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ResetUI();
                RecordingStatusText.Text = "Finished";
                RecordingPathText.Text = path;
            });
        }

        private void Recorder_RecordingFailed(object? sender, string message)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ResetUI();
                ShowError(message);
            });
        }

        private void ResetUI()
        {
            StartRecordingButton.IsEnabled = true;
            StopRecordingButton.IsEnabled = false;
            RecordingProgressRing.IsActive = false;
            _recorder?.Dispose();
            _recorder = null;
        }

        private void ShowError(string message)
        {
            RecordingStatusText.Text = message;
        }
    }
}
