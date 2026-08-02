using LiteDB;
using System;
using System.Collections.Generic;

namespace App.Models
{
    internal sealed class SettingDocument
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        [BsonField("value")]
        public BsonValue Value { get; set; } = BsonValue.Null;
        [BsonField("name")]
        public string Name { get; set; } = string.Empty;
        [BsonField("default")]
        public BsonValue Default { get; set; } = BsonValue.Null;
    }

    internal sealed class NoteDocument
    {
        [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("D");
        [BsonField("embedding")] public byte[] Embedding { get; set; } = [];
        [BsonField("value")] public string Value { get; set; } = string.Empty;
        [BsonField("attachments")] public List<string> Attachments { get; set; } = [];
        [BsonField("timestamp")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    internal sealed class AttachmentDocument
    {
        [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("D");
        [BsonField("value")] public byte[] Value { get; set; } = [];
        [BsonField("filename")] public string Filename { get; set; } = string.Empty;
        [BsonField("mimetype")] public string Mimetype { get; set; } = "application/octet-stream";
        [BsonField("hash")] public string Hash { get; set; } = string.Empty;
        [BsonField("size")] public long Size { get; set; }
        [BsonField("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    internal sealed class MemoDocument
    {
        [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("D");
        [BsonField("embedding")] public byte[] Embedding { get; set; } = [];
        [BsonField("value")] public string Value { get; set; } = string.Empty;
        [BsonField("attachments")] public List<string> Attachments { get; set; } = [];
        [BsonField("timestamp")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    internal sealed class TodoDocument
    {
        [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("D");
        [BsonField("embedding")] public byte[] Embedding { get; set; } = [];
        [BsonField("value")] public string Value { get; set; } = string.Empty;
        [BsonField("category")] public string Category { get; set; } = string.Empty;
        [BsonField("is_done")] public bool IsDone { get; set; }
        [BsonField("created_by")] public string CreatedBy { get; set; } = "user";
        [BsonField("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [BsonField("notify")] public string Notify { get; set; } = string.Empty;
        [BsonField("notified_at")] public DateTime NotifiedAt { get; set; }
        [BsonField("done_at")] public DateTime? DoneAt { get; set; }
    }

    internal sealed class TodoCategoryDocument
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        [BsonField("description")] public string Description { get; set; } = string.Empty;
    }

    internal sealed class ChatSessionDocument
    {
        [BsonId] public string Agent { get; set; } = string.Empty;
        [BsonField("context")] public string Context { get; set; } = string.Empty;
        [BsonField("histories")] public string Histories { get; set; } = string.Empty;
    }
}
