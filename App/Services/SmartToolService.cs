using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using App.Models;

namespace App.Services
{
    internal sealed class SmartToolService
    {
        private const int MaximumFileBytes = 1_000_000;
        private const int MaximumReadResultCharacters = 60_000;
        private const int MaximumSearchFiles = 10;
        private const int MaximumMatchSnippetLength = 125;
        private readonly SecretaryToolService _secretaryTools;
        private readonly Func<IReadOnlyList<string>> _userMessages;

        public SmartToolService(SecretaryToolService secretaryTools, Func<IReadOnlyList<string>> userMessages)
        {
            _secretaryTools = secretaryTools;
            _userMessages = userMessages;
        }

        public static JsonArray CreateDeclarations() =>
        [
            Function("search_notes", "Use when the user wants saved notes whose text contains a phrase.", Props(("search_text", String("Literal text to find in saved note content."))), "search_text"),
            Function("search_todos", "Use when the user wants todos in one category whose text contains a phrase.", Props(("category_name", String("Exact todo category name.")), ("search_text", String("Literal text to find in todo content."))), "category_name", "search_text"),
            Function("get_local_context", "Use only for current device-local context: date/time, configured location, weather, clipboard text, language, or battery percentage.", Props(("context_type", SecretaryToolService.DataKindSchema())), "context_type"),
            Function("read_user_file", "Use to read a text file only after the user explicitly provides its absolute Windows path. Optional offsets select a zero-based, end-exclusive character range.", Props(("absolute_file_path", String("Full absolute Windows path explicitly provided by the user.")), ("start_offset", Integer("Optional zero-based first character to return.")), ("end_offset", Integer("Optional zero-based exclusive end character; requires start_offset."))), "absolute_file_path"),
            Function("search_user_directory", "Use to recursively find text matches inside files under an absolute Windows directory explicitly provided by the user. Returns file paths, line numbers, and snippets.", Props(("absolute_directory_path", String("Full absolute Windows directory path explicitly provided by the user.")), ("search_pattern", String("Case-insensitive .NET regular expression matched against file contents."))), "absolute_directory_path", "search_pattern"),
            Function("list_user_directory", "Use to discover files and folders under an absolute Windows directory explicitly provided by the user. Can limit recursion depth and filter entry names.", Props(("absolute_directory_path", String("Full absolute Windows directory path explicitly provided by the user.")), ("max_depth", Integer("Optional recursion depth from 0 to 10; defaults to 3.")), ("name_pattern", String("Optional case-insensitive .NET regular expression matched against entry names."))), "absolute_directory_path"),
            new JsonObject { ["type"] = "google_search" }
        ];

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "search_notes" or "search_todos" or "get_local_context" => await _secretaryTools.ExecuteAsync(name, arguments, token),
                    "read_user_file" => ReadFile(Required(arguments, "absolute_file_path"), OptionalRange(arguments)),
                    "search_user_directory" => SearchFiles(Required(arguments, "absolute_directory_path"), Required(arguments, "search_pattern")),
                    "list_user_directory" => ListFiles(Required(arguments, "absolute_directory_path"), OptionalInt(arguments, "max_depth", 3), Optional(arguments, "name_pattern")),
                    _ => Error("unknown_tool", $"Smart cannot use the tool “{name}”.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (FormatException exception) { return Error("invalid_arguments", exception.Message); }
            catch (UnauthorizedAccessException exception) { return Error("path_not_authorized", exception.Message); }
            catch (Exception exception) when (exception is IOException or ArgumentException or RegexParseException)
            {
                return Error("operation_failed", exception.Message);
            }
            catch (Exception)
            {
                return Error("operation_failed", "Smart could not complete that read-only operation.");
            }
        }

