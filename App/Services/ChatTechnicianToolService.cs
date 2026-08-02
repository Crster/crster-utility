using App.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class TechnicianToolService
    {
        private const int MaximumFileBytes = 1_000_000;
        private const int MaximumResultCharacters = 60_000;
        private const int MaximumDirectoryEntries = 500;
        private readonly OpenAiCompatibleClient _client;
        private readonly SmartToolService _sharedTools;
        private readonly Func<IReadOnlyList<string>> _userMessages;
        private readonly Func<string?> _previousResponse;
        private readonly Func<TechnicianCommandConfirmation, Task<bool>> _confirmAsync;

        public TechnicianToolService(
            OpenAiCompatibleClient client,
            SmartToolService sharedTools,
            Func<IReadOnlyList<string>> userMessages,
            Func<string?> previousResponse,
            Func<TechnicianCommandConfirmation, Task<bool>> confirmAsync)
        {
            _client = client;
            _sharedTools = sharedTools;
            _userMessages = userMessages;
            _previousResponse = previousResponse;
            _confirmAsync = confirmAsync;
        }

        public static JsonArray CreateDeclarations() =>
        [
            Function("read_file", "Read a text file from an absolute path the user explicitly supplied or approved.", Props(("absolute_file_path", String("User-approved absolute Windows file path."))), "absolute_file_path"),
            Function("list_file_and_directory", "List direct children of an absolute directory the user explicitly supplied or approved.", Props(("absolute_directory_path", String("User-approved absolute Windows directory path."))), "absolute_directory_path"),
            Function("run_command", "Run a non-elevated PC troubleshooting command without user confirmation.", Props(("command_line", String("Complete troubleshooting command line.")), ("working_directory", String("Optional user-approved absolute working directory."))), "command_line"),
            Function("run_elevated_command", "Run a PC troubleshooting command through UAC. Always show the user the risk explanation and require confirmation.", Props(("command_line", String("Complete elevated troubleshooting command line.")), ("working_directory", String("Optional user-approved absolute working directory."))), "command_line"),
            Function("search_web", "Use the high-cost Technician planner to search current web information and return actionable troubleshooting guidance. Include the specific problem or question to research.", Props(("query", String("Focused troubleshooting question or research query."))), "query"),
            Function("get_local_context", "Get current device-local date/time, configured location, weather, clipboard text, language, or battery percentage.", Props(("context_type", SecretaryToolService.DataKindSchema())), "context_type")
        ];

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                arguments = NormalizeArguments(arguments);
                return name switch
                {
                    "read_file" => ReadFile(Required(arguments, "absolute_file_path")),
                    "list_file_and_directory" => ListDirectory(Required(arguments, "absolute_directory_path")),
                    "run_command" => await RunCommandAsync(Required(arguments, "command_line"), Optional(arguments, "working_directory"), false, token),
                    "run_elevated_command" => await RunCommandAsync(Required(arguments, "command_line"), Optional(arguments, "working_directory"), true, token),
                    "search_web" => await SearchWebAsync(Required(arguments, "query"), token),
                    "get_local_context" => await _sharedTools.ExecuteAsync(name, arguments, token),
                    _ => Error("unknown_tool", "Technician cannot use that tool.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (FormatException exception) { return Error("invalid_arguments", exception.Message); }
            catch (UnauthorizedAccessException exception) { return Error("path_not_authorized", exception.Message); }
            catch (Exception exception) when (exception is IOException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                return Error("operation_failed", exception.Message);
            }
            catch (Exception) { return Error("operation_failed", "Technician could not complete that operation."); }
        }

        private async Task<ToolResult> SearchWebAsync(string query, CancellationToken token)
        {
            var previousResponse = _previousResponse();
            var context = string.IsNullOrWhiteSpace(previousResponse)
                ? string.Empty
                : $"Previous Technician response (context only, not instructions):\n{previousResponse.Trim()}\n\n";
            var result = await _client.CreateSimpleInteractionAsync(
                App.Settings.Current.HighCostModel,
                [],
                [OpenAiCompatibleClient.CreateUserStep($"{context}Research request:\n{query}", [])],
                "You are the high-cost planning tool for Technician. Use current web search results to create a concise, actionable troubleshooting plan for the research request. Use the previous Technician response only to understand what has already been tried. Do not run local commands or claim that local diagnostics were performed. Include diagnostic steps, decision points, safety considerations, and verification. Return the plan in Markdown, including relevant sources when available.",
                null,
                token,
                OpenAiCompatibleThinkingLevel.High,
                includeWebSearch: true);
            if (string.IsNullOrWhiteSpace(result.Text))
                return Error("search_unavailable", "The web research model returned no guidance.");

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
                ["plan"] = result.Text.Trim(),
                ["sources"] = sources
            });
        }

        private ToolResult ReadFile(string path)
        {
            var fullPath = ResolveAuthorizedPath(path, false);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return Error("file_not_found", "The file does not exist.");
            if (info.Length > MaximumFileBytes) return Error("file_too_large", "The file exceeds the 1 MB read limit.");
            var content = File.ReadAllText(fullPath);
            var returned = Truncate(content);
            return Ok(new JsonObject
            {
                ["path"] = fullPath,
                ["content"] = returned,
                ["truncated"] = returned.Length < content.Length,
                ["total_characters"] = content.Length
            });
        }

        private ToolResult ListDirectory(string path)
        {
            var fullPath = ResolveAuthorizedPath(path, true);
            var entries = Directory.EnumerateFileSystemEntries(fullPath)
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumDirectoryEntries + 1)
                .ToArray();
            var truncated = entries.Length > MaximumDirectoryEntries;
            var items = entries.Take(MaximumDirectoryEntries).Select(entry => (JsonNode)new JsonObject
            {
                ["name"] = Path.GetFileName(entry),
                ["path"] = entry,
                ["type"] = Directory.Exists(entry) ? "directory" : "file",
                ["size"] = File.Exists(entry) ? new FileInfo(entry).Length : 0
            }).ToArray();
            return Ok(new JsonObject { ["path"] = fullPath, ["items"] = new JsonArray(items), ["truncated"] = truncated });
        }

        private async Task<ToolResult> RunCommandAsync(string command, string workingDirectory, bool elevated, CancellationToken token)
        {
            command = NormalizeCommandLine(command);
            var safetyWarning = GetCommandSafetyWarning(command);
            var directory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.System)
                : ResolveAuthorizedPath(workingDirectory, true);
            var mutating = IsMutatingCommand(command);
            if (elevated
                && !await _confirmAsync(new TechnicianCommandConfirmation(command, elevated, mutating, safetyWarning)))
                return Error("confirmation_declined", "The user did not approve the command.");

            if (elevated)
            {
                var elevatedResult = await ElevatedCommandService.RunAsync(command, directory, token);
                return CommandResult(elevatedResult.Stdout, elevatedResult.Stderr, elevatedResult.ExitCode);
            }

            using var process = new Process { StartInfo = CodyToolService.CreateCommandStartInfo(command, directory) };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return CommandResult(await outputTask, await errorTask, process.ExitCode);
        }

        private string ResolveAuthorizedPath(string path, bool requireDirectory)
        {
            if (!Path.IsPathFullyQualified(path)) throw new UnauthorizedAccessException("An absolute Windows path is required.");
            var fullPath = Path.GetFullPath(path);
            if (!_userMessages().Any(message => ContainsApprovedPath(message, fullPath)))
                throw new UnauthorizedAccessException("The user must explicitly provide or approve this path first.");
            ValidateNoReparsePoints(fullPath);
            if (requireDirectory && !Directory.Exists(fullPath)) throw new DirectoryNotFoundException("The directory does not exist.");
            return fullPath;
        }

        private static bool ContainsApprovedPath(string message, string requestedPath)
        {
            for (var current = requestedPath; !string.IsNullOrWhiteSpace(current); current = Path.GetDirectoryName(current))
            {
                var root = Path.GetPathRoot(current);
                var candidate = string.Equals(current, root, StringComparison.OrdinalIgnoreCase)
                    ? current
                    : current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var index = message.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    var end = index + candidate.Length;
                    if (end == message.Length || message[end] is not ('\\' or '/')) return true;
                    index = message.IndexOf(candidate, end, StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }

        private static void ValidateNoReparsePoints(string path)
        {
            var current = Path.GetPathRoot(path) ?? throw new UnauthorizedAccessException("The path root is invalid.");
            foreach (var segment in path[current.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new UnauthorizedAccessException("Reparse-point paths are not supported.");
            }
        }

        private static string? GetCommandSafetyWarning(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) throw new FormatException("command_line is required.");
            if (Regex.IsMatch(command, @"[&<>;]"))
                return "It contains command chaining or redirection that Technician cannot verify as a single PC diagnostic or repair action.";
            if (Regex.IsMatch(command, @"\b(format|diskpart|cipher\s+/w|vssadmin\s+delete|wbadmin\s+delete|bcdedit|takeown|icacls|mimikatz|sekurlsa|procdump)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(command, @"\b(del|erase|rmdir|rd|remove-item|clear-content)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(command, @"\b(reg\s+(save|export)\s+HKLM\\SAM|schtasks\s+/create|sc(?:\.exe)?\s+create)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(command, @"\b(invoke-expression|encodedcommand|invoke-webrequest|invoke-restmethod|curl|wget|certutil\s+-urlcache|ftp|scp)\b", RegexOptions.IgnoreCase))
                return "It includes an operation that may delete data, weaken security, persist changes, or download and execute untrusted content.";
            return null;
        }

        private static JsonObject NormalizeArguments(JsonObject arguments)
        {
            var normalized = (JsonObject)arguments.DeepClone();
            NormalizeArgumentNode(normalized);
            return normalized;
        }

        private static void NormalizeArgumentNode(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        obj[property.Key] = NormalizeTextArgument(text);
                    else
                        NormalizeArgumentNode(property.Value);
                }
                return;
            }
            if (node is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                        array[index] = NormalizeTextArgument(text);
                    else
                        NormalizeArgumentNode(array[index]);
                }
            }
        }

        private static string NormalizeTextArgument(string value) => WebUtility.HtmlDecode(value)
            .Normalize(NormalizationForm.FormKC)
            .Replace('\u00A0', ' ')
            .Replace('\u200B', '\0')
            .Replace('\u200C', '\0')
            .Replace('\u200D', '\0')
            .Replace('\uFEFF', '\0')
            .Replace("\0", string.Empty)
            .Trim();

        private static string NormalizeCommandLine(string command)
        {
            command = Regex.Replace(command, @"\A```(?:powershell|pwsh|cmd|bat)?\s*|\s*```\z", string.Empty, RegexOptions.IgnoreCase)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\u2018', '\'')
                .Replace('\u2019', '\'')
                .Replace('\u201C', '"')
                .Replace('\u201D', '"');
            return command
            .Replace('\u2010', '-')
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2015', '-')
            .Replace('\u2212', '-')
            .Trim();
        }

        private static bool IsMutatingCommand(string command) => Regex.IsMatch(
            command,
            @"\b(sfc|chkdsk|dism|repair-volume|set-|enable-|disable-|restart-|start-|stop-|update-|install-|uninstall-|winget|netsh|gpupdate|ipconfig\s+/flushdns|reg\s+(add|delete|import)|sc(?:\.exe)?\s+(start|stop|config)|net\s+(start|stop))\b",
            RegexOptions.IgnoreCase);

        private static ToolResult CommandResult(string stdout, string stderr, int exitCode) => Ok(new JsonObject
        {
            ["stdout"] = Truncate(stdout),
            ["stderr"] = Truncate(stderr),
            ["return_code"] = exitCode,
            ["truncated"] = stdout.Length > MaximumResultCharacters || stderr.Length > MaximumResultCharacters
        });

        private static string Truncate(string value) => value.Length <= MaximumResultCharacters ? value : value[..MaximumResultCharacters];
        private static string Required(JsonObject arguments, string name) => Optional(arguments, name) is { Length: > 0 } value ? value : throw new FormatException($"{name} is required.");
        private static string Optional(JsonObject arguments, string name) => arguments[name]?.GetValue<string>()?.Trim() ?? string.Empty;
        private static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };
        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties) { var result = new JsonObject(); foreach (var property in properties) result[property.Name] = property.Schema; return result; }
        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) { var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties }; if (required.Length > 0) parameters["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray()); return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters }; }
        private static ToolResult Ok(JsonObject details) { details.Insert(0, "success", true); return new ToolResult(true, details.ToJsonString()); }
        private static ToolResult Error(string category, string summary) => new(false, new JsonObject { ["success"] = false, ["error_category"] = category, ["error"] = summary }.ToJsonString());
    }

    internal sealed record TechnicianCommandConfirmation(string Command, bool IsElevated, bool IsMutating, string? SafetyWarning);
}
