using System;
using System.Linq;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace App.Controls
{
    public sealed partial class MarkdownView : UserControl
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
            nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(string.Empty, OnMarkdownChanged));

        public MarkdownView() => InitializeComponent();

        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        private static void OnMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
            ((MarkdownView)dependencyObject).Render(args.NewValue as string ?? string.Empty);

        private void Render(string markdown)
        {
            ContentHost.Children.Clear();
            var document = Markdig.Markdown.Parse(markdown, Pipeline);
            foreach (var block in document) AddBlock(block, ContentHost);
        }

        private void AddBlock(Markdig.Syntax.Block block, Panel host)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var headingText = CreateRichText(heading.Inline);
                    headingText.FontSize = heading.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 };
                    headingText.FontWeight = FontWeights.SemiBold;
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
                    host.Children.Add(new Border { BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], Padding = new Thickness(12, 4, 4, 4), Child = quoteHost });
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
                        Grid.SetColumn(itemHost, 1); row.Children.Add(marker); row.Children.Add(itemHost); listHost.Children.Add(row);
                    }
                    host.Children.Add(listHost);
                    break;
                case Table table:
                    host.Children.Add(CreateTable(table));
                    break;
                case ThematicBreakBlock:
                    host.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], Margin = new Thickness(0, 4, 0, 4) });
                    break;
                case ContainerBlock container:
                    foreach (var child in container) AddBlock(child, host);
                    break;
            }
        }

        private static RichTextBlock CreateRichText(ContainerInline? inline)
        {
            var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var paragraph = new Paragraph();
            if (inline is not null) AddInlines(inline, paragraph.Inlines);
            text.Blocks.Add(paragraph);
            return text;
        }

        private static void AddInlines(ContainerInline container, InlineCollection target)
        {
            for (var item = container.FirstChild; item is not null; item = item.NextSibling)
            {
                switch (item)
                {
                    case LiteralInline literal: target.Add(new Run { Text = literal.Content.ToString() }); break;
                    case CodeInline code: target.Add(new Run { Text = code.Content, FontFamily = new FontFamily("Cascadia Mono") }); break;
                    case LineBreakInline: target.Add(new LineBreak()); break;
                    case EmphasisInline emphasis:
                        var span = new Span();
                        if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold; else span.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
                        AddInlines(emphasis, span.Inlines); target.Add(span); break;
                    case LinkInline link:
                        var hyperlink = new Hyperlink();
                        if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)) hyperlink.NavigateUri = uri;
                        AddInlines(link, hyperlink.Inlines); target.Add(hyperlink); break;
                    case ContainerInline nested: AddInlines(nested, target); break;
                }
            }
        }

        private static Border CreateCodeBlock(string code) => new()
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"], CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = code.TrimEnd(), FontFamily = new FontFamily("Cascadia Mono"), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap } }
        };

        private static Border CreateTable(Table table)
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
                    foreach (var child in cell) if (child is ParagraphBlock paragraph) cellHost.Children.Add(CreateRichText(paragraph.Inline));
                    var border = new Border { Padding = new Thickness(8, 6, 8, 6), Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = cellHost };
                    Grid.SetRow(border, rowIndex); Grid.SetColumn(border, column); grid.Children.Add(border);
                }
            }
            return new Border { CornerRadius = new CornerRadius(6), Child = grid };
        }
    }
}
