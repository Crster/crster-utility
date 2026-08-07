using System;
using System.Threading.Tasks;
using App.Services;
using App.Services.Mcp;
using EasyWindowsTerminalControl;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace App.Controls
{
    /// <summary>Hosts one command line coding agent in a console, inside the panel Cody normally fills.</summary>
    public sealed partial class CliAgentPanel : UserControl
    {
        private EasyTerminalControl? _terminal;
        private CliAgentTool? _tool;
        private CliAgentMcpWiring? _mcp;
        private DispatcherQueueTimer? _resizeSettleTimer;
        private DispatcherQueueTimer? _restartTimer;
        private string _workspacePath = string.Empty;
        private string _startError = string.Empty;
        private bool _startWhenSized;
        private bool _isSuppressed;
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

        /// <summary>True while the hosted CLI console is starting or running, so the host page can
        /// refuse to swap it out for Cody mid-session.</summary>
        internal bool IsSessionActive => _terminal is not null || _startWhenSized;

        /// <summary>Shows the agent and starts its session when it is not running yet. A different agent,
        /// or a change to what the Cody MCP server offers it, restarts the session, because a CLI reads
        /// its MCP configuration once at launch.</summary>
        internal void Activate(CliAgentTool tool, CliAgentMcpWiring? mcp)
        {
            var agentChanged = _tool is not null
                && !string.Equals(_tool.Id, tool.Id, StringComparison.OrdinalIgnoreCase);
            var wiringChanged = (_mcp is null) != (mcp is null)
                || !string.Equals(_mcp?.Arguments, mcp?.Arguments, StringComparison.Ordinal);
            var restarting = agentChanged || (wiringChanged && _terminal is not null);

            // Same agent, only what the Cody MCP server offers it changed. Restart the console in
            // place instead of replacing the control: a control that has been detached still throws
            // from its own GettingFocus handler the next time anything takes focus.
            if (!agentChanged && wiringChanged && _terminal is { } running)
            {
                _tool = tool;
                _mcp = mcp;
                _startError = string.Empty;
                RestartInPlace(running);
                return;
            }

            if (restarting) StopSession();

            _tool = tool;
            _mcp = mcp;
            _startError = string.Empty;

            // The console that just went away is still being torn down by WinUI. Starting its
            // replacement in the same pass makes two native consoles fight over the same host,
            // so wait for the tree to settle first.
            if (restarting)
            {
                UpdateHeader();
                StartSessionAfterTeardown();
                return;
            }

            StartSession();
            UpdateSessionVisibility();
            UpdateHeader();
            FocusSession();
        }

        /// <summary>Relaunches the agent inside the console that is already hosted, so no control is
        /// ever detached. Falls back to a full restart if the control cannot restart itself.</summary>
        private async void RestartInPlace(EasyTerminalControl terminal)
        {
            if (_tool is null) return;

            try
            {
                terminal.StartupCommandLine = CliAgentCatalog.CreateCommandLine(
                    _tool,
                    _workspacePath,
                    _mcp?.Arguments ?? string.Empty,
                    _mcp?.Environment);
                terminal.WorkingDirectory = _workspacePath;
                await terminal.RestartTerm();
            }
            catch (Exception exception) when (exception is SystemException)
            {
                // The control could not relaunch its shell; fall back to a full restart.
                if (!ReferenceEquals(_terminal, terminal)) return;

                StopSession();
                UpdateHeader();
                StartSessionAfterTeardown();
                return;
            }

            if (!ReferenceEquals(_terminal, terminal)) return;

            UpdateHeader();
            // The new shell starts at a default size, so push the panel size back into it.
            ResizeSession(terminal);
            FocusSession();
        }

        /// <summary>
        /// Starts the replacement console once the outgoing one has really gone. A dispatcher hop is
        /// not enough. On a first load the start waits for the host to gain a size, and that wait is
        /// what lets the console attach cleanly. A restart has no such wait, because the panel already
        /// has its size and no layout change ever comes, so the new console would attach while the old
        /// one is still detaching and size itself against that half-torn-down host.
        /// </summary>
        private void StartSessionAfterTeardown()
        {
            _restartTimer ??= DispatcherQueue.CreateTimer();
            _restartTimer.Stop();
            _restartTimer.Interval = TimeSpan.FromMilliseconds(250);
            _restartTimer.IsRepeating = false;
            _restartTimer.Tick -= RestartTimer_Tick;
            _restartTimer.Tick += RestartTimer_Tick;
            _restartTimer.Start();
        }

        private void RestartTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            if (_tool is null || _terminal is not null) return;

            StartSession();
            UpdateSessionVisibility();
            UpdateHeader();
            FocusSession();
        }

        /// <summary>Kills the agent process and releases its console.</summary>
        internal void StopSession()
        {
            // A caller that stops on purpose decides for itself whether to start again.
            _restartTimer?.Stop();
            var terminal = _terminal;
            _terminal = null;
            _isReady = false;
            _startWhenSized = false;
            if (terminal is null)
            {
                UpdateHeader();
                return;
            }

            // The console hands focus to its native child by cancelling the XAML focus move. Once it
            // has left the visual tree that cancel is rejected, and the rejection reaches the
            // dispatcher as an unhandled exception the next time anything takes focus. So take focus
            // off the dying console and make it unfocusable before it goes.
            try
            {
                var focused = FocusManager.GetFocusedElement(XamlRoot);
                var hadFocus = ReferenceEquals(focused, terminal)
                    || ReferenceEquals(focused, terminal.Terminal);
                terminal.Terminal.IsTabStop = false;
                terminal.Terminal.IsHitTestVisible = false;
                terminal.Terminal.IsEnabled = false;
                terminal.IsTabStop = false;
                terminal.IsEnabled = false;
                if (hadFocus && IsLoaded) RestartButton.Focus(FocusState.Programmatic);
            }
            catch (Exception exception) when (exception is SystemException)
            {
                // The console is going away regardless; a failed focus move must not stop the teardown.
            }

            // A visible console keeps mapping its window from layout updates after it leaves the
            // tree, so hide it before the teardown detaches it.
            terminal.Visibility = Visibility.Collapsed;

            // Each step gets its own guard: if one throws, the rest must still run, or the console
            // is left half attached and the next layout pass walks into freed native state.
            TermPTY? session = null;
            try
            {
                session = terminal.DisconnectConPTYTerm();
            }
            catch (Exception exception) when (IsExpectedShutdownException(exception))
            {
            }

            try
            {
                session?.CloseStdinToApp();
            }
            catch (Exception exception) when (IsExpectedShutdownException(exception))
            {
            }

            try
            {
                session?.StopExternalTermOnly();
            }
            catch (Exception exception) when (IsExpectedShutdownException(exception))
            {
            }

            // Disconnect while the native terminal is still hosted; removing it first races WinUI teardown.
            TerminalHost.Children.Remove(terminal);
            // Drop the pinned height so the next console is measured fresh, not against this one.
            TerminalHost.Height = double.NaN;

            // Only now, with the console detached, is it safe to end a shell that ignored the closed
            // stdin. Killing the tree while ConPTY was still attached takes the whole app down.
            try
            {
                var process = session?.Process;
                if (process is not null && !process.HasExited) process.Kill(true);
            }
            catch (Exception exception) when (exception is SystemException)
            {
                // The shell may already have ended with its stdin closed.
            }

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

        /// <summary>Hides the console while a menu, flyout or dialog is open above it. The console
        /// draws in its own child window, which always sits over XAML popups.</summary>
        internal void SetConsoleSuppressed(bool suppressed)
        {
            if (_isSuppressed == suppressed) return;
            _isSuppressed = suppressed;
            RefreshSession();
        }

        private bool IsOnScreen =>
            IsLoaded && !_isSuppressed && Visibility == Visibility.Visible && XamlRoot is not null;

        /// <summary>Picks up a start that was waiting for a size, then re-maps and re-sizes the console.</summary>
        private void RefreshSession()
        {
            if (_startWhenSized) StartSession();
            UpdateSessionVisibility();
            UpdateHeader();
        }

        /// <summary>
        /// Starts the console only once its host has a real size. The console sizes its child window
        /// from the first layout pass it sees, so attaching it in the same pass that reveals this
        /// panel would start it against a zero-sized host and make it report a size the layout
        /// system rejects. When the size is not known yet the start waits for the next layout pass.
        /// </summary>
        private void StartSession()
        {
            if (_tool is null || _workspacePath.Length == 0 || _terminal is not null) return;
            // A console left over from a teardown that has not finished must never get a second one
            // added beside it; wait for the host to be empty instead.
            if (TerminalHost.Children.Count > 0)
            {
                // No layout change is coming to retrigger this, so ask again shortly.
                StartSessionAfterTeardown();
                return;
            }

            var measured = TryMeasureConsole(out var width, out var height);
            if (!IsOnScreen || !measured)
            {
                _startWhenSized = true;
                return;
            }

            _startWhenSized = false;
            // Pin the host before the console is added, so its very first measure is bounded.
            TerminalHost.Height = height;
            try
            {
                var terminal = new EasyTerminalControl
                {
                    StartupCommandLine = CliAgentCatalog.CreateCommandLine(
                        _tool,
                        _workspacePath,
                        _mcp?.Arguments ?? string.Empty,
                        _mcp?.Environment),
                    WorkingDirectory = _workspacePath,
                    FontFamilyWhenSettingTheme = new FontFamily(TerminalPresets.FontFamily),
                    FontSizeWhenSettingTheme = TerminalPresets.FontSize,
                    LogConPTYOutput = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    // Deliberately left unsized. The control applies its font when its inner terminal
                    // loads, so a size given before that is measured in default font cells and the
                    // console keeps that oversized text. ResizeWhenReady assigns the size afterwards,
                    // which is a real change and makes the console recount rows and columns.
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
            // Re-apply the theme now that the inner terminal has loaded, so the font family and size
            // are in force before the first resize decides how many rows and columns fit.
            try
            {
                terminal.Theme = TerminalPresets.CreateTheme();
            }
            catch (Exception exception) when (IsExpectedShutdownException(exception))
            {
                // The control can detach before the theme is re-applied; the first theme still holds.
            }

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

        private void CliAgentPanel_Loaded(object sender, RoutedEventArgs e) => RefreshSession();

        // Resizing a console reflows its buffer and repaints, so a splitter or window drag would
        // stutter if every size tick was forwarded. Coalesce a burst into one resize.
        private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_startWhenSized)
            {
                RefreshSession();
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
            if (!TryMeasureConsole(out var width, out var height)) return;

            try
            {
                TerminalHost.Height = height;
                terminal.Width = width;
                terminal.Height = height;
            }
            catch (ArgumentException)
            {
                // The native control can detach while the panel is being hidden.
            }
        }

        /// <summary>Measures the room left for the console from the panel and its header, never from
        /// the host itself: the host's height is something this code sets, so reading it back would
        /// only ever return the last value written and the console could never be resized down.</summary>
        private bool TryMeasureConsole(out double width, out double height)
        {
            width = ActualWidth;
            height = ActualHeight - HeaderBorder.ActualHeight;
            return IsUsableSize(width) && IsUsableSize(height);
        }

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tool is null) return;
            StopSession();
            _startError = string.Empty;
            UpdateHeader();
            StartSessionAfterTeardown();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e) => StopSession();

        private void UpdateHeader()
        {
            var tool = _tool;
            AgentNameText.Text = tool?.Name ?? "Agent";
            ToolTipService.SetToolTip(AgentNameText, tool?.FileName ?? string.Empty);
            ApprovalBadge.Visibility = tool?.SkipsApprovals == true ? Visibility.Visible : Visibility.Collapsed;
            McpBadge.Visibility = _mcp is not null ? Visibility.Visible : Visibility.Collapsed;

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
