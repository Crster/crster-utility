using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace App.Controls
{
    internal sealed partial class MonacoEditorControl : UserControl
    {
        private TaskCompletionSource _ready = new();
        private bool _initialized;

        public MonacoEditorControl()
        {
            InitializeComponent();
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Loaded += MonacoEditorControl_Loaded;
            SizeChanged += MonacoEditorControl_SizeChanged;
            ActualThemeChanged += MonacoEditorControl_ActualThemeChanged;
        }

        public event EventHandler<string>? ContentChanged;
        public event EventHandler<string>? SaveRequested;

        public Task PreloadAsync() => _ready.Task;

        public async Task OpenDocumentAsync(string documentId, string text, string language)
        {
            Debug.WriteLine($"[Cody.Monaco] Open document requested. Id={documentId}, Language={language}, Characters={text.Length}, ControlInitialized={_initialized}");
            await _ready.Task;
            PostMessage(new
            {
                type = "openModel",
                documentId,
                value = text,
                language,
                theme = ActualTheme == ElementTheme.Light ? "vs" : "vs-dark"
            });
        }

        public async Task ActivateDocumentAsync(string documentId)
        {
            await _ready.Task;
            PostMessage(new { type = "activateModel", documentId });
        }

        public async Task OpenDiffAsync(string documentId, string original, string modified, string language)
        {
            await _ready.Task;
            PostMessage(new
            {
                type = "openDiff",
                documentId,
                original,
                modified,
                language,
                theme = ActualTheme == ElementTheme.Light ? "vs" : "vs-dark"
            });
        }

        public async Task RevealMatchAsync(string documentId, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            await _ready.Task;
            PostMessage(new { type = "revealMatch", documentId, query });
        }

        public async Task CloseDocumentAsync(string documentId)
        {
            await _ready.Task;
            PostMessage(new { type = "closeModel", documentId });
        }

        public async Task<string> GetTextAsync(string documentId)
        {
            await _ready.Task;
            Debug.WriteLine($"[Cody.Monaco] Reading editor text. Id={documentId}");
            var json = await EditorWebView.ExecuteScriptAsync(
                $"window.getDocumentValue({JsonSerializer.Serialize(documentId)})");
            return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
        }

        private async void MonacoEditorControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            Debug.WriteLine("[Cody.Monaco] Control loaded. Initializing WebView2.");
            try
            {
                ApplyNativeBackground();
                await EditorWebView.EnsureCoreWebView2Async();
                ApplyNativeBackground();
                Debug.WriteLine($"[Cody.Monaco] WebView2 initialized. Runtime={EditorWebView.CoreWebView2.Environment.BrowserVersionString}");
                var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco");
                Debug.WriteLine($"[Cody.Monaco] Asset root={assets}");
                Debug.WriteLine($"[Cody.Monaco] editor.html exists={File.Exists(Path.Combine(assets, "editor.html"))}");
                Debug.WriteLine($"[Cody.Monaco] loader.js exists={File.Exists(Path.Combine(assets, "vs", "loader.js"))}");
                EditorWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "monaco.local",
                    assets,
                    CoreWebView2HostResourceAccessKind.Allow);
                EditorWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                EditorWebView.CoreWebView2.ProcessFailed += (_, args) =>
                    Debug.WriteLine($"[Cody.Monaco] WebView2 process failed. Kind={args.ProcessFailedKind}");
                EditorWebView.NavigationStarting += (_, args) =>
                    Debug.WriteLine($"[Cody.Monaco] Navigation starting. Uri={args.Uri}");
                EditorWebView.NavigationCompleted += (_, args) =>
                    Debug.WriteLine($"[Cody.Monaco] Navigation completed. Success={args.IsSuccess}, Error={args.WebErrorStatus}");
                var theme = ActualTheme == ElementTheme.Light ? "light" : "dark";
                EditorWebView.Source = new Uri($"https://monaco.local/editor.html?theme={theme}");
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Cody.Monaco] Initialization failed: {exception}");
                LoadingRing.IsActive = false;
                EditorWebView.Visibility = Visibility.Collapsed;
                Content = new TextBlock
                {
                    Text = $"Monaco editor could not start: {exception.Message}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16)
                };
            }
        }

        private void CoreWebView2_WebMessageReceived(
            CoreWebView2 sender,
            CoreWebView2WebMessageReceivedEventArgs args)
        {
            var message = args.TryGetWebMessageAsString();
            if (message != "changed") Debug.WriteLine($"[Cody.Monaco] Web message: {message}");
            if (message == "ready")
            {
                _ready.TrySetResult();
            }
            else if (message == "modelApplied")
            {
                Debug.WriteLine($"[Cody.Monaco] Model applied. Control={ActualWidth:0}x{ActualHeight:0}, Host={EditorHost.ActualWidth:0}x{EditorHost.ActualHeight:0}, WebView={EditorWebView.ActualWidth:0}x{EditorWebView.ActualHeight:0}");
                EditorWebView.Opacity = 1;
                EditorWebView.InvalidateMeasure();
                EditorWebView.InvalidateArrange();
                EditorWebView.UpdateLayout();
                PostMessage(new { type = "layout" });
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    var focused = EditorWebView.Focus(FocusState.Programmatic);
                    Debug.WriteLine($"[Cody.Monaco] WebView focus requested after model apply. Focused={focused}");
                    PostMessage(new { type = "focus" });
                });
            }
            else if (message.StartsWith("error:", StringComparison.Ordinal))
            {
                Debug.WriteLine($"[Cody.Monaco] JavaScript error: {message[6..]}");
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                EditorWebView.Visibility = Visibility.Collapsed;
                Content = new TextBlock
                {
                    Text = $"Monaco editor could not start: {message[6..]}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16)
                };
            }
            else if (message.StartsWith("changed:", StringComparison.Ordinal))
                ContentChanged?.Invoke(this, Uri.UnescapeDataString(message[8..]));
            else if (message.StartsWith("save:", StringComparison.Ordinal))
                SaveRequested?.Invoke(this, Uri.UnescapeDataString(message[5..]));
        }

        private void PostMessage(object message)
        {
            EditorWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }

        private void MonacoEditorControl_ActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyNativeBackground();
            if (_ready.Task.IsCompletedSuccessfully)
            {
                Debug.WriteLine($"[Cody.Monaco] Applying theme. Theme={ActualTheme}");
                PostMessage(new
                {
                    type = "setTheme",
                    theme = ActualTheme == ElementTheme.Light ? "vs" : "vs-dark"
                });
            }
        }

        private void ApplyNativeBackground()
        {
            EditorWebView.DefaultBackgroundColor = ActualTheme == ElementTheme.Light
                ? Colors.White
                : ColorHelper.FromArgb(255, 30, 30, 30);
        }

        private void MonacoEditorControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_ready.Task.IsCompletedSuccessfully) return;
            Debug.WriteLine($"[Cody.Monaco] Size changed. Width={e.NewSize.Width:0}, Height={e.NewSize.Height:0}");
            PostMessage(new { type = "layout" });
        }
    }
}
