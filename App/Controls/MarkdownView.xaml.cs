using App.Models;
using Markdig;
using Markdig.Extensions.Emoji;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace App.Controls
{
    public sealed partial class MarkdownView : UserControl
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        private readonly List<FrameworkElement> _thumbnailImages = [];
        private Func<string, string?>? _attachmentResolver;
        private bool _notebookMode;
        private PasswordCopyValue? _activePassword;
        private sealed record PasswordCopyValue(string RawValue);

        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
            nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(string.Empty, OnMarkdownChanged));

        public MarkdownView()
        {
            InitializeComponent();
            ContentHost.SizeChanged += (_, _) => ResizeThumbnails();
            PreviewKeyDown += MarkdownView_PreviewKeyDown;
        }

        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        internal void ConfigureNotebook(Func<string, string?> attachmentResolver)
        {
            _notebookMode = true;
            _attachmentResolver = attachmentResolver;
            Render(Markdown);
        }

        private static void OnMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
            ((MarkdownView)dependencyObject).Render(args.NewValue as string ?? string.Empty);

        private void Render(string markdown)
        {
            ContentHost.Children.Clear();
            _thumbnailImages.Clear();
            var source = _notebookMode ? NotebookFormat.PrepareMarkdown(markdown) : markdown;
            var document = Markdig.Markdown.Parse(source, Pipeline);
            foreach (var block in document) AddBlock(block, ContentHost);
            ResizeThumbnails();
        }

        private void AddBlock(Markdig.Syntax.Block block, Panel host)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var headingText = CreateRichText(heading.Inline);
                    if (_notebookMode)
                    {
                        var weights = new[] { FontWeights.Bold, FontWeights.SemiBold, FontWeights.Medium, FontWeights.Normal, FontWeights.Light, FontWeights.Thin };
                        var opacities = new[] { 1d, .88, .76, .64, .52, .40 };
                        var brush = (Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as SolidColorBrush)
                            ?? (Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush)
                            ?? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 120, 212));
                        headingText.Foreground = new SolidColorBrush(brush.Color) { Opacity = opacities[Math.Clamp(heading.Level - 1, 0, 5)] };
                        headingText.FontWeight = weights[Math.Clamp(heading.Level - 1, 0, 5)];
                    }
                    else
                    {
                        headingText.FontSize = heading.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 };
                        headingText.FontWeight = FontWeights.SemiBold;
                    }
                    host.Children.Add(headingText);
                    break;
                case ParagraphBlock paragraph:
                    host.Children.Add(CreateRichText(paragraph.Inline));
                    break;
                case FencedCodeBlock fenced:
                    host.Children.Add(CreateCodeBlock(fenced.Lines.ToString()));
                    break;
                case CodeBlock code:
                    host.Children.Add(CreateCodeBlock(code.Lines.ToString()));
                    break;
                case QuoteBlock quote:
                    var quoteHost = new StackPanel { Spacing = 6 };
                    foreach (var child in quote) AddBlock(child, quoteHost);
                    host.Children.Add(new Border
                    {
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                        Padding = new Thickness(12, 4, 4, 4),
                        Child = quoteHost
                    });
                    break;
                case ListBlock list:
                    var listHost = new StackPanel { Spacing = 5 };
                    var number = int.TryParse(list.OrderedStart, out var orderedStart) ? orderedStart : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var row = new Grid { ColumnSpacing = 8 };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        row.ColumnDefinitions.Add(new ColumnDefinition());
                        var marker = new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", VerticalAlignment = VerticalAlignment.Top };
                        var itemHost = new StackPanel { Spacing = 4 };
                        foreach (var child in item) AddBlock(child, itemHost);
                        Grid.SetColumn(itemHost, 1);
                        row.Children.Add(marker);
                        row.Children.Add(itemHost);
                        listHost.Children.Add(row);
                    }
                    host.Children.Add(listHost);
                    break;
                case Table table:
                    host.Children.Add(CreateTable(table));
                    break;
                case ThematicBreakBlock:
                    host.Children.Add(new Border
                    {
                        Height = 1,
                        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                        Margin = new Thickness(0, 4, 0, 4)
                    });
                    break;
                case ContainerBlock container:
                    foreach (var child in container) AddBlock(child, host);
                    break;
            }
        }

        private RichTextBlock CreateRichText(ContainerInline? inline)
        {
            var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var paragraph = new Paragraph();
            if (inline is not null) AddInlines(inline, paragraph.Inlines);
            text.Blocks.Add(paragraph);
            return text;
        }

        private void AddInlines(ContainerInline container, InlineCollection target)
        {
            for (var item = container.FirstChild; item is not null; item = item.NextSibling)
            {
                switch (item)
                {
                    case EmojiInline emoji:
                        target.Add(new Run { Text = emoji.Match });
                        break;
                    case LiteralInline literal:
                        target.Add(new Run { Text = literal.Content.ToString() });
                        break;
                    case CodeInline code:
                        target.Add(new Run { Text = code.Content, FontFamily = new FontFamily("Cascadia Mono") });
                        break;
                    case LineBreakInline:
                        target.Add(new LineBreak());
                        break;
                    case EmphasisInline emphasis when emphasis.DelimiterChar == '=':
                        target.Add(CreateOffsetInline(PlainText(emphasis), 0, true));
                        break;
                    case EmphasisInline emphasis when emphasis.DelimiterChar == '~' && emphasis.DelimiterCount == 1:
                        target.Add(CreateOffsetInline(PlainText(emphasis), 3, false));
                        break;
                    case EmphasisInline emphasis when emphasis.DelimiterChar == '^':
                        target.Add(CreateOffsetInline(PlainText(emphasis), -4, false));
                        break;
                    case EmphasisInline emphasis:
                        var span = new Span();
                        ApplyEmphasis(emphasis, span);
                        AddInlines(emphasis, span.Inlines);
                        target.Add(span);
                        break;
                    case LinkInline link when _notebookMode && TryDecodePassword(link.Url, out var password):
                        target.Add(CreatePasswordInline(password));
                        break;
                    case LinkInline link when link.IsImage:
                        target.Add(CreateImageInline(link));
                        break;
                    case LinkInline link:
                        target.Add(CreateLinkInline(link));
                        break;
                    case ContainerInline nested:
                        AddInlines(nested, target);
                        break;
                }
            }
        }

        private static void ApplyEmphasis(EmphasisInline emphasis, Span span)
        {
            if (emphasis.DelimiterChar is '*' or '_')
            {
                if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                else span.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
            }
            else if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
            {
                span.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
            }
        }

        private static Microsoft.UI.Xaml.Documents.Inline CreateOffsetInline(string value, double verticalOffset, bool highlight)
        {
            var text = new TextBlock
            {
                Text = value,
                FontSize = highlight ? 14 : 10,
                RenderTransform = new TranslateTransform { Y = verticalOffset }
            };
            FrameworkElement visual = text;
            if (highlight)
            {
                visual = new Border
                {
                    Padding = new Thickness(2, 0, 2, 0),
                    Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 255, 215, 0)),
                    Child = text
                };
            }
            return new InlineUIContainer { Child = visual };
        }

        private Microsoft.UI.Xaml.Documents.Inline CreateLinkInline(LinkInline link)
        {
            var targetValue = link.Url ?? string.Empty;
            var hyperlink = new Hyperlink();
            AddInlines(link, hyperlink.Inlines);
            if (!CanOpenTarget(targetValue)) hyperlink.Foreground = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
            else hyperlink.Click += async (_, _) =>
            {
                if (!IsControlDown()) return;
                await OpenTargetAsync(targetValue);
            };
            return hyperlink;
        }

        private Microsoft.UI.Xaml.Documents.Inline CreateImageInline(LinkInline link)
        {
            var targetValue = link.Url ?? string.Empty;
            var resolved = ResolveTarget(targetValue);
            FrameworkElement visual;
            if (resolved is not null)
            {
                var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 240, HorizontalAlignment = HorizontalAlignment.Left };
                try { image.Source = new BitmapImage(resolved.Value.Uri); }
                catch { }
                image.PointerPressed += async (_, args) =>
                {
                    if (!args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
                    args.Handled = true;
                    await OpenTargetAsync(targetValue);
                };
                _thumbnailImages.Add(image);
                visual = image;
            }
            else
            {
                visual = new Border
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    Background = (Brush)Application.Current.Resources["ControlFillColorDisabledBrush"],
                    Child = new TextBlock { Text = $"{PlainText(link)} (unavailable)", TextWrapping = TextWrapping.Wrap }
                };
            }

            return new InlineUIContainer { Child = visual };
        }

        private Microsoft.UI.Xaml.Documents.Inline CreatePasswordInline(string password)
        {
            var hidden = $" {new string('•', password.Length)} ";
            var warningBrush = Application.Current.Resources.ContainsKey("SystemFillColorCautionBrush")
                ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
            var text = new TextBlock
            {
                Text = hidden,
                IsTextSelectionEnabled = true,
                IsTabStop = true,
                Foreground = warningBrush,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(text, "Masked password");
            var passwordValue = new PasswordCopyValue(password);
            text.Tag = passwordValue;

            var copyItem = new MenuFlyoutItem { Text = "Copy password" };
            copyItem.Click += (_, _) => CopyPassword(password);
            var menu = new MenuFlyout();
            menu.Items.Add(copyItem);
            text.ContextFlyout = menu;
            text.PointerPressed += (_, _) =>
            {
                _activePassword = passwordValue;
                text.Focus(FocusState.Pointer);
            };
            text.GotFocus += (_, _) => _activePassword = passwordValue;
            text.LostFocus += (_, _) =>
            {
                if (ReferenceEquals(_activePassword, passwordValue)) _activePassword = null;
            };
            return new InlineUIContainer { Child = text };
        }

        private void MarkdownView_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key != VirtualKey.C || !IsControlDown() || XamlRoot is null) return;
            var password = (FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement)?.Tag as PasswordCopyValue ?? _activePassword;
            if (password is null) return;
            args.Handled = true;
            CopyPassword(password.RawValue);
        }

        private static void CopyPassword(string value)
        {
            var package = new DataPackage();
            package.SetText(value);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        private Border CreateCodeBlock(string code) => new()
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = code.TrimEnd(),
                FontFamily = new FontFamily("Cascadia Mono"),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            }
        };

        private Border CreateTable(Table table)
        {
            var grid = new Grid { RowSpacing = 1, ColumnSpacing = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] };
            var rows = table.OfType<TableRow>().ToList();
            var columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
            for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var column = 0; column < rows[rowIndex].Count; column++)
                {
                    var cell = (TableCell)rows[rowIndex][column];
                    var cellHost = new StackPanel();
                    foreach (var child in cell)
                        if (child is ParagraphBlock paragraph) cellHost.Children.Add(CreateRichText(paragraph.Inline));
                    var border = new Border
                    {
                        Padding = new Thickness(8, 6, 8, 6),
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        Child = cellHost
                    };
                    Grid.SetRow(border, rowIndex);
                    Grid.SetColumn(border, column);
                    grid.Children.Add(border);
                }
            }
            return new Border { CornerRadius = new CornerRadius(6), Child = grid };
        }

        private void ResizeThumbnails()
        {
            if (!_notebookMode || ContentHost.ActualWidth <= 0) return;
            var width = Math.Max(64, ContentHost.ActualWidth * .2);
            foreach (var image in _thumbnailImages) image.Width = width;
        }

        private bool CanOpenTarget(string target) => ResolveTarget(target) is not null;

        private (Uri Uri, string? FilePath)? ResolveTarget(string target)
        {
            if (TryGetLocalAttachmentId(target, out var attachmentId))
            {
                var path = _attachmentResolver?.Invoke(attachmentId);
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? (new Uri(path), path) : null;
            }
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is "http" or "https") return (uri, null);
                if (uri.IsFile && File.Exists(uri.LocalPath)) return (uri, uri.LocalPath);
                return null;
            }
            return Path.IsPathFullyQualified(target) && File.Exists(target) ? (new Uri(target), target) : null;
        }

        private async Task OpenTargetAsync(string target)
        {
            var resolved = ResolveTarget(target);
            if (resolved is null) return;
            try
            {
                if (resolved.Value.FilePath is null) await Launcher.LaunchUriAsync(resolved.Value.Uri);
                else await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(resolved.Value.FilePath));
            }
            catch { }
        }

        private static bool TryGetLocalAttachmentId(string target, out string attachmentId)
        {
            attachmentId = string.Empty;
            if (!target.StartsWith("local://", StringComparison.OrdinalIgnoreCase)) return false;
            var value = target[8..];
            var dot = value.IndexOf('.');
            if (dot >= 0) value = value[..dot];
            if (!Guid.TryParse(value, out var id)) return false;
            attachmentId = id.ToString("D");
            return true;
        }

        private static bool TryDecodePassword(string? url, out string password)
        {
            password = string.Empty;
            if (url is null || !url.StartsWith("notebook-password:", StringComparison.Ordinal)) return false;
            try
            {
                password = Encoding.UTF8.GetString(Convert.FromBase64String(url[18..]));
                return true;
            }
            catch { return false; }
        }

        private static string PlainText(ContainerInline container)
        {
            var output = new StringBuilder();
            for (var item = container.FirstChild; item is not null; item = item.NextSibling)
            {
                if (item is LiteralInline literal) output.Append(literal.Content);
                else if (item is CodeInline code) output.Append(code.Content);
                else if (item is ContainerInline nested) output.Append(PlainText(nested));
            }
            return output.ToString();
        }

        private static bool IsControlDown() =>
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
    }
}
