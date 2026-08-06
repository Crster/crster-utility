using App.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace App.Services
{
    internal sealed class TodoSearchService
    {
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

        /// <summary>Ranks open todos by how many of the supplied search patterns they contain.</summary>
        public List<TodoSearchResult> Search(IReadOnlyList<Regex> patterns, int maximumResults = 10)
        {
            if (patterns.Count == 0) return [];
            return Database.Todos.Find(todo => !todo.IsDone)
                .Select(todo => (Todo: todo, Matches: KeywordSearchService.CountMatches(patterns, $"{todo.Category}\n{todo.Value}")))
                .Where(match => match.Matches > 0)
                .OrderByDescending(match => match.Matches)
                .ThenByDescending(match => match.Todo.CreatedAt)
                .Take(maximumResults)
                .Select(match => ToResult(match.Todo))
                .ToList();
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
