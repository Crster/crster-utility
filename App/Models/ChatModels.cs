using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace App.Models
{
    internal enum ChatItemKind { User, Assistant, Tool, Error }

    internal sealed class ChatItem
    {
        public ChatItemKind Kind { get; init; }
        public string Content { get; set; } = string.Empty;
        public string Title { get; init; } = string.Empty;
    }

    internal sealed class GeminiModel
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public string Description { get; init; } = string.Empty;
        public bool SupportsChat { get; init; }
        public bool SupportsThinking { get; init; }
        public override string ToString() => SupportsChat ? DisplayName : $"{DisplayName} (not chat compatible)";
    }

    internal sealed record ChatAttachment(string LocalPath, string DisplayName, string MimeType, string? RemoteName, string? RemoteUri);

    internal sealed record GeminiFunctionCall(string Id, string Name, JsonObject Arguments);

    internal sealed class GeminiTurnResult
    {
        public List<JsonObject> Steps { get; } = [];
        public List<GeminiFunctionCall> FunctionCalls { get; } = [];
        public string Text { get; set; } = string.Empty;
    }

    internal sealed record ToolResult(bool Success, string Output);
}
