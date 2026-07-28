using App.Services;
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
            if (args.Length == 3 && args[0].Equals(ElevatedCommandService.HelperArgument, StringComparison.Ordinal))
            {
                Environment.ExitCode = ElevatedCommandService.RunHelper(args[1], args[2]);
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();

            var currentInstance = AppInstance.GetCurrent();
            var mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
            if (!mainInstance.IsCurrent)
            {
                var activationArguments = currentInstance.GetActivatedEventArgs();
                var redirectThread = new Thread(() =>
                {
                    try
                    {
                        mainInstance.RedirectActivationToAsync(activationArguments)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine($"Activation redirection failed: {exception}");
                    }
                })
                {
                    IsBackground = false,
                    Name = "ActivationRedirect"
                };
                redirectThread.SetApartmentState(ApartmentState.MTA);
                redirectThread.Start();
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
                    EnqueueActivation(dispatcherQueue, application);
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

            EnqueueActivation(dispatcherQueue, application);
        }

        private static void EnqueueActivation(DispatcherQueue dispatcherQueue, App application)
        {
            if (!dispatcherQueue.TryEnqueue(application.ActivateFromRedirectedLaunch))
                System.Diagnostics.Debug.WriteLine("Activation could not be queued on the UI thread.");
        }
    }
}
