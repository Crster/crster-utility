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
        private readonly TodoSearchService _todoSearch = new();
        private int _searchVersion;
        private bool _allowClose;
        internal bool IsHiddenToTray { get; private set; }
        private static readonly FeatureSearchResult[] FeatureSearchResults =
        [
            new() { Title = "Caffeine", Details = "Keep your computer active", SearchTerms = "caffeine keep awake active prevent sleep", Destination = "ToolsPage" },
            new() { Title = "Take a screenshot", Details = "Capture and edit your screen", SearchTerms = "screenshot screen capture snapshot take picture", Destination = "SnapshotsPage" },
            new() { Title = "Record screen", Details = "Start a screen recording", SearchTerms = "record screen recording video capture start recording", Destination = "RecordingsPage" },
            new() { Title = "Artist", Details = "Generate and edit images with Gemini", SearchTerms = "artist image generate generator edit nano banana gemini", Destination = "ArtistPage" },
            new() { Title = "Todos", Details = "View and complete your todos", SearchTerms = "todo todos tasks checklist complete done", Destination = "TodoPage" },
            new() { Title = "Start with Windows", Details = "Launch Crster Utility after sign-in", SearchTerms = "settings preferences startup launch boot login tray", Destination = "SettingsPage" },
            new() { Title = "Database folder", Details = "Choose where Crster Utility stores its database", SearchTerms = "settings preferences storage notebook path location browse", Destination = "SettingsPage" },
            new() { Title = "Gemini API key", Details = "Configure the Gemini API key", SearchTerms = "settings preferences gemini api key credential", Destination = "SettingsPage" },
            new() { Title = "Gemini models", Details = "Choose embedding, low-cost, high-cost, and artist models", SearchTerms = "settings preferences gemini model embedding low cost high artist", Destination = "SettingsPage" },
            new() { Title = "Location", Details = "Set the city and country", SearchTerms = "settings preferences city country region", Destination = "SettingsPage" },
            new() { Title = "Snapshot shortcut", Details = "Choose the keyboard shortcut for snapshots", SearchTerms = "settings preferences screenshot capture hotkey keyboard", Destination = "SettingsPage" },
            new() { Title = "Capture mouse cursor", Details = "Include the pointer in snapshots", SearchTerms = "settings preferences screenshot snapshot pointer mouse", Destination = "SettingsPage" },
            new() { Title = "Recordings microphone", Details = "Choose the microphone used for screen recordings", SearchTerms = "settings preferences recording audio input device mic", Destination = "SettingsPage" },
            new() { Title = "Caffeine shortcut", Details = "Choose the keyboard shortcut for Caffeine", SearchTerms = "settings preferences caffeine hotkey keyboard awake", Destination = "SettingsPage" }
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
            ShowAndActivate();
        }

        internal void ShowFromActivation()
        {
            ShowAndActivate();
        }

        private void ShowAndActivate()
        {
            IsHiddenToTray = false;
            AppWindow.Show();
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
                else if (tag == "TodoPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.TodoPage));
                }
                else if (tag == "ArtistPage")
                {
                    NavigationPresenter.Navigate(typeof(Pages.ArtistPage));
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
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                if (NavigationPresenter.CurrentSourcePageType == typeof(Pages.SnapshotsPage) && Pages.SnapshotsPage.Current is not null)
                {
                    await Pages.SnapshotsPage.Current.CaptureFromShortcutAsync();
                    return;
                }

                await Pages.SnapshotsPage.CaptureFromHotkeyAsync();
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
            if (query.Length < 2)
            {
                sender.ItemsSource = null;
                return;
            }

            try
            {
                await Task.Delay(1000);
                if (searchVersion != _searchVersion) return;
                var notebookResults = _notebookDatabase.FuzzySearch(query);
                var todoResults = _todoSearch.FuzzySearch(query);
                var featureResults = FeatureSearchResults
                    .Where(result => MatchesSearch(result, query))
                    .ToList();
                var results = new List<object>(featureResults.Count + notebookResults.Count + todoResults.Count);
                results.AddRange(featureResults);
                results.AddRange(todoResults);
                results.AddRange(notebookResults);
                if (searchVersion == _searchVersion && string.Equals(sender.Text.Trim(), query, StringComparison.Ordinal))
                    sender.ItemsSource = results;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Global search failed: {exception.Message}");
                if (searchVersion == _searchVersion) sender.ItemsSource = null;
            }
        }

        private async void GlobalSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is not null)
            {
                NavigateToSearchResult(args.ChosenSuggestion);
                sender.Text = string.Empty;
                sender.ItemsSource = null;
                return;
            }

            var query = args.QueryText.Trim();
            var searchVersion = ++_searchVersion;
            if (query.Length < 2)
            {
                sender.ItemsSource = null;
                return;
            }

            try
            {
                var notebookSearch = _notebookDatabase.SearchAsync(query);
                var todoSearch = _todoSearch.SearchAsync(query);
                await Task.WhenAll(notebookSearch, todoSearch);
                var notebookResults = await notebookSearch;
                var todoResults = await todoSearch;
                if (searchVersion != _searchVersion) return;
                var featureResults = FeatureSearchResults.Where(result => MatchesSearch(result, query));
                sender.ItemsSource = featureResults.Cast<object>()
                    .Concat(todoResults)
                    .Concat(notebookResults)
                    .ToList();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Semantic search failed: {exception.Message}");
                if (searchVersion == _searchVersion) sender.ItemsSource = null;
            }
        }

        private void NavigateToSearchResult(object result)
        {
            if (result is NotebookSearchResult notebookResult)
            {
                SidebarNavigation.SelectedItem = NotebookNavItem;
                NavigationPresenter.Navigate(typeof(Pages.NotebookPage), notebookResult.EntryKey);
            }
            else if (result is TodoSearchResult todoResult)
            {
                SidebarNavigation.SelectedItem = TodoNavItem;
                NavigationPresenter.Navigate(typeof(Pages.TodoPage), todoResult.TodoId);
            }
            else if (result is FeatureSearchResult featureResult)
            {
                NavigateToFeature(featureResult.Destination);
            }
        }

        private static bool MatchesSearch(FeatureSearchResult result, string query)
        {
            var text = $"{result.Title} {result.Details} {result.SearchTerms}";
            return
            query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

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
                case "ArtistPage":
                    SidebarNavigation.SelectedItem = ArtistNavItem;
                    break;
                case "TodoPage":
                    SidebarNavigation.SelectedItem = TodoNavItem;
                    break;
                case "SettingsPage":
                    SidebarNavigation.SelectedItem = SidebarNavigation.SettingsItem;
                    break;
            }
        }
    }
}
