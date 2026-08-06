using System;

namespace App.Models
{
    internal sealed record SecretaryProfileContext(string Text, long LastTurnId);

    internal sealed record SecretaryConversationTurn(
        long Id,
        string UserText,
        string AssistantText,
        string AttachmentNames,
        DateTimeOffset CreatedUtc);

    internal sealed record SecretaryMemory(
        string Id,
        string Category,
        string SubjectKey,
        string Content,
        int Importance,
        DateTimeOffset UpdatedUtc);

    internal sealed record SecretaryScheduleEvent(
        string Id,
        string Title,
        DateTimeOffset StartUtc,
        DateTimeOffset? EndUtc,
        string TimeZoneId,
        bool IsAllDay,
        string? Location,
        string? Notes,
        string Status);

    internal sealed record SecretaryResumeChunk(
        long Id,
        string SectionTitle,
        string Content);
}
