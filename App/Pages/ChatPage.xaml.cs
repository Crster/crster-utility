using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using App.Controls;
using App.Models;
using App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class ChatPage : Page
    {
        private const int MaximumToolRounds = 12;
        private readonly SecureSettingsService _settingsService = new();
        private readonly ChatToolService _tools = new();
        private readonly List<JsonObject> _history = [];
        private readonly List<ChatAttachment> _sessionAttachments = [];
        private readonly List<ChatAttachment> _pendingAttachments = [];
        private AppSettings _settings = new();
        private GeminiClient? _client;
        private CancellationTokenSource? _operationCancellation;
        private bool _loaded;

        public ChatPage()
        {
            InitializeComponent();
            Loaded += ChatPage_Loaded;
            Unloaded += ChatPage_Unloaded;
        }

        private async void ChatPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;
            _settings = await _settingsService.LoadAsync();
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey) && !await RequestApiKeyAsync())
            {
                StatusText.Text = "A Gemini API key is required.";
                return;
            }
            _client = new GeminiClient(_settings.GeminiApiKey);
            await LoadModelsAsync();
        }

        private async Task<bool> RequestApiKeyAsync()
        {
            var input = new PasswordBox { Header = "Gemini API key", PlaceholderText = "Paste your key", MinWidth = 380 };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Connect Gemini", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Password)) return false;
            _settings.GeminiApiKey = input.Password.Trim();
            await _settingsService.SaveAsync(_settings);
            return true;
        }

        private async Task LoadModelsAsync()
        {
            if (_client is null) return;
            SetBusy(true, "Loading models...");
            try
            {
                var availableModels = await _client.ListModelsAsync(CancellationToken.None);
                var models = CreateCuratedModels(availableModels);
                ModelBox.ItemsSource = models;
                var selected = models.FirstOrDefault(model => model.SupportsChat && model.Id == _settings.LastGeminiModel) ?? models.FirstOrDefault(model => model.SupportsChat);
                ModelBox.SelectedItem = selected;
                if (selected is null) StatusText.Text = "No chat-compatible Gemini models are available.";
            }
            catch (Exception exception) { AddMessage(ChatItemKind.Error, "Connection error", exception.Message); }
            finally { SetBusy(false); }
        }

        private static List<GeminiModel> CreateCuratedModels(IReadOnlyList<GeminiModel> availableModels)
        {
            var byId = availableModels.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
            var choices = new[]
            {
                (Id: "gemini-2.5-flash-lite", Label: "Mini", Description: "Gemini 2.5 Flash-Lite"),
                (Id: "gemini-3.5-flash-lite", Label: "Pro", Description: "Gemini 3.5 Flash-Lite"),
                (Id: "gemini-3.6-flash", Label: "Max", Description: "Gemini 3.6 Flash")
            };

            var models = new List<GeminiModel>();
            foreach (var choice in choices)
            {
                if (!byId.TryGetValue(choice.Id, out var available)) continue;
                models.Add(new GeminiModel
                {
                    Id = available.Id,
                    DisplayName = choice.Label,
                    Description = $"{choice.Description}. {available.Description}".Trim(),
                    SupportsChat = available.SupportsChat,
                    SupportsThinking = available.SupportsThinking
                });
            }
            return models;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendAsync();

        private async Task SendAsync()
        {
            if (_operationCancellation is not null || _client is null || ModelBox.SelectedItem is not GeminiModel { SupportsChat: true } model) return;
            var prompt = ComposerBox.Text;
            if (string.IsNullOrWhiteSpace(prompt) && _pendingAttachments.Count == 0) return;
            ComposerBox.Text = string.Empty;
            var attachments = _pendingAttachments.ToList();
            _pendingAttachments.Clear();
            RefreshAttachments();
            AddMessage(ChatItemKind.User, "You", prompt, attachments.Select(item => item.DisplayName));
            var userStep = GeminiClient.CreateUserStep(prompt, attachments);
            await RunInteractionLoopAsync(model, [userStep]);
        }

        private async Task RunInteractionLoopAsync(GeminiModel model, IReadOnlyList<JsonObject> initialSteps)
        {
            _operationCancellation = new CancellationTokenSource();
            SetBusy(true, "Gemini is working...");
            try
            {
                IReadOnlyList<JsonObject> nextSteps = initialSteps;
                for (var round = 0; round < MaximumToolRounds; round++)
                {
                    var thinkingMode = ThinkingBox.SelectedItem?.ToString() ?? "Smart";
                    var tools = string.IsNullOrWhiteSpace(_tools.WorkspaceRoot)
                        ? new JsonArray()
                        : ChatToolService.CreateDeclarations(thinkingMode == "Plan");
                    var result = await _client!.CreateInteractionAsync(model.Id, _history, nextSteps, tools,
                        thinkingMode switch { "None" => "minimal", "Plan" => "high", _ => "medium" }, CreateSystemInstruction(thinkingMode), _operationCancellation.Token);
                    foreach (var step in nextSteps) _history.Add((JsonObject)step.DeepClone());
                    foreach (var step in result.Steps) _history.Add(step);
                    if (!string.IsNullOrWhiteSpace(result.Text)) AddMessage(ChatItemKind.Assistant, model.DisplayName, result.Text);
                    if (result.FunctionCalls.Count == 0) return;

                    var functionResults = new List<JsonObject>();
                    foreach (var call in result.FunctionCalls)
                    {
                        var stopwatch = Stopwatch.StartNew();
                        ToolResult toolResult;
                        var approved = !ChatToolService.IsRisky(call.Name) || await ConfirmToolAsync(call);
                        if (!approved) toolResult = new ToolResult(false, "DENIED: The user declined this tool call.");
                        else toolResult = await _tools.ExecuteAsync(call.Name, call.Arguments, _operationCancellation.Token);
                        stopwatch.Stop();
                        AddToolCall(call, toolResult, approved, stopwatch.ElapsedMilliseconds);
                        functionResults.Add(GeminiClient.CreateFunctionResult(call, toolResult));
                    }
                    nextSteps = functionResults;
                }
                AddMessage(ChatItemKind.Error, "Tool limit reached", "Gemini exceeded the maximum of 12 consecutive tool rounds.");
            }
            catch (OperationCanceledException) { StatusText.Text = "Stopped"; }
            catch (Exception exception) { AddMessage(ChatItemKind.Error, "Gemini error", exception.Message); }
            finally { _operationCancellation.Dispose(); _operationCancellation = null; SetBusy(false); }
        }

        private string CreateSystemInstruction(string thinkingMode)
        {
            var workspaceInstruction = string.IsNullOrWhiteSpace(_tools.WorkspaceRoot)
                ? "No workspace is selected, so no local tools are available."
                : $"The selected workspace is '{_tools.WorkspaceRoot}'.";
            return thinkingMode == "Plan"
                ? $"You are the Crster Utility assistant. Plan mode is active: inspect with read-only tools as useful, but return a plan and do not attempt mutations or execution. Tool cursor ranges are zero-based, end-exclusive UTF-16 offsets. {workspaceInstruction}"
                : $"You are the Crster Utility assistant. Use available local automation tools when they materially help. Search before editing, use zero-based end-exclusive UTF-16 cursor ranges, keep changes precise, and report tool failures honestly. {workspaceInstruction}";
        }

        private async Task<bool> ConfirmToolAsync(GeminiFunctionCall call)
        {
            var content = new ScrollViewer { MaxHeight = 420, Content = new TextBox { Text = call.Arguments.ToJsonString(new() { WriteIndented = true }), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono"), MinWidth = 480 } };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = $"Allow {call.Name}?", Content = content, PrimaryButtonText = "Allow", CloseButtonText = "Deny", DefaultButton = ContentDialogButton.Close };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (_client is null || App.MainWindow is null || _operationCancellation is not null) return;
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;
            SetBusy(true, "Uploading files...");
            try
            {
                foreach (var file in files)
                {
                    var attachment = await _client.UploadFileAsync(file.Path, CancellationToken.None);
                    _pendingAttachments.Add(attachment); _sessionAttachments.Add(attachment);
                }
                RefreshAttachments();
            }
            catch (Exception exception) { AddMessage(ChatItemKind.Error, "Upload error", exception.Message); }
            finally { SetBusy(false); }
        }

        private async void WorkspaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is null) return;
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            _tools.WorkspaceRoot = folder.Path;
            WorkspaceText.Text = folder.Name;
            ToolTipService.SetToolTip(WorkspaceButton, folder.Path);
        }

        private async void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _operationCancellation?.Cancel();
            await DeleteRemoteAttachmentsAsync();
            _history.Clear(); _sessionAttachments.Clear(); _pendingAttachments.Clear(); ConversationHost.Children.Clear(); ConversationHost.Children.Add(EmptyState); EmptyState.Visibility = Visibility.Visible; RefreshAttachments(); StatusText.Text = string.Empty;
        }

        private async Task DeleteRemoteAttachmentsAsync()
        {
            if (_client is null) return;
            foreach (var attachment in _sessionAttachments)
                if (!string.IsNullOrWhiteSpace(attachment.RemoteName)) try { await _client.DeleteFileAsync(attachment.RemoteName, CancellationToken.None); } catch { }
        }

        private void AddMessage(ChatItemKind kind, string title, string content, IEnumerable<string>? attachmentNames = null)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            if (EmptyState.Parent is Panel parent) parent.Children.Remove(EmptyState);
            var body = new StackPanel { Spacing = 7 };
            body.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources[kind == ChatItemKind.Error ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"] });
            if (attachmentNames is not null)
                foreach (var name in attachmentNames) body.Children.Add(new TextBlock { Text = $"📎 {name}", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            if (!string.IsNullOrEmpty(content))
            {
                if (kind is ChatItemKind.Assistant or ChatItemKind.User) body.Children.Add(new MarkdownView { Markdown = content });
                else body.Children.Add(new TextBlock { Text = content, FontFamily = new FontFamily("Cascadia Mono"), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
            }
            var border = new Border { Padding = new Thickness(14, 11, 14, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources[kind == ChatItemKind.User ? "AccentFillColorTertiaryBrush" : "CardBackgroundFillColorDefaultBrush"], Child = body, MaxWidth = 820, HorizontalAlignment = kind == ChatItemKind.User ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            ConversationHost.Children.Add(border);
            _ = DispatcherQueue.TryEnqueue(() => ConversationScroller.ChangeView(null, ConversationScroller.ScrollableHeight, null));
        }

        private void AddToolCall(GeminiFunctionCall call, ToolResult result, bool approved, long elapsedMilliseconds)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            if (EmptyState.Parent is Panel parent) parent.Children.Remove(EmptyState);

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new FontIcon { Glyph = "\uE756", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = call.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var summary = GetFirstArgumentSummary(call.Arguments);
            if (!string.IsNullOrEmpty(summary))
            {
                header.Children.Add(new TextBlock
                {
                    Text = summary,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 300
                });
            }

            var status = approved ? result.Success ? "Completed" : "Failed" : "Denied";
            var details = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            details.Children.Add(new TextBlock
            {
                Text = $"{status} · {elapsedMilliseconds} ms",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources[result.Success ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush"]
            });
            details.Children.Add(new TextBlock
            {
                Text = $"Arguments\n{call.Arguments.ToJsonString(new() { WriteIndented = true })}\n\nResult\n{result.Output}",
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            });

            var expander = new Expander { Header = header, Content = details, IsExpanded = false };
            var border = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                Child = expander,
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ConversationHost.Children.Add(border);
            _ = DispatcherQueue.TryEnqueue(() => ConversationScroller.ChangeView(null, ConversationScroller.ScrollableHeight, null));
        }

        private static string GetFirstArgumentSummary(JsonObject arguments)
        {
            var first = arguments.FirstOrDefault();
            if (string.IsNullOrEmpty(first.Key) || first.Value is null) return string.Empty;
            var value = first.Value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : first.Value.ToJsonString();
            if (value.Length > 72) value = value[..69] + "...";
            return $"{first.Key}: {value}";
        }

        private void RefreshAttachments()
        {
            AttachmentHost.Items.Clear();
            foreach (var attachment in _pendingAttachments)
                AttachmentHost.Items.Add(new Border { Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"], Child = new TextBlock { Text = $"📎 {attachment.DisplayName}" } });
        }

        private async void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModelBox.SelectedItem is not GeminiModel model) return;
            SendButton.IsEnabled = model.SupportsChat;
            _settings.LastGeminiModel = model.Id;
            await _settingsService.SaveAsync(_settings);
        }

        private async void ComposerBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            var controlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (e.Key == global::Windows.System.VirtualKey.Enter && !controlDown) { e.Handled = true; await SendAsync(); }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();
        private void SetBusy(bool busy, string status = "") { BusyRing.IsActive = busy; StopButton.Visibility = busy && _operationCancellation is not null ? Visibility.Visible : Visibility.Collapsed; SendButton.IsEnabled = !busy && ModelBox.SelectedItem is GeminiModel { SupportsChat: true }; StatusText.Text = status; }
        private async void ChatPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _operationCancellation?.Cancel();
            await DeleteRemoteAttachmentsAsync();
            _client?.Dispose();
            _client = null;
        }
    }
}
