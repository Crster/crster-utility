using System;
using System.Runtime.InteropServices;

namespace App.Services
{
    public static class NativeInputService
    {
        private const uint InputMouse = 0;
        private const uint InputKeyboard = 1;
        private const uint MouseeventfWheel = 0x0800;
        private const uint KeyeventfKeyup = 0x0002;
        private const ushort VkMenu = 0x12;
        private const ushort VkControl = 0x11;
        private const ushort VkTab = 0x09;
        private const int SmXVirtualScreen = 76;
        private const int SmYVirtualScreen = 77;
        private const int SmCxVirtualScreen = 78;
        private const int SmCyVirtualScreen = 79;
        private const int GwlExStyle = -20;
        private const nint WsExToolWindow = 0x00000080;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const int SwRestore = 9;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr(IntPtr windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr(IntPtr windowHandle, int index, nint value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public readonly record struct CursorPosition(int X, int Y);
        public readonly record struct ScreenBounds(int Left, int Top, int Width, int Height)
        {
            public int Right => Left + Width - 1;
            public int Bottom => Top + Height - 1;
        }

        public static bool TryExcludeForegroundWindowFromTaskSwitcher(out IntPtr windowHandle, out nint originalExtendedStyle)
        {
            windowHandle = GetForegroundWindow();
            if (windowHandle == IntPtr.Zero)
            {
                originalExtendedStyle = default;
                return false;
            }

            originalExtendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle);
            SetWindowLongPtr(windowHandle, GwlExStyle, originalExtendedStyle | WsExToolWindow);
            RefreshWindowFrame(windowHandle);
            return true;
        }

        public static void RestoreTaskSwitcherVisibility(IntPtr windowHandle, nint originalExtendedStyle)
        {
            if (windowHandle == IntPtr.Zero)
                return;

            SetWindowLongPtr(windowHandle, GwlExStyle, originalExtendedStyle);
            RefreshWindowFrame(windowHandle);
        }

        public static void ActivateWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return;

            ShowWindow(windowHandle, SwRestore);
            var foregroundWindow = GetForegroundWindow();
            var currentThreadId = GetCurrentThreadId();
            var foregroundThreadId = foregroundWindow == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foregroundWindow, out _);
            var attached = foregroundThreadId != 0 && foregroundThreadId != currentThreadId &&
                AttachThreadInput(currentThreadId, foregroundThreadId, true);

            try
            {
                BringWindowToTop(windowHandle);
                SetForegroundWindow(windowHandle);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }

        public static bool TryGetCursorPosition(out CursorPosition position)
        {
            if (!GetCursorPos(out var point))
            {
                position = default;
                return false;
            }

            position = new CursorPosition(point.X, point.Y);
            return true;
        }

        public static bool TryCreateCenteredScreenBounds(double screenCoverage, out ScreenBounds bounds)
        {
            var screenLeft = GetSystemMetrics(SmXVirtualScreen);
            var screenTop = GetSystemMetrics(SmYVirtualScreen);
            var screenWidth = GetSystemMetrics(SmCxVirtualScreen);
            var screenHeight = GetSystemMetrics(SmCyVirtualScreen);

            if (screenWidth <= 0 || screenHeight <= 0 || screenCoverage <= 0 || screenCoverage > 1)
            {
                bounds = default;
                return false;
            }

            var width = Math.Max(1, (int)Math.Round(screenWidth * screenCoverage));
            var height = Math.Max(1, (int)Math.Round(screenHeight * screenCoverage));
            var left = screenLeft + ((screenWidth - width) / 2);
            var top = screenTop + ((screenHeight - height) / 2);
            bounds = new ScreenBounds(left, top, width, height);
            return true;
        }

        public static bool MoveCursorNearCurrentPosition(Random random, ScreenBounds bounds, int maximumDistance, out CursorPosition position)
        {
            if (maximumDistance <= 0 || !TryGetCursorPosition(out var currentPosition))
            {
                position = default;
                return false;
            }

            var distance = random.Next(1, maximumDistance + 1);
            var angle = random.NextDouble() * Math.Tau;
            var x = Math.Clamp(currentPosition.X + (int)Math.Round(Math.Cos(angle) * distance), bounds.Left, bounds.Right);
            var y = Math.Clamp(currentPosition.Y + (int)Math.Round(Math.Sin(angle) * distance), bounds.Top, bounds.Bottom);
            if (!SetCursorPos(x, y))
            {
                position = default;
                return false;
            }

            position = new CursorPosition(x, y);
            return true;
        }

        public static void Scroll(int wheelDelta)
        {
            Send(new Input
            {
                Type = InputMouse,
                Data = new InputUnion { Mouse = new MouseInput { MouseData = wheelDelta, Flags = MouseeventfWheel } }
            });
        }

        public static void SendAltTab() => SendModifiedKey(VkMenu, VkTab);

        public static void SendCtrlTab() => SendModifiedKey(VkControl, VkTab);

        private static void RefreshWindowFrame(IntPtr windowHandle) =>
            SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        private static void SendModifiedKey(ushort modifier, ushort key)
        {
            Send(
                KeyDown(modifier),
                KeyDown(key),
                KeyUp(key),
                KeyUp(modifier));
        }

        private static Input KeyDown(ushort key) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key } }
        };

        private static Input KeyUp(ushort key) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = KeyeventfKeyup } }
        };

        private static void Send(params Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;

            [FieldOffset(0)]
            public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public int MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }
    }
}
