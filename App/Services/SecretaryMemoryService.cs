using App.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class SecretaryMemoryService : IDisposable
    {
        private const string EmbeddingModel = "gemini-embedding-2";
        private const int EmbeddingDimensions = 768;
        private const int MaximumRetrievedTurns = 30;
        private const int MaximumRetrievedFacts = 8;
        private const int MaximumTurnExcerptCharacters = 1_500;
        private const int MaximumContextCharacters = 60_000;
        private readonly GeminiClient _client;
        private string _databasePath;
        private readonly SemaphoreSlim _databaseGate = new(1, 1);
        private readonly bool _followsNotebookLocation;
        private bool _initialized;

        public SecretaryMemoryService(GeminiClient client, string? rootPath = null)
        {
            _client = client;
            _followsNotebookLocation = string.IsNullOrWhiteSpace(rootPath);
            _databasePath = System.IO.Path.Combine(
                _followsNotebookLocation ? new NotebookDatabaseService().RootPath : rootPath!,
                "secretary.sqlite");
            if (_followsNotebookLocation) App.Settings.Changed += Settings_Changed;
        }

        public async Task<string> BuildRetrievalContextAsync(string query, bool includeFullResume, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            float[]? queryEmbedding = null;
            try { queryEmbedding = await _client.EmbedRetrievalQueryAsync(query, token); }
            catch when (!token.IsCancellationRequested) { }

            SecretaryProfileContext profile;
            List<SecretaryConversationTurn> turns;
            List<SecretaryMemory> memories;
            List<SecretaryScheduleEvent> events;
            List<SecretaryResumeChunk> resumeChunks;
            string? fullResume;

            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                profile = await ReadProfileAsync(connection, token);
                turns = await ReadTurnsAsync(connection, token);
                memories = await ReadMemoriesAsync(connection, null, 500, token);
                events = await ReadEventsAsync(connection, null, null, 500, token);
                resumeChunks = await ReadResumeChunksAsync(connection, token);
                fullResume = await ReadResumeTextAsync(connection, token);
            }
            finally { _databaseGate.Release(); }

            var builder = new StringBuilder();
            AppendSection(builder, "Rolling profile and current context", profile.Text);

            var rankedTurns = turns
                .Select(item => (Item: item, Score: Score(query, queryEmbedding, $"{item.UserText}\n{item.AssistantText}", item.Embedding)))
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Item.CreatedUtc)
                .Take(MaximumRetrievedTurns)
                .ToList();
            if (rankedTurns.Count > 0)
            {
                builder.AppendLine("\nPast Secretary conversations (reference only):");
                foreach (var ranked in rankedTurns)
                {
                    var text = $"User: {ranked.Item.UserText}\nSecretary: {ranked.Item.AssistantText}";
                    builder.AppendLine($"- Turn {ranked.Item.Id} ({ranked.Item.CreatedUtc:O}): {Truncate(text, MaximumTurnExcerptCharacters)}");
                }
            }

            var rankedFacts = memories
                .Select(item => (Text: $"Memory {item.Id} [{item.Category}/{item.SubjectKey}]: {item.Content}", Score: Score(query, queryEmbedding, $"{item.SubjectKey} {item.Content}", item.Embedding)))
                .Concat(events.Select(item => (
                    Text: $"Event {item.Id}: {item.Title}; {item.StartUtc:O}; timezone {item.TimeZoneId}; location {item.Location}; notes {item.Notes}; status {item.Status}",
                    Score: Score(query, queryEmbedding, $"{item.Title} {item.Location} {item.Notes}", item.Embedding))))
                .OrderByDescending(item => item.Score)
                .Take(MaximumRetrievedFacts)
                .ToList();
            if (rankedFacts.Count > 0)
            {
                builder.AppendLine("\nRelevant personal memories and events (reference only):");
                foreach (var item in rankedFacts) builder.AppendLine($"- {Truncate(item.Text, 1_500)}");
            }

            if (includeFullResume && !string.IsNullOrWhiteSpace(fullResume))
            {
                AppendSection(builder, "Master resume (factual source; do not invent beyond it)", fullResume);
            }
            else
            {
                var relevantResume = resumeChunks
                    .Select(item => (Item: item, Score: Score(query, queryEmbedding, $"{item.SectionTitle} {item.Content}", item.Embedding)))
                    .OrderByDescending(item => item.Score)
                    .Take(5)
                    .ToList();
                if (relevantResume.Count > 0)
                {
                    builder.AppendLine("\nRelevant master-resume sections:");
                    foreach (var item in relevantResume)
                        builder.AppendLine($"- {item.Item.SectionTitle}: {Truncate(item.Item.Content, 2_500)}");
                }
            }

            if (queryEmbedding is not null)
                await RetryPendingEmbeddingsAsync(3, token);

            return Truncate(builder.ToString().Trim(), MaximumContextCharacters);
        }

        public async Task<string> BuildPersonalInfoContextAsync(string topic, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            float[]? embedding = null;
            try { embedding = await _client.EmbedRetrievalQueryAsync(topic, token); }
            catch when (!token.IsCancellationRequested) { }

            List<SecretaryMemory> memories;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                memories = await ReadMemoriesAsync(connection, null, 500, token);
            }
            finally { _databaseGate.Release(); }

            var relevant = memories
                .Select(item => (Item: item, Score: Score(topic, embedding, $"{item.SubjectKey} {item.Content}", item.Embedding)))
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Item.UpdatedUtc)
                .Take(12)
                .Select(item => $"- {item.Item.SubjectKey} (written {item.Item.UpdatedUtc:O}): {Truncate(item.Item.Content, 1_500)}");
            return string.Join("\n", relevant);
        }

        public async Task<ToolResult> ListPersonalInfoAsync(string topic, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            float[]? embedding = null;
            try { embedding = await _client.EmbedRetrievalQueryAsync(topic, token); }
            catch when (!token.IsCancellationRequested) { }

            List<SecretaryMemory> memories;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                memories = await ReadMemoriesAsync(connection, null, 500, token);
            }
            finally { _databaseGate.Release(); }

            var items = new JsonArray();
            foreach (var match in memories
                .Select(item => (Item: item, Score: Score(topic, embedding, $"{item.SubjectKey} {item.Content}", item.Embedding)))
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Item.UpdatedUtc)
                .Take(20))
            {
                items.Add(new JsonObject
                {
                    ["topic"] = match.Item.SubjectKey,
                    ["knowledge"] = match.Item.Content,
                    ["written_utc"] = match.Item.UpdatedUtc.ToString("O"),
                    ["relevance"] = match.Score
                });
            }
            return Result(true, "completed", $"Found {items.Count} personal information item(s).", new JsonObject { ["items"] = items });
        }

        public Task<ToolResult> WritePersonalInfoAsync(string topic, string newKnowledge, CancellationToken token) =>
            RememberAsync("personal_info", topic, newKnowledge, 3, token);

        public async Task<long> SaveConversationTurnAsync(
            string sessionId,
            string userText,
            string assistantText,
            IReadOnlyList<string> attachmentNames,
            CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            long id;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO conversation_turns
                        (session_id, user_text, assistant_text, attachment_names, created_utc, embedding_status)
                    VALUES ($session, $user, $assistant, $attachments, $created, 'pending');
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$user", userText);
                command.Parameters.AddWithValue("$assistant", assistantText);
                command.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(attachmentNames));
                command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                id = Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            }
            finally { _databaseGate.Release(); }

            await TryEmbedAndUpdateAsync("conversation_turns", id.ToString(CultureInfo.InvariantCulture), "conversation", $"{userText}\n{assistantText}", token);
            return id;
        }

        public async Task<(SecretaryProfileContext Profile, IReadOnlyList<SecretaryConversationTurn> Turns)> GetProfileUpdateInputAsync(CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                var profile = await ReadProfileAsync(connection, token);
                var turns = await ReadTurnsAfterAsync(connection, profile.LastTurnId, token);
                return (profile, turns);
            }
            finally { _databaseGate.Release(); }
        }

        public async Task SaveProfileContextAsync(string text, long lastTurnId, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            text = LimitWords(text, 500);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO profile_context (id, context_text, last_turn_id, updated_utc)
                    VALUES (1, $text, $turn, $updated)
                    ON CONFLICT(id) DO UPDATE SET
                        context_text = excluded.context_text,
                        last_turn_id = excluded.last_turn_id,
                        updated_utc = excluded.updated_utc;
                    """;
                command.Parameters.AddWithValue("$text", text);
                command.Parameters.AddWithValue("$turn", lastTurnId);
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(token);
            }
            finally { _databaseGate.Release(); }
        }

        public async Task<ToolResult> RememberAsync(string category, string subjectKey, string content, int importance, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            category = NormalizeKey(category, "general");
            subjectKey = NormalizeKey(subjectKey, "memory");
            importance = Math.Clamp(importance, 1, 5);
            var id = Guid.NewGuid().ToString("N");
            var writtenUtc = DateTimeOffset.UtcNow;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO memories
                        (id, category, subject_key, content, importance, created_utc, updated_utc, embedding_status)
                    VALUES ($id, $category, $subject, $content, $importance, $now, $now, 'pending')
                    ON CONFLICT(category, subject_key) DO UPDATE SET
                        content = excluded.content,
                        importance = excluded.importance,
                        updated_utc = excluded.updated_utc,
                        embedding = NULL,
                        embedding_status = 'pending';
                    SELECT id FROM memories WHERE category = $category AND subject_key = $subject;
                    """;
                var now = writtenUtc.ToString("O");
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$category", category);
                command.Parameters.AddWithValue("$subject", subjectKey);
                command.Parameters.AddWithValue("$content", content.Trim());
                command.Parameters.AddWithValue("$importance", importance);
                command.Parameters.AddWithValue("$now", now);
                id = Convert.ToString(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) ?? id;
            }
            finally { _databaseGate.Release(); }

            var embedded = await TryEmbedAndUpdateAsync("memories", id, $"{category}: {subjectKey}", content, token);
            return Result(true, embedded ? "completed" : "partial", $"Remembered {subjectKey}.", new JsonObject
            {
                ["memory_id"] = id,
                ["category"] = category,
                ["subject_key"] = subjectKey,
                ["written_utc"] = writtenUtc.ToString("O"),
                ["embedding_status"] = embedded ? "ready" : "pending"
            });
        }

        public async Task<ToolResult> ListMemoriesAsync(string? category, int limit, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                var memories = await ReadMemoriesAsync(connection, category, Math.Clamp(limit, 1, 100), token);
                var items = new JsonArray();
                foreach (var item in memories)
                    items.Add(MemoryJson(item));
                return Result(true, "completed", $"Found {items.Count} memor{(items.Count == 1 ? "y" : "ies")}.", new JsonObject { ["items"] = items });
            }
            finally { _databaseGate.Release(); }
        }

        public async Task<ToolResult> SearchMemoriesAsync(string query, int limit, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            float[]? embedding = null;
            try { embedding = await _client.EmbedRetrievalQueryAsync(query, token); }
            catch when (!token.IsCancellationRequested) { }

            List<SecretaryMemory> memories;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                memories = await ReadMemoriesAsync(connection, null, 500, token);
            }
            finally { _databaseGate.Release(); }

            var matches = memories
                .Select(item => (Item: item, Score: Score(query, embedding, $"{item.SubjectKey} {item.Content}", item.Embedding)))
                .OrderByDescending(item => item.Score)
                .Take(Math.Clamp(limit, 1, 50));
            var items = new JsonArray();
            foreach (var match in matches)
            {
                var value = MemoryJson(match.Item);
                value["relevance"] = match.Score;
                items.Add(value);
            }
            return Result(true, "completed", $"Found {items.Count} matching memories.", new JsonObject { ["items"] = items });
        }

        public async Task<ToolResult> UpdateMemoryAsync(string id, string content, int? importance, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            string? title = null;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE memories SET
                        content = $content,
                        importance = COALESCE($importance, importance),
                        updated_utc = $updated,
                        embedding = NULL,
                        embedding_status = 'pending'
                    WHERE id = $id;
                    SELECT category || ': ' || subject_key FROM memories WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$content", content.Trim());
                command.Parameters.AddWithValue("$importance", importance is null ? DBNull.Value : Math.Clamp(importance.Value, 1, 5));
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                title = Convert.ToString(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            }
            finally { _databaseGate.Release(); }
            if (title is null) return Result(false, "not_found", "That memory was not found.");
            var embedded = await TryEmbedAndUpdateAsync("memories", id, title, content, token);
            return Result(true, embedded ? "completed" : "partial", "Updated the memory.", new JsonObject { ["memory_id"] = id, ["embedding_status"] = embedded ? "ready" : "pending" });
        }

        public Task<ToolResult> ForgetMemoryAsync(string id, CancellationToken token) =>
            DeleteByIdAsync("memories", id, "memory", token);

        public async Task<ToolResult> CreateScheduleEventAsync(SecretaryScheduleEvent item, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO schedule_events
                        (id, title, start_utc, end_utc, timezone_id, is_all_day, location, notes, status, created_utc, updated_utc, embedding_status)
                    VALUES
                        ($id, $title, $start, $end, $timezone, $allDay, $location, $notes, $status, $now, $now, 'pending');
                    """;
                AddEventParameters(command, item);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(token);
            }
            finally { _databaseGate.Release(); }
            var embedded = await TryEmbedAndUpdateAsync("schedule_events", item.Id, "schedule event", EventEmbeddingText(item), token);
            return Result(true, embedded ? "completed" : "partial", $"Scheduled {item.Title}.", new JsonObject { ["event_id"] = item.Id, ["embedding_status"] = embedded ? "ready" : "pending" });
        }

        public async Task<ToolResult> ListScheduleAsync(DateTimeOffset? startUtc, DateTimeOffset? endUtc, int limit, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                var events = await ReadEventsAsync(connection, startUtc, endUtc, Math.Clamp(limit, 1, 100), token);
                var items = new JsonArray();
                foreach (var item in events) items.Add(EventJson(item));
                return Result(true, "completed", $"Found {items.Count} schedule event(s).", new JsonObject { ["items"] = items });
            }
            finally { _databaseGate.Release(); }
        }

        public async Task<ToolResult> UpdateScheduleEventAsync(SecretaryScheduleEvent item, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            var changed = 0;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE schedule_events SET
                        title = $title,
                        start_utc = $start,
                        end_utc = $end,
                        timezone_id = $timezone,
                        is_all_day = $allDay,
                        location = $location,
                        notes = $notes,
                        status = $status,
                        updated_utc = $updated,
                        embedding = NULL,
                        embedding_status = 'pending'
                    WHERE id = $id;
                    """;
                AddEventParameters(command, item);
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                changed = await command.ExecuteNonQueryAsync(token);
            }
            finally { _databaseGate.Release(); }
            if (changed == 0) return Result(false, "not_found", "That schedule event was not found.");
            var embedded = await TryEmbedAndUpdateAsync("schedule_events", item.Id, "schedule event", EventEmbeddingText(item), token);
            return Result(true, embedded ? "completed" : "partial", $"Updated {item.Title}.", new JsonObject { ["event_id"] = item.Id, ["embedding_status"] = embedded ? "ready" : "pending" });
        }

        public Task<ToolResult> DeleteScheduleEventAsync(string id, CancellationToken token) =>
            DeleteByIdAsync("schedule_events", id, "schedule event", token);

        public async Task<ToolResult> ReplaceResumeAsync(string resumeText, string? sourceFilename, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            var version = Guid.NewGuid().ToString("N");
            var chunks = SplitResume(resumeText);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = (SqliteTransaction)transaction;
                    delete.CommandText = "DELETE FROM resume_chunks; DELETE FROM resume_profile;";
                    await delete.ExecuteNonQueryAsync(token);
                }
                await using (var profile = connection.CreateCommand())
                {
                    profile.Transaction = (SqliteTransaction)transaction;
                    profile.CommandText =
                        """
                        INSERT INTO resume_profile (id, version, resume_text, source_filename, updated_utc)
                        VALUES (1, $version, $text, $source, $updated);
                        """;
                    profile.Parameters.AddWithValue("$version", version);
                    profile.Parameters.AddWithValue("$text", resumeText.Trim());
                    profile.Parameters.AddWithValue("$source", (object?)sourceFilename ?? DBNull.Value);
                    profile.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                    await profile.ExecuteNonQueryAsync(token);
                }
                foreach (var chunk in chunks)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText =
                        """
                        INSERT INTO resume_chunks
                            (resume_version, section_title, content, embedding_status)
                        VALUES ($version, $title, $content, 'pending');
                        """;
                    command.Parameters.AddWithValue("$version", version);
                    command.Parameters.AddWithValue("$title", chunk.Title);
                    command.Parameters.AddWithValue("$content", chunk.Content);
                    await command.ExecuteNonQueryAsync(token);
                }
                await transaction.CommitAsync(token);
            }
            finally { _databaseGate.Release(); }

            await EmbedPendingResumeChunksAsync(token);
            return Result(true, "completed", $"Saved the master resume in {chunks.Count} section(s).", new JsonObject
            {
                ["source_filename"] = sourceFilename,
                ["section_count"] = chunks.Count
            });
        }

        public async Task<ToolResult> ReadResumeAsync(CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                var text = await ReadResumeTextAsync(connection, token);
                return string.IsNullOrWhiteSpace(text)
                    ? Result(false, "not_found", "No master resume is stored.")
                    : Result(true, "completed", "Loaded the master resume.", new JsonObject { ["resume_text"] = text });
            }
            finally { _databaseGate.Release(); }
        }

        public async Task<ToolResult> ClearResumeAsync(CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM resume_chunks; DELETE FROM resume_profile;";
                var changed = await command.ExecuteNonQueryAsync(token);
                return Result(changed > 0, changed > 0 ? "completed" : "not_found", changed > 0 ? "Cleared the master resume." : "No master resume was stored.");
            }
            finally { _databaseGate.Release(); }
        }

        public async Task<ToolResult> SearchHistoryAsync(string query, int limit, CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            float[]? embedding = null;
            try { embedding = await _client.EmbedRetrievalQueryAsync(query, token); }
            catch when (!token.IsCancellationRequested) { }
            List<SecretaryConversationTurn> turns;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                turns = await ReadTurnsAsync(connection, token);
            }
            finally { _databaseGate.Release(); }
            var items = new JsonArray();
            foreach (var match in turns
                .Select(item => (Item: item, Score: Score(query, embedding, $"{item.UserText} {item.AssistantText}", item.Embedding)))
                .OrderByDescending(item => item.Score)
                .Take(Math.Clamp(limit, 1, 50)))
            {
                items.Add(new JsonObject
                {
                    ["turn_id"] = match.Item.Id,
                    ["created_utc"] = match.Item.CreatedUtc,
                    ["user"] = Truncate(match.Item.UserText, 1_500),
                    ["secretary"] = Truncate(match.Item.AssistantText, 1_500),
                    ["relevance"] = match.Score
                });
            }
            return Result(true, "completed", $"Found {items.Count} conversation turn(s).", new JsonObject { ["items"] = items });
        }

        public Task<ToolResult> DeleteHistoryTurnAsync(long id, CancellationToken token) =>
            DeleteByIdAsync("conversation_turns", id.ToString(CultureInfo.InvariantCulture), "conversation turn", token);

        public async Task<ToolResult> ClearHistoryAsync(CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM conversation_turns; UPDATE profile_context SET context_text = '', last_turn_id = 0, updated_utc = $updated WHERE id = 1;";
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(token);
                return Result(true, "completed", "Cleared Secretary conversation history.");
            }
            finally { _databaseGate.Release(); }
        }

        public async Task<string?> GetRememberedWeatherLocationAsync(CancellationToken token)
        {
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT content FROM memories
                    WHERE (category = 'location' AND subject_key IN ('default_weather_city', 'home_city', 'current_city'))
                       OR (category = 'personal_info' AND subject_key IN ('weather_location', 'environment', 'home'))
                    ORDER BY CASE subject_key WHEN 'default_weather_city' THEN 0 WHEN 'weather_location' THEN 1 WHEN 'home_city' THEN 2 ELSE 3 END
                    LIMIT 1;
                    """;
                return Convert.ToString(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            }
            finally { _databaseGate.Release(); }
        }

        private async Task EnsureInitializedAsync(CancellationToken token)
        {
            if (_initialized) return;
            await _databaseGate.WaitAsync(token);
            try
            {
                if (_initialized) return;
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS schema_info (
                        version INTEGER NOT NULL
                    );
                    INSERT INTO schema_info(version)
                    SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);

                    CREATE TABLE IF NOT EXISTS profile_context (
                        id INTEGER PRIMARY KEY CHECK (id = 1),
                        context_text TEXT NOT NULL DEFAULT '',
                        last_turn_id INTEGER NOT NULL DEFAULT 0,
                        updated_utc TEXT NOT NULL
                    );
                    INSERT OR IGNORE INTO profile_context(id, context_text, last_turn_id, updated_utc)
                    VALUES (1, '', 0, '1970-01-01T00:00:00.0000000+00:00');

                    CREATE TABLE IF NOT EXISTS memories (
                        id TEXT PRIMARY KEY,
                        category TEXT NOT NULL,
                        subject_key TEXT NOT NULL,
                        content TEXT NOT NULL,
                        importance INTEGER NOT NULL CHECK (importance BETWEEN 1 AND 5),
                        created_utc TEXT NOT NULL,
                        updated_utc TEXT NOT NULL,
                        embedding_model TEXT,
                        embedding_dimensions INTEGER,
                        embedding BLOB,
                        embedding_status TEXT NOT NULL DEFAULT 'pending',
                        UNIQUE(category, subject_key)
                    );
                    CREATE INDEX IF NOT EXISTS ix_memories_category ON memories(category, updated_utc DESC);
                    CREATE INDEX IF NOT EXISTS ix_memories_embedding_status ON memories(embedding_status);

                    CREATE TABLE IF NOT EXISTS schedule_events (
                        id TEXT PRIMARY KEY,
                        title TEXT NOT NULL,
                        start_utc TEXT NOT NULL,
                        end_utc TEXT,
                        timezone_id TEXT NOT NULL,
                        is_all_day INTEGER NOT NULL DEFAULT 0,
                        location TEXT,
                        notes TEXT,
                        status TEXT NOT NULL DEFAULT 'scheduled',
                        created_utc TEXT NOT NULL,
                        updated_utc TEXT NOT NULL,
                        embedding_model TEXT,
                        embedding_dimensions INTEGER,
                        embedding BLOB,
                        embedding_status TEXT NOT NULL DEFAULT 'pending'
                    );
                    CREATE INDEX IF NOT EXISTS ix_schedule_start ON schedule_events(start_utc);
                    CREATE INDEX IF NOT EXISTS ix_schedule_embedding_status ON schedule_events(embedding_status);

                    CREATE TABLE IF NOT EXISTS conversation_turns (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        user_text TEXT NOT NULL,
                        assistant_text TEXT NOT NULL,
                        attachment_names TEXT NOT NULL DEFAULT '[]',
                        created_utc TEXT NOT NULL,
                        embedding_model TEXT,
                        embedding_dimensions INTEGER,
                        embedding BLOB,
                        embedding_status TEXT NOT NULL DEFAULT 'pending'
                    );
                    CREATE INDEX IF NOT EXISTS ix_turns_created ON conversation_turns(created_utc DESC);
                    CREATE INDEX IF NOT EXISTS ix_turns_embedding_status ON conversation_turns(embedding_status);

                    CREATE TABLE IF NOT EXISTS resume_profile (
                        id INTEGER PRIMARY KEY CHECK (id = 1),
                        version TEXT NOT NULL,
                        resume_text TEXT NOT NULL,
                        source_filename TEXT,
                        updated_utc TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS resume_chunks (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        resume_version TEXT NOT NULL,
                        section_title TEXT NOT NULL,
                        content TEXT NOT NULL,
                        embedding_model TEXT,
                        embedding_dimensions INTEGER,
                        embedding BLOB,
                        embedding_status TEXT NOT NULL DEFAULT 'pending'
                    );
                    CREATE INDEX IF NOT EXISTS ix_resume_embedding_status ON resume_chunks(embedding_status);
                    """;
                await command.ExecuteNonQueryAsync(token);
                _initialized = true;
            }
            finally { _databaseGate.Release(); }
        }

        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken token)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(token);
            return connection;
        }

        private async Task<bool> TryEmbedAndUpdateAsync(string table, string id, string title, string text, CancellationToken token)
        {
            try
            {
                var embedding = await _client.EmbedRetrievalDocumentAsync(title, text, token);
                await UpdateEmbeddingAsync(table, id, embedding, token);
                return true;
            }
            catch when (!token.IsCancellationRequested) { return false; }
        }

        private async Task UpdateEmbeddingAsync(string table, string id, float[] embedding, CancellationToken token)
        {
            if (table is not ("memories" or "schedule_events" or "conversation_turns" or "resume_chunks"))
                throw new ArgumentOutOfRangeException(nameof(table));
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE {table} SET embedding_model = $model, embedding_dimensions = $dimensions, embedding = $embedding, embedding_status = 'ready' WHERE id = $id;";
                command.Parameters.AddWithValue("$model", EmbeddingModel);
                command.Parameters.AddWithValue("$dimensions", EmbeddingDimensions);
                command.Parameters.AddWithValue("$embedding", FloatsToBytes(embedding));
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(token);
            }
            finally { _databaseGate.Release(); }
        }

        private async Task RetryPendingEmbeddingsAsync(int maximum, CancellationToken token)
        {
            var pending = new List<(string Table, string Id, string Title, string Text)>();
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                foreach (var definition in new[]
                {
                    ("memories", "category || ': ' || subject_key", "content"),
                    ("schedule_events", "'schedule event'", "title || ' ' || COALESCE(location, '') || ' ' || COALESCE(notes, '')"),
                    ("conversation_turns", "'conversation'", "user_text || char(10) || assistant_text")
                })
                {
                    if (pending.Count >= maximum) break;
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT id, {definition.Item2}, {definition.Item3} FROM {definition.Item1} WHERE embedding_status = 'pending' LIMIT $limit;";
                    command.Parameters.AddWithValue("$limit", maximum - pending.Count);
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                        pending.Add((definition.Item1, Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!, reader.GetString(1), reader.GetString(2)));
                }
            }
            finally { _databaseGate.Release(); }
            foreach (var item in pending)
                await TryEmbedAndUpdateAsync(item.Table, item.Id, item.Title, item.Text, token);
        }

        private async Task EmbedPendingResumeChunksAsync(CancellationToken token)
        {
            List<(long Id, string Title, string Content)> chunks;
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                chunks = [];
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, section_title, content FROM resume_chunks WHERE embedding_status = 'pending';";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) chunks.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
            finally { _databaseGate.Release(); }
            foreach (var chunk in chunks)
                await TryEmbedAndUpdateAsync("resume_chunks", chunk.Id.ToString(CultureInfo.InvariantCulture), chunk.Title, chunk.Content, token);
        }

        private async Task<ToolResult> DeleteByIdAsync(string table, string id, string label, CancellationToken token)
        {
            if (table is not ("memories" or "schedule_events" or "conversation_turns"))
                throw new ArgumentOutOfRangeException(nameof(table));
            await EnsureInitializedAsync(token);
            await _databaseGate.WaitAsync(token);
            try
            {
                await using var connection = await OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table} WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                var changed = await command.ExecuteNonQueryAsync(token);
                return Result(changed > 0, changed > 0 ? "completed" : "not_found", changed > 0 ? $"Deleted the {label}." : $"The {label} was not found.");
            }
            finally { _databaseGate.Release(); }
        }

        private static async Task<SecretaryProfileContext> ReadProfileAsync(SqliteConnection connection, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT context_text, last_turn_id FROM profile_context WHERE id = 1;";
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token)
                ? new SecretaryProfileContext(reader.GetString(0), reader.GetInt64(1))
                : new SecretaryProfileContext(string.Empty, 0);
        }

        private static async Task<List<SecretaryConversationTurn>> ReadTurnsAsync(SqliteConnection connection, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, user_text, assistant_text, attachment_names, created_utc, embedding FROM conversation_turns ORDER BY id DESC;";
            return await ReadTurnRowsAsync(command, token);
        }

        private static async Task<List<SecretaryConversationTurn>> ReadTurnsAfterAsync(SqliteConnection connection, long id, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, user_text, assistant_text, attachment_names, created_utc, embedding FROM conversation_turns WHERE id > $id ORDER BY id;";
            command.Parameters.AddWithValue("$id", id);
            return await ReadTurnRowsAsync(command, token);
        }

        private static async Task<List<SecretaryConversationTurn>> ReadTurnRowsAsync(SqliteCommand command, CancellationToken token)
        {
            var values = new List<SecretaryConversationTurn>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                values.Add(new SecretaryConversationTurn(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5)));
            return values;
        }

        private static async Task<List<SecretaryMemory>> ReadMemoriesAsync(SqliteConnection connection, string? category, int limit, CancellationToken token)
        {
            var values = new List<SecretaryMemory>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, category, subject_key, content, importance, updated_utc, embedding
                FROM memories
                WHERE ($category IS NULL OR category = $category)
                ORDER BY importance DESC, updated_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$category", (object?)category ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                values.Add(new SecretaryMemory(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6)));
            return values;
        }

        private static async Task<List<SecretaryScheduleEvent>> ReadEventsAsync(
            SqliteConnection connection,
            DateTimeOffset? startUtc,
            DateTimeOffset? endUtc,
            int limit,
            CancellationToken token)
        {
            var values = new List<SecretaryScheduleEvent>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, title, start_utc, end_utc, timezone_id, is_all_day, location, notes, status, embedding
                FROM schedule_events
                WHERE ($start IS NULL OR start_utc >= $start)
                  AND ($end IS NULL OR start_utc <= $end)
                ORDER BY start_utc
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$start", startUtc is null ? DBNull.Value : startUtc.Value.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$end", endUtc is null ? DBNull.Value : endUtc.Value.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                values.Add(new SecretaryScheduleEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    reader.GetString(4),
                    reader.GetInt32(5) != 0,
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : (byte[])reader.GetValue(9)));
            return values;
        }

        private static async Task<List<SecretaryResumeChunk>> ReadResumeChunksAsync(SqliteConnection connection, CancellationToken token)
        {
            var values = new List<SecretaryResumeChunk>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, section_title, content, embedding FROM resume_chunks ORDER BY id;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                values.Add(new SecretaryResumeChunk(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3)));
            return values;
        }

        private static async Task<string?> ReadResumeTextAsync(SqliteConnection connection, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT resume_text FROM resume_profile WHERE id = 1;";
            return Convert.ToString(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }

        private static void AddEventParameters(SqliteCommand command, SecretaryScheduleEvent item)
        {
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$title", item.Title.Trim());
            command.Parameters.AddWithValue("$start", item.StartUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$end", item.EndUtc is null ? DBNull.Value : item.EndUtc.Value.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$timezone", item.TimeZoneId);
            command.Parameters.AddWithValue("$allDay", item.IsAllDay ? 1 : 0);
            command.Parameters.AddWithValue("$location", (object?)item.Location ?? DBNull.Value);
            command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", item.Status);
        }

        private static string EventEmbeddingText(SecretaryScheduleEvent item) =>
            $"{item.Title}; {item.StartUtc:O}; {item.TimeZoneId}; {item.Location}; {item.Notes}; {item.Status}";

        private static JsonObject MemoryJson(SecretaryMemory item) => new()
        {
            ["memory_id"] = item.Id,
            ["category"] = item.Category,
            ["subject_key"] = item.SubjectKey,
            ["content"] = item.Content,
            ["importance"] = item.Importance,
            ["updated_utc"] = item.UpdatedUtc
        };

        private static JsonObject EventJson(SecretaryScheduleEvent item) => new()
        {
            ["event_id"] = item.Id,
            ["title"] = item.Title,
            ["start_utc"] = item.StartUtc,
            ["end_utc"] = item.EndUtc,
            ["timezone_id"] = item.TimeZoneId,
            ["is_all_day"] = item.IsAllDay,
            ["location"] = item.Location,
            ["notes"] = item.Notes,
            ["status"] = item.Status
        };

        private static ToolResult Result(bool success, string status, string summary, JsonObject? details = null)
        {
            var root = details ?? new JsonObject();
            root.Insert(0, "summary", summary);
            root.Insert(0, "status", status);
            return new ToolResult(success, root.ToJsonString(), status);
        }

        private static double Score(string query, float[]? queryEmbedding, string text, byte[]? storedEmbedding)
        {
            if (queryEmbedding is not null && storedEmbedding is not null)
            {
                var candidate = BytesToFloats(storedEmbedding);
                if (candidate.Length == queryEmbedding.Length) return Cosine(queryEmbedding, candidate);
            }
            var terms = Tokenize(query);
            if (terms.Count == 0) return 0;
            var haystack = text.ToLowerInvariant();
            return terms.Count(term => haystack.Contains(term, StringComparison.Ordinal)) / (double)terms.Count;
        }

        private static double Cosine(float[] left, float[] right)
        {
            double dot = 0;
            double leftLength = 0;
            double rightLength = 0;
            for (var index = 0; index < left.Length; index++)
            {
                dot += left[index] * right[index];
                leftLength += left[index] * left[index];
                rightLength += right[index] * right[index];
            }
            return leftLength == 0 || rightLength == 0 ? 0 : dot / Math.Sqrt(leftLength * rightLength);
        }

        private static byte[] FloatsToBytes(float[] values)
        {
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float[] BytesToFloats(byte[] bytes)
        {
            var values = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        private static IReadOnlyList<string> Tokenize(string value) =>
            value.Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '/', '\\', '-', '_', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => item.Length > 2)
                .Select(item => item.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();

        private static string NormalizeKey(string value, string fallback)
        {
            var normalized = string.Join('_', Tokenize(value));
            return normalized.Length == 0 ? fallback : normalized[..Math.Min(normalized.Length, 100)];
        }

        private static void AppendSection(StringBuilder builder, string title, string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            builder.AppendLine($"\n{title}:");
            builder.AppendLine(content.Trim());
        }

        private static string Truncate(string value, int maximum) =>
            value.Length <= maximum ? value : $"{value[..Math.Max(0, maximum - 3)]}...";

        private static string LimitWords(string value, int maximumWords)
        {
            var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return words.Length <= maximumWords ? value.Trim() : string.Join(' ', words.Take(maximumWords));
        }

        private static List<(string Title, string Content)> SplitResume(string resumeText)
        {
            var chunks = new List<(string Title, string Content)>();
            var paragraphs = resumeText.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = new StringBuilder();
            var currentTitle = "Resume";
            foreach (var paragraph in paragraphs)
            {
                var isHeading = paragraph.Length <= 80 && !paragraph.Contains('\n') && paragraph.Count(char.IsLetter) > 2;
                if (isHeading && current.Length > 0)
                {
                    chunks.Add((currentTitle, current.ToString().Trim()));
                    current.Clear();
                    currentTitle = paragraph.Trim().TrimEnd(':');
                    continue;
                }
                if (current.Length + paragraph.Length > 3_000 && current.Length > 0)
                {
                    chunks.Add((currentTitle, current.ToString().Trim()));
                    current.Clear();
                }
                if (current.Length > 0) current.AppendLine().AppendLine();
                current.Append(paragraph);
            }
            if (current.Length > 0) chunks.Add((currentTitle, current.ToString().Trim()));
            if (chunks.Count == 0) chunks.Add(("Resume", resumeText.Trim()));
            return chunks;
        }

        private void Settings_Changed(object? sender, AppSettings settings)
        {
            _databasePath = System.IO.Path.Combine(settings.NotebookDataPath, "secretary.sqlite");
            _initialized = false;
        }

        public void Dispose()
        {
            if (_followsNotebookLocation) App.Settings.Changed -= Settings_Changed;
        }
    }
}
