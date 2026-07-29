using App.Models;
using Cronos;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization;

namespace App.Services
{
    internal sealed class SecretaryToolService : IDisposable
    {
        private const int MaximumResults = 20;
        private readonly SecretaryMemoryService _memory;
        private readonly TodoRepository _todos = new();
        private readonly WeatherService _weather = new();

        public SecretaryToolService(SecretaryMemoryService memory) => _memory = memory;

        public static JsonArray CreateDeclarations() => new()
        {
            Function("search_saved_information", "Search all saved notes, memos, and todos using a .NET regular expression. regex is required. Use a focused, case-insensitive-compatible pattern such as four13\\s+group.", Props(("regex", String())), "regex"),
            Function("find_notes", "Find notes containing the supplied text.", Props(("query", String())), "query"),
            Function("find_memos", "Find saved memos. topic and query are optional: omit both to retrieve all saved memos, for example when summarizing what is known about the user.", Props(("topic", MemoTopic()), ("query", String()))),
            Function("write_memo", "Save a clearly stated detail that may make future help more personal, accurate, or useful. Never save secrets, credentials, guesses, or invented claims.", Props(("topic", MemoTopic()), ("value", String())), "topic", "value"),
            Function("delete_memo", "Delete an incorrect, outdated, conflicting, or explicitly forgotten memo by key. Find the memo first unless its key is already known.", Props(("key", String())), "key"),
            Function("find_todos", "Find todos in a category whose text contains the query.", Props(("category", String()), ("query", String())), "category", "query"),
            Function("get_todo_categories", "List all todo categories and their descriptions.", new JsonObject()),
            Function("get_todos", "Get unfinished unscheduled todos and scheduled todos due now or within 30 minutes.", new JsonObject()),
            Function("write_todo", "Save a todo. notify is optional standard five-field cron text interpreted in local time.", Props(("value", String()), ("category", String()), ("notify", String())), "value", "category"),
            Function("get_data", "Get one current local data value.", Props(("kind", DataKindSchema())), "kind")
        };

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "search_saved_information" => await SearchLocalKnowledgeAsync(RequiredString(arguments, "regex"), token),
                    "find_notes" => FindNotes(RequiredString(arguments, "query")),
                    "find_memos" => await FindMemosAsync(OptionalString(arguments, "topic"), OptionalString(arguments, "query"), token),
                    "write_memo" => await WriteMemoAsync(RequiredString(arguments, "topic"), RequiredString(arguments, "value"), token),
                    "delete_memo" => DeleteMemo(RequiredString(arguments, "key")),
                    "find_todos" => FindTodos(RequiredString(arguments, "category"), RequiredString(arguments, "query")),
                    "get_todo_categories" => GetTodoCategories(),
                    "get_todos" => GetTodos(),
                    "write_todo" => await WriteTodoAsync(RequiredString(arguments, "value"), RequiredString(arguments, "category"), OptionalString(arguments, "notify"), token),
                    "get_data" => await GetDataAsync(RequiredString(arguments, "kind"), token),
                    _ => Error("unknown_tool", $"Secretary cannot use the tool “{name}”.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (FormatException exception) { return Error("invalid_arguments", exception.Message); }
            catch (ArgumentException exception) { return Error("invalid_arguments", exception.Message); }
            catch (Exception) { return Error("operation_failed", "Secretary could not complete that local operation."); }
        }

