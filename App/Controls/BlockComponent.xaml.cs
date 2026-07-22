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
using Windows.System;

namespace App.Controls
{
    public sealed partial class Noteblock : UserControl
    {
        private NotebookEntry? _entry;
        private NotebookAttachmentStorageService? _attachmentStorage;
        private TextBox? _editor;
        private bool _isPointerOver;
        private bool _isEditorFocused;
        private bool _isInitialEditorFocus;
        private bool _isSearchHighlighted;
        private readonly DispatcherTimer _initialFocusTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

        public event EventHandler? ContentChanged;
        public event EventHandler? RemoveRequested;
        public event EventHandler? InteractionStateChanged;

        internal NotebookEntry Entry => _entry ?? throw new InvalidOperationException("The noteblock has not been configured.");
        internal bool IsBlockPointerOver => _isPointerOver;
        internal bool IsEditorFocused => _isEditorFocused;

        public Noteblock()
        {
            InitializeComponent();
            HorizontalAlignment = HorizontalAlignment.Stretch;
            AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(Noteblock_DoubleTapped), true);
            _initialFocusTimer.Tick += (_, _) => { _initialFocusTimer.Stop(); _isInitialEditorFocus = false; };
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
            ShowEditor();
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
            _isInitialEditorFocus = false;
            _editor = null;
            Root.Padding = new Thickness(10);
            SetHoverBackground();
            ContentHost.Children.Clear();
            if (_entry is null) return;

            var panel = new StackPanel { Spacing = 8 };
            foreach (var section in NotebookFormat.Parse(_entry.Content)) panel.Children.Add(CreateSection(section));
            ContentHost.Children.Add(panel);
        }

        private FrameworkElement CreateSection(NoteSection section) => section.Kind switch
        {
            NoteSectionKind.Title => CreateRichText(section.Content, 28, Microsoft.UI.Text.FontWeights.SemiBold),
            NoteSectionKind.Description => CreateRichText(section.Content, 13, Microsoft.UI.Text.FontWeights.Normal, (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]),
            NoteSectionKind.Password => CreatePassword(section.Content),
            NoteSectionKind.File => CreateAttachment(section.Content, false),
            NoteSectionKind.Image => CreateAttachment(section.Content, true),
            NoteSectionKind.Table => CreateTable(section.Content),
            NoteSectionKind.Todo => CreateTodo(section),
            _ => CreateRichText(section.Content, 14, Microsoft.UI.Text.FontWeights.Normal)
        };

        private static RichTextBlock CreateRichText(string text, double size, global::Windows.UI.Text.FontWeight weight, Brush? foreground = null)
        {
            var block = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
            var paragraph = new Paragraph { FontSize = size, FontWeight = weight };
            if (foreground is not null) paragraph.Foreground = foreground;
            AddCustomInlines(paragraph, text);
            block.Blocks.Add(paragraph);
            return block;
        }

        private static void AddCustomInlines(Paragraph paragraph, string text)
        {
            var position = 0;
            while (position < text.Length)
            {
                var markerIndex = FindNextMarker(text, position, out var marker, out var closing);
                if (markerIndex < 0) { paragraph.Inlines.Add(new Run { Text = text[position..] }); break; }
                if (markerIndex > position) paragraph.Inlines.Add(new Run { Text = text[position..markerIndex] });
                var closeIndex = text.IndexOf(closing, markerIndex + 1);
                if (closeIndex < 0) { paragraph.Inlines.Add(new Run { Text = text[markerIndex..] }); break; }
                var value = text[(markerIndex + 1)..closeIndex];
                if (marker == '"') paragraph.Inlines.Add(new Bold { Inlines = { new Run { Text = value } } });
                else if (marker == '\'') paragraph.Inlines.Add(new Run { Text = value, FontFamily = new FontFamily("Cascadia Mono") });
                else paragraph.Inlines.Add(new Italic { Inlines = { new Run { Text = value } } });
                position = closeIndex + 1;
            }
            if (text.Length == 0) paragraph.Inlines.Add(new Run { Text = string.Empty });
        }

