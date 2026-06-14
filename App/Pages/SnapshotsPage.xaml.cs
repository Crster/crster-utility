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

namespace App.Pages
{
    public sealed partial class SnapshotsPage : Page
    {
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
        }

        private async void CaptureScreenButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (App.MainWindow is null) throw new Exception("App.MainWindow is not set");

            IsMainWindowVisible = false;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            if (App.MainWindow.Visible)
            {
                IsMainWindowVisible = true;
                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_HIDEWINDOW);
            }

            Snapshot = await ScreenCaptureService.CaptureAsync();

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
            ScreenshotDisplay.Source = result.Image;
            BuildPalette(result.PaletteColors);
        }

        private void BuildPalette(List<WColor> colors)
        {
            PaletteContainer.Children.Clear();

            foreach (var color in colors)
            {
                var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

                var border = new Microsoft.UI.Xaml.Controls.Border
                {
                    Width = 40,
                    Height = 40,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        new WColor { A = color.A, R = color.R, G = color.G, B = color.B }),
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
                    BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(MColor.LightGray)
                };

                var tooltip = new Microsoft.UI.Xaml.Controls.ToolTip { Content = hex };
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(border, tooltip);

                border.Tapped += (s, e) =>
                {
                    var dataPackage = new DataPackage();
                    dataPackage.SetText(hex);
                    WClipboard.SetContent(dataPackage);
                };

                PaletteContainer.Children.Add(border);
            }
        }
    }
}
