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

        private readonly DispatcherTimer _activityTimer = new();
        private readonly DispatcherTimer _mouseMonitorTimer = new();
        private readonly Random _random = new();
        private NativeInputService.CursorPosition _lastAutomatedCursorPosition;
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
            if (!NativeInputService.TryGetCursorPosition(out _lastAutomatedCursorPosition))
                return;

            _isRunning = true;
            CaffeineButton.Content = "Stop Caffeine";
            CaffeineDescriptionText.Text = "Caffeine is active. Move the pointer 100 pixels away from its last automated position to stop.";
            _mouseMonitorTimer.Start();
            PerformActivity();
            ScheduleNextActivity();
        }

        private void StopCaffeine()
        {
            _isRunning = false;
            _activityTimer.Stop();
            _mouseMonitorTimer.Stop();
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
            switch (_random.Next(4))
            {
                case 0:
                    if (NativeInputService.MoveCursorToRandomPosition(_random, out var position))
                        _lastAutomatedCursorPosition = position;
                    break;
                case 1:
                    NativeInputService.Scroll(_random.Next(0, 2) == 0 ? 120 : -120);
                    break;
                case 2:
                    NativeInputService.SendAltTab();
                    break;
                default:
                    NativeInputService.SendCtrlTab();
                    break;
            }
        }

        private void ToolsPage_Unloaded(object sender, RoutedEventArgs e) => StopCaffeine();
    }
}
