using LiteDB;
using System;
using System.Collections.Generic;

namespace App.Models
{
    internal sealed class SettingDocument
    {
        [BsonId] public string Key { get; set; } = string.Empty;
        public BsonValue Value { get; set; } = BsonValue.Null;
        public string Name { get; set; } = string.Empty;
        public BsonValue Default { get; set; } = BsonValue.Null;
    }

    internal sealed class AttachmentDocument
    {
        [BsonId] public string Key { get; set; } = Guid.NewGuid().ToString("D");
        public byte[] Value { get; set; } = [];
        public string Filename { get; set; } = string.Empty;
        public string Mimetype { get; set; } = "application/octet-stream";
        public string Hash { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    internal sealed class MemoDocument
    {
        [BsonId] public string Key { get; set; } = Guid.NewGuid().ToString("D");
        public byte[] Embedding { get; set; } = [];
        public string Topic { get; set; } = "knowledge";
        public string Value { get; set; } = string.Empty;
        public List<string> Attachments { get; set; } = [];
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    internal sealed class TodoDocument
    {
        [BsonId] public string Key { get; set; } = Guid.NewGuid().ToString("D");
        public string Value { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public bool IsDone { get; set; }
        public string CreatedBy { get; set; } = "user";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DoneAt { get; set; }
    }

    internal sealed class TodoCategoryDocument
    {
        [BsonId] public string Key { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
