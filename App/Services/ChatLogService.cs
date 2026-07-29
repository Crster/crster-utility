using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using App.Models;
using Windows.Storage;

namespace App.Services
{
    internal sealed class ChatLogService
    {
#if DEBUG
        private const bool IsEnabled = true;
#else
        private const bool IsEnabled = false;
#endif
        private static readonly SemaphoreSlim WriteLock = new(1, 1);
        private readonly string _directory = IsEnabled ? ResolveDirectory() : string.Empty;

        public string Path => ResolvePath(ChatPersonality.Technician);

        private static string ResolveDirectory()
        {
            try
            {
                return System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "logs");
            }
            catch
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "crster",
                    "utility",
                    "logs");
            }
        }

        public async Task WriteAsync(string eventName, params (string Name, object? Value)[] properties)
        {
            if (!IsEnabled) return;
            var details = string.Join(" ", properties.Select(property => $"{property.Name}={Normalize(property.Value)}"));
            var line = $"{DateTimeOffset.UtcNow:O} {eventName}{(details.Length == 0 ? string.Empty : $" {details}")}{Environment.NewLine}";
            await AppendAsync(ResolvePersonality(eventName, properties), line);
        }

        public async Task WriteJsonAsync(string eventName, JsonObject payload)
        {
            if (!IsEnabled) return;
            var line = $"{DateTimeOffset.UtcNow:O} {eventName} {payload.ToJsonString()}{Environment.NewLine}";
            await AppendAsync(ResolvePersonality(eventName, []), line);
        }

        public async Task WriteJsonAsync(ChatPersonality personality, string eventName, JsonObject payload)
        {
            if (!IsEnabled) return;
            var line = $"{DateTimeOffset.UtcNow:O} {eventName} {payload.ToJsonString()}{Environment.NewLine}";
            await AppendAsync(personality, line);
        }

        private string ResolvePath(ChatPersonality? personality) =>
            System.IO.Path.Combine(_directory, $"{personality?.ToString().ToLowerInvariant() ?? "system"}.log");

        private async Task AppendAsync(ChatPersonality? personality, string line)
        {
            await WriteLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_directory);
                await File.AppendAllTextAsync(ResolvePath(personality), line);
            }
            catch
            {
                // Diagnostics must never interrupt the chat workflow.
            }
            finally
            {
                WriteLock.Release();
            }
        }

        private static ChatPersonality? ResolvePersonality(
            string eventName,
            (string Name, object? Value)[] properties)
        {
            var supplied = properties.FirstOrDefault(property =>
                property.Name.Equals("personality", StringComparison.OrdinalIgnoreCase)).Value;
            if (supplied is ChatPersonality personality) return personality;
            if (Enum.TryParse<ChatPersonality>(supplied?.ToString(), true, out var parsed)) return parsed;
            if (eventName.StartsWith("technician.", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("project_context.", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("tool_budget.", StringComparison.OrdinalIgnoreCase))
                return ChatPersonality.Technician;
            return null;
        }

        private static string Normalize(object? value)
        {
            var text = value?.ToString() ?? "null";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 500 ? text : text[..500];
        }
    }
}
