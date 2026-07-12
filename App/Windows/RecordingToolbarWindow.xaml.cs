using System;
using System.Runtime.InteropServices;
using App.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace App.Windows
{
    public sealed partial class RecordingToolbarWindow : Window
    {
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const long WsCaption = 0x00C00000L;
        private const long WsThickFrame = 0x00040000L;
        private const long WsExLayered = 0x00080000L;
        private const uint LwaColorKey = 0x00000001;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;
        private readonly RecordingSessionController _controller;
        private readonly DispatcherTimer _statsTimer;
        private bool _isProgrammaticClose;
        private bool _isStopping;
        private bool _isDragging;
        private PointInt32 _dragStartCursorPosition;
        private PointInt32 _dragStartWindowPosition;

        public RecordingToolbarWindow(RecordingSessionController controller)
        {
            InitializeComponent();
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            SystemBackdrop = new TransparentBackdrop();
            Activated += RecordingToolbarWindow_Activated;
            AppWindow.SetIcon("Assets/WindowIcon.ico");
            AppWindow.Resize(new SizeInt32(176, 48));
            AppWindow.IsShownInSwitchers = false;
            CenterOnTopOfCurrentScreen();
            ((Microsoft.UI.Xaml.Media.Animation.Storyboard)RootGrid.Resources["RecordingPulseStoryboard"]).Begin();

            _statsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _statsTimer.Tick += StatsTimer_Tick;
            UpdateStats();
            _statsTimer.Start();

            Closed += RecordingToolbarWindow_Closed;
        }

        private void RecordingToolbarWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            var windowHandle = WindowNative.GetWindowHandle(this);

            var style = GetWindowLong(windowHandle, GwlStyle).ToInt64();
            SetWindowLong(windowHandle, GwlStyle, new IntPtr(style & ~(WsCaption | WsThickFrame)));

            var extendedStyle = GetWindowLong(windowHandle, GwlExStyle).ToInt64();
            SetWindowLong(windowHandle, GwlExStyle, new IntPtr(extendedStyle | WsExLayered));
            SetLayeredWindowAttributes(windowHandle, 0, 0, LwaColorKey);
            SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
        }

        private void CenterOnTopOfCurrentScreen()
        {
            var anchorWindow = App.MainWindow as MainWindow;
            var windowId = anchorWindow?.AppWindow.Id ?? AppWindow.Id;
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            var workArea = displayArea.WorkArea;
            var size = AppWindow.Size;

            AppWindow.Move(new PointInt32(
                workArea.X + (workArea.Width - size.Width) / 2,
                workArea.Y + 24));
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

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isStopping)
                return;

            _isStopping = true;
            StopButton.IsEnabled = false;
            await _controller.StopRecordingAsync();
        }

        private void TimerDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(TimerDragArea).Properties.IsLeftButtonPressed)
                return;

            if (!GetCursorPos(out var cursorPosition))
                return;

            _dragStartCursorPosition = new PointInt32(cursorPosition.X, cursorPosition.Y);
            _dragStartWindowPosition = AppWindow.Position;
            _isDragging = TimerDragArea.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void TimerDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging)
                return;

            if (!e.GetCurrentPoint(TimerDragArea).Properties.IsLeftButtonPressed || !GetCursorPos(out var cursorPosition))
            {
                EndDrag(e);
                return;
            }

            AppWindow.Move(new PointInt32(
                _dragStartWindowPosition.X + cursorPosition.X - _dragStartCursorPosition.X,
                _dragStartWindowPosition.Y + cursorPosition.Y - _dragStartCursorPosition.Y));
            e.Handled = true;
        }

        private void TimerDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndDrag(e);
        }

        private void TimerDragArea_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            EndDrag(e);
        }

        private void EndDrag(PointerRoutedEventArgs e)
        {
            if (!_isDragging)
                return;

            TimerDragArea.ReleasePointerCapture(e.Pointer);
            _isDragging = false;
        }

        private async void RecordingToolbarWindow_Closed(object sender, WindowEventArgs args)
        {
            _statsTimer.Stop();

            if (!_isProgrammaticClose)
                await _controller.StopRecordingAsync();
        }

        public void CloseProgrammatically()
        {
            _isProgrammaticClose = true;
            Close();
        }

        private static IntPtr GetWindowLong(IntPtr windowHandle, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr(windowHandle, index)
                : new IntPtr(GetWindowLong32(windowHandle, index));
        }

        private static void SetWindowLong(IntPtr windowHandle, int index, IntPtr value)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr(windowHandle, index, value);
            else
                SetWindowLong32(windowHandle, index, value.ToInt32());
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr windowHandle, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;

            public int Y;
        }
    }
}