        private Task<ToolResult> SearchLocalKnowledgeAsync(string regexPattern, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            var matches = new List<(DateTime Timestamp, JsonObject Item)>();
            foreach (var note in _memory.ListNotes())
            {
                if (!regex.IsMatch(note.Value)) continue;
                matches.Add((note.Timestamp, new JsonObject
                {
                    ["source"] = "note",
                    ["key"] = note.Id,
                    ["value"] = note.Value,
                    ["written_utc"] = note.Timestamp.ToString("O")
                }));
            }

            foreach (var memo in _memory.ListMemos())
            {
                if (!regex.IsMatch(memo.Value)) continue;
                matches.Add((memo.Timestamp, new JsonObject
                {
                    ["source"] = "memo",
                    ["key"] = memo.Id,
                    ["topic"] = memo.Topic,
                    ["value"] = memo.Value,
                    ["written_utc"] = memo.Timestamp.ToString("O")
                }));
            }

            foreach (var todo in _todos.List())
            {
                if (!regex.IsMatch(todo.Value)) continue;
                var value = TodoJson(todo);
                value.Insert(0, "source", "todo");
                matches.Add((todo.CreatedAt, value));
            }

            var items = new JsonArray(matches
                .OrderByDescending(match => match.Timestamp)
                .Take(MaximumResults)
                .Select(match => (JsonNode)match.Item)
                .ToArray());
            return Task.FromResult(Ok($"Found {items.Count} saved result(s).", new JsonObject { ["items"] = items }));
        }

        private ToolResult FindNotes(string query)
        {
            var items = new JsonArray(_memory.FindNotes(query, MaximumResults).Select(item => (JsonNode)new JsonObject
            {
                ["key"] = item.Id, ["value"] = item.Value, ["written_utc"] = item.Timestamp.ToString("O")
            }).ToArray());
            return Ok($"Found {items.Count} note(s).", new JsonObject { ["items"] = items });
        }

        private async Task<ToolResult> FindMemosAsync(string topic, string query, CancellationToken token)
        {
            var matches = await _memory.FindMemosAsync(topic, query, MaximumResults, token);
            var items = new JsonArray(matches.Select(item => (JsonNode)new JsonObject
            {
                ["key"] = item.Id, ["topic"] = item.Topic, ["value"] = item.Value, ["written_utc"] = item.Timestamp.ToString("O")
            }).ToArray());
            return Ok($"Found {items.Count} memo(s).", new JsonObject { ["items"] = items });
        }

        private async Task<ToolResult> WriteMemoAsync(string topic, string value, CancellationToken token)
        {
            var memo = await _memory.WriteMemoAsync(topic, value, token);
            return Ok("Saved the memo.", new JsonObject { ["key"] = memo.Id, ["topic"] = memo.Topic });
        }

        private ToolResult DeleteMemo(string key) =>
            _memory.DeleteMemo(key)
                ? Ok("Deleted the memo.", new JsonObject { ["key"] = key })
                : Error("memo_not_found", "No memo with that key was found.");

