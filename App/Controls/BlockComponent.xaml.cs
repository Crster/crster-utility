using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using App.Models;
using App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace App.Controls
{
    public sealed partial class Noteblock : UserControl
    {
        private NotebookEntry? _entry;
        private NotebookAttachmentStorageService? _attachmentStorage;
        private TextBox? _editor;
        private string? _editorOriginalContent;
        private bool _isPointerOver;
        private bool _isEditorFocused;
        private bool _isInitialEditorFocus;
        private bool _isSelectionOperationActive;
        private bool _isCommitting;
        private bool _isSearchHighlighted;
        private readonly DispatcherTimer _initialFocusTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(75) };

        public event EventHandler? RemoveRequested;
        public event EventHandler? EditRequested;
        public event EventHandler? InteractionStateChanged;
        public event Func<Noteblock, Task<bool>>? CommitRequested;

        internal NotebookEntry Entry => _entry ?? throw new InvalidOperationException("The noteblock has not been configured.");
        internal bool IsBlockPointerOver => _isPointerOver;
        internal bool IsEditorFocused => _isEditorFocused;

        public Noteblock()
        {
            InitializeComponent();
            HorizontalAlignment = HorizontalAlignment.Stretch;
            AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(Noteblock_DoubleTapped), true);
            _initialFocusTimer.Tick += (_, _) => { _initialFocusTimer.Stop(); _isInitialEditorFocus = false; };
            _resizeTimer.Tick += (_, _) =>
            {
                _resizeTimer.Stop();
                if (_editor is not null) ResizeEditor(_editor);
            };
        }

        internal void Configure(NotebookEntry entry, NotebookAttachmentStorageService attachmentStorage, bool startInEditMode = false)
        {
            _entry = entry;
            _attachmentStorage = attachmentStorage;
            if (startInEditMode) ShowEditorContent(); else ShowPreview();
        }

        internal void ShowEditor()
        {
            if (_entry is null || _editor is not null) return;
            ShowEditorContent();
        }

        internal void InsertSyntax((string Prefix, string Suffix) syntax)
        {
            ShowEditor();
            if (_editor is null) return;
            var selected = _editor.SelectedText;
            _editor.SelectedText = syntax.Prefix + selected + syntax.Suffix;
            _editor.SelectionStart -= syntax.Suffix.Length;
            _editor.SelectionLength = selected.Length;
            _editor.Focus(FocusState.Keyboard);
        }

        internal void InsertText(string text)
        {
            ShowEditor();
            if (_editor is null) return;
            var separator = _editor.SelectionStart > 0 && _editor.Text[_editor.SelectionStart - 1] is not ('\r' or '\n') ? Environment.NewLine : string.Empty;
            _editor.SelectedText = separator + text;
            _editor.SelectionStart += (separator + text).Length;
            _editor.SelectionLength = 0;
            _editor.Focus(FocusState.Keyboard);
        }

        internal void InsertValue(string text)
        {
            ShowEditor();
            if (_editor is null) return;
            InsertShortcutText(_editor, text);
        }

        internal bool TryCaptureSelection(out int start, out string selectedText)
        {
            start = 0;
            selectedText = string.Empty;
            if (_editor is null || string.IsNullOrWhiteSpace(_editor.SelectedText)) return false;
            _isSelectionOperationActive = true;
            start = _editor.SelectionStart;
            selectedText = _editor.SelectedText;
            return true;
        }

        internal void EndSelectionOperation()
        {
            _isSelectionOperationActive = false;
        }

        internal bool TryReplaceSelection(int start, string originalText, string replacement)
        {
            if (_editor is null || _editor.SelectionStart != start ||
                !string.Equals(_editor.SelectedText, originalText, StringComparison.Ordinal))
                return false;
            InsertShortcutText(_editor, replacement);
            return true;
        }

        internal void HighlightSearchResult()
        {
            _isSearchHighlighted = true;
            SetHoverBackground();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _isSearchHighlighted = false;
                if (_editor is null) SetHoverBackground();
            };
            timer.Start();
        }

        private void Noteblock_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            EditRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            if (_editor is null) SetHoverBackground();
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            if (_editor is null) SetHoverBackground();
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Root_GotFocus(object sender, RoutedEventArgs e) =>
            BlockScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        private void Root_LostFocus(object sender, RoutedEventArgs e)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (XamlRoot is null || IsDescendantOf(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, Root)) return;
                BlockScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            });
        }

        private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
        {
            while (element is not null)
            {
                if (ReferenceEquals(element, ancestor)) return true;
                element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        private void SetHoverBackground()
        {
            if (_isSearchHighlighted)
            {
                Root.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 0, 120, 212));
                return;
            }

            Root.Background = _isPointerOver
                ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private void ShowPreview()
        {
            _initialFocusTimer.Stop();
            _resizeTimer.Stop();
            _isInitialEditorFocus = false;
            _editor = null;
            _editorOriginalContent = null;
            Root.Padding = new Thickness(10);
            SetHoverBackground();
            ContentHost.Children.Clear();
            if (_entry is null) return;

            var preview = new MarkdownView { Markdown = _entry.Content };
            preview.ConfigureNotebook(id => _attachmentStorage?.GetFullPath(id));
            ContentHost.Children.Add(preview);
            ScrollToBottom();
        }

        private void ShowEditorContent()
        {
            if (_entry is null) return;
            _editorOriginalContent = _entry.Content;
            _isInitialEditorFocus = true;
            ContentHost.Children.Clear();
            Root.Padding = new Thickness(10);
            Root.Background = (Brush)Application.Current.Resources["LayerOnMicaBaseAltFillColorDefaultBrush"];
            var editor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Text = _entry.Content };
            ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Disabled);
            editor.TextChanged += Editor_TextChanged;
            editor.SizeChanged += Editor_SizeChanged;
            editor.KeyDown += Editor_KeyDown;
            editor.GotFocus += Editor_GotFocus;
            editor.Paste += Editor_Paste;
            ResizeEditor(editor);
            _editor = editor;
            ContentHost.Children.Add(editor);
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { editor.Focus(FocusState.Keyboard); editor.SelectionStart = editor.Text.Length; });
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                BlockScrollViewer.ChangeView(null, BlockScrollViewer.ScrollableHeight, null, true));
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_entry is null || sender is not TextBox editor) return;
            _entry.Content = editor.Text;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private static void Editor_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is TextBox editor && e.NewSize.Width != e.PreviousSize.Width)
                ResizeEditor(editor);
        }

        private async void Editor_Paste(object sender, TextControlPasteEventArgs e)
        {
            if (sender is not TextBox editor || _attachmentStorage is null) return;
            var package = Clipboard.GetContent();
            try
            {
                if (package.Contains(StandardDataFormats.Bitmap))
                {
                    e.Handled = true;
                    var bitmapPath = await _attachmentStorage.CopyBitmapAsync(await package.GetBitmapAsync());
                    InsertText($"![image](local://{bitmapPath}.png)");
                    return;
                }
                if (!package.Contains(StandardDataFormats.Text)) return;
                var text = (await package.GetTextAsync()).Trim();
                if (text.Contains('\r') || text.Contains('\n') || !File.Exists(text)) return;
                e.Handled = true;
                var attachmentPath = await _attachmentStorage.CopyFromPathAsync(text);
                var extension = Path.GetExtension(text);
                var target = $"local://{attachmentPath}{extension}";
                InsertText(NotebookAttachmentStorageService.IsImagePath(text)
                    ? $"![{Path.GetFileNameWithoutExtension(text)}]({target})"
                    : $"[{Path.GetFileName(text)}]({target})");
            }
            catch { }
        }

        private void Editor_GotFocus(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, _editor)) return;
            _isEditorFocused = true;
            if (sender is Control editor) { editor.GotFocus -= Editor_GotFocus; editor.LostFocus += Editor_LostFocus; }
            _initialFocusTimer.Stop(); _initialFocusTimer.Start();
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void Editor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, _editor) || _isCommitting) return;
            _isEditorFocused = false;
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
            await Task.Yield();
            if (_isSelectionOperationActive) return;
            if (_isInitialEditorFocus) { _ = DispatcherQueue.TryEnqueue(() => _editor?.Focus(FocusState.Keyboard)); return; }
            if (RemoveIfEmpty()) return;
            if (string.Equals(_entry?.Content, _editorOriginalContent, StringComparison.Ordinal))
            {
                ShowPreview();
                return;
            }
            _isCommitting = true;
            var committed = CommitRequested is null || await CommitRequested.Invoke(this);
            _isCommitting = false;
            if (!committed)
            {
                _isEditorFocused = true;
                InteractionStateChanged?.Invoke(this, EventArgs.Empty);
                _ = DispatcherQueue.TryEnqueue(() => _editor?.Focus(FocusState.Keyboard));
                return;
            }
            ShowPreview();
        }

        private async void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox editor) return;

            if (e.Key == VirtualKey.F1)
            {
                e.Handled = true;
                var usage = await NotebookShortcutService.GetSystemUsageTextAsync();
                InsertShortcutText(editor, usage);
                return;
            }

            if (e.Key == VirtualKey.F2)
            {
                e.Handled = true;
                await InsertSelectedFilePathAsync(editor);
                return;
            }

            if (e.Key == VirtualKey.F5)
            {
                e.Handled = true;
                InsertShortcutText(editor, DateTime.Now.ToString("g"));
                return;
            }

            if (e.Key == VirtualKey.F6)
            {
                e.Handled = true;
                InsertShortcutText(editor, $"@password{{{NotebookShortcutService.CreateReadablePassword()}}}");
                return;
            }

            if (e.Key == VirtualKey.F7)
            {
                e.Handled = true;
                InsertShortcutText(editor, NotebookShortcutService.CreateSecretKey());
                return;
            }

            if (e.Key == VirtualKey.F8)
            {
                e.Handled = true;
                InsertShortcutText(editor, Guid.NewGuid().ToString());
                return;
            }

            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                if (!RemoveIfEmpty()) ShowPreview();
            }
        }

        private async Task InsertSelectedFilePathAsync(TextBox editor)
        {
            if (App.MainWindow is null) return;
            var selectionStart = editor.SelectionStart;
            var selectionLength = editor.SelectionLength;
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            if (!ReferenceEquals(editor, _editor))
            {
                ShowEditor();
                if (_editor is null) return;
                editor = _editor;
                editor.SelectionStart = Math.Min(selectionStart, editor.Text.Length);
                editor.SelectionLength = Math.Min(selectionLength, editor.Text.Length - editor.SelectionStart);
            }

            InsertShortcutText(editor, file.Path);
        }

        private static void InsertShortcutText(TextBox editor, string text)
        {
            editor.SelectedText = text;
            editor.SelectionStart += text.Length;
            editor.SelectionLength = 0;
            editor.Focus(FocusState.Keyboard);
        }

        private bool RemoveIfEmpty()
        {
            if (_entry is null || !string.IsNullOrWhiteSpace(_entry.Content)) return false;
            RemoveRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private static void ResizeEditor(TextBox editor)
        {
            var contentWidth = editor.ActualWidth - editor.Padding.Left - editor.Padding.Right;
            if (contentWidth <= 0) return;

            var measurement = new TextBlock
            {
                Text = string.IsNullOrEmpty(editor.Text) ? " " : editor.Text,
                FontFamily = editor.FontFamily,
                FontSize = editor.FontSize,
                FontStyle = editor.FontStyle,
                FontWeight = editor.FontWeight,
                TextWrapping = TextWrapping.Wrap
            };
            measurement.Measure(new global::Windows.Foundation.Size(contentWidth, double.PositiveInfinity));
            editor.Height = Math.Max(40, Math.Ceiling(measurement.DesiredSize.Height + editor.Padding.Top + editor.Padding.Bottom));
        }
    }
}
