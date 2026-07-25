using App.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class NotebookDatabaseService
    {
        private const double MinimumSearchSimilarity = 0.45;
        private const double MaximumSimilarityDropFromBest = 0.12;

        public NotebookDatabaseService() { }

        private LiteDatabaseService Database => App.Settings.Database;
        public string RootPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CrsterUtility", "Attachments");

        public Task<List<NotebookEntry>> LoadAsync() =>
            Task.FromResult(Database.Notes.Query().OrderByDescending(item => item.Timestamp).ToList().Select(ToEntry).ToList());

        public async Task<List<NotebookSearchResult>> SearchAsync(string query, int maximumResults = 10, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return [];
            using var gemini = new GeminiClient(App.Settings.Current.GeminiApiKey);
            var embedding = await gemini.EmbedRetrievalQueryAsync(query, cancellationToken);
            var ranked = Database.Notes.FindAll()
                .Where(item => item.Embedding.Length > 0)
                .Select(item => (Item: item, Score: Cosine(embedding, BytesToFloats(item.Embedding))))
                .Where(item => item.Score >= MinimumSearchSimilarity)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Item.Timestamp)
                .ToList();
            if (ranked.Count == 0) return [];

            var relativeCutoff = ranked[0].Score - MaximumSimilarityDropFromBest;
            return ranked
                .Where(item => item.Score >= relativeCutoff)
                .Take(maximumResults)
                .Select(item => new NotebookSearchResult
                {
                    EntryKey = item.Item.Id,
                    Title = NotebookFormat.GetTitle(item.Item.Value) ?? "Note",
                    Details = Preview(item.Item.Value)
                }).ToList();
        }

        public List<NotebookSearchResult> FuzzySearch(string query, int maximumResults = 10)
        {
            var queryTerms = Terms(query);
            if (queryTerms.Count == 0) return [];

            return Database.Notes.FindAll()
                .Select(item => (Item: item, Score: FuzzyScore(queryTerms, Terms(NotebookFormat.CreateSearchText(item.Value)))))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Item.Timestamp)
                .Take(maximumResults)
                .Select(item => new NotebookSearchResult
                {
                    EntryKey = item.Item.Id,
                    Title = NotebookFormat.GetTitle(item.Item.Value) ?? "Note",
                    Details = Preview(item.Item.Value)
                }).ToList();
        }

        public Task<NotebookEntry?> GetEntryAsync(string entryKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = Database.Notes.FindById(entryKey);
            return Task.FromResult(document is null ? null : ToEntry(document));
        }

        public async Task SaveAsync(IEnumerable<NotebookEntry> entries)
        {
            var incoming = entries.ToList();
            var existing = Database.Notes.FindAll().ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var prepared = new List<NoteDocument>();
            using var gemini = new GeminiClient(App.Settings.Current.GeminiApiKey);
            try
            {
                foreach (var entry in incoming)
                {
                    var attachmentIds = NotebookFormat.ExtractAttachmentIds(entry.Content);
                    if (existing.TryGetValue(entry.Key, out var stored) &&
                        string.Equals(stored.Value, entry.Content, StringComparison.Ordinal) &&
                        stored.Attachments.SequenceEqual(attachmentIds))
                    {
                        prepared.Add(stored);
                        continue;
                    }

                    var embedding = await gemini.EmbedNoteAsync(entry.Content, CancellationToken.None);
                    prepared.Add(new NoteDocument
                    {
                        Id = entry.Key,
                        Value = entry.Content,
                        Attachments = attachmentIds,
                        Embedding = FloatsToBytes(embedding),
                        Timestamp = DateTime.UtcNow
                    });
                }
            }
            catch { DeleteOrphanedAttachments(); throw; }

            Database.Database.BeginTrans();
            try
            {
                var incomingKeys = prepared.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var deleted in existing.Keys.Where(key => !incomingKeys.Contains(key))) Database.Notes.Delete(deleted);
                foreach (var entry in prepared) Database.Notes.Upsert(entry);
                DeleteOrphanedAttachments();
                Database.Database.Commit();
            }
            catch
            {
                Database.Database.Rollback();
                throw;
            }
        }

        private void DeleteOrphanedAttachments()
        {
            var referenced = Database.Notes.FindAll().SelectMany(item => item.Attachments)
                .Concat(Database.Memos.FindAll().SelectMany(item => item.Attachments))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var attachment in Database.Attachments.FindAll())
                if (!referenced.Contains(attachment.Id)) Database.Attachments.Delete(attachment.Id);
        }

        private static string Preview(string content)
        {
            var value = NotebookFormat.CreateSearchText(content);
            if (string.IsNullOrWhiteSpace(value)) return "Note";
            return value.Length <= 120 ? value : $"{value[..117]}...";
        }

        private static NotebookEntry ToEntry(NoteDocument document) => new()
        {
            Key = document.Id,
            Type = "note",
            Content = document.Value,
            Embedding = document.Embedding,
            Attachments = [.. document.Attachments],
            Timestamp = document.Timestamp
        };

        internal static byte[] FloatsToBytes(float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        internal static float[] BytesToFloats(byte[] values) => MemoryMarshal.Cast<byte, float>(values.AsSpan()).ToArray();

        internal static double Cosine(float[] left, float[] right)
        {
            if (left.Length == 0 || left.Length != right.Length) return 0;
            double dot = 0, leftMagnitude = 0, rightMagnitude = 0;
            for (var index = 0; index < left.Length; index++)
            {
                dot += left[index] * right[index];
                leftMagnitude += left[index] * left[index];
                rightMagnitude += right[index] * right[index];
            }
            return leftMagnitude == 0 || rightMagnitude == 0 ? 0 : dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
        }

        private static double FuzzyScore(IReadOnlyList<string> queryTerms, IReadOnlyList<string> documentTerms)
        {
            if (documentTerms.Count == 0) return 0;
            double total = 0;
            foreach (var queryTerm in queryTerms)
            {
                var best = documentTerms.Max(documentTerm => TermSimilarity(queryTerm, documentTerm));
                if (best < 0.7) return 0;
                total += best;
            }
            return total / queryTerms.Count;
        }

        private static double TermSimilarity(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) return 1;
            if (left.Length >= 3 && right.StartsWith(left, StringComparison.Ordinal) ||
                right.Length >= 3 && left.StartsWith(right, StringComparison.Ordinal)) return 0.9;
            var maximumLength = Math.Max(left.Length, right.Length);
            return maximumLength == 0 ? 1 : 1d - (double)LevenshteinDistance(left, right) / maximumLength;
        }

        private static int LevenshteinDistance(string left, string right)
        {
            var previous = Enumerable.Range(0, right.Length + 1).ToArray();
            var current = new int[right.Length + 1];
            for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
            {
                current[0] = leftIndex;
                for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
                    current[rightIndex] = Math.Min(
                        Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                        previous[rightIndex - 1] + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1));
                (previous, current) = (current, previous);
            }
            return previous[right.Length];
        }

        private static List<string> Terms(string value) =>
            value.ToLowerInvariant()
                .Split([' ', '\r', '\n', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(term => term.Length > 1)
                .Distinct(StringComparer.Ordinal)
                .ToList();
    }
}
