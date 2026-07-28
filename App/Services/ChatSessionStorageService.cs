using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using App.Models;

namespace App.Services
{
    internal sealed class ChatSessionStorageService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public IReadOnlyDictionary<ChatPersonality, ChatSession> Load()
        {
            var sessions = Enum.GetValues<ChatPersonality>()
                .ToDictionary(personality => personality, _ => new ChatSession());

            foreach (var document in App.Settings.Database.ChatSessions.FindAll())
            {
                if (!Enum.TryParse<ChatPersonality>(document.Agent, true, out var personality)) continue;
                try { sessions[personality] = Deserialize(document); }
                catch (Exception) { }
            }

            return sessions;
        }

        public void Save(ChatPersonality personality, ChatSession session)
        {
            App.Settings.Database.ChatSessions.Upsert(new ChatSessionDocument
            {
                Agent = personality.ToString(),
                Context = session.ContextText,
                Histories = JsonSerializer.Serialize(new PersistedChatSession
                {
                    History = session.History.Select(step => step.ToJsonString()).ToList(),
                    Messages = session.Messages.Select(PersistedChatMessage.From).ToList(),
                    ProjectDocumentationScanned = session.ProjectDocumentationScanned,
                    ProjectDocumentationFingerprint = session.ProjectDocumentationFingerprint,
                    LastTechnicianModelTier = session.LastTechnicianModelTier
                }, SerializerOptions)
            });
        }

        public void Delete(ChatPersonality personality) => App.Settings.Database.ChatSessions.Delete(personality.ToString());

        private static ChatSession Deserialize(ChatSessionDocument document)
        {
            var persisted = string.IsNullOrWhiteSpace(document.Histories)
                ? new PersistedChatSession()
                : JsonSerializer.Deserialize<PersistedChatSession>(document.Histories, SerializerOptions) ?? new PersistedChatSession();
            var session = new ChatSession
            {
                ContextText = document.Context,
                ProjectDocumentationScanned = persisted.ProjectDocumentationScanned,
                ProjectDocumentationFingerprint = persisted.ProjectDocumentationFingerprint ?? string.Empty,
                LastTechnicianModelTier = persisted.LastTechnicianModelTier
            };

            foreach (var history in persisted.History)
                if (JsonNode.Parse(history) is JsonObject step) session.History.Add(step);
            foreach (var message in persisted.Messages) session.Messages.Add(message.ToChatMessage());
            return session;
        }

        private sealed class PersistedChatSession
        {
            public List<string> History { get; set; } = [];
            public List<PersistedChatMessage> Messages { get; set; } = [];
            public bool ProjectDocumentationScanned { get; set; }
            public string? ProjectDocumentationFingerprint { get; set; }
            public TechnicianModelTier? LastTechnicianModelTier { get; set; }
        }

        private sealed class PersistedChatMessage
        {
            public ChatItemKind Kind { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
            public string? ImageData { get; set; }
            public string? ImageMimeType { get; set; }
            public string? ToolArguments { get; set; }
            public bool? ToolSucceeded { get; set; }
            public bool IncludeInContext { get; set; }

            public static PersistedChatMessage From(ChatMessage message) => new()
            {
                Kind = message.Kind,
                Title = message.Title,
                Content = message.Content,
                ImageData = message.Image is null ? null : Convert.ToBase64String(message.Image.Data),
                ImageMimeType = message.Image?.MimeType,
                ToolArguments = message.ToolArguments?.ToJsonString(),
                ToolSucceeded = message.ToolSucceeded,
                IncludeInContext = message.IncludeInContext
            };

            public ChatMessage ToChatMessage() => new(
                Kind,
                Title,
                Content,
                Image: string.IsNullOrWhiteSpace(ImageData) ? null : new GeneratedImage(Convert.FromBase64String(ImageData), ImageMimeType ?? "application/octet-stream"),
                ToolArguments: string.IsNullOrWhiteSpace(ToolArguments) ? null : JsonNode.Parse(ToolArguments) as JsonObject,
                ToolSucceeded: ToolSucceeded,
                IncludeInContext: IncludeInContext);
        }
    }
}
