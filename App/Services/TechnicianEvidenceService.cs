using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using App.Models;

namespace App.Services
{
    /// <summary>
    /// Builds the TASK/DONE/EVIDENCE/OPEN handoff the research planner needs, so the planner sees
    /// what was already asked, tried, and measured instead of only the last reply.
    /// </summary>
    internal static class TechnicianEvidenceService
    {
        private const int MaximumRequestCharacters = 700;
        private const int MaximumResponseCharacters = 500;
        private const int MaximumEvidenceItems = 8;
        private const int MaximumEvidenceOutputCharacters = 320;

        public static string BuildHandoff(IReadOnlyList<ChatMessage> messages, int maximumCharacters)
        {
            var requests = messages.Where(message => message.Kind == ChatItemKind.User).ToList();
            var task = requests.Count > 0 ? Shorten(requests[0].Content, MaximumRequestCharacters) : "Not stated.";
            var open = requests.Count > 1 ? Shorten(requests[^1].Content, MaximumRequestCharacters) : "None beyond the original request.";
            var done = messages.LastOrDefault(message => message.Kind == ChatItemKind.Assistant)?.Content is { Length: > 0 } reply
                ? Shorten(reply, MaximumResponseCharacters)
                : "No conclusion reached yet.";

            var evidence = messages
                .Where(message => message.Kind == ChatItemKind.Tool)
                .Reverse()
                .Take(MaximumEvidenceItems)
                .Reverse()
                .Select(DescribeToolMessage)
                .Where(line => line.Length > 0)
                .ToList();

            // Oldest evidence is dropped first, because the newest measurements decide the next step.
            while (true)
            {
                var handoff = Compose(task, done, evidence, open);
                if (handoff.Length <= maximumCharacters || evidence.Count == 0) return handoff;
                evidence.RemoveAt(0);
            }
        }

        private static string Compose(string task, string done, IReadOnlyList<string> evidence, string open)
        {
            var builder = new StringBuilder();
            builder.AppendLine("TASK").AppendLine($"- {task}").AppendLine();
            builder.AppendLine("DONE").AppendLine($"- {done}").AppendLine();
            builder.AppendLine("EVIDENCE");
            if (evidence.Count == 0) builder.AppendLine("- No local diagnostics have been run yet.");
            else foreach (var line in evidence) builder.AppendLine($"- {line}");
            builder.AppendLine();
            builder.AppendLine("OPEN").AppendLine($"- {open}");
            return builder.ToString().TrimEnd();
        }

        private static string DescribeToolMessage(ChatMessage message)
        {
            var arguments = message.ToolArguments;
            var subject = Value(arguments, "command_line")
                ?? Value(arguments, "absolute_file_path")
                ?? Value(arguments, "absolute_directory_path")
                ?? Value(arguments, "query")
                ?? Value(arguments, "context_type")
                ?? string.Empty;
            var outcome = DescribeToolOutput(message);
            var status = message.ToolSucceeded == false ? "failed" : "ok";
            var subjectText = subject.Length == 0 ? string.Empty : $" `{Shorten(subject, 160)}`";
            return $"{message.Title}{subjectText} → {status}{(outcome.Length == 0 ? string.Empty : $": {outcome}")}";
        }

        private static string DescribeToolOutput(ChatMessage message)
        {
            JsonObject? output;
            try { output = JsonNode.Parse(message.Content) as JsonObject; }
            catch (JsonException) { return Shorten(message.Content, MaximumEvidenceOutputCharacters); }
            if (output is null) return Shorten(message.Content, MaximumEvidenceOutputCharacters);

            var parts = new List<string>();
            if (output["return_code"] is JsonValue code) parts.Add($"exit {code}");
            foreach (var field in new[] { "error", "stderr", "stdout", "answer", "summary" })
            {
                var text = Value(output, field);
                if (string.IsNullOrWhiteSpace(text)) continue;
                parts.Add($"{field}: {Shorten(text, MaximumEvidenceOutputCharacters)}");
                break;
            }
            return string.Join(", ", parts);
        }

        private static string? Value(JsonObject? source, string name) =>
            source?[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null;

        private static string Shorten(string value, int maximumLength)
        {
            var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return collapsed.Length <= maximumLength ? collapsed : collapsed[..maximumLength].TrimEnd() + "…";
        }
    }
}
