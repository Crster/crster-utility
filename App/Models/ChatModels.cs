using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace App.Models
{
    internal enum ChatItemKind { User, Assistant, Thinking, Tool, Error }
    internal enum ChatPersonality { Secretary, Smart, Technician, Cody }
    internal enum OpenAiCompatibleThinkingLevel { Default, Disabled, Minimal, Low, High }

    internal sealed class OpenAiCompatibleModel
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public string Description { get; init; } = string.Empty;
        public bool SupportsChat { get; init; }
        public bool SupportsEmbedding { get; init; }
        public bool SupportsImageGeneration { get; init; }
        public bool SupportsThinking { get; init; }
    }

    internal sealed record ChatAttachment(
        string LocalPath,
        string DisplayName,
        string MimeType,
        string? RemoteName,
        string? RemoteUri,
        bool IsTemporary = false,
        Guid AttachmentId = default,
        string FileExtension = "",
        string TokenName = "");
    internal sealed record OpenAiCompatibleFunctionCall(string Id, string Name, JsonObject Arguments, string? ArgumentsError = null);
    internal sealed record GeneratedImage(byte[] Data, string MimeType);
    internal sealed record GroundedSource(string Title, string Uri);

    internal sealed class OpenAiCompatibleTurnResult
    {
        public List<JsonObject> Steps { get; } = [];
        public List<OpenAiCompatibleFunctionCall> FunctionCalls { get; } = [];
        public List<GroundedSource> Sources { get; } = [];
        public string Text { get; set; } = string.Empty;
        public string Thinking { get; set; } = string.Empty;
        public GeneratedImage? Image { get; set; }
        public string? InteractionId { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
    }

    internal sealed class ChatSession
    {
        public string PersistentId { get; } = Guid.NewGuid().ToString("N");
        public List<JsonObject> History { get; } = [];
        public List<ChatMessage> Messages { get; } = [];
        public string ContextText { get; set; } = string.Empty;
        public bool ProjectDocumentationScanned { get; set; }
        public string ProjectDocumentationFingerprint { get; set; } = string.Empty;
        public TechnicianModelTier? LastTechnicianModelTier { get; set; }
        public string AgentProvider { get; set; } = string.Empty;
        public string AgentSessionId { get; set; } = string.Empty;
    }

    internal sealed record ChatMessage(
        ChatItemKind Kind,
        string Title,
        string Content,
        IReadOnlyList<ChatAttachment>? Attachments = null,
        GeneratedImage? Image = null,
        JsonObject? ToolArguments = null,
        bool? ToolSucceeded = null,
        bool IncludeInContext = true,
        string? DiffOld = null,
        string? DiffNew = null);

    /// <summary>DiffOld/DiffNew are UI-only before/after text for change tools; they are never sent back
    /// to the model, which only ever sees Output.</summary>
    internal sealed record ToolResult(
        bool Success,
        string Output,
        string Status = "completed",
        GeneratedImage? Image = null,
        string? DiffOld = null,
        string? DiffNew = null);
}
