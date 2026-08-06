using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace App.Services
{
    /// <summary>Finds programs on this machine: on PATH, in System32, or in a given list of folders.</summary>
    internal static class ExecutableLocator
    {
        /// <summary>Returns the full path of an executable found in System32 or on PATH, or null.</summary>
        public static string? Find(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName)) return null;

            var systemPath = Path.Combine(Environment.SystemDirectory, executableName);
            if (File.Exists(systemPath)) return systemPath;

            return FindIn(PathDirectories(), executableName);
        }

        /// <summary>Returns the full path of the first folder that contains the file, or null.</summary>
        public static string? FindIn(IEnumerable<string> directories, string fileName)
        {
            foreach (var directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;

                string candidate;
                try
                {
                    candidate = Path.Combine(directory, fileName);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry must not stop the search.
                    continue;
                }

                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        /// <summary>The folders listed in the PATH environment variable, in order.</summary>
        public static IReadOnlyList<string> PathDirectories() =>
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        /// <summary>Windows PowerShell 5, preferring the known System32 location over PATH.</summary>
        public static string? FindWindowsPowerShell()
        {
            var windowsDirectory = Path.GetDirectoryName(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(windowsDirectory)) return Find("powershell.exe");

            var powerShellPath = Path.Combine(
                windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(powerShellPath) ? powerShellPath : Find("powershell.exe");
        }
    }
}