        private ToolResult ReadFile(string path, (int Start, int End)? range)
        {
            var fullPath = ResolveAuthorizedPath(path, requireDirectory: false);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return Error("file_not_found", "The file does not exist.");
            if (info.Length > MaximumFileBytes) return Error("file_too_large", "The file exceeds the 1 MB read limit.");
            var content = File.ReadAllText(fullPath);
            if (range is null)
            {
                var returned = content.Length <= MaximumReadResultCharacters ? content : content[..MaximumReadResultCharacters];
                return Ok(returned.Length == content.Length ? "Read the file." : "Read the first portion of the file; use start and end for another range.", new JsonObject
                {
                    ["path"] = fullPath, ["content"] = returned, ["truncated"] = returned.Length < content.Length, ["total_characters"] = content.Length
                });
            }

            ValidateRange(range.Value, content.Length);
            var selected = content[range.Value.Start..range.Value.End];
            var result = selected.Length <= MaximumReadResultCharacters ? selected : selected[..MaximumReadResultCharacters];
            return Ok("Read the selected character range.", new JsonObject
            {
                ["path"] = fullPath, ["content"] = result, ["start"] = range.Value.Start, ["end"] = range.Value.End, ["truncated"] = result.Length < selected.Length
            });
        }

        private ToolResult SearchFiles(string directory, string regexPattern)
        {
            var root = ResolveAuthorizedPath(directory, requireDirectory: true);
            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            var results = new JsonArray();
            foreach (var path in EnumerateFiles(root))
            {
                try
                {
                    if (new FileInfo(path).Length > MaximumFileBytes) continue;
                    var lineNumber = 0;
                    foreach (var line in File.ReadLines(path))
                    {
                        lineNumber++;
                        var match = regex.Match(line);
                        if (!match.Success) continue;
                        var (snippet, matchStart) = Snippet(line, match.Index, match.Length);
                        results.Add(new JsonObject
                        {
                            ["path"] = path, ["line"] = lineNumber, ["snippet"] = snippet,
                            ["start"] = matchStart, ["end"] = matchStart + Math.Min(match.Length, snippet.Length - matchStart)
                        });
                        break;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                if (results.Count >= MaximumSearchFiles) break;
            }
            return Ok($"Found matches in {results.Count} file(s).", new JsonObject { ["items"] = results });
        }

        private ToolResult ListFiles(string path, int depth, string pattern)
        {
            if (depth is < 0 or > 10) throw new FormatException("depth must be between 0 and 10.");
            var root = ResolveAuthorizedPath(path, requireDirectory: true);
            Regex? regex = string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            var items = new JsonArray(EnumerateEntries(root, depth)
                .Where(entry => regex is null || regex.IsMatch(Path.GetFileName(entry)))
                .Take(500)
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["path"] = entry,
                    ["type"] = Directory.Exists(entry) ? "directory" : "file"
                }).ToArray());
            return Ok($"Listed {items.Count} item(s).", new JsonObject { ["items"] = items });
        }

        private string ResolveAuthorizedPath(string path, bool requireDirectory)
        {
            if (!Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(Path.GetPathRoot(path)))
                throw new UnauthorizedAccessException("Include the full absolute Windows path in your message before asking Smart to inspect it.");

            var fullPath = Path.GetFullPath(path);
            if (requireDirectory && !Directory.Exists(fullPath))
                throw new DirectoryNotFoundException("The directory does not exist.");

            var authorizationRoot = FindAuthorizationRoot(fullPath);
            if (authorizationRoot is null)
                throw new UnauthorizedAccessException("That path was not explicitly supplied by you. Include the full absolute Windows path in your message, then try again.");

            ValidateNoReparsePoints(authorizationRoot, fullPath);
            return fullPath;
        }

