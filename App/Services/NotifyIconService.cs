using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using System.IO;
using WinRT.Interop;
using Microsoft.UI.Xaml.Controls;

namespace App.Services
{
    public partial class NotifyIconService : IDisposable
    {
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpNid);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")] private static partial IntPtr CreatePopupMenu();
        [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AppendMenu(IntPtr menu, uint flags, nuint id, string text);
        [LibraryImport("user32.dll")] private static partial uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rectangle);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyMenu(IntPtr menu);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out POINT point);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(IntPtr window);
        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr CreateIcon(
            IntPtr instance,
            int width,
            int height,
            byte planes,
            byte bitsPerPixel,
            byte[] andBits,
            byte[] xorBits);
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate newProc);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const int WM_GETICON = 0x7F;
        private const int ICON_SMALL = 0;

        private const int WM_USER_TRAY_ICON = 0x4000;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int GWLP_WNDPROC = -4;
        private const uint MF_STRING = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;
        private const nuint ShowCommand = 1;
        private const nuint ExitCommand = 2;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;

            public uint dwState;
            public uint dwStateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;

            public uint uVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;

            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        private readonly Window window;
        private readonly DispatcherTimer _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private NOTIFYICONDATA _nid;
        private WndProcDelegate? newWndProc;
        private IntPtr oldWndProc;
        private IntPtr _appIcon;
        private IntPtr _transparentIcon;
        private bool _showTransparentIcon;
        private bool _isAdded;

        public event Action? TrayLeftClick;
        public event Action? TrayRightClick;
        public event Action? TrayDoubleClick;
        public event Action? TrayShowRequested;
        public event Action? TrayExitRequested;

        private void OnTrayLeftClick() => TrayLeftClick?.Invoke();
        private void OnTrayRightClick()
        {
            TrayRightClick?.Invoke();
            ShowContextMenu();
        }
        private void OnTrayDoubleClick() => TrayDoubleClick?.Invoke();

        public NotifyIconService(Window window)
        {
            this.window = window;
            _pulseTimer.Tick += PulseTimer_Tick;
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(window);
            IntPtr hIcon = SendMessage(hWnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
            _appIcon = hIcon;
            _transparentIcon = CreateTransparentIcon();

            _nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = hWnd,
                uID = 1,
                uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP,
                uCallbackMessage = WM_USER_TRAY_ICON,
                hIcon = hIcon,
                szTip = window.Title
            };

            int result = Shell_NotifyIcon(NIM_ADD, ref _nid);
            _isAdded = result != 0;

            if (_isAdded)
            {
                newWndProc = CustomWndProc;
                oldWndProc = SetWindowLongPtr(hWnd, GWLP_WNDPROC, newWndProc);
            }
        }

        private static IntPtr CreateTransparentIcon()
        {
            const int iconSize = 16;
            const int maskBytes = iconSize * iconSize / 8;
            var transparentMask = new byte[maskBytes];
            Array.Fill(transparentMask, byte.MaxValue);
            return CreateIcon(
                IntPtr.Zero,
                iconSize,
                iconSize,
                1,
                1,
                transparentMask,
                new byte[maskBytes]);
        }

        public void SetProcessing(bool isProcessing)
        {
            if (!_isAdded) return;

            if (isProcessing)
            {
                if (_pulseTimer.IsEnabled) return;
                _showTransparentIcon = false;
                _pulseTimer.Start();
                PulseTimer_Tick(null, EventArgs.Empty);
                return;
            }

            _pulseTimer.Stop();
            _showTransparentIcon = false;
            SetIcon(_appIcon);
        }

        private void PulseTimer_Tick(object? sender, object e)
        {
            _showTransparentIcon = !_showTransparentIcon;
            SetIcon(_showTransparentIcon && _transparentIcon != IntPtr.Zero ? _transparentIcon : _appIcon);
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_USER_TRAY_ICON)
            {
                switch ((int)lParam)
                {
                    case WM_LBUTTONUP:
                        OnTrayLeftClick();
                        break;
                    case WM_RBUTTONUP:
                        OnTrayRightClick();
                        break;
                    case WM_LBUTTONDBLCLK:
                        OnTrayDoubleClick();
                        break;
                }
            }

            return CallWindowProc(oldWndProc, hWnd, msg, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero || !GetCursorPos(out var point)) return;
            try
            {
                _ = AppendMenu(menu, MF_STRING, ShowCommand, "Show");
                _ = AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
                _ = AppendMenu(menu, MF_STRING, ExitCommand, "Exit");
                var hwnd = WindowNative.GetWindowHandle(window);
                _ = SetForegroundWindow(hwnd);
                var selected = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.X, point.Y, 0, hwnd, IntPtr.Zero);
                if (selected == ShowCommand) TrayShowRequested?.Invoke();
                else if (selected == ExitCommand) TrayExitRequested?.Invoke();
            }
            finally { _ = DestroyMenu(menu); }
        }

        public void SetTitle(string title)
        {
            _nid.szTip = title;
            _nid.uFlags = NIF_TIP; // ensure tooltip flag is set
            int result = Shell_NotifyIcon(0x00000001 /* NIM_MODIFY */, ref _nid);

            if (result == 0)
            {
                throw new InvalidOperationException("Failed to update tray icon title.");
            }
        }

        public void SetIcon(IntPtr hIcon)
        {
            _nid.hIcon = hIcon;
            _nid.uFlags = NIF_ICON; // ensure icon flag is set
            int result = Shell_NotifyIcon(0x00000001 /* NIM_MODIFY */, ref _nid);

            if (result == 0)
            {
                throw new InvalidOperationException("Failed to update tray icon icon.");
            }
        }

        public void Dispose()
        {
            _pulseTimer.Stop();
            if (_isAdded)
            {
                _ = Shell_NotifyIcon(NIM_DELETE, ref _nid);
                _isAdded = false;
            }
            if (_transparentIcon != IntPtr.Zero)
            {
                _ = DestroyIcon(_transparentIcon);
                _transparentIcon = IntPtr.Zero;
            }
        }
    }
}
