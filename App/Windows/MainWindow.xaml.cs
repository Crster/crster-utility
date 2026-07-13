using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Graphics;

namespace App.Windows
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;

            AppWindow.Resize(new SizeInt32(800, 600));
            CenterOnCurrentScreen();
            AppWindow.SetIcon("Assets/WindowIcon.ico");
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

            SidebarNavigation.SelectionChanged += SidebarNavigation_SelectionChanged;
            SidebarNavigation.Loaded += (s, e) =>
            {
                SidebarNavigation.SelectedItem = SnapshotsNavItem;
            };
        }

        private void CenterOnCurrentScreen()
        {
            DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            RectInt32 workArea = displayArea.WorkArea;
            SizeInt32 size = AppWindow.Size;

            int x = workArea.X + (workArea.Width - size.Width) / 2;
            int y = workArea.Y + (workArea.Height - size.Height) / 2;

            AppWindow.Move(new PointInt32(x, y));
        }

        private void SidebarNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                string? tag = item.Tag?.ToString();
                if (tag == "SnapshotsPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.SnapshotsPage));
                }
                else if (tag == "RecordingsPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.RecordingsPage));
                }
                else if (tag == "NotebookBooksPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.NotebookPage));
                }
                else
                {
                    NavigationPresenter.Content = null;
                }
            }
        }
    }
}
