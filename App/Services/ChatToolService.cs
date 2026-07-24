using App.Models;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class ChatToolService
    {
        private const int MaximumFileBytes = 2 * 1024 * 1024;
        private const int MaximumOutputCharacters = 50_000;
        private const int MaximumEntries = 1_000;
        public string? DefaultPath { get; set; }

        public static JsonArray CreateDeclarations() => new()
        {
            Function("execute_command_shell", "Run non-elevated PowerShell. Mark risky when the command changes system state or can destroy data.", Props(
                ("command", String()), ("working_directory", String()), ("timeout_seconds", Integer()),
                ("risk_level", Enum("safe", "risky")), ("approval_reason", String()), ("destructive_effect", String())), "command", "risk_level"),
            Function("execute_command_shell_admin", "Run PowerShell as administrator. Always requires approval and Windows UAC.", Props(
                ("command", String()), ("working_directory", String()), ("timeout_seconds", Integer()),
                ("approval_reason", String()), ("destructive_effect", String())), "command", "approval_reason", "destructive_effect"),
            Function("read_file", "Read a UTF-8 text file. offset and limit are line numbers.", Props(("path", String()), ("offset", Integer()), ("limit", Integer())), "path"),
            Function("read_file_info", "Return file metadata and Windows attributes.", Props(("path", String())), "path"),
            Function("write_file", "Create or overwrite a UTF-8 text file. overwrite must be true for an existing file.", Props(
                ("path", String()), ("content", String()), ("overwrite", Boolean()), ("risk_level", Enum("safe", "risky")),
                ("approval_reason", String()), ("destructive_effect", String())), "path", "content", "risk_level"),
            Function("delete_file", "Permanently delete a file.", Props(
                ("path", String()), ("risk_level", Enum("safe", "risky")), ("approval_reason", String()), ("destructive_effect", String())), "path", "risk_level"),
            Function("list_directory", "List directory entries with attributes. Recursive traversal does not follow reparse points.", Props(
                ("path", String()), ("recursive", Boolean()), ("max_depth", Integer()), ("include_hidden", Boolean())), "path"),
            Function("make_directory", "Create a directory and missing parents.", Props(("path", String())), "path"),
            Function("delete_directory", "Permanently delete a directory. Always requires approval.", Props(
                ("path", String()), ("recursive", Boolean()), ("approval_reason", String()), ("destructive_effect", String())), "path", "approval_reason", "destructive_effect"),
            Function("hash_file", "Hash a file. algorithm defaults to SHA256.", Props(("path", String()), ("algorithm", Enum("SHA256", "SHA384", "SHA512", "MD5"))), "path"),
            Function("zip_file", "Create a ZIP archive from files or directories.", Props(("source_paths", ArrayOfStrings()), ("destination_path", String()), ("overwrite", Boolean())), "source_paths", "destination_path"),
            Function("unzip_file", "Extract a ZIP archive while blocking path traversal.", Props(("archive_path", String()), ("destination_path", String()), ("overwrite", Boolean())), "archive_path", "destination_path"),
            Function("read_file_hex", "Read a bounded section of a file as hexadecimal bytes.", Props(("path", String()), ("offset", Integer()), ("byte_count", Integer())), "path"),
            Function("grep", "Search file contents using ripgrep.", Props(
                ("pattern", String()), ("path", String()), ("file_glob", String()), ("case_sensitive", Boolean()), ("max_results", Integer())), "pattern", "path"),
            Function("kill_process", "Terminate a process by ID.", Props(
                ("process_id", Integer()), ("force", Boolean()), ("risk_level", Enum("safe", "risky")),
                ("approval_reason", String()), ("destructive_effect", String())), "process_id", "risk_level"),
            Function("search_process", "Search running processes by name, executable path, or process ID.", Props(("name", String()), ("executable_path", String()), ("process_id", Integer()))),
            Function("list_special_folders", "List Windows special folders and their resolved paths.", new JsonObject()),
            Function("list_environment_variable", "List environment variable names and full values. scope is process, user, machine, or all.", Props(("scope", Enum("process", "user", "machine", "all")))),
            Function("list_system_info", "Return basic hardware, operating-system, date/time, timezone, storage, memory, display, uptime, and elevation information.", new JsonObject()),
            Function("screen_capture", "Ask the user to manually select a screen region, then return that image.", Props(("purpose", String()), ("include_cursor", Boolean())))
        };

        public static ToolApprovalPolicy ApprovalPolicy(string name, JsonObject arguments) => name switch
        {
            "execute_command_shell_admin" => ToolApprovalPolicy.AlwaysWithUac,
            "delete_directory" => ToolApprovalPolicy.Always,
            "screen_capture" => ToolApprovalPolicy.ManualScreenSelection,
            "execute_command_shell" or "write_file" or "delete_file" or "kill_process"
                when string.Equals(OptionalString(arguments, "risk_level"), "risky", StringComparison.OrdinalIgnoreCase) => ToolApprovalPolicy.AgentConditional,
            _ => ToolApprovalPolicy.None
        };

        public static string ApprovalTarget(string name, JsonObject arguments) => name switch
        {
            "execute_command_shell" or "execute_command_shell_admin" => RequiredString(arguments, "command"),
            "kill_process" => $"Process ID {OptionalInt(arguments, "process_id")}",
            _ => OptionalString(arguments, "path") ?? name
        };

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "execute_command_shell" => await RunPowerShellAsync(arguments, token),
                    "execute_command_shell_admin" => await RunElevatedPowerShellAsync(arguments, token),
                    "read_file" => ReadFile(PathOf(arguments, "path"), OptionalInt(arguments, "offset"), OptionalInt(arguments, "limit")),
                    "read_file_info" => ReadFileInfo(PathOf(arguments, "path")),
                    "write_file" => WriteFile(PathOf(arguments, "path"), RequiredString(arguments, "content"), OptionalBool(arguments, "overwrite")),
                    "delete_file" => DeleteFile(PathOf(arguments, "path")),
                    "list_directory" => ListDirectory(PathOf(arguments, "path"), OptionalBool(arguments, "recursive"), OptionalInt(arguments, "max_depth"), OptionalBool(arguments, "include_hidden")),
                    "make_directory" => MakeDirectory(PathOf(arguments, "path")),
                    "delete_directory" => DeleteDirectory(PathOf(arguments, "path"), OptionalBool(arguments, "recursive")),
                    "hash_file" => HashFile(PathOf(arguments, "path"), OptionalString(arguments, "algorithm")),
                    "zip_file" => ZipFile(arguments),
                    "unzip_file" => UnzipFile(PathOf(arguments, "archive_path"), PathOf(arguments, "destination_path"), OptionalBool(arguments, "overwrite")),
                    "read_file_hex" => ReadFileHex(PathOf(arguments, "path"), OptionalLong(arguments, "offset"), OptionalInt(arguments, "byte_count")),
                    "grep" => await GrepAsync(arguments, token),
                    "kill_process" => KillProcess(RequiredInt(arguments, "process_id"), OptionalBool(arguments, "force")),
                    "search_process" => SearchProcess(arguments),
                    "list_special_folders" => ListSpecialFolders(),
                    "list_environment_variable" => ListEnvironmentVariables(OptionalString(arguments, "scope")),
                    "list_system_info" => ListSystemInfo(),
                    _ => Error("unknown_tool", $"Unknown tool: {name}")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException exception) { return Error("access_denied", exception.Message); }
            catch (Exception exception) { return Error("operation_failed", exception.Message); }
        }

        private ToolResult ReadFile(string path, int? offset, int? limit)
        {
            var lines = ReadText(path).Replace("\r\n", "\n").Split('\n');
            var start = Math.Clamp(offset ?? 0, 0, lines.Length);
            var count = Math.Clamp(limit ?? 400, 1, 2_000);
            return Ok(new JsonObject
            {
                ["summary"] = $"Read {Math.Min(count, lines.Length - start)} line(s).",
                ["path"] = path,
                ["content"] = string.Join("\n", lines.Skip(start).Take(count).Select((line, index) => $"{start + index + 1}: {line}")),
                ["total_lines"] = lines.Length,
                ["truncated"] = start + count < lines.Length
            });
        }

        private static ToolResult ReadFileInfo(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The file does not exist.", path);
            return Ok(new JsonObject
            {
                ["summary"] = "File metadata retrieved.",
                ["path"] = info.FullName,
                ["size_bytes"] = info.Length,
                ["created"] = info.CreationTime,
                ["modified"] = info.LastWriteTime,
                ["accessed"] = info.LastAccessTime,
                ["attributes"] = info.Attributes.ToString(),
                ["extension"] = info.Extension,
                ["read_only"] = info.IsReadOnly
            });
        }

        private static ToolResult WriteFile(string path, string content, bool overwrite)
        {
            if (File.Exists(path) && !overwrite) throw new IOException("The file already exists; set overwrite to true to replace it.");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new IOException("A parent directory is required."));
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return Ok(new JsonObject { ["summary"] = File.Exists(path) ? "File written." : "File created.", ["path"] = path, ["characters"] = content.Length });
        }

        private static ToolResult DeleteFile(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The file does not exist.", path);
            File.Delete(path);
            return Ok(new JsonObject { ["summary"] = "File permanently deleted.", ["path"] = path, ["destructive_effect"] = "The file was not moved to the Recycle Bin." });
        }

        private ToolResult ListDirectory(string path, bool recursive, int? requestedDepth, bool includeHidden)
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            var maxDepth = Math.Clamp(requestedDepth ?? (recursive ? 5 : 1), 1, 20);
            var results = new List<JsonNode>();
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((path, 0));
            while (queue.Count > 0 && results.Count < MaximumEntries)
            {
                var current = queue.Dequeue();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                    if (!includeHidden && info.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                    var isDirectory = info is DirectoryInfo;
                    results.Add(new JsonObject
                    {
                        ["name"] = info.Name,
                        ["path"] = info.FullName,
                        ["kind"] = isDirectory ? "directory" : "file",
                        ["size_bytes"] = info is FileInfo file ? file.Length : null,
                        ["created"] = info.CreationTime,
                        ["modified"] = info.LastWriteTime,
                        ["attributes"] = info.Attributes.ToString()
                    });
                    if (recursive && isDirectory && current.Depth + 1 < maxDepth && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        queue.Enqueue((entry, current.Depth + 1));
                    if (results.Count == MaximumEntries) break;
                }
            }
            return Ok(new JsonObject { ["summary"] = $"Listed {results.Count} entr{(results.Count == 1 ? "y" : "ies")}.", ["path"] = path, ["entries"] = new JsonArray(results.ToArray()), ["truncated"] = results.Count == MaximumEntries });
        }

        private static ToolResult MakeDirectory(string path)
        {
            Directory.CreateDirectory(path);
            return Ok(new JsonObject { ["summary"] = "Directory is available.", ["path"] = path });
        }

        private static ToolResult DeleteDirectory(string path, bool recursive)
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            Directory.Delete(path, recursive);
            return Ok(new JsonObject { ["summary"] = "Directory permanently deleted.", ["path"] = path, ["recursive"] = recursive, ["destructive_effect"] = "The directory was not moved to the Recycle Bin." });
        }

        private static ToolResult HashFile(string path, string? algorithm)
        {
            using var stream = File.OpenRead(path);
            using HashAlgorithm hash = (algorithm ?? "SHA256").ToUpperInvariant() switch
            {
                "MD5" => MD5.Create(),
                "SHA384" => SHA384.Create(),
                "SHA512" => SHA512.Create(),
                _ => SHA256.Create()
            };
            return Ok(new JsonObject { ["summary"] = "File hash calculated.", ["path"] = path, ["algorithm"] = hash.GetType().Name.Replace("Managed", ""), ["hash"] = Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant() });
        }

        private ToolResult ZipFile(JsonObject arguments)
        {
            var destination = PathOf(arguments, "destination_path");
            var overwrite = OptionalBool(arguments, "overwrite");
            if (File.Exists(destination))
            {
                if (!overwrite) throw new IOException("The destination archive exists; set overwrite to true.");
                File.Delete(destination);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new IOException("A destination parent is required."));
            using var archive = System.IO.Compression.ZipFile.Open(destination, ZipArchiveMode.Create);
            var sources = arguments["source_paths"]?.AsArray().Select(item => PathOf(item?.GetValue<string>() ?? string.Empty)).ToArray() ?? throw new ArgumentException("source_paths is required.");
            foreach (var source in sources)
            {
                if (File.Exists(source)) archive.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Optimal);
                else if (Directory.Exists(source))
                {
                    var rootName = new DirectoryInfo(source).Name;
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        archive.CreateEntryFromFile(file, Path.Combine(rootName, Path.GetRelativePath(source, file)).Replace('\\', '/'), CompressionLevel.Optimal);
                }
                else throw new FileNotFoundException("A ZIP source does not exist.", source);
            }
            return Ok(new JsonObject { ["summary"] = "ZIP archive created.", ["path"] = destination, ["source_count"] = sources.Length });
        }

        private static ToolResult UnzipFile(string archivePath, string destination, bool overwrite)
        {
            Directory.CreateDirectory(destination);
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Unsafe archive entry: {entry.FullName}");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) && !overwrite) throw new IOException($"Extraction target exists: {target}");
                entry.ExtractToFile(target, overwrite);
            }
            return Ok(new JsonObject { ["summary"] = $"Extracted {archive.Entries.Count} archive entries.", ["archive_path"] = archivePath, ["destination_path"] = destination });
        }

        private static ToolResult ReadFileHex(string path, long? requestedOffset, int? requestedCount)
        {
            var offset = Math.Max(0, requestedOffset ?? 0);
            var count = Math.Clamp(requestedCount ?? 256, 1, 4096);
            using var stream = File.OpenRead(path);
            stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
            var buffer = new byte[Math.Min(count, (int)Math.Min(int.MaxValue, stream.Length - stream.Position))];
            var read = stream.Read(buffer, 0, buffer.Length);
            var lines = Enumerable.Range(0, (read + 15) / 16).Select(row =>
            {
                var bytes = buffer.Skip(row * 16).Take(16).ToArray();
                return $"{offset + row * 16:X8}  {string.Join(" ", bytes.Select(value => value.ToString("X2"))),-47}  {new string(bytes.Select(value => value is >= 32 and <= 126 ? (char)value : '.').ToArray())}";
            });
            return Ok(new JsonObject { ["summary"] = $"Read {read} byte(s).", ["path"] = path, ["offset"] = offset, ["hex"] = string.Join("\n", lines), ["truncated"] = offset + read < stream.Length });
        }

        private async Task<ToolResult> GrepAsync(JsonObject arguments, CancellationToken token)
        {
            var values = new List<string> { "--line-number", "--color", "never" };
            if (!OptionalBool(arguments, "case_sensitive")) values.Add("--ignore-case");
            values.Add("--max-count"); values.Add(Math.Clamp(OptionalInt(arguments, "max_results") ?? 100, 1, 1_000).ToString());
            var glob = OptionalString(arguments, "file_glob"); if (!string.IsNullOrWhiteSpace(glob)) { values.Add("--glob"); values.Add(glob); }
            values.Add(RequiredString(arguments, "pattern")); values.Add(PathOf(arguments, "path"));
            return await RunProcessAsync("rg", values, null, 60, token);
        }

        private static ToolResult KillProcess(int processId, bool force)
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            process.Kill(force);
            process.WaitForExit(10_000);
            return Ok(new JsonObject { ["summary"] = "Process terminated.", ["process_id"] = processId, ["name"] = name, ["entire_tree"] = force });
        }

        private static ToolResult SearchProcess(JsonObject arguments)
        {
            var name = OptionalString(arguments, "name");
            var executable = OptionalString(arguments, "executable_path");
            var id = OptionalInt(arguments, "process_id");
            var matches = new JsonArray();
            foreach (var process in Process.GetProcesses().OrderBy(item => item.ProcessName))
            {
                using (process)
                {
                    if (id.HasValue && process.Id != id) continue;
                    if (!string.IsNullOrWhiteSpace(name) && !process.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { }
                    if (!string.IsNullOrWhiteSpace(executable) && !string.Equals(path, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) continue;
                    matches.Add(new JsonObject { ["process_id"] = process.Id, ["name"] = process.ProcessName, ["executable_path"] = path, ["responding"] = SafeResponding(process) });
                    if (matches.Count == 250) break;
                }
            }
            return Ok(new JsonObject { ["summary"] = $"Found {matches.Count} matching process(es).", ["processes"] = matches, ["truncated"] = matches.Count == 250 });
        }

        private static bool? SafeResponding(Process process) { try { return process.Responding; } catch { return null; } }

        private static ToolResult ListSpecialFolders()
        {
            var folders = new JsonArray(System.Enum.GetValues<Environment.SpecialFolder>().Distinct().OrderBy(value => value.ToString()).Select(folder =>
                (JsonNode)new JsonObject { ["name"] = folder.ToString(), ["path"] = Environment.GetFolderPath(folder) }).ToArray());
            return Ok(new JsonObject { ["summary"] = $"Listed {folders.Count} special folders.", ["folders"] = folders });
        }

        private static ToolResult ListEnvironmentVariables(string? requestedScope)
        {
            var scope = (requestedScope ?? "all").ToLowerInvariant();
            var targets = scope switch
            {
                "process" => new[] { EnvironmentVariableTarget.Process },
                "user" => new[] { EnvironmentVariableTarget.User },
                "machine" => new[] { EnvironmentVariableTarget.Machine },
                "all" => new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine },
                _ => throw new ArgumentException("scope must be process, user, machine, or all.")
            };
            var values = new List<JsonNode>();
            foreach (var target in targets)
                foreach (DictionaryEntry item in Environment.GetEnvironmentVariables(target))
                    values.Add(new JsonObject { ["scope"] = target.ToString().ToLowerInvariant(), ["name"] = item.Key?.ToString(), ["value"] = item.Value?.ToString() ?? string.Empty });
            values = values.OrderBy(item => item?["scope"]?.GetValue<string>()).ThenBy(item => item?["name"]?.GetValue<string>()).ToList();
            return Ok(new JsonObject { ["summary"] = $"Listed {values.Count} environment variable(s). Values may contain secrets.", ["variables"] = new JsonArray(values.ToArray()) });
        }

        private static ToolResult ListSystemInfo()
        {
            var now = DateTimeOffset.Now;
            var zone = TimeZoneInfo.Local;
            var warnings = new JsonArray();
            var root = new JsonObject
            {
                ["summary"] = "Basic system information retrieved.",
                ["local_datetime"] = now,
                ["utc_datetime"] = now.UtcDateTime,
                ["timezone"] = new JsonObject { ["id"] = zone.Id, ["display_name"] = zone.DisplayName, ["utc_offset"] = zone.GetUtcOffset(now).ToString() },
                ["operating_system"] = new JsonObject { ["description"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription, ["version"] = Environment.OSVersion.VersionString, ["process_architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), ["os_architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), ["is_64_bit"] = Environment.Is64BitOperatingSystem },
                ["computer_name"] = Environment.MachineName,
                ["user_name"] = Environment.UserName,
                ["logical_processors"] = Environment.ProcessorCount,
                ["uptime"] = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(),
                ["boot_time"] = DateTimeOffset.Now.Subtract(TimeSpan.FromMilliseconds(Environment.TickCount64)),
                ["elevated"] = IsElevated(),
                ["drives"] = Drives(warnings),
                ["warnings"] = warnings
            };
            TryAddRegistryHardware(root, warnings);
            TryAddMemory(root, warnings);
            TryAddDisplay(root, warnings);
            TryAddPower(root, warnings);
            return Ok(root);
        }

        private static JsonArray Drives(JsonArray warnings)
        {
            var values = new JsonArray();
            foreach (var drive in DriveInfo.GetDrives().Where(item => item.DriveType == DriveType.Fixed))
            {
                try { if (drive.IsReady) values.Add(new JsonObject { ["name"] = drive.Name, ["format"] = drive.DriveFormat, ["total_bytes"] = drive.TotalSize, ["free_bytes"] = drive.AvailableFreeSpace }); }
                catch (Exception exception) { warnings.Add($"Drive {drive.Name}: {exception.Message}"); }
            }
            return values;
        }

        private static void TryAddRegistryHardware(JsonObject root, JsonArray warnings)
        {
            try
            {
                using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                root["cpu"] = new JsonObject { ["name"] = cpu?.GetValue("ProcessorNameString")?.ToString()?.Trim(), ["identifier"] = cpu?.GetValue("Identifier")?.ToString(), ["mhz"] = JsonValue.Create(cpu?.GetValue("~MHz") as int?) };
                using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                root["system"] = new JsonObject { ["manufacturer"] = bios?.GetValue("SystemManufacturer")?.ToString(), ["model"] = bios?.GetValue("SystemProductName")?.ToString() };
                root["bios"] = new JsonObject { ["manufacturer"] = bios?.GetValue("BIOSVendor")?.ToString(), ["version"] = bios?.GetValue("BIOSVersion")?.ToString(), ["release_date"] = bios?.GetValue("BIOSReleaseDate")?.ToString() };
                root["gpus"] = ReadGpuRegistry();
                using var windows = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                root["windows"] = new JsonObject { ["edition"] = windows?.GetValue("ProductName")?.ToString(), ["display_version"] = windows?.GetValue("DisplayVersion")?.ToString(), ["build"] = windows?.GetValue("CurrentBuildNumber")?.ToString(), ["installation_type"] = windows?.GetValue("InstallationType")?.ToString() };
            }
            catch (Exception exception) { warnings.Add($"Registry hardware information: {exception.Message}"); }
        }

        private static void TryAddMemory(JsonObject root, JsonArray warnings)
        {
            try
            {
                var info = new Microsoft.VisualBasic.Devices.ComputerInfo();
                root["memory"] = new JsonObject { ["installed_physical_bytes"] = info.TotalPhysicalMemory, ["available_physical_bytes"] = info.AvailablePhysicalMemory };
            }
            catch (Exception exception) { warnings.Add($"Memory information: {exception.Message}"); }
        }

        private static void TryAddDisplay(JsonObject root, JsonArray warnings)
        {
            try
            {
                var area = Microsoft.UI.Windowing.DisplayArea.Primary;
                root["primary_display"] = new JsonObject { ["width"] = area.OuterBounds.Width, ["height"] = area.OuterBounds.Height };
            }
            catch (Exception exception) { warnings.Add($"Display information: {exception.Message}"); }
        }

        private static JsonArray ReadGpuRegistry()
        {
            var values = new JsonArray();
            using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            foreach (var adapterId in video?.GetSubKeyNames() ?? [])
            {
                using var adapter = video?.OpenSubKey($@"{adapterId}\0000");
                var name = adapter?.GetValue("DriverDesc")?.ToString();
                if (string.IsNullOrWhiteSpace(name) || values.Any(item => item?["name"]?.GetValue<string>() == name)) continue;
                var memoryValue = adapter?.GetValue("HardwareInformation.MemorySize");
                values.Add(new JsonObject
                {
                    ["name"] = name,
                    ["adapter_memory_bytes"] = memoryValue switch { int signed => (long)(uint)signed, long wide => wide, _ => null }
                });
            }
            return values;
        }

        private static void TryAddPower(JsonObject root, JsonArray warnings)
        {
            try
            {
                if (!GetSystemPowerStatus(out var status)) throw new InvalidOperationException("Windows did not return power status.");
                root["power"] = new JsonObject
                {
                    ["ac_line"] = status.ACLineStatus switch { 0 => "offline", 1 => "online", _ => "unknown" },
                    ["battery_percent"] = status.BatteryLifePercent == byte.MaxValue ? null : status.BatteryLifePercent,
                    ["battery_saver"] = status.SystemStatusFlag == 1,
                    ["battery_life_seconds"] = status.BatteryLifeTime == uint.MaxValue ? null : status.BatteryLifeTime
                };
            }
            catch (Exception exception) { warnings.Add($"Power information: {exception.Message}"); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        private async Task<ToolResult> RunPowerShellAsync(JsonObject arguments, CancellationToken token) =>
            await RunProcessAsync("powershell.exe", new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", RequiredString(arguments, "command") },
                OptionalWorkingDirectory(arguments), Math.Clamp(OptionalInt(arguments, "timeout_seconds") ?? 120, 1, 1_800), token);

        private async Task<ToolResult> RunElevatedPowerShellAsync(JsonObject arguments, CancellationToken token)
        {
            var taskDirectory = Path.Combine(Path.GetTempPath(), $"CrsterUtility-{Guid.NewGuid():N}");
            Directory.CreateDirectory(taskDirectory);
            var scriptPath = Path.Combine(taskDirectory, "command.ps1");
            var outputPath = Path.Combine(taskDirectory, "result.json");
            try
            {
                var command = RequiredString(arguments, "command");
                var wrapper = "$ErrorActionPreference='Continue'\r\n" +
                    $"$resultPath='{outputPath.Replace("'", "''")}'\r\n" +
                    "try { $output = & {\r\n" + command + "\r\n} *>&1 | Out-String; $code = if ($LASTEXITCODE -is [int]) { $LASTEXITCODE } else { 0 }; " +
                    "$value = @{ exitCode=$code; output=$output; error=$null } } catch { $value = @{ exitCode=1; output=''; error=$_.Exception.Message } }; " +
                    "$value | ConvertTo-Json -Compress | Set-Content -LiteralPath $resultPath -Encoding UTF8\r\n";
                File.WriteAllText(scriptPath, wrapper, new UTF8Encoding(false));
                var info = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = OptionalWorkingDirectory(arguments) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\""
                };
                Process? process;
                try { process = Process.Start(info); }
                catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223) { return Error("uac_cancelled", "Windows UAC elevation was cancelled.", "cancelled_by_user"); }
                if (process is null) return Error("launch_failed", "The elevated PowerShell process could not be started.");
                using (process)
                {
                    var timeout = TimeSpan.FromSeconds(Math.Clamp(OptionalInt(arguments, "timeout_seconds") ?? 300, 1, 1_800));
                    var wait = process.WaitForExitAsync(token);
                    if (await Task.WhenAny(wait, Task.Delay(timeout, token)) != wait)
                    {
                        try { process.Kill(true); } catch { }
                        return Error("timeout", $"The elevated command exceeded {timeout.TotalSeconds:0} seconds.", "timed_out");
                    }
                    await wait;
                }
                if (!File.Exists(outputPath)) return Error("missing_result", "The elevated process ended without returning a result.");
                var result = JsonNode.Parse(await File.ReadAllTextAsync(outputPath, token))?.AsObject() ?? new JsonObject();
                var exitCode = result["exitCode"]?.GetValue<int>() ?? 1;
                return ProcessResult(exitCode, result["output"]?.GetValue<string>() ?? string.Empty, result["error"]?.GetValue<string>() ?? string.Empty);
            }
            finally
            {
                try { Directory.Delete(taskDirectory, true); } catch { }
            }
        }

        private static async Task<ToolResult> RunProcessAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory, int timeoutSeconds, CancellationToken token)
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (!string.IsNullOrWhiteSpace(workingDirectory)) info.WorkingDirectory = workingDirectory;
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The command could not be started.");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            var wait = process.WaitForExitAsync(token);
            if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), token)) != wait)
            {
                try { process.Kill(true); } catch { }
                return Error("timeout", $"The command exceeded {timeoutSeconds} seconds.", "timed_out");
            }
            await wait;
            return ProcessResult(process.ExitCode, await stdout, await stderr);
        }

        private static ToolResult ProcessResult(int exitCode, string stdout, string stderr)
        {
            var truncated = stdout.Length > MaximumOutputCharacters || stderr.Length > MaximumOutputCharacters;
            return new ToolResult(exitCode == 0, Json(new JsonObject
            {
                ["status"] = exitCode == 0 ? "completed" : "failed",
                ["summary"] = exitCode == 0 ? "Command completed." : $"Command exited with code {exitCode}.",
                ["exit_code"] = exitCode,
                ["stdout"] = Truncate(stdout),
                ["stderr"] = Truncate(stderr),
                ["truncated"] = truncated
            }), exitCode == 0 ? "completed" : "failed");
        }

        private string PathOf(JsonObject arguments, string name) => PathOf(RequiredString(arguments, name));
        private string PathOf(string value) => Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(DefaultPathOrProfile(), value));
        private string DefaultPathOrProfile() => !string.IsNullOrWhiteSpace(DefaultPath)
            ? (Directory.Exists(DefaultPath) ? DefaultPath! : Path.GetDirectoryName(DefaultPath)!)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private string? OptionalWorkingDirectory(JsonObject arguments) => OptionalString(arguments, "working_directory") is { Length: > 0 } value ? PathOf(value) : null;

        private static string ReadText(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The file does not exist.", path);
            if (info.Length > MaximumFileBytes) throw new IOException("The file exceeds the 2 MB text limit.");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string? OptionalString(JsonObject value, string name) => value[name]?.GetValue<string>();
        private static int? OptionalInt(JsonObject value, string name) => value[name]?.GetValue<int>();
        private static long? OptionalLong(JsonObject value, string name) => value[name]?.GetValue<long>();
        private static bool OptionalBool(JsonObject value, string name) => value[name]?.GetValue<bool>() ?? false;
        private static int RequiredInt(JsonObject value, string name) => OptionalInt(value, name) ?? throw new ArgumentException($"{name} is required.");
        private static string RequiredString(JsonObject value, string name) => OptionalString(value, name) ?? throw new ArgumentException($"{name} is required.");
        private static ToolResult Ok(JsonObject value) => new(true, Json(value));
        private static ToolResult Error(string category, string summary, string status = "failed") => new(false, Json(new JsonObject { ["status"] = status, ["error_category"] = category, ["summary"] = summary }), status);
        private static string Json(JsonObject value) => value.ToJsonString(new() { WriteIndented = true });
        private static string Truncate(string value) => value.Length <= MaximumOutputCharacters ? value : value[..MaximumOutputCharacters] + "\n[output truncated]";

        private static JsonObject String() => new() { ["type"] = "string" };
        private static JsonObject Integer() => new() { ["type"] = "integer" };
        private static JsonObject Boolean() => new() { ["type"] = "boolean" };
        private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode)value).ToArray()) };
        private static JsonObject ArrayOfStrings() => new() { ["type"] = "array", ["items"] = String() };
        private static JsonObject Props(params (string Name, JsonObject Schema)[] values) => new(values.Select(value => KeyValuePair.Create<string, JsonNode?>(value.Name, value.Schema)));
        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray()) }
        };
    }
}
