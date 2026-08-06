using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using App.Models;

namespace App.Services.Mcp
{
    /// <summary>What the Cody workspace gives a hosted CLI agent. Only actions the CLI cannot do on
    /// its own: the app's editor, its dialogs, its saved commands and its todo list.</summary>
    internal interface ICodyMcpHost
    {
        bool HasWorkspace { get; }

        Task<ToolResult> OpenInEditorAsync(string path, string reveal);

        Task<ToolResult> OpenDiffAsync(string path);

        Task<ToolResult> AskUserAsync(string question, IReadOnlyList<string> choices);

        Task<ToolResult> NotifyAsync(string title, string message);

        Task<ToolResult> ListWorkspaceCommandsAsync();

        Task<ToolResult> RunWorkspaceCommandAsync(string name);

        Task<ToolResult> AddTodoAsync(string text, string category);
    }

    /// <summary>Declares the Cody tools in MCP shape and routes a call to the workspace.</summary>
    internal sealed class CodyMcpTools(ICodyMcpHost host)
    {
        private static readonly JsonSerializerOptions ResultOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>Tools that cannot do anything without a workspace open in the app.</summary>
        private static readonly HashSet<string> WorkspaceTools = new(StringComparer.Ordinal)
        {
            "cody_open_in_editor", "cody_open_diff", "cody_list_workspace_commands", "cody_run_workspace_command"
        };

        public static JsonArray CreateDeclarations() =>
        [
            Tool(
                "cody_open_in_editor",
                "Open a workspace file in the Cody editor so the user can see it. Use this whenever you "
                    + "want the user to look at a file you are talking about.",
                Props(
                    ("path", String("Path of the file, absolute or relative to the workspace.")),
                    ("reveal", String("Optional text in the file to scroll to and highlight."))),
                "path"),
            Tool(
                "cody_open_diff",
                "Open a side by side diff of a modified file in the Cody editor, comparing the working "
                    + "copy against the last commit. Use it to show the user what changed.",
                Props(("path", String("Path of the modified file, absolute or relative to the workspace."))),
                "path"),
            Tool(
                "cody_ask_user",
                "Ask the user a question in a dialog inside the app and wait for the answer. Use this "
                    + "when you need a decision only the user can make. Give choices when the answer is "
                    + "one of a small set, otherwise the user types a free answer.",
                Props(
                    ("question", String("The question, in plain language.")),
                    ("choices", StringArray("Optional answers to pick from, at most six."))),
                "question"),
            Tool(
                "cody_notify",
                "Show a Windows notification. Use it only when you finish long work or you are blocked, "
                    + "so the user notices while looking at another window.",
                Props(
                    ("title", String("Short title.")),
                    ("message", String("One or two sentences."))),
                "title",
                "message"),
            Tool(
                "cody_list_workspace_commands",
                "List the commands saved for this workspace in .crster\\cody.json, with the command line "
                    + "each one runs. Call this before running one.",
                new JsonObject()),
            Tool(
                "cody_run_workspace_command",
                "Run a saved workspace command by its name, in the app's terminal panel where the user "
                    + "can watch it. Prefer this over running build or test commands yourself, so the "
                    + "output stays visible in the app.",
                Props(("name", String("Name of the command, as returned by cody_list_workspace_commands."))),
                "name"),
            Tool(
                "cody_add_todo",
                "Add an item to the user's todo list in the app. Use it for follow-up work you found but "
                    + "were not asked to do.",
                Props(
                    ("text", String("What needs doing, in one line.")),
                    ("category", String("Optional list to file it under. Defaults to the workspace name."))),
                "text")
        ];

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            if (WorkspaceTools.Contains(name) && !host.HasWorkspace)
                return Error("no_workspace", "No workspace is open in the app.");

            try
            {
                return name switch
                {
                    "cody_open_in_editor" => await host.OpenInEditorAsync(
                        Required(arguments, "path"),
                        Optional(arguments, "reveal")),
                    "cody_open_diff" => await host.OpenDiffAsync(Required(arguments, "path")),
                    "cody_ask_user" => await host.AskUserAsync(
                        Required(arguments, "question"),
                        ReadChoices(arguments)),
                    "cody_notify" => await host.NotifyAsync(
                        Required(arguments, "title"),
                        Required(arguments, "message")),
                    "cody_list_workspace_commands" => await host.ListWorkspaceCommandsAsync(),
                    "cody_run_workspace_command" => await host.RunWorkspaceCommandAsync(
                        Required(arguments, "name")),
                    "cody_add_todo" => await host.AddTodoAsync(
                        Required(arguments, "text"),
                        Optional(arguments, "category")),
                    _ => Error("unknown_tool", $"Cody has no tool named {name}.")
                };
            }
            catch (ArgumentException exception)
            {
                return Error("invalid_arguments", exception.Message);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Error("cancelled", "The workspace stopped the call before it finished.");
            }
            catch (Exception exception) when (exception is System.IO.IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                return Error("operation_failed", exception.Message);
            }
        }

        private static IReadOnlyList<string> ReadChoices(JsonObject arguments)
        {
            if (arguments["choices"] is not JsonArray array) return [];
            return array
                .Select(item => item?.GetValue<string>()?.Trim() ?? string.Empty)
                .Where(choice => choice.Length > 0)
                .Take(6)
                .ToList();
        }

        private static string Required(JsonObject arguments, string name)
        {
            var value = Optional(arguments, name);
            return value.Length > 0 ? value : throw new ArgumentException($"'{name}' is required.");
        }

        private static string Optional(JsonObject arguments, string name) =>
            arguments[name] switch
            {
                null => string.Empty,
                JsonValue value when value.TryGetValue<string>(out var text) => text.Trim(),
                var node => node.ToJsonString().Trim()
            };

        // MCP names the schema "inputSchema", unlike the "parameters" the chat models take.
        private static JsonObject Tool(
            string name,
            string description,
            JsonObject properties,
            params string[] required) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(item => (JsonNode?)item).ToArray())
            }
        };

        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties)
        {
            var result = new JsonObject();
            foreach (var (name, schema) in properties) result[name] = schema;
            return result;
        }

        private static JsonObject String(string description) => new()
        {
            ["type"] = "string",
            ["description"] = description
        };

        private static JsonObject StringArray(string description) => new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string" }
        };

        internal static ToolResult Ok(JsonObject details)
        {
            details.Insert(0, "success", true);
            return new ToolResult(true, details.ToJsonString(ResultOptions));
        }

        internal static ToolResult Error(string category, string summary)
        {
            var details = new JsonObject
            {
                ["success"] = false,
                ["error"] = category,
                ["summary"] = summary
            };
            return new ToolResult(false, details.ToJsonString(ResultOptions), "failed");
        }
    }
}
