using System;
using App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Pages
{
    public sealed partial class ToolsPage : Page
    {
        private const int StopDistancePixels = 100;
        private const int BrowserTabCount = 3;
        private const int MaximumVirtualScrollOffset = 3;
        private const double MouseMovementScreenCoverage = 0.70;
        private const int MaximumMouseJumpDistance = 200;
        private const int MinimumScrollActionsPerCycle = 3;
        private const int MaximumScrollActionsPerCycle = 6;

        private readonly DispatcherTimer _activityTimer = new();
        private readonly DispatcherTimer _mouseMonitorTimer = new();
        private readonly Random _random = new();
        private readonly int[] _browserTabScrollOffsets = new int[BrowserTabCount];
        private NativeInputService.CursorPosition _lastAutomatedCursorPosition;
        private NativeInputService.ScreenBounds _mouseMovementBounds;
        private IntPtr _caffeineWindowHandle;
        private nint _caffeineWindowExtendedStyle;
        private int _ideScrollOffset;
        private int _browserTabIndex;
        private int _remainingScrollActions;
        private ActiveExternalApp _activeExternalApp;
        private bool _movePending;
        private bool _switchBrowserTabNext;
        private bool _isRunning;

        public ToolsPage()
        {
            InitializeComponent();
            _activityTimer.Tick += ActivityTimer_Tick;
            _mouseMonitorTimer.Interval = TimeSpan.FromMilliseconds(100);
            _mouseMonitorTimer.Tick += MouseMonitorTimer_Tick;
            Unloaded += ToolsPage_Unloaded;
        }

        private void CaffeineButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
                StopCaffeine();
            else
                StartCaffeine();
        }

        private void StartCaffeine()
        {
            if (!NativeInputService.TryGetCursorPosition(out _lastAutomatedCursorPosition) ||
                !NativeInputService.TryCreateCenteredScreenBounds(MouseMovementScreenCoverage, out _mouseMovementBounds))
                return;

            _isRunning = true;
            _switchBrowserTabNext = false;
            NativeInputService.TryExcludeForegroundWindowFromTaskSwitcher(out _caffeineWindowHandle, out _caffeineWindowExtendedStyle);
            CaffeineButton.Content = "Stop Caffeine";
            CaffeineDescriptionText.Text = "Caffeine is active across your IDE and browser. Move the pointer 100 pixels away from its last automated position to stop.";
            _mouseMonitorTimer.Start();
            SwitchToIde();
            BeginActivityBlock();
            ScheduleNextActivity();
        }

        private void StopCaffeine()
        {
            _isRunning = false;
            _activityTimer.Stop();
            _mouseMonitorTimer.Stop();
            NativeInputService.RestoreTaskSwitcherVisibility(_caffeineWindowHandle, _caffeineWindowExtendedStyle);
            NativeInputService.ActivateWindow(_caffeineWindowHandle);
            _caffeineWindowHandle = IntPtr.Zero;
            _caffeineWindowExtendedStyle = default;
            CaffeineButton.Content = "Start Caffeine";
            CaffeineDescriptionText.Text = "Keeps your computer active with occasional cursor movement, scrolling, or tab switching. Move the pointer 100 pixels away to stop.";
        }

        private void ActivityTimer_Tick(object? sender, object e)
        {
            PerformActivity();
            ScheduleNextActivity();
        }

        private void ScheduleNextActivity()
        {
            _activityTimer.Interval = TimeSpan.FromMilliseconds(_random.Next(100, 1001));
            _activityTimer.Start();
        }

        private void MouseMonitorTimer_Tick(object? sender, object e)
        {
            if (!NativeInputService.TryGetCursorPosition(out var currentPosition))
                return;

            var xDistance = currentPosition.X - _lastAutomatedCursorPosition.X;
            var yDistance = currentPosition.Y - _lastAutomatedCursorPosition.Y;
            if ((xDistance * xDistance) + (yDistance * yDistance) >= StopDistancePixels * StopDistancePixels)
                StopCaffeine();
        }

        private void PerformActivity()
        {
            if (_movePending)
            {
                _movePending = false;
                if (NativeInputService.MoveCursorNearCurrentPosition(
                    _random,
                    _mouseMovementBounds,
                    MaximumMouseJumpDistance,
                    out var position))
                {
                    _lastAutomatedCursorPosition = position;
                }

                return;
            }

            if (_remainingScrollActions > 0)
            {
                ScrollWithinVirtualBounds();
                _remainingScrollActions--;
                return;
            }

            if (_activeExternalApp == ActiveExternalApp.Ide)
            {
                SwitchExternalApp();
                _switchBrowserTabNext = true;
            }
            else if (_switchBrowserTabNext)
            {
                SwitchBrowserTab();
                _switchBrowserTabNext = false;
            }
            else
            {
                SwitchExternalApp();
            }

            BeginActivityBlock();
        }

        private void BeginActivityBlock()
        {
            _movePending = true;
            _remainingScrollActions = _random.Next(MinimumScrollActionsPerCycle, MaximumScrollActionsPerCycle + 1);
        }

        private void SwitchToIde()
        {
            NativeInputService.SendAltTab();
            _activeExternalApp = ActiveExternalApp.Ide;
        }

        private void SwitchExternalApp()
        {
            NativeInputService.SendAltTab();
            _activeExternalApp = _activeExternalApp == ActiveExternalApp.Ide
                ? ActiveExternalApp.Browser
                : ActiveExternalApp.Ide;
        }

        private void SwitchBrowserTab()
        {
            NativeInputService.SendCtrlTab();
            _browserTabIndex = (_browserTabIndex + 1) % BrowserTabCount;
        }

        private void ScrollWithinVirtualBounds()
        {
            ref var scrollOffset = ref GetActiveScrollOffset();
            var scrollUp = scrollOffset >= MaximumVirtualScrollOffset ||
                (scrollOffset > -MaximumVirtualScrollOffset && _random.Next(0, 2) == 0);

            NativeInputService.Scroll(scrollUp ? 120 : -120);
            scrollOffset += scrollUp ? -1 : 1;
        }

        private ref int GetActiveScrollOffset()
        {
            if (_activeExternalApp == ActiveExternalApp.Ide)
                return ref _ideScrollOffset;

            return ref _browserTabScrollOffsets[_browserTabIndex];
        }

        private void ToolsPage_Unloaded(object sender, RoutedEventArgs e) => StopCaffeine();

        private enum ActiveExternalApp
        {
            Ide,
            Browser
        }
    }
}
