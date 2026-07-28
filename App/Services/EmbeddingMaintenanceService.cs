using App.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal static class EmbeddingMaintenanceService
    {
        private static int _isRunning;

        public static void InvalidateAndRebuild()
        {
            var database = App.Settings.Database;
            foreach (var note in database.Notes.FindAll()) { note.Embedding = []; database.Notes.Update(note); }
            foreach (var memo in database.Memos.FindAll()) { memo.Embedding = []; database.Memos.Update(memo); }
            foreach (var todo in database.Todos.FindAll()) { todo.Embedding = []; database.Todos.Update(todo); }
            if (Interlocked.Exchange(ref _isRunning, 1) == 0) _ = RebuildAsync();
        }

        private static async Task RebuildAsync()
        {
            try
            {
                using var client = new GeminiClient(App.Settings.Current.GeminiApiKey);
                var database = App.Settings.Database;
                foreach (var note in database.Notes.FindAll())
                {
                    var embeddingText = NotebookFormat.CreateEmbeddingText(note.Value);
                    note.Embedding = string.IsNullOrWhiteSpace(embeddingText)
                        ? []
                        : NotebookDatabaseService.FloatsToBytes(await client.EmbedNoteAsync(embeddingText, CancellationToken.None));
                    database.Notes.Update(note);
                }
                foreach (var memo in database.Memos.FindAll())
                {
                    memo.Embedding = NotebookDatabaseService.FloatsToBytes(await client.EmbedRetrievalDocumentAsync(memo.Topic, memo.Value, CancellationToken.None));
                    database.Memos.Update(memo);
                }
                foreach (var todo in database.Todos.FindAll())
                {
                    todo.Embedding = NotebookDatabaseService.FloatsToBytes(await client.EmbedRetrievalDocumentAsync(todo.Category, todo.Value, CancellationToken.None));
                    database.Todos.Update(todo);
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Embedding rebuild failed: {exception.Message}");
            }
            finally { Interlocked.Exchange(ref _isRunning, 0); }
        }
    }
}
