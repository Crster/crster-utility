using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using App.Models;

namespace App.Services
{
    internal sealed class ChatToolService
    {
        private const int MaximumFileBytes = 2 * 1024 * 1024;
        private const int MaximumOutputCharacters = 50_000;
        private const int MaximumSearchFiles = 2_000;
        private readonly NotebookDatabaseService _notebook = new();

        public string? WorkspaceRoot { get; set; }

        public static bool IsRisky(string name) => name is "edit_file" or "remove_file" or "create_file" or "create_process" or "create_shell";

        public static JsonArray CreateDeclarations(bool planning)
        {
            var tools = new JsonArray
            {
                Function("find_file", "Search a UTF-8 text file and return matching excerpts with zero-based, end-exclusive UTF-16 cursor ranges.", Props(("filename", "string"), ("search", "string")), "filename", "search"),
                Function("search_file", "Search filenames and text beneath a workspace folder for a topic.", Props(("folder", "string"), ("topic", "string")), "folder", "topic"),
                Function("find_process", "Find running process information matching a name, executable path, or PID.", Props(("search", "string")), "search"),
                Function("find_note", "Search the Crster notebook and return matching note content.", Props(("topic", "string")), "topic")
            };
            if (!planning)
            {
                tools.Add(Function("edit_file", "Replace exactly one zero-based, end-exclusive UTF-16 cursor range in an existing UTF-8 text file.",
                    new JsonObject { ["filename"] = Type("string"), ["cursor_range"] = new JsonObject { ["type"] = "object", ["properties"] = Props(("start", "integer"), ("end", "integer")), ["required"] = new JsonArray("start", "end") }, ["data"] = Type("string") }, "filename", "cursor_range", "data"));
                tools.Add(Function("remove_file", "Permanently remove one file.", Props(("filename", "string")), "filename"));
                tools.Add(Function("create_file", "Create a new UTF-8 text file; fail if it already exists.", Props(("filename", "string"), ("content", "string")), "filename", "content"));
                tools.Add(Function("create_process", "Run an executable with an argument array and timeout in milliseconds.",
                    new JsonObject { ["exe"] = Type("string"), ["args"] = new JsonObject { ["type"] = "array", ["items"] = Type("string") }, ["timeout"] = Type("integer") }, "exe", "args", "timeout"));
                tools.Add(Function("create_shell", "Run a non-interactive PowerShell command with a fixed 60 second timeout.", Props(("cmd", "string")), "cmd"));
            }
            return tools;
        }

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
        {
            try
            {
                return name switch
                {
                    "find_file" => FindFile(RequiredString(arguments, "filename"), RequiredString(arguments, "search")),
                    "search_file" => SearchFiles(RequiredString(arguments, "folder"), RequiredString(arguments, "topic"), cancellationToken),
                    "edit_file" => EditFile(RequiredString(arguments, "filename"), arguments["cursor_range"]?.AsObject() ?? throw new ArgumentException("cursor_range is required."), RequiredString(arguments, "data")),
                    "remove_file" => RemoveFile(RequiredString(arguments, "filename")),
                    "create_file" => CreateFile(RequiredString(arguments, "filename"), RequiredString(arguments, "content")),
                    "create_process" => await RunProcessAsync(RequiredString(arguments, "exe"), arguments["args"]?.AsArray().Select(value => value?.GetValue<string>() ?? string.Empty).ToArray() ?? [], RequiredInt(arguments, "timeout"), cancellationToken),
                    "create_shell" => await RunProcessAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", RequiredString(arguments, "cmd")], 60_000, cancellationToken),
                    "find_process" => FindProcess(RequiredString(arguments, "search")),
                    "find_note" => await FindNoteAsync(RequiredString(arguments, "topic"), cancellationToken),
                    _ => new ToolResult(false, $"Unknown tool: {name}")
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new ToolResult(false, $"ERROR: {exception.Message}");
            }
        }

        private ToolResult FindFile(string filename, string search)
        {
            var path = ResolveFile(filename, mustExist: true);
            var content = ReadText(path);
            var matches = new JsonArray();
            var offset = 0;
            while (matches.Count < 20 && (offset = content.IndexOf(search, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var start = Math.Max(0, offset - 300);
                var end = Math.Min(content.Length, offset + search.Length + 300);
                matches.Add(new JsonObject { ["start"] = offset, ["end"] = offset + search.Length, ["excerptStart"] = start, ["excerptEnd"] = end, ["excerpt"] = content[start..end] });
                offset += Math.Max(1, search.Length);
            }
            return JsonResult(true, new JsonObject { ["filename"] = Path.GetRelativePath(WorkspaceRoot!, path), ["matches"] = matches });
        }

        private ToolResult SearchFiles(string folder, string topic, CancellationToken cancellationToken)
        {
            var root = ResolveDirectory(folder);
            var results = new List<(int Score, JsonObject Result)>();
            var visited = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > MaximumSearchFiles) break;
                if (IsIgnored(path) || new FileInfo(path).Length > MaximumFileBytes) continue;
                var score = Path.GetFileName(path).Contains(topic, StringComparison.OrdinalIgnoreCase) ? 3 : 0;
                string? excerpt = null;
                try
                {
                    var content = ReadText(path);
                    var index = content.IndexOf(topic, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0) { score += 2; excerpt = content[Math.Max(0, index - 120)..Math.Min(content.Length, index + topic.Length + 220)]; }
                }
                catch { continue; }
                if (score > 0) results.Add((score, new JsonObject { ["filename"] = Path.GetRelativePath(WorkspaceRoot!, path), ["excerpt"] = excerpt }));
            }
            var array = new JsonArray(results.OrderByDescending(item => item.Score).Take(30).Select(item => (JsonNode)item.Result).ToArray());
            return JsonResult(true, new JsonObject { ["results"] = array, ["filesScanned"] = visited, ["truncated"] = visited >= MaximumSearchFiles });
        }

        private ToolResult EditFile(string filename, JsonObject range, string data)
        {
            var path = ResolveFile(filename, mustExist: true);
            var content = ReadText(path);
            var start = RequiredInt(range, "start");
            var end = RequiredInt(range, "end");
            if (start < 0 || end < start || end > content.Length) throw new ArgumentOutOfRangeException(nameof(range), "Cursor range is outside the file.");
            WriteAtomic(path, string.Concat(content.AsSpan(0, start), data, content.AsSpan(end)));
            return JsonResult(true, new JsonObject { ["filename"] = Path.GetRelativePath(WorkspaceRoot!, path), ["replaced"] = end - start, ["inserted"] = data.Length });
        }

        private ToolResult CreateFile(string filename, string content)
        {
            var path = ResolveFile(filename, mustExist: false);
            if (File.Exists(path)) throw new IOException("The file already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
            return JsonResult(true, new JsonObject { ["filename"] = Path.GetRelativePath(WorkspaceRoot!, path), ["characters"] = content.Length });
        }

        private ToolResult RemoveFile(string filename)
        {
            var path = ResolveFile(filename, mustExist: true);
            File.Delete(path);
            return JsonResult(true, new JsonObject { ["removed"] = Path.GetRelativePath(WorkspaceRoot!, path), ["recoverable"] = false });
        }

        private static async Task<ToolResult> RunProcessAsync(string executable, IReadOnlyList<string> arguments, int timeout, CancellationToken cancellationToken)
        {
            if (timeout is < 1 or > 300_000) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be between 1 and 300000 milliseconds.");
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The process could not be started.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var timedOut = false;
            try { await process.WaitForExitAsync(timeoutSource.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { timedOut = true; try { process.Kill(true); } catch { } }
            var output = await outputTask;
            var error = await errorTask;
            return JsonResult(!timedOut && process.ExitCode == 0, new JsonObject { ["exitCode"] = timedOut ? null : process.ExitCode, ["timedOut"] = timedOut, ["stdout"] = Truncate(output), ["stderr"] = Truncate(error) });
        }

        private static ToolResult FindProcess(string search)
        {
            var array = new JsonArray();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string path;
                    try { path = process.MainModule?.FileName ?? string.Empty; } catch { path = string.Empty; }
                    if (!process.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase) && !path.Contains(search, StringComparison.OrdinalIgnoreCase) && process.Id.ToString() != search) continue;
                    array.Add(new JsonObject { ["pid"] = process.Id, ["name"] = process.ProcessName, ["path"] = path });
                    if (array.Count >= 50) break;
                }
            }
            return JsonResult(true, new JsonObject { ["processes"] = array });
        }

        private async Task<ToolResult> FindNoteAsync(string topic, CancellationToken cancellationToken)
        {
            var matches = await _notebook.SearchAsync(topic, 10, cancellationToken);
            var entries = await _notebook.LoadAsync();
            var byIndex = entries.ToDictionary(entry => entry.Index);
            var array = new JsonArray(matches.Select(match => (JsonNode)new JsonObject { ["index"] = match.EntryIndex, ["title"] = match.Title, ["content"] = byIndex.GetValueOrDefault(match.EntryIndex)?.Content ?? match.Details }).ToArray());
            return JsonResult(true, new JsonObject { ["notes"] = array });
        }

        private string ResolveFile(string value, bool mustExist)
        {
            EnsureWorkspace();
            var path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(WorkspaceRoot!, value));
            EnsureContained(path);
            if (mustExist && !File.Exists(path)) throw new FileNotFoundException("File not found.", value);
            if (Directory.Exists(path)) throw new IOException("The requested path is a directory.");
            RejectReparsePoints(path, includeLeaf: mustExist);
            return path;
        }

