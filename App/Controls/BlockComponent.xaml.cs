using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace App.Controls
{
    public sealed partial class Noteblock : UserControl
    {
        private NotebookEntry? _entry;
        private Func<string, string>? _resolveAttachmentPath;
        private Control? _editor;
        private bool _isPointerOver;

        public event EventHandler? ContentChanged;
        public event EventHandler? RemoveRequested;

        internal NotebookEntry Entry => _entry ?? throw new InvalidOperationException("The noteblock has not been configured.");

        public Noteblock()
        {
            InitializeComponent();
            HorizontalAlignment = HorizontalAlignment.Stretch;
            AddHandler(TappedEvent, new TappedEventHandler(Noteblock_Tapped), true);
        }

        internal void Configure(NotebookEntry entry, Func<string, string> resolveAttachmentPath)
        {
            _entry = entry;
            _resolveAttachmentPath = resolveAttachmentPath;
            ShowPreview();
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

        private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            if (_editor is null) Root.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        }

        private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            if (_editor is null) Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private void ShowPreview()
        {
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
                "password" => new TextBlock { Text = "••••••••", FontFamily = new FontFamily("Consolas"), Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                _ => CreateMarkdownPreview(_entry.Content)
            });
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ShowEditorContent()
        {
            if (_entry is null) return;
            ContentHost.Children.Clear();
            Root.Padding = new Thickness(0);
            Root.Background = (Brush)Application.Current.Resources["LayerOnMicaBaseAltFillColorDefaultBrush"];

            if (_entry.Type == "password")
            {
                var editor = new PasswordBox
                {
                    Password = _entry.Content,
                    PlaceholderText = "Password",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10),
                    Margin = new Thickness(0),
                    MinHeight = 0,
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                };
                editor.PasswordChanged += (_, _) => { _entry.Content = editor.Password; ContentChanged?.Invoke(this, EventArgs.Empty); };
                editor.KeyDown += Editor_KeyDown;
                editor.LostFocus += Editor_LostFocus;
                _editor = editor;
            }
            else
            {
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
                editor.LostFocus += Editor_LostFocus;
                ResizeEditor(editor, _entry.Content);
                _editor = editor;
            }

            ContentHost.Children.Add(_editor);
            var editorToFocus = _editor;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                editorToFocus.Focus(FocusState.Programmatic);
                if (editorToFocus is TextBox textBox)
                {
                    ResizeEditor(textBox, _entry.Content!);
                    textBox.SelectionStart = textBox.Text.Length;
                    textBox.SelectionLength = 0;
                }
            });
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
            if (ReferenceEquals(sender, _editor)) ShowPreview();
        }

        private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != global::Windows.System.VirtualKey.Escape || _entry is null) return;
            e.Handled = true;
            if (string.IsNullOrWhiteSpace(_entry.Content))
            {
                RemoveRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            ShowPreview();
        }

        private FrameworkElement CreateImagePreview()
        {
            var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 560, HorizontalAlignment = HorizontalAlignment.Stretch };
            var path = _resolveAttachmentPath?.Invoke(_entry!.Content);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) image.Source = new BitmapImage(new Uri(path));
            return image;
        }

        private FrameworkElement CreateFilePreview()
        {
            var path = _resolveAttachmentPath?.Invoke(_entry!.Content);
            var size = path is not null && File.Exists(path) ? $"  •  {FormatBytes(new FileInfo(path).Length)}" : string.Empty;
            return new TextBlock { Text = $"📎  {Path.GetFileName(_entry!.Content)}{size}", TextWrapping = TextWrapping.Wrap, FontSize = 15 };
        }

        private static FrameworkElement CreateTodoPreview(string content)
        {
            var panel = new StackPanel { Spacing = 4 };
            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var done = line.TrimStart().StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);
                panel.Children.Add(new CheckBox { Content = line.Length >= 5 ? line[5..].TrimStart() : line, IsChecked = done, IsEnabled = false });
            }
            return panel;
        }

        private static FrameworkElement CreateTablePreview(string content)
        {
            var rows = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.Trim().StartsWith("| ---", StringComparison.Ordinal)).Select(line => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray()).ToList();
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
            const string pattern = "(\\*\\*.+?\\*\\*|(?<!\\*)\\*[^*]+?\\*(?!\\*)|`.+?`|\\[[^]]+\\]\\([^)]+\\))";
            var position = 0;
            foreach (Match match in Regex.Matches(text, pattern))
            {
                if (match.Index > position) paragraph.Inlines.Add(new Run { Text = text[position..match.Index] });
                var token = match.Value;
                if (token.StartsWith("**")) paragraph.Inlines.Add(new Bold { Inlines = { new Run { Text = token[2..^2] } } });
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
