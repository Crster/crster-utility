using App.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class TodoSearchService
    {
        private const double MinimumSearchSimilarity = 0.45;
        private const double MaximumSimilarityDropFromBest = 0.12;
        private LiteDatabaseService Database => App.Settings.Database;

        public List<TodoSearchResult> FuzzySearch(string query, int maximumResults = 10)
        {
            var terms = SearchTerms(query);
            if (terms.Count == 0) return [];

            return Database.Todos.FindAll()
                .Where(todo => terms.All(term =>
                    todo.Value.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    todo.Category.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(todo => todo.IsDone)
                .ThenByDescending(todo => todo.CreatedAt)
                .Take(maximumResults)
                .Select(ToResult)
                .ToList();
        }

        public async Task<List<TodoSearchResult>> SearchAsync(string query, int maximumResults = 10, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return [];
            var todos = Database.Todos.Find(todo => !todo.IsDone).ToList();
            if (todos.Count == 0) return [];

            using var openAiCompatible = new OpenAiCompatibleClient(App.Settings.Current.OpenAiCompatibleApiKey);
            foreach (var todo in todos.Where(todo => todo.Embedding.Length == 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var embedding = await openAiCompatible.EmbedRetrievalDocumentAsync(todo.Category, todo.Value, cancellationToken);
                todo.Embedding = NotebookDatabaseService.FloatsToBytes(embedding);
                Database.Todos.Update(todo);
            }

            var queryEmbedding = await openAiCompatible.EmbedRetrievalQueryAsync(query, cancellationToken);
            var ranked = todos
                .Where(todo => todo.Embedding.Length > 0)
                .Select(todo => (Todo: todo, Score: NotebookDatabaseService.Cosine(
                    queryEmbedding, NotebookDatabaseService.BytesToFloats(todo.Embedding))))
                .Where(match => match.Score >= MinimumSearchSimilarity)
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.Todo.CreatedAt)
                .ToList();
            if (ranked.Count == 0) return [];

            // Keep only the results clustered with the best match so a weak query cannot fill the list.
            var relativeCutoff = ranked[0].Score - MaximumSimilarityDropFromBest;
            return ranked
                .Where(match => match.Score >= relativeCutoff)
                .Take(maximumResults)
                .Select(match => ToResult(match.Todo))
                .ToList();
        }

        public async Task RefreshEmbeddingAsync(TodoDocument todo, CancellationToken cancellationToken = default)
        {
            using var openAiCompatible = new OpenAiCompatibleClient(App.Settings.Current.OpenAiCompatibleApiKey);
            var embedding = await openAiCompatible.EmbedRetrievalDocumentAsync(todo.Category, todo.Value, cancellationToken);
            todo.Embedding = NotebookDatabaseService.FloatsToBytes(embedding);
            Database.Todos.Update(todo);
        }

        private static TodoSearchResult ToResult(TodoDocument todo) => new()
        {
            TodoId = todo.Id,
            Title = todo.Value,
            Details = $"{todo.Category} · {(todo.IsDone ? "Done" : "Todo")}"
        };

        private static List<string> SearchTerms(string query) =>
            query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
