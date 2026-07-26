using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using App.Models;

namespace App.Services
{
    internal sealed class TechnicianToolService
    {
        private const int MaximumFileBytes = 1_000_000;
        private const int MaximumSearchFiles = 10;
        private const int MaximumMatchSnippetLength = 125;
        private readonly GeminiClient _client;
        private readonly TechnicianMemoryService _memory;
        private readonly SecretaryToolService _secretaryTools;
        private readonly Func<string, Task<bool>> _confirmAsync;
        private readonly Func<Task<ToolResult>> _compactAsync;
        private readonly Func<Task<ToolResult>> _cleanupAsync;
        private readonly Action _enableHighThinking;
        private readonly Action _resetHighThinking;

        public TechnicianToolService(GeminiClient client, TechnicianMemoryService memory, SecretaryToolService secretaryTools,
            Func<string, Task<bool>> confirmAsync, Func<Task<ToolResult>> compactAsync, Func<Task<ToolResult>> cleanupAsync,
            Action enableHighThinking, Action resetHighThinking)
        {
            _client = client;
            _memory = memory;
            _secretaryTools = secretaryTools;
            _confirmAsync = confirmAsync;
            _compactAsync = compactAsync;
            _cleanupAsync = cleanupAsync;
            _enableHighThinking = enableHighThinking;
            _resetHighThinking = resetHighThinking;
        }

        public string WorkspacePath { get; set; } = string.Empty;

        public static JsonArray CreateDeclarations() =>
        [
            Function("read_file", "Read a text file inside the selected workspace. Optionally provide zero-based, end-exclusive start and end offsets to read a character range.", Props(("path", String()), ("start", Integer()), ("end", Integer())), "path"),
            Function("write_file", "Write text to a file inside the selected workspace. Creates parent folders when needed. Optionally provide zero-based, end-exclusive start and end offsets to replace a character range in an existing file; provide neither offset to replace the whole file.", Props(("path", String()), ("content", String()), ("start", Integer()), ("end", Integer())), "path", "content"),
            Function("patch_file", "Replace one exact text block in a workspace file. Read the file first and use its exact old_text.", Props(("path", String()), ("old_text", String()), ("new_text", String())), "path", "old_text", "new_text"),
            Function("delete_file", "Delete a file or empty directory inside the workspace after user confirmation.", Props(("path", String())), "path"),
            Function("search_file", "Recursively search workspace files in a directory by regular expression, like grep. Returns matching file paths and contextual line snippets up to 125 characters; start and end are zero-based, end-exclusive offsets of the match in each snippet.", Props(("directory", String()), ("regex_pattern", String())), "directory", "regex_pattern"),
            Function("list_file_and_directory", "List workspace files and directories. Optionally filter by regex and depth.", Props(("path", String()), ("depth", Integer()), ("regex", String())), "path"),
            Function("execute", "Run a non-risky command in the selected workspace and return stdout, stderr, and exit code.", Props(("command", String()), ("arguments", String())), "command"),
            Function("execute_sudo", "Run a command elevated through a Windows UAC prompt after confirmation.", Props(("command", String()), ("arguments", String())), "command"),
            Function("list_process", "List running processes.", new JsonObject()),
            Function("kill_process", "Terminate a process by process ID after confirmation.", Props(("process_id", Integer())), "process_id"),
            Function("read_memo", "Semantically search the Technician's short-term memory before working on a topic.", Props(("query", String())), "query"),
            Function("write_memo", "Save a concise, useful Technician memory about the workspace or user preference.", Props(("value", String())), "value"),
            Function("clear_memo", "Clear all Technician short-term memory when explicitly requested.", new JsonObject()),
            Function("think", "Enable high thinking for a difficult or repeated unresolved problem.", new JsonObject()),
            Function("compact", "Build rich continuation context from the Technician chat, workspace, and memos, then clear chat and memos.", new JsonObject()),
            Function("clean_up", "Clear Technician chat and Context-panel text while retaining Technician memory.", new JsonObject()),
            Function("research", "Research current official documentation with Google Search grounding and return source-linked context.", Props(("topic", String())), "topic"),
            Function("plan", "Create a comprehensive implementation plan without editing workspace files.", Props(("request", String())), "request"),
            Function("design", "Create a UI/UX design brief that preserves consistent product patterns and guides a beautiful, accessible interface without editing workspace files.", Props(("request", String())), "request"),
            Function("get_data", "Return only one of these local data values: local date/time, configured location, weather, clipboard text, language, or battery percentage. It cannot obtain any other data.", Props(("kind", SecretaryToolService.DataKindSchema())), "kind")
        ];

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                if (RequiresWorkspace(name) && !HasWorkspace())
                    return Error("workspace_required", "Select a Technician workspace before running local operations.");

