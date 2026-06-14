using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Graphics;
using WinRT.Interop;

namespace App.Windows
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;

            AppWindow.Resize(new SizeInt32(800, 600));
            AppWindow.SetIcon("Assets/WindowIcon.ico");
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

            SidebarNavigation.SelectionChanged += SidebarNavigation_SelectionChanged;
        }

        private void SidebarNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                string? tag = item.Tag?.ToString();
                if (tag == "SnapshotsPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.SnapshotsPage));
                } else
                {
                    NavigationPresenter.Content = null;
                }
            }
        }
    }
}
