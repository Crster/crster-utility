using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        private const int MaximumPatchSnippetLines = 24;
        private const int MaximumPatchSnippetLineLength = 200;
        private const int MaximumPatchSnippetCharacters = 3_000;
        private const int MaximumPatchAmbiguousMatches = 8;
        private const int MaximumPatchContextLineLength = 120;
        private const int MaximumPatchSuccessSnippetEdits = 5;
        private const int MaximumPatchSuccessSnippetLines = 12;
        private const int MaximumPatchSyntaxErrors = 10;
        private const int MaximumFuzzyScoredWindows = 8;
        private const int MaximumFuzzyCompareCharacters = 4_000;
        private const int MaximumFuzzyEditDistance = 400;
        private const double MinimumFuzzyPatchSimilarity = 0.75;
        private const double MinimumFuzzyLineOverlap = 0.5;
        private const double MinimumPatchCandidateSimilarity = 0.60;
        private const string ExactMatchMode = "exact";
        private const string LineEndingMatchMode = "line_endings";
        private const string BlankLineMatchMode = "blank_lines";
        private const string FuzzyMatchMode = "fuzzy";
        private readonly GeminiClient _client;
        private readonly SecretaryToolService _secretaryTools;
        private readonly Func<string, Task<bool>> _confirmAsync;
        private readonly Func<Task<ToolResult>> _compactAsync;
        private readonly Func<Task<ToolResult>> _cleanupAsync;
        private readonly Dictionary<string, string> _completeReadHashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pathsWithFailedPatches = new(StringComparer.OrdinalIgnoreCase);
        public TechnicianToolService(GeminiClient client, SecretaryToolService secretaryTools,
            Func<string, Task<bool>> confirmAsync, Func<Task<ToolResult>> compactAsync, Func<Task<ToolResult>> cleanupAsync)
        {
            _client = client;
            _secretaryTools = secretaryTools;
            _confirmAsync = confirmAsync;
            _compactAsync = compactAsync;
            _cleanupAsync = cleanupAsync;
        }

        public string WorkspacePath { get; set; } = string.Empty;

        public static JsonArray CreateDeclarations() =>
        [
            Function("read_file", "Read a text file inside the selected workspace. Optionally provide zero-based, end-exclusive start and end offsets to read a character range. If the same complete file is read again without changing, the tool omits duplicate content and directs you to reuse the prior result.", Props(("path", String()), ("start", Integer()), ("end", Integer())), "path"),
            Function("write_file", "Write text to a file inside the workspace. Creates parent folders when needed. Optionally provide zero-based, end-exclusive start and end offsets to replace a character range in an existing file; provide neither offset to replace the whole file. Never use a whole-file write to bypass a failed patch on an existing file.", Props(("path", String()), ("content", String()), ("start", Integer()), ("end", Integer())), "path", "content"),
            Function("patch_file", "Atomically apply one or more text patches to a file inside the workspace. Use old_text/new_text for a single edit, or edits: [{ old_text, new_text, replace_all }] to apply several edits as one all-or-nothing call. Copy old_text verbatim from a prior read_file result, including its exact indentation and blank lines; never elide text with three dots, never add line numbers, and never guess unread content. Extend old_text with surrounding lines until it appears exactly once in the file, or set replace_all to change every occurrence. Matching falls back from exact text to whitespace-aware and near-exact line matching, and the result reports which mode matched and whether new_text was re-indented. A failed patch writes nothing, so follow its next_step and reuse its candidate snippet. When file_state is unchanged_since_complete_read, do not call read_file again. syntax_check parses the patched result as C# and rejects the whole patch when it has syntax errors.", Props(("path", String()), ("old_text", String()), ("new_text", String()), ("replace_all", Boolean()), ("edits", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["properties"] = Props(("old_text", String()), ("new_text", String()), ("replace_all", Boolean())), ["required"] = new JsonArray("old_text", "new_text") } }), ("syntax_check", Boolean())), "path"),
            Function("delete_file", "Delete a file or empty directory inside the workspace after user confirmation.", Props(("path", String())), "path"),
            Function("search_file", "Recursively search workspace files in a directory by regular expression, like grep. Returns matching file paths and contextual line snippets up to 125 characters; start and end are zero-based, end-exclusive offsets of the match in each snippet.", Props(("directory", String()), ("regex_pattern", String())), "directory", "regex_pattern"),
            Function("list_file_and_directory", "List workspace files and directories. Optionally filter by regex and depth.", Props(("path", String()), ("depth", Integer()), ("regex", String())), "path"),
            Function("execute", "Run a non-risky command in the selected workspace and return stdout, stderr, and exit code.", Props(("command", String()), ("arguments", String())), "command"),
            Function("execute_sudo", "Run a command elevated through a Windows UAC prompt after confirmation.", Props(("command", String()), ("arguments", String())), "command"),
            Function("list_process", "List running processes.", new JsonObject()),
            Function("kill_process", "Terminate a process by process ID after confirmation.", Props(("process_id", Integer())), "process_id"),
            Function("compact", "Build rich continuation context from the Technician chat, workspace, and memos, then clear chat and memos.", new JsonObject()),
            Function("clean_up", "Clear Technician chat and Context-panel text while retaining Technician memory.", new JsonObject()),
            Function("research", "Create private knowledge-based research context from the supplied request and context only. It cannot search the web or inspect files.", Props(("topic", String())), "topic"),
            Function("plan", "Create private implementation-planning context from the supplied request and context only. It cannot inspect or edit workspace files.", Props(("request", String())), "request"),
            Function("design", "Create a private UI/UX design brief from the supplied request and context only. It cannot inspect or edit workspace files.", Props(("request", String())), "request"),
            Function("get_data", "Return only one of these local data values: local date/time, configured location, weather, clipboard text, language, or battery percentage. It cannot obtain any other data.", Props(("kind", SecretaryToolService.DataKindSchema())), "kind")
        ];

        public static JsonArray CreateExecutionDeclarations()
        {
            var declarations = CreateDeclarations();
            return new JsonArray(declarations
                .OfType<JsonObject>()
                .Where(declaration => declaration["name"]?.GetValue<string>() is not ("plan" or "design" or "research"))
                .Select(declaration => (JsonNode)declaration.DeepClone())
                .ToArray());
        }

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
                if (initialContent.Length == content.Length)
                {
                    var contentHash = ComputeContentHash(content);
                    if (_completeReadHashes.TryGetValue(fullPath, out var previousHash)
                        && string.Equals(previousHash, contentHash, StringComparison.Ordinal))
                        return ReusedCompleteRead(fullPath, content.Length);
                    _completeReadHashes[fullPath] = contentHash;
                }
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
            if (range.Value.Start == 0 && range.Value.End == content.Length && returnedContent.Length == content.Length)
            {
                var contentHash = ComputeContentHash(content);
                if (_completeReadHashes.TryGetValue(fullPath, out var previousHash)
                    && string.Equals(previousHash, contentHash, StringComparison.Ordinal))
                    return ReusedCompleteRead(fullPath, content.Length);
                _completeReadHashes[fullPath] = contentHash;
            }
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
            if (range is null && File.Exists(fullPath) && _pathsWithFailedPatches.Contains(fullPath))
                return Error(
                    "whole_file_patch_fallback_blocked",
                    "The existing file was not changed. A whole-file overwrite is blocked after a failed patch.",
                    new JsonObject
                    {
                        ["next_step"] = "Use patch_file with literal old_text from the previous complete read or the failure's candidate_snippet. Do not call read_file or write_file again."
                    });
            if (range is not null)
            {
                if (!File.Exists(fullPath)) return Error("file_not_found", "A character-range write requires an existing file.");
                var existingContent = File.ReadAllText(fullPath);
                ValidateRange(range.Value, existingContent.Length);
                content = existingContent[..range.Value.Start] + content + existingContent[range.Value.End..];
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            _completeReadHashes.Remove(fullPath);
            _pathsWithFailedPatches.Remove(fullPath);
            return Ok("Wrote the file.", new JsonObject { ["path"] = fullPath, ["bytes"] = new FileInfo(fullPath).Length });
        }

        private ToolResult PatchFile(string path, JsonObject arguments)
        {
            var fullPath = ResolveWorkspacePath(path);
            if (!File.Exists(fullPath)) return Error("file_not_found", "The file does not exist.");
            var content = File.ReadAllText(fullPath);
            var unchangedSinceCompleteRead = _completeReadHashes.TryGetValue(fullPath, out var completeReadHash)
                && string.Equals(completeReadHash, ComputeContentHash(content), StringComparison.Ordinal);
            var edits = ParsePatchEdits(arguments);
            var source = CreatePatchIndex(content);
            var resolved = new List<ResolvedPatch>();

            // Resolve every edit against the untouched file so a partial patch can never be persisted.
            for (var editIndex = 0; editIndex < edits.Count; editIndex++)
            {
                var edit = edits[editIndex];
                var matches = FindPatchMatches(source, edit, editIndex);
                if (matches.Count == 0)
                {
                    _pathsWithFailedPatches.Add(fullPath);
                    return PatchNotFound(source, edit, editIndex, edits.Count, unchangedSinceCompleteRead);
                }
                if (matches.Count > 1 && !(edit.ReplaceAll && SupportsReplaceAll(matches[0].MatchMode)))
                {
                    _pathsWithFailedPatches.Add(fullPath);
                    return PatchAmbiguous(source, matches, editIndex);
                }
                resolved.AddRange(matches);
            }

            var ordered = resolved.OrderBy(match => match.Start).ThenBy(match => match.Length).ToArray();
            for (var index = 1; index < ordered.Length; index++)
                if (ordered[index].Start < ordered[index - 1].Start + ordered[index - 1].Length)
                {
                    _pathsWithFailedPatches.Add(fullPath);
                    return PatchOverlap(source.Lines, ordered[index - 1], ordered[index]);
                }

            var patched = content;
            foreach (var patch in resolved.OrderByDescending(match => match.Start))
                patched = patched[..patch.Start] + patch.NewText + patched[(patch.Start + patch.Length)..];

            // An approximate match may have landed on the wrong block, so gate C# on Roslyn even when the caller did not ask.
            var approximate = resolved.Any(patch => patch.MatchMode is FuzzyMatchMode or BlankLineMatchMode);
            if ((OptionalBoolean(arguments, "syntax_check") || approximate) && Path.GetExtension(fullPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var errors = CSharpSyntaxTree.ParseText(patched).GetDiagnostics()
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Take(MaximumPatchSyntaxErrors)
                    .ToArray();
                if (errors.Length > 0)
                {
                    _pathsWithFailedPatches.Add(fullPath);
                    return PatchSyntaxError(errors);
                }
            }

            File.WriteAllText(fullPath, patched, new UTF8Encoding(false));
            _completeReadHashes.Remove(fullPath);
            _pathsWithFailedPatches.Remove(fullPath);
            return PatchApplied(fullPath, patched, resolved);
        }

        private static ToolResult ReusedCompleteRead(string fullPath, int totalCharacters) =>
            Ok("The file is unchanged since the previous complete read. Reuse that result; duplicate content was omitted.", new JsonObject
            {
                ["path"] = fullPath,
                ["reused_previous_read"] = true,
                ["content_omitted"] = true,
                ["total_characters"] = totalCharacters
            });

        private static string ComputeContentHash(string content) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        private static bool SupportsReplaceAll(string matchMode) => matchMode is not (BlankLineMatchMode or FuzzyMatchMode);

        private static ToolResult PatchApplied(string fullPath, string patched, List<ResolvedPatch> resolved)
        {
            var lines = SplitLines(patched);
            var includeSnippets = resolved.Count <= MaximumPatchSuccessSnippetEdits;
            var reindented = resolved.Where(patch => patch.Reindented).ToArray();
            var approximate = resolved.Where(patch => patch.MatchMode is FuzzyMatchMode or BlankLineMatchMode).ToArray();
            var entries = new JsonArray(resolved.OrderBy(patch => patch.Start).Select(patch =>
            {
                var start = TranslatePatchStart(patch.Start, resolved);
                var end = start + patch.NewText.Length;
                var firstLine = LineIndexAt(lines, start);
                var lastLine = LineIndexAt(lines, Math.Max(start, end - 1));
                var entry = new JsonObject
                {
                    ["edit_index"] = patch.EditIndex,
                    ["line_start"] = firstLine + 1,
                    ["line_end"] = lastLine + 1,
                    ["start"] = start,
                    ["end"] = end,
                    ["match_mode"] = patch.MatchMode,
                    ["reindented"] = patch.Reindented
                };
                if (includeSnippets)
                    entry["snippet"] = CreateNumberedSnippet(lines, firstLine, lastLine, firstLine, MaximumPatchSuccessSnippetLines);
                return (JsonNode)entry;
            }).ToArray());

            var summary = new StringBuilder($"Applied {resolved.Count} edit(s).");
            if (approximate.Length > 0)
                summary.Append($" {approximate.Length} edit(s) matched only approximately; check the reported lines.");
            if (reindented.Length > 0)
                summary.Append($" {reindented.Length} edit(s) had new_text re-indented to the file's indentation.");
            return Ok(summary.ToString(), new JsonObject
            {
                ["path"] = fullPath,
                ["characters"] = patched.Length,
                ["edits"] = entries,
                ["edits_truncated"] = !includeSnippets
            });
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

        private static readonly Regex ElisionMarkerPattern = new(@"^(?:(?://|#|<!--|/\*|--)\s*)?(?:\.\.\.|…)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex LineNumberPrefixPattern = new(@"^\s*(\d{1,7})\s*[|:\t]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        // Matching tolerance grows level by level and the first level that matches wins, so precision always beats tolerance.
        private static readonly (string Mode, Func<string, string> Key, bool Reindent)[] LineLevels =
        [
            ("trailing_space", static line => line.TrimEnd(), false),
            ("indentation", static line => line.Trim(), true),
            ("inner_space", CollapseWhitespace, true),
            ("no_space", StripWhitespace, true)
        ];

        private static List<ResolvedPatch> FindPatchMatches(PatchIndex source, PatchEdit edit, int editIndex)
        {
            var exact = FindOccurrences(source.Text, edit.OldText)
                .Select(start => MaterializeSpan(source, start, edit.OldText.Length, edit.NewText, editIndex, ExactMatchMode))
                .ToList();
            if (exact.Count > 0) return exact;

            if (source.Text.Contains('\r') || edit.OldText.Contains('\r') || (source.Text.Length > 0 && source.Text[0] == '\uFEFF'))
            {
                var normalized = FindNormalizedOccurrences(source.Text, edit.OldText)
                    .Select(match => MaterializeSpan(source, match.Start, match.Length, edit.NewText, editIndex, LineEndingMatchMode))
                    .ToList();
                if (normalized.Count > 0) return normalized;
            }

            var expected = SplitLines(edit.OldText);

            // A whitespace-only old_text would match every blank line once edges are trimmed, so stop before the line levels.
            if (expected.All(line => line.Text.Trim().Length == 0)) return [];

            foreach (var level in LineLevels)
            {
                var keyed = ResolveCandidates(source, edit, editIndex, FindKeyedLineMatches(source, expected, level.Key), level.Mode, level.Reindent);
                if (keyed.Count > 0) return keyed;
            }

            foreach (var key in new Func<string, string>[] { CollapseWhitespace, StripWhitespace })
            {
                var blankInsensitive = ResolveCandidates(source, edit, editIndex,
                    DedupeOverlapping(FindBlankInsensitiveMatches(source, expected, key)), BlankLineMatchMode, true);
                if (blankInsensitive.Count > 0) return blankInsensitive;
            }

            var fuzzy = FindFuzzyMatch(source, expected);
            return fuzzy is null ? [] : ResolveCandidates(source, edit, editIndex, [fuzzy], FuzzyMatchMode, true);
        }

        private static PatchIndex CreatePatchIndex(string content)
        {
            var lines = SplitLines(content);
            var keys = new string[lines.Length];
            var hashes = new ulong[lines.Length];
            var postings = new Dictionary<ulong, List<int>>();
            for (var index = 0; index < lines.Length; index++)
            {
                keys[index] = StripWhitespace(lines[index].Text);
                hashes[index] = keys[index].Length == 0 ? 0 : HashKey(keys[index]);
                if (hashes[index] == 0) continue;
                if (!postings.TryGetValue(hashes[index], out var lineNumbers)) postings[hashes[index]] = lineNumbers = [];
                lineNumbers.Add(index);
            }
            return new PatchIndex(content, lines, keys, hashes, postings);
        }

        private static string StripWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value) if (!char.IsWhiteSpace(character)) builder.Append(character);
            return builder.ToString();
        }

        private static string CollapseWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character)) { pendingSpace = builder.Length > 0; continue; }
                if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
                builder.Append(character);
            }
            return builder.ToString();
        }

        // Zero is reserved to mark a blank line, so a real key never hashes to it.
        private static ulong HashKey(string value)
        {
            var hash = 14695981039346656037UL;
            foreach (var character in value) { hash ^= character; hash *= 1099511628211UL; }
            return hash == 0 ? 1 : hash;
        }

        private static List<ResolvedPatch> ResolveCandidates(PatchIndex source, PatchEdit edit, int editIndex,
            IEnumerable<PatchCandidate> candidates, string mode, bool reindent)
        {
            var resolved = new List<ResolvedPatch>();
            foreach (var candidate in candidates)
            {
                var patch = CreateResolvedPatch(source, edit, editIndex, candidate, mode, reindent);
                if (patch is not null) resolved.Add(patch);
            }
            return resolved;
        }

        private static ResolvedPatch? CreateResolvedPatch(PatchIndex source, PatchEdit edit, int editIndex,
            PatchCandidate candidate, string mode, bool reindent)
        {
            var first = source.Lines[candidate.FirstLine];
            var last = source.Lines[candidate.LastLine];
            var terminator = Terminator(source.Text, last);

            // old_text without a trailing newline must not consume the matched block's terminator, or the
            // following line is pulled onto the replacement's last line.
            var includeTerminator = EndsWithNewLine(edit.OldText) && terminator.Length > 0;
            var start = first.Start;
            var length = (includeTerminator ? last.End : last.TextEnd) - start;
            if (length <= 0) return null;

            var text = edit.NewText;
            if (mode == BlankLineMatchMode) text = TrimBlankEdges(text);
            if (reindent) text = Reindent(text, source.Text[start..(start + length)]);
            text = NormalizeTerminators(text, terminator.Length > 0 ? terminator : DominantTerminator(source.Text));
            if (includeTerminator && !EndsWithNewLine(text)) text += terminator;
            else if (!includeTerminator && EndsWithNewLine(text)) text = StripTrailingTerminator(text);
            return new ResolvedPatch(start, length, text, editIndex, mode, reindent);
        }

        // Levels that match on raw characters already own their range, so they only need the file's line-ending
        // style applied. The normalized level also needs trailing-newline parity, because normalization — not the
        // caller — decided whether the matched range includes the block's terminator.
        private static ResolvedPatch MaterializeSpan(PatchIndex source, int start, int length, string newText, int editIndex, string mode)
        {
            var text = NormalizeTerminators(newText, DominantTerminator(source.Text));
            if (mode != ExactMatchMode)
            {
                var includeTerminator = EndsWithNewLine(source.Text[start..(start + length)]);
                if (includeTerminator && !EndsWithNewLine(text)) text += DominantTerminator(source.Text);
                else if (!includeTerminator && EndsWithNewLine(text)) text = StripTrailingTerminator(text);
            }
            return new ResolvedPatch(start, length, text, editIndex, mode, false);
        }

        private static string DominantTerminator(string content) => content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        private static string Terminator(string text, PatchLine line) => text[line.TextEnd..line.End];
        private static bool EndsWithNewLine(string value) => value.Length > 0 && value[^1] == '\n';
        private static string StripTrailingTerminator(string text) =>
            text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;

        private static string NormalizeTerminators(string text, string terminator)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var line in SplitLines(text))
            {
                builder.Append(line.Text);
                if (line.End > line.TextEnd) builder.Append(terminator);
            }
            return builder.ToString();
        }

        private static string TrimBlankEdges(string text)
        {
            var lines = SplitLines(text);
            var first = 0;
            var last = lines.Length - 1;
            while (first <= last && lines[first].Text.Trim().Length == 0) first++;
            while (last >= first && lines[last].Text.Trim().Length == 0) last--;
            return first > last ? text : text[lines[first].Start..lines[last].End];
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

                // One past the last matched character keeps a CR interior to the match and excludes the
                // CR that belongs to the following line; the next character's position would swallow it.
                var end = positions[index + normalizedOld.Length - 1] + 1;
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

        // A window matching under any level key is necessarily equal under the stripped key, so anchoring on
        // the first non-blank stripped line is a sound prefilter: it can only produce false positives.
        private static IEnumerable<int> EnumerateWindowStarts(PatchIndex source, PatchLine[] expected)
        {
            var limit = source.Lines.Length - expected.Length;
            if (limit < 0) return [];
            var anchorOffset = -1;
            for (var index = 0; index < expected.Length; index++)
                if (StripWhitespace(expected[index].Text).Length > 0) { anchorOffset = index; break; }
            if (anchorOffset < 0) return [];
            if (!source.Postings.TryGetValue(HashKey(StripWhitespace(expected[anchorOffset].Text)), out var postings)) return [];
            var offset = anchorOffset;
            return postings.Select(line => line - offset).Where(start => start >= 0 && start <= limit);
        }

        private static List<PatchCandidate> FindKeyedLineMatches(PatchIndex source, PatchLine[] expected, Func<string, string> key)
        {
            var expectedKeys = expected.Select(line => key(line.Text)).ToArray();
            var matches = new List<PatchCandidate>();
            foreach (var start in EnumerateWindowStarts(source, expected))
            {
                var matched = true;
                for (var offset = 0; offset < expected.Length; offset++)
                    if (!string.Equals(key(source.Lines[start + offset].Text), expectedKeys[offset], StringComparison.Ordinal)) { matched = false; break; }
                if (matched) matches.Add(new PatchCandidate(start, start + expected.Length - 1, 1));
            }
            return matches;
        }

        // Compares only non-blank lines so added or removed blank lines no longer break an otherwise exact block.
        private static List<PatchCandidate> FindBlankInsensitiveMatches(PatchIndex source, PatchLine[] expected, Func<string, string> key)
        {
            var expectedKeys = expected.Where(line => line.Text.Trim().Length > 0).Select(line => key(line.Text)).ToArray();
            if (expectedKeys.Length == 0) return [];
            if (expectedKeys.Length == 1 && StripWhitespace(expectedKeys[0]).Length < 8) return [];
            if (!source.Postings.TryGetValue(HashKey(StripWhitespace(expectedKeys[0])), out var postings)) return [];

            var maximumSpan = expected.Length * 2 + 8;
            var matches = new List<PatchCandidate>();
            foreach (var start in postings)
            {
                var line = start;
                var matchedCount = 0;
                var lastMatched = -1;
                while (line < source.Lines.Length && matchedCount < expectedKeys.Length)
                {
                    if (source.Keys[line].Length == 0) { line++; continue; }
                    if (!string.Equals(key(source.Lines[line].Text), expectedKeys[matchedCount], StringComparison.Ordinal)) break;
                    lastMatched = line;
                    matchedCount++;
                    line++;
                }
                if (matchedCount == expectedKeys.Length && lastMatched - start + 1 <= maximumSpan)
                    matches.Add(new PatchCandidate(start, lastMatched, 1));
            }
            return matches;
        }

        // Adjacent windows over a repetitive block describe the same region, so keep only the first of each cluster.
        private static List<PatchCandidate> DedupeOverlapping(List<PatchCandidate> candidates)
        {
            var kept = new List<PatchCandidate>();
            foreach (var candidate in candidates.OrderBy(match => match.FirstLine))
                if (kept.Count == 0 || candidate.FirstLine > kept[^1].LastLine) kept.Add(candidate);
            return kept;
        }

        private static PatchCandidate? FindFuzzyMatch(PatchIndex source, PatchLine[] expected)
        {
            var expectedKeys = expected.Where(line => line.Text.Trim().Length > 0).Select(line => CollapseWhitespace(line.Text)).ToArray();
            if (expectedKeys.Length == 0) return null;
            if (expectedKeys.Length == 1 && StripWhitespace(expectedKeys[0]).Length < 8) return null;

            var expectedHashes = expectedKeys.Select(key => HashKey(StripWhitespace(key))).ToArray();
            var required = Math.Max(1, (int)Math.Ceiling(expectedKeys.Length * MinimumFuzzyLineOverlap));
            var drift = Math.Clamp(expected.Length / 4, 1, 10);
            var maximumSpan = expected.Length * 2 + 4;
            var windows = new List<(int FirstLine, int LastLine, int Overlap)>();
            for (var length = Math.Max(1, expected.Length - drift); length <= expected.Length + drift; length++)
                windows.AddRange(FindOverlappingWindows(source, expectedHashes, length, required));
            if (windows.Count == 0) return null;

            var expectedText = ClampCompare(string.Join("\n", expectedKeys));
            PatchCandidate? best = null;
            foreach (var window in windows.OrderByDescending(window => window.Overlap).Take(MaximumFuzzyScoredWindows))
            {
                var bounds = TrimToNonBlank(source, window.FirstLine, window.LastLine);
                if (bounds is null || bounds.Value.LastLine - bounds.Value.FirstLine + 1 > maximumSpan) continue;
                var similarity = ScoreWindow(source, expectedHashes, expectedText, bounds.Value.FirstLine, bounds.Value.LastLine, MinimumFuzzyPatchSimilarity);
                if (similarity < MinimumFuzzyPatchSimilarity) continue;
                if (best is null || similarity > best.Similarity)
                    best = new PatchCandidate(bounds.Value.FirstLine, bounds.Value.LastLine, similarity);
            }
            return best;
        }

        // Scores at line granularity first: in code a whole missing or altered line is one unit of difference,
        // whereas character distance exaggerates it badly on short blocks. Character distance is only consulted
        // when no line matched exactly, which is the case a line score cannot describe.
        private static double ScoreWindow(PatchIndex source, ulong[] expectedHashes, string expectedText, int firstLine, int lastLine, double minimum)
        {
            var similarity = LineSimilarity(expectedHashes, NonBlankHashes(source, firstLine, lastLine));
            if (similarity >= minimum) return similarity;
            var candidateText = ClampCompare(JoinNonBlankKeys(source, firstLine, lastLine));
            return Math.Max(similarity, SimilarityWithin(expectedText, candidateText, minimum));
        }

        private static double LineSimilarity(ulong[] left, ulong[] right)
        {
            var longest = Math.Max(left.Length, right.Length);
            return longest == 0 ? 1 : 1 - (double)LineDistance(left, right) / longest;
        }

        private static ulong[] NonBlankHashes(PatchIndex source, int firstLine, int lastLine)
        {
            var hashes = new List<ulong>(lastLine - firstLine + 1);
            for (var index = firstLine; index <= lastLine; index++)
                if (source.Hashes[index] != 0) hashes.Add(source.Hashes[index]);
            return [.. hashes];
        }

        private static int LineDistance(ulong[] left, ulong[] right)
        {
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var column = 0; column <= right.Length; column++) previous[column] = column;
            for (var row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (var column = 1; column <= right.Length; column++)
                {
                    var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + cost);
                }
                (previous, current) = (current, previous);
            }
            return previous[right.Length];
        }

        // Slides a fixed-length window while maintaining the multiset overlap with old_text's line hashes,
        // so unrelated windows are discarded in O(1) per line instead of by an edit-distance computation.
        private static List<(int FirstLine, int LastLine, int Overlap)> FindOverlappingWindows(PatchIndex source, ulong[] expectedHashes, int windowLength, int required)
        {
            if (windowLength <= 0 || windowLength > source.Lines.Length) return [];
            var demand = new Dictionary<ulong, int>();
            foreach (var hash in expectedHashes) demand[hash] = demand.GetValueOrDefault(hash) + 1;
            var have = new Dictionary<ulong, int>();
            var overlap = 0;
            var windows = new List<(int, int, int)>();
            for (var index = 0; index < source.Lines.Length; index++)
            {
                var entering = source.Hashes[index];
                if (entering != 0 && demand.TryGetValue(entering, out var wantedEntering))
                {
                    var count = have.GetValueOrDefault(entering);
                    if (count < wantedEntering) overlap++;
                    have[entering] = count + 1;
                }
                if (index >= windowLength)
                {
                    var leaving = source.Hashes[index - windowLength];
                    if (leaving != 0 && demand.TryGetValue(leaving, out var wantedLeaving))
                    {
                        var count = have[leaving];
                        if (count <= wantedLeaving) overlap--;
                        have[leaving] = count - 1;
                    }
                }
                if (index >= windowLength - 1 && overlap >= required) windows.Add((index - windowLength + 1, index, overlap));
            }
            return windows;
        }

        private static (int FirstLine, int LastLine)? TrimToNonBlank(PatchIndex source, int firstLine, int lastLine)
        {
            while (firstLine <= lastLine && source.Keys[firstLine].Length == 0) firstLine++;
            while (lastLine >= firstLine && source.Keys[lastLine].Length == 0) lastLine--;
            return firstLine > lastLine ? null : (firstLine, lastLine);
        }

        private static string JoinNonBlankKeys(PatchIndex source, int firstLine, int lastLine)
        {
            var builder = new StringBuilder();
            for (var index = firstLine; index <= lastLine; index++)
            {
                if (source.Keys[index].Length == 0) continue;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(CollapseWhitespace(source.Lines[index].Text));
            }
            return builder.ToString();
        }

        private static string ClampCompare(string value) =>
            value.Length <= MaximumFuzzyCompareCharacters ? value : value[..MaximumFuzzyCompareCharacters];

        private static ToolResult PatchNotFound(
            PatchIndex source,
            PatchEdit edit,
            int editIndex,
            int editCount,
            bool unchangedSinceCompleteRead)
        {
            var expected = SplitLines(edit.OldText);
            var candidate = FindClosestCandidate(source, expected);
            var diagnosis = DiagnosePatchFailure(source, expected, candidate);
            var first = source.Lines[candidate.FirstLine];
            var last = source.Lines[candidate.LastLine];
            var focusLine = diagnosis.FileLine > 0 ? diagnosis.FileLine - 1 : candidate.FirstLine;

            var details = new JsonObject
            {
                ["next_step"] = CreatePatchNextStep(diagnosis, candidate, expected.Length, source.Lines.Length, first.Start, last.End, unchangedSinceCompleteRead),
                ["probable_cause"] = diagnosis.Cause,
                ["edit_index"] = editIndex,
                ["file_state"] = unchangedSinceCompleteRead
                    ? "unchanged_since_complete_read"
                    : "complete_read_unavailable_or_stale"
            };
            if (diagnosis.Cause == "content_differs") details["first_mismatch"] = CreateMismatchDetail(diagnosis, expected.Length);
            details["candidate"] = new JsonObject
            {
                ["start_line"] = candidate.FirstLine + 1,
                ["end_line"] = candidate.LastLine + 1,
                ["start"] = first.Start,
                ["end"] = last.End,
                ["similarity"] = Math.Round(candidate.Similarity, 2)
            };
            details["candidate_snippet"] = CreateNumberedSnippet(source.Lines, candidate.FirstLine, candidate.LastLine, focusLine, MaximumPatchSnippetLines);

            var editLabel = editCount > 1 ? $" Edit {editIndex + 1} of {editCount}." : string.Empty;
            var summary = $"old_text did not match; the file was not changed.{editLabel} Closest candidate is lines {candidate.FirstLine + 1}-{candidate.LastLine + 1} ({candidate.Similarity:P0} similar).";
            return Error("patch_not_found", summary, details);
        }

        private static JsonObject CreateMismatchDetail(PatchDiagnosis diagnosis, int expectedLineCount)
        {
            var detail = new JsonObject
            {
                ["old_text_line"] = diagnosis.OldTextLine,
                ["file_line"] = diagnosis.FileLine,
                ["first_differing_column"] = diagnosis.Column,
                ["expected"] = CapAroundColumn(diagnosis.Expected, diagnosis.Column, MaximumPatchSnippetLineLength),
                ["actual"] = CapAroundColumn(diagnosis.Actual, diagnosis.Column, MaximumPatchSnippetLineLength),
                ["matching_prefix_lines"] = diagnosis.MatchingPrefixLines,
                ["matching_suffix_lines"] = diagnosis.MatchingSuffixLines,
                ["lines_inserted_or_removed"] = diagnosis.MatchingPrefixLines + diagnosis.MatchingSuffixLines + 1 < expectedLineCount
            };

            // Only worth describing when the two lines are otherwise identical, because then the difference is invisible.
            if (string.Equals(diagnosis.Expected.Trim(), diagnosis.Actual.Trim(), StringComparison.Ordinal))
                detail["whitespace"] = new JsonObject
                {
                    ["expected_indent"] = DescribeIndent(diagnosis.Expected),
                    ["actual_indent"] = DescribeIndent(diagnosis.Actual),
                    ["expected_trailing"] = DescribeTrailing(diagnosis.Expected),
                    ["actual_trailing"] = DescribeTrailing(diagnosis.Actual)
                };
            return detail;
        }

        private static string CreatePatchNextStep(
            PatchDiagnosis diagnosis,
            PatchCandidate candidate,
            int expectedLineCount,
            int fileLineCount,
            int start,
            int end,
            bool unchangedSinceCompleteRead)
        {
            var fileReadGuidance = unchangedSinceCompleteRead
                ? "The file is unchanged since the last complete read; do not call read_file again. Reuse that result"
                : "The previous complete read is unavailable or stale; call read_file only if the candidate snippet is not the intended code";
            var rebuild = $"Rebuild old_text from candidate_snippet below, using the text after the first '|' on each line and dropping the numbers, then call patch_file again. {fileReadGuidance}. As a last resort, call write_file with start {start} and end {end} to replace exactly file lines {candidate.FirstLine + 1}-{candidate.LastLine + 1}; that range is end-exclusive and includes the final newline.";
            return diagnosis.Cause switch
            {
                "old_text_longer_than_file" =>
                    $"Nothing was written. old_text has {expectedLineCount} lines but the file has only {fileLineCount}. {fileReadGuidance} and patch a smaller literal block, or call write_file with no start or end to replace the whole file.",
                "elision_marker" =>
                    $"Nothing was written. Line {diagnosis.OldTextLine} of old_text is an elision marker, and patch_file cannot expand elided text. Send the full literal text for the block, or split the change into two entries in the edits array so each old_text is contiguous real text, then call again.",
                "line_number_prefix" =>
                    $"Nothing was written. The lines of old_text carry '{diagnosis.Sample}' style line-number prefixes copied from a tool result. Remove the numbers and the separator so old_text is exactly the file's own text, then call again.",
                "no_similar_text" =>
                    $"Nothing was written. No block in this file resembles old_text; the closest is only {candidate.Similarity:P0} similar. Confirm the path. {fileReadGuidance} and build old_text from the available file content. Do not guess the text.",
                _ when diagnosis.MatchingPrefixLines + diagnosis.MatchingSuffixLines + 1 < expectedLineCount =>
                    $"Nothing was written. old_text matches file lines {candidate.FirstLine + 1}-{candidate.LastLine + 1} for its first {diagnosis.MatchingPrefixLines} lines and its last {diagnosis.MatchingSuffixLines} lines, so lines were added or removed in between. {rebuild}",
                _ =>
                    $"Nothing was written. Line {diagnosis.OldTextLine} of old_text does not match file line {diagnosis.FileLine}. {rebuild}"
            };
        }

        private static PatchDiagnosis DiagnosePatchFailure(PatchIndex source, PatchLine[] expected, PatchCandidate candidate)
        {
            if (expected.Length > source.Lines.Length)
                return new PatchDiagnosis("old_text_longer_than_file", 0, 0, 0, string.Empty, string.Empty, 0, 0, string.Empty);
            if (TryFindElisionMarker(expected, out var elisionIndex))
                return new PatchDiagnosis("elision_marker", elisionIndex + 1, 0, 0, expected[elisionIndex].Text.Trim(), string.Empty, 0, 0, string.Empty);
            if (TryFindLineNumberPrefix(expected, out var sample))
                return new PatchDiagnosis("line_number_prefix", 0, 0, 0, string.Empty, string.Empty, 0, 0, sample);
            if (candidate.Similarity < MinimumPatchCandidateSimilarity)
                return new PatchDiagnosis("no_similar_text", 0, 0, 0, string.Empty, string.Empty, 0, 0, string.Empty);

            var comparable = Math.Min(expected.Length, candidate.LastLine - candidate.FirstLine + 1);
            var prefix = 0;
            while (prefix < comparable && SameTrimmed(expected[prefix].Text, source.Lines[candidate.FirstLine + prefix].Text)) prefix++;
            var suffix = 0;
            while (suffix < comparable - prefix && SameTrimmed(expected[^(suffix + 1)].Text, source.Lines[candidate.LastLine - suffix].Text)) suffix++;

            var mismatchIndex = Math.Min(prefix, expected.Length - 1);
            var fileIndex = Math.Min(candidate.FirstLine + prefix, candidate.LastLine);
            var expectedText = expected[mismatchIndex].Text;
            var actualText = source.Lines[fileIndex].Text;
            return new PatchDiagnosis("content_differs", mismatchIndex + 1, fileIndex + 1,
                FirstDifferingColumn(expectedText, actualText), expectedText, actualText, prefix, suffix, string.Empty);
        }

        private static bool SameTrimmed(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

        private static int FirstDifferingColumn(string left, string right)
        {
            var limit = Math.Min(left.Length, right.Length);
            for (var index = 0; index < limit; index++) if (left[index] != right[index]) return index + 1;
            return limit + 1;
        }

        private static bool TryFindElisionMarker(PatchLine[] expected, out int index)
        {
            for (index = 0; index < expected.Length; index++)
            {
                var text = expected[index].Text.Trim();
                if (text.Length > 0 && ElisionMarkerPattern.IsMatch(text)) return true;
            }
            index = -1;
            return false;
        }

        private static bool TryFindLineNumberPrefix(PatchLine[] expected, out string sample)
        {
            sample = string.Empty;
            var previous = -1;
            var counted = 0;
            foreach (var line in expected)
            {
                if (line.Text.Trim().Length == 0) continue;
                var match = LineNumberPrefixPattern.Match(line.Text);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number) || number <= previous) return false;
                previous = number;
                if (counted == 0) sample = match.Value.Trim();
                counted++;
            }
            return counted >= 2;
        }

        private static string DescribeIndent(string value)
        {
            var indent = value[..(value.Length - value.TrimStart().Length)];
            if (indent.Length == 0) return "none";
            var tabs = indent.Count(character => character == '\t');
            return $"{indent.Length - tabs} space(s) and {tabs} tab(s)";
        }

        private static string DescribeTrailing(string value)
        {
            var trailing = value.Length - value.TrimEnd().Length;
            return trailing == 0 ? "none" : $"{trailing} whitespace character(s)";
        }

        private static ToolResult PatchAmbiguous(PatchIndex source, List<ResolvedPatch> matches, int editIndex)
        {
            var mode = matches[0].MatchMode;
            var listed = new JsonArray(matches.Take(MaximumPatchAmbiguousMatches).Select(match =>
            {
                var firstLine = LineIndexAt(source.Lines, match.Start);
                var lastLine = LineIndexAt(source.Lines, Math.Max(match.Start, match.Start + match.Length - 1));
                return (JsonNode)new JsonObject
                {
                    ["line"] = firstLine + 1,
                    ["end_line"] = lastLine + 1,
                    ["start"] = match.Start,
                    ["end"] = match.Start + match.Length,
                    ["context_before"] = firstLine > 0 ? CapLine(source.Lines[firstLine - 1].Text, MaximumPatchContextLineLength) : string.Empty,
                    ["first_line"] = CapLine(source.Lines[firstLine].Text, MaximumPatchContextLineLength),
                    ["context_after"] = lastLine + 1 < source.Lines.Length ? CapLine(source.Lines[lastLine + 1].Text, MaximumPatchContextLineLength) : string.Empty
                };
            }).ToArray());

            var nextStep = SupportsReplaceAll(mode)
                ? $"Nothing was written. Two valid fixes: extend old_text with the distinguishing lines shown in context_before and context_after for the one match you want, or set replace_all true to change all {matches.Count} occurrences. Then call patch_file again."
                : $"Nothing was written. old_text matched {matches.Count} places only after {mode} matching, so replace_all is not accepted for it. Extend old_text with the distinguishing lines shown in context_before and context_after until it identifies one block, then call patch_file again.";
            return Error("patch_ambiguous", $"old_text matches {matches.Count} places in the file; the file was not changed.", new JsonObject
            {
                ["next_step"] = nextStep,
                ["edit_index"] = editIndex,
                ["match_count"] = matches.Count,
                ["match_mode"] = mode,
                ["matches"] = listed,
                ["matches_truncated"] = matches.Count > MaximumPatchAmbiguousMatches
            });
        }

        private static ToolResult PatchOverlap(PatchLine[] lines, ResolvedPatch first, ResolvedPatch second)
        {
            var identical = first.Start == second.Start && first.Length == second.Length;
            var firstRange = DescribePatchRange(lines, first);
            var secondRange = DescribePatchRange(lines, second);
            var summary = identical
                ? $"Edits {first.EditIndex + 1} and {second.EditIndex + 1} match the same text at lines {firstRange.StartLine}-{firstRange.EndLine}; the file was not changed."
                : $"Edits {first.EditIndex + 1} and {second.EditIndex + 1} target overlapping text at lines {firstRange.StartLine}-{firstRange.EndLine} and lines {secondRange.StartLine}-{secondRange.EndLine}; the file was not changed.";
            var nextStep = identical
                ? "Nothing was written. Two edits resolved to the same block, so one is redundant or one old_text is not specific enough. Remove the duplicate, or extend the old_text of each edit with its own surrounding lines so they target different blocks, then call patch_file again."
                : $"Nothing was written. Merge the two edits into one edit whose old_text is the whole block from line {firstRange.StartLine} to line {secondRange.EndLine} and whose new_text is the final text for that whole block, or shrink each old_text so the two ranges do not touch. Then call patch_file again.";
            return Error("patch_overlap", summary, new JsonObject
            {
                ["next_step"] = nextStep,
                ["overlap_kind"] = identical ? "identical" : "partial",
                ["edits"] = new JsonArray(CreateOverlapEntry(first, firstRange), CreateOverlapEntry(second, secondRange))
            });
        }

        private static JsonNode CreateOverlapEntry(ResolvedPatch patch, (int StartLine, int EndLine) range) => new JsonObject
        {
            ["edit_index"] = patch.EditIndex,
            ["start_line"] = range.StartLine,
            ["end_line"] = range.EndLine,
            ["start"] = patch.Start,
            ["end"] = patch.Start + patch.Length
        };

        private static (int StartLine, int EndLine) DescribePatchRange(PatchLine[] lines, ResolvedPatch patch) =>
            (LineIndexAt(lines, patch.Start) + 1, LineIndexAt(lines, Math.Max(patch.Start, patch.Start + patch.Length - 1)) + 1);

        private static ToolResult PatchSyntaxError(IReadOnlyList<Diagnostic> errors) =>
            Error("patch_syntax_error", $"The patched result has {errors.Count} C# syntax error(s); the file was not changed.", new JsonObject
            {
                ["next_step"] = "Nothing was written. The line and column numbers below refer to the rejected result, not to the file on disk. Fix new_text so the block is syntactically complete, then call patch_file again. Set syntax_check false only if this file is intentionally not compilable C#.",
                ["errors"] = new JsonArray(errors.Select(error => (JsonNode)CapLine(error.ToString(), MaximumPatchSnippetLineLength)).ToArray())
            });

        private static PatchCandidate FindClosestCandidate(PatchIndex source, PatchLine[] expected)
        {
            var expectedKeys = expected.Where(line => line.Text.Trim().Length > 0).Select(line => CollapseWhitespace(line.Text)).ToArray();
            var windowLength = Math.Clamp(expected.Length, 1, source.Lines.Length);
            if (expectedKeys.Length == 0) return new PatchCandidate(0, windowLength - 1, 0);

            var expectedHashes = expectedKeys.Select(key => HashKey(StripWhitespace(key))).ToArray();
            var windows = FindOverlappingWindows(source, expectedHashes, windowLength, 1);
            if (windows.Count == 0) return new PatchCandidate(0, windowLength - 1, 0);

            var expectedText = ClampCompare(string.Join("\n", expectedKeys));
            var best = new PatchCandidate(windows[0].FirstLine, windows[0].LastLine, 0);
            foreach (var window in windows.OrderByDescending(window => window.Overlap).Take(MaximumFuzzyScoredWindows))
            {
                // Scored with the same metric the fuzzy level uses, so the percentage the model sees is the
                // percentage that failed the threshold.
                var similarity = ScoreWindow(source, expectedHashes, expectedText, window.FirstLine, window.LastLine, 0);
                if (similarity > best.Similarity) best = new PatchCandidate(window.FirstLine, window.LastLine, similarity);
            }
            return best;
        }

        private static double SimilarityWithin(string left, string right, double minimum)
        {
            var longest = Math.Max(left.Length, right.Length);
            if (longest == 0) return 1;
            var allowed = Math.Min(MaximumFuzzyEditDistance, (int)Math.Floor((1 - minimum) * longest));
            var distance = LevenshteinWithin(left, right, allowed);
            return distance > allowed ? 0 : 1 - (double)distance / longest;
        }

        // Banded Levenshtein with two reused rows and an early abort, so a rejected candidate costs
        // O(length * band) instead of a full quadratic pass with a fresh row allocation per line.
        private static int LevenshteinWithin(string left, string right, int maximum)
        {
            if (maximum < 0 || Math.Abs(left.Length - right.Length) > maximum) return maximum + 1;
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var column = 0; column <= right.Length; column++) previous[column] = column;
            for (var row = 1; row <= left.Length; row++)
            {
                var from = Math.Max(1, row - maximum);
                var to = Math.Min(right.Length, row + maximum);
                current[0] = row;
                for (var column = 1; column < from; column++) current[column] = maximum + 1;
                var rowMinimum = from > 1 ? maximum + 1 : row;
                for (var column = from; column <= to; column++)
                {
                    var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                    var value = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + cost);
                    current[column] = value;
                    if (value < rowMinimum) rowMinimum = value;
                }
                for (var column = to + 1; column <= right.Length; column++) current[column] = maximum + 1;
                if (rowMinimum > maximum) return maximum + 1;
                (previous, current) = (current, previous);
            }
            return Math.Min(previous[right.Length], maximum + 1);
        }

        private static PatchLine[] SplitLines(string value)
        {
            var lines = new List<PatchLine>();
            var start = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '\n') continue;
                var textEnd = index > start && value[index - 1] == '\r' ? index - 1 : index;
                lines.Add(new PatchLine(start, textEnd, index + 1, value[start..textEnd]));
                start = index + 1;
            }
            if (start < value.Length || value.Length == 0) lines.Add(new PatchLine(start, value.Length, value.Length, value[start..]));
            return [.. lines];
        }

        private static string Reindent(string newText, string matchedBlock)
        {
            var firstContentLine = SplitLines(matchedBlock).FirstOrDefault(line => line.Text.Trim().Length > 0);
            var targetIndent = firstContentLine?.Text ?? string.Empty;
            targetIndent = targetIndent[..(targetIndent.Length - targetIndent.TrimStart().Length)];
            var lines = SplitLines(newText);
            var indents = lines.Where(line => line.Text.Trim().Length > 0).Select(line => line.Text.Length - line.Text.TrimStart().Length).ToArray();
            var sourceIndent = indents.Length == 0 ? 0 : indents.Min();
            var builder = new StringBuilder(newText.Length + lines.Length * targetIndent.Length);
            foreach (var line in lines)
            {
                // Emit a blank line as its terminator alone so reindenting never leaves trailing whitespace behind.
                if (line.Text.Trim().Length > 0) builder.Append(targetIndent).Append(line.Text[Math.Min(sourceIndent, line.Text.Length)..]);
                builder.Append(Terminator(newText, line));
            }
            return builder.ToString();
        }

        private static int TranslatePatchStart(int originalStart, IEnumerable<ResolvedPatch> patches) => originalStart + patches.Where(patch => patch.Start < originalStart).Sum(patch => patch.NewText.Length - patch.Length);

        private static int LineIndexAt(PatchLine[] lines, int position)
        {
            var low = 0;
            var high = lines.Length - 1;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (lines[middle].Start <= position) low = middle; else high = middle - 1;
            }
            return low;
        }

        private static string CapLine(string value, int maximum) =>
            value.Length <= maximum ? value : $"{value[..maximum]} ...[clipped, do not copy]";

        private static string CapAroundColumn(string value, int column, int maximum)
        {
            if (value.Length <= maximum) return value;
            var start = Math.Clamp(column - maximum / 2, 0, value.Length - maximum);
            return $"{(start > 0 ? "..." : string.Empty)}{value.Substring(start, maximum)} ...[clipped, do not copy]";
        }

        // Centres the window on the mismatching line rather than the block start, so the useful line
        // survives both the line cap and the function-result truncation.
        private static string CreateNumberedSnippet(PatchLine[] lines, int firstLine, int lastLine, int focusLine, int maximumLines)
        {
            var contextStart = Math.Max(0, firstLine - 3);
            var contextEnd = Math.Min(lines.Length - 1, lastLine + 3);
            var windowStart = contextStart;
            var windowEnd = contextEnd;
            if (contextEnd - contextStart + 1 > maximumLines)
            {
                var centre = focusLine >= 0 ? focusLine : firstLine;
                windowStart = Math.Clamp(centre - maximumLines / 2, contextStart, Math.Max(contextStart, contextEnd - maximumLines + 1));
                windowEnd = Math.Min(contextEnd, windowStart + maximumLines - 1);
            }

            var builder = new StringBuilder();
            if (windowStart > contextStart)
                builder.Append($"...[{windowStart - contextStart} earlier line(s) omitted; candidate starts at line {firstLine + 1}]\n");
            for (var index = windowStart; index <= windowEnd; index++)
            {
                if (builder.Length >= MaximumPatchSnippetCharacters)
                    return $"{builder}...[snippet truncated; call read_file with start and end for the full block]";
                builder.Append(index + 1).Append('|').Append(CapLine(lines[index].Text, MaximumPatchSnippetLineLength)).Append('\n');
            }
            if (windowEnd < contextEnd)
                builder.Append($"...[{contextEnd - windowEnd} later line(s) omitted; candidate ends at line {lastLine + 1}]");
            return builder.ToString().TrimEnd('\n');
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

        private async Task<ToolResult> CompactAsync()
        {
            return await _compactAsync();
        }

        private async Task<ToolResult> CleanUpAsync()
        {
            return await _cleanupAsync();
        }

        private async Task<ToolResult> ResearchAsync(string topic, CancellationToken token)
        {
            try
            {
                var result = await _client.CreateSimpleInteractionAsync("gemini-3.5-flash-lite", [], [GeminiClient.CreateUserStep(topic, [])],
                    "You are an internal context consultant. Create concise research context only from the supplied request and context. You have no tools, no web access, no file-system access, no command access, and no process access. Do not claim current, verified, or source-backed facts unless they are explicitly supplied. State uncertainty where it matters. Do not write a user-facing answer or reveal reasoning.", null, token);
                if (string.IsNullOrWhiteSpace(result.Text)) return Error("research_unavailable", "gemini-3.5-flash-lite did not return research context.");
                return Ok("Generated private research context.", new JsonObject { ["context"] = result.Text });
            }
            catch (Exception exception)
            {
                return Error("research_unavailable", $"Research context request failed: {SanitizeExternalError(exception.Message)}");
            }
        }

        private static string SanitizeExternalError(string message)
        {
            var normalized = Regex.Replace(message, @"\s+", " ").Trim();
            return normalized.Length <= 500 ? normalized : $"{normalized[..500]}…";
        }

        private async Task<ToolResult> PlanAsync(string request, CancellationToken token)
        {
            var result = await _client.CreateSimpleInteractionAsync("gemini-3.5-flash-lite", [], [GeminiClient.CreateUserStep(request, [])],
                "You are an internal context consultant. Create a concise, implementation-ready private plan from the supplied request and context only. You have no tools, no file-system access, no command access, no process access, and no web access. Do not claim independent inspection. Do not execute changes, write a user-facing answer, or reveal reasoning. State assumptions, risks, interfaces, and validation.", null, token);
            return string.IsNullOrWhiteSpace(result.Text) ? Error("plan_unavailable", "gemini-3.5-flash-lite did not return a plan.") : Ok("Generated an implementation plan.", new JsonObject { ["plan"] = result.Text });
        }

        private async Task<ToolResult> DesignAsync(string request, CancellationToken token)
        {
            var result = await _client.CreateSimpleInteractionAsync("gemini-3.5-flash-lite", [], [GeminiClient.CreateUserStep(request, [])],
                "You are an internal UI/UX context consultant. Create a concise, implementation-ready private design brief from the supplied request and context only. You have no tools, no file-system access, no command access, no process access, and no web access. Do not claim independent inspection or current-trend research. Do not edit files, write a user-facing answer, or reveal reasoning. Specify the user goal, information hierarchy, layout and responsive behavior, component and interaction states, accessibility requirements, visual direction, and implementation considerations. Prefer practical, consistent decisions over generic design advice.", null, token);
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
        // The default encoder escapes <, >, &, ' and every non-ASCII character as \uXXXX, which inflates code
        // and markup results several-fold against the function-result size limit and hands the model escape
        // sequences to copy back into old_text. Relaxed escaping is still valid JSON.
        private static readonly JsonSerializerOptions ToolResultJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static ToolResult Ok(string summary, JsonObject? details = null) { var root = details ?? new JsonObject(); root.Insert(0, "summary", summary); root.Insert(0, "status", "completed"); return new ToolResult(true, root.ToJsonString(ToolResultJson)); }
        // Keys are serialized in insertion order and GeminiClient truncates function results from the tail,
        // so callers place the recovery instructions first and any bulky snippet last.
        private static ToolResult Error(string category, string summary, JsonObject? details = null) { var root = details ?? new JsonObject(); root.Insert(0, "summary", summary); root.Insert(0, "error_category", category); root.Insert(0, "status", "failed"); return new ToolResult(false, root.ToJsonString(ToolResultJson)); }

        private sealed record PatchEdit(string OldText, string NewText, bool ReplaceAll);
        private sealed record ResolvedPatch(int Start, int Length, string NewText, int EditIndex, string MatchMode, bool Reindented);

        /// <summary>A source line where <c>Start..TextEnd</c> is the text and <c>TextEnd..End</c> is the terminator.</summary>
        private sealed record PatchLine(int Start, int TextEnd, int End, string Text);
        private sealed record PatchIndex(string Text, PatchLine[] Lines, string[] Keys, ulong[] Hashes, Dictionary<ulong, List<int>> Postings);
        private sealed record PatchCandidate(int FirstLine, int LastLine, double Similarity);
        private sealed record PatchDiagnosis(string Cause, int OldTextLine, int FileLine, int Column, string Expected, string Actual, int MatchingPrefixLines, int MatchingSuffixLines, string Sample);
    }
}
