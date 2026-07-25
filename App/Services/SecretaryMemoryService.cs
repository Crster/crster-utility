using App.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class SecretaryMemoryService : IDisposable
    {
        private static readonly HashSet<string> AllowedTopics = new(StringComparer.OrdinalIgnoreCase)
        {
            "personal", "career", "knowledge", "opinion", "idea", "relationship", "guide", "milestone"
        };
        private readonly GeminiClient _client;

        public SecretaryMemoryService(GeminiClient client, string? rootPath = null)
        {
            _client = client;
        }

        public async Task<string> BuildPersonalInfoContextAsync(string topic, CancellationToken token)
        {
            var matches = await SearchAsync(topic, 12, token);
            return string.Join("\n", matches.Select(item => $"- [{item.Topic}] {item.Value} ({item.Timestamp:O})"));
        }

        public async Task<ToolResult> ListPersonalInfoAsync(string topic, CancellationToken token)
        {
            var matches = await SearchAsync(topic, 20, token);
            var items = new JsonArray();
            foreach (var item in matches)
                items.Add(new JsonObject
                {
                    ["key"] = item.Key, ["topic"] = item.Topic, ["knowledge"] = item.Value,
                    ["written_utc"] = item.Timestamp.ToString("O")
                });
            return Result(true, $"Found {items.Count} memo(s).", new JsonObject { ["items"] = items });
        }

        public async Task<ToolResult> WritePersonalInfoAsync(string topic, string newKnowledge, CancellationToken token)
        {
            topic = topic.Trim().ToLowerInvariant();
            if (!AllowedTopics.Contains(topic))
                return Result(false, $"Memo topic must be one of: {string.Join(", ", AllowedTopics.Order())}.");
            var embedding = await _client.EmbedRetrievalDocumentAsync(topic, newKnowledge, token);
            var memo = new MemoDocument
            {
                Topic = topic,
                Value = newKnowledge.Trim(),
                Embedding = NotebookDatabaseService.FloatsToBytes(embedding),
                Timestamp = DateTime.UtcNow
            };
            App.Settings.Database.Memos.Insert(memo);
            return Result(true, "Saved the memo.", new JsonObject { ["key"] = memo.Key, ["topic"] = memo.Topic });
        }

        public Task<ToolResult> ClearHistoryAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Result(true, "Started a new in-memory Secretary session."));
        }

        public async Task<string?> GetRememberedWeatherLocationAsync(CancellationToken token)
        {
            var matches = await SearchAsync("default weather location city", 5, token);
            return matches.FirstOrDefault(item => item.Topic == "personal")?.Value;
        }

        private async Task<List<MemoDocument>> SearchAsync(string query, int maximum, CancellationToken token)
        {
            var embedding = await _client.EmbedRetrievalQueryAsync(query, token);
            return App.Settings.Database.Memos.FindAll()
                .Where(item => item.Embedding.Length > 0)
                .Select(item => (Item: item, Score: NotebookDatabaseService.Cosine(embedding, NotebookDatabaseService.BytesToFloats(item.Embedding))))
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Item.Timestamp)
                .Take(maximum)
                .Select(item => item.Item).ToList();
        }

        private static ToolResult Result(bool success, string summary, JsonObject? details = null)
        {
            var root = details ?? new JsonObject();
            root.Insert(0, "summary", summary);
            root.Insert(0, "status", success ? "completed" : "failed");
            return new ToolResult(success, root.ToJsonString());
        }

        public void Dispose() { }
    }
}
