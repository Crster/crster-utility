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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace App.Services
{
    internal sealed class TechnicianToolService
    {
        private const int MaximumFileBytes = 1_000_000;
        private const int MaximumReadResultCharacters = 60_000;
        private const int MaximumCommandOutputCharacters = 20_000;
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
            Function("patch_file", "Atomically apply one or more text patches. Use old_text/new_text for one edit, or edits: [{ old_text, new_text, replace_all }]. Matching falls back from exact text to safe whitespace-aware matching; failures include the closest candidate. syntax_check is reserved for an optional C# syntax gate when Roslyn is available.", Props(("path", String()), ("old_text", String()), ("new_text", String()), ("replace_all", Boolean()), ("edits", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["properties"] = Props(("old_text", String()), ("new_text", String()), ("replace_all", Boolean())), ["required"] = new JsonArray("old_text", "new_text") } }), ("syntax_check", Boolean())), "path"),
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
                    "patch_file" => PatchFile(Required(arguments, "path"), arguments),
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
            if (range is null)
            {
                var initialContent = content.Length <= MaximumReadResultCharacters ? content : content[..MaximumReadResultCharacters];
                return Ok(content.Length <= MaximumReadResultCharacters ? "Read the file." : "Read the first portion of the file; use start and end to read additional ranges.", new JsonObject
                {
                    ["path"] = fullPath,
                    ["content"] = initialContent,
                    ["truncated"] = initialContent.Length < content.Length,
                    ["total_characters"] = content.Length
                });
            }

            ValidateRange(range.Value, content.Length);
            var selectedContent = content[range.Value.Start..range.Value.End];
            var returnedContent = selectedContent.Length <= MaximumReadResultCharacters ? selectedContent : selectedContent[..MaximumReadResultCharacters];
            return Ok("Read the selected character range.", new JsonObject
            {
                ["path"] = fullPath,
                ["content"] = returnedContent,
                ["start"] = range.Value.Start,
                ["end"] = range.Value.End,
                ["truncated"] = returnedContent.Length < selectedContent.Length
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

        private ToolResult PatchFile(string path, JsonObject arguments)
        {
            var fullPath = ResolveWorkspacePath(path);
            if (!File.Exists(fullPath)) return Error("file_not_found", "The file does not exist.");
            var content = File.ReadAllText(fullPath);
            var edits = ParsePatchEdits(arguments);
            var resolved = new List<ResolvedPatch>();

            // Resolve every edit against the untouched file so a partial patch can never be persisted.
            foreach (var edit in edits)
            {
                var matches = FindPatchMatches(content, edit);
                if (matches.Count == 0) return PatchNotFound(content, edit.OldText);
                if (!edit.ReplaceAll && matches.Count > 1) return PatchAmbiguous(content, matches);
                resolved.AddRange(matches.Select(match => new ResolvedPatch(match.Start, match.Length, match.NewText)));
            }

            var overlaps = resolved.OrderBy(match => match.Start).ToArray();
            for (var index = 1; index < overlaps.Length; index++)
                if (overlaps[index].Start < overlaps[index - 1].Start + overlaps[index - 1].Length)
                    return Error("patch_overlap", "Two edits target overlapping text. Refine the edits so each original range is distinct.");

            var patched = content;
            foreach (var patch in resolved.OrderByDescending(match => match.Start))
                patched = patched[..patch.Start] + patch.NewText + patched[(patch.Start + patch.Length)..];

            if (OptionalBoolean(arguments, "syntax_check") && Path.GetExtension(fullPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var errors = CSharpSyntaxTree.ParseText(patched).GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(10).ToArray();
                if (errors.Length > 0)
                    return Error("patch_syntax_error", $"C# syntax validation failed; no file was changed. {string.Join("; ", errors.Select(error => error.ToString()))}");
            }

            File.WriteAllText(fullPath, patched, new UTF8Encoding(false));
            var snippets = new JsonArray(resolved.OrderBy(patch => patch.Start).Select(patch => (JsonNode)new JsonObject
            {
                ["line_start"] = LineNumberAt(patched, TranslatePatchStart(patch.Start, resolved)),
                ["snippet"] = CreatePatchSnippet(patched, TranslatePatchStart(patch.Start, resolved), patch.NewText.Length)
            }).ToArray());
            return Ok($"Patched the file with {resolved.Count} edit(s).", new JsonObject { ["path"] = fullPath, ["edits"] = snippets });
        }

        private static List<PatchEdit> ParsePatchEdits(JsonObject arguments)
        {
            var edits = new List<PatchEdit>();
            if (arguments["edits"] is JsonArray editArray)
            {
                foreach (var node in editArray)
                {
                    if (node is not JsonObject edit) throw new FormatException("Every edits item must be an object.");
                    edits.Add(new PatchEdit(RequiredContent(edit, "old_text"), RequiredContent(edit, "new_text"), OptionalBoolean(edit, "replace_all")));
                }
            }
            else if (arguments.ContainsKey("old_text") || arguments.ContainsKey("new_text"))
            {
                edits.Add(new PatchEdit(RequiredContent(arguments, "old_text"), RequiredContent(arguments, "new_text"), OptionalBoolean(arguments, "replace_all")));
            }
            if (edits.Count == 0) throw new FormatException("Provide old_text and new_text, or a non-empty edits array.");
            if (edits.Any(edit => edit.OldText.Length == 0)) throw new FormatException("old_text cannot be empty.");
            return edits;
        }

        private static List<ResolvedPatch> FindPatchMatches(string content, PatchEdit edit)
        {
            var exact = FindOccurrences(content, edit.OldText).Select(start => new ResolvedPatch(start, edit.OldText.Length, edit.NewText)).ToList();
            if (exact.Count > 0) return exact;

            var normalized = FindNormalizedOccurrences(content, edit.OldText);
            if (normalized.Count > 0) return normalized.Select(match => new ResolvedPatch(match.Start, match.Length, edit.NewText)).ToList();

            var trimmedEnd = FindLineMatches(content, edit.OldText, trimAll: false);
            if (trimmedEnd.Count > 0) return trimmedEnd.Select(match => new ResolvedPatch(match.Start, match.Length, edit.NewText)).ToList();

            var fullyTrimmed = FindLineMatches(content, edit.OldText, trimAll: true);
            if (fullyTrimmed.Count > 0) return fullyTrimmed.Select(match => new ResolvedPatch(match.Start, match.Length, Reindent(edit.NewText, content[match.Start..(match.Start + match.Length)]))).ToList();

            var fuzzy = FindFuzzyLineMatches(content, edit.OldText);
            return fuzzy.Select(match => new ResolvedPatch(match.Start, match.Length, Reindent(edit.NewText, content[match.Start..(match.Start + match.Length)]))).ToList();
        }

        private static List<int> FindOccurrences(string content, string value)
        {
            var results = new List<int>();
            for (var start = 0; (start = content.IndexOf(value, start, StringComparison.Ordinal)) >= 0; start += Math.Max(1, value.Length)) results.Add(start);
            return results;
        }

        private static List<(int Start, int Length)> FindNormalizedOccurrences(string content, string oldText)
        {
            var (normalizedContent, positions) = NormalizeForPatch(content);
            var (normalizedOld, _) = NormalizeForPatch(oldText);
            var matches = new List<(int Start, int Length)>();
            for (var index = 0; (index = normalizedContent.IndexOf(normalizedOld, index, StringComparison.Ordinal)) >= 0; index += Math.Max(1, normalizedOld.Length))
            {
                var start = positions[index];
                var end = index + normalizedOld.Length < positions.Count ? positions[index + normalizedOld.Length] : content.Length;
                matches.Add((start, end - start));
            }
            return matches;
        }

        private static (string Text, List<int> Positions) NormalizeForPatch(string value)
        {
            var builder = new StringBuilder(value.Length);
            var positions = new List<int>(value.Length);
            for (var index = value.Length > 0 && value[0] == '\uFEFF' ? 1 : 0; index < value.Length; index++)
            {
                if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n') continue;
                builder.Append(value[index]);
                positions.Add(index);
            }
            return (builder.ToString(), positions);
        }

        private static List<(int Start, int Length)> FindLineMatches(string content, string oldText, bool trimAll)
        {
            var source = SplitLines(content);
            var expected = SplitLines(oldText);
            if (expected.Count == 0 || expected.Count > source.Count) return [];
            var matches = new List<(int Start, int Length)>();
            for (var start = 0; start <= source.Count - expected.Count; start++)
            {
                var matchesBlock = true;
                for (var offset = 0; offset < expected.Count; offset++)
                {
                    var actual = source[start + offset].Text;
                    var wanted = expected[offset].Text;
                    if (trimAll ? !string.Equals(actual.Trim(), wanted.Trim(), StringComparison.Ordinal) : !string.Equals(actual.TrimEnd(), wanted.TrimEnd(), StringComparison.Ordinal)) { matchesBlock = false; break; }
                }
                if (matchesBlock)
                {
                    var first = source[start];
                    var last = source[start + expected.Count - 1];
                    matches.Add((first.Start, last.End - first.Start));
                }
            }
            return matches;
        }

        private static List<(int Start, int Length)> FindFuzzyLineMatches(string content, string oldText)
        {
            var source = SplitLines(content);
            var expected = SplitLines(oldText);
            if (expected.Count == 0 || expected.Count > source.Count) return [];
            var expectedText = string.Join("\n", expected.Select(line => line.Text.Trim()));
            var matches = new List<(int Start, int Length)>();
            for (var start = 0; start <= source.Count - expected.Count; start++)
            {
                var candidate = string.Join("\n", source.Skip(start).Take(expected.Count).Select(line => line.Text.Trim()));
                if (Similarity(expectedText, candidate) < 0.95) continue;
                var first = source[start];
                var last = source[start + expected.Count - 1];
                matches.Add((first.Start, last.End - first.Start));
            }
            return matches;
        }

        private static ToolResult PatchNotFound(string content, string oldText)
        {
            var closest = FindClosestCandidate(content, oldText);
            return Error("patch_not_found", $"No match. Closest candidate lines {closest.StartLine}–{closest.EndLine} ({closest.Similarity:P0} similar):\n{closest.Snippet}");
        }

        private static ToolResult PatchAmbiguous(string content, IReadOnlyList<ResolvedPatch> matches)
        {
            var lines = string.Join(", ", matches.Take(10).Select(match => LineNumberAt(content, match.Start).ToString()));
            return Error("patch_ambiguous", $"Found {matches.Count} matches (lines {lines}) — add context or set replace_all.");
        }

        private static (int StartLine, int EndLine, double Similarity, string Snippet) FindClosestCandidate(string content, string oldText)
        {
            var source = SplitLines(content);
            var expected = SplitLines(oldText);
            var count = Math.Max(1, Math.Min(expected.Count, source.Count));
            var wanted = string.Join("\n", expected.Select(line => line.Text.Trim()));
            var bestStart = 0;
            var bestScore = -1d;
            for (var start = 0; start <= source.Count - count; start++)
            {
                var candidate = string.Join("\n", source.Skip(start).Take(count).Select(line => line.Text.Trim()));
                var score = Similarity(wanted, candidate);
                if (score > bestScore) { bestScore = score; bestStart = start; }
            }
            var first = source[bestStart];
            var last = source[bestStart + count - 1];
            return (bestStart + 1, bestStart + count, bestScore, CreatePatchSnippet(content, first.Start, last.End - first.Start));
        }

        private static double Similarity(string left, string right)
        {
            if (left.Length == 0 && right.Length == 0) return 1;
            var previous = Enumerable.Range(0, right.Length + 1).ToArray();
            for (var row = 1; row <= left.Length; row++)
            {
                var current = new int[right.Length + 1];
                current[0] = row;
                for (var column = 1; column <= right.Length; column++)
                    current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
                previous = current;
            }
            return 1d - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
        }

        private static List<PatchLine> SplitLines(string value)
        {
            var lines = new List<PatchLine>();
            var start = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '\n') continue;
                var textEnd = index > start && value[index - 1] == '\r' ? index - 1 : index;
                lines.Add(new PatchLine(start, index + 1, value[start..textEnd]));
                start = index + 1;
            }
            if (start < value.Length || value.Length == 0) lines.Add(new PatchLine(start, value.Length, value[start..]));
            return lines;
        }

        private static string Reindent(string newText, string matchedBlock)
        {
            var firstContentLine = SplitLines(matchedBlock).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line.Text));
            var targetIndent = firstContentLine?.Text ?? string.Empty;
            targetIndent = targetIndent[..(targetIndent.Length - targetIndent.TrimStart().Length)];
            var lines = SplitLines(newText);
            var indents = lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).Select(line => line.Text.Length - line.Text.TrimStart().Length).ToArray();
            var sourceIndent = indents.Length == 0 ? 0 : indents.Min();
            return string.Concat(lines.Select(line => string.IsNullOrWhiteSpace(line.Text) ? line.Text + NewLineSuffix(newText, line) : targetIndent + line.Text[Math.Min(sourceIndent, line.Text.Length)..] + NewLineSuffix(newText, line)));
        }

        private static string NewLineSuffix(string text, PatchLine line) => line.End > line.Start && text[line.End - 1] == '\n' ? (line.End > line.Start + 1 && text[line.End - 2] == '\r' ? "\r\n" : "\n") : string.Empty;

        private static int TranslatePatchStart(int originalStart, IEnumerable<ResolvedPatch> patches) => originalStart + patches.Where(patch => patch.Start < originalStart).Sum(patch => patch.NewText.Length - patch.Length);

        private static int LineNumberAt(string content, int position) => 1 + content.Take(position).Count(character => character == '\n');

        private static string CreatePatchSnippet(string content, int start, int length)
        {
            var firstLine = Math.Max(0, LineNumberAt(content, start) - 4);
            var lastLine = LineNumberAt(content, Math.Min(content.Length, start + length)) + 3;
            return string.Join("\n", SplitLines(content).Skip(firstLine).Take(lastLine - firstLine).Select(line => line.Text));
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
            var stdout = await outputTask;
            var stderr = await errorTask;
            return Ok("Command completed.", new JsonObject
            {
                ["exit_code"] = process.ExitCode,
                ["stdout"] = TruncateOutput(stdout),
                ["stderr"] = TruncateOutput(stderr),
                ["output_truncated"] = stdout.Length > MaximumCommandOutputCharacters || stderr.Length > MaximumCommandOutputCharacters
            });
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
                var result = await _client.CreateGroundedInteractionAsync("gemini-3.5-flash-lite", topic,
                    "Research the topic using current authoritative sources. Return concise, practical context for another agent to act on; identify version-specific facts and cite sources.", token);
                if (string.IsNullOrWhiteSpace(result.Text)) return Error("research_unavailable", "gemini-3.5-flash-lite did not return research context.");
                return Ok("Generated grounded research context.", new JsonObject { ["context"] = result.Text, ["sources"] = new JsonArray(result.Sources.Select(source => (JsonNode)new JsonObject { ["title"] = source.Title, ["uri"] = source.Uri }).ToArray()) });
            }
            catch (Exception)
            {
                return Error("research_unavailable", "gemini-3.5-flash-lite with Google Search grounding is unavailable for this API key.");
            }
        }

        private async Task<ToolResult> PlanAsync(string request, CancellationToken token)
        {
            var result = await _client.CreateSimpleInteractionAsync("gemini-3.5-flash-lite", [], [GeminiClient.CreateUserStep(request, [])],
                "Create a comprehensive, implementation-ready plan. Do not execute changes. State assumptions, risks, interfaces, and validation.", null, token);
            return string.IsNullOrWhiteSpace(result.Text) ? Error("plan_unavailable", "gemini-3.5-flash-lite did not return a plan.") : Ok("Generated an implementation plan.", new JsonObject { ["plan"] = result.Text });
        }

        private async Task<ToolResult> DesignAsync(string request, CancellationToken token)
        {
            var result = await _client.CreateSimpleInteractionAsync("gemini-3.5-flash-lite", [], [GeminiClient.CreateUserStep(request, [])],
                "Act as a senior UI/UX designer. Create a concise, implementation-ready design brief without editing files. Prefer current UI/UX design trends and contemporary visual conventions, while preserving and extending existing product patterns when context is available. Specify the user goal, information hierarchy, layout and responsive behavior, component and interaction states, accessibility requirements, visual direction, and implementation considerations. Prefer practical, consistent decisions over generic design advice.", null, token);
            return string.IsNullOrWhiteSpace(result.Text) ? Error("design_unavailable", "gemini-3.5-flash-lite did not return a design brief.") : Ok("Generated a UI/UX design brief.", new JsonObject { ["design"] = result.Text });
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
        private static string TruncateOutput(string value) => value.Length <= MaximumCommandOutputCharacters ? value : value[..MaximumCommandOutputCharacters] + "\n[Output truncated]";
        private static void ValidateRange((int Start, int End) range, int length)
        {
            if (range.Start < 0 || range.End < range.Start || range.End > length)
                throw new FormatException($"The character range must satisfy 0 <= start <= end <= {length}.");
        }
        private static string Required(JsonObject arguments, string name) => Optional(arguments, name) is { Length: > 0 } value ? value : throw new FormatException($"{name} is required.");
        private static string RequiredContent(JsonObject arguments, string name) => arguments[name]?.GetValue<string>() ?? throw new FormatException($"{name} is required.");
        private static string Optional(JsonObject arguments, string name) => arguments[name]?.GetValue<string>()?.Trim() ?? string.Empty;
        private static int OptionalInt(JsonObject arguments, string name, int fallback) => arguments[name]?.GetValue<int>() ?? fallback;
        private static bool OptionalBoolean(JsonObject arguments, string name) => arguments[name]?.GetValue<bool>() ?? false;
        private static (int Start, int End)? OptionalRange(JsonObject arguments)
        {
            var start = arguments["start"]?.GetValue<int>();
            var end = arguments["end"]?.GetValue<int>();
            if (start.HasValue != end.HasValue) throw new FormatException("start and end must be provided together.");
            return start.HasValue ? (start.Value, end!.Value) : null;
        }
        private static JsonObject String() => new() { ["type"] = "string" };
        private static JsonObject Integer() => new() { ["type"] = "integer" };
        private static JsonObject Boolean() => new() { ["type"] = "boolean" };
        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties) { var result = new JsonObject(); foreach (var property in properties) result[property.Name] = property.Schema; return result; }
        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) { var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties }; if (required.Length > 0) parameters["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray()); return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters }; }
        private static ToolResult Ok(string summary, JsonObject? details = null) { var root = details ?? new JsonObject(); root.Insert(0, "summary", summary); root.Insert(0, "status", "completed"); return new ToolResult(true, root.ToJsonString()); }
        private static ToolResult Error(string category, string summary) => new(false, new JsonObject { ["status"] = "failed", ["error_category"] = category, ["summary"] = summary }.ToJsonString());

        private sealed record PatchEdit(string OldText, string NewText, bool ReplaceAll);
        private sealed record ResolvedPatch(int Start, int Length, string NewText);
        private sealed record PatchLine(int Start, int End, string Text);
    }
}
