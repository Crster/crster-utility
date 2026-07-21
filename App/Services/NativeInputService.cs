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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        public readonly record struct CursorPosition(int X, int Y);

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

        public static bool MoveCursorToRandomPosition(Random random, out CursorPosition position)
        {
            var screenLeft = GetSystemMetrics(SmXVirtualScreen);
            var screenTop = GetSystemMetrics(SmYVirtualScreen);
            var screenWidth = GetSystemMetrics(SmCxVirtualScreen);
            var screenHeight = GetSystemMetrics(SmCyVirtualScreen);

            if (screenWidth <= 0 || screenHeight <= 0)
            {
                position = default;
                return false;
            }

            var x = random.Next(screenLeft, screenLeft + screenWidth);
            var y = random.Next(screenTop, screenTop + screenHeight);
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
