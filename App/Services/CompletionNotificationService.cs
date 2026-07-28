using Microsoft.UI.Windowing;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace App.Services
{
    internal static class CompletionNotificationService
    {
        // Section: Completion notifications
        internal static void ShowWhenMainWindowIsInactive(string title, string message)
        {
            if (!IsMainWindowInactive()) return;

            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }

        private static bool IsMainWindowInactive()
        {
            if (App.MainWindow is not Windows.MainWindow mainWindow) return false;

            return mainWindow.IsHiddenToTray
                || !mainWindow.Visible
                || mainWindow.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        }
    }
}
