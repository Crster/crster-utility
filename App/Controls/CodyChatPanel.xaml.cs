using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using App.Models;
using App.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace App.Controls
{
    /// <summary>Cody's chat surface: streamed answers, thinking, and workspace tool activity.</summary>
    public sealed partial class CodyChatPanel : UserControl
    {
        internal const string SessionDividerToolName = "new_session";
        private const int StreamFlushMilliseconds = 80;
        private const double AutoScrollThreshold = 64;

        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _streamTimer;
        private readonly StringBuilder _pendingText = new();
        private readonly StringBuilder _pendingThinking = new();
        private readonly Dictionary<string, ToolRow> _liveToolRows = new(StringComparer.Ordinal);
        private ChatSession _session = new();
        private MarkdownView? _streamingAnswer;
        private FrameworkElement? _streamingAnswerContainer;
        private string _streamingAnswerText = string.Empty;
        private ThinkingCard? _liveThinking;
        private string _workspacePath = string.Empty;
        private bool _isBusy;
        private bool _autoScroll = true;

        public CodyChatPanel()
        {
            InitializeComponent();
            _streamTimer = DispatcherQueue.CreateTimer();
            _streamTimer.Interval = TimeSpan.FromMilliseconds(StreamFlushMilliseconds);
            _streamTimer.Tick += (_, _) => FlushStreamBuffers();
            UpdateEmptyState();
        }

        // Section: Public surface
        internal event EventHandler<string>? PromptSubmitted;
        internal event EventHandler? StopRequested;
        internal event EventHandler? WorkspaceRequested;
        internal event EventHandler? SessionChanged;

        internal ChatSession Session
        {
            get => _session;
            set
            {
                _session = value;
                RenderSession();
            }
        }

        internal string WorkspacePath
        {
            get => _workspacePath;
            set
            {
                _workspacePath = value;
                UpdateEmptyState();
            }
        }

        internal bool IsBusy => _isBusy;

        internal void FocusComposer() => PromptBox.Focus(FocusState.Programmatic);

        internal void SetBusy(bool busy)
        {
            _isBusy = busy;
            SendIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            StopIcon.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(SendButton, busy ? "Stop Cody" : "Send prompt");
            AutomationProperties.SetName(SendButton, busy ? "Stop Cody" : "Send prompt");
            if (busy) _streamTimer.Start();
            else
            {
                _streamTimer.Stop();
                FlushStreamBuffers();
            }
        }

        // Section: Turn lifecycle
        internal void BeginTurn()
        {
            _pendingText.Clear();
            _pendingThinking.Clear();
            _streamingAnswer = null;
            _streamingAnswerContainer = null;
            _streamingAnswerText = string.Empty;
            _liveThinking = null;
            _liveToolRows.Clear();
            _autoScroll = true;
        }

        /// <summary>Commits whatever the turn produced. Streamed text wins; the returned answer is the fallback.</summary>
        internal void CompleteTurn(string answerText)
        {
            FlushStreamBuffers();
            CommitThinking();
            var text = string.IsNullOrWhiteSpace(_streamingAnswerText) ? answerText : _streamingAnswerText;
            CommitStreamingAnswer(text);
            foreach (var row in _liveToolRows.Values) row.MarkInterrupted();
            _liveToolRows.Clear();
        }

        internal void HandleAgentEvent(CodyAgentEvent agentEvent)
        {
            if (DispatcherQueue.HasThreadAccess) ApplyAgentEvent(agentEvent);
            else _ = DispatcherQueue.TryEnqueue(() => ApplyAgentEvent(agentEvent));
        }

        private void ApplyAgentEvent(CodyAgentEvent agentEvent)
        {
            switch (agentEvent.Kind)
            {
                case CodyAgentEventKind.ThinkingDelta:
                    _pendingThinking.Append(agentEvent.Content);
                    break;
                case CodyAgentEventKind.TextDelta:
                    _pendingText.Append(agentEvent.Content);
                    break;
                case CodyAgentEventKind.ToolStarted:
                    FlushStreamBuffers();
                    CommitThinking();
                    CommitStreamingAnswer(_streamingAnswerText);
                    BeginToolRow(agentEvent);
                    break;
                case CodyAgentEventKind.ToolCompleted:
                    CompleteToolRow(agentEvent);
                    break;
                case CodyAgentEventKind.Notice:
                    FlushStreamBuffers();
                    AddMessage(new ChatMessage(ChatItemKind.Assistant, agentEvent.Title, agentEvent.Content));
                    break;
            }
            if (!_streamTimer.IsRunning) FlushStreamBuffers();
        }

        // Section: Streaming
        private void FlushStreamBuffers()
        {
            if (_pendingThinking.Length > 0)
            {
                var delta = _pendingThinking.ToString();
                _pendingThinking.Clear();
                EnsureThinkingCard().Append(delta);
                ScrollToLatest();
            }
            if (_pendingText.Length == 0) return;

            var textDelta = _pendingText.ToString();
            _pendingText.Clear();
            CommitThinking();
            _streamingAnswerText += textDelta;
            EnsureStreamingAnswer().Markdown = _streamingAnswerText;
            ScrollToLatest();
        }

        private MarkdownView EnsureStreamingAnswer()
        {
            if (_streamingAnswer is not null) return _streamingAnswer;

            RemoveEmptyState();
            _streamingAnswer = new MarkdownView();
            var container = new Border
            {
                Padding = new Thickness(2, 2, 2, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = _streamingAnswer
            };
            _streamingAnswerContainer = container;
            TranscriptHost.Children.Add(container);
            return _streamingAnswer;
        }

        private void CommitStreamingAnswer(string text)
        {
            if (_streamingAnswerContainer is not null)
            {
                TranscriptHost.Children.Remove(_streamingAnswerContainer);
                _streamingAnswerContainer = null;
                _streamingAnswer = null;
            }
            _streamingAnswerText = string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
                AddMessage(new ChatMessage(ChatItemKind.Assistant, "Cody", text.Trim()));
        }

        private ThinkingCard EnsureThinkingCard()
        {
            if (_liveThinking is not null) return _liveThinking;

            RemoveEmptyState();
            _liveThinking = new ThinkingCard();
            TranscriptHost.Children.Add(_liveThinking.Container);
            return _liveThinking;
        }

        private void CommitThinking()
        {
            if (_liveThinking is null) return;

            var card = _liveThinking;
            _liveThinking = null;
            TranscriptHost.Children.Remove(card.Container);
            var thinkingText = card.Text.Trim();
            if (thinkingText.Length > 0)
                AddMessage(new ChatMessage(ChatItemKind.Thinking, card.ElapsedTitle, thinkingText));
        }

        // Section: Tool activity
        private void BeginToolRow(CodyAgentEvent agentEvent)
        {
            RemoveEmptyState();
            var row = new ToolRow(agentEvent.Title, DescribeTool(agentEvent.Title, agentEvent.Arguments));
            _liveToolRows[agentEvent.ToolId] = row;
            TranscriptHost.Children.Add(row.Container);
            ScrollToLatest();
        }

        private void CompleteToolRow(CodyAgentEvent agentEvent)
        {
            if (_liveToolRows.Remove(agentEvent.ToolId, out var row))
                TranscriptHost.Children.Remove(row.Container);
            AddMessage(new ChatMessage(
                ChatItemKind.Tool,
                agentEvent.Title,
                agentEvent.Content,
                ToolArguments: agentEvent.Arguments is null ? null : (JsonObject)agentEvent.Arguments.DeepClone(),
                ToolSucceeded: agentEvent.Succeeded,
                DiffOld: agentEvent.DiffOld,
                DiffNew: agentEvent.DiffNew));
        }

        // Section: Transcript
        internal void AddMessage(ChatMessage message)
        {
            _session.Messages.Add(message);
            RenderMessage(message);
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Marks the start of a new session that carries a compacted summary of the previous one.</summary>
        internal void AddSessionDivider(string summary) =>
            AddMessage(new ChatMessage(ChatItemKind.Tool, SessionDividerToolName, summary, ToolSucceeded: true));

        internal void RenderSession()
        {
            TranscriptHost.Children.Clear();
            foreach (var message in _session.Messages) RenderMessage(message);
            UpdateEmptyState();
            ScrollToLatest();
        }

        private void RenderMessage(ChatMessage message)
        {
            RemoveEmptyState();
            TranscriptHost.Children.Add(message.Kind switch
            {
                ChatItemKind.User => CreateUserBubble(message),
                ChatItemKind.Thinking => CreateThinkingBlock(message),
                ChatItemKind.Tool => message.Title == SessionDividerToolName
                    ? CreateSessionDivider(message)
                    : CreateToolBlock(message, _workspacePath),
                ChatItemKind.Error => CreateErrorBlock(message),
                _ => CreateAssistantBlock(message)
            });
            ScrollToLatest();
        }

        private static UIElement CreateUserBubble(ChatMessage message) => new Border
        {
            Padding = new Thickness(14, 10, 14, 11),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 6, 0, 2),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Resource("AccentFillColorTertiaryBrush"),
            Child = new TextBlock
            {
                Text = message.Content,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            }
        };

        private static UIElement CreateAssistantBlock(ChatMessage message)
        {
            var body = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 6) };
            if (!string.IsNullOrWhiteSpace(message.Title) && message.Title != "Cody")
                body.Children.Add(new TextBlock
                {
                    Text = message.Title,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                });
            body.Children.Add(new MarkdownView { Markdown = message.Content });
            return body;
        }

        private static UIElement CreateErrorBlock(ChatMessage message) => new Border
        {
            Padding = new Thickness(12, 9, 12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Resource("CardStrokeColorDefaultBrush"),
            Background = Resource("ControlFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = message.Content,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontSize = 12,
                Foreground = Resource("SystemFillColorCriticalBrush")
            }
        };

        private static UIElement CreateSessionDivider(ChatMessage message)
        {
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            header.Children.Add(new FontIcon
            {
                Glyph = "",
                FontSize = 11,
                Foreground = Resource("TextFillColorTertiaryBrush")
            });
            header.Children.Add(new TextBlock
            {
                Text = "New session · previous work compacted",
                FontSize = 12,
                Foreground = Resource("TextFillColorTertiaryBrush")
            });
            return CreateDisclosure(header, message.Content, HorizontalAlignment.Center);
        }

        private static UIElement CreateThinkingBlock(ChatMessage message)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new FontIcon
            {
                Glyph = "",
                FontSize = 11,
                Foreground = Resource("TextFillColorTertiaryBrush")
            });
            header.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(message.Title) ? "Thought" : message.Title,
                FontSize = 12,
                FontStyle = global::Windows.UI.Text.FontStyle.Italic,
                Foreground = Resource("TextFillColorTertiaryBrush")
            });
            var container = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = 4 };
            container.Children.Add(header);
            if (message.Content.Length > 0)
                container.Children.Add(new TextBlock
                {
                    Text = message.Content,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                });
            return container;
        }

        private static UIElement CreateToolBlock(ChatMessage message, string workspacePath)
        {
            var succeeded = message.ToolSucceeded != false;
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(new FontIcon
            {
                Glyph = succeeded ? "" : "",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Foreground = Resource(succeeded ? "TextFillColorTertiaryBrush" : "SystemFillColorCriticalBrush")
            });
            header.Children.Add(new FontIcon
            {
                Glyph = ToolGlyph(message.Title),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Foreground = Resource(ToolAccentBrush(message.Title))
            });
            header.Children.Add(new TextBlock
            {
                Text = ToolVerb(message.Title),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Foreground = Resource("TextFillColorSecondaryBrush")
            });
            var detail = DescribeTool(message.Title, message.ToolArguments);
            if (detail.Length > 0)
                header.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                });
            if (message.DiffOld is not null && message.DiffNew is not null)
                return CreateDisclosure(header, BuildDiffView(message.DiffOld, message.DiffNew), HorizontalAlignment.Left, hasDetails: true);
            if (message.Title == "list_workspace_entries" && BuildListView(message.Content, workspacePath) is { } listView)
                return CreateDisclosure(header, listView, HorizontalAlignment.Left, hasDetails: true);
            if (message.Title == "search_workspace_files" && BuildSearchView(message.Content, workspacePath) is { } searchView)
                return CreateDisclosure(header, searchView, HorizontalAlignment.Left, hasDetails: true);
            return CreateDisclosure(header, FormatToolOutput(message.Content), HorizontalAlignment.Left);
        }

        /// <summary>Normalizes an absolute or workspace-relative path to a forward-slashed, workspace-relative path.</summary>
        private static string NormalizePath(string path, string workspacePath)
        {
            if (!string.IsNullOrWhiteSpace(workspacePath) && Path.IsPathRooted(path))
            {
                try { path = Path.GetRelativePath(workspacePath, path); }
                catch (ArgumentException) { }
            }
            return path.Replace('\\', '/');
        }

        /// <summary>Renders list_workspace_entries results as one normalized path per line instead of raw JSON.</summary>
        private static UIElement? BuildListView(string output, string workspacePath)
        {
            JsonObject? result;
            try { result = JsonNode.Parse(output) as JsonObject; }
            catch (JsonException) { return null; }
            if (result?["items"] is not JsonArray items) return null;
            var panel = new StackPanel { Spacing = 2 };
            foreach (var item in items.OfType<JsonObject>())
            {
                var path = item["path"]?.GetValue<string>();
                if (path is null) continue;
                var isDirectory = item["attribute"]?.GetValue<string>()?.Contains("Directory", StringComparison.OrdinalIgnoreCase) == true;
                var normalized = NormalizePath(path, workspacePath) + (isDirectory ? "/" : "");
                panel.Children.Add(new TextBlock
                {
                    Text = normalized,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    Foreground = Resource(isDirectory ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush")
                });
            }
            if (result["is_truncated"]?.GetValue<bool>() == true)
                panel.Children.Add(new TextBlock
                {
                    Text = "... results truncated",
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                });
            return panel.Children.Count == 0 ? null : panel;
        }

        /// <summary>Renders search_workspace_files results as normalized path + snippet, with the match highlighted.</summary>
        private static UIElement? BuildSearchView(string output, string workspacePath)
        {
            JsonObject? result;
            try { result = JsonNode.Parse(output) as JsonObject; }
            catch (JsonException) { return null; }
            if (result?["matches"] is not JsonArray matches) return null;
            var panel = new StackPanel { Spacing = 10 };
            foreach (var match in matches.OfType<JsonObject>())
            {
                var filename = match["filename"]?.GetValue<string>();
                var snippet = match["snippet"]?.GetValue<string>();
                if (filename is null || snippet is null) continue;
                var snippetStart = match["snippet_start"]?.GetValue<int>() ?? 0;
                var matchStart = match["match_start"]?.GetValue<int>() ?? 0;
                var matchText = match["match"]?.GetValue<string>() ?? string.Empty;

                var entry = new StackPanel { Spacing = 2 };
                entry.Children.Add(new TextBlock
                {
                    Text = NormalizePath(filename, workspacePath),
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    Foreground = Resource("TextFillColorPrimaryBrush")
                });
                entry.Children.Add(BuildHighlightedSnippet(snippet, matchStart - snippetStart, matchText.Length));
                panel.Children.Add(entry);
            }
            if (result["is_truncated"]?.GetValue<bool>() == true)
                panel.Children.Add(new TextBlock
                {
                    Text = "... results truncated",
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                });
            return panel.Children.Count == 0 ? null : panel;
        }

        private static TextBlock BuildHighlightedSnippet(string snippet, int highlightStart, int highlightLength)
        {
            var textBlock = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            if (highlightStart < 0 || highlightLength <= 0 || highlightStart + highlightLength > snippet.Length)
            {
                textBlock.Inlines.Add(new Run { Text = snippet, Foreground = Resource("TextFillColorTertiaryBrush") });
                return textBlock;
            }
            if (highlightStart > 0)
                textBlock.Inlines.Add(new Run { Text = snippet[..highlightStart], Foreground = Resource("TextFillColorTertiaryBrush") });
            textBlock.Inlines.Add(new Run
            {
                Text = snippet.Substring(highlightStart, highlightLength),
                Foreground = Resource("SystemFillColorCautionBrush"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var afterStart = highlightStart + highlightLength;
            if (afterStart < snippet.Length)
                textBlock.Inlines.Add(new Run { Text = snippet[afterStart..], Foreground = Resource("TextFillColorTertiaryBrush") });
            return textBlock;
        }

        /// <summary>A single quiet line that expands to its raw detail on click.</summary>
        private static UIElement CreateDisclosure(FrameworkElement header, string details, HorizontalAlignment alignment) =>
            CreateDisclosure(
                header,
                new TextBlock
                {
                    Text = details,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                },
                alignment,
                hasDetails: details.Length > 0);

        /// <summary>A single quiet line that expands to arbitrary detail content on click.</summary>
        private static UIElement CreateDisclosure(FrameworkElement header, UIElement detailsContent, HorizontalAlignment alignment, bool hasDetails)
        {
            var chevron = new FontIcon
            {
                Glyph = "",
                FontSize = 9,
                Margin = new Thickness(2, 0, 0, 0),
                Foreground = Resource("TextFillColorDisabledBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var headerContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerContent.Children.Add(header);
            headerContent.Children.Add(chevron);

            var detailsView = new Border
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(10, 8, 10, 9),
                CornerRadius = new CornerRadius(8),
                Background = Resource("ControlFillColorSecondaryBrush"),
                Child = new ScrollViewer
                {
                    MaxHeight = 320,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = detailsContent
                }
            };

            var headerButton = new Button
            {
                Content = headerContent,
                Padding = new Thickness(6, 3, 6, 3),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = alignment,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                IsEnabled = hasDetails
            };
            headerButton.Click += (_, _) =>
            {
                var isExpanded = detailsView.Visibility == Visibility.Visible;
                detailsView.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;
                chevron.Glyph = isExpanded ? "" : "";
            };

            var container = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            container.Children.Add(headerButton);
            container.Children.Add(detailsView);
            return container;
        }

        // Section: Diff rendering
        private const int MaximumDiffLines = 2000;
        private const int DiffContextLines = 2;

        /// <summary>Renders a compact colorized unified diff: removed lines in red, added lines in green,
        /// unchanged context collapsed to a marker line away from any change.</summary>
        private static UIElement BuildDiffView(string oldText, string newText)
        {
            var oldLines = SplitLines(oldText);
            var newLines = SplitLines(newText);
            if (oldLines.Length > MaximumDiffLines || newLines.Length > MaximumDiffLines)
                return new TextBlock
                {
                    Text = $"Diff too large to display inline ({oldLines.Length} -> {newLines.Length} lines).",
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                };

            var panel = new StackPanel { Spacing = 0 };
            var lineFontFamily = new FontFamily("Cascadia Mono");
            var skippedContext = 0;
            void FlushSkipped()
            {
                if (skippedContext <= 0) return;
                panel.Children.Add(new TextBlock
                {
                    Text = $"... {skippedContext} unchanged line{(skippedContext == 1 ? "" : "s")}",
                    FontFamily = lineFontFamily,
                    FontSize = 11,
                    Margin = new Thickness(2, 2, 0, 2),
                    Foreground = Resource("TextFillColorTertiaryBrush")
                });
                skippedContext = 0;
            }

            var diff = ComputeLineDiff(oldLines, newLines);
            for (var index = 0; index < diff.Count; index++)
            {
                var entry = diff[index];
                if (entry.Sign == ' ')
                {
                    var nearChange =
                        (index > 0 && diff[index - 1].Sign != ' ') ||
                        Enumerable.Range(index + 1, Math.Min(DiffContextLines, diff.Count - index - 1)).Any(next => diff[next].Sign != ' ');
                    if (!nearChange)
                    {
                        skippedContext++;
                        continue;
                    }
                }
                FlushSkipped();
                var (prefix, background, foreground) = entry.Sign switch
                {
                    '+' => ("+ ", "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
                    '-' => ("- ", "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
                    _ => ("  ", (string?)null, "TextFillColorTertiaryBrush")
                };
                panel.Children.Add(new Border
                {
                    Background = background is null ? null : Resource(background),
                    Child = new TextBlock
                    {
                        Text = prefix + entry.Line,
                        FontFamily = lineFontFamily,
                        FontSize = 11,
                        TextWrapping = TextWrapping.NoWrap,
                        IsTextSelectionEnabled = true,
                        Padding = new Thickness(4, 0, 4, 0),
                        Foreground = Resource(foreground)
                    }
                });
            }
            FlushSkipped();
            return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

        /// <summary>Line-based LCS diff. Quadratic in line count; callers must cap input size.</summary>
        private static List<(char Sign, string Line)> ComputeLineDiff(string[] oldLines, string[] newLines)
        {
            var oldCount = oldLines.Length;
            var newCount = newLines.Length;
            var lengths = new int[oldCount + 1, newCount + 1];
            for (var i = oldCount - 1; i >= 0; i--)
                for (var j = newCount - 1; j >= 0; j--)
                    lengths[i, j] = oldLines[i] == newLines[j]
                        ? lengths[i + 1, j + 1] + 1
                        : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

            var result = new List<(char Sign, string Line)>();
            var oldIndex = 0;
            var newIndex = 0;
            while (oldIndex < oldCount && newIndex < newCount)
            {
                if (oldLines[oldIndex] == newLines[newIndex])
                {
                    result.Add((' ', oldLines[oldIndex]));
                    oldIndex++;
                    newIndex++;
                }
                else if (lengths[oldIndex + 1, newIndex] >= lengths[oldIndex, newIndex + 1])
                {
                    result.Add(('-', oldLines[oldIndex]));
                    oldIndex++;
                }
                else
                {
                    result.Add(('+', newLines[newIndex]));
                    newIndex++;
                }
            }
            while (oldIndex < oldCount) { result.Add(('-', oldLines[oldIndex])); oldIndex++; }
            while (newIndex < newCount) { result.Add(('+', newLines[newIndex])); newIndex++; }
            return result;
        }

        // Section: Tool presentation
        private static string ToolVerb(string toolName) => toolName switch
        {
            "read_workspace_file" => "Read",
            "write_workspace_file" => "Write",
            "patch_workspace_file" => "Edit",
            "update_workspace_command" => "Update command",
            "delete_workspace_entry" => "Delete",
            "search_workspace_files" => "Search",
            "list_workspace_entries" => "List",
            "run_workspace_command" => "Run",
            "run_elevated_workspace_command" => "Run as admin",
            _ => toolName.Replace('_', ' ')
        };

        private static string ToolGlyph(string toolName) => toolName switch
        {
            "read_workspace_file" => "",
            "write_workspace_file" or "patch_workspace_file" or "update_workspace_command" => "",
            "delete_workspace_entry" => "",
            "search_workspace_files" => "",
            "list_workspace_entries" => "",
            "run_workspace_command" or "run_elevated_workspace_command" => "",
            _ => ""
        };

        /// <summary>Per-action accent so write/delete/run tools are distinguishable at a glance.</summary>
        private static string ToolAccentBrush(string toolName) => toolName switch
        {
            "write_workspace_file" or "patch_workspace_file" or "update_workspace_command" => "SystemFillColorCautionBrush",
            "delete_workspace_entry" => "SystemFillColorCriticalBrush",
            "run_workspace_command" or "run_elevated_workspace_command" => "SystemFillColorAttentionBrush",
            _ => "TextFillColorSecondaryBrush"
        };

        private static string DescribeTool(string toolName, JsonObject? arguments) =>
            arguments is null ? string.Empty : CodyAgentService.DescribeToolCall(toolName, arguments);

        private static string FormatToolOutput(string output)
        {
            try
            {
                if (JsonNode.Parse(output) is not JsonObject result) return output;
                if (result["content"] is JsonValue value
                    && value.TryGetValue<string>(out var embedded)
                    && !string.IsNullOrWhiteSpace(embedded))
                    return embedded;
                return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                return output;
            }
        }

        // Section: Composer
        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                StopRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            SubmitPrompt(PromptBox.Text);
        }

        private void PromptBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if (shiftState.HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

            e.Handled = true;
            if (!_isBusy) SubmitPrompt(PromptBox.Text);
        }

        /// <summary>Sends a prompt from the composer or from an "Ask Cody" entry point elsewhere in the page.</summary>
        internal void SubmitPrompt(string prompt)
        {
            var text = prompt.Trim();
            if (text.Length == 0 || _isBusy) return;

            PromptBox.Text = string.Empty;
            PromptSubmitted?.Invoke(this, text);
        }

        private void ChooseWorkspaceButton_Click(object sender, RoutedEventArgs e) =>
            WorkspaceRequested?.Invoke(this, EventArgs.Empty);

        private void UpdateEmptyState()
        {
            var hasWorkspace = !string.IsNullOrWhiteSpace(_workspacePath);
            PromptBox.IsEnabled = hasWorkspace;
            SendButton.IsEnabled = hasWorkspace;
            EmptyStateDetailText.Text = hasWorkspace
                ? "Ask Cody to inspect, change, or run your project."
                : "Choose a workspace so Cody can read and change your project files.";
            ChooseWorkspaceButton.Visibility = hasWorkspace ? Visibility.Collapsed : Visibility.Visible;
            if (_session.Messages.Count == 0 && !TranscriptHost.Children.Contains(EmptyState))
                TranscriptHost.Children.Insert(0, EmptyState);
        }

        private void RemoveEmptyState() => TranscriptHost.Children.Remove(EmptyState);

        // Section: Scrolling
        private void TranscriptScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) =>
            _autoScroll = TranscriptScroller.ScrollableHeight - TranscriptScroller.VerticalOffset <= AutoScrollThreshold;

        private void ScrollToLatest()
        {
            if (!_autoScroll) return;
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                TranscriptScroller.UpdateLayout();
                TranscriptScroller.ChangeView(null, TranscriptScroller.ScrollableHeight, null, true);
            });
        }

        private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

        // Section: Live rows
        /// <summary>The streaming thinking block shown while the model reasons.</summary>
        private sealed class ThinkingCard
        {
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private readonly TextBlock _body;
            private readonly StringBuilder _text = new();

            public ThinkingCard()
            {
                _body = new TextBlock
                {
                    FontSize = 12,
                    FontStyle = global::Windows.UI.Text.FontStyle.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 6,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                };
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new ProgressRing { Width = 12, Height = 12, IsActive = true });
                header.Children.Add(new TextBlock
                {
                    Text = "Thinking",
                    FontSize = 12,
                    FontStyle = global::Windows.UI.Text.FontStyle.Italic,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                });
                var content = new StackPanel { Spacing = 4, Margin = new Thickness(6, 2, 0, 2) };
                content.Children.Add(header);
                content.Children.Add(_body);
                Container = content;
            }

            public FrameworkElement Container { get; }

            public string Text => _text.ToString();

            public string ElapsedTitle => $"Thought for {Math.Max(1, (int)_stopwatch.Elapsed.TotalSeconds)}s";

            public void Append(string delta)
            {
                _text.Append(delta);
                _body.Text = _text.ToString();
            }
        }

        /// <summary>A tool chip shown while its call is still running.</summary>
        private sealed class ToolRow
        {
            private readonly TextBlock _verbText;

            public ToolRow(string toolName, string detail)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(6, 3, 6, 3)
                };
                row.Children.Add(new ProgressRing { Width = 12, Height = 12, IsActive = true });
                row.Children.Add(new FontIcon
                {
                    Glyph = ToolGlyph(toolName),
                    FontSize = 12,
                    Foreground = Resource("TextFillColorSecondaryBrush")
                });
                _verbText = new TextBlock
                {
                    Text = ToolVerb(toolName),
                    FontSize = 12,
                    Foreground = Resource("TextFillColorSecondaryBrush")
                };
                row.Children.Add(_verbText);
                if (detail.Length > 0)
                    row.Children.Add(new TextBlock
                    {
                        Text = detail,
                        FontSize = 12,
                        FontFamily = new FontFamily("Cascadia Mono"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = Resource("TextFillColorTertiaryBrush")
                    });
                Container = row;
            }

            public FrameworkElement Container { get; }

            public void MarkInterrupted() => _verbText.Text += " (stopped)";
        }
    }
}