        private ToolResult FindTodos(string category, string query)
        {
            var items = new JsonArray(_todos.ListByCategory(category.Trim())
                .Where(item => item.Value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(MaximumResults)
                .Select(TodoJson).ToArray());
            return Ok($"Found {items.Count} todo(s).", new JsonObject { ["items"] = items });
        }

        private ToolResult GetTodoCategories()
        {
            var items = new JsonArray(_todos.ListCategories().Select(item => (JsonNode)new JsonObject
            {
                ["category"] = item.Id, ["description"] = item.Description
            }).ToArray());
            return Ok($"Found {items.Count} todo categor{(items.Count == 1 ? "y" : "ies")}.", new JsonObject { ["items"] = items });
        }

        private ToolResult GetTodos()
        {
            var now = DateTimeOffset.Now;
            var end = now.AddMinutes(30);
            var items = new JsonArray();
            foreach (var todo in _todos.List().Where(item => !item.IsDone))
            {
                if (string.IsNullOrWhiteSpace(todo.Notify))
                {
                    items.Add(TodoJson(todo));
                    continue;
                }

                CronExpression cron;
                try { cron = CronExpression.Parse(todo.Notify, CronFormat.Standard); }
                catch (CronFormatException) { continue; }
                var next = cron.GetNextOccurrence(now.AddMinutes(-1), TimeZoneInfo.Local);
                if (next is null || next > end) continue;
                var value = TodoJson(todo);
                value["next_notify_local"] = next.Value.ToString("O");
                items.Add(value);
            }
            return Ok($"Found {items.Count} relevant unfinished todo(s).", new JsonObject { ["items"] = items });
        }

        private async Task<ToolResult> WriteTodoAsync(string value, string category, string notify, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(notify))
            {
                try { _ = CronExpression.Parse(notify, CronFormat.Standard); }
                catch (CronFormatException exception) { throw new FormatException($"notify is not a valid five-field cron expression: {exception.Message}"); }
            }
            var todo = _todos.Create(value, category, "secretary", notify);
            try { await new TodoSearchService().RefreshEmbeddingAsync(todo, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Todo embedding failed: {exception.Message}"); }
            return Ok("Saved the todo.", new JsonObject { ["item"] = TodoJson(todo) });
        }

        private async Task<ToolResult> GetDataAsync(string kind, CancellationToken token)
        {
            switch (kind.Trim().ToLowerInvariant())
            {
                case "local_datetime":
                    var now = DateTimeOffset.Now;
                    return Ok("Returned the local date and time.", new JsonObject
                    {
                        ["value"] = now.ToString("O"), ["timezone"] = TimeZoneInfo.Local.Id
                    });
                case "location":
                    return ConfiguredLocation();
                case "weather":
                    var location = LocationText();
                    if (location is null) return MissingLocation();
                    return await _weather.GetWeatherAsync(location, null, null, false, token);
                case "clipboard":
                    return await ClipboardTextAsync();
                case "language":
                    return Ok("Returned the current language.", new JsonObject
                    {
                        ["value"] = ApplicationLanguages.Languages.FirstOrDefault() ?? CultureInfo.CurrentUICulture.Name
                    });
                case "battery_percentage":
                    return BatteryPercentage();
                default:
                    return Error("unsupported_kind", "kind must be local_datetime, weather, location, clipboard, language, or battery_percentage.");
            }
        }

        private static ToolResult ConfiguredLocation()
        {
            var location = LocationText();
            return location is null
                ? MissingLocation()
                : Ok("Returned the configured location.", new JsonObject
                {
                    ["value"] = location,
                    ["city"] = App.Settings.Current.City.Trim(),
                    ["country"] = App.Settings.Current.Country.Trim()
                });
        }

        private static string? LocationText()
        {
            var city = App.Settings.Current.City.Trim();
            var country = App.Settings.Current.Country.Trim();
            return city.Length == 0 || country.Length == 0 ? null : $"{city}, {country}";
        }

        private static ToolResult MissingLocation() =>
            Error("location_not_configured", "Set both Secretary city and country in Settings first.");

        private static async Task<ToolResult> ClipboardTextAsync()
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return Error("clipboard_text_unavailable", "The clipboard does not contain text.");
            var text = await content.GetTextAsync();
            return string.IsNullOrEmpty(text)
                ? Error("clipboard_text_unavailable", "The clipboard text is empty.")
                : Ok("Returned clipboard text.", new JsonObject { ["value"] = text });
        }

        private static ToolResult BatteryPercentage()
        {
            if (!GetSystemPowerStatus(out var status) || status.BatteryFlag == 128 || status.BatteryLifePercent == byte.MaxValue)
                return Error("battery_unavailable", "This device does not report a battery percentage.");
            return Ok("Returned battery percentage.", new JsonObject { ["value"] = status.BatteryLifePercent.ToString(CultureInfo.InvariantCulture) });
        }

        private static JsonObject TodoJson(TodoDocument item) => new()
        {
            ["key"] = item.Id,
            ["value"] = item.Value,
            ["category"] = item.Category,
            ["is_done"] = item.IsDone,
            ["notify"] = item.Notify,
            ["created_utc"] = item.CreatedAt.ToString("O")
        };

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
        private static JsonObject MemoTopic() => new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("personal", "career", "knowledge", "opinion", "idea", "relationship", "guide", "milestone")
        };
        internal static JsonObject DataKindSchema() => new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("local_datetime", "weather", "location", "clipboard", "language", "battery_percentage")
        };

        private static string RequiredString(JsonObject arguments, string name)
        {
            var value = OptionalString(arguments, name);
            return value.Length == 0 ? throw new FormatException($"{name} is required.") : value;
        }

        private static string OptionalString(JsonObject arguments, string name)
        {
            try { return arguments[name]?.GetValue<string>()?.Trim() ?? string.Empty; }
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

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }


        public void Dispose() => _weather.Dispose();
    }
}
