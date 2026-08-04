using App.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal static class EmbeddingMaintenanceService
    {
        private static int _isRunning;

        public static bool IsRunning => Volatile.Read(ref _isRunning) == 1;

        /// <summary>Clears every stored embedding and regenerates it with the current embedding model.</summary>
        public static async Task<int> RebuildAllAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _isRunning, 1) == 1)
                throw new InvalidOperationException("An embedding rebuild is already running.");
            try
            {
                var database = App.Settings.Database;
                // LiteDB cursors stay open while enumerated, so materialize before updating the same collection.
                var notes = database.Notes.FindAll().ToList();
                var memos = database.Memos.FindAll().ToList();
                var todos = database.Todos.FindAll().ToList();
                foreach (var note in notes) { note.Embedding = []; database.Notes.Update(note); }
                foreach (var memo in memos) { memo.Embedding = []; database.Memos.Update(memo); }
                foreach (var todo in todos) { todo.Embedding = []; database.Todos.Update(todo); }

                var rebuilt = 0;
                using var client = new OpenAiCompatibleClient(App.Settings.Current.OpenAiCompatibleApiKey);
                foreach (var note in notes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var embeddingText = NotebookFormat.CreateEmbeddingText(note.Value);
                    note.Embedding = string.IsNullOrWhiteSpace(embeddingText)
                        ? []
                        : NotebookDatabaseService.FloatsToBytes(await client.EmbedNoteAsync(embeddingText, cancellationToken));
                    database.Notes.Update(note);
                    rebuilt++;
                }
                foreach (var memo in memos)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    memo.Embedding = NotebookDatabaseService.FloatsToBytes(await client.EmbedRetrievalDocumentAsync(string.Empty, memo.Value, cancellationToken));
                    database.Memos.Update(memo);
                    rebuilt++;
                }
                foreach (var todo in todos)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    todo.Embedding = NotebookDatabaseService.FloatsToBytes(await client.EmbedRetrievalDocumentAsync(todo.Category, todo.Value, cancellationToken));
                    database.Todos.Update(todo);
                    rebuilt++;
                }
                return rebuilt;
            }
            finally { Interlocked.Exchange(ref _isRunning, 0); }
        }
    }
}