                return name switch
                {
                    "read_file" => ReadFile(Required(arguments, "path"), OptionalRange(arguments)),
                    "write_file" => WriteFile(Required(arguments, "path"), RequiredContent(arguments, "content"), OptionalRange(arguments)),
                    "patch_file" => PatchFile(Required(arguments, "path"), Required(arguments, "old_text"), Required(arguments, "new_text")),
                    "delete_file" => await DeleteFileAsync(Required(arguments, "path")),
                    "search_file" => SearchFiles(Required(arguments, "directory"), Required(arguments, "regex_pattern")),
                    "list_file_and_directory" => ListFiles(Optional(arguments, "path"), OptionalInt(arguments, "depth", 3), Optional(arguments, "regex")),
                    "execute" => await ExecuteCommandAsync(Required(arguments, "command"), Optional(arguments, "arguments"), false, token),
                    "execute_sudo" => await ExecuteCommandAsync(Required(arguments, "command"), Optional(arguments, "arguments"), true, token),
                    "list_process" => ListProcesses(),
                    "kill_process" => await KillProcessAsync(OptionalInt(arguments, "process_id", 0)),
                    "read_memo" => await ReadMemoAsync(Required(arguments, "query"), token),
                    "write_memo" => await WriteMemoAsync(Required(arguments, "value"), token),
                    "clear_memo" => ClearMemo(),
                    "think" => EnableHighThinking(),
                    "compact" => await CompactAsync(),
                    "clean_up" => await CleanUpAsync(),
                    "research" => await ResearchAsync(Required(arguments, "topic"), token),
                    "plan" => await PlanAsync(Required(arguments, "request"), token),
                    "design" => await DesignAsync(Required(arguments, "request"), token),
                    "get_data" => await _secretaryTools.ExecuteAsync("get_data", arguments, token),
                    _ => Error("unknown_tool", $"Technician cannot use the tool “{name}”.")
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
            {
                return Error("operation_failed", exception.Message);
            }
            catch (Win32Exception exception)
            {
                return Error("command_unavailable", exception.Message);
            }
            catch (Exception)
            {
                return Error("operation_failed", "Technician could not complete that local operation.");
            }
        }

        private ToolResult ReadFile(string path, (int Start, int End)? range)
        {
            var fullPath = ResolveWorkspacePath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return Error("file_not_found", "The file does not exist.");
            if (info.Length > MaximumFileBytes) return Error("file_too_large", "The file exceeds the 1 MB read limit.");
            var content = File.ReadAllText(fullPath);
            if (range is null) return Ok("Read the file.", new JsonObject { ["path"] = fullPath, ["content"] = content });

            ValidateRange(range.Value, content.Length);
            return Ok("Read the selected character range.", new JsonObject
            {
                ["path"] = fullPath,
                ["content"] = content[range.Value.Start..range.Value.End],
                ["start"] = range.Value.Start,
                ["end"] = range.Value.End
            });
        }

