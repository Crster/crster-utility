using System;
using System.Collections.Generic;

namespace App.Models
{
    internal sealed class NotebookEntry
    {
        public string Key { get; set; } = Guid.NewGuid().ToString("D");

        public string Type { get; set; } = "note";
        public string Content { get; set; } = string.Empty;
        public byte[] Embedding { get; set; } = [];
        public List<string> Attachments { get; set; } = [];
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    internal sealed class NotebookSearchResult
    {
        public required string EntryKey { get; init; }

        public required string Title { get; init; }

        public required string Details { get; init; }
    }

    internal sealed class FeatureSearchResult
    {
        public required string Title { get; init; }

        public required string Details { get; init; }

        public required string SearchTerms { get; init; }

        public required string Destination { get; init; }
    }

    internal sealed class TodoSearchResult
    {
        public required string TodoId { get; init; }
        public required string Title { get; init; }
        public required string Details { get; init; }
    }
}
