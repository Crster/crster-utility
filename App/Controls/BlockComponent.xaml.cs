using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;

namespace App.Controls
{
    public sealed partial class Noteblock : UserControl
    {
        private NotebookEntry? _entry;
        private Func<string, string>? _resolveAttachmentPath;
        private Control? _editor;
        private bool _isPointerOver;
        private bool _isEditorFocused;
        private readonly DispatcherTimer _initialFocusTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private bool _isInitialEditorFocus;

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
            Root.PointerPressed += Root_PointerPressed;
            AddHandler(TappedEvent, new TappedEventHandler(Noteblock_Tapped), true);
            _initialFocusTimer.Tick += (_, _) =>
            {
                _initialFocusTimer.Stop();
                _isInitialEditorFocus = false;
            };
        }

        internal void Configure(NotebookEntry entry, Func<string, string> resolveAttachmentPath, bool startInEditMode = false)
        {
            _entry = entry;
            _resolveAttachmentPath = resolveAttachmentPath;
            if (startInEditMode) ShowEditorContent();
            else ShowPreview();
        }

        internal void ShowEditor()
        {
            if (_entry is null || _editor is not null) return;
            ShowEditorContent();
        }

        private void Noteblock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ShowEditor();
        }

        private async void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || _editor is not null || _entry?.Type is not ("image" or "file")) return;

            var attachment = ParseAttachmentContent(_entry.Content);
            var path = ResolveAttachmentPath(attachment.Path);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            e.Handled = true;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                await Launcher.LaunchFileAsync(file);
            }
            catch (Exception)
            {
                // Ignore unavailable files and keep the block usable for editing.
            }
        }

        private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            if (_editor is null) Root.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            if (_editor is null) Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ShowPreview()
        {
            _initialFocusTimer.Stop();
            _isInitialEditorFocus = false;
            _editor = null;
            Root.Padding = new Thickness(10);
            Root.Background = _isPointerOver
                ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ContentHost.Children.Clear();
            if (_entry is null) return;

            ContentHost.Children.Add(_entry.Type switch
            {
                "image" => CreateImagePreview(),
                "file" => CreateFilePreview(),
                "table" => CreateTablePreview(_entry.Content),
                "todo" => CreateTodoPreview(_entry.Content),
                _ => CreateMarkdownPreview(_entry.Content)
            });
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ShowEditorContent()
        {
            if (_entry is null) return;
            _isInitialEditorFocus = true;
            ContentHost.Children.Clear();
            Root.Padding = new Thickness(0);
            Root.Background = (Brush)Application.Current.Resources["LayerOnMicaBaseAltFillColorDefaultBrush"];

            var editor = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(10),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Disabled);
            editor.Text = NormalizeEditorText(_entry.Content);
            editor.TextChanged += (_, _) =>
            {
                _entry.Content = editor.Text;
                ResizeEditor(editor, editor.Text);
                ContentChanged?.Invoke(this, EventArgs.Empty);
            };
            editor.KeyDown += Editor_KeyDown;
            editor.GotFocus += Editor_GotFocus;
            ResizeEditor(editor, _entry.Content);
            _editor = editor;

            ContentHost.Children.Add(_editor);
            var editorToFocus = _editor;
            void FocusEditorAfterLayout(object? sender, object e)
            {
                LayoutUpdated -= FocusEditorAfterLayout;
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    editorToFocus.Focus(FocusState.Keyboard);
                    if (editorToFocus is TextBox textBox)
                    {
                        ResizeEditor(textBox, _entry.Content!);
                        textBox.SelectionStart = textBox.Text.Length;
                        textBox.SelectionLength = 0;
                    }
                });
            }

            LayoutUpdated += FocusEditorAfterLayout;
        }

        private void Editor_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not Control editor || !ReferenceEquals(sender, _editor)) return;
            _isEditorFocused = true;
            editor.GotFocus -= Editor_GotFocus;
            editor.LostFocus += Editor_LostFocus;
            _initialFocusTimer.Stop();
            _initialFocusTimer.Start();
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ResizeEditor(TextBox editor, string content)
        {
            var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var lineCount = normalizedContent.Count(character => character == '\n') + 1;
            editor.Height = Math.Max(40, lineCount * 22 + 20);
            editor.InvalidateMeasure();
            ContentHost.InvalidateMeasure();
            Root.InvalidateMeasure();
        }

        private static string NormalizeEditorText(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

        private void Editor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, _editor)) return;
            _isEditorFocused = false;
            InteractionStateChanged?.Invoke(this, EventArgs.Empty);
            if (_isInitialEditorFocus)
            {
                if (sender is Control editor)
                {
                    _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => editor.Focus(FocusState.Keyboard));
                }
                return;
            }

            if (RemoveIfContentEmpty()) return;
            ShowPreview();
        }

        private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != global::Windows.System.VirtualKey.Escape || _entry is null) return;
            e.Handled = true;
            if (RemoveIfContentEmpty()) return;

            ShowPreview();
        }

        private bool RemoveIfContentEmpty()
        {
            if (_entry is null || !string.IsNullOrWhiteSpace(_entry.Content)) return false;
            RemoveRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private FrameworkElement CreateImagePreview()
        {
            var attachment = ParseAttachmentContent(_entry!.Content);
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                MaxWidth = 200,
                MaxHeight = 200
            };
            var path = ResolveAttachmentPath(attachment.Path);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) image.Source = new BitmapImage(new Uri(path));
            return CreateAttachmentLayout(image, attachment.Description, Path.GetFileName(attachment.Path));
        }

        private FrameworkElement CreateFilePreview()
        {
            var attachment = ParseAttachmentContent(_entry!.Content);
            var path = ResolveAttachmentPath(attachment.Path);
            var filePreview = new Grid();
            filePreview.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] });
            filePreview.Children.Add(new SymbolIcon
            {
                Symbol = Symbol.Document,
                Width = 64,
                Height = 64,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) _ = LoadFileThumbnailAsync(path, filePreview);
            return CreateAttachmentLayout(filePreview, attachment.Description, Path.GetFileName(attachment.Path));
        }

        private static FrameworkElement CreateAttachmentLayout(FrameworkElement visual, string description, string? title = null)
        {
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(visual, 0);
            layout.Children.Add(visual);

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description)) return layout;

            var details = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
            if (!string.IsNullOrWhiteSpace(title))
            {
                details.Children.Add(new TextBlock
                {
                    Text = title,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }

            if (!string.IsNullOrWhiteSpace(description)) details.Children.Add(CreateAttachmentDescription(description));
            Grid.SetColumn(details, 2);
            layout.Children.Add(details);
            return layout;
        }

        private static TextBlock CreateAttachmentDescription(string description) => new()
        {
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontFamily = new FontFamily("Cascadia Mono")
        };

        private static async Task LoadFileThumbnailAsync(string path, Grid filePreview)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 200);
                if (thumbnail is null) return;
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                filePreview.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = 200, MaxHeight = 200 });
            }
            catch (Exception)
            {
                // Keep the document fallback when Windows cannot provide a thumbnail or file-type icon.
            }
        }

        private string? ResolveAttachmentPath(string path) => Path.IsPathFullyQualified(path) ? path : _resolveAttachmentPath?.Invoke(path);

        private static (string Path, string Description) ParseAttachmentContent(string content)
        {
            var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var firstLineEnd = normalizedContent.IndexOf('\n');
            var path = firstLineEnd >= 0 ? normalizedContent[..firstLineEnd] : normalizedContent;
            var description = firstLineEnd >= 0 ? normalizedContent[(firstLineEnd + 1)..].TrimStart('\n') : string.Empty;
            return (path.Trim(), description.Trim());
        }

        private static FrameworkElement CreateTodoPreview(string content)
        {
            var panel = new StackPanel { Spacing = 0 };
            var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            foreach (var line in normalizedContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var item = line.TrimStart();
                var done = item.StartsWith('+');
                var isTodo = done || item.StartsWith('-');
                var text = isTodo ? item[1..].TrimStart() : line;
                var checkBox = new CheckBox
                {
                    IsChecked = done,
                    IsEnabled = false,
                    Width = 22,
                    MinWidth = 0,
                    Height = 22,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var todoItem = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 22 };
                todoItem.Children.Add(checkBox);
                todoItem.Children.Add(new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(todoItem);
            }
            return panel;
        }

        private static FrameworkElement CreateTablePreview(string content)
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
            var rows = new List<string[]>();
            var row = new List<string>();
            var field = new StringBuilder();
            var isQuoted = false;

            for (var index = 0; index < content.Length; index++)
            {
                var character = content[index];
                if (character == '"')
                {
                    if (isQuoted && index + 1 < content.Length && content[index + 1] == '"')
                    {
                        field.Append(character);
                        index++;
                    }
                    else isQuoted = !isQuoted;
                }
                else if (character == ',' && !isQuoted)
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if ((character == '\r' || character == '\n') && !isQuoted)
                {
                    if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
                    row.Add(field.ToString());
                    rows.Add([.. row]);
                    row.Clear();
                    field.Clear();
                }
                else field.Append(character);
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add([.. row]);
            }

            return rows;
        }

        private static FrameworkElement CreateMarkdownPreview(string content)
        {
            var richText = new RichTextBlock { TextWrapping = TextWrapping.Wrap };
            foreach (var line in content.Split('\n'))
            {
                var paragraph = new Paragraph { FontSize = line.StartsWith("# ") ? 28 : line.StartsWith("## ") ? 22 : line.StartsWith("### ") ? 18 : 14 };
                AddMarkdownInlines(paragraph, line.StartsWith("- ") ? $"• {line[2..]}" : line.TrimStart('#', ' '));
                richText.Blocks.Add(paragraph);
            }
            return richText;
        }

        private static void AddMarkdownInlines(Paragraph paragraph, string text)
        {
            const string pattern = "(~~.+?~~|\\*\\*.+?\\*\\*|(?<!\\*)\\*[^*]+?\\*(?!\\*)|`.+?`|\\[[^]]+\\]\\([^)]+\\))";
            var position = 0;
            foreach (Match match in Regex.Matches(text, pattern))
            {
                if (match.Index > position) paragraph.Inlines.Add(new Run { Text = text[position..match.Index] });
                var token = match.Value;
                if (token.StartsWith("~~")) paragraph.Inlines.Add(new Run { Text = "****", FontFamily = new FontFamily("Cascadia Mono") });
                else if (token.StartsWith("**")) paragraph.Inlines.Add(new Bold { Inlines = { new Run { Text = token[2..^2] } } });
                else if (token.StartsWith('*')) paragraph.Inlines.Add(new Italic { Inlines = { new Run { Text = token[1..^1] } } });
                else if (token.StartsWith('`')) paragraph.Inlines.Add(new Run { Text = token[1..^1], FontFamily = new FontFamily("Cascadia Mono") });
                else paragraph.Inlines.Add(new Run { Text = token[1..token.IndexOf("](", StringComparison.Ordinal)] });
                position = match.Index + match.Length;
            }
            if (position < text.Length) paragraph.Inlines.Add(new Run { Text = text[position..] });
        }

        private static string FormatBytes(long bytes) => bytes >= 1_048_576 ? $"{bytes / 1_048_576d:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    }
}
