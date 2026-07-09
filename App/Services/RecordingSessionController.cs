using System;
using System.Threading.Tasks;

namespace App.Services
{
    public sealed class RecordingSessionController
    {
        private readonly ScreenRecorderService _recorder;
        private readonly Func<Task> _stopRecordingAsync;

        public RecordingSessionController(ScreenRecorderService recorder, Func<Task> stopRecordingAsync)
        {
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _stopRecordingAsync = stopRecordingAsync ?? throw new ArgumentNullException(nameof(stopRecordingAsync));
        }

        public string DurationText { get; private set; } = "00:00";
        public string FpsText { get; private set; } = "0.0";
        public string FileSizeText { get; private set; } = "0 MB";

        public void RefreshStats()
        {
            var elapsed = _recorder.IsRecording ? _recorder.Elapsed : _recorder.FinalElapsed;
            var fps = _recorder.IsRecording ? _recorder.CurrentFps : _recorder.FinalFps;

            DurationText = FormatElapsed(elapsed);
            FpsText = fps.ToString("0.0");
            FileSizeText = FormatBytes(_recorder.CurrentRecordingSizeBytes);
        }

        public Task StopRecordingAsync()
        {
            return _stopRecordingAsync();
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
    }
}
