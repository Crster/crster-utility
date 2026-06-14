using App.Services;
using Microsoft.UI.Xaml;

namespace App
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }
        public static NotifyIconService? SystemTray { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            App.MainWindow = new Windows.MainWindow();
            App.MainWindow.Activate();

            App.SystemTray = new NotifyIconService(App.MainWindow);
            App.SystemTray.TrayLeftClick += SystemTray_TrayLeftClick;
        }

        private void SystemTray_TrayLeftClick()
        {
            App.MainWindow?.Activate();
        }
    }
}
