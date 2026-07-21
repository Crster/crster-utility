using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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
        private NotebookAttachmentStorageService? _attachmentStorage;
        private readonly ObservableCollection<NotebookEntry> _entries = [];
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
            _saveTimer.Tick += async (_, _) =>
            {
                _saveTimer.Stop();
                await SaveNotebookAsync();
            };
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
            if (type is "image" or "file")
            {
                await AddAttachmentAsync(type);
                UpdateToolbarHighlight();
                return;
            }

            AddEntry(type, type switch
            {
                "table" => "\"Header 1\",\"Header 2\",\"Header 3\"\n\"Row 1, Cell 1\",\"Row 1, Cell 2\",\"Row 1, Cell 3\"\n\"Row 2, Cell 1\",\"Row 2, Cell 2\",\"Row 2, Cell 3\"",
                "todo" => "- New task",
                _ => string.Empty
            });
            UpdateToolbarHighlight();
        }

        private async Task AddAttachmentAsync(string type)
        {
            if (App.MainWindow is null || _attachmentStorage is null) return;
            var picker = new FileOpenPicker();
            if (type == "image")
            {
                picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".gif"); picker.FileTypeFilter.Add(".webp");
            }
            else picker.FileTypeFilter.Add("*");

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                var attachmentPath = await _attachmentStorage.CopyFromPathAsync(file.Path);
                AddEntry(type, $"{attachmentPath}{Environment.NewLine}{file.Name}{Environment.NewLine}{Environment.NewLine}", startEditing: true);
            }
        }

        private void AddEntry(string type, string content, bool startEditing = false)
        {
            var index = _entries.Count == 0 ? 1 : _entries.Max(entry => entry.Index) + 1;
            var entry = new NotebookEntry { Type = type, Content = content, Index = index };
            _entryToEdit = startEditing ? entry : null;
            _entries.Insert(0, entry);
            ScheduleSave();
        }

        private void BlocksHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            AddEntry("paragraph", string.Empty, startEditing: true);
        }

        private void Block_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Noteblock { DataContext: NotebookEntry entry } block || _attachmentStorage is null) return;
            var startInEditMode = ReferenceEquals(entry, _entryToEdit);
            if (startInEditMode) _entryToEdit = null;
            block.Configure(entry, _attachmentStorage.GetFullPath, startInEditMode);
            block.ContentChanged -= Block_ContentChanged;
            block.RemoveRequested -= Block_RemoveRequested;
            block.InteractionStateChanged -= Block_InteractionStateChanged;
            block.ContentChanged += Block_ContentChanged;
            block.RemoveRequested += Block_RemoveRequested;
            block.InteractionStateChanged += Block_InteractionStateChanged;
            Block_InteractionStateChanged(block, EventArgs.Empty);

            if (_searchResultIndex == entry.Index)
            {
                _searchResultIndex = null;
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    BlocksHost.ScrollIntoView(entry, ScrollIntoViewAlignment.Leading);
                    block.HighlightSearchResult();
                });
            }
        }

        private void Block_InteractionStateChanged(object? sender, EventArgs e)
        {
            if (sender is not Noteblock block) return;
            if (block.IsBlockPointerOver) _hoveredBlock = block;
            else if (ReferenceEquals(block, _hoveredBlock)) _hoveredBlock = null;

            if (block.IsEditorFocused) _focusedBlock = block;
            else if (ReferenceEquals(block, _focusedBlock)) _focusedBlock = null;

            UpdateToolbarHighlight();
        }

        private void UpdateToolbarHighlight()
        {
            var type = (_hoveredBlock ?? _focusedBlock)?.Entry.Type;
            ParagraphTool.IsChecked = type == "paragraph";
            ImageTool.IsChecked = type == "image";
            FileTool.IsChecked = type == "file";
            TableTool.IsChecked = type == "table";
            TodoTool.IsChecked = type == "todo";
        }

        private void Block_ContentChanged(object? sender, EventArgs e) => ScheduleSave();

        private void Block_RemoveRequested(object? sender, EventArgs e)
        {
            if (sender is not Noteblock block) return;
            var entry = _entries.FirstOrDefault(candidate => ReferenceEquals(candidate, block.Entry));
            if (entry is null) return;
            if (ReferenceEquals(block, _hoveredBlock)) _hoveredBlock = null;
            if (ReferenceEquals(block, _focusedBlock)) _focusedBlock = null;
            _entries.Remove(entry);
            UpdateToolbarHighlight();
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            if (_isLoading) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private async Task SaveNotebookAsync()
        {
            if (_isSaving)
            {
                _saveQueued = true;
                return;
            }

            _isSaving = true;
            try
            {
                do
                {
                    _saveQueued = false;
                    var snapshot = _entries.Select(entry => new NotebookEntry
                    {
                        Type = entry.Type,
                        Content = entry.Content,
                        Index = entry.Index
                    }).ToList();

                    await Task.Run(() => _database.SaveAsync(snapshot));
                }
                while (_saveQueued);
            }
            finally
            {
                _isSaving = false;
            }
        }
    }
}
