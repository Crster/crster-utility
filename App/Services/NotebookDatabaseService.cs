using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using App.Models;
using Build5Nines.SharpVector;
using Windows.Storage;

namespace App.Services
{
    internal sealed class NotebookDatabaseService
    {
        private readonly string _documentPath;
        private readonly string _indexPath;
        private BasicMemoryVectorDatabase _vectorDatabase = new();

        public NotebookDatabaseService()
        {
            RootPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Notebook");
            Directory.CreateDirectory(RootPath);
            _documentPath = Path.Combine(RootPath, "notebook.json");
            _indexPath = Path.Combine(RootPath, "notebook-index.b59vdb");
        }

        public string RootPath { get; }

        public async Task<List<NotebookEntry>> LoadAsync()
        {
            await StartFreshIfLegacyAsync();
            if (!File.Exists(_documentPath)) return [];

            await using var stream = File.OpenRead(_documentPath);
            var entries = await JsonSerializer.DeserializeAsync<List<NotebookEntry>>(stream) ?? [];
            return entries.OrderByDescending(entry => entry.Index).ToList();
        }

        public async Task SaveAsync(IEnumerable<NotebookEntry> entries)
        {
            var orderedEntries = entries.OrderByDescending(entry => entry.Index).ToList();
            var temporaryPath = $"{_documentPath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, orderedEntries, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporaryPath, _documentPath, true);

            _vectorDatabase = new BasicMemoryVectorDatabase();
            foreach (var entry in orderedEntries.Where(entry => entry.Type != "password" && !string.IsNullOrWhiteSpace(entry.Content)))
                _vectorDatabase.AddText(entry.Content, entry.Index.ToString());
            await _vectorDatabase.SaveToFileAsync(_indexPath);
        }

        private async Task StartFreshIfLegacyAsync()
        {
            if (!File.Exists(_documentPath)) return;

            await using var stream = File.OpenRead(_documentPath);
            using var document = await JsonDocument.ParseAsync(stream);
            var isCurrentFormat = document.RootElement.ValueKind == JsonValueKind.Array &&
                                  document.RootElement.EnumerateArray().All(item =>
                                      item.TryGetProperty("type", out _) &&
                                      item.TryGetProperty("content", out _) &&
                                      item.TryGetProperty("index", out _));
            if (isCurrentFormat) return;

            var backupPath = Path.Combine(RootPath, $"notebook.legacy-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(_documentPath, backupPath, true);
            if (File.Exists(_indexPath)) File.Delete(_indexPath);
        }
    }
}
