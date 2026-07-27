using System;
using App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Pages
{
    public sealed partial class ToolsPage : Page
    {
        private static ToolsPage? Current;
        private const int StopDistancePixels = 100;
        private const int BrowserTabCount = 3;
        private const int MaximumVirtualScrollOffset = 3;
        private const double MouseMovementScreenCoverage = 0.70;
        private const int MaximumMouseJumpDistance = 200;
        private const int CenterJumpChance = 8;
        private const int MinimumScrollActionsPerCycle = 1;
        private const int MaximumScrollActionsPerCycle = 3;

        private readonly DispatcherTimer _activityTimer = new();
        private readonly DispatcherTimer _mouseMonitorTimer = new();
        private readonly Random _random = new();
        private readonly int[] _browserTabScrollOffsets = new int[BrowserTabCount];
        private NativeInputService.CursorPosition _lastAutomatedCursorPosition;
        private NativeInputService.ScreenBounds _mouseMovementBounds;
        private int _ideScrollOffset;
        private int _browserTabIndex;
        private int _remainingScrollActions;
        private ActiveExternalApp _activeExternalApp;
        private bool _movePending;
        private bool _isRunning;

        public ToolsPage()
        {
            InitializeComponent();
            Current = this;
            _activityTimer.Tick += ActivityTimer_Tick;
            _mouseMonitorTimer.Interval = TimeSpan.FromMilliseconds(100);
            _mouseMonitorTimer.Tick += MouseMonitorTimer_Tick;
            Unloaded += ToolsPage_Unloaded;
        }

        internal static void ToggleFromShortcut()
        {
            if (Current is null) return;
            if (Current._isRunning) Current.StopCaffeine(); else Current.StartCaffeine();
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
            App.SystemTray?.SetProcessing(true);
            CaffeineButton.Content = "Stop Caffeine";
            CaffeineDescriptionText.Text = "Caffeine will begin in 5 seconds. Move the pointer 100 pixels away from its last automated position to stop.";
            _mouseMonitorTimer.Start();
            FocusNextWindow();
            BeginActivityBlock();
            _activityTimer.Interval = TimeSpan.FromSeconds(5);
            _activityTimer.Start();
        }

        private void StopCaffeine()
        {
            _isRunning = false;
            App.SystemTray?.SetProcessing(false);
            _activityTimer.Stop();
            _mouseMonitorTimer.Stop();
            CaffeineButton.Content = "Start Caffeine";
            CaffeineDescriptionText.Text = "Keeps your computer active with occasional cursor movement, scrolling, or tab switching. Move the pointer 100 pixels away to stop.";
        }

        private void ActivityTimer_Tick(object? sender, object e)
        {
            CaffeineDescriptionText.Text = "Caffeine is active across your IDE and browser. Move the pointer 100 pixels away from its last automated position to stop.";
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
                var moved = _random.Next(CenterJumpChance) == 0
                    ? NativeInputService.MoveCursorNearScreenCenter(_random, _mouseMovementBounds, out var position)
                    : NativeInputService.MoveCursorNearCurrentPosition(
                        _random,
                        _mouseMovementBounds,
                        MaximumMouseJumpDistance,
                        out position);
                if (moved)
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

            SwitchActiveTab();
            SwitchExternalApp();
            BeginActivityBlock();
        }

        private void FocusNextWindow()
        {
            if (App.MainWindow is Windows.MainWindow mainWindow)
            {
                if (!mainWindow.IsHiddenToTray)
                    mainWindow.HideToTray();
            }

            NativeInputService.ClickLeftMouseButton();
            NativeInputService.TryGetCursorPosition(out _lastAutomatedCursorPosition);
            _activeExternalApp = ActiveExternalApp.Ide;
        }

        private void BeginActivityBlock()
        {
            _movePending = true;
            _remainingScrollActions = _random.Next(MinimumScrollActionsPerCycle, MaximumScrollActionsPerCycle + 1);
        }

        private void SwitchExternalApp()
        {
            NativeInputService.SendAltTab();
            _activeExternalApp = _activeExternalApp == ActiveExternalApp.Ide
                ? ActiveExternalApp.Browser
                : ActiveExternalApp.Ide;
        }

        private void SwitchActiveTab()
        {
            NativeInputService.SendCtrlTab();
            if (_activeExternalApp == ActiveExternalApp.Browser)
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

        private void ToolsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(Current, this)) Current = null;
            StopCaffeine();
        }

        private enum ActiveExternalApp
        {
            Ide,
            Browser
        }
    }
}
