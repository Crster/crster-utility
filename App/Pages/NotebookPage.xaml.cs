using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App.Controls;
using App.Models;
using App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class NotebookPage : Page
    {
        private readonly NotebookDatabaseService _database = new();
        private readonly ObservableCollection<NotebookEntry> _entries = [];
        private NotebookAttachmentStorageService? _attachmentStorage;
        private NotebookEntry? _editingEntry;
        private bool _isCreatingNote;
        private bool _isLoading;
        private bool _isSaving;
        private bool _saveQueued;
        private string? _searchResultKey;
        private readonly CancellationTokenSource _pageCancellation = new();

        public NotebookPage()
        {
            InitializeComponent();
            SetEditingToolbarEnabled(false);
            Loaded += NotebookPage_Loaded;
        }

        private async void NotebookPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoading = true;
            try
            {
                _attachmentStorage = new NotebookAttachmentStorageService(_database.RootPath);
                var entries = await _database.LoadAsync();
                if (_pageCancellation.IsCancellationRequested) return;
                foreach (var entry in entries.OrderByDescending(entry => entry.Timestamp)) _entries.Add(entry);
                BlocksHost.ItemsSource = _entries;
                UpdateEmptyState();
            }
            finally
            {
                _isLoading = false;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _searchResultKey = e.Parameter as string;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _pageCancellation.Cancel();
            BlocksHost.ItemsSource = null;
            _entries.Clear();
            _editingEntry = null;
            base.OnNavigatedFrom(e);
        }

        private async void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string type }) return;
            if (type == "improve")
            {
                await ImproveSelectionAsync(sender as Button);
                return;
            }
            if (type == "new")
            {
                OpenEditor(new NotebookEntry { Type = "note", Timestamp = DateTime.UtcNow }, true);
                return;
            }
            if (type == "save")
            {
                await SaveEditingAsync();
                return;
            }
            if (type is "image" or "file") { await AddAttachmentAsync(type); return; }
            if (type is "date" or "generated_password" or "random")
            {
                InsertEditorText(type switch
                {
                    "date" => DateTime.Now.ToString("g"),
                    "generated_password" => $"@password{{{NotebookShortcutService.CreateReadablePassword()}}}",
                    _ => NotebookShortcutService.CreateSecretKey()
                });
                return;
            }

            InsertEditorSyntax(type switch
            {
                "heading" => ("# ", string.Empty),
                "bold" => ("**", "**"),
                "italic" => ("*", "*"),
                "code" => ("`", "`"),
                "code_block" => ("```\r\n", "\r\n```"),
                "secret" => ("@password{", "}"),
                "table" => ("@table{\r\n\"Header 1\",\"Header 2\"\r\n\"Value 1\",\"Value 2\"\r\n}", string.Empty),
                _ => (string.Empty, string.Empty)
            });
        }

        private async Task ImproveSelectionAsync(Button? button)
        {
            if (_editingEntry is null || string.IsNullOrWhiteSpace(NoteEditor.SelectedText))
            {
                SaveStatusText.Text = "Select text in the note editor to improve it.";
                return;
            }
            if (string.IsNullOrWhiteSpace(App.Settings.Current.OpenAiCompatibleApiKey))
            {
                SaveStatusText.Text = "An AI provider API key is required to improve text.";
                return;
            }

            if (button is not null) button.IsEnabled = false;
            SaveStatusText.Text = "Improving selection…";
            try
            {
                var selectionStart = NoteEditor.SelectionStart;
                var selectedText = NoteEditor.SelectedText;
                using var client = new OpenAiCompatibleClient(App.Settings.Current.OpenAiCompatibleApiKey);
                var improved = await client.ImproveWritingAsync(selectedText, _pageCancellation.Token);
                _pageCancellation.Token.ThrowIfCancellationRequested();
                if (NoteEditor.SelectionStart != selectionStart || !string.Equals(NoteEditor.SelectedText, selectedText, StringComparison.Ordinal))
                    SaveStatusText.Text = "The selection changed before the improvement completed; nothing was replaced.";
                else
                {
                    NoteEditor.SelectedText = improved;
                    SaveStatusText.Text = string.Empty;
                }
            }
            catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                SaveStatusText.Text = $"Improve failed: {exception.Message}";
            }
            finally
            {
                if (button is not null && !_pageCancellation.IsCancellationRequested) button.IsEnabled = true;
            }
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
            var attachmentId = await _attachmentStorage.CopyFromPathAsync(file.Path);
            var target = $"local://{attachmentId}{file.FileType}";
            InsertEditorText(type == "image"
                ? $"![{System.IO.Path.GetFileNameWithoutExtension(file.Name)}]({target})"
                : $"[{file.Name}]({target})");
        }

        private void BlocksHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindParent<Noteblock>(source) is not null) return;
            e.Handled = true;
            OpenEditor(new NotebookEntry { Type = "note", Timestamp = DateTime.UtcNow }, true);
        }

        private void Block_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigureBlock(sender as Noteblock);
        }

        private void Block_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            ConfigureBlock(sender as Noteblock);
        }

        private void ConfigureBlock(Noteblock? block)
        {
            if (block is not { DataContext: NotebookEntry entry } || _attachmentStorage is null) return;
            block.Configure(entry, _attachmentStorage);
            block.EditRequested -= Block_EditRequested;
            block.EditRequested += Block_EditRequested;

            if (string.Equals(_searchResultKey, entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                _searchResultKey = null;
                _ = DispatcherQueue.TryEnqueue(() => { BlocksHost.ScrollIntoView(entry, ScrollIntoViewAlignment.Leading); block.HighlightSearchResult(); });
            }
        }

        private void SetEditingToolbarEnabled(bool isEnabled)
        {
            foreach (var button in EditingToolbar.Children.OfType<Button>())
                button.IsEnabled = isEnabled;
        }

        private void Block_EditRequested(object? sender, EventArgs e)
        {
            if (sender is Noteblock block) OpenEditor(block.Entry, false);
        }

        private void OpenEditor(NotebookEntry entry, bool isCreatingNote)
        {
            _editingEntry = entry;
            _isCreatingNote = isCreatingNote;
            NoteEditor.Text = string.Empty;
            BlocksHost.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            NoteEditorView.Visibility = Visibility.Visible;
            NewNoteButton.Visibility = Visibility.Collapsed;
            SaveNoteButton.Visibility = Visibility.Visible;
            SetEditingToolbarEnabled(true);
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!ReferenceEquals(entry, _editingEntry)) return;
                NoteEditor.Text = entry.Content;
                NoteEditor.Focus(FocusState.Keyboard);
                NoteEditor.SelectionStart = NoteEditor.Text.Length;
                var scrollViewer = FindDescendant<ScrollViewer>(NoteEditor);
                if (scrollViewer is not null) scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, true);
            });
        }

        private async Task SaveEditingAsync()
        {
            if (_editingEntry is null) return;
            var editingEntry = _editingEntry;
            editingEntry.Content = NoteEditor.Text;
            if (string.IsNullOrWhiteSpace(editingEntry.Content))
            {
                if (_isCreatingNote) { CloseEditor(); return; }
                _entries.Remove(editingEntry);
            }
            else if (_isCreatingNote && !_entries.Contains(editingEntry)) _entries.Insert(0, editingEntry);

            if (await SaveNotebookAsync())
            {
                var entryIndex = _entries.IndexOf(editingEntry);
                CloseEditor();
                if (entryIndex >= 0) _entries[entryIndex] = editingEntry;
            }
        }

        private void CloseEditor()
        {
            _editingEntry = null;
            _isCreatingNote = false;
            NoteEditor.Text = string.Empty;
            NoteEditorView.Visibility = Visibility.Collapsed;
            BlocksHost.Visibility = Visibility.Visible;
            NewNoteButton.Visibility = Visibility.Visible;
            SaveNoteButton.Visibility = Visibility.Collapsed;
            SetEditingToolbarEnabled(false);
            UpdateEmptyState();
        }

        private async void NoteEditor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.F1)
            {
                e.Handled = true;
                InsertEditorValue(await NotebookShortcutService.GetSystemUsageTextAsync());
                return;
            }

            if (e.Key == VirtualKey.F2)
            {
                e.Handled = true;
                await InsertSelectedFilePathAsync();
                return;
            }

            if (e.Key == VirtualKey.F5)
            {
                e.Handled = true;
                InsertEditorValue(DateTime.Now.ToString("g"));
                return;
            }

            if (e.Key == VirtualKey.F6)
            {
                e.Handled = true;
                InsertEditorValue($"@password{{{NotebookShortcutService.CreateReadablePassword()}}}");
                return;
            }

            if (e.Key == VirtualKey.F7)
            {
                e.Handled = true;
                InsertEditorValue(NotebookShortcutService.CreateSecretKey());
                return;
            }

            if (e.Key == VirtualKey.F8)
            {
                e.Handled = true;
                InsertEditorValue(Guid.NewGuid().ToString());
                return;
            }

            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseEditor();
            }
        }

        private async void NoteEditor_Paste(object sender, TextControlPasteEventArgs e)
        {
            if (_editingEntry is null || _attachmentStorage is null) return;
            var editingEntry = _editingEntry;
            try
            {
                var content = Clipboard.GetContent();
                var containsStorageItems = content.Contains(StandardDataFormats.StorageItems);
                var containsBitmap = content.Contains(StandardDataFormats.Bitmap);
                if (!containsStorageItems && !containsBitmap) return;

                e.Handled = true;
                var references = new List<string>();
                if (containsStorageItems)
                {
                    var files = (await content.GetStorageItemsAsync()).OfType<StorageFile>().ToList();
                    foreach (var file in files)
                    {
                        var attachmentId = await _attachmentStorage.CopyFromPathAsync(file.Path);
                        var target = $"local://{attachmentId}{file.FileType}";
                        references.Add(NotebookAttachmentStorageService.IsImagePath(file.Path)
                            ? $"![{System.IO.Path.GetFileNameWithoutExtension(file.Name)}]({target})"
                            : $"[{file.Name}]({target})");
                    }
                }
                else
                {
                    var attachmentId = await _attachmentStorage.CopyBitmapAsync(await content.GetBitmapAsync());
                    references.Add($"![Pasted image](local://{attachmentId}.png)");
                }

                if (!ReferenceEquals(editingEntry, _editingEntry) || references.Count == 0) return;
                InsertEditorText(string.Join(Environment.NewLine, references));
                SaveStatusText.Text = string.Empty;
            }
            catch (Exception exception)
            {
                SaveStatusText.Text = $"Paste failed: {exception.Message}";
            }
        }

        private async Task InsertSelectedFilePathAsync()
        {
            if (App.MainWindow is null || _editingEntry is null) return;
            var editingEntry = _editingEntry;
            var selectionStart = NoteEditor.SelectionStart;
            var selectionLength = NoteEditor.SelectionLength;
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null || !ReferenceEquals(editingEntry, _editingEntry)) return;

            NoteEditor.SelectionStart = Math.Min(selectionStart, NoteEditor.Text.Length);
            NoteEditor.SelectionLength = Math.Min(selectionLength, NoteEditor.Text.Length - NoteEditor.SelectionStart);
            InsertEditorValue(file.Path);
        }

        private void InsertEditorValue(string text)
        {
            if (_editingEntry is null) return;
            NoteEditor.SelectedText = text;
            NoteEditor.SelectionStart += text.Length;
            NoteEditor.SelectionLength = 0;
            NoteEditor.Focus(FocusState.Keyboard);
        }

        private void InsertEditorSyntax((string Prefix, string Suffix) syntax)
        {
            if (_editingEntry is null) return;
            var selected = NoteEditor.SelectedText;
            NoteEditor.SelectedText = syntax.Prefix + selected + syntax.Suffix;
            NoteEditor.SelectionStart -= syntax.Suffix.Length;
            NoteEditor.SelectionLength = selected.Length;
            NoteEditor.Focus(FocusState.Keyboard);
        }

        private void InsertEditorText(string text)
        {
            if (_editingEntry is null) return;
            var separator = NoteEditor.SelectionStart > 0 && NoteEditor.Text[NoteEditor.SelectionStart - 1] is not ('\r' or '\n') ? Environment.NewLine : string.Empty;
            NoteEditor.SelectedText = separator + text;
            NoteEditor.SelectionStart += (separator + text).Length;
            NoteEditor.SelectionLength = 0;
            NoteEditor.Focus(FocusState.Keyboard);
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

        private static T? FindDescendant<T>(DependencyObject source) where T : DependencyObject
        {
            for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(source); index++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(source, index);
                if (child is T match) return match;
                if (FindDescendant<T>(child) is { } descendant) return descendant;
            }
            return null;
        }

        private void UpdateEmptyState() =>
            EmptyState.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private async Task<bool> SaveNotebookAsync()
        {
            if (_isLoading) return true;
            if (_isSaving) { _saveQueued = true; return true; }
            _isSaving = true;
            var saved = true;
            try
            {
                do
                {
                    _saveQueued = false;
                    var snapshot = _entries.Select(entry => new NotebookEntry
                    {
                        Key = entry.Key,
                        Type = "note",
                        Content = entry.Content,
                        Attachments = [.. entry.Attachments],
                        Embedding = entry.Embedding,
                        Timestamp = entry.Timestamp
                    }).ToList();
                    try
                    {
                        await _database.SaveAsync(snapshot);
                        SaveStatusText.Text = string.Empty;
                    }
                    catch (Exception exception)
                    {
                        SaveStatusText.Text = $"Not saved: {exception.Message}";
                        saved = false;
                    }
                } while (_saveQueued);
            }
            finally { _isSaving = false; }
            return saved;
        }

    }
}
