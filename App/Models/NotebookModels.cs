using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace App.Models
{
    internal sealed class NotebookEntry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "note";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    internal sealed class NotebookDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 2;

        [JsonPropertyName("entries")]
        public List<NotebookEntry> Entries { get; set; } = [];
    }

    internal sealed class NotebookSearchResult
    {
        public required int EntryIndex { get; init; }

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
}
