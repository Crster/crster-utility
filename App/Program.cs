using App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace App
{
    public static class Program
    {
        private const string MainInstanceKey = "CrsterUtility.Main";
        private static readonly object ActivationLock = new();
        private static App? _application;
        private static DispatcherQueue? _dispatcherQueue;
        private static bool _activationPending;

        [DllImport("ole32.dll")]
        private static extern int CoWaitForMultipleObjects(
            uint flags,
            uint timeoutMilliseconds,
            uint handleCount,
            IntPtr[] handles,
            out uint index);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);

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
                RedirectActivationTo(activationArguments, mainInstance);
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

        private static void RedirectActivationTo(AppActivationArguments activationArguments, AppInstance mainInstance)
        {
            using var redirectCompleted = new EventWaitHandle(false, EventResetMode.ManualReset);
            _ = Task.Run(async () =>
            {
                try
                {
                    await mainInstance.RedirectActivationToAsync(activationArguments);
                }
                catch (Exception exception)
                {
                }
                finally
                {
                    redirectCompleted.Set();
                }
            });

            const uint infinite = 0xFFFFFFFF;
            var handles = new[] { redirectCompleted.SafeWaitHandle.DangerousGetHandle() };
            var waitResult = CoWaitForMultipleObjects(0, infinite, 1, handles, out _);
            if (waitResult < 0)

            if (!AllowSetForegroundWindow(mainInstance.ProcessId))

            Thread.Sleep(250);
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
        }
    }
}
