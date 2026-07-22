using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using App.Models;
using Windows.Storage;

namespace App.Services
{
    internal sealed class NotebookDatabaseService
    {
        private readonly string _documentPath;
        private readonly string _indexPath;
        private static readonly object SearchIndexLock = new();
        private static readonly TimeSpan SearchIndexIdleTimeout = TimeSpan.FromMinutes(3);
        private static NotebookSearchIndex? _searchIndex;
        private static Task<NotebookSearchIndex>? _searchIndexTask;
        private static Timer? _searchIndexEvictionTimer;

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
            return await LoadEntriesFromDiskAsync();
        }

        public async Task<List<NotebookSearchResult>> SearchAsync(string query, int maximumResults = 10, CancellationToken cancellationToken = default)
        {
            var terms = ExtractSearchTerms(query);
            if (terms.Count == 0) return [];

            var searchIndex = await GetSearchIndexAsync();
            TouchSearchIndex();
            return await Task.Run(() => searchIndex.Search(terms, maximumResults, cancellationToken), cancellationToken);
        }

        public async Task SaveAsync(IEnumerable<NotebookEntry> entries)
        {
            var orderedEntries = entries.OrderByDescending(entry => entry.Index).ToList();
            var temporaryPath = $"{_documentPath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, new NotebookDocument { Entries = orderedEntries }, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporaryPath, _documentPath, true);

            bool shouldRefreshSearchIndex;
            lock (SearchIndexLock) shouldRefreshSearchIndex = _searchIndex is not null;
            if (shouldRefreshSearchIndex)
            {
                var searchIndex = await Task.Run(() => NotebookSearchIndex.Create(orderedEntries), CancellationToken.None);
                SetSearchIndex(searchIndex);
            }
        }

        private Task<NotebookSearchIndex> GetSearchIndexAsync()
        {
            lock (SearchIndexLock)
            {
                if (_searchIndex is not null) return Task.FromResult(_searchIndex);
                return _searchIndexTask ??= CreateSearchIndexAsync();
            }
        }

        private async Task<NotebookSearchIndex> CreateSearchIndexAsync()
        {
            var entries = await LoadEntriesFromDiskAsync();
            var searchIndex = await Task.Run(() => NotebookSearchIndex.Create(entries));
            SetSearchIndex(searchIndex);
            return searchIndex;
        }

        private async Task<List<NotebookEntry>> LoadEntriesFromDiskAsync()
        {
            if (!File.Exists(_documentPath)) return [];

            var entries = await Task.Run(async () =>
            {
                await using var stream = File.OpenRead(_documentPath);
                return (await JsonSerializer.DeserializeAsync<NotebookDocument>(stream))?.Entries ?? [];
            });
            foreach (var entry in entries) entry.Type = "note";
            return entries.OrderByDescending(entry => entry.Index).ToList();
        }

        private static void SetSearchIndex(NotebookSearchIndex searchIndex)
        {
            lock (SearchIndexLock)
            {
                _searchIndex = searchIndex;
                _searchIndexTask = Task.FromResult(searchIndex);
                _searchIndexEvictionTimer ??= new Timer(_ => ReleaseIdleSearchIndex());
                _searchIndexEvictionTimer.Change(SearchIndexIdleTimeout, Timeout.InfiniteTimeSpan);
            }
        }

        private static void TouchSearchIndex()
        {
            lock (SearchIndexLock)
                _searchIndexEvictionTimer?.Change(SearchIndexIdleTimeout, Timeout.InfiniteTimeSpan);
        }

        private static void ReleaseIdleSearchIndex()
        {
            lock (SearchIndexLock)
            {
                _searchIndex = null;
                _searchIndexTask = null;
                _searchIndexEvictionTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        private static string CreateSearchPreview(NotebookEntry entry)
        {
            var content = NotebookFormat.CreateSearchText(entry.Content);
            if (string.IsNullOrWhiteSpace(content)) content = "Note";
            return content.Length <= 120 ? content : $"{content[..117]}...";
        }

        private static NotebookEntry CloneEntry(NotebookEntry entry) => new()
        {
            Type = entry.Type,
            Content = entry.Content,
            Index = entry.Index
        };

        private static List<string> ExtractSearchTerms(string text)
        {
            var terms = new List<string>();
            var token = new System.Text.StringBuilder();
            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character)) token.Append(char.ToLowerInvariant(character));
                else AddSearchTerm(token, terms);
            }

            AddSearchTerm(token, terms);
            return terms;
        }

        private static void AddSearchTerm(System.Text.StringBuilder token, List<string> terms)
        {
            if (token.Length > 1 && token.ToString() is not "a" and not "an" and not "the" and not "of") terms.Add(token.ToString());
            token.Clear();
        }

        private sealed class NotebookSearchIndex
        {
            private readonly Dictionary<string, List<NotebookEntry>> _entriesByTerm;

            private NotebookSearchIndex(List<NotebookEntry> entries, Dictionary<string, List<NotebookEntry>> entriesByTerm)
            {
                Entries = entries;
                _entriesByTerm = entriesByTerm;
            }

            public static NotebookSearchIndex Empty { get; } = new([], new(StringComparer.Ordinal));

            public List<NotebookEntry> Entries { get; }

            public static NotebookSearchIndex Create(IEnumerable<NotebookEntry> entries)
            {
                var orderedEntries = entries.OrderByDescending(entry => entry.Index).Select(CloneEntry).ToList();
                var entriesByTerm = new Dictionary<string, List<NotebookEntry>>(StringComparer.Ordinal);
                foreach (var entry in orderedEntries)
                {
                    foreach (var term in ExtractSearchTerms(NotebookFormat.CreateSearchText(entry.Content)).Distinct(StringComparer.Ordinal))
                    {
                        if (!entriesByTerm.TryGetValue(term, out var matchingEntries))
                        {
                            matchingEntries = [];
                            entriesByTerm.Add(term, matchingEntries);
                        }

                        matchingEntries.Add(entry);
                    }
                }

                return new NotebookSearchIndex(orderedEntries, entriesByTerm);
            }

            public List<NotebookSearchResult> Search(IReadOnlyList<string> terms, int maximumResults, CancellationToken cancellationToken)
            {
                var entryLists = new List<List<NotebookEntry>>(terms.Count);
                foreach (var term in terms.Distinct(StringComparer.Ordinal))
                {
                    if (!_entriesByTerm.TryGetValue(term, out var matchingEntries)) return [];
                    entryLists.Add(matchingEntries);
                }

                var candidates = entryLists.OrderBy(entries => entries.Count).First();
                var results = new List<NotebookSearchResult>(Math.Min(maximumResults, candidates.Count));
                foreach (var entry in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entryLists.Any(entries => !ContainsEntryIndex(entries, entry.Index))) continue;

                    results.Add(new NotebookSearchResult
                    {
                        EntryIndex = entry.Index,
                        Title = NotebookFormat.GetTitle(entry.Content) ?? "Note",
                        Details = CreateSearchPreview(entry)
                    });
                    if (results.Count == maximumResults) break;
                }

                return results;
            }

            private static bool ContainsEntryIndex(List<NotebookEntry> entries, int index)
            {
                var low = 0;
                var high = entries.Count - 1;
                while (low <= high)
                {
                    var middle = low + (high - low) / 2;
                    var candidateIndex = entries[middle].Index;
                    if (candidateIndex == index) return true;
                    if (candidateIndex < index) high = middle - 1;
                    else low = middle + 1;
                }

                return false;
            }
        }
    }
}
