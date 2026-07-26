using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using App.Services;
using App.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using MColor = Microsoft.UI.Colors;
using WColor = Windows.UI.Color;
using WClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using DataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using System.Threading;

namespace App.Pages
{
    public sealed partial class SnapshotsPage : Page
    {
        internal static SnapshotsPage? Current { get; private set; }
        private bool IsMainWindowVisible = false;
        private CanvasBitmap? Snapshot;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOP = new IntPtr(0);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;

        public SnapshotsPage()
        {
            this.InitializeComponent();
            Current = this;
            Unloaded += (_, _) => { if (ReferenceEquals(Current, this)) Current = null; };
        }

        internal Task CaptureFromShortcutAsync() => CaptureScreenAsync();

        internal static async Task CaptureFromHotkeyAsync()
        {
            var snapshot = await ScreenCaptureService.CaptureAsync(App.Settings.Current.SnapshotCaptureMouseCursor);
            if (snapshot is null) throw new Exception("Failed to capture desktop");

            var editSnapshotWindow = new EditSnapshotWindow(snapshot);
            editSnapshotWindow.Closed += (_, _) => snapshot.Dispose();
            editSnapshotWindow.Activate();
            NativeInputService.ActivateWindow(WinRT.Interop.WindowNative.GetWindowHandle(editSnapshotWindow));
        }

        private async void CaptureScreenButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await CaptureScreenAsync();

        private async Task CaptureScreenAsync()
        {
            if (App.MainWindow is null) throw new Exception("App.MainWindow is not set");

            IsMainWindowVisible = false;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            if (App.MainWindow.Visible && App.MainWindow is not MainWindow { IsHiddenToTray: true })
            {
                IsMainWindowVisible = true;
                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_HIDEWINDOW);
                Thread.Sleep(100);
            }

            Snapshot = await ScreenCaptureService.CaptureAsync(App.Settings.Current.SnapshotCaptureMouseCursor);

            if (Snapshot is null) throw new Exception("Failed to capture desktop");

            EditSnapshotWindow editSnapshotWindow = new EditSnapshotWindow(Snapshot);
            editSnapshotWindow.Closed += EditSnapshotWindow_Closed;
            editSnapshotWindow.ImageSaved += EditSnapshotWindow_ImageSaved;
            editSnapshotWindow.Activate();
        }

        private void EditSnapshotWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
        {
            if (App.MainWindow is null) throw new Exception("App.MainWindow is not set");

            if (Snapshot is not null)
            {
                Snapshot.Dispose();
                Snapshot = null;
            }

            if (IsMainWindowVisible)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
                App.MainWindow.Activate();
            }
        }

        private void EditSnapshotWindow_ImageSaved(object? sender, SavedImageResult result)
        {
            EmptyStatePanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            ResultCard.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

            ScreenshotDisplay.Source = result.Image;
            BuildPalette(result.PaletteColors);
        }

        private void BuildPalette(List<ThemeColor> themeColors)
        {
            PaletteLeft.Children.Clear();
            PaletteRight.Children.Clear();

            int count = themeColors.Count;
            int half = Math.Min(5, count);
            int leftCount = Math.Min(half, count);
            int rightCount = Math.Max(0, count - leftCount);

            void AddSwatch(StackPanel parent, ThemeColor tc)
            {
                var border = new Microsoft.UI.Xaml.Controls.Border
                {
                    Width = 48,
                    Height = 48,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        new WColor { A = tc.Color.A, R = tc.Color.R, G = tc.Color.G, B = tc.Color.B }),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6),
                    BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(MColor.LightGray)
                };

                var tooltip = new Microsoft.UI.Xaml.Controls.ToolTip { Content = $"{tc.Name}: {tc.Hex}" };
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(border, tooltip);

                border.Tapped += (s, e) =>
                {
                    var dataPackage = new DataPackage();
                    dataPackage.SetText(tc.Hex);
                    WClipboard.SetContent(dataPackage);
                };

                parent.Children.Add(border);
            }

            for (int i = 0; i < leftCount; i++)
                AddSwatch(PaletteLeft, themeColors[i]);

            for (int i = leftCount; i < leftCount + rightCount; i++)
                AddSwatch(PaletteRight, themeColors[i]);
        }
    }
}
