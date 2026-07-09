using System;
using App.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace App.Windows
{
    public sealed partial class RecordingToolbarWindow : Window
    {
        private readonly RecordingSessionController _controller;
        private readonly DispatcherTimer _statsTimer;
        private bool _isProgrammaticClose;

        public RecordingToolbarWindow(RecordingSessionController controller)
        {
            InitializeComponent();
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TimerDragArea);
            AppWindow.SetIcon("Assets/WindowIcon.ico");
            ConfigureWindowChrome();
            CenterOnTopOfCurrentScreen();

            _statsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _statsTimer.Tick += StatsTimer_Tick;
            UpdateStats();
            _statsTimer.Start();

            Closed += RecordingToolbarWindow_Closed;
        }

        private void ConfigureWindowChrome()
        {
            CompactOverlayPresenter presenter = CompactOverlayPresenter.Create();
            presenter.InitialSize = CompactOverlaySize.Small;
            AppWindow.SetPresenter(presenter);
            AppWindow.Resize(new SizeInt32(96, 32));
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

            AppWindow.IsShownInSwitchers = false;
            ExtendsContentIntoTitleBar = true;
        }

        private void CenterOnTopOfCurrentScreen()
        {
            var anchorWindow = App.MainWindow as MainWindow;
            var windowId = anchorWindow?.AppWindow.Id ?? AppWindow.Id;
            DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            RectInt32 workArea = displayArea.WorkArea;
            SizeInt32 size = AppWindow.Size;

            int x = workArea.X + (workArea.Width - size.Width) / 2;
            int y = workArea.Y + 24;

            AppWindow.Move(new PointInt32(x, y));
        }

        private void StatsTimer_Tick(object? sender, object e)
        {
            UpdateStats();
        }

        private void UpdateStats()
        {
            _controller.RefreshStats();
            TimerText.Text = _controller.DurationText;
        }

        private async void RecordingToolbarWindow_Closed(object sender, WindowEventArgs args)
        {
            _statsTimer.Stop();

            if (_isProgrammaticClose)
                return;

            await _controller.StopRecordingAsync();
        }

        public void CloseProgrammatically()
        {
            _isProgrammaticClose = true;
            Close();
        }
    }
}
