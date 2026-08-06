using System;
using System.Collections.Generic;
using System.Linq;
using App.Models;

namespace App.Services
{
    internal sealed class ChatSessionStorageService
    {
        public IReadOnlyDictionary<ChatPersonality, ChatSession> Load()
        {
            var sessions = Enum.GetValues<ChatPersonality>()
                .ToDictionary(personality => personality, _ => new ChatSession());

            foreach (var document in App.Settings.Database.ChatSessions.FindAll())
            {
                if (!Enum.TryParse<ChatPersonality>(document.Agent, true, out var personality)) continue;
                try { sessions[personality] = ChatSessionSerializer.Deserialize(document.Histories, document.Context); }
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
                Histories = ChatSessionSerializer.Serialize(session)
            });
        }

        public void Delete(ChatPersonality personality) => App.Settings.Database.ChatSessions.Delete(personality.ToString());
    }
}
