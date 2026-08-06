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
        private readonly NotebookDatabaseService _notebook = new();
        private readonly TodoRepository _todos = new();
        private readonly WeatherService _weather = new();

        public SecretaryToolService(SecretaryMemoryService memory) => _memory = memory;

        public static JsonArray CreateDeclarations() => new()
        {
            Function("search_memory", "Search the user's saved notes, memos, and todos. Use first for anything personal.", Props(("search_keyword", String("One keyword or short phrase."))), "search_keyword"),
            Function("save_note", "Save a note. Only when the user asks to keep it.", Props(("note_text", String("Exact text to save."))), "note_text"),
            Function("save_memo", "Remember one lasting fact about the user, only when they ask. Never a secret, password, or guess.", Props(("memo_text", String("Exact detail to remember."))), "memo_text"),
            Function("remove_memo", "Delete one saved memo. Get its key from search_memory first.", Props(("memo_key", String("Memo key from search_memory."))), "memo_key"),
            Function("list_todo_categories", "List todo categories. Call before save_todo.", new JsonObject()),
            Function("save_todo", "Save a task the user asks to keep. Reuse a category from list_todo_categories; default is General.", Props(("todo_text", String("Task text.")), ("category_name", String("Existing category, or a short new one.")), ("notification_cron", String("Optional reminder, 5-field cron, local time."))), "todo_text"),
            Function("get_local_context", "Get one live fact about this device: time, location, weather, clipboard, language, or battery.", Props(("context_type", DataKindSchema())), "context_type")
        };

        public static JsonArray CreateReadOnlyDeclarations() => new()
        {
            Function("search_memory", "Search the user's saved notes, memos, and todos. Use when the answer may depend on what they saved.", Props(("search_keyword", String("One keyword or short phrase."))), "search_keyword"),
            Function("get_local_context", "Get one live fact about this device: time, location, weather, clipboard, language, or battery.", Props(("context_type", DataKindSchema())), "context_type")
        };

        public async Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
        {
            try
            {
                return name switch
                {
                    "search_memory" => await SearchLocalKnowledgeAsync(RequiredString(arguments, "search_keyword"), token),
                    "save_note" => await SaveNoteAsync(RequiredString(arguments, "note_text"), token),
                    "save_memo" => WriteMemo(RequiredString(arguments, "memo_text")),
                    "remove_memo" => DeleteMemo(RequiredString(arguments, "memo_key")),
                    "list_todo_categories" => ListTodoCategories(),
                    "save_todo" => WriteTodo(RequiredString(arguments, "todo_text"), OptionalString(arguments, "category_name") is { Length: > 0 } category ? category : "General", OptionalString(arguments, "notification_cron")),
                    "get_local_context" => await GetDataAsync(RequiredString(arguments, "context_type"), token),
                    _ => Error("unknown_tool", $"Secretary cannot use the tool “{name}”.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (FormatException exception) { return Error("invalid_arguments", exception.Message); }
            catch (ArgumentException exception) { return Error("invalid_arguments", exception.Message); }
            catch (Exception) { return Error("operation_failed", "Secretary could not complete that local operation."); }
        }

        private async Task<ToolResult> SaveNoteAsync(string content, CancellationToken token)
        {
            var note = await _notebook.CreateAsync(content, token);
            return Ok("Saved the note.", new JsonObject { ["key"] = note.Key });
        }

        private async Task<ToolResult> SearchLocalKnowledgeAsync(string keyword, CancellationToken token)
        {
            using var client = new OpenAiCompatibleClient(App.Settings.Current.OpenAiCompatibleApiKey);
            var patterns = await KeywordSearchService.CreatePatternsAsync(client, keyword, token);
            var matches = new List<(int Matches, DateTime Timestamp, JsonObject Item)>();
            foreach (var note in _memory.ListNotes())
            {
                AddKeywordMatch(matches, note.Timestamp, note.Value, new JsonObject
                {
                    ["source"] = "note",
                    ["key"] = note.Id,
                    ["value"] = note.Value,
                    ["written_utc"] = note.Timestamp.ToString("O")
                }, patterns);
            }

            foreach (var memo in _memory.ListMemos())
            {
                AddKeywordMatch(matches, memo.Timestamp, memo.Value, new JsonObject
                {
                    ["source"] = "memo",
                    ["key"] = memo.Id,
                    ["value"] = memo.Value,
                    ["written_utc"] = memo.Timestamp.ToString("O")
                }, patterns);
            }

            foreach (var todo in _todos.List())
            {
                var value = TodoJson(todo);
                value.Insert(0, "source", "todo");
                AddKeywordMatch(matches, todo.CreatedAt, $"{todo.Category}\n{todo.Value}", value, patterns);
            }

            var items = new JsonArray(matches
                .OrderByDescending(match => match.Matches)
                .ThenByDescending(match => match.Timestamp)
                .Take(MaximumResults)
                .Select(match => (JsonNode)match.Item)
                .ToArray());
            return Ok($"Found {items.Count} saved result(s).", new JsonObject { ["items"] = items });
        }

        private static void AddKeywordMatch(
            List<(int Matches, DateTime Timestamp, JsonObject Item)> matches,
            DateTime timestamp,
            string text,
            JsonObject item,
            IReadOnlyList<Regex> patterns)
        {
            var count = KeywordSearchService.CountMatches(patterns, text);
            if (count == 0) return;
            matches.Add((count, timestamp, item));
        }

        private ToolResult WriteMemo(string value)
        {
            var memo = _memory.WriteMemo(value);
            return Ok("Saved the memo.", new JsonObject { ["key"] = memo.Id });
        }

        private ToolResult DeleteMemo(string key) =>
            _memory.DeleteMemo(key)
                ? Ok("Deleted the memo.", new JsonObject { ["key"] = key })
                : Error("memo_not_found", "No memo with that key was found.");

        private ToolResult ListTodoCategories()
        {
            var categories = new JsonArray(_todos.ListCategories()
                .Select(category => (JsonNode)new JsonObject
                {
                    ["name"] = category.Id,
                    ["description"] = category.Description
                })
                .ToArray());
            return Ok("Returned the available todo categories.", new JsonObject { ["categories"] = categories });
        }

        private ToolResult WriteTodo(string value, string category, string notify)
        {
            if (!string.IsNullOrWhiteSpace(notify))
            {
                try { _ = CronExpression.Parse(notify, CronFormat.Standard); }
                catch (CronFormatException exception) { throw new FormatException($"notify is not a valid five-field cron expression: {exception.Message}"); }
            }
            var todo = _todos.Create(value, category, "secretary", notify);
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

        private static JsonObject String(string? description = null)
        {
            var schema = new JsonObject { ["type"] = "string" };
            if (description is not null) schema["description"] = description;
            return schema;
        }

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
