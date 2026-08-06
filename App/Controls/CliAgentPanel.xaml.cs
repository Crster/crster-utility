using System;
using System.Threading.Tasks;
using App.Services;
using EasyWindowsTerminalControl;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace App.Controls
{
    /// <summary>Hosts one command line coding agent in a console, inside the panel Cody normally fills.</summary>
    public sealed partial class CliAgentPanel : UserControl
    {
        private EasyTerminalControl? _terminal;
        private CliAgentTool? _tool;
        private DispatcherQueueTimer? _resizeSettleTimer;
        private string _workspacePath = string.Empty;
        private string _startError = string.Empty;
        private bool _startWhenSized;
        private bool _isReady;

        public CliAgentPanel()
        {
            InitializeComponent();
            Loaded += CliAgentPanel_Loaded;
            // Switching back to Cody collapses this panel, and docking it re-parents it. Both
            // detach the console's child window, so it has to be re-mapped and re-sized after.
            RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateSessionVisibility());
            UpdateHeader();
        }

        /// <summary>Folder the agent runs in. Changing it stops the running session.</summary>
        internal string WorkspacePath
        {
            get => _workspacePath;
            set
            {
                var path = value ?? string.Empty;
                if (string.Equals(_workspacePath, path, StringComparison.OrdinalIgnoreCase)) return;
                _workspacePath = path;
                StopSession();
            }
        }

        internal CliAgentTool? ActiveTool => _tool;

        /// <summary>Shows the agent and starts its session when it is not running yet.</summary>
        internal void Activate(CliAgentTool tool)
        {
            if (_tool is not null && !string.Equals(_tool.Id, tool.Id, StringComparison.OrdinalIgnoreCase))
                StopSession();

            _tool = tool;
            _startError = string.Empty;
            StartSession();
            UpdateSessionVisibility();
            UpdateHeader();
            FocusSession();
        }

        /// <summary>Kills the agent process and releases its console.</summary>
        internal void StopSession()
        {
            var terminal = _terminal;
            _terminal = null;
            _isReady = false;
            _startWhenSized = false;
            if (terminal is null)
            {
                UpdateHeader();
                return;
            }

            terminal.Visibility = Visibility.Collapsed;
            try
            {
                var process = terminal.ConPTYTerm.Process;
                if (process is not null && !process.HasExited) process.Kill(true);
            }
            catch (Exception exception) when (IsExpectedShutdownException(exception))
            {
            }

            try
            {
                var session = terminal.DisconnectConPTYTerm();
                session?.CloseStdinToApp();
                session?.StopExternalTermOnly();
            }
            catch (Exception exception) when (IsExpectedShutdownException(exception))
            {
            }

            TerminalHost.Children.Remove(terminal);
            UpdateHeader();
        }

        internal void FocusSession()
        {
            var terminal = _terminal;
            if (terminal is null || !IsOnScreen) return;
            // Low priority, so the focus lands after the console has been revealed and arranged.
            _ = DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    if (!ReferenceEquals(_terminal, terminal) || !IsOnScreen) return;
                    terminal.Terminal.Focus(FocusState.Programmatic);
                });
        }

        private bool IsOnScreen => IsLoaded && Visibility == Visibility.Visible && XamlRoot is not null;

        /// <summary>
        /// Starts the console only once its host has a real size. The console sizes its child window
        /// from the first layout pass it sees, so attaching it in the same pass that reveals this
        /// panel would start it against a zero-sized host and make it report a size the layout
        /// system rejects. When the size is not known yet the start waits for the next layout pass.
        /// </summary>
        private void StartSession()
        {
            if (_tool is null || _workspacePath.Length == 0 || _terminal is not null) return;

            var width = TerminalHost.ActualWidth;
            var height = TerminalHost.ActualHeight;
            if (!IsOnScreen || !IsUsableSize(width) || !IsUsableSize(height))
            {
                _startWhenSized = true;
                return;
            }

            _startWhenSized = false;
            try
            {
                var terminal = new EasyTerminalControl
                {
                    StartupCommandLine = CliAgentCatalog.CreateCommandLine(_tool, _workspacePath),
                    WorkingDirectory = _workspacePath,
                    FontFamilyWhenSettingTheme = new FontFamily(TerminalPresets.FontFamily),
                    FontSizeWhenSettingTheme = TerminalPresets.FontSize,
                    LogConPTYOutput = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    // Sized before it enters the tree so its first measure is never unconstrained.
                    Width = width,
                    Height = height,
                    // Revealed by UpdateSessionVisibility once this panel has been arranged.
                    Visibility = Visibility.Collapsed,
                    Win32InputMode = true,
                    // A full-screen agent UI needs the keys the host would otherwise consume.
                    InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey
                        | EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
                    Theme = TerminalPresets.CreateTheme()
                };
                _terminal = terminal;
                TerminalHost.Children.Add(terminal);
                ResizeWhenReady(terminal);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or System.IO.IOException)
            {
                _terminal = null;
                _startError = $"Could not start {_tool.Name}: {exception.Message}";
            }
        }

        /// <summary>Waits for the agent process and its native control to settle, then gives it the panel size.</summary>
        private async void ResizeWhenReady(EasyTerminalControl terminal)
        {
            const int readinessAttempts = 20;
            for (var attempt = 0; attempt < readinessAttempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 600 : 250));
                if (!ReferenceEquals(_terminal, terminal)) return;
                if (terminal.ConPTYTerm.TermProcIsStarted && terminal.Terminal.IsLoaded) break;
                if (attempt == readinessAttempts - 1) return;
            }

            _isReady = true;
            UpdateHeader();
            UpdateSessionVisibility();
            FocusSession();
        }

        /// <summary>Hides the console while this panel is off screen, and maps it back once the panel
        /// has really been arranged, because it attaches its child window the moment it turns visible.</summary>
        private void UpdateSessionVisibility()
        {
            var terminal = _terminal;
            if (terminal is null) return;

            if (!IsOnScreen)
            {
                terminal.Visibility = Visibility.Collapsed;
                return;
            }

            _ = DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    if (!ReferenceEquals(_terminal, terminal) || !IsOnScreen) return;
                    terminal.Visibility = Visibility.Visible;
                    ResizeSession(terminal);
                });
        }

        private void CliAgentPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (_startWhenSized) StartSession();
            UpdateSessionVisibility();
            UpdateHeader();
        }

        // Resizing a console reflows its buffer and repaints, so a splitter or window drag would
        // stutter if every size tick was forwarded. Coalesce a burst into one resize.
        private void TerminalHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_startWhenSized)
            {
                StartSession();
                UpdateSessionVisibility();
                UpdateHeader();
                return;
            }

            _resizeSettleTimer ??= DispatcherQueue.CreateTimer();
            _resizeSettleTimer.Stop();
            _resizeSettleTimer.Interval = TimeSpan.FromMilliseconds(80);
            _resizeSettleTimer.IsRepeating = false;
            _resizeSettleTimer.Tick -= ResizeSettleTimer_Tick;
            _resizeSettleTimer.Tick += ResizeSettleTimer_Tick;
            _resizeSettleTimer.Start();
        }

        private void ResizeSettleTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            ResizeSession(_terminal);
        }

        private void ResizeSession(EasyTerminalControl? terminal)
        {
            if (!_isReady || terminal is null || !ReferenceEquals(_terminal, terminal) || !IsOnScreen)
                return;

            var width = TerminalHost.ActualWidth;
            var height = TerminalHost.ActualHeight;
            if (!IsUsableSize(width) || !IsUsableSize(height)) return;

            try
            {
                terminal.Width = width;
                terminal.Height = height;
            }
            catch (ArgumentException)
            {
                // The native control can detach while the panel is being hidden.
            }
        }

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tool is null) return;
            StopSession();
            _startError = string.Empty;
            StartSession();
            UpdateSessionVisibility();
            UpdateHeader();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e) => StopSession();

        private void UpdateHeader()
        {
            var tool = _tool;
            AgentNameText.Text = tool?.Name ?? "Agent";
            ToolTipService.SetToolTip(AgentNameText, tool?.FileName ?? string.Empty);
            ApprovalBadge.Visibility = tool?.SkipsApprovals == true ? Visibility.Visible : Visibility.Collapsed;

            var isStarted = _terminal is not null;
            var isStarting = _startWhenSized || (isStarted && !_isReady);
            StatusText.Text = isStarted || isStarting ? (_isReady ? "Running" : "Starting") : "Stopped";
            RestartButton.IsEnabled = tool is not null && _workspacePath.Length > 0;
            StopButton.IsEnabled = isStarted;

            var showConsole = isStarted || isStarting;
            TerminalHost.Visibility = showConsole ? Visibility.Visible : Visibility.Collapsed;
            PlaceholderPanel.Visibility = showConsole ? Visibility.Collapsed : Visibility.Visible;
            if (showConsole) return;

            if (_workspacePath.Length == 0)
            {
                PlaceholderText.Text = "Choose a workspace to start this agent.";
                PlaceholderStartButton.Visibility = Visibility.Collapsed;
                return;
            }

            PlaceholderText.Text = _startError.Length > 0
                ? _startError
                : $"{tool?.Name ?? "This agent"} is not running.";
            PlaceholderStartText.Text = $"Start {tool?.Name ?? "agent"}";
            PlaceholderStartButton.Visibility = Visibility.Visible;
        }

        private static bool IsUsableSize(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;

        private static bool IsExpectedShutdownException(Exception exception) =>
            exception is InvalidOperationException
                or ArgumentException
                or System.ComponentModel.Win32Exception
                or NotSupportedException;
    }
}
