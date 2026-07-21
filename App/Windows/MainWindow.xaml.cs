using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using App.Models;
using App.Services;
using Windows.Graphics;

namespace App.Windows
{
    public sealed partial class MainWindow : Window
    {
        private readonly NotebookDatabaseService _notebookDatabase = new();
        private int _searchVersion;
        private CancellationTokenSource? _searchCancellation;
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
                else if (tag == "ToolsPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.ToolsPage));
                }
                else
                {
                    NavigationPresenter.Content = null;
                }
            }
        }

        private async void GlobalSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

            var query = sender.Text.Trim();
            var searchVersion = ++_searchVersion;
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = new CancellationTokenSource();
            if (query.Length < 2)
            {
                sender.ItemsSource = null;
                return;
            }

            try
            {
                await Task.Delay(250, _searchCancellation.Token);
                var results = await _notebookDatabase.SearchAsync(query, cancellationToken: _searchCancellation.Token);
                if (searchVersion == _searchVersion && string.Equals(sender.Text.Trim(), query, StringComparison.Ordinal))
                    sender.ItemsSource = results;
            }
            catch (OperationCanceledException)
            {
                // A newer keystroke has already started a more relevant search.
            }
        }

        private void GlobalSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            SidebarNavigation.SelectedItem = NotebookNavItem;
            if (args.SelectedItem is NotebookSearchResult result)
                NavigationPresenter.Navigate(typeof(Pages.NotebookPage), result.EntryIndex);
            sender.Text = string.Empty;
            sender.ItemsSource = null;
        }
    }
}
