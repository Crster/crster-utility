using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace App.Services
{
    internal static class NotebookShortcutService
    {
        private const string UppercaseCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        private const string LowercaseCharacters = "abcdefghijkmnopqrstuvwxyz";
        private const string DigitCharacters = "23456789";
        private const string SymbolCharacters = "!@#$%&*?";
        private const string PasswordCharacters = UppercaseCharacters + LowercaseCharacters + DigitCharacters + SymbolCharacters;

        internal static string CreateSecretKey() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        internal static string CreateReadablePassword()
        {
            var characters = new char[8];
            characters[0] = RandomCharacter(UppercaseCharacters);
            characters[1] = RandomCharacter(LowercaseCharacters);
            characters[2] = RandomCharacter(DigitCharacters);
            characters[3] = RandomCharacter(SymbolCharacters);
            for (var index = 4; index < characters.Length; index++)
                characters[index] = RandomCharacter(PasswordCharacters);

            for (var index = characters.Length - 1; index > 0; index--)
            {
                var target = RandomNumberGenerator.GetInt32(index + 1);
                (characters[index], characters[target]) = (characters[target], characters[index]);
            }

            return new string(characters);
        }

        internal static async Task<string> GetSystemUsageTextAsync()
        {
            double? cpuPercent = null;
            if (GetSystemTimes(out var idleBefore, out var kernelBefore, out var userBefore))
            {
                await Task.Delay(250);
                if (GetSystemTimes(out var idleAfter, out var kernelAfter, out var userAfter))
                {
                    var idle = ToUInt64(idleAfter) - ToUInt64(idleBefore);
                    var total = ToUInt64(kernelAfter) - ToUInt64(kernelBefore)
                        + ToUInt64(userAfter) - ToUInt64(userBefore);
                    cpuPercent = total == 0 ? 0 : 100d * (total - idle) / total;
                }
            }

            var memory = new Microsoft.VisualBasic.Devices.ComputerInfo();
            var totalMemory = memory.TotalPhysicalMemory;
            var usedMemory = totalMemory - memory.AvailablePhysicalMemory;
            var memoryPercent = totalMemory == 0 ? 0 : 100d * usedMemory / totalMemory;
            const double bytesPerGigabyte = 1024d * 1024d * 1024d;
            var cpuText = cpuPercent is null ? "unavailable" : $"{cpuPercent:F0}%";

            return $"RAM: {usedMemory / bytesPerGigabyte:F1} / {totalMemory / bytesPerGigabyte:F1} GB ({memoryPercent:F0}%) | CPU: {cpuText}";
        }

        private static char RandomCharacter(string characters) =>
            characters[RandomNumberGenerator.GetInt32(characters.Length)];

        private static ulong ToUInt64(FILETIME value) =>
            ((ulong)value.HighDateTime << 32) | value.LowDateTime;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            internal uint LowDateTime;
            internal uint HighDateTime;
        }
    }
}