        private string? FindAuthorizationRoot(string requestedPath)
        {
            var messages = _userMessages();
            var candidate = TrimTrailingSeparators(requestedPath);
            while (!string.IsNullOrWhiteSpace(candidate))
            {
                if (messages.Any(message => ContainsExplicitPath(message, candidate)))
                {
                    if (File.Exists(candidate))
                        return string.Equals(candidate, requestedPath, StringComparison.OrdinalIgnoreCase) ? candidate : null;
                    if (Directory.Exists(candidate)) return candidate;
                }

                var parent = Directory.GetParent(candidate)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase)) break;
                candidate = TrimTrailingSeparators(parent);
            }
            return null;
        }

        private static bool ContainsExplicitPath(string message, string path)
        {
            var start = 0;
            while ((start = message.IndexOf(path, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var beforeOkay = start == 0 || !char.IsLetterOrDigit(message[start - 1]);
                var end = start + path.Length;
                var afterOkay = end == message.Length || message[end] is '"' or '\'' or '`' or ')' or ']' or '}' or ',' or ';' or ':' or '.' or '!' or '?' || char.IsWhiteSpace(message[end]);
                if (!afterOkay && message[end] is '\\' or '/')
                {
                    var next = end + 1;
                    afterOkay = next == message.Length || message[next] is '"' or '\'' or '`' or ')' or ']' or '}' or ',' or ';' or ':' or '.' or '!' or '?' || char.IsWhiteSpace(message[next]);
                }
                if (beforeOkay && afterOkay) return true;
                start++;
            }
            return false;
        }

        private static string TrimTrailingSeparators(string path)
        {
            var root = Path.GetPathRoot(path) ?? string.Empty;
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(trimmed, trimmedRoot, StringComparison.OrdinalIgnoreCase) || trimmed.Length == 0
                ? root
                : trimmed;
        }

        private static void ValidateNoReparsePoints(string root, string path)
        {
            if (File.Exists(root) && string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
            {
                if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
                    throw new UnauthorizedAccessException("Reparse-point paths are not supported.");
                return;
            }

            var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Reparse-point paths are not supported.");
            var relative = Path.GetRelativePath(current, path);
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new UnauthorizedAccessException("Reparse-point paths are not supported.");
            }
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                IEnumerable<string> files;
                IEnumerable<string> directories;
                try
                {
                    files = Directory.EnumerateFiles(directory).ToArray();
                    directories = Directory.EnumerateDirectories(directory).ToArray();
                }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var file in files) yield return file;
                foreach (var child in directories)
                    if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) pending.Push(child);
            }
        }

        private static IEnumerable<string> EnumerateEntries(string root, int depth)
        {
            if (depth < 0 || new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) yield break;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(root); }
            catch (UnauthorizedAccessException) { yield break; }
            foreach (var entry in entries)
            {
                yield return entry;
                if (Directory.Exists(entry) && depth > 0 && !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                    foreach (var child in EnumerateEntries(entry, depth - 1)) yield return child;
            }
        }

        private static (string Text, int MatchStart) Snippet(string line, int matchStart, int matchLength)
        {
            if (line.Length <= MaximumMatchSnippetLength) return (line, matchStart);
            var snippetStart = Math.Clamp(matchStart - (MaximumMatchSnippetLength - Math.Min(matchLength, MaximumMatchSnippetLength)) / 2, 0, line.Length - MaximumMatchSnippetLength);
            return (line.Substring(snippetStart, MaximumMatchSnippetLength), matchStart - snippetStart);
        }

        private static void ValidateRange((int Start, int End) range, int length)
        {
            if (range.Start < 0 || range.End < range.Start || range.End > length)
                throw new FormatException($"The character range must satisfy 0 <= start <= end <= {length}.");
        }

        private static string Required(JsonObject arguments, string name) => Optional(arguments, name) is { Length: > 0 } value ? value : throw new FormatException($"{name} is required.");
        private static string Optional(JsonObject arguments, string name) => arguments[name]?.GetValue<string>()?.Trim() ?? string.Empty;
        private static int OptionalInt(JsonObject arguments, string name, int fallback) => arguments[name]?.GetValue<int>() ?? fallback;
        private static (int Start, int End)? OptionalRange(JsonObject arguments)
        {
            var start = arguments["start_offset"]?.GetValue<int>();
            var end = arguments["end_offset"]?.GetValue<int>();
            if (start.HasValue != end.HasValue) throw new FormatException("start_offset and end_offset must be provided together.");
            return start.HasValue ? (start.Value, end!.Value) : null;
        }

        private static JsonObject String(string? description = null) { var schema = new JsonObject { ["type"] = "string" }; if (description is not null) schema["description"] = description; return schema; }
        private static JsonObject Integer(string? description = null) { var schema = new JsonObject { ["type"] = "integer" }; if (description is not null) schema["description"] = description; return schema; }
        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties) { var result = new JsonObject(); foreach (var property in properties) result[property.Name] = property.Schema; return result; }
        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required)
        {
            var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required.Length > 0) parameters["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray());
            return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters };
        }
        private static ToolResult Ok(string summary, JsonObject? details = null) { var root = details ?? new JsonObject(); root.Insert(0, "summary", summary); root.Insert(0, "status", "completed"); return new ToolResult(true, root.ToJsonString()); }
        private static ToolResult Error(string category, string summary) => new(false, new JsonObject { ["status"] = "failed", ["error_category"] = category, ["summary"] = summary }.ToJsonString());
    }
}
