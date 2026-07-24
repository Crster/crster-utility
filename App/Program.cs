using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Threading;

namespace App
{
    public static class Program
    {
        private const string MainInstanceKey = "CrsterUtility.Main";
        private static readonly object ActivationLock = new();
        private static App? _application;
        private static DispatcherQueue? _dispatcherQueue;
        private static bool _activationPending;

        [STAThread]
        private static void Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            var currentInstance = AppInstance.GetCurrent();
            var mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
            if (!mainInstance.IsCurrent)
            {
                mainInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs())
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                return;
            }

            mainInstance.Activated += MainInstance_Activated;
            Application.Start(initializationParameters =>
            {
                var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(dispatcherQueue));

                var application = new App();
                bool activationPending;
                lock (ActivationLock)
                {
                    _application = application;
                    _dispatcherQueue = dispatcherQueue;
                    activationPending = _activationPending;
                    _activationPending = false;
                }

                if (activationPending)
                    _ = dispatcherQueue.TryEnqueue(application.ActivateFromRedirectedLaunch);
            });
        }

        private static void MainInstance_Activated(object? sender, AppActivationArguments args)
        {
            App? application;
            DispatcherQueue? dispatcherQueue;
            lock (ActivationLock)
            {
                application = _application;
                dispatcherQueue = _dispatcherQueue;
                if (application is null || dispatcherQueue is null)
                {
                    _activationPending = true;
                    return;
                }
            }

            _ = dispatcherQueue.TryEnqueue(application.ActivateFromRedirectedLaunch);
        }
    }
}
