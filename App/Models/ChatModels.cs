using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace App.Models
{
    internal enum ChatItemKind { User, Assistant, Tool, Error }
    internal enum ChatPersonality { Smart, Technician, Artist, Planner, Study }

    internal sealed class GeminiModel
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public string Description { get; init; } = string.Empty;
        public bool SupportsChat { get; init; }
        public bool SupportsThinking { get; init; }
    }

    internal sealed record ChatAttachment(string LocalPath, string DisplayName, string MimeType, string? RemoteName, string? RemoteUri);
    internal sealed record GeminiFunctionCall(string Id, string Name, JsonObject Arguments);
    internal sealed record GeneratedImage(byte[] Data, string MimeType);
    internal sealed record GroundedSource(string Title, string Uri);

    internal sealed class GeminiTurnResult
    {
        public List<JsonObject> Steps { get; } = [];
        public List<GeminiFunctionCall> FunctionCalls { get; } = [];
        public List<GroundedSource> Sources { get; } = [];
        public string Text { get; set; } = string.Empty;
        public GeneratedImage? Image { get; set; }
        public string? InteractionId { get; set; }
    }

    internal sealed class ChatSession
    {
        public List<JsonObject> History { get; } = [];
        public List<ChatMessage> Messages { get; } = [];
        public List<ChatAttachment> Attachments { get; } = [];
        public List<GeneratedImage> GeneratedImages { get; } = [];
        public string ContextText { get; set; } = string.Empty;
        public string OriginalArtistPrompt { get; set; } = string.Empty;
        public string ArtistEditSummary { get; set; } = string.Empty;
        public bool HasPathContext { get; set; }
        public string? PathContext { get; set; }
    }

    internal sealed record ChatMessage(ChatItemKind Kind, string Title, string Content, IReadOnlyList<string>? AttachmentNames = null, GeneratedImage? Image = null);

    internal enum ToolRiskLevel { Safe, Risky }
    internal enum ToolApprovalPolicy { None, AgentConditional, Always, AlwaysWithUac, ManualScreenSelection }

    internal sealed record ToolResult(
        bool Success,
        string Output,
        string Status = "completed",
        GeneratedImage? Image = null);
}
