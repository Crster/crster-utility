using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace App.Services
{
    internal sealed partial class TransparentBackdrop : SystemBackdrop
    {
        private static readonly Lazy<global::Windows.UI.Composition.Compositor> Compositor = new(() =>
        {
            WindowsSystemDispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();
            return new global::Windows.UI.Composition.Compositor();
        });

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            connectedTarget.SystemBackdrop = Compositor.Value.CreateColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            disconnectedTarget.SystemBackdrop = null;
        }
    }

    internal static class WindowsSystemDispatcherQueueHelper
    {
        private static object? _dispatcherQueueController;

        public static void EnsureWindowsSystemDispatcherQueueController()
        {
            if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is not null || _dispatcherQueueController is not null)
                return;

            var options = new DispatcherQueueOptions
            {
                Size = Marshal.SizeOf<DispatcherQueueOptions>(),
                ThreadType = 2,
                ApartmentType = 2
            };

            CreateDispatcherQueueController(options, ref _dispatcherQueueController);
        }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(
            DispatcherQueueOptions options,
            [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions
        {
            public int Size;
            public int ThreadType;
            public int ApartmentType;
        }
    }
}
