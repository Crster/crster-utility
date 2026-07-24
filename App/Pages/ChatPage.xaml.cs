using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using App.Controls;
using App.Models;
using App.Services;
using App.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class ChatPage : Page
    {
        private const int MaximumToolRounds = 15;
        private static readonly string[] AllowedContextAttachmentExtensions =
        [
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp",
            ".mp3", ".mp4", ".txt", ".log", ".ini", ".json", ".conf", ".env", ".csv",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx"
        ];
        private static readonly Regex LocalPathPattern = new("(?<!\\w)(?:[A-Za-z]:\\\\[^\\r\\n\\\"'<>|*?]+|\\\\\\\\[^\\s\\\"'<>|*?]+)", RegexOptions.Compiled);
        private readonly SecureSettingsService _settingsService = new();
        private readonly ChatToolService _tools = new();
        private readonly ChatLogService _chatLog = new();
        private readonly Dictionary<ChatPersonality, ChatSession> _sessions = Enum.GetValues<ChatPersonality>().ToDictionary(item => item, _ => new ChatSession());
        private AppSettings _settings = new();
        private GeminiClient? _client;
        private CancellationTokenSource? _operationCancellation;
        private ChatPersonality _personality = ChatPersonality.Smart;
        private bool _loaded;
        private bool _isBusy;
        private bool _renderingContext;
        private bool _changingPersonality;

        public ChatPage()
        {
            InitializeComponent();
            Loaded += ChatPage_Loaded;
            Unloaded += ChatPage_Unloaded;
        }

        private ChatSession Session => _sessions[_personality];

        private async void ChatPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;
            _settings = await _settingsService.LoadAsync();
            if (!Enum.TryParse(_settings.LastChatPersonality, true, out _personality)) _personality = ChatPersonality.Smart;
            PersonalityBox.ItemsSource = Enum.GetValues<ChatPersonality>();
            PersonalityBox.SelectedItem = _personality;
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey) && !await RequestApiKeyAsync()) { StatusText.Text = "A Gemini API key is required."; return; }
            _client = new GeminiClient(_settings.GeminiApiKey);
            RenderSession();
        }

        private async Task<bool> RequestApiKeyAsync()
        {
            var input = new PasswordBox { Header = "Gemini API key", PlaceholderText = "Paste your key", MinWidth = 380 };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Connect Gemini", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Password)) return false;
            _settings.GeminiApiKey = input.Password.Trim(); await _settingsService.SaveAsync(_settings); return true;
        }

        private async Task SendAsync()
        {
            if (_operationCancellation is not null) return;
            if (_client is null) throw new InvalidOperationException("Chat is not connected. Add a Gemini API key and reopen the Chat page.");
            var prompt = ComposerBox.Text.Trim();
            if (prompt.Length == 0) return;
            var attachments = Session.Attachments.ToList();
            await _chatLog.WriteAsync("send.started",
                ("personality", _personality),
                ("model", Model()),
                ("promptLength", prompt.Length),
                ("attachmentCount", attachments.Count),
                ("historyStepCount", Session.History.Count));
            ComposerBox.Text = string.Empty;
            DiscoverPathContext(prompt, attachments);
            AddMessage(new ChatMessage(ChatItemKind.User, "You", prompt));
            var userStep = _personality == ChatPersonality.Artist ? CreateArtistUserStep(prompt, attachments) : GeminiClient.CreateUserStep(prompt, attachments);
            await RunInteractionAsync(userStep);
        }

        private JsonObject CreateArtistUserStep(string prompt, IReadOnlyList<ChatAttachment> attachments)
        {
            if (string.IsNullOrWhiteSpace(Session.OriginalArtistPrompt)) Session.OriginalArtistPrompt = prompt;
            else Session.ArtistEditSummary = string.IsNullOrWhiteSpace(Session.ArtistEditSummary) ? prompt : $"{Session.ArtistEditSummary}\n- {prompt}";
            var context = $"Original creative brief:\n{Session.OriginalArtistPrompt}\n\nEdit history summary:\n{(string.IsNullOrWhiteSpace(Session.ArtistEditSummary) ? "No prior edits." : Session.ArtistEditSummary)}\n\nCurrent request:\n{prompt}";
            return GeminiClient.CreateArtistStep(context, attachments, Session.GeneratedImages.TakeLast(2));
        }

        private async Task RunInteractionAsync(JsonObject initialStep)
        {
            _operationCancellation = new CancellationTokenSource(); SetBusy(true, $"{_personality} is working...");
            try
            {
                IReadOnlyList<JsonObject> nextSteps = [initialStep];
                for (var round = 0; round < MaximumToolRounds; round++)
                {
                    var tools = GetTools();
                    var requestTimer = Stopwatch.StartNew();
                    await _chatLog.WriteAsync("request.started",
                        ("personality", _personality),
                        ("model", Model()),
                        ("round", round + 1),
                        ("inputStepCount", nextSteps.Count),
                        ("historyStepCount", Session.History.Count),
                        ("toolCount", tools?.Count ?? 0));
                    GeminiTurnResult result = _personality == ChatPersonality.Artist
                        ? await _client!.CreateArtistInteractionAsync(Session.History, nextSteps[0], EffectiveSystemInstruction(), ThinkingLevel(), _operationCancellation.Token)
                        : await _client!.CreateSimpleInteractionAsync(Model(), Session.History, nextSteps, EffectiveSystemInstruction(), tools, ThinkingLevel(), _operationCancellation.Token);
                    requestTimer.Stop();
                    await _chatLog.WriteAsync("request.completed",
                        ("personality", _personality),
                        ("model", Model()),
                        ("round", round + 1),
                        ("elapsedMs", requestTimer.ElapsedMilliseconds),
                        ("responseStepCount", result.Steps.Count),
                        ("textLength", result.Text.Length),
                        ("functionCallCount", result.FunctionCalls.Count),
                        ("sourceCount", result.Sources.Count),
                        ("hasImage", result.Image is not null),
                        ("interactionId", result.InteractionId));
                    foreach (var nextStep in nextSteps) Session.History.Add(CreateHistoryStep(nextStep));
                    foreach (var step in result.Steps) Session.History.Add(step);
                    if (!string.IsNullOrWhiteSpace(result.Text)) AddMessage(new ChatMessage(ChatItemKind.Assistant, _personality.ToString(), result.Text));
                    if (result.Image is not null) { Session.GeneratedImages.Add(result.Image); AddMessage(new ChatMessage(ChatItemKind.Assistant, "Artist", "", Image: result.Image)); }
                    if (result.Sources.Count > 0) AddMessage(new ChatMessage(ChatItemKind.Assistant, "Sources", string.Join("\n", result.Sources.DistinctBy(source => source.Uri).Select(source => $"- [{source.Title}]({source.Uri})"))));
                    if (string.IsNullOrWhiteSpace(result.Text) && result.Image is null && result.FunctionCalls.Count == 0)
                        throw new InvalidOperationException("Gemini completed the request without returning a response.");
                    if (_personality != ChatPersonality.Technician || result.FunctionCalls.Count == 0) return;
                    var responses = new List<JsonObject>();
                    foreach (var call in result.FunctionCalls)
                    {
                        var policy = ChatToolService.ApprovalPolicy(call.Name, call.Arguments);
                        var approved = policy is ToolApprovalPolicy.None or ToolApprovalPolicy.ManualScreenSelection || await ConfirmToolAsync(call, policy);
                        var toolResult = !approved
                            ? new ToolResult(false, "{\"status\":\"cancelled_by_user\",\"summary\":\"The user denied this operation; no change was made.\"}", "cancelled_by_user")
                            : call.Name == "screen_capture"
                                ? await CaptureScreenRegionAsync(call)
                                : await _tools.ExecuteAsync(call.Name, call.Arguments, _operationCancellation.Token);
                        AddMessage(new ChatMessage(ChatItemKind.Tool, call.Name, toolResult.Output, Image: toolResult.Image));
                        responses.Add(GeminiClient.CreateFunctionResult(call, toolResult));
                    }
                    nextSteps = responses;
                }
                AddMessage(new ChatMessage(ChatItemKind.Error, "Tool limit reached", "Technician exceeded the maximum number of tool rounds."));
            }
            catch (OperationCanceledException)
            {
                await _chatLog.WriteAsync("send.cancelled", ("personality", _personality), ("model", Model()));
                StatusText.Text = "Stopped";
            }
            catch (Exception exception)
            {
                await _chatLog.WriteAsync("send.failed",
                    ("personality", _personality),
                    ("model", Model()),
                    ("exceptionType", exception.GetType().Name),
                    ("message", exception.Message));
                AddMessage(new ChatMessage(ChatItemKind.Error, "Gemini error", exception.Message));
            }
            finally
            {
                _operationCancellation.Dispose();
                _operationCancellation = null;
                SetBusy(false);
                await _chatLog.WriteAsync("send.finished", ("personality", _personality), ("model", Model()));
            }
        }

        private static JsonObject CreateHistoryStep(JsonObject step)
        {
            var historyStep = (JsonObject)step.DeepClone();
            if (historyStep["content"] is not JsonArray content) return historyStep;
            foreach (var item in content.Where(item => item?["uri"] is not null).ToList()) content.Remove(item);
            return historyStep;
        }

        private JsonArray? GetTools()
        {
            if (_personality == ChatPersonality.Technician) return ChatToolService.CreateDeclarations();
            return _personality switch
            {
                ChatPersonality.Study => new JsonArray { new JsonObject { ["type"] = "google_search" }, new JsonObject { ["type"] = "url_context" } },
                ChatPersonality.Smart or ChatPersonality.Planner => new JsonArray { new JsonObject { ["type"] = "url_context" } },
                _ => null
            };
        }

        private string Model() => _personality switch
        {
            ChatPersonality.Smart => "gemini-2.5-flash-lite",
            ChatPersonality.Technician => "gemini-3-flash-preview",
            ChatPersonality.Planner => "gemini-3-flash-preview",
            ChatPersonality.Study => "gemini-3.5-flash",
            _ => "gemini-3.1-flash-image"
        };

        private string? ThinkingLevel() => _personality switch
        {
            ChatPersonality.Technician or ChatPersonality.Planner => "high",
            ChatPersonality.Artist => "low",
            _ => null
        };

        private string SystemInstruction() => _personality switch
        {
            ChatPersonality.Smart => "You are Smart. Give simple, concise, friendly answers. Summarize supplied context clearly. Do not call tools.",
            ChatPersonality.Technician => TechnicianInstruction(),
            ChatPersonality.Artist => "You are Artist, a professional graphic artist. Create polished visuals and honor the original brief and edit history. Preserve all requested elements unless the current edit explicitly changes them.",
            ChatPersonality.Planner => "You are Planner. Before providing a plan, ask a comprehensive Markdown requirements checklist. Gather product goal, users, features, platform, language/stack, data, security, edge cases, accessibility, testing, deployment, operations, future growth, and relevant monetization. After the user answers enough questions, return a detailed decision-complete Markdown plan.",
            ChatPersonality.Study => "You are Study, a knowledgeable research writer. Use Google Search grounding and produce a detailed wiki-style Markdown article with clear sections and inline citations. Cover only relevant topics, including background, concepts, applications, current state, limitations, and related topics.",
            _ => string.Empty
        };

        private string EffectiveSystemInstruction()
        {
            var instruction = SystemInstruction();
            return string.IsNullOrWhiteSpace(Session.ContextText)
                ? instruction
                : $"{instruction}\n\nConversation context supplied by the user:\n{Session.ContextText.Trim()}";
        }

        private static string TechnicianInstruction() =>
            """
            You are Technician, a Windows support and automation agent. You can inspect and operate across the PC. Diagnose before changing state, prefer dedicated tools over PowerShell, and use non-elevated PowerShell unless administration is necessary.

            For execute_command_shell, write_file, delete_file, and kill_process, accurately set risk_level to safe or risky. Never mark an operation safe merely to avoid approval. Destructive shell commands include deletion, overwrite, network resets, clearing logs, changing services, permissions, synchronization, installation, removal, or other persistent system changes. Writes to sensitive system or application files and terminating important processes are risky. For risky operations, give a concise approval_reason and destructive_effect.

            execute_command_shell_admin and delete_directory always need a specific approval_reason and destructive_effect. Directory deletion is permanent. Request screen_capture only when visual information is materially useful, state the purpose, and tell the user what area to select. Environment-variable values may contain credentials; do not repeat secrets in prose unless directly necessary.

            Inspect before acting, use hashes rather than filenames alone for duplicates, preserve at least one copy, preserve originals before repair when practical, and verify every change. Never claim success because a command merely started.

            After every diagnostic or automation workflow, finish with a concise result explanation: what was attempted; what was inspected or changed; whether it succeeded, partially succeeded, failed, or was cancelled; verification evidence; affected files, processes, settings, or components; destructive effects; and any restart, sign-out, reconnection, or manual follow-up. For diagnostics, summarize findings and the recommended next action.
            """;

        private async Task<bool> ConfirmToolAsync(GeminiFunctionCall call, ToolApprovalPolicy policy)
        {
            var reason = call.Arguments["approval_reason"]?.GetValue<string>();
            var effect = call.Arguments["destructive_effect"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(effect))
            {
                AddMessage(new ChatMessage(ChatItemKind.Error, call.Name, "Technician did not provide the required approval reason and destructive effect."));
                return false;
            }
            var target = ChatToolService.ApprovalTarget(call.Name, call.Arguments);
            var warning = call.Name == "delete_directory" ? "\n\nThis deletion is permanent and will not use the Recycle Bin." : string.Empty;
            var elevation = policy == ToolApprovalPolicy.AlwaysWithUac ? "\n\nWindows UAC will also request administrator permission." : string.Empty;
            var content = new StackPanel { Spacing = 10, MinWidth = 500 };
            content.Children.Add(new TextBlock { Text = $"Operation\n{call.Name}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = $"Target or command\n{target}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            content.Children.Add(new TextBlock { Text = $"Why Technician needs it\n{reason}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"What can happen if approved\n{effect}{warning}{elevation}", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Allow this operation?", Content = content, PrimaryButtonText = "Allow", CloseButtonText = "Deny", DefaultButton = ContentDialogButton.Close };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async Task<ToolResult> CaptureScreenRegionAsync(GeminiFunctionCall call)
        {
            if (App.MainWindow is not MainWindow mainWindow)
                return new ToolResult(false, "{\"status\":\"failed\",\"summary\":\"The main window is unavailable.\"}");

            var purpose = call.Arguments["purpose"]?.GetValue<string>() ?? "Technician needs visual information.";
            var notice = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Select a screen area",
                Content = new TextBlock { Text = $"{purpose}\n\nAfter continuing, drag over the exact area Technician may inspect. Cancel the selector to share nothing.", TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await notice.ShowAsync() != ContentDialogResult.Primary)
                return new ToolResult(false, "{\"status\":\"cancelled_by_user\",\"summary\":\"The user cancelled screen capture; no image was shared.\"}", "cancelled_by_user");

            mainWindow.HideToTray();
            await Task.Delay(150, _operationCancellation!.Token);
            var snapshot = await ScreenCaptureService.CaptureAsync(call.Arguments["include_cursor"]?.GetValue<bool>() ?? true);
            if (snapshot is null)
            {
                mainWindow.ShowFromTray();
                return new ToolResult(false, "{\"status\":\"failed\",\"summary\":\"The desktop could not be captured.\"}");
            }

            var completion = new TaskCompletionSource<GeneratedImage?>();
            var editor = new EditSnapshotWindow(snapshot);
            editor.ImageSaved += (_, saved) => completion.TrySetResult(new GeneratedImage(saved.Data, "image/png"));
            editor.Closed += (_, _) => completion.TrySetResult(null);
            editor.Activate();
            var image = await completion.Task;
            snapshot.Dispose();
            mainWindow.ShowFromTray();
            return image is null
                ? new ToolResult(false, "{\"status\":\"cancelled_by_user\",\"summary\":\"The user cancelled region selection; no image was shared.\"}", "cancelled_by_user")
                : new ToolResult(true, $"{{\"status\":\"completed\",\"summary\":\"The user selected a screen region for: {JsonEscape(purpose)}\"}}", Image: image);
        }

        private static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

        private void ContextButton_Click(object sender, RoutedEventArgs e)
        {
            ContextPanel.Visibility = ContextPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(ContextButton, ContextPanel.Visibility == Visibility.Visible ? "Hide conversation context" : "Show conversation context");
        }

        private async void AddContextFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_client is null || App.MainWindow is null || _operationCancellation is not null) return;
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var files = await picker.PickMultipleFilesAsync(); if (files.Count == 0) return;
            SetBusy(true, "Uploading files...");
            try
            {
                foreach (var file in files)
                    await UploadContextFileAsync(file, file.Path, file.Name);
                RefreshContext();
            }
            catch (Exception exception) { AddMessage(new ChatMessage(ChatItemKind.Error, "Upload error", exception.Message)); }
            finally { SetBusy(false); }
        }

        private async void AddContextClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_client is null || _operationCancellation is not null) return;
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                AddMessage(new ChatMessage(ChatItemKind.Error, "Clipboard", "The clipboard does not contain an image."));
                return;
            }

            StorageFile? clipboardImage = null;
            SetBusy(true, "Uploading clipboard image...");
            try
            {
                var bitmapReference = await content.GetBitmapAsync();
                clipboardImage = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    $"{Guid.NewGuid():N}.png",
                    CreationCollisionOption.FailIfExists);
                using (var input = await bitmapReference.OpenReadAsync())
                using (var output = await clipboardImage.OpenAsync(FileAccessMode.ReadWrite))
                {
                    await RandomAccessStream.CopyAsync(input, output);
                    await output.FlushAsync();
                }

                var displayName = $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
                await UploadContextFileAsync(clipboardImage, string.Empty, displayName);
                RefreshContext();
            }
            catch (Exception exception) { AddMessage(new ChatMessage(ChatItemKind.Error, "Clipboard upload error", exception.Message)); }
            finally
            {
                if (clipboardImage is not null) await clipboardImage.DeleteAsync(StorageDeleteOption.PermanentDelete);
                SetBusy(false);
            }
        }

        private async Task UploadContextFileAsync(StorageFile file, string localPath, string displayName)
        {
            StorageFile? convertedImage = null;
            StorageFile? metadataFile = null;
            var uploadPath = file.Path;
            try
            {
                if (!IsDirectlyAttachableFile(file))
                {
                    metadataFile = await CreateFileMetadataAttachmentAsync(file);
                    uploadPath = metadataFile.Path;
                    displayName = $"{file.Name}.txt";
                }
                else if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    convertedImage = await ConvertAttachmentImageToJpegAsync(file);
                    uploadPath = convertedImage.Path;
                    displayName = $"{Path.GetFileNameWithoutExtension(displayName)}.jpg";
                }

                var uploadedAttachment = await _client!.UploadFileAsync(uploadPath, CancellationToken.None);
                var attachment = uploadedAttachment with { LocalPath = localPath, DisplayName = displayName };
                Session.Attachments.Add(attachment);
                DiscoverPathContext(string.Empty, [attachment]);
            }
            finally
            {
                if (convertedImage is not null) await convertedImage.DeleteAsync(StorageDeleteOption.PermanentDelete);
                if (metadataFile is not null) await metadataFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
        }

        private static bool IsDirectlyAttachableFile(StorageFile file) =>
            AllowedContextAttachmentExtensions.Contains(file.FileType, StringComparer.OrdinalIgnoreCase)
            || file.Name.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || file.Name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase);

        private static async Task<StorageFile> CreateFileMetadataAttachmentAsync(StorageFile source)
        {
            var metadataFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                $"{Guid.NewGuid():N}.txt",
                CreationCollisionOption.FailIfExists);
            try
            {
                var properties = await source.GetBasicPropertiesAsync();
                var mimeType = string.IsNullOrWhiteSpace(source.ContentType) ? "application/octet-stream" : source.ContentType;
                var metadata = $"Full path: {source.Path}\r\nSize: {properties.Size} bytes\r\nMIME type: {mimeType}";
                await FileIO.WriteTextAsync(metadataFile, metadata);
                return metadataFile;
            }
            catch
            {
                await metadataFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                throw;
            }
        }

        private static async Task<StorageFile> ConvertAttachmentImageToJpegAsync(StorageFile source)
        {
            var output = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                $"{Guid.NewGuid():N}.jpg",
                CreationCollisionOption.FailIfExists);
            try
            {
                using var inputStream = await source.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(inputStream);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                using var outputStream = await output.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                return output;
            }
            catch
            {
                await output.DeleteAsync(StorageDeleteOption.PermanentDelete);
                throw;
            }
        }

        private void DiscoverPathContext(string prompt, IEnumerable<ChatAttachment> attachments)
        {
            if (_personality != ChatPersonality.Technician) return;
            var candidates = LocalPathPattern.Matches(prompt).Select(match => match.Value.Trim())
                .Concat(attachments.Select(item => item.LocalPath).Where(path => !string.IsNullOrWhiteSpace(path)));
            foreach (var candidate in candidates)
            {
                try { var fullPath = Path.GetFullPath(candidate); if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) continue; Session.PathContext = fullPath; Session.HasPathContext = true; _tools.DefaultPath = fullPath; StatusText.Text = $"Local tools enabled for {fullPath}"; return; } catch { }
            }
        }

        private async void CompactButton_Click(object sender, RoutedEventArgs e) => await CompactConversationAsync();

        private async Task CompactConversationAsync()
        {
            if (_client is null || _operationCancellation is not null || Session.Messages.Count == 0) return;

            _operationCancellation = new CancellationTokenSource();
            SetBusy(true, "Compacting conversation...");
            try
            {
                var existingContext = string.IsNullOrWhiteSpace(Session.ContextText)
                    ? "No existing conversation context."
                    : $"Existing conversation context:\n{Session.ContextText.Trim()}";
                var transcript = string.Join(
                    "\n\n",
                    Session.Messages
                        .Where(message => message.Kind != ChatItemKind.Error)
                        .Select(message => $"{message.Title}:\n{(string.IsNullOrWhiteSpace(message.Content) ? "[Generated image]" : message.Content)}"));
                var request = GeminiClient.CreateUserStep(
                    $"{existingContext}\n\nConversation transcript:\n{transcript}\n\nCreate a concise, self-contained context summary of this conversation. Preserve the user's goals, requirements, decisions, constraints, important facts, unresolved questions, and any file details needed to continue. Do not mention that this is a summary and do not include conversational filler.",
                    Session.Attachments);
                var result = await _client.CreateSimpleInteractionAsync(
                    "gemini-2.5-flash-lite",
                    [],
                    [request],
                    "You compact conversations into accurate continuation context. Return only the compacted context text.",
                    null,
                    null,
                    _operationCancellation.Token);

                if (string.IsNullOrWhiteSpace(result.Text)) throw new InvalidOperationException("Gemini returned an empty compacted context.");

                var previousSession = Session;
                await DeleteRemoteAttachmentsAsync(previousSession);
                _sessions[_personality] = new ChatSession { ContextText = result.Text.Trim() };
                ComposerBox.Text = string.Empty;
                RenderSession();
                ContextPanel.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(ContextButton, "Hide conversation context");
                StatusText.Text = "Conversation compacted into context.";
            }
            catch (OperationCanceledException) { StatusText.Text = "Compaction stopped."; }
            catch (Exception exception) { AddMessage(new ChatMessage(ChatItemKind.Error, "Compaction error", exception.Message)); }
            finally
            {
                _operationCancellation.Dispose();
                _operationCancellation = null;
                SetBusy(false, StatusText.Text);
            }
        }

        private async Task DeleteRemoteAttachmentsAsync(ChatSession session)
        {
            if (_client is null) return;
            foreach (var attachment in session.Attachments.Where(item => !string.IsNullOrWhiteSpace(item.RemoteName))) try { await _client.DeleteFileAsync(attachment.RemoteName!, CancellationToken.None); } catch { }
        }

        private async void PersonalityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PersonalityBox.SelectedItem is not ChatPersonality personality || _changingPersonality) return;
            if (personality != _personality)
            {
                _changingPersonality = true;
                try
                {
                    _operationCancellation?.Cancel();
                    var previousPersonality = _personality;
                    await DeleteRemoteAttachmentsAsync(_sessions[previousPersonality]);
                    await DeleteRemoteAttachmentsAsync(_sessions[personality]);
                    _sessions[previousPersonality] = new ChatSession();
                    _sessions[personality] = new ChatSession();
                    _personality = personality;
                    ComposerBox.Text = string.Empty;
                    ContextPanel.Visibility = Visibility.Collapsed;
                    ToolTipService.SetToolTip(ContextButton, "Show conversation context");
                }
                finally { _changingPersonality = false; }
            }
            ComposerBox.PlaceholderText = personality == ChatPersonality.Artist ? "Describe the image or edit..." : $"Message {personality}...";
            _settings.LastChatPersonality = personality.ToString();
            await _settingsService.SaveAsync(_settings);
            RenderSession();
            StatusText.Text = string.Empty;
        }

        private void RenderSession()
        {
            _tools.DefaultPath = _personality == ChatPersonality.Technician && Session.HasPathContext ? Session.PathContext : null;
            ConversationHost.Children.Clear();
            foreach (var message in Session.Messages) RenderMessage(message);
            if (Session.Messages.Count == 0) { EmptyTitle.Text = $"Ask {_personality}"; EmptyDescription.Text = _personality == ChatPersonality.Technician ? "Diagnose Windows, inspect files, or automate a PC support task." : "Ask a question, or add reference material from Context."; ConversationHost.Children.Add(EmptyState); EmptyState.Visibility = Visibility.Visible; }
            RefreshContext();
        }

        private void AddMessage(ChatMessage message) { Session.Messages.Add(message); RenderMessage(message); }
        private void RenderMessage(ChatMessage message)
        {
            EmptyState.Visibility = Visibility.Collapsed; if (EmptyState.Parent is Panel parent) parent.Children.Remove(EmptyState);
            var body = new StackPanel { Spacing = message.Kind == ChatItemKind.Tool ? 4 : 7 };
            var title = new TextBlock
            {
                Text = message.Kind == ChatItemKind.Tool ? $"Tool · {message.Title}" : message.Title,
                FontSize = message.Kind == ChatItemKind.Tool ? 10 : 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources[message.Kind switch
                {
                    ChatItemKind.Error => "SystemFillColorCriticalBrush",
                    ChatItemKind.Tool => "TextFillColorSecondaryBrush",
                    _ => "TextFillColorPrimaryBrush"
                }]
            };
            if (message.Kind == ChatItemKind.Tool) title.FontFamily = new FontFamily("Cascadia Mono");
            body.Children.Add(title);
            if (message.AttachmentNames is not null) foreach (var name in message.AttachmentNames) body.Children.Add(new TextBlock { Text = $"📎 {name}", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            if (!string.IsNullOrWhiteSpace(message.Content))
                body.Children.Add(message.Kind switch
                {
                    ChatItemKind.Assistant or ChatItemKind.User => new MarkdownView { Markdown = message.Content },
                    ChatItemKind.Tool => CreateToolResultView(message.Content),
                    _ => CreateNormalizedTextBlock(message.Content)
                });
            if (message.Image is not null) body.Children.Add(CreateImagePanel(message.Image));
            var messageContainer = new Border
            {
                Padding = message.Kind == ChatItemKind.Tool ? new Thickness(12, 8, 12, 8) : new Thickness(14, 11, 14, 12),
                CornerRadius = new CornerRadius(message.Kind == ChatItemKind.Tool ? 9 : 12),
                BorderThickness = new Thickness(message.Kind == ChatItemKind.Tool ? 0 : 1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources[message.Kind switch
                {
                    ChatItemKind.User => "AccentFillColorTertiaryBrush",
                    ChatItemKind.Tool => "SubtleFillColorSecondaryBrush",
                    _ => "CardBackgroundFillColorDefaultBrush"
                }],
                Child = body,
                HorizontalAlignment = message.Kind == ChatItemKind.User ? HorizontalAlignment.Right : HorizontalAlignment.Stretch
            };
            if (message.Kind == ChatItemKind.User) messageContainer.MaxWidth = 720;
            ConversationHost.Children.Add(messageContainer);
            ScrollToLatestMessage();
        }

        private void ScrollToLatestMessage()
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ConversationScroller.UpdateLayout();
                ConversationScroller.ChangeView(null, ConversationScroller.ScrollableHeight, null, true);
            });
        }

        private static FrameworkElement CreateToolResultView(string content)
        {
            try
            {
                var root = JsonNode.Parse(content);
                if (root is null) return CreateCollapsedToolText(content);
                var tree = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
                AddJsonChildren(tree, root, 0);
                var status = root is JsonObject jsonObject ? jsonObject["status"]?.GetValue<string>() : null;
                var summary = root is JsonObject summaryObject ? summaryObject["summary"]?.GetValue<string>() : null;
                return new Expander
                {
                    Header = CreateToolSummary(status, summary),
                    Content = new Border
                    {
                        Padding = new Thickness(8, 6, 0, 4),
                        BorderThickness = new Thickness(1, 0, 0, 0),
                        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        Child = tree
                    },
                    IsExpanded = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
            }
            catch (System.Text.Json.JsonException)
            {
                return CreateCollapsedToolText(content);
            }
        }

        private static Expander CreateCollapsedToolText(string content) => new()
        {
            Header = CreateToolSummary(null, "View tool details"),
            Content = new Border
            {
                Padding = new Thickness(8, 6, 0, 4),
                BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = CreateNormalizedTextBlock(content)
            },
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        private static FrameworkElement CreateToolSummary(string? status, string? summary)
        {
            var panel = new Grid { ColumnSpacing = 8 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "result" : status.Replace('_', ' ');
            panel.Children.Add(new Border
            {
                Padding = new Thickness(7, 2, 7, 2),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                Child = new TextBlock
                {
                    Text = normalizedStatus,
                    FontSize = 9,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                }
            });

            var summaryText = new TextBlock
            {
                Text = NormalizeText(string.IsNullOrWhiteSpace(summary) ? "View tool details" : summary),
                FontSize = 10,
                FontFamily = new FontFamily("Cascadia Mono"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            Grid.SetColumn(summaryText, 1);
            panel.Children.Add(summaryText);
            return panel;
        }

        private static void AddJsonChildren(Panel target, JsonNode node, int depth)
        {
            if (node is JsonObject jsonObject)
            {
                foreach (var property in jsonObject)
                    target.Children.Add(CreateJsonBranch(property.Key, property.Value, depth));
                return;
            }

            if (node is JsonArray jsonArray)
            {
                for (var index = 0; index < jsonArray.Count; index++)
                    target.Children.Add(CreateJsonBranch($"[{index}]", jsonArray[index], depth));
                return;
            }

            target.Children.Add(CreateJsonBranch("result", node, depth));
        }

        private static FrameworkElement CreateJsonBranch(string label, JsonNode? value, int depth)
        {
            if (value is JsonObject jsonObject)
            {
                var children = new StackPanel { Spacing = 3 };
                AddJsonChildren(children, jsonObject, depth + 1);
                return CreateTreeExpander(label, $"Object · {jsonObject.Count} field(s)", children, depth);
            }

            if (value is JsonArray jsonArray)
            {
                var children = new StackPanel { Spacing = 3 };
                AddJsonChildren(children, jsonArray, depth + 1);
                return CreateTreeExpander(label, $"List · {jsonArray.Count} item(s)", children, depth);
            }

            var text = value is null ? "null" : value.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? NormalizeText(value.GetValue<string>())
                : value.ToJsonString();
            var leaf = CreateTreeLabel(label, text);
            leaf.Margin = new Thickness(depth * 18, 2, 0, 2);
            return leaf;
        }

        private static Expander CreateTreeExpander(string label, string description, FrameworkElement children, int depth)
        {
            return new Expander
            {
                Header = CreateTreeLabel(label, description),
                Content = children,
                IsExpanded = true,
                Margin = new Thickness(depth * 18, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
        }

        private static FrameworkElement CreateTreeLabel(string label, string value)
        {
            var panel = new Grid { ColumnSpacing = 8 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top
            });
            var valueText = new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 10,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(valueText, 1);
            panel.Children.Add(valueText);
            return panel;
        }

        private static TextBlock CreateNormalizedTextBlock(string content) => new()
        {
            Text = NormalizeText(content),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 10,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };

        private static string NormalizeText(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');

        private FrameworkElement CreateImagePanel(GeneratedImage generated)
        {
            var panel = new StackPanel { Spacing = 8 }; var image = new Image { MaxWidth = 720, MaxHeight = 540, Stretch = Stretch.Uniform }; _ = SetImageAsync(image, generated);
            var save = new Button { Content = "Download image", HorizontalAlignment = HorizontalAlignment.Left }; save.Click += async (_, _) => await SaveImageAsync(generated); panel.Children.Add(image); panel.Children.Add(save); return panel;
        }

        private static async Task SetImageAsync(Image image, GeneratedImage generated)
        {
            var stream = new InMemoryRandomAccessStream(); await stream.WriteAsync(generated.Data.AsBuffer()); stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); image.Source = bitmap;
        }

        private async Task SaveImageAsync(GeneratedImage generated)
        {
            if (App.MainWindow is null) return;
            var isJpeg = generated.MimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase);
            var picker = new FileSavePicker { SuggestedFileName = "artist-image" };
            picker.FileTypeChoices.Add(isJpeg ? "JPEG image" : "PNG image", [isJpeg ? ".jpg" : ".png"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await FileIO.WriteBytesAsync(file, generated.Data);
        }

        private void RefreshContext()
        {
            _renderingContext = true;
            ContextTextBox.Text = Session.ContextText;
            SystemInstructionText.Text = SystemInstruction();
            _renderingContext = false;

            ContextAttachmentHost.Items.Clear();
            foreach (var attachment in Session.Attachments)
            {
                var removeButton = new Button
                {
                    Tag = attachment,
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                    VerticalAlignment = VerticalAlignment.Center
                };
                removeButton.Click += RemoveContextAttachmentButton_Click;
                ToolTipService.SetToolTip(removeButton, $"Remove {attachment.DisplayName}");

                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                content.Children.Add(new FontIcon { Glyph = "\uE723", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(new TextBlock { Text = attachment.DisplayName, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(removeButton);
                ContextAttachmentHost.Items.Add(new Border
                {
                    Margin = new Thickness(16, 0, 6, 4),
                    Padding = new Thickness(0, 2, 2, 2),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Child = content
                });
            }

            var contextCount = Session.Attachments.Count + (string.IsNullOrWhiteSpace(Session.ContextText) ? 0 : 1);
            ContextCountBadge.Visibility = contextCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            ContextCountText.Text = contextCount.ToString();
            UpdateSendAvailability();
            CompactButton.IsEnabled = !_isBusy && Session.Messages.Count > 0;
        }

        private async void RemoveContextAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ChatAttachment attachment } || _operationCancellation is not null) return;
            Session.Attachments.Remove(attachment);
            if (_client is not null && !string.IsNullOrWhiteSpace(attachment.RemoteName))
                try { await _client.DeleteFileAsync(attachment.RemoteName!, CancellationToken.None); }
                catch (Exception exception) { AddMessage(new ChatMessage(ChatItemKind.Error, "Remove attachment error", exception.Message)); }
            RecalculatePathContext();
            RefreshContext();
        }

        private void ContextTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_renderingContext) return;
            Session.ContextText = ContextTextBox.Text;
            RecalculatePathContext();
            RefreshContextIndicator();
        }

        private void RefreshContextIndicator()
        {
            SystemInstructionText.Text = SystemInstruction();
            var contextCount = Session.Attachments.Count + (string.IsNullOrWhiteSpace(Session.ContextText) ? 0 : 1);
            ContextCountBadge.Visibility = contextCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            ContextCountText.Text = contextCount.ToString();
        }

        private void RecalculatePathContext()
        {
            if (_personality != ChatPersonality.Technician) return;
            Session.HasPathContext = false;
            Session.PathContext = null;
            _tools.DefaultPath = null;
            DiscoverPathContext(Session.ContextText, Session.Attachments);
            SystemInstructionText.Text = SystemInstruction();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_operationCancellation is not null)
            {
                _operationCancellation.Cancel();
                return;
            }
            await SendFromComposerAsync();
        }
        private void ComposerBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSendAvailability();
        private void UpdateSendAvailability() => SendButton.IsEnabled = _operationCancellation is not null || (!_isBusy && !string.IsNullOrWhiteSpace(ComposerBox.Text));
        private async void ComposerBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            if (_operationCancellation is null)
                await SendFromComposerAsync();
        }

        private async Task SendFromComposerAsync()
        {
            try
            {
                await SendAsync();
            }
            catch (Exception exception)
            {
                await _chatLog.WriteAsync("send.handler_failed",
                    ("personality", _personality),
                    ("exceptionType", exception.GetType().Name),
                    ("message", exception.Message));
                try
                {
                    AddMessage(new ChatMessage(ChatItemKind.Error, "Send error", exception.Message));
                }
                catch
                {
                    StatusText.Text = $"Send error: {exception.Message}";
                    StatusText.Visibility = Visibility.Visible;
                }
                finally
                {
                    try { SetBusy(false); }
                    catch
                    {
                        _isBusy = false;
                        BusyRing.IsActive = false;
                    }
                }
            }
        }
        private void SetBusy(bool busy, string status = "")
        {
            _isBusy = busy;
            BusyRing.IsActive = busy;
            var canStop = busy && _operationCancellation is not null;
            SendActionIcon.Glyph = canStop ? "\uEE95" : "\uE724";
            if (canStop) SendActionIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            else SendActionIcon.ClearValue(IconElement.ForegroundProperty);
            ToolTipService.SetToolTip(SendButton, canStop ? "Stop response" : "Send message");
            SendButton.Background = (Brush)Application.Current.Resources[canStop ? "SystemFillColorCriticalBrush" : "AccentFillColorDefaultBrush"];
            AddContextFileButton.IsEnabled = !busy;
            PersonalityBox.IsEnabled = !busy;
            CompactButton.IsEnabled = !busy && Session.Messages.Count > 0;
            UpdateSendAvailability();
            StatusText.Text = status;
        }
        private async void ChatPage_Unloaded(object sender, RoutedEventArgs e) { _operationCancellation?.Cancel(); foreach (var session in _sessions.Values) await DeleteRemoteAttachmentsAsync(session); _client?.Dispose(); _client = null; }
    }
}
