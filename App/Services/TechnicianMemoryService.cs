using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App.Models;

namespace App.Services
{
    internal sealed class TechnicianMemoryService
    {
        private const int Capacity = 1000;
        private readonly GeminiClient _client;
        private readonly Dictionary<string, TechnicianMemo> _items = new(StringComparer.Ordinal);

        public TechnicianMemoryService(GeminiClient client) => _client = client;

        public async Task<IReadOnlyList<TechnicianMemo>> ReadAsync(string query, int maximum, CancellationToken token)
        {
            var embedding = await _client.EmbedRetrievalQueryAsync(query, token);
            var matches = _items.Values
                .Select(item => (Item: item, Score: NotebookDatabaseService.Cosine(embedding, item.Embedding)))
                .OrderByDescending(item => item.Score)
                .Take(Math.Clamp(maximum, 1, 20))
                .Select(item => item.Item)
                .ToList();
            foreach (var item in matches) item.LastAccessedUtc = DateTime.UtcNow;
            return matches;
        }

        public async Task<TechnicianMemo> WriteAsync(string value, CancellationToken token)
        {
            var embedding = await _client.EmbedRetrievalDocumentAsync("Technician memory", value, token);
            if (_items.Count >= Capacity)
            {
                var oldest = _items.Values.OrderBy(item => item.LastAccessedUtc).First();
                _items.Remove(oldest.Key);
            }

            var memo = new TechnicianMemo(Guid.NewGuid().ToString("D"), value.Trim(), embedding, DateTime.UtcNow);
            _items[memo.Key] = memo;
            return memo;
        }

        public IReadOnlyList<TechnicianMemo> List() => _items.Values.OrderByDescending(item => item.LastAccessedUtc).ToList();
        public void Clear() => _items.Clear();
    }

    internal sealed class TechnicianMemo
    {
        public TechnicianMemo(string key, string value, float[] embedding, DateTime lastAccessedUtc)
        {
            Key = key;
            Value = value;
            Embedding = embedding;
            LastAccessedUtc = lastAccessedUtc;
        }

        public string Key { get; }
        public string Value { get; }
        public float[] Embedding { get; }
        public DateTime LastAccessedUtc { get; set; }
    }
}
