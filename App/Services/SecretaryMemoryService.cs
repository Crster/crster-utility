using App.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace App.Services
{
    internal sealed class SecretaryMemoryService
    {
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

        public MemoDocument WriteMemo(string value)
        {
            value = Required(value, nameof(value));
            var memo = new MemoDocument
            {
                Value = value,
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

        private static string Required(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new FormatException($"{name} is required.") : value.Trim();
    }
}
