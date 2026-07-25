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
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class NotebookPage : Page
    {
        private readonly NotebookDatabaseService _database = new();
        private readonly ObservableCollection<NotebookEntry> _entries = [];
        private NotebookAttachmentStorageService? _attachmentStorage;
        private NotebookEntry? _entryToEdit;
        private Noteblock? _hoveredBlock;
        private Noteblock? _focusedBlock;
        private bool _isLoading;
        private bool _isSaving;
        private bool _saveQueued;
        private string? _searchResultKey;

        public NotebookPage()
        {
            InitializeComponent();
            Loaded += NotebookPage_Loaded;
        }

        private async void NotebookPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoading = true;
            _attachmentStorage = new NotebookAttachmentStorageService(_database.RootPath);
            foreach (var entry in (await _database.LoadAsync()).OrderByDescending(entry => entry.Timestamp)) _entries.Add(entry);
            _isLoading = false;
            BlocksHost.ItemsSource = _entries;
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
            if (type is "image" or "file") { await AddAttachmentAsync(type); return; }
            if (type is "date" or "generated_password" or "random")
            {
                EnsureEditableBlock()?.InsertValue(type switch
                {
                    "date" => DateTime.Now.ToString("g"),
                    "generated_password" => $"@password{{{NotebookShortcutService.CreateReadablePassword()}}}",
                    _ => NotebookShortcutService.CreateSecretKey()
                });
                return;
            }

            EnsureEditableBlock()?.InsertSyntax(type switch
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
            var block = _focusedBlock ?? _hoveredBlock;
            if (block is null || !block.TryCaptureSelection(out var selectionStart, out var selectedText))
            {
                SaveStatusText.Text = "Select text in an editable note to improve it.";
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
                using var client = new GeminiClient(App.Settings.Current.GeminiApiKey);
                var improved = await client.ImproveWritingAsync(selectedText, CancellationToken.None);
                SaveStatusText.Text = block.TryReplaceSelection(selectionStart, selectedText, improved)
                    ? string.Empty
                    : "The selection changed before the improvement completed; nothing was replaced.";
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
            EnsureEditableBlock()?.InsertText(type == "image"
                ? $"![{System.IO.Path.GetFileNameWithoutExtension(file.Name)}]({target})"
                : $"[{file.Name}]({target})");
        }

        private NotebookEntry AddEntry(string content, bool startEditing)
        {
            var entry = new NotebookEntry { Type = "note", Content = content, Timestamp = DateTime.UtcNow };
            _entryToEdit = startEditing ? entry : null;
            _entries.Insert(0, entry);
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
            block.RemoveRequested -= Block_RemoveRequested;
            block.InteractionStateChanged -= Block_InteractionStateChanged;
            block.CommitRequested -= Block_CommitRequested;
            block.RemoveRequested += Block_RemoveRequested;
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
        }

        private Task<bool> Block_CommitRequested(Noteblock block) => SaveNotebookAsync();

        private void Block_RemoveRequested(object? sender, EventArgs e)
        {
            if (sender is not Noteblock block) return;
            if (ReferenceEquals(block, _hoveredBlock)) _hoveredBlock = null;
            if (ReferenceEquals(block, _focusedBlock)) _focusedBlock = null;
            _entries.Remove(block.Entry);
            _ = SaveNotebookAsync();
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
