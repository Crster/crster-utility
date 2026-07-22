using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using App.Controls;
using App.Models;
using App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class NotebookPage : Page
    {
        private readonly NotebookDatabaseService _database = new();
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly ObservableCollection<NotebookEntry> _entries = [];
        private NotebookAttachmentStorageService? _attachmentStorage;
        private NotebookEntry? _entryToEdit;
        private Noteblock? _hoveredBlock;
        private Noteblock? _focusedBlock;
        private bool _isLoading;
        private bool _isSaving;
        private bool _saveQueued;
        private int? _searchResultIndex;

        public NotebookPage()
        {
            InitializeComponent();
            Loaded += NotebookPage_Loaded;
            _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveNotebookAsync(); };
        }

        private async void NotebookPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoading = true;
            _attachmentStorage = new NotebookAttachmentStorageService(_database.RootPath);
            foreach (var entry in (await _database.LoadAsync()).OrderByDescending(entry => entry.Index)) _entries.Add(entry);
            _isLoading = false;
            BlocksHost.ItemsSource = _entries;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _searchResultIndex = e.Parameter as int?;
        }

        private async void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string type }) return;
            if (type is "image" or "file") { await AddAttachmentAsync(type); return; }

            EnsureEditableBlock()?.InsertSyntax(type switch
            {
                "title" => ("#", string.Empty),
                "description" => ("##", string.Empty),
                "important" => ("\"", "\""),
                "mono" => ("'", "'"),
                "italic" => ("(", ")"),
                "password" => ("@password: ", string.Empty),
                "table" => ("@table: {\r\n\"Header 1\",\"Header 2\"\r\n\"Value 1\",\"Value 2\"\r\n}", string.Empty),
                "todo" => ("@todo: {\r\n- New task\r\n}", string.Empty),
                _ => (string.Empty, string.Empty)
            });
        }

        private async Task AddAttachmentAsync(string type)
        {
            if (App.MainWindow is null || _attachmentStorage is null) return;
            var picker = new FileOpenPicker();
            if (type == "image")
                foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }) picker.FileTypeFilter.Add(extension);
            else picker.FileTypeFilter.Add("*");

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var attachmentPath = await _attachmentStorage.CopyFromPathAsync(file.Path);
            EnsureEditableBlock()?.InsertText($"@{type}: {attachmentPath}");
        }

        private NotebookEntry AddEntry(string content, bool startEditing)
        {
            var index = _entries.Count == 0 ? 1 : _entries.Max(entry => entry.Index) + 1;
            var entry = new NotebookEntry { Type = "note", Content = content, Index = index };
            _entryToEdit = startEditing ? entry : null;
            _entries.Insert(0, entry);
            ScheduleSave();
            return entry;
        }

        private void BlocksHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindParent<Noteblock>(source) is not null) return;
            e.Handled = true;
            AddEntry(string.Empty, true);
        }

        private void Block_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Noteblock { DataContext: NotebookEntry entry } block || _attachmentStorage is null) return;
            var startInEditMode = ReferenceEquals(entry, _entryToEdit);
            if (startInEditMode) _entryToEdit = null;
            block.Configure(entry, _attachmentStorage, startInEditMode);
            block.ContentChanged -= Block_ContentChanged;
            block.RemoveRequested -= Block_RemoveRequested;
            block.InteractionStateChanged -= Block_InteractionStateChanged;
            block.ContentChanged += Block_ContentChanged;
            block.RemoveRequested += Block_RemoveRequested;
            block.InteractionStateChanged += Block_InteractionStateChanged;

            if (_searchResultIndex == entry.Index)
            {
                _searchResultIndex = null;
                _ = DispatcherQueue.TryEnqueue(() => { BlocksHost.ScrollIntoView(entry, ScrollIntoViewAlignment.Leading); block.HighlightSearchResult(); });
            }
        }

        private void Block_InteractionStateChanged(object? sender, EventArgs e)
        {
            if (sender is not Noteblock block) return;
            if (block.IsBlockPointerOver) _hoveredBlock = block;
            else if (ReferenceEquals(block, _hoveredBlock)) _hoveredBlock = null;
            if (block.IsEditorFocused) _focusedBlock = block;
            else if (ReferenceEquals(block, _focusedBlock)) _focusedBlock = null;
        }

        private void Block_ContentChanged(object? sender, EventArgs e) => ScheduleSave();

        private void Block_RemoveRequested(object? sender, EventArgs e)
        {
            if (sender is not Noteblock block) return;
            if (ReferenceEquals(block, _hoveredBlock)) _hoveredBlock = null;
            if (ReferenceEquals(block, _focusedBlock)) _focusedBlock = null;
            _entries.Remove(block.Entry);
            ScheduleSave();
        }

        private Noteblock? EnsureEditableBlock()
        {
            var block = _focusedBlock ?? _hoveredBlock;
            if (block is not null) { block.ShowEditor(); return block; }
            var entry = AddEntry(string.Empty, true);
            BlocksHost.UpdateLayout();
            return (BlocksHost.ContainerFromItem(entry) as ListViewItem)?.ContentTemplateRoot as Noteblock;
        }

        private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source is not null)
            {
                if (source is T match) return match;
                source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private void ScheduleSave()
        {
            if (_isLoading) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private async Task SaveNotebookAsync()
        {
            if (_isSaving) { _saveQueued = true; return; }
            _isSaving = true;
            try
            {
                do
                {
                    _saveQueued = false;
                    var snapshot = _entries.Select(entry => new NotebookEntry { Type = "note", Content = entry.Content, Index = entry.Index }).ToList();
                    await Task.Run(() => _database.SaveAsync(snapshot));
                } while (_saveQueued);
            }
            finally { _isSaving = false; }
        }
    }
}
