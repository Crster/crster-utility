using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class SecureSettingsService
    {
        private const string SettingsDirectoryName = "crster\\utility";
        private readonly string _path;

        public SecureSettingsService()
        {
            _path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SettingsDirectoryName, "setting.ini");
        }

        public string Path => _path;
        public AppSettings Current { get; private set; } = AppSettings.CreateDefault();
        public event EventHandler<AppSettings>? Changed;

        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    Current = AppSettings.CreateDefault();
                    Save(Current);
                    return Current;
                }

                var values = Parse(File.ReadAllLines(_path));
                Current = AppSettings.FromValues(values);
            }
            catch
            {
                Current = AppSettings.CreateDefault();
            }
            return Current;
        }

        public Task<AppSettings> LoadAsync() => Task.FromResult(Load());

        public void Save(AppSettings settings)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllLines(temp, settings.ToIniLines());
            File.Move(temp, _path, true);
            Current = settings;
            Changed?.Invoke(this, settings);
        }

        public Task SaveAsync(AppSettings settings)
        {
            Save(settings);
            return Task.CompletedTask;
        }

        private static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var section = string.Empty;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                result[$"{section}.{line[..separator].Trim()}"] = line[(separator + 1)..].Trim();
            }
            return result;
        }
    }

    internal sealed class AppSettings
    {
        public bool StartWithWindows { get; set; }
        public string NotebookDataPath { get; set; } = string.Empty;
        public string GeminiApiKey { get; set; } = string.Empty;
        public string SnapshotShortcut { get; set; } = "PrintScreen";
        public bool SnapshotCaptureMouseCursor { get; set; } = true;
        public string RecordingMicrophoneDeviceId { get; set; } = string.Empty;
        public string CaffeineShortcut { get; set; } = "Ctrl+Shift+Alt+F12";
        public string LastGeminiModel { get; set; } = string.Empty;
        public string LastChatPersonality { get; set; } = "Smart";

        public static AppSettings CreateDefault() => new()
        {
            NotebookDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "crster", "utility", "notebook")
        };

        public static AppSettings FromValues(IReadOnlyDictionary<string, string> values)
        {
            var settings = CreateDefault();
            settings.StartWithWindows = ReadBool("General.StartWithWindows", settings.StartWithWindows);
            settings.NotebookDataPath = Read("Notebook.DataPath", settings.NotebookDataPath);
            settings.GeminiApiKey = Read("Gemini.ApiKey", settings.GeminiApiKey);
            settings.LastGeminiModel = Read("Gemini.LastModel", settings.LastGeminiModel);
            settings.LastChatPersonality = Read("Gemini.LastChatPersonality", settings.LastChatPersonality);
            settings.SnapshotShortcut = Read("Snapshot.Shortcut", settings.SnapshotShortcut);
            settings.SnapshotCaptureMouseCursor = ReadBool("Snapshot.CaptureMouseCursor", settings.SnapshotCaptureMouseCursor);
            settings.RecordingMicrophoneDeviceId = Read("Recording.MicrophoneDeviceId", settings.RecordingMicrophoneDeviceId);
            settings.CaffeineShortcut = Read("Caffeine.Shortcut", settings.CaffeineShortcut);
            return settings;

            string Read(string key, string fallback) => values.TryGetValue(key, out var value) ? value : fallback;
            bool ReadBool(string key, bool fallback) => values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        public AppSettings Clone() => (AppSettings)MemberwiseClone();

        public IEnumerable<string> ToIniLines() =>
        [
            "[General]", $"StartWithWindows={StartWithWindows}", "",
            "[Notebook]", $"DataPath={NotebookDataPath}", "",
            "[Gemini]", $"ApiKey={GeminiApiKey}", $"LastModel={LastGeminiModel}", $"LastChatPersonality={LastChatPersonality}", "",
            "[Snapshot]", $"Shortcut={SnapshotShortcut}", $"CaptureMouseCursor={SnapshotCaptureMouseCursor}", "",
            "[Recording]", $"MicrophoneDeviceId={RecordingMicrophoneDeviceId}", "",
            "[Caffeine]", $"Shortcut={CaffeineShortcut}"
        ];
    }
}
