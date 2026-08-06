using System;
using System.Collections.Generic;
using System.Linq;
using App.Models;

namespace App.Services
{
    /// <summary>Stores chats the user kept on purpose, so a previous chat can be reopened or removed.</summary>
    internal sealed class SavedChatSessionService
    {
        private const int MaximumTitleCharacters = 70;

        public IReadOnlyList<SavedChatSessionDocument> List() =>
            App.Settings.Database.SavedChatSessions
                .FindAll()
                .OrderByDescending(document => document.SavedAt)
                .ToList();

        /// <summary>Writes the chat under <paramref name="id"/>, or under a new id when none is given.</summary>
        public SavedChatSessionDocument Save(ChatPersonality personality, ChatSession session, string? id = null)
        {
            var document = new SavedChatSessionDocument
            {
                Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("D") : id,
                Agent = personality.ToString(),
                Title = CreateTitle(session),
                MessageCount = session.Messages.Count(message => message.Kind is ChatItemKind.User or ChatItemKind.Assistant),
                SavedAt = DateTime.UtcNow,
                Histories = ChatSessionSerializer.Serialize(session)
            };
            App.Settings.Database.SavedChatSessions.Upsert(document);
            return document;
        }

        public ChatSession? Load(string id)
        {
            var document = App.Settings.Database.SavedChatSessions.FindById(id);
            if (document is null) return null;
            try { return ChatSessionSerializer.Deserialize(document.Histories); }
            catch (Exception) { return null; }
        }

        public void Delete(string id) => App.Settings.Database.SavedChatSessions.Delete(id);

        private static string CreateTitle(ChatSession session)
        {
            var firstRequest = session.Messages.FirstOrDefault(message => message.Kind == ChatItemKind.User)?.Content;
            if (string.IsNullOrWhiteSpace(firstRequest)) return "Untitled chat";

            var title = string.Join(' ', firstRequest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return title.Length <= MaximumTitleCharacters ? title : title[..MaximumTitleCharacters].TrimEnd() + "…";
        }
    }
}