        private ToolResult WriteFile(string path, string content, (int Start, int End)? range)
        {
            var fullPath = ResolveWorkspacePath(path);
            if (range is not null)
            {
                if (!File.Exists(fullPath)) return Error("file_not_found", "A character-range write requires an existing file.");
                var existingContent = File.ReadAllText(fullPath);
                ValidateRange(range.Value, existingContent.Length);
                content = existingContent[..range.Value.Start] + content + existingContent[range.Value.End..];
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            return Ok("Wrote the file.", new JsonObject { ["path"] = fullPath, ["bytes"] = new FileInfo(fullPath).Length });
        }

        private ToolResult PatchFile(string path, string oldText, string newText)
        {
            var fullPath = ResolveWorkspacePath(path);
            var content = File.ReadAllText(fullPath);
            var position = content.IndexOf(oldText, StringComparison.Ordinal);
            if (position < 0) return Error("patch_not_found", "old_text was not found exactly once in the file.");
            if (content.IndexOf(oldText, position + oldText.Length, StringComparison.Ordinal) >= 0)
                return Error("patch_ambiguous", "old_text occurs more than once; use a more specific text block.");
            File.WriteAllText(fullPath, content[..position] + newText + content[(position + oldText.Length)..], new UTF8Encoding(false));
            return Ok("Patched the file.", new JsonObject { ["path"] = fullPath });
        }

        private async Task<ToolResult> DeleteFileAsync(string path)
        {
            var fullPath = ResolveWorkspacePath(path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return Error("path_not_found", "The file or directory does not exist.");
            if (!await _confirmAsync($"Delete '{fullPath}'? This cannot be undone.")) return Error("confirmation_declined", "The user did not approve deletion.");
            if (File.Exists(fullPath)) File.Delete(fullPath); else Directory.Delete(fullPath, false);
            return Ok("Deleted the selected path.", new JsonObject { ["path"] = fullPath });
        }

        private ToolResult SearchFiles(string directory, string regexPattern)
        {
            var root = ResolveWorkspacePath(directory);
            if (!Directory.Exists(root)) return Error("directory_not_found", "The directory does not exist.");

            var pattern = new Regex(regexPattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            var results = new JsonArray();
            foreach (var path in EnumerateEntries(root, 12).Where(File.Exists))
            {
                try
                {
                    if (new FileInfo(path).Length > MaximumFileBytes) continue;
                    var content = File.ReadAllText(path);
                    var matches = pattern.Matches(content);
                    if (matches.Count == 0) continue;

                    var snippets = new JsonArray();
                    foreach (Match match in matches)
                    {
                        var (snippet, start) = CreateMatchSnippet(content, match.Index, match.Length);
                        snippets.Add(new JsonObject
                        {
                            ["match"] = snippet,
                            ["start"] = start,
                            ["end"] = start + match.Length
                        });
                    }
                    results.Add(new JsonObject { ["file"] = path, ["matches"] = snippets });
                    if (results.Count >= MaximumSearchFiles) break;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return Ok($"Found matches in {results.Count} file(s).", new JsonObject { ["files"] = results });
        }

        private static (string Snippet, int MatchStart) CreateMatchSnippet(string content, int matchIndex, int matchLength)
        {
            var lineStart = content.LastIndexOf('\n', Math.Max(0, matchIndex - 1)) + 1;
            var lineEnd = content.IndexOf('\n', matchIndex + matchLength);
            if (lineEnd < 0) lineEnd = content.Length;
            var line = content[lineStart..lineEnd].TrimEnd('\r');
            var matchStart = matchIndex - lineStart;
            if (line.Length <= MaximumMatchSnippetLength) return (line, matchStart);

            var snippetStart = Math.Clamp(matchStart - (MaximumMatchSnippetLength - matchLength) / 2, 0, line.Length - MaximumMatchSnippetLength);
            return (line.Substring(snippetStart, MaximumMatchSnippetLength), matchStart - snippetStart);
        }

        private ToolResult ListFiles(string path, int depth, string regex)
        {
            var root = ResolveWorkspacePath(string.IsNullOrWhiteSpace(path) ? "." : path);
            if (!Directory.Exists(root)) return Error("directory_not_found", "The path is not a directory.");
            Regex? filter = string.IsNullOrWhiteSpace(regex) ? null : new Regex(regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            var items = EnumerateEntries(root, Math.Clamp(depth, 0, 12))
                .Where(item => filter is null || filter.IsMatch(item))
                .Take(500)
                .Select(item => (JsonNode)new JsonObject
                {
                    ["path"] = item,
                    ["name"] = Path.GetFileName(item),
                    ["extension"] = Path.GetExtension(item),
                    ["attribute"] = File.GetAttributes(item).ToString(),
                    ["size"] = File.Exists(item) ? new FileInfo(item).Length : 0
                }).ToArray();
            return Ok($"Listed {items.Length} item(s).", new JsonObject { ["items"] = new JsonArray(items) });
        }

        private async Task<ToolResult> ExecuteCommandAsync(string command, string arguments, bool elevated, CancellationToken token)
        {
            EnsureWorkspace();
            if (elevated || IsRiskyCommand(command, arguments))
            {
                if (!await _confirmAsync($"Run {(elevated ? "elevated " : string.Empty)}command '{command} {arguments}' in '{WorkspacePath}'?"))
                    return Error("confirmation_declined", "The user did not approve the command.");
            }
            if (elevated)
            {
                var start = new ProcessStartInfo(command, arguments) { WorkingDirectory = WorkspacePath, UseShellExecute = true, Verb = "runas" };
                Process.Start(start);
                return Ok("Started the elevated command. Windows does not return its output to this app.");
            }

            using var process = new Process { StartInfo = new ProcessStartInfo(command, arguments) { WorkingDirectory = WorkspacePath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return Ok("Command completed.", new JsonObject { ["exit_code"] = process.ExitCode, ["stdout"] = await outputTask, ["stderr"] = await errorTask });
        }

        private ToolResult ListProcesses()
        {
            var items = Process.GetProcesses().OrderBy(process => process.ProcessName).Take(500).Select(process => (JsonNode)new JsonObject
            {
                ["process_id"] = process.Id, ["name"] = process.ProcessName
            }).ToArray();
            return Ok($"Listed {items.Length} process(es).", new JsonObject { ["items"] = new JsonArray(items) });
        }

        private async Task<ToolResult> KillProcessAsync(int processId)
        {
            if (processId <= 0) return Error("invalid_process_id", "process_id must be a positive number.");
            var process = Process.GetProcessById(processId);
            if (!await _confirmAsync($"Terminate process '{process.ProcessName}' (PID {processId})?")) return Error("confirmation_declined", "The user did not approve process termination.");
            process.Kill();
            return Ok("Terminated the process.", new JsonObject { ["process_id"] = processId });
        }

        private async Task<ToolResult> ReadMemoAsync(string query, CancellationToken token)
        {
            var items = await _memory.ReadAsync(query, 10, token);
            return Ok($"Found {items.Count} memo(s).", new JsonObject { ["items"] = new JsonArray(items.Select(item => (JsonNode)new JsonObject { ["key"] = item.Key, ["value"] = item.Value }).ToArray()) });
        }

        private async Task<ToolResult> WriteMemoAsync(string value, CancellationToken token)
        {
            var memo = await _memory.WriteAsync(value, token);
            return Ok("Saved the memo.", new JsonObject { ["key"] = memo.Key });
        }

        private ToolResult ClearMemo()
        {
            _resetHighThinking();
            _memory.Clear();
            return Ok("Cleared Technician memory.");
        }

        private ToolResult EnableHighThinking()
        {
            _enableHighThinking();
            return Ok("Enabled high thinking for the current Technician request.");
        }

        private async Task<ToolResult> CompactAsync()
        {
            _resetHighThinking();
            return await _compactAsync();
        }

        private async Task<ToolResult> CleanUpAsync()
        {
            _resetHighThinking();
            return await _cleanupAsync();
        }

        private async Task<ToolResult> ResearchAsync(string topic, CancellationToken token)
        {
            try
            {
                var result = await _client.CreateGroundedInteractionAsync("gemini-3.6-flash", topic,
                    "Research the topic using current authoritative sources. Return concise, practical context for another agent to act on; identify version-specific facts and cite sources.", token);
                if (string.IsNullOrWhiteSpace(result.Text)) return Error("research_unavailable", "gemini-3.6-flash did not return research context.");
                return Ok("Generated grounded research context.", new JsonObject { ["context"] = result.Text, ["sources"] = new JsonArray(result.Sources.Select(source => (JsonNode)new JsonObject { ["title"] = source.Title, ["uri"] = source.Uri }).ToArray()) });
            }
            catch (Exception)
            {
                return Error("research_unavailable", "gemini-3.6-flash with Google Search grounding is unavailable for this API key.");
            }
        }

        private async Task<ToolResult> PlanAsync(string request, CancellationToken token)
        {
            var result = await _client.CreateSimpleInteractionAsync("gemini-3.6-flash", [], [GeminiClient.CreateUserStep(request, [])],
                "Create a comprehensive, implementation-ready plan. Do not execute changes. State assumptions, risks, interfaces, and validation.", null, token);
            return string.IsNullOrWhiteSpace(result.Text) ? Error("plan_unavailable", "gemini-3.6-flash did not return a plan.") : Ok("Generated an implementation plan.", new JsonObject { ["plan"] = result.Text });
        }

        private async Task<ToolResult> DesignAsync(string request, CancellationToken token)
        {
            var result = await _client.CreateSimpleInteractionAsync("gemini-3.6-flash", [], [GeminiClient.CreateUserStep(request, [])],
                "Act as a senior UI/UX designer. Create a concise, implementation-ready design brief without editing files. Prefer current UI/UX design trends and contemporary visual conventions, while preserving and extending existing product patterns when context is available. Specify the user goal, information hierarchy, layout and responsive behavior, component and interaction states, accessibility requirements, visual direction, and implementation considerations. Prefer practical, consistent decisions over generic design advice.", null, token);
            return string.IsNullOrWhiteSpace(result.Text) ? Error("design_unavailable", "gemini-3.6-flash did not return a design brief.") : Ok("Generated a UI/UX design brief.", new JsonObject { ["design"] = result.Text });
        }

        private string ResolveWorkspacePath(string path)
        {
            EnsureWorkspace();
            var rootPath = Path.GetFullPath(WorkspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = rootPath + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path));
            if (!string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The path is outside the selected workspace.");
            if (!string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase)) ValidateNoReparsePoints(root, fullPath);
            return fullPath;
        }

        private void EnsureWorkspace()
        {
            if (!HasWorkspace()) throw new InvalidOperationException("Choose an existing Technician workspace first.");
        }

        private bool HasWorkspace() => !string.IsNullOrWhiteSpace(WorkspacePath) && Directory.Exists(WorkspacePath);

        private static bool RequiresWorkspace(string toolName) => toolName is
            "read_file" or "write_file" or "patch_file" or "delete_file" or "search_file" or "list_file_and_directory"
            or "execute" or "execute_sudo" or "list_process" or "kill_process";

        private static void ValidateNoReparsePoints(string root, string path)
        {
            var current = root.TrimEnd(Path.DirectorySeparatorChar);
            var relative = path[root.Length..];
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new UnauthorizedAccessException("Workspace reparse points are not supported.");
            }
        }

        private static IEnumerable<string> EnumerateEntries(string root, int depth)
        {
            if (depth < 0 || new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) yield break;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(root); } catch (UnauthorizedAccessException) { yield break; }
            foreach (var entry in entries)
            {
                yield return entry;
                if (Directory.Exists(entry) && depth > 0 && !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                    foreach (var child in EnumerateEntries(entry, depth - 1)) yield return child;
            }
        }

        private static bool IsRiskyCommand(string command, string arguments) => Regex.IsMatch($"{command} {arguments}", @"\b(rm|rmdir|rd|del|erase|format|diskpart|cipher|mkdir|md|copy|xcopy|robocopy|move|ren|rename|Remove-Item|Clear-Content|New-Item|Copy-Item|Move-Item|Rename-Item|Set-Content|Add-Content|Out-File|Set-ItemProperty|Remove-ItemProperty|reg(?:\.exe)?|regedit|takeown|icacls|cacls|attrib|bcdedit|shutdown|restart|taskkill|Stop-Process|Stop-Service|Restart-Service|sc|net|msiexec|winget|choco|scoop|Set-ExecutionPolicy)\b", RegexOptions.IgnoreCase);
        private static void ValidateRange((int Start, int End) range, int length)
        {
            if (range.Start < 0 || range.End < range.Start || range.End > length)
                throw new FormatException($"The character range must satisfy 0 <= start <= end <= {length}.");
        }
        private static string Required(JsonObject arguments, string name) => Optional(arguments, name) is { Length: > 0 } value ? value : throw new FormatException($"{name} is required.");
        private static string RequiredContent(JsonObject arguments, string name) => arguments[name]?.GetValue<string>() ?? throw new FormatException($"{name} is required.");
        private static string Optional(JsonObject arguments, string name) => arguments[name]?.GetValue<string>()?.Trim() ?? string.Empty;
        private static int OptionalInt(JsonObject arguments, string name, int fallback) => arguments[name]?.GetValue<int>() ?? fallback;
        private static (int Start, int End)? OptionalRange(JsonObject arguments)
        {
            var start = arguments["start"]?.GetValue<int>();
            var end = arguments["end"]?.GetValue<int>();
            if (start.HasValue != end.HasValue) throw new FormatException("start and end must be provided together.");
            return start.HasValue ? (start.Value, end!.Value) : null;
        }
        private static JsonObject String() => new() { ["type"] = "string" };
        private static JsonObject Integer() => new() { ["type"] = "integer" };
        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties) { var result = new JsonObject(); foreach (var property in properties) result[property.Name] = property.Schema; return result; }
        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) { var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties }; if (required.Length > 0) parameters["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray()); return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters }; }
        private static ToolResult Ok(string summary, JsonObject? details = null) { var root = details ?? new JsonObject(); root.Insert(0, "summary", summary); root.Insert(0, "status", "completed"); return new ToolResult(true, root.ToJsonString()); }
        private static ToolResult Error(string category, string summary) => new(false, new JsonObject { ["status"] = "failed", ["error_category"] = category, ["summary"] = summary }.ToJsonString());
    }
}