        private static int FindNextMarker(string text, int start, out char marker, out char closing)
        {
            var candidates = new[] { ('"', '"'), ('\'', '\''), ('(', ')') };
            var best = -1;
            marker = closing = '\0';
            foreach (var candidate in candidates)
            {
                var index = text.IndexOf(candidate.Item1, start);
                if (index >= 0 && (best < 0 || index < best)) { best = index; marker = candidate.Item1; closing = candidate.Item2; }
            }
            return best;
        }

        private static FrameworkElement CreatePassword(string value)
        {
            var password = new PasswordBox { Password = value, PasswordRevealMode = PasswordRevealMode.Hidden, MinWidth = 220, IsTabStop = false, IsHitTestVisible = false };
            var toggle = new Button { Content = new SymbolIcon(Symbol.View), Width = 36, Height = 32, Padding = new Thickness(6) };
            toggle.Click += (_, _) => password.PasswordRevealMode = password.PasswordRevealMode == PasswordRevealMode.Hidden ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(password); panel.Children.Add(toggle);
            return panel;
        }

        private FrameworkElement CreateAttachment(string storedPath, bool isImage)
        {
            var fullPath = ResolvePath(storedPath);
            FrameworkElement visual;
            if (isImage)
            {
                var image = new Image { Stretch = Stretch.Uniform, MaxWidth = 220, MaxHeight = 180 };
                if (fullPath is not null && File.Exists(fullPath)) image.Source = new BitmapImage(new Uri(fullPath));
                visual = image;
            }
            else
            {
                var grid = new Grid { Width = 72, Height = 72 };
                grid.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] });
                grid.Children.Add(new SymbolIcon { Symbol = Symbol.Document, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                if (fullPath is not null && File.Exists(fullPath)) _ = LoadFileThumbnailAsync(fullPath, grid);
                visual = grid;
            }

            var layout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            layout.Children.Add(visual);
            layout.Children.Add(new TextBlock { Text = Path.GetFileName(storedPath), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            layout.PointerPressed += async (_, e) =>
            {
                if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || fullPath is null || !File.Exists(fullPath)) return;
                e.Handled = true;
                try { await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(fullPath)); } catch { }
            };
            return layout;
        }

        private string? ResolvePath(string path) => string.IsNullOrWhiteSpace(path) ? null : Path.IsPathFullyQualified(path) ? path : _attachmentStorage?.GetFullPath(path);

        private static async Task LoadFileThumbnailAsync(string path, Grid host)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 200);
                if (thumbnail is null) return;
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                host.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
            }
            catch { }
        }

        private FrameworkElement CreateTodo(NoteSection section)
        {
            var panel = new StackPanel { Spacing = 2 };
            var lines = section.Content.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var trimmed = lines[index].TrimStart();
                if (!trimmed.StartsWith('-') && !trimmed.StartsWith('+'))
                {
                    panel.Children.Add(new TextBlock { Text = lines[index], TextWrapping = TextWrapping.Wrap });
                    continue;
                }
                var lineIndex = index;
                var checkBox = new CheckBox { Content = trimmed[1..].TrimStart(), IsChecked = trimmed[0] == '+', Tag = lineIndex };
                checkBox.Click += (_, _) => UpdateTodo(section, lineIndex, checkBox.IsChecked == true);
                panel.Children.Add(checkBox);
            }
            return panel;
        }

        private void UpdateTodo(NoteSection section, int lineIndex, bool done)
        {
            if (_entry is null) return;
            var content = NotebookFormat.Normalize(_entry.Content);
            var lines = section.Content.Split('\n');
            var relative = 0;
            for (var index = 0; index < lineIndex; index++) relative += lines[index].Length + 1;
            var marker = relative;
            while (marker < relative + lines[lineIndex].Length && char.IsWhiteSpace(lines[lineIndex][marker - relative])) marker++;
            var openingLineEnd = content.IndexOf('\n', section.SourceStart);
            if (openingLineEnd < 0) return;
            var absolute = openingLineEnd + 1 + marker;
            if (absolute >= content.Length) return;
            content = content[..absolute] + (done ? '+' : '-') + content[(absolute + 1)..];
            _entry.Content = content;
            ShowPreview();
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private static FrameworkElement CreateTable(string content)
        {
            var rows = ParseCsvRows(content);
            if (rows.Count == 0) return new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap };
            var grid = new Grid();
            for (var column = 0; column < rows.Max(row => row.Length); column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (var row = 0; row < rows.Count; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var column = 0; column < rows[row].Length; column++)
                {
                    var cell = new Border { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 1, 1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Child = new TextBlock { Text = rows[row][column], FontWeight = row == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal } };
                    Grid.SetRow(cell, row); Grid.SetColumn(cell, column); grid.Children.Add(cell);
                }
            }
            return grid;
        }

        private static List<string[]> ParseCsvRows(string content)
        {
            var rows = new List<string[]>(); var row = new List<string>(); var field = new StringBuilder(); var quoted = false;
            for (var index = 0; index < content.Length; index++)
            {
                var character = content[index];
                if (character == '"')
                {
                    if (quoted && index + 1 < content.Length && content[index + 1] == '"') { field.Append('"'); index++; }
                    else quoted = !quoted;
                }
                else if (character == ',' && !quoted) { row.Add(field.ToString()); field.Clear(); }
                else if (character == '\n' && !quoted) { row.Add(field.ToString()); rows.Add([.. row]); row.Clear(); field.Clear(); }
                else field.Append(character);
            }
            if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add([.. row]); }
            return rows;
        }

        private void ShowEditorContent()
        {
            if (_entry is null) return;
            _isInitialEditorFocus = true;
            ContentHost.Children.Clear();
            Root.Padding = new Thickness(0);
            Root.Background = (Brush)Application.Current.Resources["LayerOnMicaBaseAltFillColorDefaultBrush"];
            var editor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Text = _entry.Content };
            ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Disabled);
            editor.TextChanged += Editor_TextChanged;
            editor.KeyDown += Editor_KeyDown;
            editor.GotFocus += Editor_GotFocus;
            editor.Paste += Editor_Paste;
            ResizeEditor(editor);
            _editor = editor;
            ContentHost.Children.Add(editor);
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { editor.Focus(FocusState.Keyboard); editor.SelectionStart = editor.Text.Length; });
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_entry is null || sender is not TextBox editor) return;
            _entry.Content = editor.Text;
            ResizeEditor(editor);
            ContentChanged?.Invoke(this, EventArgs.Empty);
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
                    InsertText($"@image: {bitmapPath}");
                    return;
                }
                if (!package.Contains(StandardDataFormats.Text)) return;
                var text = (await package.GetTextAsync()).Trim();
                if (text.Contains('\r') || text.Contains('\n') || !File.Exists(text)) return;
                e.Handled = true;
                var attachmentPath = await _attachmentStorage.CopyFromPathAsync(text);
                InsertText($"@{(NotebookAttachmentStorageService.IsImagePath(text) ? "image" : "file")}: {attachmentPath}");
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

        private void Editor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, _editor)) return;
            _isEditorFocused = false;
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
            if (_isInitialEditorFocus) { _ = DispatcherQueue.TryEnqueue(() => _editor?.Focus(FocusState.Keyboard)); return; }
            if (RemoveIfEmpty()) return;
            ShowPreview();
        }

        private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            if (!RemoveIfEmpty()) ShowPreview();
        }

        private bool RemoveIfEmpty()
        {
            if (_entry is null || !string.IsNullOrWhiteSpace(_entry.Content)) return false;
            RemoveRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private static void ResizeEditor(TextBox editor)
        {
            var lines = NotebookFormat.Normalize(editor.Text).Count(character => character == '\n') + 1;
            editor.Height = Math.Max(40, lines * 22 + 20);
        }
    }
}
