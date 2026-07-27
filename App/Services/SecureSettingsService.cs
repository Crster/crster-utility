using App.Models;
using LiteDB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class SecureSettingsService : IDisposable
    {
        private const string SettingsDirectoryName = "crster\\utility";
        private readonly string _path;
        private LiteDatabaseService? _database;

        public SecureSettingsService()
        {
            _path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SettingsDirectoryName, "setting.ini");
        }

        public string Path => _path;
        public bool IsConfigured { get; private set; }
        public AppSettings Current { get; private set; } = AppSettings.CreateDefault();
        public LiteDatabaseService Database => _database ?? throw new InvalidOperationException("Application storage has not been configured.");
        public event EventHandler<AppSettings>? Changed;

        public AppSettings Load()
        {
            IsConfigured = false;
            if (!File.Exists(_path)) return Current = AppSettings.CreateDefault();
            try
            {
                var values = Parse(File.ReadAllLines(_path));
                var folder = Read(values, "Storage.DatabaseFolder");
                var apiKey = Read(values, "Gemini.ApiKey");
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(apiKey)) return Current = AppSettings.CreateDefault();
                Initialize(folder, apiKey);
            }
            catch
            {
                _database?.Dispose();
                _database = null;
                Current = AppSettings.CreateDefault();
            }
            return Current;
        }

        public Task<AppSettings> LoadAsync() => Task.FromResult(Load());

        public void Configure(string databaseFolder, string geminiApiKey)
        {
            databaseFolder = System.IO.Path.GetFullPath(databaseFolder.Trim());
            geminiApiKey = geminiApiKey.Trim();
            if (geminiApiKey.Length == 0) throw new InvalidOperationException("A Gemini API key is required.");
            Directory.CreateDirectory(databaseFolder);
            Initialize(databaseFolder, geminiApiKey);
            WriteBootstrap(databaseFolder, geminiApiKey);
        }

        private void Initialize(string databaseFolder, string geminiApiKey)
        {
            _database?.Dispose();
            _database = new LiteDatabaseService(System.IO.Path.Combine(databaseFolder, "CrsterUtility.db"));
            SeedSettings(_database.Settings);
            Current = AppSettings.FromDatabase(databaseFolder, geminiApiKey, _database.Settings);
            IsConfigured = true;
        }

        public void Save(AppSettings settings)
        {
            if (!IsConfigured) throw new InvalidOperationException("Application storage has not been configured.");
            if (!string.Equals(Current.DatabaseFolder, settings.DatabaseFolder, StringComparison.OrdinalIgnoreCase))
                MoveDatabase(settings.DatabaseFolder);

            var collection = Database.Settings;
            foreach (var definition in AppSettings.Definitions)
            {
                var document = collection.FindById(definition.Key)
                    ?? throw new InvalidOperationException($"Setting '{definition.Key}' is missing from application storage.");
                document.Value = definition.Read(settings);
                collection.Update(definition.Key, document);
            }
            WriteBootstrap(settings.DatabaseFolder, settings.GeminiApiKey);
            var embeddingModelChanged = !string.Equals(Current.EmbeddingModel, settings.EmbeddingModel, StringComparison.OrdinalIgnoreCase);
            Current = settings;
            Changed?.Invoke(this, settings);
            if (embeddingModelChanged) EmbeddingMaintenanceService.InvalidateAndRebuild();
        }

        public Task SaveAsync(AppSettings settings) { Save(settings); return Task.CompletedTask; }

        public void Reset(string key)
        {
            var document = Database.Settings.FindById(key) ?? throw new KeyNotFoundException($"Unknown setting '{key}'.");
            document.Value = document.Default;
            Database.Settings.Update(key, document);
            Current = AppSettings.FromDatabase(Current.DatabaseFolder, Current.GeminiApiKey, Database.Settings);
            Changed?.Invoke(this, Current);
        }

        private void MoveDatabase(string destinationFolder)
        {
            destinationFolder = System.IO.Path.GetFullPath(destinationFolder);
            Directory.CreateDirectory(destinationFolder);
            var sourcePath = Database.Path;
            var destinationPath = System.IO.Path.Combine(destinationFolder, "CrsterUtility.db");
            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(destinationPath)) throw new IOException("The selected folder already contains CrsterUtility.db.");
            _database?.Dispose();
            try
            {
                File.Copy(sourcePath, destinationPath, false);
                using (var probe = new LiteDatabaseService(destinationPath)) _ = probe.Settings.Count();
                _database = new LiteDatabaseService(destinationPath);
            }
            catch
            {
                _database = new LiteDatabaseService(sourcePath);
                throw;
            }
        }

        private static void SeedSettings(ILiteCollection<SettingDocument> collection)
        {
            MigrateTextSetting(collection, "secretary.city", "general.city");
            MigrateTextSetting(collection, "secretary.country", "general.country");
            foreach (var definition in AppSettings.Definitions)
                if (collection.FindById(definition.Key) is null)
                    collection.Insert(new SettingDocument { Id = definition.Key, Name = definition.Name, Value = definition.Default, Default = definition.Default });
        }

        private static void MigrateTextSetting(ILiteCollection<SettingDocument> collection, string oldKey, string newKey)
        {
            if (collection.FindById(newKey) is not null) return;
            var old = collection.FindById(oldKey);
            if (old?.Value is not { IsString: true } value || string.IsNullOrWhiteSpace(value.AsString)) return;
            collection.Insert(new SettingDocument { Id = newKey, Name = old.Name, Value = value.AsString, Default = old.Default });
        }

        private void WriteBootstrap(string databaseFolder, string apiKey)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllLines(temporaryPath, ["[Storage]", $"DatabaseFolder={databaseFolder}", "", "[Gemini]", $"ApiKey={apiKey}"]);
            File.Move(temporaryPath, _path, true);
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
                if (separator > 0) result[$"{section}.{line[..separator].Trim()}"] = line[(separator + 1)..].Trim();
            }
            return result;
        }

        private static string Read(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) ? value : string.Empty;
        public void Dispose() => _database?.Dispose();
    }

    internal sealed record SettingDefinition(string Key, string Name, BsonValue Default, Func<AppSettings, BsonValue> Read);

    internal sealed class AppSettings
    {
        internal static readonly IReadOnlyList<SettingDefinition> Definitions =
        [
            new("general.startWithWindows", "Start with Windows", false, value => value.StartWithWindows),
            new("snapshot.shortcut", "Snapshot shortcut", "PrintScreen", value => value.SnapshotShortcut),
            new("snapshot.captureMouseCursor", "Capture mouse cursor", true, value => value.SnapshotCaptureMouseCursor),
            new("recording.microphoneDeviceId", "Recording microphone", "", value => value.RecordingMicrophoneDeviceId),
            new("caffeine.shortcut", "Caffeine shortcut", "Ctrl+Shift+Alt+F12", value => value.CaffeineShortcut),
            new("gemini.lastChatPersonality", "Last chat personality", "Secretary", value => value.LastChatPersonality),
            new("gemini.embeddingModel", "Embedding model", "gemini-embedding-001", value => value.EmbeddingModel),
            new("gemini.lowCostModel", "Low cost model", "gemini-2.5-flash-lite", value => value.LowCostModel),
            new("gemini.highCostModel", "High cost model", "gemini-3.5-flash", value => value.HighCostModel),
            new("gemini.artistModel", "Artist model", "gemini-3.1-flash-image", value => value.ArtistModel),
            new("technician.workspace", "Technician workspace", "", value => value.TechnicianWorkspace),
            new("general.city", "City", "Manila", value => value.City),
            new("general.country", "Country", "Philippines", value => value.Country)
        ];

        public string DatabaseFolder { get; set; } = string.Empty;
        public string NotebookDataPath { get => DatabaseFolder; set => DatabaseFolder = value; }
        public string GeminiApiKey { get; set; } = string.Empty;
        public bool StartWithWindows { get; set; }
        public string SnapshotShortcut { get; set; } = "PrintScreen";
        public bool SnapshotCaptureMouseCursor { get; set; } = true;
        public string RecordingMicrophoneDeviceId { get; set; } = string.Empty;
        public string CaffeineShortcut { get; set; } = "Ctrl+Shift+Alt+F12";
        public string LastChatPersonality { get; set; } = "Secretary";
        public string EmbeddingModel { get; set; } = "gemini-embedding-001";
        public string LowCostModel { get; set; } = "gemini-2.5-flash-lite";
        public string HighCostModel { get; set; } = "gemini-3.5-flash";
        public string ArtistModel { get; set; } = "gemini-3.1-flash-image";
        public string TechnicianWorkspace { get; set; } = string.Empty;
        public string City { get; set; } = "Manila";
        public string Country { get; set; } = "Philippines";

        public static AppSettings CreateDefault() => new()
        {
            DatabaseFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "crster", "utility")
        };

        internal static AppSettings FromDatabase(string folder, string apiKey, ILiteCollection<SettingDocument> collection)
        {
            var result = CreateDefault();
            result.DatabaseFolder = folder;
            result.GeminiApiKey = apiKey;
            result.StartWithWindows = Bool("general.startWithWindows", result.StartWithWindows);
            result.SnapshotShortcut = Text("snapshot.shortcut", result.SnapshotShortcut);
            result.SnapshotCaptureMouseCursor = Bool("snapshot.captureMouseCursor", result.SnapshotCaptureMouseCursor);
            result.RecordingMicrophoneDeviceId = Text("recording.microphoneDeviceId", result.RecordingMicrophoneDeviceId);
            result.CaffeineShortcut = Text("caffeine.shortcut", result.CaffeineShortcut);
            result.LastChatPersonality = Text("gemini.lastChatPersonality", result.LastChatPersonality);
            result.EmbeddingModel = Text("gemini.embeddingModel", result.EmbeddingModel);
            result.LowCostModel = Text("gemini.lowCostModel", result.LowCostModel);
            result.HighCostModel = Text("gemini.highCostModel", result.HighCostModel);
            result.ArtistModel = Text("gemini.artistModel", result.ArtistModel);
            result.TechnicianWorkspace = Text("technician.workspace", result.TechnicianWorkspace);
            result.City = Text("general.city", result.City);
            result.Country = Text("general.country", result.Country);
            return result;

            string Text(string key, string fallback) => collection.FindById(key)?.Value is { IsString: true } value ? value.AsString : fallback;
            bool Bool(string key, bool fallback) => collection.FindById(key)?.Value is { IsBoolean: true } value ? value.AsBoolean : fallback;
        }

        public AppSettings Clone() => (AppSettings)MemberwiseClone();
    }
}