        private string ResolveDirectory(string value)
        {
            EnsureWorkspace();
            var path = Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? WorkspaceRoot! : Path.IsPathRooted(value) ? value : Path.Combine(WorkspaceRoot!, value));
            EnsureContained(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(value);
            RejectReparsePoints(path, includeLeaf: true);
            return path;
        }

        private void EnsureWorkspace() { if (string.IsNullOrWhiteSpace(WorkspaceRoot) || !Directory.Exists(WorkspaceRoot)) throw new InvalidOperationException("Select a valid workspace before using file tools."); }
        private void EnsureContained(string path)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkspaceRoot!));
            if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The path is outside the selected workspace.");
        }

        private void RejectReparsePoints(string path, bool includeLeaf)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkspaceRoot!));
            var relative = Path.GetRelativePath(root, path);
            var current = root;
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var index = 0; index < parts.Length - (includeLeaf ? 0 : 1); index++)
            {
                current = Path.Combine(current, parts[index]);
                if (!File.Exists(current) && !Directory.Exists(current)) break;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Reparse points are not allowed in workspace paths.");
            }
        }

        private static string ReadText(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumFileBytes) throw new IOException("The file exceeds the 2 MB tool limit.");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static void WriteAtomic(string path, string content)
        {
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }

        private static bool IsIgnored(string path) => path.Split(Path.DirectorySeparatorChar).Any(part => part is ".git" or ".vs" or "bin" or "obj" or "node_modules") ||
            Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".zip" or ".pdf";
        private static string RequiredString(JsonObject value, string name) => value[name]?.GetValue<string>() ?? throw new ArgumentException($"{name} is required.");
        private static int RequiredInt(JsonObject value, string name) => value[name]?.GetValue<int>() ?? throw new ArgumentException($"{name} is required.");
        private static string Truncate(string value) => value.Length <= MaximumOutputCharacters ? value : value[..MaximumOutputCharacters] + "\n[output truncated]";
        private static ToolResult JsonResult(bool success, JsonObject value) => new(success, value.ToJsonString(new() { WriteIndented = true }));
        private static JsonObject Type(string type) => new() { ["type"] = type };
        private static JsonObject Props(params (string Name, string Type)[] values) => new(values.Select(value => KeyValuePair.Create<string, JsonNode?>(value.Name, Type(value.Type))));
        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
        {
            ["type"] = "function", ["name"] = name, ["description"] = description,
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray()) }
        };
    }
}
