using System;
using App.Services;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.UI.Xaml;

namespace App
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }
        public static NotifyIconService? SystemTray { get; private set; }
        internal static SecureSettingsService Settings { get; } = new();
        private KeyboardService? _keyboard;
        private Windows.FirstRunWindow? _firstRunWindow;
        private bool _redirectedLaunchPending;

        public App()
        {
            InitializeComponent();
            UnhandledException += (_, args) =>
            {
                if (IsHostedTerminalFocusFault(args.Exception))
                {
                    // Known defect in the hosted console control: it hands focus to its native child
                    // by cancelling the XAML focus move, and that cancel is rejected once the control
                    // has left the visual tree. The throw escapes its own event handler, so we cannot
                    // catch it at the call site. Nothing is left in a bad state, so the app must not
                    // die for it. CliAgentPanel.StopSession already stops it being raised.
                    args.Handled = true;
                }
            };
        }

        /// <summary>True for the one failure the hosted terminal control raises from its own
        /// GettingFocus handler after it has been detached. Deliberately narrow: it must not swallow
        /// any other ArgumentException.</summary>
        private static bool IsHostedTerminalFocusFault(Exception? exception) =>
            exception is ArgumentException
            && exception.StackTrace?.Contains("TerminalControl_GettingFocus", StringComparison.Ordinal) == true;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var isStartupActivation = AppInstance.GetCurrent().GetActivatedEventArgs()?.Kind == ExtendedActivationKind.StartupTask;
            Settings.Load();
            if (!Settings.IsConfigured)
            {
                _firstRunWindow = new Windows.FirstRunWindow();
                _firstRunWindow.SetupCompleted += (_, _) =>
                {
                    _firstRunWindow = null;
                    InitializeMainWindow(false);
                };
                _firstRunWindow.Activate();
                return;
            }
            InitializeMainWindow(isStartupActivation);
        }

        private void InitializeMainWindow(bool isStartupActivation)
        {
            AppNotificationManager.Default.Register();
            App.MainWindow = new Windows.MainWindow();
            if (isStartupActivation)
                (App.MainWindow as Windows.MainWindow)?.HideToTray();
            else
                App.MainWindow.Activate();

            App.SystemTray = new NotifyIconService(App.MainWindow);
            App.SystemTray.TrayLeftClick += SystemTray_TrayLeftClick;
            App.SystemTray.TrayShowRequested += SystemTray_TrayLeftClick;
            App.SystemTray.TrayExitRequested += () => (App.MainWindow as Windows.MainWindow)?.ExitFromTray();
            _keyboard = new KeyboardService(App.MainWindow.DispatcherQueue);
            _keyboard.Configure(Settings.Current.SnapshotShortcut, Settings.Current.CaffeineShortcut);
            _keyboard.SnapshotPressed += (_, _) => (App.MainWindow as Windows.MainWindow)?.CaptureSnapshotFromHotkey();
            _keyboard.CaffeinePressed += (_, _) => (App.MainWindow as Windows.MainWindow)?.ToggleCaffeineFromHotkey();
            _keyboard.CopilotPressed += (_, _) => (App.MainWindow as Windows.MainWindow)?.ShowFromActivation();
            _keyboard.Start();
            Settings.Changed += (_, settings) => _keyboard.Configure(settings.SnapshotShortcut, settings.CaffeineShortcut);

            if (_redirectedLaunchPending)
            {
                _redirectedLaunchPending = false;
                (App.MainWindow as Windows.MainWindow)?.ShowFromActivation();
            }
        }

        internal void ActivateFromRedirectedLaunch()
        {
            if (App.MainWindow is Windows.MainWindow mainWindow)
                mainWindow.ShowFromActivation();
            else
                _redirectedLaunchPending = true;
        }

        private void SystemTray_TrayLeftClick()
        {
            if (App.MainWindow is Windows.MainWindow mainWindow) mainWindow.ShowFromTray();
        }
    }
}
