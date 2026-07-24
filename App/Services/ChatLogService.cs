using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class ChatLogService
    {
        private static readonly SemaphoreSlim WriteLock = new(1, 1);
        private readonly string _path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "crster",
            "utility",
            "logs",
            "chat.log");

        public string Path => _path;

        public async Task WriteAsync(string eventName, params (string Name, object? Value)[] properties)
        {
            var details = string.Join(" ", properties.Select(property => $"{property.Name}={Normalize(property.Value)}"));
            var line = $"{DateTimeOffset.UtcNow:O} {eventName}{(details.Length == 0 ? string.Empty : $" {details}")}{Environment.NewLine}";

            await WriteLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                await File.AppendAllTextAsync(_path, line);
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

        private static string Normalize(object? value)
        {
            var text = value?.ToString() ?? "null";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 500 ? text : text[..500];
        }
    }
}
