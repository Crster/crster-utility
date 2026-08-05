using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
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
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace App.Controls
{
    /// <summary>Cody's chat surface: streamed answers, thinking, and workspace tool activity.</summary>
    public sealed partial class CodyChatPanel : UserControl
    {
        internal const string SessionDividerToolName = "new_session";
        private const int StreamFlushMilliseconds = 80;
        private const double AutoScrollThreshold = 64;
        internal const int MaximumInlineContextCharacters = 10_240;

        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _streamTimer;
        private readonly StringBuilder _pendingText = new();
        private readonly StringBuilder _pendingThinking = new();
        private readonly Dictionary<string, ToolRow> _liveToolRows = new(StringComparer.Ordinal);
        private readonly List<ChatAttachment> _stagedAttachments = [];
        private ChatSession _session = new();
        private MarkdownView? _streamingAnswer;
        private FrameworkElement? _streamingAnswerContainer;
        private ChatMessage? _pendingUserMessage;
        private UIElement? _pendingUserMessageContainer;
        private string _streamingAnswerText = string.Empty;
        private ThinkingCard? _liveThinking;
        private ProcessingStatusRow? _processingStatus;
        private string _workspacePath = string.Empty;
        private bool _isBusy;
        private bool _autoScroll = true;

        public CodyChatPanel()
        {
            InitializeComponent();
            _streamTimer = DispatcherQueue.CreateTimer();
            _streamTimer.Interval = TimeSpan.FromMilliseconds(StreamFlushMilliseconds);
            _streamTimer.Tick += (_, _) =>
            {
                FlushStreamBuffers();
                RefreshProcessingStatus();
            };
            UpdateEmptyState();
        }

        // Section: Public surface
        internal event EventHandler<CodyPromptRequest>? PromptSubmitted;
        internal event EventHandler? StopRequested;
        internal event EventHandler? WorkspaceRequested;
        internal event EventHandler? SessionChanged;
        internal event EventHandler? PlanApproved;
        internal event EventHandler? PlanReworkRequested;
        internal event Func<string, Task>? FileOpenRequested;

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

        /// <summary>Stages short context in the composer and long context as a text attachment.</summary>
        internal async Task StageTextContextAsync(string displayName, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length <= MaximumInlineContextCharacters)
            {
                AppendContextToComposer(text);
                return;
            }

            await StageTextAttachmentAsync(displayName, text);
        }

        /// <summary>Stages text as a removable attachment, regardless of its length.</summary>
        internal async Task StageTextAttachmentAsync(string displayName, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                $"cody-context-{Guid.NewGuid():N}.txt",
                CreationCollisionOption.FailIfExists);
            await FileIO.WriteTextAsync(file, text);
            AddStagedAttachment(new ChatAttachment(
                file.Path,
                displayName,
                "text/plain",
                null,
                null,
                true,
                Guid.NewGuid(),
                ".txt"));
            FocusComposer();
        }

        internal async Task StageFileAttachmentAsync(string path, string? displayName = null)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            AddStagedAttachment(CreateAttachment(file, displayName));
            FocusComposer();
        }

        private void AppendContextToComposer(string text)
        {
            var separator = string.IsNullOrWhiteSpace(PromptBox.Text) ? string.Empty : "\r\n\r\n";
            PromptBox.Text += separator + text;
            PromptBox.SelectionStart = PromptBox.Text.Length;
            PromptBox.SelectionLength = 0;
            FocusComposer();
        }

        internal void SetBusy(bool busy)
        {
            _isBusy = busy;
            SendIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            StopIcon.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(SendButton, busy ? "Stop Cody" : "Send prompt");
            AutomationProperties.SetName(SendButton, busy ? "Stop Cody" : "Send prompt");
            if (busy)
            {
                StartProcessingStatus();
                _streamTimer.Start();
            }
            else
            {
                _streamTimer.Stop();
                FlushStreamBuffers();
                FinishProcessingStatus();
            }
            UpdateComposerAvailability();
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
            StartProcessingStatus();
        }

        /// <summary>Renders the submitted prompt immediately while the page prepares its session.</summary>
        internal void ShowPendingUserPrompt(string prompt, IReadOnlyList<ChatAttachment>? attachments = null)
        {
            DiscardPendingUserPrompt();
            _pendingUserMessage = new ChatMessage(ChatItemKind.User, "You", prompt, attachments);
            _pendingUserMessageContainer = CreateUserBubble(_pendingUserMessage);
            RemoveEmptyState();
            TranscriptHost.Children.Add(_pendingUserMessageContainer);
            MoveProcessingStatusToEnd();
            ScrollToLatest();
        }

        /// <summary>Persists the already-rendered prompt without rendering it a second time.</summary>
        internal void CommitPendingUserPrompt()
        {
            if (_pendingUserMessage is not { } message) return;

            _pendingUserMessage = null;
            _pendingUserMessageContainer = null;
            _session.Messages.Add(message);
            SessionChanged?.Invoke(this, EventArgs.Empty);
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
            FinishProcessingStatus();
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
            NoteProcessingEvent(agentEvent.Kind);
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
                MoveProcessingStatusToEnd();
                ScrollToLatest();
            }
            if (_pendingText.Length == 0) return;

            var textDelta = _pendingText.ToString();
            _pendingText.Clear();
            CommitThinking();
            _streamingAnswerText += textDelta;
            EnsureStreamingAnswer().Markdown = _streamingAnswerText;
            MoveProcessingStatusToEnd();
            ScrollToLatest();
        }

        private MarkdownView EnsureStreamingAnswer()
        {
            if (_streamingAnswer is not null) return _streamingAnswer;

            RemoveEmptyState();
            _streamingAnswer = CreateCodyMarkdown();
            var container = new Border
            {
                Padding = new Thickness(2, 2, 2, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = _streamingAnswer
            };
            _streamingAnswerContainer = container;
            TranscriptHost.Children.Add(container);
            MoveProcessingStatusToEnd();
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
            MoveProcessingStatusToEnd();
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
            MoveProcessingStatusToEnd();
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
            MoveProcessingStatusToEnd();
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Marks the start of a new session that carries a compacted summary of the previous one.</summary>
        internal void AddSessionDivider(string summary) =>
            AddMessage(new ChatMessage(ChatItemKind.Tool, SessionDividerToolName, summary, ToolSucceeded: true));

        /// <summary>Shows the actions that gate implementation after Cody has delivered a plan.</summary>
        internal void ShowPlanReview()
        {
            var prompt = new TextBlock
            {
                Text = "Review the plan before Cody makes changes.",
                FontSize = 12,
                Foreground = Resource("TextFillColorSecondaryBrush")
            };
            var approved = new Button { Content = "Approved", MinWidth = 88 };
            var rework = new Button { Content = "Rework", MinWidth = 88 };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(approved);
            actions.Children.Add(rework);
            var review = new StackPanel { Spacing = 8, Margin = new Thickness(2, 2, 2, 8) };
            review.Children.Add(prompt);
            review.Children.Add(actions);

            approved.Click += (_, _) =>
            {
                TranscriptHost.Children.Remove(review);
                PlanApproved?.Invoke(this, EventArgs.Empty);
            };
            rework.Click += (_, _) =>
            {
                TranscriptHost.Children.Remove(review);
                PlanReworkRequested?.Invoke(this, EventArgs.Empty);
            };
            TranscriptHost.Children.Add(review);
            ScrollToLatest();
        }

        internal void RenderSession()
        {
            DiscardPendingUserPrompt();
            TranscriptHost.Children.Clear();
            foreach (var message in _session.Messages) RenderMessage(message);
            MoveProcessingStatusToEnd();
            UpdateEmptyState();
            ScrollToLatest();
        }

        private void DiscardPendingUserPrompt()
        {
            if (_pendingUserMessageContainer is not null)
                TranscriptHost.Children.Remove(_pendingUserMessageContainer);
            _pendingUserMessage = null;
            _pendingUserMessageContainer = null;
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

        private static UIElement CreateUserBubble(ChatMessage message)
        {
            var content = new StackPanel { Spacing = 6 };
            if (!string.IsNullOrWhiteSpace(message.Content))
                content.Children.Add(new TextBlock
                {
                    Text = message.Content,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });
            foreach (var attachment in message.Attachments ?? [])
            {
                var attachmentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                attachmentRow.Children.Add(new FontIcon { Glyph = AttachmentGlyph(attachment), FontSize = 12 });
                attachmentRow.Children.Add(new TextBlock { Text = attachment.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
                content.Children.Add(attachmentRow);
            }
            return new Border
            {
                Padding = new Thickness(14, 10, 14, 11),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 6, 0, 2),
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Resource("AccentFillColorTertiaryBrush"),
                Child = content
            };
        }

        private UIElement CreateAssistantBlock(ChatMessage message)
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
            var markdown = CreateCodyMarkdown();
            markdown.Markdown = message.Content;
            body.Children.Add(markdown);
            return body;
        }

        private MarkdownView CreateCodyMarkdown()
        {
            var markdown = new MarkdownView();
            markdown.ConfigureCodyChat(() => _workspacePath, OpenFileRequestedAsync);
            return markdown;
        }

        private Task OpenFileRequestedAsync(string path) => FileOpenRequested?.Invoke(path) ?? Task.CompletedTask;

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

        private UIElement CreateToolBlock(ChatMessage message, string workspacePath)
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
                header.Children.Add(ToolFileArgument(message.Title, message.ToolArguments) is { } path
                    ? CreateToolFileLink(detail, path)
                    : CreateToolDetailText(detail));
            if (message.DiffOld is not null && message.DiffNew is not null)
                return CreateDisclosure(header, BuildDiffView(message.DiffOld, message.DiffNew), HorizontalAlignment.Left, hasDetails: true);
            if (message.Title == "list_workspace_entries" && BuildListView(message.Content, workspacePath) is { } listView)
                return CreateDisclosure(header, listView, HorizontalAlignment.Left, hasDetails: true);
            if (message.Title == "search_workspace_files" && BuildSearchView(message.Content, workspacePath) is { } searchView)
                return CreateDisclosure(header, searchView, HorizontalAlignment.Left, hasDetails: true);
            return CreateDisclosure(header, FormatToolOutput(message.Title, message.Content), HorizontalAlignment.Left);
        }

        private static TextBlock CreateToolDetailText(string detail) => new()
        {
            Text = detail,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Resource("TextFillColorTertiaryBrush")
        };

        private TextBlock CreateToolFileLink(string displayText, string path, double fontSize = 12)
        {
            var text = CreateToolDetailText(displayText);
            text.FontSize = fontSize;
            text.IsTextSelectionEnabled = true;
            text.Foreground = Resource("AccentTextFillColorPrimaryBrush");
            ToolTipService.SetToolTip(text, "Open file in editor");
            text.PointerPressed += async (_, args) =>
            {
                args.Handled = true;
                await OpenFileRequestedAsync(path);
            };
            return text;
        }

        private static string? ToolFileArgument(string toolName, JsonObject? arguments)
        {
            if (arguments is null || toolName is not ("read_workspace_file" or "write_workspace_file" or "patch_workspace_file"))
                return null;
            return arguments["workspace_path"]?.GetValue<string>();
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
        private UIElement? BuildListView(string output, string workspacePath)
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
                panel.Children.Add(isDirectory
                    ? new TextBlock
                    {
                        Text = normalized,
                        FontFamily = new FontFamily("Cascadia Mono"),
                        FontSize = 11,
                        IsTextSelectionEnabled = true,
                        Foreground = Resource("TextFillColorSecondaryBrush")
                    }
                    : CreateToolFileButton(normalized, path));
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
        private UIElement? BuildSearchView(string output, string workspacePath)
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
                entry.Children.Add(CreateToolFileButton(NormalizePath(filename, workspacePath), filename));
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

        private Button CreateToolFileButton(string displayText, string path)
        {
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = displayText,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Resource("AccentTextFillColorPrimaryBrush")
                },
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            ToolTipService.SetToolTip(button, "Open file in editor");
            button.Click += async (_, _) => await OpenFileRequestedAsync(path);
            return button;
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

        private static string FormatToolOutput(string toolName, string output)
        {
            try
            {
                if (JsonNode.Parse(output) is not JsonObject result) return output;
                if (ReadResultText(result, "error") is { } error)
                {
                    var suggestion = ReadResultText(result, "suggestion");
                    return suggestion is null ? error : $"{error}\n\nSuggestion: {suggestion}";
                }

                if (toolName == "read_workspace_file" && ReadResultText(result, "content") is { } fileContent)
                    return fileContent + (result["is_truncated"]?.GetValue<bool>() == true ? "\n\n[Read result truncated]" : string.Empty);

                if (toolName is "run_workspace_command" or "run_elevated_workspace_command")
                    return FormatCommandOutput(result);

                if (ReadResultText(result, "content") is { } content) return content;
                if (ReadResultText(result, "answer") is { } answer) return answer;
                return FormatResultFields(result);
            }
            catch (JsonException)
            {
                return output;
            }
        }

        private static string? ReadResultText(JsonObject result, string name) =>
            result[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static string FormatCommandOutput(JsonObject result)
        {
            var sections = new List<string>();
            if (ReadResultText(result, "stdout") is { Length: > 0 } stdout) sections.Add(stdout);
            if (ReadResultText(result, "stderr") is { Length: > 0 } stderr) sections.Add($"stderr:\n{stderr}");
            if (result["return_code"] is JsonValue code && code.TryGetValue<int>(out var returnCode))
                sections.Add($"Exit code: {returnCode}");
            if (result["truncated"]?.GetValue<bool>() == true) sections.Add("[Output truncated]");
            return sections.Count == 0 ? "Command completed with no output." : string.Join("\n\n", sections);
        }

        private static string FormatResultFields(JsonObject result)
        {
            var fields = result
                .Where(field => field.Key is not "success" and not "is_truncated" and not "truncated")
                .Select(field => $"{HumanizeFieldName(field.Key)}: {DescribeResultValue(field.Value)}")
                .ToList();
            if (result["is_truncated"]?.GetValue<bool>() == true) fields.Add("[Results truncated]");
            return fields.Count == 0 ? "Completed." : string.Join("\n", fields);
        }

        private static string HumanizeFieldName(string name) => name.Replace('_', ' ');

        private static string DescribeResultValue(JsonNode? value) => value switch
        {
            null => "none",
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
            JsonValue scalar => scalar.ToJsonString(),
            JsonArray array => $"{array.Count} item{(array.Count == 1 ? string.Empty : "s")}",
            JsonObject obj => $"{obj.Count} field{(obj.Count == 1 ? string.Empty : "s")}",
            _ => value.ToJsonString()
        };

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

        private void PromptBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposerAvailability();

        /// <summary>Sends the composer text and its staged attachments together.</summary>
        internal void SubmitPrompt(string prompt)
        {
            var text = prompt.Trim();
            if ((text.Length == 0 && _stagedAttachments.Count == 0) || _isBusy) return;

            PromptBox.Text = string.Empty;
            var attachments = _stagedAttachments.ToArray();
            _stagedAttachments.Clear();
            RenderStagedAttachments();
            PromptSubmitted?.Invoke(this, new CodyPromptRequest(text, attachments));
        }

        private async void PromptBox_Paste(object sender, TextControlPasteEventArgs e)
        {
            if (_isBusy) return;
            StorageFile? temporaryImage = null;
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.StorageItems))
                {
                    e.Handled = true;
                    var files = (await content.GetStorageItemsAsync()).OfType<StorageFile>();
                    foreach (var file in files)
                        AddStagedAttachment(CreateAttachment(file));
                    return;
                }
                if (content.Contains(StandardDataFormats.Bitmap))
                {
                    e.Handled = true;
                    var bitmap = await content.GetBitmapAsync();
                    temporaryImage = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                        $"cody-clipboard-{Guid.NewGuid():N}.png",
                        CreationCollisionOption.FailIfExists);
                    using var input = await bitmap.OpenReadAsync();
                    var decoder = await BitmapDecoder.CreateAsync(input);
                    using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                    using var output = await temporaryImage.OpenAsync(FileAccessMode.ReadWrite);
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                    encoder.SetSoftwareBitmap(softwareBitmap);
                    await encoder.FlushAsync();
                    AddStagedAttachment(CreateAttachment(temporaryImage, "Clipboard image", true));
                    temporaryImage = null;
                    return;
                }
                if (content.Contains(StandardDataFormats.Text))
                {
                    e.Handled = true;
                    var text = await content.GetTextAsync();
                    if (text.Length > MaximumInlineContextCharacters)
                        await StageTextContextAsync("Clipboard text", text);
                    else
                        PromptBox.SelectedText = text;
                }
            }
            catch
            {
                if (temporaryImage is not null)
                    try { await temporaryImage.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
            }
        }

        private static ChatAttachment CreateAttachment(StorageFile file, string? displayName = null, bool temporary = false) => new(
            file.Path,
            displayName ?? file.Name,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            null,
            null,
            temporary,
            Guid.NewGuid(),
            file.FileType);

        private void AddStagedAttachment(ChatAttachment attachment)
        {
            _stagedAttachments.Add(attachment);
            RenderStagedAttachments();
            UpdateComposerAvailability();
        }

        private void RenderStagedAttachments()
        {
            AttachmentHost.Children.Clear();
            AttachmentHost.Visibility = _stagedAttachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var attachment in _stagedAttachments)
            {
                var item = attachment;
                var chip = new Border
                {
                    Padding = new Thickness(6, 2, 4, 2),
                    Margin = new Thickness(0, 0, 6, 4),
                    CornerRadius = new CornerRadius(7),
                    Background = Resource("ControlFillColorSecondaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var content = new Grid { ColumnSpacing = 4 };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var reference = new Button
                {
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0
                };
                ToolTipService.SetToolTip(reference, "Insert attachment reference");
                var referenceContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                referenceContent.Children.Add(new FontIcon { Glyph = AttachmentGlyph(item), FontSize = 11 });
                referenceContent.Children.Add(new TextBlock
                {
                    Text = item.DisplayName,
                    MaxWidth = 240,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                reference.Content = referenceContent;
                reference.Click += (_, _) => InsertAttachmentReference(item.DisplayName);
                content.Children.Add(reference);
                var trashIcon = new SymbolIcon
                {
                    Symbol = Symbol.Delete,
                    Foreground = Resource("TextFillColorTertiaryBrush")
                };
                var trashIconView = new Viewbox { Width = 11, Height = 11, Child = trashIcon };
                var remove = new Button
                {
                    Content = trashIconView,
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };
                var transparent = new SolidColorBrush(Colors.Transparent);
                remove.Resources["ButtonBackgroundPointerOver"] = transparent;
                remove.Resources["ButtonBorderBrushPointerOver"] = transparent;
                remove.Resources["ButtonBackgroundPressed"] = transparent;
                remove.Resources["ButtonBorderBrushPressed"] = transparent;
                Grid.SetColumn(remove, 1);
                ToolTipService.SetToolTip(remove, $"Remove {item.DisplayName}");
                remove.Click += async (_, _) => await RemoveStagedAttachmentAsync(item);
                remove.PointerEntered += (_, _) => trashIcon.Foreground = Resource("SystemFillColorCriticalBrush");
                remove.PointerExited += (_, _) => trashIcon.Foreground = Resource("TextFillColorTertiaryBrush");
                content.Children.Add(remove);
                chip.Child = content;
                VariableSizedWrapGrid.SetColumnSpan(chip, AttachmentChipColumnSpan(item.DisplayName));
                AttachmentHost.Children.Add(chip);
            }
        }

        private static int AttachmentChipColumnSpan(string displayName) =>
            Math.Clamp((int)Math.Ceiling((displayName.Length * 6.2 + 44) / 8), 9, 36);

        private void InsertAttachmentReference(string displayName)
        {
            var selectionStart = PromptBox.SelectionStart;
            var prefix = selectionStart > 0 && !char.IsWhiteSpace(PromptBox.Text[selectionStart - 1]) ? " " : string.Empty;
            var insertion = prefix + $"[{displayName}] ";
            PromptBox.SelectedText = insertion;
            PromptBox.SelectionStart = selectionStart + insertion.Length;
            PromptBox.SelectionLength = 0;
            FocusComposer();
        }

        private async Task RemoveStagedAttachmentAsync(ChatAttachment attachment)
        {
            if (!_stagedAttachments.Remove(attachment)) return;
            if (attachment.IsTemporary)
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(attachment.LocalPath);
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                catch (FileNotFoundException) { }
            RenderStagedAttachments();
            UpdateComposerAvailability();
        }

        private static string AttachmentGlyph(ChatAttachment attachment) =>
            attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "\uE8B9" :
            attachment.IsTemporary && attachment.MimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ? "\uE7C3" :
            "\uE8A5";

        private void UpdateComposerAvailability()
        {
            var hasWorkspace = !string.IsNullOrWhiteSpace(_workspacePath);
            PromptBox.IsEnabled = hasWorkspace && !_isBusy;
            SendButton.IsEnabled = _isBusy || (hasWorkspace && (!string.IsNullOrWhiteSpace(PromptBox.Text) || _stagedAttachments.Count > 0));
        }

        private void ChooseWorkspaceButton_Click(object sender, RoutedEventArgs e) =>
            WorkspaceRequested?.Invoke(this, EventArgs.Empty);

        private void UpdateEmptyState()
        {
            var hasWorkspace = !string.IsNullOrWhiteSpace(_workspacePath);
            UpdateComposerAvailability();
            EmptyStateDetailText.Text = hasWorkspace
                ? "Ask Cody to inspect, change, or run your project."
                : "Choose a workspace so Cody can read and change your project files.";
            ChooseWorkspaceButton.Visibility = hasWorkspace ? Visibility.Collapsed : Visibility.Visible;
            if (_session.Messages.Count == 0 && !TranscriptHost.Children.Contains(EmptyState))
                TranscriptHost.Children.Insert(0, EmptyState);
        }

        private void RemoveEmptyState() => TranscriptHost.Children.Remove(EmptyState);

        // Section: Processing status
        // This row deliberately never becomes a ChatMessage: it is live UI feedback, not conversation history.
        private void StartProcessingStatus()
        {
            _processingStatus ??= new ProcessingStatusRow();
            RemoveEmptyState();
            _processingStatus.SetEvent(ProcessingEvent.Processing);
            MoveProcessingStatusToEnd();
            ScrollToLatest();
        }

        private void NoteProcessingEvent(CodyAgentEventKind eventKind)
        {
            if (!_isBusy) return;

            _processingStatus ??= new ProcessingStatusRow();
            _processingStatus.SetEvent(eventKind switch
            {
                CodyAgentEventKind.ThinkingDelta => ProcessingEvent.Thinking,
                CodyAgentEventKind.TextDelta => ProcessingEvent.Responding,
                CodyAgentEventKind.ToolStarted => ProcessingEvent.ToolStarting,
                CodyAgentEventKind.ToolCompleted => ProcessingEvent.ToolFinished,
                CodyAgentEventKind.Notice => ProcessingEvent.Notice,
                _ => ProcessingEvent.Processing
            });
            MoveProcessingStatusToEnd();
            ScrollToLatest();
        }

        private void RefreshProcessingStatus()
        {
            if (_processingStatus is null) return;
            _processingStatus.Refresh();
            MoveProcessingStatusToEnd();
        }

        /// <summary>Keeps the ephemeral processing card below streamed text, thinking, and tool rows.</summary>
        private void MoveProcessingStatusToEnd()
        {
            if (_processingStatus is null) return;
            if (TranscriptHost.Children.Count > 0
                && ReferenceEquals(TranscriptHost.Children[TranscriptHost.Children.Count - 1], _processingStatus.Container))
                return;
            TranscriptHost.Children.Remove(_processingStatus.Container);
            TranscriptHost.Children.Add(_processingStatus.Container);
        }

        private void FinishProcessingStatus()
        {
            if (_processingStatus is null) return;
            TranscriptHost.Children.Remove(_processingStatus.Container);
            _processingStatus = null;
        }

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
        private enum ProcessingEvent { Processing, Thinking, Responding, ToolStarting, ToolFinished, Notice }

        /// <summary>
        /// A rotating, non-persisted status card. The word banks yield more than 64,000 distinct
        /// sentences for every event type without carrying a 5,000-line string table in memory.
        /// </summary>
        private sealed class ProcessingStatusRow
        {
            private static readonly string[] EventOpeners =
            [
                "Herding cats", "Chasing the red dot", "Knocking things off the desk",
                "Judging you silently", "Stuck halfway into a box", "Ignoring you on purpose",
                "Plotting world domination", "Staring at the wall intensely"
            ];
            private static readonly string[] ThinkingOpeners =
            [
                "Staring blankly into space", "Contemplating the void",
                "Doing math in its head (a cat)", "Deep in a thousand-yard stare",
                "Loading brain.exe", "Overthinking a nap decision",
                "Calculating the perfect pounce angle", "Having a very important thought"
            ];
            private static readonly string[] RespondingOpeners =
            [
                "Coughing up a hairball of words", "Meowing until it makes sense",
                "Kneading the keyboard", "Purring out an answer",
                "Typing with paw beans", "Composing a very important meow",
                "Translating cat to human", "Winding up to speak"
            ];
            private static readonly string[] ToolStartingOpeners =
            [
                "Sharpening the claws", "Sneaking up on the mouse",
                "Batting at a loose thread", "Prepping the ambush",
                "Sniffing around suspiciously", "Testing if the box still fits",
                "Winding up for a zoomie", "Eyeing the tool like prey"
            ];
            private static readonly string[] ToolFinishedOpeners =
            [
                "Brought back a dead bug (proudly)", "Knocked it off the desk successfully",
                "Caught the red dot, finally", "Trotting back with the loot",
                "Left a paw print on it", "Delivered, no cap, just cat",
                "Nailed the landing (mostly)", "Dropped it at your feet"
            ];
            private static readonly string[] NoticeOpeners =
            [
                "Knocked a glass off the table", "Meowed at the door for no reason",
                "Left a hairball as a gift", "Sat on the important paper",
                "Yelled at 3am for attention", "Stared at nothing near the ceiling",
                "Demanded snacks immediately", "Zoomied through the room"
            ];
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private readonly TextBlock _message;
            private ProcessingEvent _event;
            private string _currentSentence = "";

            public ProcessingStatusRow()
            {
                _message = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 2),
                    Foreground = Resource("TextFillColorTertiaryBrush"),
                    RenderTransform = new TranslateTransform(),
                    Opacity = 0
                };
                Container = _message;
                SetEvent(ProcessingEvent.Processing);
            }

            private void AnimateNewSentence()
            {
                var transform = (TranslateTransform)_message.RenderTransform;
                transform.Y = 0;
                _message.Opacity = 0;

                var storyboard = new Storyboard();
                var fadeIn = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fadeIn, _message);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");

                var slideUp = new DoubleAnimation
                {
                    From = 6,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slideUp, transform);
                Storyboard.SetTargetProperty(slideUp, "Y");

                storyboard.Children.Add(fadeIn);
                storyboard.Children.Add(slideUp);
                storyboard.Begin();
            }

            public FrameworkElement Container { get; }

            public void SetEvent(ProcessingEvent processingEvent)
            {
                _event = processingEvent;
                Refresh(forceNewMessage: true);
            }

            public void Refresh() => Refresh(forceNewMessage: false);

            private void Refresh(bool forceNewMessage)
            {
                var sentenceChanged = forceNewMessage || string.IsNullOrEmpty(_currentSentence);
                if (sentenceChanged)
                    _currentSentence = CreateFunSentence(_event);

                var elapsed = TimeSpan.FromSeconds(Math.Max(0, (int)_stopwatch.Elapsed.TotalSeconds));
                var elapsedText = elapsed.TotalMinutes >= 1
                    ? $"{(int)elapsed.TotalMinutes}m"
                    : $"{elapsed.Seconds}s";
                _message.Text = $"{elapsedText} - {_currentSentence}";

                if (sentenceChanged)
                    AnimateNewSentence();
            }

            private static string CreateFunSentence(ProcessingEvent processingEvent)
            {
                var openers = OpenersFor(processingEvent);
                return openers[Random.Shared.Next(openers.Length)];
            }

            private static string[] OpenersFor(ProcessingEvent processingEvent) => processingEvent switch
            {
                ProcessingEvent.Thinking => ThinkingOpeners,
                ProcessingEvent.Responding => RespondingOpeners,
                ProcessingEvent.ToolStarting => ToolStartingOpeners,
                ProcessingEvent.ToolFinished => ToolFinishedOpeners,
                ProcessingEvent.Notice => NoticeOpeners,
                _ => EventOpeners
            };
        }

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
