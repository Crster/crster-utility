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

            using var qwen = new QwenClient(App.Settings.Current.QwenApiKey);
            foreach (var todo in todos.Where(todo => todo.Embedding.Length == 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var embedding = await qwen.EmbedRetrievalDocumentAsync(todo.Category, todo.Value, cancellationToken);
                todo.Embedding = NotebookDatabaseService.FloatsToBytes(embedding);
                Database.Todos.Update(todo);
            }

            var queryEmbedding = await qwen.EmbedRetrievalQueryAsync(query, cancellationToken);
            return todos
                .Where(todo => todo.Embedding.Length > 0)
                .Select(todo => (Todo: todo, Score: NotebookDatabaseService.Cosine(
                    queryEmbedding, NotebookDatabaseService.BytesToFloats(todo.Embedding))))
                .Where(match => match.Score >= MinimumSearchSimilarity)
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.Todo.CreatedAt)
                .Take(maximumResults)
                .Select(match => ToResult(match.Todo))
                .ToList();
        }

        public async Task RefreshEmbeddingAsync(TodoDocument todo, CancellationToken cancellationToken = default)
        {
            using var qwen = new QwenClient(App.Settings.Current.QwenApiKey);
            var embedding = await qwen.EmbedRetrievalDocumentAsync(todo.Category, todo.Value, cancellationToken);
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
