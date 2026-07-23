using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.System;

namespace App.Services
{
    public sealed partial class KeyboardService : IDisposable
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static partial IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
        [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UnhookWindowsHookEx(IntPtr hook);
        [LibraryImport("user32.dll")] private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial IntPtr GetModuleHandle(string? name);
        [LibraryImport("user32.dll")] private static partial short GetKeyState(int key);

        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private IntPtr _hook;
        private readonly LowLevelKeyboardProc _callback;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
        private GlobalShortcut? _snapshot;
        private GlobalShortcut? _caffeine;

        public event EventHandler? SnapshotPressed;
        public event EventHandler? CaffeinePressed;

        public KeyboardService(Microsoft.UI.Dispatching.DispatcherQueue dispatcher) { _dispatcher = dispatcher; _callback = HookCallback; }
        public void Configure(string snapshotShortcut, string caffeineShortcut)
        {
            _snapshot = GlobalShortcut.TryParse(snapshotShortcut, out var snapshot) ? snapshot : null;
            _caffeine = GlobalShortcut.TryParse(caffeineShortcut, out var caffeine) ? caffeine : null;
        }
        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Debug.WriteLine($"Failed to install keyboard hook. Error: {Marshal.GetLastWin32Error()}");
        }
        private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && (message == (IntPtr)WmKeyDown || message == (IntPtr)WmSysKeyDown))
            {
                var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(data).vkCode;
                if (_snapshot?.Matches(key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Menu), IsDown(VirtualKey.Shift), IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) == true)
                {
                    _dispatcher.TryEnqueue(() => SnapshotPressed?.Invoke(this, EventArgs.Empty));
                    return (IntPtr)1;
                }
                if (_caffeine?.Matches(key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Menu), IsDown(VirtualKey.Shift), IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) == true)
                {
                    _dispatcher.TryEnqueue(() => CaffeinePressed?.Invoke(this, EventArgs.Empty));
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, code, message, data);
        }
        private static bool IsDown(VirtualKey key) => (GetKeyState((int)key) & 0x8000) != 0;
        public void Dispose() { if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } GC.SuppressFinalize(this); }
    }

    internal sealed record GlobalShortcut(uint Key, bool Control, bool Alt, bool Shift, bool Windows)
    {
        public bool Matches(uint key, bool control, bool alt, bool shift, bool windows) => Key == key && Control == control && Alt == alt && Shift == shift && Windows == windows;
        public static bool TryParse(string value, out GlobalShortcut? shortcut)
        {
            shortcut = null;
            var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            var modifiers = parts.Take(parts.Length - 1).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (modifiers.Any(item => item is not ("Ctrl" or "Control" or "Alt" or "Shift" or "Win" or "Windows"))) return false;
            var keyText = parts[^1];
            var virtualKey = keyText switch
            {
                "PrintScreen" => 0x2Cu,
                "]" => 0xDDu,
                "[" => 0xDBu,
                _ => 0u
            };
            if (virtualKey == 0 && keyText.Length == 1 && char.IsDigit(keyText[0])) virtualKey = keyText[0];
            if (virtualKey != 0) shortcut = new(virtualKey, modifiers.Contains("Ctrl") || modifiers.Contains("Control"), modifiers.Contains("Alt"), modifiers.Contains("Shift"), modifiers.Contains("Win") || modifiers.Contains("Windows"));
            else if (Enum.TryParse<VirtualKey>(keyText, true, out var key) && key is not VirtualKey.None) shortcut = new((uint)key, modifiers.Contains("Ctrl") || modifiers.Contains("Control"), modifiers.Contains("Alt"), modifiers.Contains("Shift"), modifiers.Contains("Win") || modifiers.Contains("Windows"));
            return shortcut is not null;
        }
    }
}
