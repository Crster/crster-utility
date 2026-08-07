using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace App.Services
{
    internal sealed class WindowsCleanupService
    {
        public async Task<CleanupResult> RunAsync(
            CleanupOptions options,
            IProgress<CleanupStepProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = new CleanupResult();

            // Safe (non-elevated) operations
            var safeSteps = new List<(string Name, Action<CleanupResult> Action)>();

            if (options.ClearTempFolders)
                safeSteps.Add(("User and system temp folders", r => ClearTempFolders(r)));
            if (options.ClearRecycleBin)
                safeSteps.Add(("Recycle Bin", r => ClearRecycleBin(r)));
            if (options.ClearExplorerHistory)
                safeSteps.Add(("Explorer file history", r => ClearExplorerHistory(r)));
            if (options.ClearRecentDocuments)
                safeSteps.Add(("Recent documents", r => ClearRecentDocuments(r)));
            if (options.ClearAppLogs)
                safeSteps.Add(("Application logs and crash dumps", r => ClearAppLogs(r)));
            if (options.ClearWindowsErrorReports)
                safeSteps.Add(("Windows Error Reports", r => ClearWindowsErrorReports(r)));
            if (options.ClearThumbnailCache)
                safeSteps.Add(("Thumbnail cache", r => ClearThumbnailCache(r)));
            if (options.ClearIconCache)
                safeSteps.Add(("Icon cache", r => ClearIconCache(r)));
            if (options.ClearWindowsUpdateCache)
                safeSteps.Add(("Windows Update download cache", r => ClearWindowsUpdateCache(r)));
            if (options.ClearDeliveryOptimization)
                safeSteps.Add(("Delivery Optimization files", r => ClearDeliveryOptimization(r)));
            if (options.ClearFontCache)
                safeSteps.Add(("Font cache", r => ClearFontCache(r)));
            if (options.ClearPrefetch)
                safeSteps.Add(("Prefetch files", r => ClearPrefetch(r)));
            if (options.ClearMemoryDumps)
                safeSteps.Add(("Memory dump files", r => ClearMemoryDumps(r)));
            if (options.ClearEmptyFolders)
                safeSteps.Add(("Empty folders in home directory", r => ClearEmptyFoldersInHome(r)));
            if (options.ClearOrphanFiles)
                safeSteps.Add(("Old temp and orphan files", r => ClearOrphanFiles(r)));

            foreach (var (name, action) in safeSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new CleanupStepProgress(name, "Running...", CleanupStepState.Running));
                try
                {
                    await Task.Run(() => action(result), cancellationToken);
                    progress.Report(new CleanupStepProgress(name, "Done", CleanupStepState.Succeeded));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress.Report(new CleanupStepProgress(name, $"Skipped: {ex.Message}", CleanupStepState.Skipped));
                }
            }

            // Elevated operations — batched into a single UAC prompt
            var needsElevation = options.ClearRegistryKeys
                || options.ClearDriverLogs
                || options.ClearEventLogs
                || options.ClearEnvironmentPath
                || options.ClearDnsCache
                || options.ClearSetupLogs;

            if (needsElevation)
            {
                progress.Report(new CleanupStepProgress("Elevated cleanup", "Requesting administrator access...", CleanupStepState.Running));
                try
                {
                    var script = BuildElevatedScript(options);
                    var elevatedResult = await ElevatedCommandService.RunAsync(
                        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"{EscapeForCmd(script)}\"",
                        Environment.SystemDirectory,
                        cancellationToken);

                    if (elevatedResult.ExitCode == 0)
                    {
                        ParseElevatedOutput(elevatedResult.Stdout, result, options, progress);
                    }
                    else
                    {
                        var error = string.IsNullOrWhiteSpace(elevatedResult.Stderr)
                            ? elevatedResult.Stdout
                            : elevatedResult.Stderr;
                        progress.Report(new CleanupStepProgress("Elevated cleanup", $"Failed: {error}", CleanupStepState.Failed));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress.Report(new CleanupStepProgress("Elevated cleanup", $"Failed: {ex.Message}", CleanupStepState.Failed));
                }
            }

            return result;
        }

        #region Safe operations

        private static void ClearTempFolders(CleanupResult result)
        {
            var windowsRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var paths = new[]
            {
                Path.GetTempPath(),
                Path.Combine(windowsRoot, "Temp")
            };

            foreach (var path in paths)
                DeleteContents(path, result);
        }

        private static void ClearRecycleBin(CleanupResult result)
        {
            // Use the Shell COM approach via PowerShell for a clean empty
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Clear-RecycleBin -Force -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(30_000);
            result.ItemsCleared++;
        }

        private static void ClearExplorerHistory(CleanupResult result)
        {
            // Clear Run MRU (the "Run" dialog history)
            TryDeleteRegistryValues(Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU");

            // Clear address bar / typed paths history
            TryDeleteRegistryValues(Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths");

            // Clear the Explorer RecentDocs MRU (recent files list in the registry)
            TryDeleteRegistryValues(Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs");

            // Do NOT delete .lnk files from the Recent folder: pinned Quick Access
            // folders live there, and deleting them removes the user's pins.
            // Do NOT touch AutomaticDestinations / CustomDestinations: those jump
            // list files also hold pinned Quick Access and taskbar entries.
        }

        private static void ClearRecentDocuments(CleanupResult result)
        {
            // Clear only the RecentDocs MRU registry list. Do not delete .lnk files
            // from the Recent folder: they include pinned Quick Access items.
            TryDeleteRegistryValues(Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs");
        }

        private static void ClearAppLogs(CleanupResult result)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Common app log locations
            var logLocations = new[]
            {
                Path.Combine(localAppData, "Temp"),
                Path.Combine(localAppData, "CrashDumps"),
                Path.Combine(localAppData, "D3DSCache"),
                Path.Combine(localAppData, "Microsoft", "Windows", "WER"),
                Path.Combine(localAppData, "Microsoft", "Terminal Server Client", "Cache"),
            };

            foreach (var path in logLocations)
            {
                if (!Directory.Exists(path)) continue;
                DeleteFilesRecursive(path, "*.log", result);
                DeleteFilesRecursive(path, "*.etl", result);
                DeleteFilesRecursive(path, "*.dmp", result);
            }
        }

        private static void ClearWindowsErrorReports(CleanupResult result)
        {
            var werPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "WER"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "WER", "ReportQueue"),
            };

            foreach (var path in werPaths)
                DeleteContents(path, result);
        }

        private static void ClearThumbnailCache(CleanupResult result)
        {
            var explorerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Explorer");

            if (!Directory.Exists(explorerPath)) return;
            DeleteFiles(explorerPath, "thumbcache_*.db", result);
            DeleteFiles(explorerPath, "iconcache_*.db", result);
        }

        private static void ClearIconCache(CleanupResult result)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var iconCache = Path.Combine(localAppData, "IconCache.db");

            try
            {
                if (File.Exists(iconCache))
                {
                    File.Delete(iconCache);
                    result.FilesDeleted++;
                }
            }
            catch { }

            // Also clear the Windows Explorer icon cache
            var explorerPath = Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
            DeleteFiles(explorerPath, "iconcache_*.db", result);
        }

        private static void ClearWindowsUpdateCache(CleanupResult result)
        {
            var windowsRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var downloadPath = Path.Combine(windowsRoot, "SoftwareDistribution", "Download");
            DeleteContents(downloadPath, result);
        }

        private static void ClearDeliveryOptimization(CleanupResult result)
        {
            var windowsRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var doPath = Path.Combine(windowsRoot, "SoftwareDistribution", "DataStore", "Logs");
            DeleteFiles(doPath, "*.log", result);
        }

        private static void ClearFontCache(CleanupResult result)
        {
            var windowsRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var fontCachePath = Path.Combine(windowsRoot, "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache");
            DeleteFiles(fontCachePath, "*.dat", result);
        }

        private static void ClearPrefetch(CleanupResult result)
        {
            var windowsRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var prefetchPath = Path.Combine(windowsRoot, "Prefetch");
            DeleteFiles(prefetchPath, "*.pf", result);
        }

        private static void ClearMemoryDumps(CleanupResult result)
        {
            var windowsRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

            // System memory.dmp
            var memoryDmp = Path.Combine(windowsRoot, "MEMORY.DMP");
            TryDeleteFile(memoryDmp, result);

            // Minidumps
            var minidumpPath = Path.Combine(windowsRoot, "Minidump");
            DeleteFiles(minidumpPath, "*.dmp", result);

            // User crash dumps
            var userCrashDumps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrashDumps");
            DeleteFiles(userCrashDumps, "*.dmp", result);
        }

        private static void ClearEmptyFoldersInHome(CleanupResult result)
        {
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Directory.Exists(homePath)) return;

            // Skip known important directories
            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AppData", "Desktop", "Documents", "Downloads", "Music",
                "Pictures", "Videos", ".crster", ".vscode", ".git",
                "node_modules", ".nuget", ".dotnet", "scoop"
            };

            ClearEmptyFoldersRecursive(homePath, result, 0, 3, skipDirs);
        }

        private static void ClearOrphanFiles(CleanupResult result)
        {
            // Old temp files older than 7 days
            var tempPath = Path.GetTempPath();
            if (Directory.Exists(tempPath))
            {
                var cutoff = DateTime.Now.AddDays(-7);
                foreach (var pattern in new[] { "*.tmp", "*.temp", "*.bak", "*.old", "*.chk" })
                {
                    foreach (var file in SafeEnumerateFiles(tempPath, pattern))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.LastAccessTime < cutoff)
                            {
                                info.Delete();
                                result.FilesDeleted++;
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        #endregion

        #region Elevated script

        private static string BuildElevatedScript(CleanupOptions options)
        {
            var parts = new List<string>();

            parts.Add("$ErrorActionPreference = 'SilentlyContinue'");
            parts.Add("$results = @{}");

            if (options.ClearRegistryKeys)
            {
                parts.Add(@"
$regCleaned = 0
$runPaths = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run64'
)
foreach ($regPath in $runPaths) {
    if (Test-Path $regPath) {
        $props = Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue
        if ($props) {
            $props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' -and $_.Name -ne '(default)' } | ForEach-Object {
                $val = $_.Value
                $exePath = $null
                if ($val -is [string]) {
                    $trimmed = $val.Trim()
                    if ($trimmed.StartsWith('""')) {
                        $end = $trimmed.IndexOf('""', 2)
                        if ($end -gt 2) { $exePath = $trimmed.Substring(2, $end - 2) }
                    } else {
                        $exePath = $trimmed.Split(' ')[0]
                    }
                } elseif ($val -is [byte[]]) {
                    # StartupApproved disabled entries — skip
                    return
                }
                if ($exePath -and -not (Test-Path $exePath -ErrorAction SilentlyContinue)) {
                    Remove-ItemProperty -Path $regPath -Name $_.Name -ErrorAction SilentlyContinue
                    $regCleaned++
                }
            }
        }
    }
}

# Clear orphan Uninstall entries
$uninstallPaths = @(
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
foreach ($uPath in $uninstallPaths) {
    Get-ItemProperty $uPath -ErrorAction SilentlyContinue | ForEach-Object {
        $installLoc = $_.InstallLocation
        $uninstallStr = $_.UninstallString
        if ($installLoc -and -not (Test-Path $installLoc -ErrorAction SilentlyContinue)) {
            # Orphan install location — leave the registry key alone; only flag it
        }
    }
}

# Clear orphan FileExts associations pointing to missing apps
# (Too risky — skip)

# Clear orphan COM/CLSID InprocServer32 pointing to missing DLLs
# (Too risky — skip)

# Clear orphan MUICache
$muiPath = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache'
if (Test-Path $muiPath) {
    $muiProps = Get-ItemProperty -Path $muiPath -ErrorAction SilentlyContinue
    if ($muiProps) {
        $muiProps.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object {
            # MUICache entries are named like <exepath>.FriendlyAppName
            $entryName = $_.Name
            if ($entryName -match '^([A-Z]:\\.+)\\.[A-Za-z]+$') {
                $exeFile = $Matches[1]
                if (-not (Test-Path $exeFile -ErrorAction SilentlyContinue)) {
                    Remove-ItemProperty -Path $muiPath -Name $entryName -ErrorAction SilentlyContinue
                    $regCleaned++
                }
            }
        }
    }
}

$results['RegistryKeys'] = $regCleaned
Write-Output ""REGISTRY_CLEANED=$regCleaned""
");
            }

            if (options.ClearDriverLogs)
            {
                parts.Add(@"
$driverLogCleaned = 0
$driverLogPaths = @(
    'C:\Windows\Panther',
    'C:\Windows\Logs\CBS',
    'C:\Windows\Logs\DISM',
    'C:\Windows\Logs\MoSetup',
    'C:\Windows\Logs\SetupCleanup',
    'C:\Windows\Logs\WindowsUpdate'
)
foreach ($dp in $driverLogPaths) {
    if (Test-Path $dp) {
        Get-ChildItem -Path $dp -Include '*.log','*.etl','*.evtx' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item $_.FullName -Force; $driverLogCleaned++ } catch {}
        }
    }
}
$results['DriverLogs'] = $driverLogCleaned
Write-Output ""DRIVER_LOGS=$driverLogCleaned""
");
            }

            if (options.ClearSetupLogs)
            {
                parts.Add(@"
$setupCleaned = 0
$setupPaths = @(
    'C:\Windows\Panther',
    'C:\Windows\Logs\SetupCleanup',
    'C:\Windows\Logs\MoSetup'
)
foreach ($sp in $setupPaths) {
    if (Test-Path $sp) {
        Get-ChildItem -Path $sp -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item $_.FullName -Force; $setupCleaned++ } catch {}
        }
    }
}
$results['SetupLogs'] = $setupCleaned
Write-Output ""SETUP_LOGS=$setupCleaned""
");
            }

            if (options.ClearEventLogs)
            {
                parts.Add(@"
$eventsCleared = 0
try {
    $logs = wevtutil el
    foreach ($log in $logs) {
        if ($log) {
            wevtutil cl $log 2>$null
            if ($LASTEXITCODE -eq 0) { $eventsCleared++ }
        }
    }
} catch {}
$results['EventLogs'] = $eventsCleared
Write-Output ""EVENT_LOGS=$eventsCleared""
");
            }

            if (options.ClearEnvironmentPath)
            {
                parts.Add(@"
$pathCleaned = 0
foreach ($scope in @('Machine','User')) {
    $currentPath = [Environment]::GetEnvironmentVariable('Path', $scope)
    if (-not $currentPath) { continue }
    $parts2 = $currentPath -split ';' | Where-Object { $_.Trim() -ne '' }
    $validParts = @()
    foreach ($p in $parts2) {
        $trimmed = $p.Trim()
        # Keep entries that contain environment variable references
        if ($trimmed -match '%[^%]+%') { $validParts += $trimmed; continue }
        # Keep entries that point to existing paths
        if (Test-Path $trimmed -ErrorAction SilentlyContinue) { $validParts += $trimmed }
        else { $pathCleaned++ }
    }
    $newPath = $validParts -join ';'
    if ($newPath -ne $currentPath) {
        [Environment]::SetEnvironmentVariable('Path', $newPath, $scope)
    }
}
$results['EnvironmentPath'] = $pathCleaned
Write-Output ""PATH_CLEANED=$pathCleaned""
");
            }

            if (options.ClearDnsCache)
            {
                parts.Add(@"
try { ipconfig /flushdns | Out-Null; Write-Output ""DNS_FLUSHED=1"" } catch { Write-Output ""DNS_FLUSHED=0"" }
");
            }

            parts.Add("Write-Output 'CLEANUP_COMPLETE'");
            return string.Join("\n", parts);
        }

        private static void ParseElevatedOutput(
            string output,
            CleanupResult result,
            CleanupOptions options,
            IProgress<CleanupStepProgress> progress)
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("REGISTRY_CLEANED=") && options.ClearRegistryKeys)
                {
                    if (int.TryParse(trimmed.AsSpan("REGISTRY_CLEANED=".Length), out var n))
                        result.RegistryKeysCleaned = n;
                }
                else if (trimmed.StartsWith("DRIVER_LOGS=") && options.ClearDriverLogs)
                {
                    if (int.TryParse(trimmed.AsSpan("DRIVER_LOGS=".Length), out var n))
                        result.FilesDeleted += n;
                }
                else if (trimmed.StartsWith("SETUP_LOGS=") && options.ClearSetupLogs)
                {
                    if (int.TryParse(trimmed.AsSpan("SETUP_LOGS=".Length), out var n))
                        result.FilesDeleted += n;
                }
                else if (trimmed.StartsWith("EVENT_LOGS=") && options.ClearEventLogs)
                {
                    if (int.TryParse(trimmed.AsSpan("EVENT_LOGS=".Length), out var n))
                        result.EventLogsCleared = n;
                }
                else if (trimmed.StartsWith("PATH_CLEANED=") && options.ClearEnvironmentPath)
                {
                    if (int.TryParse(trimmed.AsSpan("PATH_CLEANED=".Length), out var n))
                        result.PathEntriesRemoved = n;
                }
            }

            // Report each elevated category as succeeded
            if (options.ClearRegistryKeys)
                progress.Report(new CleanupStepProgress("Invalid registry keys", $"Removed {result.RegistryKeysCleaned} orphan entries", CleanupStepState.Succeeded));
            if (options.ClearDriverLogs)
                progress.Report(new CleanupStepProgress("Driver and setup logs", "Done", CleanupStepState.Succeeded));
            if (options.ClearEventLogs)
                progress.Report(new CleanupStepProgress("Windows Event Logs", $"Cleared {result.EventLogsCleared} logs", CleanupStepState.Succeeded));
            if (options.ClearEnvironmentPath)
                progress.Report(new CleanupStepProgress("Environment PATH", $"Removed {result.PathEntriesRemoved} invalid entries", CleanupStepState.Succeeded));
            if (options.ClearDnsCache)
                progress.Report(new CleanupStepProgress("DNS cache", "Flushed", CleanupStepState.Succeeded));
            if (options.ClearSetupLogs)
                progress.Report(new CleanupStepProgress("Setup logs", "Done", CleanupStepState.Succeeded));
        }

        #endregion

        #region Helpers

        private static void DeleteContents(string path, CleanupResult result)
        {
            if (!Directory.Exists(path)) return;

            foreach (var file in SafeEnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
                TryDeleteFile(file, result);

            foreach (var dir in SafeEnumerateDirectories(path, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Directory.Delete(dir, true);
                    result.FoldersDeleted++;
                }
                catch { }
            }
        }

        private static void DeleteFiles(string path, string pattern, CleanupResult result)
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in SafeEnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly))
                TryDeleteFile(file, result);
        }

        private static void DeleteFilesRecursive(string path, string pattern, CleanupResult result)
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in SafeEnumerateFiles(path, pattern, SearchOption.AllDirectories))
                TryDeleteFile(file, result);
        }

        private static void TryDeleteFile(string path, CleanupResult result)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    result.FilesDeleted++;
                }
            }
            catch { }
        }

        private static void TryDeleteRegistryValues(RegistryKey root, string subKeyPath)
        {
            try
            {
                using var key = root.OpenSubKey(subKeyPath, writable: true);
                if (key is null) return;
                foreach (var name in key.GetValueNames())
                {
                    if (name.Equals("MRUList", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("MRUListEx", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { key.DeleteValue(name, false); }
                    catch { }
                }
            }
            catch { }
        }

        private static void ClearEmptyFoldersRecursive(string path, CleanupResult result, int depth, int maxDepth, HashSet<string> skipDirs)
        {
            if (depth > maxDepth) return;
            if (!Directory.Exists(path)) return;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    var dirName = Path.GetFileName(dir);
                    if (skipDirs.Contains(dirName)) continue;
                    if (dirName.StartsWith('.')) continue;

                    ClearEmptyFoldersRecursive(dir, result, depth + 1, maxDepth, skipDirs);

                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            Directory.Delete(dir);
                            result.FoldersDeleted++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
        {
            try { return Directory.EnumerateFiles(path, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = System.IO.FileAttributes.System
            }); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path, SearchOption option = SearchOption.TopDirectoryOnly)
        {
            try { return Directory.EnumerateDirectories(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = System.IO.FileAttributes.System
            }); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static string EscapeForCmd(string script)
        {
            return script.Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        #endregion
    }

    internal sealed class CleanupOptions
    {
        // Safe operations
        public bool ClearTempFolders { get; set; } = true;
        public bool ClearRecycleBin { get; set; } = true;
        public bool ClearExplorerHistory { get; set; } = true;
        public bool ClearRecentDocuments { get; set; } = true;
        public bool ClearAppLogs { get; set; } = true;
        public bool ClearWindowsErrorReports { get; set; } = true;
        public bool ClearThumbnailCache { get; set; } = true;
        public bool ClearIconCache { get; set; } = true;
        public bool ClearWindowsUpdateCache { get; set; } = true;
        public bool ClearDeliveryOptimization { get; set; } = true;
        public bool ClearFontCache { get; set; } = true;
        public bool ClearPrefetch { get; set; } = true;
        public bool ClearMemoryDumps { get; set; } = true;
        public bool ClearEmptyFolders { get; set; } = true;
        public bool ClearOrphanFiles { get; set; } = true;

        // Elevated operations
        public bool ClearRegistryKeys { get; set; } = true;
        public bool ClearDriverLogs { get; set; } = true;
        public bool ClearSetupLogs { get; set; } = true;
        public bool ClearEventLogs { get; set; } = true;
        public bool ClearEnvironmentPath { get; set; } = true;
        public bool ClearDnsCache { get; set; } = true;
    }

    internal sealed class CleanupResult
    {
        public int FilesDeleted { get; set; }
        public int FoldersDeleted { get; set; }
        public int ItemsCleared { get; set; }
        public int RegistryKeysCleaned { get; set; }
        public int EventLogsCleared { get; set; }
        public int PathEntriesRemoved { get; set; }
    }

    internal sealed record CleanupStepProgress(string Category, string Status, CleanupStepState State);

    internal enum CleanupStepState { Running, Succeeded, Skipped, Failed }
}
