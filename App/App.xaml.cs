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
        }

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
