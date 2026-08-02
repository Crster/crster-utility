using App.Models;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class SmartToolService
    {
        private readonly QwenClient _client;
        private readonly SecretaryToolService _secretaryTools;

        public SmartToolService(QwenClient client, SecretaryToolService secretaryTools)
        {
            _client = client;
            _secretaryTools = secretaryTools;
        }

        public static JsonArray CreateDeclarations()
        {
            var declarations = SecretaryToolService.CreateReadOnlyDeclarations();
            declarations.Insert(0, Function(
                "search_web",
                "Search the web for current external information and return grounded sources.",
                Props(("query", String("Focused web search query."))),
                "query"));
            return declarations;
        }

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "search_web" => await SearchWebAsync(Required(arguments, "query"), token),
                    "search_memory" or "get_local_context" => await _secretaryTools.ExecuteAsync(name, arguments, token),
                    _ => Error("Smart cannot use that tool.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (FormatException exception) { return Error(exception.Message); }
            catch (Exception) { return Error("Smart could not complete that operation."); }
        }

        private async Task<ToolResult> SearchWebAsync(string query, CancellationToken token)
        {
            var model = App.Settings.Current.HighCostModel;
            QwenTurnResult result;
            try
            {
                result = await _client.CreateGroundedInteractionAsync(
                    model,
                    query,
                    "Return a concise factual research answer. Include uncertainty when sources do not establish a claim.",
                    token);
            }
            catch (InvalidOperationException exception)
            {
                return Error($"Web search failed for the selected model \"{model}\": {exception.Message}");
            }
            if (string.IsNullOrWhiteSpace(result.Text))
                return Error("Web search returned no grounded answer.");

            var sources = new JsonArray(result.Sources
                .DistinctBy(source => source.Uri)
                .Select(source => (JsonNode)new JsonObject
                {
                    ["title"] = source.Title,
                    ["uri"] = source.Uri
                })
                .ToArray());
            return Ok(new JsonObject { ["answer"] = result.Text.Trim(), ["sources"] = sources });
        }

        private static string Required(JsonObject arguments, string name) =>
            arguments[name]?.GetValue<string>()?.Trim() is { Length: > 0 } value
                ? value
                : throw new FormatException($"{name} is required.");

        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required)
        {
            var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required.Length > 0)
                parameters["required"] = new JsonArray(required.Select(value => (JsonNode)value).ToArray());
            return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters };
        }

        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties)
        {
            var result = new JsonObject();
            foreach (var property in properties) result[property.Name] = property.Schema;
            return result;
        }

        private static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };
        private static ToolResult Ok(JsonObject details) { details.Insert(0, "status", "completed"); return new ToolResult(true, details.ToJsonString()); }
        private static ToolResult Error(string summary) => new(false, new JsonObject { ["status"] = "failed", ["summary"] = summary }.ToJsonString());
    }
}
