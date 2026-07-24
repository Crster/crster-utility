using App.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class PlannerToolService
    {
        private const int MaximumCandidateFiles = 2_000;
        private const int MaximumFileBytes = 1 * 1024 * 1024;
        private const int MaximumResultCharacters = 30_000;
        private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", ".next", "dist", "build"
        };
        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".xml", ".json", ".md", ".txt", ".js", ".jsx", ".ts", ".tsx",
            ".css", ".scss", ".html", ".py", ".java", ".kt", ".swift", ".go", ".rs", ".sql",
            ".yml", ".yaml", ".toml", ".ini", ".conf", ".env", ".ps1", ".sh", ".bat", ".cmd"
        };

        private readonly ChatToolService _fileTools;
        private readonly GeminiClient _client;

        public PlannerToolService(ChatToolService fileTools, GeminiClient client)
        {
            _fileTools = fileTools;
            _client = client;
        }

        public static JsonArray CreateDeclarations() => new()
        {
            Function("read_file", "Read a UTF-8 text file. offset and limit are zero-based line offset and line count.", Props(
                ("path", String()), ("offset", Integer()), ("limit", Integer())), "path"),
            Function("list_directory", "List directory entries without changing them. Recursive traversal does not follow reparse points.", Props(
                ("path", String()), ("recursive", Boolean()), ("max_depth", Integer()), ("include_hidden", Boolean())), "path"),
            Function("grep", "Search text file contents using a regular expression.", Props(
                ("pattern", String()), ("path", String()), ("file_glob", String()), ("case_sensitive", Boolean()), ("max_results", Integer())), "pattern", "path"),
            Function("fuzzy_search_file", "Find files whose names or contents are relevant to a topic and return bounded excerpts from the best matches.", Props(
                ("topic", String()), ("path", String()), ("max_results", Integer())), "topic", "path"),
            Function("load_skill", "Research a technical topic on the web and load current, source-grounded guidance, prioritizing official documentation and recent versions.", Props(
                ("topic", String())), "topic")
        };

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "read_file" or "list_directory" or "grep" => await _fileTools.ExecuteAsync(name, arguments, token),
                    "fuzzy_search_file" => FuzzySearchFiles(arguments),
                    "load_skill" => await LoadSkillAsync(RequiredString(arguments, "topic"), token),
                    _ => Error("unknown_tool", $"Unknown Planner tool: {name}")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException exception) { return Error("access_denied", exception.Message); }
            catch (Exception exception) { return Error("operation_failed", exception.Message); }
        }

        private static ToolResult FuzzySearchFiles(JsonObject arguments)
        {
            var topic = RequiredString(arguments, "topic").Trim();
            var root = Path.GetFullPath(RequiredString(arguments, "path"));
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

            var terms = Regex.Matches(topic, @"[\p{L}\p{N}_-]{2,}")
                .Select(match => match.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (terms.Length == 0) throw new ArgumentException("topic must contain searchable words.");

            var matches = new List<(string Path, int Score, string Content)>();
            foreach (var path in EnumerateTextFiles(root).Take(MaximumCandidateFiles))
            {
                var info = new FileInfo(path);
                if (info.Length > MaximumFileBytes) continue;
                string content;
                try { content = File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

                var relativePath = Path.GetRelativePath(root, path);
                var score = terms.Sum(term =>
                    CountOccurrences(relativePath, term) * 12
                    + Math.Min(CountOccurrences(content, term), 20));
                if (score > 0) matches.Add((path, score, content));
            }

            var requestedCount = arguments["max_results"]?.GetValue<int>() ?? 6;
            var remainingCharacters = MaximumResultCharacters;
            var results = new JsonArray();
            foreach (var match in matches.OrderByDescending(item => item.Score).ThenBy(item => item.Path).Take(Math.Clamp(requestedCount, 1, 12)))
            {
                var excerpt = CreateRelevantExcerpt(match.Content, terms, Math.Min(6_000, remainingCharacters));
                if (excerpt.Length == 0) break;
                remainingCharacters -= excerpt.Length;
                results.Add(new JsonObject
                {
                    ["path"] = match.Path,
                    ["score"] = match.Score,
                    ["content"] = excerpt,
                    ["truncated"] = excerpt.Length < match.Content.Length
                });
                if (remainingCharacters <= 0) break;
            }

            return Ok(new JsonObject
            {
                ["summary"] = $"Found {results.Count} file(s) relevant to \"{topic}\".",
                ["root"] = root,
                ["files"] = results,
                ["scanned_limit"] = MaximumCandidateFiles
            });
        }

        private async Task<ToolResult> LoadSkillAsync(string topic, CancellationToken token)
        {
            var result = await _client.LoadSkillAsync(topic, token);
            return Ok(new JsonObject
            {
                ["summary"] = $"Loaded current guidance for \"{topic}\".",
                ["content"] = result.Text,
                ["sources"] = new JsonArray(result.Sources
                    .DistinctBy(source => source.Uri)
                    .Select(source => (JsonNode)new JsonObject { ["title"] = source.Title, ["uri"] = source.Uri })
                    .ToArray())
            });
        }

        private static IEnumerable<string> EnumerateTextFiles(string root)
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var directory = queue.Dequeue();
                IEnumerable<string> files;
                IEnumerable<string> directories;
                try
                {
                    files = Directory.EnumerateFiles(directory).ToArray();
                    directories = Directory.EnumerateDirectories(directory).ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

                foreach (var file in files)
                    if (TextExtensions.Contains(Path.GetExtension(file)) || Path.GetFileName(file).StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                        yield return file;
                foreach (var child in directories)
                {
                    var info = new DirectoryInfo(child);
                    if (!IgnoredDirectories.Contains(info.Name) && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        queue.Enqueue(child);
                }
            }
        }

        private static int CountOccurrences(string value, string term)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += term.Length;
            }
            return count;
        }

        private static string CreateRelevantExcerpt(string content, IReadOnlyList<string> terms, int limit)
        {
            if (limit <= 0) return string.Empty;
            var firstMatch = terms
                .Select(term => content.IndexOf(term, StringComparison.OrdinalIgnoreCase))
                .Where(index => index >= 0)
                .DefaultIfEmpty(0)
                .Min();
            var start = Math.Max(0, firstMatch - 1_000);
            var length = Math.Min(limit, content.Length - start);
            return content.Substring(start, length);
        }

        private static string RequiredString(JsonObject value, string name) =>
            value[name]?.GetValue<string>() is { Length: > 0 } result ? result : throw new ArgumentException($"{name} is required.");

        private static ToolResult Ok(JsonObject value) => new(true, value.ToJsonString(new() { WriteIndented = true }));
        private static ToolResult Error(string category, string summary) => new(false, new JsonObject
        {
            ["status"] = "failed",
            ["error_category"] = category,
            ["summary"] = summary
        }.ToJsonString(new() { WriteIndented = true }));

        private static JsonObject String() => new() { ["type"] = "string" };
        private static JsonObject Integer() => new() { ["type"] = "integer" };
        private static JsonObject Boolean() => new() { ["type"] = "boolean" };
        private static JsonObject Props(params (string Name, JsonObject Schema)[] values) =>
            new(values.Select(value => KeyValuePair.Create<string, JsonNode?>(value.Name, value.Schema)));
        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray())
            }
        };
    }
}
