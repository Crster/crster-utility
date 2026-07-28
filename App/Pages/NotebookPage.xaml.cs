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
        private Noteblock? _hoveredBlock;
        private Noteblock? _focusedBlock;
        private bool _isLoading;
        private bool _isSaving;
        private bool _saveQueued;
        private string? _searchResultKey;

        public NotebookPage()
        {
            InitializeComponent();
            SetEditingToolbarEnabled(false);
            Loaded += NotebookPage_Loaded;
        }

        private async void NotebookPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoading = true;
            _attachmentStorage = new NotebookAttachmentStorageService(_database.RootPath);
            foreach (var entry in (await _database.LoadAsync()).OrderByDescending(entry => entry.Timestamp)) _entries.Add(entry);
            _isLoading = false;
            BlocksHost.ItemsSource = _entries;
            UpdateEmptyState();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _searchResultKey = e.Parameter as string;
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
            if (string.IsNullOrWhiteSpace(App.Settings.Current.GeminiApiKey))
            {
                SaveStatusText.Text = "A Gemini API key is required to improve text.";
                return;
            }

            if (button is not null) button.IsEnabled = false;
            SaveStatusText.Text = "Improving selection…";
            try
            {
                var selectionStart = NoteEditor.SelectionStart;
                var selectedText = NoteEditor.SelectedText;
                using var client = new GeminiClient(App.Settings.Current.GeminiApiKey);
                var improved = await client.ImproveWritingAsync(selectedText, CancellationToken.None);
                if (NoteEditor.SelectionStart != selectionStart || !string.Equals(NoteEditor.SelectedText, selectedText, StringComparison.Ordinal))
                    SaveStatusText.Text = "The selection changed before the improvement completed; nothing was replaced.";
                else
                {
                    NoteEditor.SelectedText = improved;
                    SaveStatusText.Text = string.Empty;
                }
            }
            catch (Exception exception)
            {
                SaveStatusText.Text = $"Improve failed: {exception.Message}";
            }
            finally
            {
                if (button is not null) button.IsEnabled = true;
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
            if (sender is not Noteblock { DataContext: NotebookEntry entry } block || _attachmentStorage is null) return;
            block.Configure(entry, _attachmentStorage);
            block.RemoveRequested -= Block_RemoveRequested;
            block.EditRequested -= Block_EditRequested;
            block.InteractionStateChanged -= Block_InteractionStateChanged;
            block.CommitRequested -= Block_CommitRequested;
            block.RemoveRequested += Block_RemoveRequested;
            block.EditRequested += Block_EditRequested;
            block.InteractionStateChanged += Block_InteractionStateChanged;
            block.CommitRequested += Block_CommitRequested;

            if (string.Equals(_searchResultKey, entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                _searchResultKey = null;
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
            SetEditingToolbarEnabled(_editingEntry is not null || _focusedBlock is not null);
        }

        private void SetEditingToolbarEnabled(bool isEnabled)
        {
            foreach (var button in EditingToolbar.Children.OfType<Button>())
                button.IsEnabled = isEnabled;
        }

        private Task<bool> Block_CommitRequested(Noteblock block) => SaveNotebookAsync();

        private void Block_EditRequested(object? sender, EventArgs e)
        {
            if (sender is Noteblock block) OpenEditor(block.Entry, false);
        }

        private void Block_RemoveRequested(object? sender, EventArgs e)
        {
            if (sender is not Noteblock block) return;
            if (ReferenceEquals(block, _hoveredBlock)) _hoveredBlock = null;
            if (ReferenceEquals(block, _focusedBlock)) _focusedBlock = null;
            _entries.Remove(block.Entry);
            UpdateEmptyState();
            _ = SaveNotebookAsync();
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
            _editingEntry.Content = NoteEditor.Text;
            if (string.IsNullOrWhiteSpace(_editingEntry.Content))
            {
                if (_isCreatingNote) { CloseEditor(); return; }
                _entries.Remove(_editingEntry);
            }
            else if (_isCreatingNote && !_entries.Contains(_editingEntry)) _entries.Insert(0, _editingEntry);

            if (await SaveNotebookAsync()) CloseEditor();
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

        private void NoteEditor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            CloseEditor();
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
