using System.Text.Json.Serialization;

namespace App.Models
{
    internal sealed class NotebookEntry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "paragraph";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    internal sealed class NotebookSearchResult
    {
        public required int EntryIndex { get; init; }

        public required string Title { get; init; }

        public required string Details { get; init; }
    }
}
