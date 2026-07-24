using App.Models;
using System;
using System.Globalization;
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
        private bool _historyClearedDuringTurn;

        public SecretaryToolService(SecretaryMemoryService memory) => _memory = memory;

        public static JsonArray CreateDeclarations() => new()
        {
            new JsonObject { ["type"] = "url_context" },
            Function("list_system_info", "Return basic hardware, operating-system, current date/time, timezone, storage, memory, display, and uptime.", new JsonObject()),
            Function("get_weather", "Get current and forecast weather for a public location. Omit location to use the remembered default city.", Props(
                ("location", String()), ("start_date", String()), ("end_date", String()), ("include_hourly", Boolean()))),
            Function("search_notebook", "Search the user's notebook without changing it.", Props(("query", String()), ("limit", Integer())), "query"),
            Function("read_notebook_entry", "Read one notebook entry returned by search_notebook.", Props(("entry_index", Integer())), "entry_index"),
            Function("remember", "Save or update a durable personal fact, preference, relationship, routine, goal, location, or important work detail. Use category=location and subject_key=default_weather_city for the default weather city.", Props(
                ("category", String()), ("subject_key", String()), ("content", String()), ("importance", Integer())), "category", "subject_key", "content"),
            Function("list_memories", "List stored personal memories. category is optional.", Props(("category", String()), ("limit", Integer()))),
            Function("search_memories", "Semantically search stored personal memories.", Props(("query", String()), ("limit", Integer())), "query"),
            Function("update_memory", "Correct an existing memory by ID.", Props(("memory_id", String()), ("content", String()), ("importance", Integer())), "memory_id", "content"),
            Function("forget_memory", "Permanently delete one memory. Call only after the user directly asks to forget it.", Props(
                ("memory_id", String()), ("direct_user_request", Boolean())), "memory_id", "direct_user_request"),
            Function("list_schedule", "List local schedule events in an optional inclusive UTC date/time range.", Props(
                ("start_utc", String()), ("end_utc", String()), ("limit", Integer()))),
            Function("create_schedule_event", "Create a local schedule event. Use ISO 8601 date/times with offsets.", EventProperties(), "title", "start", "timezone_id"),
            Function("update_schedule_event", "Replace the fields of an existing local schedule event. First inspect it when current values are unknown.", EventProperties(includeId: true), "event_id", "title", "start", "timezone_id"),
            Function("delete_schedule_event", "Permanently delete a local schedule event. Call only after the user directly asks.", Props(
                ("event_id", String()), ("direct_user_request", Boolean())), "event_id", "direct_user_request"),
            Function("replace_resume", "Replace the single master resume with factual text extracted from a user-provided resume. Do not embellish.", Props(
                ("resume_text", String()), ("source_filename", String())), "resume_text"),
            Function("read_resume", "Read the stored master resume.", new JsonObject()),
            Function("clear_resume", "Permanently clear the master resume. Call only after the user directly asks.", Props(("direct_user_request", Boolean())), "direct_user_request"),
            Function("search_secretary_history", "Semantically search persisted Secretary conversations.", Props(("query", String()), ("limit", Integer())), "query"),
            Function("delete_secretary_history_turn", "Permanently delete one stored Secretary conversation turn. Call only after the user directly asks.", Props(
                ("turn_id", Integer()), ("direct_user_request", Boolean())), "turn_id", "direct_user_request"),
            Function("clear_secretary_history", "Permanently clear all stored Secretary conversation turns. Call only after the user directly asks.", Props(("direct_user_request", Boolean())), "direct_user_request")
        };

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "list_system_info" => await _systemTools.ExecuteAsync("list_system_info", new JsonObject(), token),
                    "get_weather" => await GetWeatherAsync(arguments, token),
                    "search_notebook" => await SearchNotebookAsync(RequiredString(arguments, "query"), OptionalInt(arguments, "limit") ?? 10, token),
                    "read_notebook_entry" => await ReadNotebookEntryAsync(RequiredInt(arguments, "entry_index"), token),
                    "remember" => await _memory.RememberAsync(
                        RequiredString(arguments, "category"),
                        RequiredString(arguments, "subject_key"),
                        RequiredString(arguments, "content"),
                        OptionalInt(arguments, "importance") ?? 3,
                        token),
                    "list_memories" => await _memory.ListMemoriesAsync(OptionalString(arguments, "category"), OptionalInt(arguments, "limit") ?? 30, token),
                    "search_memories" => await _memory.SearchMemoriesAsync(RequiredString(arguments, "query"), OptionalInt(arguments, "limit") ?? 10, token),
                    "update_memory" => await _memory.UpdateMemoryAsync(
                        RequiredString(arguments, "memory_id"),
                        RequiredString(arguments, "content"),
                        OptionalInt(arguments, "importance"),
                        token),
                    "forget_memory" => DirectRequest(arguments)
                        ? await _memory.ForgetMemoryAsync(RequiredString(arguments, "memory_id"), token)
                        : DirectRequestRequired(),
                    "list_schedule" => await _memory.ListScheduleAsync(
                        OptionalDateTimeOffset(arguments, "start_utc"),
                        OptionalDateTimeOffset(arguments, "end_utc"),
                        OptionalInt(arguments, "limit") ?? 50,
                        token),
                    "create_schedule_event" => await _memory.CreateScheduleEventAsync(ParseEvent(arguments, Guid.NewGuid().ToString("N")), token),
                    "update_schedule_event" => await _memory.UpdateScheduleEventAsync(ParseEvent(arguments, RequiredString(arguments, "event_id")), token),
                    "delete_schedule_event" => DirectRequest(arguments)
                        ? await _memory.DeleteScheduleEventAsync(RequiredString(arguments, "event_id"), token)
                        : DirectRequestRequired(),
                    "replace_resume" => await _memory.ReplaceResumeAsync(
                        RequiredString(arguments, "resume_text"),
                        OptionalString(arguments, "source_filename"),
                        token),
                    "read_resume" => await _memory.ReadResumeAsync(token),
                    "clear_resume" => DirectRequest(arguments) ? await _memory.ClearResumeAsync(token) : DirectRequestRequired(),
                    "search_secretary_history" => await _memory.SearchHistoryAsync(
                        RequiredString(arguments, "query"),
                        OptionalInt(arguments, "limit") ?? 10,
                        token),
                    "delete_secretary_history_turn" => DirectRequest(arguments)
                        ? await _memory.DeleteHistoryTurnAsync(RequiredLong(arguments, "turn_id"), token)
                        : DirectRequestRequired(),
                    "clear_secretary_history" => DirectRequest(arguments) ? await ClearHistoryAsync(token) : DirectRequestRequired(),
                    _ => Error("unknown_tool", $"Secretary cannot use the tool “{name}”.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (FormatException exception) { return Error("invalid_arguments", exception.Message); }
            catch (InvalidOperationException exception) { return Error("operation_failed", exception.Message); }
            catch (Exception) { return Error("operation_failed", "Secretary could not complete that local operation."); }
        }

        public bool ConsumeHistoryCleared()
        {
            var value = _historyClearedDuringTurn;
            _historyClearedDuringTurn = false;
            return value;
        }

        private async Task<ToolResult> ClearHistoryAsync(CancellationToken token)
        {
            var result = await _memory.ClearHistoryAsync(token);
            if (result.Success) _historyClearedDuringTurn = true;
            return result;
        }

        private async Task<ToolResult> GetWeatherAsync(JsonObject arguments, CancellationToken token)
        {
            var location = OptionalString(arguments, "location");
            if (string.IsNullOrWhiteSpace(location))
                location = await _memory.GetRememberedWeatherLocationAsync(token);
            return await _weather.GetWeatherAsync(
                location ?? string.Empty,
                OptionalDate(arguments, "start_date"),
                OptionalDate(arguments, "end_date"),
                OptionalBool(arguments, "include_hourly"),
                token);
        }

        private async Task<ToolResult> SearchNotebookAsync(string query, int limit, CancellationToken token)
        {
            var results = await _notebook.SearchAsync(query, Math.Clamp(limit, 1, 25), token);
            var items = new JsonArray();
            foreach (var result in results)
                items.Add(new JsonObject { ["entry_index"] = result.EntryIndex, ["title"] = result.Title, ["preview"] = result.Details });
            return Ok($"Found {items.Count} notebook entr{(items.Count == 1 ? "y" : "ies")}.", new JsonObject { ["items"] = items });
        }

        private async Task<ToolResult> ReadNotebookEntryAsync(int entryIndex, CancellationToken token)
        {
            var entry = await _notebook.GetEntryAsync(entryIndex, token);
            return entry is null
                ? Error("not_found", "That notebook entry was not found.")
                : Ok("Read the notebook entry.", new JsonObject { ["entry_index"] = entry.Index, ["content"] = entry.Content });
        }

        private static SecretaryScheduleEvent ParseEvent(JsonObject arguments, string id)
        {
            var start = RequiredDateTimeOffset(arguments, "start");
            var end = OptionalDateTimeOffset(arguments, "end");
            if (end is not null && end < start) throw new FormatException("The event end must not be before its start.");
            return new SecretaryScheduleEvent(
                id,
                RequiredString(arguments, "title"),
                start,
                end,
                RequiredString(arguments, "timezone_id"),
                OptionalBool(arguments, "is_all_day"),
                OptionalString(arguments, "location"),
                OptionalString(arguments, "notes"),
                OptionalString(arguments, "status") ?? "scheduled",
                null);
        }

        private static JsonObject EventProperties(bool includeId = false)
        {
            var properties = Props(
                ("title", String()),
                ("start", String()),
                ("end", String()),
                ("timezone_id", String()),
                ("is_all_day", Boolean()),
                ("location", String()),
                ("notes", String()),
                ("status", Enum("scheduled", "completed", "cancelled")));
            if (includeId) properties.Insert(0, "event_id", String());
            return properties;
        }

        private static JsonObject Function(string name, string description, JsonObject properties, params string[] required)
        {
            var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required.Length > 0)
            {
                var requiredValues = new JsonArray();
                foreach (var value in required) requiredValues.Add(value);
                parameters["required"] = requiredValues;
            }
            return new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters
            };
        }

        private static JsonObject Props(params (string Name, JsonObject Schema)[] properties)
        {
            var result = new JsonObject();
            foreach (var property in properties) result[property.Name] = property.Schema;
            return result;
        }

        private static JsonObject String() => new() { ["type"] = "string" };
        private static JsonObject Integer() => new() { ["type"] = "integer" };
        private static JsonObject Boolean() => new() { ["type"] = "boolean" };

        private static JsonObject Enum(params string[] values)
        {
            var items = new JsonArray();
            foreach (var value in values) items.Add(value);
            return new JsonObject { ["type"] = "string", ["enum"] = items };
        }

        private static string RequiredString(JsonObject arguments, string name) =>
            OptionalString(arguments, name) ?? throw new FormatException($"{name} is required.");

        private static string? OptionalString(JsonObject arguments, string name)
        {
            try
            {
                var value = arguments[name]?.GetValue<string>()?.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch { throw new FormatException($"{name} must be text."); }
        }

        private static int RequiredInt(JsonObject arguments, string name) =>
            OptionalInt(arguments, name) ?? throw new FormatException($"{name} is required.");

        private static int? OptionalInt(JsonObject arguments, string name)
        {
            try { return arguments[name]?.GetValue<int>(); }
            catch { throw new FormatException($"{name} must be an integer."); }
        }

        private static long RequiredLong(JsonObject arguments, string name)
        {
            try { return arguments[name]?.GetValue<long>() ?? throw new FormatException($"{name} is required."); }
            catch (InvalidOperationException) { throw new FormatException($"{name} must be an integer."); }
        }

        private static bool OptionalBool(JsonObject arguments, string name)
        {
            try { return arguments[name]?.GetValue<bool>() ?? false; }
            catch { throw new FormatException($"{name} must be true or false."); }
        }

        private static bool DirectRequest(JsonObject arguments) => OptionalBool(arguments, "direct_user_request");

        private static DateOnly? OptionalDate(JsonObject arguments, string name)
        {
            var value = OptionalString(arguments, name);
            if (value is null) return null;
            return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : throw new FormatException($"{name} must be an ISO 8601 date.");
        }

        private static DateTimeOffset RequiredDateTimeOffset(JsonObject arguments, string name) =>
            OptionalDateTimeOffset(arguments, name) ?? throw new FormatException($"{name} is required.");

        private static DateTimeOffset? OptionalDateTimeOffset(JsonObject arguments, string name)
        {
            var value = OptionalString(arguments, name);
            if (value is null) return null;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : throw new FormatException($"{name} must be an ISO 8601 date/time with an offset.");
        }

        private static ToolResult DirectRequestRequired() =>
            Error("direct_user_request_required", "This permanent deletion requires a direct request from the user.");

        private static ToolResult Ok(string summary, JsonObject? details = null)
        {
            var root = details ?? new JsonObject();
            root.Insert(0, "summary", summary);
            root.Insert(0, "status", "completed");
            return new ToolResult(true, root.ToJsonString());
        }

        private static ToolResult Error(string category, string summary) =>
            new(false, new JsonObject
            {
                ["status"] = "failed",
                ["error_category"] = category,
                ["summary"] = summary
            }.ToJsonString());

        public void Dispose()
        {
            _weather.Dispose();
            _memory.Dispose();
        }
    }
}
