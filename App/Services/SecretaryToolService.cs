using App.Models;
using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class SecretaryToolService : IDisposable
    {
        private readonly SecretaryMemoryService _memory;
        private readonly NotebookDatabaseService _notebook = new();
        private readonly ChatToolService _systemTools = new();
        private readonly WeatherService _weather = new();
        private bool _newSessionDuringTurn;

        public SecretaryToolService(SecretaryMemoryService memory) => _memory = memory;

        public static JsonArray CreateDeclarations() => new()
        {
            Function("search_notebook", "Search all notebook entries related to a topic without changing the notebook.", Props(("topic", String())), "topic"),
            Function("list_personal_info", "Return stored personal knowledge relevant to a topic.", Props(("topic", String())), "topic"),
            Function("write_personal_info", "Save or replace durable personal knowledge for a topic. Do not save secrets, credentials, transient details, or speculation.", Props(("topic", String()), ("newknowledge", String())), "topic", "newknowledge"),
            Function("list_environment", "Return local environment information relevant to a topic, including PC details, current date/time/timezone, and weather when requested.", Props(("topic", String())), "topic"),
            Function("new_session", "Permanently clear Secretary chat history and begin a virtual new topic session. Call only after saving durable facts from the outgoing topic.", new JsonObject())
        };

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "search_notebook" => await SearchNotebookAsync(RequiredString(arguments, "topic"), token),
                    "list_personal_info" => await _memory.ListPersonalInfoAsync(RequiredString(arguments, "topic"), token),
                    "write_personal_info" => await _memory.WritePersonalInfoAsync(RequiredString(arguments, "topic"), RequiredString(arguments, "newknowledge"), token),
                    "list_environment" => await ListEnvironmentAsync(RequiredString(arguments, "topic"), token),
                    "new_session" => await NewSessionAsync(token),
                    _ => Error("unknown_tool", $"Secretary cannot use the tool “{name}”.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (FormatException exception) { return Error("invalid_arguments", exception.Message); }
            catch (InvalidOperationException exception) { return Error("operation_failed", exception.Message); }
            catch (Exception) { return Error("operation_failed", "Secretary could not complete that local operation."); }
        }

        public bool ConsumeNewSessionRequested()
        {
            var value = _newSessionDuringTurn;
            _newSessionDuringTurn = false;
            return value;
        }

        private async Task<ToolResult> NewSessionAsync(CancellationToken token)
        {
            var result = await _memory.ClearHistoryAsync(token);
            if (result.Success) _newSessionDuringTurn = true;
            return result;
        }

        private async Task<ToolResult> SearchNotebookAsync(string topic, CancellationToken token)
        {
            var results = await _notebook.SearchAsync(topic, 25, token);
            var items = new JsonArray();
            foreach (var result in results)
                items.Add(new JsonObject { ["entry_index"] = result.EntryIndex, ["title"] = result.Title, ["preview"] = result.Details });
            return Ok($"Found {items.Count} notebook entr{(items.Count == 1 ? "y" : "ies")}.", new JsonObject { ["items"] = items });
        }

        private async Task<ToolResult> ListEnvironmentAsync(string topic, CancellationToken token)
        {
            var details = new JsonObject();
            var system = await _systemTools.ExecuteAsync("list_system_info", new JsonObject(), token);
            details["system"] = JsonNode.Parse(system.Output);
            if (topic.Contains("weather", StringComparison.OrdinalIgnoreCase))
            {
                var location = await _memory.GetRememberedWeatherLocationAsync(token);
                if (string.IsNullOrWhiteSpace(location))
                    details["weather"] = new JsonObject { ["status"] = "unavailable", ["summary"] = "No default weather location is stored in personal information." };
                else
                    details["weather"] = JsonNode.Parse((await _weather.GetWeatherAsync(location, null, null, false, token)).Output);
            }
            return Ok("Returned relevant local environment information.", details);
        }

        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required)
        {
            var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required.Length > 0)
            {
                var values = new JsonArray();
                foreach (var item in required) values.Add(item);
                parameters["required"] = values;
            }
            return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters };
        }

        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties)
        {
            var result = new JsonObject();
            foreach (var property in properties) result[property.Name] = property.Schema;
            return result;
        }

        private static JsonObject String() => new() { ["type"] = "string" };

        private static string RequiredString(JsonObject arguments, string name)
        {
            try
            {
                var value = arguments[name]?.GetValue<string>()?.Trim();
                return string.IsNullOrWhiteSpace(value) ? throw new FormatException($"{name} is required.") : value;
            }
            catch (InvalidOperationException) { throw new FormatException($"{name} must be text."); }
        }

        private static ToolResult Ok(string summary, JsonObject? details = null)
        {
            var root = details ?? new JsonObject();
            root.Insert(0, "summary", summary);
            root.Insert(0, "status", "completed");
            return new ToolResult(true, root.ToJsonString());
        }

        private static ToolResult Error(string category, string summary) => new(false, new JsonObject
        {
            ["status"] = "failed", ["error_category"] = category, ["summary"] = summary
        }.ToJsonString());

        public void Dispose()
        {
            _weather.Dispose();
            _memory.Dispose();
        }
    }
}
