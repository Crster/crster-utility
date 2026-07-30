using App.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class SecretaryMemoryService
    {
        private static readonly HashSet<string> AllowedTopics = new(StringComparer.OrdinalIgnoreCase)
        {
            "personal", "career", "knowledge", "opinion", "idea", "relationship", "guide", "milestone"
        };
        private readonly QwenClient _client;

        public SecretaryMemoryService(QwenClient client) => _client = client;

        public IReadOnlyList<NoteDocument> FindNotes(string query, int maximum = 20)
        {
            query = Required(query, nameof(query));
            return App.Settings.Database.Notes.FindAll()
                .Where(item => item.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Timestamp)
                .Take(maximum)
                .ToList();
        }

        public IReadOnlyList<NoteDocument> ListNotes() =>
            App.Settings.Database.Notes.FindAll().ToList();

        public IReadOnlyList<MemoDocument> ListMemos() =>
            App.Settings.Database.Memos.FindAll().ToList();

        public async Task<IReadOnlyList<MemoDocument>> FindMemosAsync(string topic, string query, int maximum, CancellationToken token)
        {
            topic = string.IsNullOrWhiteSpace(topic) ? string.Empty : NormalizeTopic(topic);
            query = query.Trim();
            var memos = App.Settings.Database.Memos.FindAll()
                .Where(item => topic.Length == 0 || string.Equals(item.Topic, topic, StringComparison.OrdinalIgnoreCase));

            if (query.Length == 0)
                return memos.OrderByDescending(item => item.Timestamp).Take(maximum).ToList();

            var embedding = await _client.EmbedRetrievalQueryAsync(query, token);
            return memos
                .Where(item => item.Embedding.Length > 0)
                .Select(item => (Item: item, Score: NotebookDatabaseService.Cosine(embedding, NotebookDatabaseService.BytesToFloats(item.Embedding))))
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Item.Timestamp)
                .Take(maximum)
                .Select(item => item.Item)
                .ToList();
        }

        public async Task<MemoDocument> WriteMemoAsync(string topic, string value, CancellationToken token)
        {
            topic = NormalizeTopic(topic);
            value = Required(value, nameof(value));
            var embedding = await _client.EmbedRetrievalDocumentAsync(topic, value, token);
            var memo = new MemoDocument
            {
                Topic = topic,
                Value = value,
                Embedding = NotebookDatabaseService.FloatsToBytes(embedding),
                Timestamp = DateTime.UtcNow
            };
            App.Settings.Database.Memos.Insert(memo);
            return memo;
        }

        public bool DeleteMemo(string key)
        {
            key = Required(key, nameof(key));
            return App.Settings.Database.Memos.Delete(key);
        }

        private static string NormalizeTopic(string topic)
        {
            topic = Required(topic, nameof(topic)).ToLowerInvariant();
            if (!AllowedTopics.Contains(topic))
                throw new FormatException($"topic must be one of: {string.Join(", ", AllowedTopics.Order())}.");
            return topic;
        }

        private static string Required(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new FormatException($"{name} is required.") : value.Trim();
    }
}
