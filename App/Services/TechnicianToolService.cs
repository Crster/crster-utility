using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using App.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace App.Services
{
    internal sealed class CodyToolService
    {
        private const int MaximumFileBytes = 1_000_000;
        private const int MaximumReadResultCharacters = 60_000;
        private const int DefaultCommandOutputEdgeCharacters = 1_000;
        private static readonly TimeSpan CommandForegroundTimeout = TimeSpan.FromSeconds(20);
        private const int MaximumSearchResultCharacters = 11_000;
        private const int MaximumPatchSnippetLines = 24;
        private const int MaximumPatchSnippetLineLength = 200;
        private const int MaximumPatchSnippetCharacters = 3_000;
        private const int MaximumPatchAmbiguousMatches = 8;
        private const int MaximumPatchContextLineLength = 120;
        private const int MaximumPatchSyntaxErrors = 10;
        private const int MaximumFuzzyScoredWindows = 8;
        private const int MaximumFuzzyCompareCharacters = 4_000;
        private const int MaximumFuzzyEditDistance = 400;
        private const double MinimumFuzzyPatchSimilarity = 0.80;
        private const double MinimumPatchCandidateSimilarity = 0.60;
        private const string ExactMatchMode = "exact";
        private const string LineEndingMatchMode = "line_endings";
        private const string WhitespaceMatchMode = "whitespace";
        private const string DecodedEscapeMatchMode = "decoded_escape";
        private const string PatchFormatGuidance = "Retry with this exact raw diff structure: <<<<<<< SEARCH\n[current file text]\n=======\n[replacement text]\n>>>>>>> REPLACE. The opening marker must contain the literal word SEARCH and the closing marker must contain the literal word REPLACE. Never put line numbers, character offsets, filenames, or other labels in either marker. Do not wrap the diff in Markdown fences.";
        private readonly OpenAiCompatibleClient _client;
        private readonly SecretaryToolService _secretaryTools;
        private readonly Func<string, Task<bool>> _confirmAsync;
        private readonly Func<string, JsonObject, Task<ToolResult>>? _workspaceCommandConfigurationAsync;
        private readonly Func<string, string, string?, Task<ToolResult>> _launchTerminalCommandAsync;
        public CodyToolService(OpenAiCompatibleClient client, SecretaryToolService secretaryTools,
            Func<string, Task<bool>> confirmAsync,
            Func<string, JsonObject, Task<ToolResult>>? workspaceCommandConfigurationAsync = null,
            Func<string, string, string?, Task<ToolResult>>? launchTerminalCommandAsync = null)
        {
            _client = client;
            _secretaryTools = secretaryTools;
            _confirmAsync = confirmAsync;
            _workspaceCommandConfigurationAsync = workspaceCommandConfigurationAsync;
            _launchTerminalCommandAsync = launchTerminalCommandAsync
                ?? ((_, _, _) => Task.FromResult(Error("terminal_unavailable", "Launching a Terminal tab is not available in this context.")));
        }

        public string WorkspacePath { get; set; } = string.Empty;

        public static JsonArray CreateExecutionDeclarations(bool includeWebSearch = false)
        {
            JsonArray declarations =
            [
                Function("read_workspace_file", "Use after discovering a file to read its current text before editing. The path must be inside the selected workspace. Optional offsets use slice semantics: negative values count from the end and an omitted end offset means end of file.", Props(("workspace_path", String("Workspace-relative file path returned by search_workspace_files or list_workspace_entries.")), ("start_offset", Integer("Optional inclusive character offset; negative values count from the end.")), ("end_offset", Integer("Optional exclusive character offset; omit for end of file."))), "workspace_path"),
                Function("write_workspace_file", "Use to create a file or replace file text inside the selected workspace. Prefer patch_workspace_file for focused edits. Omit both offsets to replace the whole file; provide both for a zero-based, end-exclusive replacement range.", Props(("workspace_path", String("Workspace-relative target file path.")), ("file_content", String("Exact text to write; may be empty.")), ("start_offset", Integer("Optional zero-based inclusive replacement offset.")), ("end_offset", Integer("Optional zero-based exclusive replacement offset."))), "workspace_path", "file_content"),
                Function("patch_workspace_file", "Use for focused edits to one existing workspace file. Supply raw diff-fenced SEARCH/REPLACE blocks whose SEARCH text is copied from the latest file content. Required marker lines are <<<<<<< SEARCH, =======, and >>>>>>> REPLACE.", Props(("workspace_path", String("Workspace-relative file path read immediately before this call.")), ("search_replace_diff", String("One or more raw SEARCH/REPLACE blocks without Markdown fences.")), ("validate_syntax", Boolean("When true, reject C# changes that introduce syntax errors."))), "workspace_path", "search_replace_diff"),
                Function("delete_workspace_entry", "Use to delete one file or empty directory inside the selected workspace. Always requires user confirmation.", Props(("workspace_path", String("Workspace-relative path of the file or empty directory to delete."))), "workspace_path"),
                Function("search_workspace_files", "Use first to locate relevant code or text by recursively matching file contents under a workspace-relative path. Searches contents, not filenames; excludes hidden and generated paths.", Props(("workspace_path", String("Workspace-relative directory to search; use \".\" for the entire workspace.")), ("search_pattern", String("Case-insensitive .NET regular expression matched against file contents."))), "workspace_path", "search_pattern"),
                Function("list_workspace_entries", "Use to discover filenames and directories when content search cannot locate the target. Lists direct children whose names match a pattern. Use \".\" and \".*\" for the workspace root.", Props(("workspace_path", String("Workspace-relative directory to list; use \".\" for the root.")), ("name_pattern", String("Case-insensitive .NET regular expression matched against entry names.")), ("include_hidden", Boolean("Include hidden, dot-prefixed, ignored, and generated entries; requires confirmation."))), "workspace_path", "name_pattern"),
                Function("run_workspace_command", "Use to run one non-elevated Windows command for inspection, build, lint, or verification. Include the executable and every argument in one command line. If the workspace contains multiple projects (e.g. a backend and a frontend in separate folders), set working_directory to the specific project folder so the command finds the right executable and config files. There is no stdin channel: never run a command that can prompt interactively (e.g. bare scaffolding wizards like 'npm create vite' or 'yarn create'); always supply every required argument up front (e.g. 'npm create vite@latest my-app -- --template react-ts') or set CI=1. Commands that have not finished after about 20 seconds are left running and returned with still_running=true, partial output, and a process_id — use terminate_process on that id once you are done with a long-lived process (dev server, watcher) or to abandon a stuck one. For a command meant to keep running (a dev server, a watcher, e.g. 'npm run dev'), use run_command_in_terminal instead.", Props(("command_line", String("Complete Windows command line, including executable and arguments.")), ("working_directory", String("Workspace-relative folder to run the command in. Omit to run at the workspace root.")), ("return_full_output", Boolean("Return untruncated output; may require confirmation."))), "command_line"),
                Function("run_elevated_workspace_command", "Use only when the requested Windows command requires administrator privileges. Runs through UAC and requires confirmation. If the workspace contains multiple projects, set working_directory to the specific project folder so the command finds the right executable and config files. There is no stdin channel: never run a command that can prompt interactively; always supply every required argument up front or set CI=1.", Props(("command_line", String("Complete Windows command line, including executable and arguments.")), ("working_directory", String("Workspace-relative folder to run the command in. Omit to run at the workspace root.")), ("return_full_output", Boolean("Return untruncated output; may require confirmation."))), "command_line"),
                Function("run_command_in_terminal", "Use to start a command that is meant to keep running rather than finish, such as a dev server or file watcher (e.g. 'npm run dev', 'vite', 'dotnet watch run'). Opens a visible Terminal tab in the app and starts the command there; the user can see its live output and stop it from that tab. This tool returns immediately once the tab is opened and does not return the command's output or exit code — use run_workspace_command instead for anything expected to finish and whose output or exit code you need.", Props(("command_line", String("Complete Windows command line, including executable and arguments.")), ("working_directory", String("Workspace-relative folder to run the command in. Omit to run at the workspace root.")), ("name", String("Short display label for the Terminal tab. Defaults to the command line if omitted."))), "command_line"),
                Function("list_workspace_commands", "List the commands saved in Cody's Commands menu. Each entry returns name, type, request, exe, args, cwd, env, envFile, and the derived command_line, plus the selected command. Use this before correcting a saved command. Do not read .crster/cody.json directly.", new JsonObject()),
                Function("update_workspace_command", "Replace one existing saved Commands-menu entry after list_workspace_commands identifies it. Use the exact command_line that tool returned as current_command_line, then provide the complete corrected entry: every field you omit is cleared, so always resend args, cwd, env, envFile, type, and request when they should stay. This updates the menu and its saved configuration together; never write .crster/cody.json directly.", Props(("current_command_line", String("Exact existing command_line returned by list_workspace_commands.")), ("name", String("Replacement display name.")), ("exe", String("Executable alone, with no arguments and no shell operators.")), ("args", StringArray("Every argument as its own array element, in order; use an empty array for none.")), ("cwd", String("Replacement workspace-relative working directory; use an empty string for the workspace root.")), ("env", StringMap("Inline environment variables as a flat string-to-string object; use an empty object for none.")), ("envFile", String("Workspace-relative path of an existing env file, or an empty string for none.")), ("type", String("Runtime or toolchain tag, such as node, dotnet, python, docker, or shell.")), ("request", String("Where the command runs: \"integrated\" for a Terminal tab, \"internal\" for a hidden run with captured output, or \"external\" for its own console window."))), "current_command_line", "name", "exe"),
                Function("list_running_processes", "Use when the user asks which processes are currently running. Returns process names and numeric IDs.", new JsonObject()),
                Function("terminate_process", "Use to stop one running process by its numeric ID. Always requires user confirmation.", Props(("process_id", Integer("Positive process ID returned by list_running_processes."))), "process_id"),
                Function("get_local_context", "Use only for current device-local context: date/time, configured location, weather, clipboard text, language, or battery percentage.", Props(("context_type", SecretaryToolService.DataKindSchema())), "context_type")
            ];
            if (includeWebSearch)
                declarations.Add(Function(
                    "web_search",
                    "Search the web for current external information. Use a focused query and rely only on the returned grounded answer and sources.",
                    Props(("query", String())),
                    "query"));
            return declarations;
        }

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                if (RequiresWorkspace(name) && !HasWorkspace())
                    return name.Equals("patch_workspace_file", StringComparison.Ordinal)
                        ? PatchError("workspace_required", "Select a Technician workspace before running local operations.")
                        : Error("workspace_required", "Select a Technician workspace before running local operations.");

                return name switch
                {
                    "read_workspace_file" => ReadFile(Required(arguments, "workspace_path"), OptionalSlice(arguments)),
                    "write_workspace_file" => WriteFile(Required(arguments, "workspace_path"), RequiredContent(arguments, "file_content"), OptionalWriteRange(arguments)),
                    "patch_workspace_file" => PatchFile(Required(arguments, "workspace_path"), arguments),
                    "delete_workspace_entry" => await DeleteFileAsync(Required(arguments, "workspace_path")),
                    "search_workspace_files" => await SearchFilesAsync(Required(arguments, "workspace_path"), RequiredContent(arguments, "search_pattern"), token),
                    "list_workspace_entries" => await ListFilesAsync(Required(arguments, "workspace_path"), RequiredContent(arguments, "name_pattern"), OptionalBoolean(arguments, "include_hidden")),
                    "run_workspace_command" => await ExecuteCommandAsync(Required(arguments, "command_line"), false, OptionalBoolean(arguments, "return_full_output"), Optional(arguments, "working_directory"), token),
                    "run_elevated_workspace_command" => await ExecuteCommandAsync(Required(arguments, "command_line"), true, OptionalBoolean(arguments, "return_full_output"), Optional(arguments, "working_directory"), token),
                    "run_command_in_terminal" => await LaunchTerminalCommandAsync(Required(arguments, "command_line"), Optional(arguments, "working_directory"), Optional(arguments, "name")),
                    "list_workspace_commands" or "update_workspace_command" => await ExecuteWorkspaceCommandConfigurationAsync(name, arguments),
                    "list_running_processes" => ListProcesses(),
                    "terminate_process" => await KillProcessAsync(OptionalInt(arguments, "process_id", 0)),
                    "web_search" => await WebSearchAsync(Required(arguments, "query"), token),
                    "get_local_context" => NormalizeResult(await _secretaryTools.ExecuteAsync("get_local_context", arguments, token)),
                    _ => Error("unknown_tool", $"Technician cannot use the tool “{name}”.")
                };
            }
            catch (FormatException exception) when (name.Equals("patch_workspace_file", StringComparison.Ordinal))
            {
                return PatchError("patch_invalid_format", $"The patch was rejected and the file was not changed. {exception.Message}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
            {
                return name.Equals("patch_workspace_file", StringComparison.Ordinal)
                    ? PatchError("operation_failed", exception.Message)
                    : Error("operation_failed", exception.Message);
            }
            catch (Win32Exception exception)
            {
                return Error(
                    "command_unavailable",
                    exception.Message,
                    solution: "Correct the command string and retry with an installed executable. For Windows management queries, use PowerShell CIM commands instead of WMIC.");
            }
            catch (Exception)
            {
                return name.Equals("patch_workspace_file", StringComparison.Ordinal)
                    ? PatchError("operation_failed", "Technician could not complete that local operation.")
                    : Error("operation_failed", "Technician could not complete that local operation.");
            }
        }

        private Task<ToolResult> ExecuteWorkspaceCommandConfigurationAsync(string name, JsonObject arguments) =>
            _workspaceCommandConfigurationAsync is null
                ? Task.FromResult(Error("tool_unavailable", "Commands-menu configuration is unavailable in this context."))
                : _workspaceCommandConfigurationAsync(name, arguments);

        private ToolResult ReadFile(string path, (int? Start, int? End) slice)
        {
            var fullPath = ResolveWorkspacePath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return Error("file_not_found", "The file does not exist.");
            if (info.Length > MaximumFileBytes) return Error("file_too_large", "The file exceeds the 1 MB read limit.");
            var content = File.ReadAllText(fullPath);
            var start = ResolveSliceIndex(slice.Start ?? 0, content.Length);
            var end = ResolveSliceIndex(slice.End ?? content.Length, content.Length);
            if (end < start) throw new FormatException("end_offset resolves before start_offset.");
            var selectedContent = content[start..end];
            var returnedContent = selectedContent.Length <= MaximumReadResultCharacters ? selectedContent : selectedContent[..MaximumReadResultCharacters];
            return Ok(new JsonObject
            {
                ["content"] = returnedContent,
                ["start"] = start,
                ["end"] = start + returnedContent.Length,
                ["is_truncated"] = returnedContent.Length < selectedContent.Length
            });
        }

        private static int ResolveSliceIndex(int index, int length) =>
            index < 0 ? Math.Max(0, length + index) : Math.Min(index, length);

        private ToolResult WriteFile(string path, string content, (int Start, int End)? range)
        {
            var fullPath = ResolveWorkspacePath(path);
            if (IsWorkspaceCommandConfigurationPath(fullPath))
                return Error("protected_configuration", "Use list_workspace_commands and update_workspace_command to change the Commands menu.");
            var previousContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
            if (range is not null)
            {
                if (!File.Exists(fullPath)) return Error("file_not_found", "A character-range write requires an existing file.");
                ValidateRange(range.Value, previousContent.Length);
                content = previousContent[..range.Value.Start] + content + previousContent[range.Value.End..];
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            return Ok("Wrote the file.", new JsonObject { ["path"] = fullPath, ["bytes"] = new FileInfo(fullPath).Length })
                with { DiffOld = previousContent, DiffNew = content };
        }

        private ToolResult PatchFile(string path, JsonObject arguments)
        {
            var fullPath = ResolveWorkspacePath(path);
            if (IsWorkspaceCommandConfigurationPath(fullPath))
                return PatchError("protected_configuration", "Use list_workspace_commands and update_workspace_command to change the Commands menu.");
            if (!File.Exists(fullPath)) return PatchError("file_not_found", "The patch was not applied because the file does not exist.");
            var content = File.ReadAllText(fullPath);
            var edits = ParseDiffFencedEdits(RequiredContent(arguments, "search_replace_diff"));
            var source = CreatePatchIndex(content);
            var resolved = new List<ResolvedPatch>();

            // Resolve every edit against the untouched file so a partial patch can never be persisted.
            for (var editIndex = 0; editIndex < edits.Count; editIndex++)
            {
                var edit = edits[editIndex];
                var matches = FindPatchMatches(source, edit, editIndex);
                if (matches.Count == 0)
                {
                    return PatchNotFound(source, edit, editIndex, edits.Count);
                }
                if (matches.Count > 1)
                {
                    return PatchAmbiguous(source, matches, editIndex);
                }
                resolved.AddRange(matches);
            }

            var ordered = resolved.OrderBy(match => match.Start).ThenBy(match => match.Length).ToArray();
            for (var index = 1; index < ordered.Length; index++)
                if (ordered[index].Start < ordered[index - 1].Start + ordered[index - 1].Length)
                {
                    return PatchOverlap(source.Lines, ordered[index - 1], ordered[index]);
                }

            var patched = content;
            foreach (var patch in resolved.OrderByDescending(match => match.Start))
                patched = patched[..patch.Start] + patch.NewText + patched[(patch.Start + patch.Length)..];

            // An approximate match may have landed on the wrong block, so gate C# on Roslyn even when the caller did not ask.
            if (OptionalBoolean(arguments, "validate_syntax") && Path.GetExtension(fullPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var errors = CSharpSyntaxTree.ParseText(patched).GetDiagnostics()
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Take(MaximumPatchSyntaxErrors)
                    .ToArray();
                if (errors.Length > 0)
                {
                    return PatchSyntaxError(errors);
                }
            }

            File.WriteAllText(fullPath, patched, new UTF8Encoding(false));
            return PatchApplied(resolved) with { DiffOld = content, DiffNew = patched };
        }

        private static ToolResult PatchApplied(List<ResolvedPatch> resolved)
        {
            var firstStart = resolved.Min(patch => TranslatePatchStart(patch.Start, resolved));
            return Ok(new JsonObject
            {
                ["write_start"] = firstStart,
                ["write_length"] = resolved.Sum(patch => patch.NewText.Length),
                ["match_modes"] = new JsonArray(resolved
                    .Select(patch => patch.MatchMode)
                    .Distinct(StringComparer.Ordinal)
                    .Select(mode => (JsonNode)mode)
                    .ToArray())
            });
        }

        private static readonly Regex DiffSearchMarkerPattern = new(@"<+\s*SEARCH\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex DiffSeparatorMarkerPattern = new(@"\n[\t ]*={2,}[\t ]*(?=\n|$)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex DiffReplaceMarkerPattern = new(@"\n>+(?:[\t ]+REPLACE\b)?[\t ]*(?=\n|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        private static List<PatchEdit> ParseDiffFencedEdits(string diff)
        {
            if (diff.StartsWith("```", StringComparison.Ordinal) || diff.EndsWith("```", StringComparison.Ordinal))
                throw new FormatException("diff must not be wrapped in Markdown fences.");
            var normalized = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var edits = new List<PatchEdit>();
            var position = 0;
            while (position < normalized.Length)
            {
                while (position < normalized.Length && char.IsWhiteSpace(normalized[position])) position++;
                if (position == normalized.Length) break;
                var searchMarker = DiffSearchMarkerPattern.Match(normalized, position);
                if (!searchMarker.Success || searchMarker.Index != position)
                    throw new FormatException($"Expected a SEARCH marker at character {position}.");
                var searchStart = searchMarker.Index + searchMarker.Length;
                if (searchStart < normalized.Length && normalized[searchStart] == '\n') searchStart++;
                var separatorMarker = DiffSeparatorMarkerPattern.Match(normalized, searchStart);
                if (!separatorMarker.Success) throw new FormatException("Missing separator marker.");
                var separatorStart = separatorMarker.Index;
                var replacementStart = separatorMarker.Index + separatorMarker.Length;
                if (replacementStart < normalized.Length && normalized[replacementStart] == '\n') replacementStart++;
                var replaceMarker = DiffReplaceMarkerPattern.Match(normalized, replacementStart);
                if (!replaceMarker.Success) throw new FormatException("Missing replacement closing marker.");
                var replaceStart = replaceMarker.Index;
                var oldText = normalized[searchStart..separatorStart].Trim('\n');
                var newText = normalized[replacementStart..replaceStart].Trim('\n');
                if (oldText.Length == 0) throw new FormatException("SEARCH content cannot be empty.");
                edits.Add(new PatchEdit(oldText, newText));
                position = replaceMarker.Index + replaceMarker.Length;
            }
            if (edits.Count == 0) throw new FormatException("diff must contain at least one Diff-Fenced section.");
            return edits;
        }

        private static readonly Regex ElisionMarkerPattern = new(@"^(?:(?://|#|<!--|/\*|--)\s*)?(?:\.\.\.|…)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex LineNumberPrefixPattern = new(@"^\s*(\d{1,7})\s*[|:\t]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        private static List<ResolvedPatch> FindPatchMatches(PatchIndex source, PatchEdit edit, int editIndex, bool allowArgumentEscapeFallback = true)
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
            if (expected.All(line => line.Text.Trim().Length == 0)) return [];
            var matches = ResolveCandidates(
                source,
                edit,
                editIndex,
                FindKeyedLineMatches(source, expected, CollapseWhitespace),
                WhitespaceMatchMode,
                false);
            if (matches.Count > 0) return matches;

            if (allowArgumentEscapeFallback)
            {
                var normalizedEdit = NormalizeModelLiteralPatchText(edit);
                if (normalizedEdit != edit)
                {
                    var decodedMatches = FindPatchMatches(
                        source,
                        normalizedEdit,
                        editIndex,
                        allowArgumentEscapeFallback: false);
                    if (decodedMatches.Count > 0)
                    {
                        return decodedMatches
                            .Select(match => match with
                            {
                                MatchMode = $"{DecodedEscapeMatchMode}:{match.MatchMode}"
                            })
                            .ToList();
                    }
                }
            }

            var fuzzyMatches = ResolveCandidates(
                source,
                edit,
                editIndex,
                FindFuzzyMatches(source, expected),
                "fuzzy",
                true);
            return fuzzyMatches;
        }

        private static PatchEdit NormalizeModelLiteralPatchText(PatchEdit edit) => new(
            NormalizeModelLiteralPatchText(edit.OldText),
            NormalizeModelLiteralPatchText(edit.NewText));

        private static readonly Regex UnicodeEscapePattern = new(
            @"\\u(?<hex>[0-9A-Fa-f]{4})",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

        private static string NormalizeModelLiteralPatchText(string value)
        {
            var normalized = value;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var decoded = UnicodeEscapePattern.Replace(
                        normalized,
                        match => ((char)Convert.ToInt32(match.Groups["hex"].Value, 16)).ToString())
                    .Replace("\\r\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\r", "\r", StringComparison.Ordinal)
                    .Replace("\\t", "\t", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\'", "'", StringComparison.Ordinal)
                    .Replace("\\#", "#", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
                if (decoded.Equals(normalized, StringComparison.Ordinal)) break;
                normalized = decoded;
            }
            return normalized;
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

        private static List<PatchCandidate> FindFuzzyMatches(PatchIndex source, PatchLine[] expected)
        {
            var expectedKeys = expected.Where(line => line.Text.Trim().Length > 0).Select(line => CollapseWhitespace(line.Text)).ToArray();
            if (expectedKeys.Length == 0) return [];
            if (expectedKeys.Length == 1 && StripWhitespace(expectedKeys[0]).Length < 8) return [];

            var expectedHashes = expectedKeys.Select(key => HashKey(StripWhitespace(key))).ToArray();
            var expectedText = ClampCompare(string.Join("\n", expectedKeys));
            var qualifying = new List<PatchCandidate>();
            for (var firstLine = 0; firstLine + expected.Length <= source.Lines.Length; firstLine++)
            {
                var bounds = TrimToNonBlank(source, firstLine, firstLine + expected.Length - 1);
                if (bounds is null) continue;
                var similarity = ScoreWindow(source, expectedHashes, expectedText, bounds.Value.FirstLine, bounds.Value.LastLine, MinimumFuzzyPatchSimilarity);
                if (similarity < MinimumFuzzyPatchSimilarity) continue;
                qualifying.Add(new PatchCandidate(bounds.Value.FirstLine, bounds.Value.LastLine, similarity));
            }
            if (qualifying.Count == 0) return [];
            var bestSimilarity = qualifying.Max(candidate => candidate.Similarity);
            return qualifying
                .Where(candidate => Math.Abs(candidate.Similarity - bestSimilarity) < 0.0001)
                .GroupBy(candidate => (candidate.FirstLine, candidate.LastLine))
                .Select(group => group.First())
                .ToList();
        }

        // Scores at line granularity first: in code a whole missing or altered line is one unit of difference,
        // whereas character distance exaggerates it badly on short blocks. Character distance is only consulted
        // when no line matched exactly, which is the case a line score cannot describe.
        private static double ScoreWindow(PatchIndex source, ulong[] expectedHashes, string expectedText, int firstLine, int lastLine, double minimum)
        {
            var candidateText = ClampCompare(JoinNonBlankKeys(source, firstLine, lastLine));
            var tokenSimilarity = TokenSimilarity(expectedText, candidateText);
            if (tokenSimilarity < minimum) return tokenSimilarity;
            var lineSimilarity = LineSimilarity(expectedHashes, NonBlankHashes(source, firstLine, lastLine));
            return Math.Max(tokenSimilarity, lineSimilarity);
        }

        private static double TokenSimilarity(string expected, string candidate)
        {
            var expectedTokens = Regex.Matches(expected, @"[\p{L}\p{N}_]+|[^\s]")
                .Select(match => match.Value).ToArray();
            var candidateTokens = Regex.Matches(candidate, @"[\p{L}\p{N}_]+|[^\s]")
                .Select(match => match.Value).ToArray();
            if (expectedTokens.Length == 0 || expectedTokens.Length != candidateTokens.Length) return 0;
            var total = 0d;
            for (var index = 0; index < expectedTokens.Length; index++)
            {
                var similarity = SimilarityWithin(expectedTokens[index], candidateTokens[index], MinimumPatchCandidateSimilarity);
                if (similarity < MinimumPatchCandidateSimilarity) return 0;
                total += similarity;
            }
            return total / expectedTokens.Length;
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
            int editCount)
        {
            var expected = SplitLines(edit.OldText);
            var candidate = FindClosestCandidate(source, expected);
            var diagnosis = DiagnosePatchFailure(source, expected, candidate);
            var first = source.Lines[candidate.FirstLine];
            var last = source.Lines[candidate.LastLine];
            var focusLine = diagnosis.FileLine > 0 ? diagnosis.FileLine - 1 : candidate.FirstLine;
            var candidateEnd = EndsWithNewLine(edit.OldText) ? last.End : last.TextEnd;
            var candidateText = source.Text[first.Start..candidateEnd];
            var candidateTruncated = candidateText.Length > MaximumPatchSnippetCharacters;
            var returnedCandidateText = candidateTruncated
                ? candidateText[..MaximumPatchSnippetCharacters]
                : candidateText;

            var details = new JsonObject
            {
                ["next_step"] = CreatePatchNextStep(diagnosis, candidate, expected.Length, source.Lines.Length, candidateTruncated),
                ["probable_cause"] = diagnosis.Cause,
                ["edit_index"] = editIndex,
                ["closest_search"] = returnedCandidateText,
                ["closest_search_truncated"] = candidateTruncated
            };
            if (!candidateTruncated)
            {
                details["retry_diff"] = $"""
                    <<<<<<< SEARCH
                    {returnedCandidateText}
                    =======
                    {edit.NewText}
                    >>>>>>> REPLACE
                    """;
            }
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
            var summary = $"SEARCH did not match; the file was not changed.{editLabel} Closest candidate is lines {candidate.FirstLine + 1}-{candidate.LastLine + 1} ({candidate.Similarity:P0} similar).";
            return PatchError("patch_not_found", summary, details);
        }

        private static JsonObject CreateMismatchDetail(PatchDiagnosis diagnosis, int expectedLineCount)
        {
            var detail = new JsonObject
            {
                ["search_line"] = diagnosis.OldTextLine,
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
            bool candidateTruncated)
        {
            var retry = candidateTruncated
                ? "The closest_search value is truncated; call read_file and copy a smaller unique SEARCH block before retrying."
                : "If closest_search is intended, use it as SEARCH and provide final source as REPLACE. Otherwise call read_file.";
            return diagnosis.Cause switch
            {
                "old_text_longer_than_file" =>
                    $"Nothing was written. SEARCH has {expectedLineCount} lines but the file has only {fileLineCount}. {retry}",
                "elision_marker" =>
                    $"Nothing was written. Line {diagnosis.OldTextLine} of SEARCH is an elision marker. {retry}",
                "line_number_prefix" =>
                    $"Nothing was written. SEARCH contains '{diagnosis.Sample}' style line-number prefixes. {retry}",
                "no_similar_text" =>
                    $"Nothing was written. No block in this file matches SEARCH; the closest is only {candidate.Similarity:P0} similar. Confirm the path. {retry}",
                _ =>
                    $"Nothing was written. Line {diagnosis.OldTextLine} of SEARCH does not match file line {diagnosis.FileLine}. {retry}"
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

            var nextStep = $"Nothing was written. Extend SEARCH with distinguishing surrounding lines so it selects one of the {matches.Count} matches, then retry.";
            return PatchError("patch_ambiguous", $"SEARCH matches {matches.Count} places in the file; the file was not changed.", new JsonObject
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
                ? "Nothing was written. Two sections target the same block. Remove the duplicate or extend each SEARCH with distinct surrounding lines."
                : $"Nothing was written. Merge the overlapping sections into one SEARCH/REPLACE block covering lines {firstRange.StartLine}-{secondRange.EndLine}, or make their SEARCH ranges disjoint.";
            return PatchError("patch_overlap", summary, new JsonObject
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
            PatchError("patch_syntax_error", $"The patched result has {errors.Count} C# syntax error(s); the file was not changed.", new JsonObject
            {
                ["next_step"] = "Nothing was written. Fix REPLACE so the result is syntactically complete, then retry. Set validate_syntax to false only if this C# file is intentionally incomplete.",
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
            if (IsWorkspaceCommandConfigurationPath(fullPath))
                return Error("protected_configuration", "Use update_workspace_command to change Commands-menu configuration.");
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return Error("path_not_found", "The file or directory does not exist.");
            if (!await _confirmAsync($"Delete '{fullPath}'? This cannot be undone.")) return Error("confirmation_declined", "The user did not approve deletion.");
            if (File.Exists(fullPath)) File.Delete(fullPath); else Directory.Delete(fullPath, false);
            return Ok("Deleted the selected path.", new JsonObject { ["path"] = fullPath });
        }

        private async Task<ToolResult> SearchFilesAsync(string directory, string regexPattern, CancellationToken token)
        {
            var root = ResolveWorkspacePath(directory);
            if (!Directory.Exists(root)) return Error("directory_not_found", "The directory does not exist.");

            var pattern = new Regex(regexPattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            var matches = new ConcurrentBag<SearchMatch>();
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });
            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var path in EnumerateSearchFiles(root))
                    {
                        token.ThrowIfCancellationRequested();
                        await channel.Writer.WriteAsync(path, token);
                    }
                    channel.Writer.TryComplete();
                }
                catch (Exception exception) { channel.Writer.TryComplete(exception); }
            }, token);
            var workerCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
            var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
            {
                await foreach (var path in channel.Reader.ReadAllAsync(token))
                {
                    try { await ScanSearchFileAsync(path, pattern, matches, token); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (DecoderFallbackException) { }
                }
            }, token)).ToArray();
            await producer;
            await Task.WhenAll(workers);

            var termSource = Regex.Replace(regexPattern, @"\\[AbBdDsSwWZzG]", " ");
            var searchTerms = Regex.Matches(termSource, @"[\p{L}\p{N}_-]{2,}")
                .Select(match => match.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var ordered = matches
                .Select(item => new RankedSearchMatch(item, SearchSimilarityPercentage(item, searchTerms)))
                .OrderByDescending(item => item.SimilarityPercentage)
                .ThenBy(item => item.Match.Filename, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Match.MatchStart)
                .ToArray();
            if (ordered.Length == 0)
                return Error("no_match", "no match found", solution: "Broaden or simplify the regular expression, then search the same path again.");
            var returned = new JsonArray();
            var remainingCharacters = MaximumSearchResultCharacters;
            foreach (var ranked in ordered)
            {
                var item = ranked.Match;
                var node = new JsonObject
                {
                    ["similarity_percentage"] = ranked.SimilarityPercentage,
                    ["filename"] = item.Filename,
                    ["match_start"] = item.MatchStart,
                    ["snippet_start"] = item.SnippetStart,
                    ["match"] = item.Match,
                    ["snippet"] = item.Snippet
                };
                var nodeLength = node.ToJsonString(ToolResultJson).Length + (returned.Count > 0 ? 1 : 0);
                if (nodeLength > remainingCharacters) break;
                returned.Add(node);
                remainingCharacters -= nodeLength;
            }
            return Ok(new JsonObject
            {
                ["matches"] = returned,
                ["total_matches"] = ordered.Length,
                ["is_truncated"] = returned.Count < ordered.Length
            });
        }

        private static int SearchSimilarityPercentage(SearchMatch match, IReadOnlyList<string> searchTerms)
        {
            if (searchTerms.Count == 0) return 100;
            var searchable = $"{match.Filename}\n{match.Snippet}";
            var matchedTerms = searchTerms.Count(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
            return (int)Math.Round(100d * matchedTerms / searchTerms.Count, MidpointRounding.AwayFromZero);
        }

        private static IEnumerable<string> EnumerateSearchFiles(string root)
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(root);
            while (pendingDirectories.Count > 0)
            {
                var directory = pendingDirectories.Pop();
                IEnumerable<string> files;
                IEnumerable<string> directories;
                try
                {
                    files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
                    directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var file in files)
                    if (!IsHiddenOrIgnored(file)) yield return file;
                foreach (var child in directories)
                    if (!IsHiddenOrIgnored(child)) pendingDirectories.Push(child);
            }
        }

        private static async Task ScanSearchFileAsync(string path, Regex pattern, ConcurrentBag<SearchMatch> results, CancellationToken token)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sample = new byte[Math.Min(4_096, (int)Math.Min(stream.Length, 4_096))];
            var sampleLength = await stream.ReadAsync(sample, token);
            if (sample.AsSpan(0, sampleLength).Contains((byte)0)) return;
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 65_536, leaveOpen: false);
            var before = new Queue<SearchLine>();
            var pending = new List<PendingSearchMatch>();
            await foreach (var sourceLine in ReadSearchLinesAsync(reader, token))
            {
                token.ThrowIfCancellationRequested();
                foreach (var item in pending) item.After.Add(sourceLine);
                for (var index = pending.Count - 1; index >= 0; index--)
                    if (pending[index].After.Count >= 4)
                    {
                        results.Add(MaterializeSearchMatch(path, pending[index]));
                        pending.RemoveAt(index);
                    }
                foreach (Match match in pattern.Matches(sourceLine.Text))
                    pending.Add(new PendingSearchMatch(sourceLine, before.ToArray(), match.Index, match.Value));
                before.Enqueue(sourceLine);
                while (before.Count > 4) before.Dequeue();
            }
            foreach (var item in pending) results.Add(MaterializeSearchMatch(path, item));
        }

        private static async IAsyncEnumerable<SearchLine> ReadSearchLinesAsync(
            StreamReader reader,
            [EnumeratorCancellation] CancellationToken token)
        {
            var buffer = new char[16_384];
            var line = new StringBuilder();
            var lineStart = 0;
            var absolute = 0;
            var pendingCarriageReturn = false;
            while (await reader.ReadAsync(buffer, token) is var read && read > 0)
            {
                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (pendingCarriageReturn)
                    {
                        if (character == '\n')
                        {
                            absolute++;
                            yield return new SearchLine(lineStart, line.ToString(), "\r\n");
                            line.Clear();
                            lineStart = absolute;
                            pendingCarriageReturn = false;
                            continue;
                        }
                        yield return new SearchLine(lineStart, line.ToString(), "\r");
                        line.Clear();
                        lineStart = absolute;
                        pendingCarriageReturn = false;
                    }
                    absolute++;
                    if (character == '\r') pendingCarriageReturn = true;
                    else if (character == '\n')
                    {
                        yield return new SearchLine(lineStart, line.ToString(), "\n");
                        line.Clear();
                        lineStart = absolute;
                    }
                    else line.Append(character);
                }
            }
            if (pendingCarriageReturn || line.Length > 0)
                yield return new SearchLine(lineStart, line.ToString(), pendingCarriageReturn ? "\r" : string.Empty);
        }

        private static SearchMatch MaterializeSearchMatch(string path, PendingSearchMatch item)
        {
            var lines = item.Before.Append(item.Line).Concat(item.After).ToArray();
            var combinedStart = lines[0].Start;
            var combined = string.Concat(lines.Select(line => line.Text + line.Terminator));
            var matchStart = item.Line.Start + item.Column;
            var relativeMatch = matchStart - combinedStart;
            var windowStart = Math.Clamp(relativeMatch - Math.Max(0, (256 - item.Match.Length) / 2), 0, Math.Max(0, combined.Length - 256));
            var length = Math.Min(combined.Length - windowStart, Math.Max(256, item.Match.Length));
            var snippet = combined.Substring(windowStart, length);
            var words = Regex.Matches(snippet, @"\S+");
            if (words.Count > 42)
            {
                var matchInSnippet = relativeMatch - windowStart;
                var firstWord = Math.Max(0, words.Cast<Match>().TakeWhile(word => word.Index < matchInSnippet).Count() - 21);
                var lastWord = Math.Min(words.Count - 1, firstWord + 41);
                var wordStart = words[firstWord].Index;
                var wordEnd = words[lastWord].Index + words[lastWord].Length;
                snippet = snippet[wordStart..wordEnd];
                windowStart += wordStart;
            }
            return new SearchMatch(path, matchStart, combinedStart + windowStart, item.Match, snippet);
        }

        private async Task<ToolResult> ListFilesAsync(string path, string regex, bool hidden)
        {
            var root = ResolveWorkspacePath(string.IsNullOrWhiteSpace(path) ? "." : path);
            if (!Directory.Exists(root)) return Error("directory_not_found", "The path is not a directory.");
            if (hidden && !await _confirmAsync($"Include hidden, dot-prefixed, ignored, and generated entries in '{root}'?"))
                return Error("confirmation_declined", "Hidden listing was not approved.", solution: "Call list_workspace_entries with include_hidden set to false, or ask the user to approve hidden access.");
            var filter = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            var entries = Directory.EnumerateFileSystemEntries(root).ToArray();
            var ignored = hidden ? [] : await GetGitIgnoredEntriesAsync(root, entries);
            var items = entries
                .Where(item => hidden || (!IsHiddenOrIgnored(item) && !ignored.Contains(item)))
                .Where(item => filter.IsMatch(Path.GetRelativePath(root, item)))
                .Take(500)
                .Select(item => (JsonNode)new JsonObject
                {
                    ["path"] = item,
                    ["name"] = Path.GetFileName(item),
                    ["extension"] = Path.GetExtension(item),
                    ["attribute"] = File.GetAttributes(item).ToString(),
                    ["size"] = File.Exists(item) ? new FileInfo(item).Length : 0
                }).ToArray();
            return Ok(new JsonObject { ["items"] = new JsonArray(items), ["is_truncated"] = items.Length == 500 });
        }

        private static async Task<HashSet<string>> GetGitIgnoredEntriesAsync(string root, string[] entries)
        {
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo("git", $"-C \"{root}\" check-ignore --no-index --stdin")
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                foreach (var entry in entries)
                    await process.StandardInput.WriteLineAsync(Path.GetRelativePath(root, entry));
                process.StandardInput.Close();
                while (await process.StandardOutput.ReadLineAsync() is { } relative)
                    ignored.Add(Path.GetFullPath(Path.Combine(root, relative)));
                await process.WaitForExitAsync();
            }
            catch (Win32Exception) { }
            catch (IOException) { }
            return ignored;
        }

        private static bool IsHiddenOrIgnored(string path)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(".", StringComparison.Ordinal)) return true;
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System) || attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            return name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".next", StringComparison.OrdinalIgnoreCase)
                || name.Equals("dist", StringComparison.OrdinalIgnoreCase)
                || name.Equals("build", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<ToolResult> ExecuteCommandAsync(string command, bool elevated, bool full, string workingDirectorySubpath, CancellationToken token)
        {
            EnsureWorkspace();
            var workingDirectory = string.IsNullOrEmpty(workingDirectorySubpath)
                ? Path.GetFullPath(WorkspacePath)
                : ResolveWorkspacePath(workingDirectorySubpath);
            if (!Directory.Exists(workingDirectory))
                return Error("directory_not_found", "The requested working_directory does not exist in the workspace.");
            if (elevated || full || IsRiskyCommand(command))
            {
                var qualifier = elevated ? "elevated " : full ? "with unbounded output " : string.Empty;
                if (!await _confirmAsync($"Run {qualifier}command '{command}' in '{workingDirectory}'?"))
                    return Error("confirmation_declined", "The user did not approve the command.", solution: "Run a safe bounded command instead, or ask the user to approve this operation.");
            }
            if (elevated)
            {
                var result = await ElevatedCommandService.RunAsync(command, workingDirectory, token);
                return CommandResult(result.Stdout, result.Stderr, result.ExitCode, full);
            }

            if (!TryCreateCommandStartInfo(command, workingDirectory, out var startInfo, out var resolutionError))
                return Error("command_not_resolved", resolutionError!, solution: "Provide a direct executable and its arguments; shell built-ins (start, dir, etc.) and operators (&, |, <, >, ;) are not supported.");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) stdout.AppendLine(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) stderr.AppendLine(args.Data); };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var waitTask = process.WaitForExitAsync(token);
                var timeoutTask = Task.Delay(CommandForegroundTimeout, CancellationToken.None);
                var completed = await Task.WhenAny(waitTask, timeoutTask);
                token.ThrowIfCancellationRequested();
                if (completed == timeoutTask)
                    return StillRunningResult(process.Id, stdout.ToString(), stderr.ToString(), full);

                await waitTask;
                var exitCode = process.ExitCode;
                process.Dispose();
                return CommandResult(stdout.ToString(), stderr.ToString(), exitCode, full);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                process.Dispose();
                throw;
            }
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        private static ToolResult StillRunningResult(int processId, string stdout, string stderr, bool full)
        {
            var truncated = !full && (stdout.Length > DefaultCommandOutputEdgeCharacters * 2 || stderr.Length > DefaultCommandOutputEdgeCharacters * 2);
            return Ok(
                "Command is still running after "
                + $"{CommandForegroundTimeout.TotalSeconds:0}s; it was not stopped, and its output so far is included below. "
                + "If this is a long-lived process (dev server, watcher), that is expected: use terminate_process with process_id when you are done with it, "
                + "or tell the user its process_id. If it is waiting on an interactive prompt (for example an 'npm create'/'yarn create' scaffolding wizard), "
                + "stop it with terminate_process and re-run with every required argument or flag supplied up front (for example "
                + "'npm create vite@latest my-app -- --template react-ts') or with the CI=1 environment variable set, since there is no way to send input to a running command.",
                new JsonObject
                {
                    ["still_running"] = true,
                    ["process_id"] = processId,
                    ["stdout"] = full ? stdout : TruncateOutput(stdout),
                    ["stderr"] = full ? stderr : TruncateOutput(stderr),
                    ["truncated"] = truncated
                });
        }

        internal static bool TryCreateCommandStartInfo(string command, string workingDirectory, out ProcessStartInfo startInfo, out string? error)
        {
            startInfo = null!;
            error = null;
            if (TryCreateDirectCommandStartInfo(command, workingDirectory, out var directStartInfo))
            {
                startInfo = directStartInfo;
                return true;
            }

            error = ContainsShellOperator(command)
                ? $"The command '{command}' uses shell operators (&, |, <, >, ;) which are not supported; run each executable separately."
                : $"Unable to resolve '{command}' to a runnable executable on PATH or in the working directory.";
            return false;
        }

        private async Task<ToolResult> LaunchTerminalCommandAsync(string command, string workingDirectorySubpath, string name)
        {
            EnsureWorkspace();
            return await _launchTerminalCommandAsync(command, workingDirectorySubpath, string.IsNullOrWhiteSpace(name) ? null : name);
        }

        internal static ProcessStartInfo CreateCommandStartInfo(string command, string workingDirectory)
        {
            if (TryCreateCommandStartInfo(command, workingDirectory, out var startInfo, out var error))
                return startInfo;
            throw new InvalidOperationException(error);
        }

        private static bool TryCreateDirectCommandStartInfo(
            string command,
            string workingDirectory,
            out ProcessStartInfo startInfo)
        {
            startInfo = null!;
            if (ContainsShellOperator(command) || !TryReadFirstArgument(command, out var executable, out var arguments))
                return false;

            var resolvedExecutable = ResolveExecutable(executable, workingDirectory);
            if (resolvedExecutable is null
                && !TryResolveUnquotedExecutablePath(command, workingDirectory, out resolvedExecutable, out arguments))
                return false;

            if (Path.GetExtension(resolvedExecutable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(resolvedExecutable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                // Batch scripts need cmd.exe, but retain the executable/argument split so commands
                // such as "npm run lint" do not become a fictitious executable named "npm run lint".
                var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrWhiteSpace(commandProcessor))
                    commandProcessor = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                startInfo = new ProcessStartInfo(commandProcessor)
                {
                    Arguments = $"/d /s /c \"\"{resolvedExecutable}\"{(arguments.Length == 0 ? string.Empty : " " + arguments)}\"",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                return true;
            }

            startInfo = new ProcessStartInfo(resolvedExecutable)
            {
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            return true;
        }

        private static bool TryReadFirstArgument(string command, out string executable, out string arguments)
        {
            executable = string.Empty;
            arguments = string.Empty;
            var start = 0;
            while (start < command.Length && char.IsWhiteSpace(command[start])) start++;
            if (start == command.Length) return false;

            var end = start;
            if (command[start] == '"')
            {
                end++;
                while (end < command.Length && command[end] != '"') end++;
                if (end == command.Length) return false;
                executable = command[(start + 1)..end];
                end++;
            }
            else
            {
                while (end < command.Length && !char.IsWhiteSpace(command[end])) end++;
                executable = command[start..end];
            }

            arguments = end < command.Length ? command[end..].TrimStart() : string.Empty;
            return !string.IsNullOrWhiteSpace(executable);
        }

        /// <summary>Accepts an unquoted absolute executable path containing spaces, such as a path copied from where.exe.</summary>
        private static bool TryResolveUnquotedExecutablePath(
            string command,
            string workingDirectory,
            out string resolvedExecutable,
            out string arguments)
        {
            resolvedExecutable = string.Empty;
            arguments = string.Empty;
            var start = 0;
            while (start < command.Length && char.IsWhiteSpace(command[start])) start++;
            if (start == command.Length || !Path.IsPathFullyQualified(command[start..])) return false;

            var fullCandidate = ResolveExecutable(command[start..].Trim(), workingDirectory);
            if (fullCandidate is not null)
            {
                resolvedExecutable = fullCandidate;
                return true;
            }

            for (var separator = command.Length - 1; separator > start; separator--)
            {
                if (!char.IsWhiteSpace(command[separator])) continue;
                var candidate = command[start..separator].TrimEnd();
                var resolved = ResolveExecutable(candidate, workingDirectory);
                if (resolved is null) continue;
                resolvedExecutable = resolved;
                arguments = command[separator..].TrimStart();
                return true;
            }
            return false;
        }

        private static bool ContainsShellOperator(string command)
        {
            var quoted = false;
            for (var index = 0; index < command.Length; index++)
            {
                if (command[index] == '"') quoted = !quoted;
                else if (!quoted && command[index] is '&' or '|' or '<' or '>' or ';') return true;
            }
            return false;
        }

        private static string? ResolveExecutable(string executable, string workingDirectory)
        {
            var candidates = new List<string>();
            if (Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            {
                candidates.Add(Path.GetFullPath(executable, workingDirectory));
            }
            else
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var extensions = Path.GetExtension(executable).Length > 0
                    ? new[] { string.Empty }
                    : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var extension in extensions)
                    candidates.Add(Path.Combine(workingDirectory, executable + extension));
                foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    foreach (var extension in extensions)
                        candidates.Add(Path.Combine(directory, executable + extension));
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        private static ToolResult CommandResult(string stdout, string stderr, int exitCode, bool full)
        {
            var truncated = !full && (stdout.Length > DefaultCommandOutputEdgeCharacters * 2 || stderr.Length > DefaultCommandOutputEdgeCharacters * 2);
            return Ok(new JsonObject
            {
                ["stdout"] = full ? stdout : TruncateOutput(stdout),
                ["stderr"] = full ? stderr : TruncateOutput(stderr),
                ["return_code"] = exitCode,
                ["truncated"] = truncated
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

        private async Task<ToolResult> WebSearchAsync(string query, CancellationToken token)
        {
            if (!_client.SupportsBuiltInWebSearch(App.Settings.Current.LowCostModel))
            {
                return Error(
                    "search_unavailable",
                    "The selected model does not support built-in web search. Choose a model with web-search support, then retry.");
            }
            var result = await _client.CreateSimpleInteractionAsync(
                App.Settings.Current.LowCostModel,
                [],
                [OpenAiCompatibleClient.CreateUserStep(query, [])],
                "Answer the search query using grounded web results. Be concise and factual. Include uncertainty when the available sources do not establish a claim.",
                null,
                token,
                OpenAiCompatibleThinkingLevel.Disabled,
                includeWebSearch: true);
            if (string.IsNullOrWhiteSpace(result.Text))
                return Error("search_unavailable", "Web search returned no grounded answer.");

            var sources = new JsonArray(result.Sources
                .DistinctBy(source => source.Uri)
                .Select(source => (JsonNode)new JsonObject
                {
                    ["title"] = source.Title,
                    ["uri"] = source.Uri
                })
                .ToArray());
            return Ok(new JsonObject
            {
                ["answer"] = result.Text.Trim(),
                ["sources"] = sources
            });
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

        private bool IsWorkspaceCommandConfigurationPath(string fullPath) =>
            string.Equals(
                Path.GetFullPath(fullPath),
                Path.Combine(Path.GetFullPath(WorkspacePath), ".crster", "cody.json"),
                StringComparison.OrdinalIgnoreCase);

        private void EnsureWorkspace()
        {
            if (!HasWorkspace()) throw new InvalidOperationException("Choose an existing Technician workspace first.");
        }

        private bool HasWorkspace() => !string.IsNullOrWhiteSpace(WorkspacePath) && Directory.Exists(WorkspacePath);

        private static bool RequiresWorkspace(string toolName) => toolName is
            "read_workspace_file" or "write_workspace_file" or "patch_workspace_file" or "delete_workspace_entry"
            or "search_workspace_files" or "list_workspace_entries" or "run_workspace_command" or "run_elevated_workspace_command"
            or "list_workspace_commands" or "update_workspace_command" or "run_command_in_terminal";

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

        private static bool IsRiskyCommand(string command)
        {
            return Regex.IsMatch(
                       command,
                       @"\b(rm|rmdir|rd|del|erase|Remove-Item|Clear-Content|format|diskpart|cipher|reg(?:\.exe)?|regedit|Set-ItemProperty|Remove-ItemProperty|takeown|icacls|cacls|attrib|bcdedit|shutdown|restart|taskkill|Stop-Process|Stop-Service|Restart-Service|sc|net|msiexec|winget|choco|scoop|Set-ExecutionPolicy)\b",
                       RegexOptions.IgnoreCase)
                   || Regex.IsMatch(command, @"\bgit(?:\.exe)?\s+(clean\b|reset\s+--hard\b)", RegexOptions.IgnoreCase);
        }
        private static string TruncateOutput(string value) => value.Length <= DefaultCommandOutputEdgeCharacters * 2
            ? value
            : value[..DefaultCommandOutputEdgeCharacters] + "\n...[output truncated]...\n" + value[^DefaultCommandOutputEdgeCharacters..];
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
        private static (int? Start, int? End) OptionalSlice(JsonObject arguments)
        {
            return (arguments["start_offset"]?.GetValue<int>(), arguments["end_offset"]?.GetValue<int>());
        }
        private static (int Start, int End)? OptionalWriteRange(JsonObject arguments)
        {
            var start = arguments["start_offset"]?.GetValue<int>();
            var end = arguments["end_offset"]?.GetValue<int>();
            if (start.HasValue != end.HasValue) throw new FormatException("start and end must be provided together.");
            return start.HasValue ? (start.Value, end!.Value) : null;
        }
        private static JsonObject String(string? description = null) { var schema = new JsonObject { ["type"] = "string" }; if (description is not null) schema["description"] = description; return schema; }
        private static JsonObject Integer(string? description = null) { var schema = new JsonObject { ["type"] = "integer" }; if (description is not null) schema["description"] = description; return schema; }
        private static JsonObject Boolean(string? description = null) { var schema = new JsonObject { ["type"] = "boolean" }; if (description is not null) schema["description"] = description; return schema; }
        private static JsonObject StringArray(string? description = null) { var schema = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }; if (description is not null) schema["description"] = description; return schema; }
        private static JsonObject StringMap(string? description = null) { var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } }; if (description is not null) schema["description"] = description; return schema; }
        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties) { var result = new JsonObject(); foreach (var property in properties) result[property.Name] = property.Schema; return result; }
        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) { var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties }; if (required.Length > 0) parameters["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray()); return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters }; }
        // The default encoder escapes <, >, &, ' and every non-ASCII character as \uXXXX, which inflates code
        // and markup results several-fold against the function-result size limit and hands the model escape
        // sequences to copy back into old_text. Relaxed escaping is still valid JSON.
        private static readonly JsonSerializerOptions ToolResultJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static ToolResult Ok(JsonObject? details = null) { var root = details ?? new JsonObject(); root.Insert(0, "success", true); return new ToolResult(true, root.ToJsonString(ToolResultJson)); }
        private static ToolResult Ok(string summary, JsonObject? details = null) => Ok(details);
        private static ToolResult Error(string category, string summary, JsonObject? details = null, string? solution = null)
        {
            return new ToolResult(false, new JsonObject
            {
                ["success"] = false,
                ["error"] = summary,
                ["suggestion"] = solution ?? SolutionFor(category)
            }.ToJsonString(ToolResultJson));
        }
        private static string SolutionFor(string category) => category switch
        {
            "workspace_required" => "Select an existing Technician workspace, then retry the tool.",
            "file_not_found" or "path_not_found" or "directory_not_found" => "Check the path with list_workspace_entries, then retry with an existing path.",
            "patch_not_found" => "Read the target file and retry with a more distinctive SEARCH block copied from the current content.",
            "patch_ambiguous" => "Add more surrounding source text to the SEARCH block so one location is the unique best match.",
            "confirmation_declined" => "Continue without the risky action or ask the user to approve it explicitly.",
            "no_match" => "Broaden or simplify the pattern and search again.",
            _ => "Inspect the error, correct the tool arguments, and retry with the narrowest safe operation."
        };
        private static ToolResult PatchError(string category, string summary, JsonObject? details = null)
        {
            var suggestion = category.StartsWith("patch_", StringComparison.Ordinal)
                ? $"{SolutionFor(category)} {PatchFormatGuidance}"
                : SolutionFor(category);
            return Error(category, summary, solution: suggestion);
        }

        private static ToolResult NormalizeResult(ToolResult result)
        {
            try
            {
                var source = JsonNode.Parse(result.Output) as JsonObject;
                var details = source is null ? new JsonObject() : (JsonObject)source.DeepClone();
                details.Remove("success");
                details.Remove("status");
                details.Remove("summary");
                details.Remove("error");
                details.Remove("error_category");
                details.Remove("solution");
                details.Remove("suggestion");
                if (result.Success) return Ok(details);
                var error = source?["error"]?.GetValue<string>()
                    ?? source?["summary"]?.GetValue<string>()
                    ?? "The delegated tool failed.";
                var solution = source?["suggestion"]?.GetValue<string>()
                    ?? source?["solution"]?.GetValue<string>()
                    ?? "Inspect the error and retry with corrected arguments.";
                return Error("delegated_tool_failed", error, solution: solution);
            }
            catch (JsonException)
            {
                return result.Success
                    ? Ok(new JsonObject { ["content"] = result.Output })
                    : Error("delegated_tool_failed", result.Output, solution: "Inspect the error and retry with corrected arguments.");
            }
        }

        private sealed record PatchEdit(string OldText, string NewText);
        private sealed record ResolvedPatch(int Start, int Length, string NewText, int EditIndex, string MatchMode, bool Reindented);
        private sealed record SearchLine(int Start, string Text, string Terminator);
        private sealed record SearchMatch(string Filename, int MatchStart, int SnippetStart, string Match, string Snippet);
        private sealed record RankedSearchMatch(SearchMatch Match, int SimilarityPercentage);
        private sealed class PendingSearchMatch(SearchLine line, SearchLine[] before, int column, string match)
        {
            public SearchLine Line { get; } = line;
            public SearchLine[] Before { get; } = before;
            public int Column { get; } = column;
            public string Match { get; } = match;
            public List<SearchLine> After { get; } = [];
        }

        /// <summary>A source line where <c>Start..TextEnd</c> is the text and <c>TextEnd..End</c> is the terminator.</summary>
        private sealed record PatchLine(int Start, int TextEnd, int End, string Text);
        private sealed record PatchIndex(string Text, PatchLine[] Lines, string[] Keys, ulong[] Hashes, Dictionary<ulong, List<int>> Postings);
        private sealed record PatchCandidate(int FirstLine, int LastLine, double Similarity);
        private sealed record PatchDiagnosis(string Cause, int OldTextLine, int FileLine, int Column, string Expected, string Actual, int MatchingPrefixLines, int MatchingSuffixLines, string Sample);
    }
}
