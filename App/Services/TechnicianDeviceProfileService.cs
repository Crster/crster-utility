using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace App.Services
{
    /// <summary>
    /// A short description of this PC. Windows guidance differs per edition and build, so the
    /// research planner is told exactly which machine the advice is for.
    /// </summary>
    internal static class TechnicianDeviceProfileService
    {
        private static readonly Lazy<string> Profile = new(Build, isThreadSafe: true);

        /// <summary>Collected once per app run; these values do not change while the app is open.</summary>
        public static string Current => Profile.Value;

        private static string Build()
        {
            var lines = new List<string>();
            var windows = ReadWindowsKey();
            var edition = windows.GetValueOrDefault("ProductName", "Windows");
            var display = windows.GetValueOrDefault("DisplayVersion", string.Empty);
            var build = windows.GetValueOrDefault("CurrentBuild", Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture));
            var updateBuild = windows.GetValueOrDefault("UBR", string.Empty);
            var fullBuild = string.IsNullOrEmpty(updateBuild) ? build : $"{build}.{updateBuild}";
            lines.Add($"- Windows: {edition}{(string.IsNullOrEmpty(display) ? string.Empty : $" {display}")}, build {fullBuild}");
            lines.Add($"- Architecture: {RuntimeInformation.OSArchitecture} ({RuntimeInformation.ProcessArchitecture} process)");

            var memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (memoryBytes > 0) lines.Add($"- Memory available to apps: {memoryBytes / (1024d * 1024 * 1024):0.#} GB");
            lines.Add($"- Logical processors: {Environment.ProcessorCount}");

            var graphics = ReadGraphicsAdapter();
            if (graphics is not null) lines.Add($"- Graphics: {graphics}");

            lines.Add($"- Locale: {CultureInfo.CurrentCulture.Name}, time zone {TimeZoneInfo.Local.Id}");
            lines.Add($"- Uptime: {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\ hh\\:mm}");
            return string.Join(Environment.NewLine, lines);
        }

        private static Dictionary<string, string> ReadWindowsKey()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key is null) return values;
                foreach (var name in new[] { "ProductName", "DisplayVersion", "CurrentBuild", "UBR" })
                {
                    if (key.GetValue(name) is { } value && value.ToString() is { Length: > 0 } text) values[name] = text;
                }
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
            {
            }
            return values;
        }

        private static string? ReadGraphicsAdapter()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
                var description = key?.GetValue("DriverDesc")?.ToString();
                if (string.IsNullOrWhiteSpace(description)) return null;
                var driverVersion = key?.GetValue("DriverVersion")?.ToString();
                return string.IsNullOrWhiteSpace(driverVersion) ? description : $"{description}, driver {driverVersion}";
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
