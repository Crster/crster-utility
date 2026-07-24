using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App.Models;
using App.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace App.Windows
{
    public sealed partial class MainWindow : Window
    {
        private readonly NotebookDatabaseService _notebookDatabase = new();
        private int _searchVersion;
        private CancellationTokenSource? _searchCancellation;
        private bool _allowClose;
        internal bool IsHiddenToTray { get; private set; }
        private static readonly FeatureSearchResult[] FeatureSearchResults =
        [
            new() { Title = "Caffeine", Details = "Keep your computer active", SearchTerms = "caffeine keep awake active prevent sleep", Destination = "ToolsPage" },
            new() { Title = "Take a screenshot", Details = "Capture and edit your screen", SearchTerms = "screenshot screen capture snapshot take picture", Destination = "SnapshotsPage" },
            new() { Title = "Record screen", Details = "Start a screen recording", SearchTerms = "record screen recording video capture start recording", Destination = "RecordingsPage" },
            new() { Title = "Start with Windows", Details = "Launch Crster Utility after sign-in", SearchTerms = "start with windows startup launch boot sign in login tray", Destination = "SettingsPage" }
        ];
        public MainWindow()
        {
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;

            AppWindow.Resize(new SizeInt32(800, 600));
            CenterOnCurrentScreen();
            AppWindow.SetIcon("Assets/WindowIcon.ico");
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            AppWindow.Closing += AppWindow_Closing;

            SidebarNavigation.SelectionChanged += SidebarNavigation_SelectionChanged;
            SidebarNavigation.Loaded += (s, e) =>
            {
                SidebarNavigation.SelectedItem = ChatNavItem;
            };
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose || !App.Settings.Current.StartWithWindows) return;

            args.Cancel = true;
            HideToTray();
        }

        internal void HideToTray()
        {
            IsHiddenToTray = true;
            AppWindow.Hide();
        }

        internal void ShowFromTray()
        {
            IsHiddenToTray = false;
            AppWindow.Show();
            Activate();
        }

        internal void ShowChatFromActivation()
        {
            IsHiddenToTray = false;
            AppWindow.Show();
            SidebarNavigation.SelectedItem = ChatNavItem;
            if (NavigationPresenter.CurrentSourcePageType != typeof(Pages.ChatPage))
                NavigationPresenter.Navigate(typeof(Pages.ChatPage));

            NativeInputService.ActivateWindow(WindowNative.GetWindowHandle(this));
        }

        internal void ExitFromTray()
        {
            _allowClose = true;
            Close();
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
            if (args.IsSettingsSelected)
            {
                NavigationPresenter.Navigate(typeof(Pages.SettingsPage));
                return;
            }

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
                else if (tag == "ChatPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.ChatPage));
                }
                else if (tag == "SettingsPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.SettingsPage));
                }
                else
                {
                    NavigationPresenter.Content = null;
                }
            }
        }

        internal void CaptureSnapshotFromHotkey()
        {
            SidebarNavigation.SelectedItem = SnapshotsNavItem;
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                if (Pages.SnapshotsPage.Current is not null) await Pages.SnapshotsPage.Current.CaptureFromShortcutAsync();
            });
        }

        internal void ToggleCaffeineFromHotkey()
        {
            SidebarNavigation.SelectedItem = ToolsNavItem;
            _ = DispatcherQueue.TryEnqueue(() => Pages.ToolsPage.ToggleFromShortcut());
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
                var notebookResults = await _notebookDatabase.SearchAsync(query, cancellationToken: _searchCancellation.Token);
                var featureResults = FeatureSearchResults
                    .Where(result => MatchesSearch(result.SearchTerms, query))
                    .ToList();
                var results = new List<object>(featureResults.Count + notebookResults.Count);
                results.AddRange(featureResults);
                results.AddRange(notebookResults);
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
            if (args.SelectedItem is NotebookSearchResult notebookResult)
            {
                SidebarNavigation.SelectedItem = NotebookNavItem;
                NavigationPresenter.Navigate(typeof(Pages.NotebookPage), notebookResult.EntryIndex);
            }
            else if (args.SelectedItem is FeatureSearchResult featureResult)
            {
                NavigateToFeature(featureResult.Destination);
            }
            sender.Text = string.Empty;
            sender.ItemsSource = null;
        }

        private static bool MatchesSearch(string text, string query) =>
            query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

        private void NavigateToFeature(string destination)
        {
            switch (destination)
            {
                case "SnapshotsPage":
                    SidebarNavigation.SelectedItem = SnapshotsNavItem;
                    break;
                case "RecordingsPage":
                    SidebarNavigation.SelectedItem = RecordingsNavItem;
                    break;
                case "ToolsPage":
                    SidebarNavigation.SelectedItem = ToolsNavItem;
                    break;
                case "SettingsPage":
                    SidebarNavigation.SelectedItem = SidebarNavigation.SettingsItem;
                    break;
            }
        }
    }
}
