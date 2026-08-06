using App.Models;
using LiteDB;
using System;
using System.IO;

namespace App.Services
{
    internal sealed class LiteDatabaseService : IDisposable
    {
        private readonly LiteDatabase _database;

        public LiteDatabaseService(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            _database = new LiteDatabase(new ConnectionString { Filename = Path, Connection = ConnectionType.Shared });
            Configure();
        }

        public string Path { get; }
        public ILiteDatabase Database => _database;
        public ILiteCollection<SettingDocument> Settings => _database.GetCollection<SettingDocument>("Settings");
        public ILiteCollection<NoteDocument> Notes => _database.GetCollection<NoteDocument>("Notes");
        public ILiteCollection<AttachmentDocument> Attachments => _database.GetCollection<AttachmentDocument>("Attachments");
        public ILiteCollection<MemoDocument> Memos => _database.GetCollection<MemoDocument>("Memos");
        public ILiteCollection<TodoDocument> Todos => _database.GetCollection<TodoDocument>("Todos");
        public ILiteCollection<TodoCategoryDocument> TodoCategories => _database.GetCollection<TodoCategoryDocument>("TodoCategories");
        public ILiteCollection<ChatSessionDocument> ChatSessions => _database.GetCollection<ChatSessionDocument>("ChatSessions");
        public ILiteCollection<SavedChatSessionDocument> SavedChatSessions => _database.GetCollection<SavedChatSessionDocument>("SavedChatSessions");

        private void Configure()
        {
            MigrateTodoIsDoneField();
            Notes.EnsureIndex(item => item.Timestamp);
            Attachments.EnsureIndex(item => item.Hash);
            Memos.EnsureIndex(item => item.Timestamp);
            Todos.EnsureIndex(item => item.Category);
            Todos.EnsureIndex(item => item.IsDone);
            Todos.EnsureIndex(item => item.CreatedAt);
            SavedChatSessions.EnsureIndex(item => item.SavedAt);
        }

        private void MigrateTodoIsDoneField()
        {
            var todos = _database.GetCollection<BsonDocument>("Todos");
            foreach (var todo in todos.FindAll())
            {
                if (!todo.ContainsKey("isDone")) continue;
                if (!todo.ContainsKey("is_done")) todo["is_done"] = todo["isDone"];
                todo.Remove("isDone");
                todos.Update(todo);
            }
        }

        public void Dispose() => _database.Dispose();
    }
}
