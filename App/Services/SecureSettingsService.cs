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
                var baseUrl = Read(values, "OpenAICompatible.BaseUrl");
                var apiKey = Read(values, "OpenAICompatible.ApiKey");
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return Current = AppSettings.CreateDefault();
                Initialize(folder, baseUrl, apiKey);
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

        public void Configure(string databaseFolder, string openAiCompatibleBaseUrl, string openAiCompatibleApiKey)
        {
            databaseFolder = System.IO.Path.GetFullPath(databaseFolder.Trim());
            openAiCompatibleBaseUrl = ValidateBaseUrl(openAiCompatibleBaseUrl);
            openAiCompatibleApiKey = openAiCompatibleApiKey.Trim();
            if (openAiCompatibleApiKey.Length == 0) throw new InvalidOperationException("An API key is required.");
            Directory.CreateDirectory(databaseFolder);
            Initialize(databaseFolder, openAiCompatibleBaseUrl, openAiCompatibleApiKey);
            WriteBootstrap(databaseFolder, openAiCompatibleBaseUrl, openAiCompatibleApiKey);
        }

        private void Initialize(string databaseFolder, string openAiCompatibleBaseUrl, string openAiCompatibleApiKey)
        {
            _database?.Dispose();
            _database = new LiteDatabaseService(System.IO.Path.Combine(databaseFolder, "CrsterUtility.db"));
            SeedSettings(_database.Settings);
            Current = AppSettings.FromDatabase(databaseFolder, openAiCompatibleBaseUrl, openAiCompatibleApiKey, _database.Settings);
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
            settings.OpenAiCompatibleBaseUrl = ValidateBaseUrl(settings.OpenAiCompatibleBaseUrl);
            WriteBootstrap(settings.DatabaseFolder, settings.OpenAiCompatibleBaseUrl, settings.OpenAiCompatibleApiKey);
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
            Current = AppSettings.FromDatabase(Current.DatabaseFolder, Current.OpenAiCompatibleBaseUrl, Current.OpenAiCompatibleApiKey, Database.Settings);
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

        private void WriteBootstrap(string databaseFolder, string baseUrl, string apiKey)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllLines(temporaryPath, ["[Storage]", $"DatabaseFolder={databaseFolder}", "", "[OpenAICompatible]", $"BaseUrl={baseUrl}", $"ApiKey={apiKey}"]);
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
        private static string ValidateBaseUrl(string value)
        {
            value = value.Trim().TrimEnd('/');
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                throw new InvalidOperationException("Enter a valid HTTP or HTTPS OpenAI-compatible URL.");
            return value;
        }
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
            new("ai.lastChatPersonality", "Last chat personality", "Secretary", value => value.LastChatPersonality),
            new("ai.embeddingModel", "Embedding model", "text-embedding-3-small", value => value.EmbeddingModel),
            new("ai.lowCostModel", "Low cost model", "gpt-4.1-mini", value => value.LowCostModel),
            new("ai.highCostModel", "High cost model", "gpt-4.1", value => value.HighCostModel),
            new("ai.artistModel", "Artist model", "gpt-image-2", value => value.ArtistModel),
            new("technician.workspace", "Technician workspace", "", value => value.TechnicianWorkspace),
            new("cody.workspace", "Cody workspace", "", value => value.CodyWorkspace),
            new("cody.agentProvider", "Cody agent provider", "Codex", value => value.CodyAgentProvider),
            new("general.city", "City", "Manila", value => value.City),
            new("general.country", "Country", "Philippines", value => value.Country)
        ];

        public string DatabaseFolder { get; set; } = string.Empty;
        public string NotebookDataPath { get => DatabaseFolder; set => DatabaseFolder = value; }
        public string OpenAiCompatibleApiKey { get; set; } = string.Empty;
        public string OpenAiCompatibleBaseUrl { get; set; } = "https://api.openai.com/v1";
        public bool StartWithWindows { get; set; }
        public string SnapshotShortcut { get; set; } = "PrintScreen";
        public bool SnapshotCaptureMouseCursor { get; set; } = true;
        public string RecordingMicrophoneDeviceId { get; set; } = string.Empty;
        public string CaffeineShortcut { get; set; } = "Ctrl+Shift+Alt+F12";
        public string LastChatPersonality { get; set; } = "Secretary";
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";
        public string LowCostModel { get; set; } = "gpt-4.1-mini";
        public string HighCostModel { get; set; } = "gpt-4.1";
        public string ArtistModel { get; set; } = "gpt-image-2";
        public string TechnicianWorkspace { get; set; } = string.Empty;
        public string CodyWorkspace { get; set; } = string.Empty;
        public string CodyAgentProvider { get; set; } = "Codex";
        public string City { get; set; } = "Manila";
        public string Country { get; set; } = "Philippines";

        public static AppSettings CreateDefault() => new()
        {
            DatabaseFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "crster", "utility")
        };

        internal static AppSettings FromDatabase(string folder, string baseUrl, string apiKey, ILiteCollection<SettingDocument> collection)
        {
            var result = CreateDefault();
            result.DatabaseFolder = folder;
            result.OpenAiCompatibleApiKey = apiKey;
            result.OpenAiCompatibleBaseUrl = baseUrl;
            result.StartWithWindows = Bool("general.startWithWindows", result.StartWithWindows);
            result.SnapshotShortcut = Text("snapshot.shortcut", result.SnapshotShortcut);
            result.SnapshotCaptureMouseCursor = Bool("snapshot.captureMouseCursor", result.SnapshotCaptureMouseCursor);
            result.RecordingMicrophoneDeviceId = Text("recording.microphoneDeviceId", result.RecordingMicrophoneDeviceId);
            result.CaffeineShortcut = Text("caffeine.shortcut", result.CaffeineShortcut);
            result.LastChatPersonality = Text("ai.lastChatPersonality", result.LastChatPersonality);
            result.EmbeddingModel = Text("ai.embeddingModel", result.EmbeddingModel);
            result.LowCostModel = Text("ai.lowCostModel", result.LowCostModel);
            result.HighCostModel = Text("ai.highCostModel", result.HighCostModel);
            result.ArtistModel = Text("ai.artistModel", result.ArtistModel);
            if (string.Equals(result.ArtistModel, "gpt-image-1", StringComparison.OrdinalIgnoreCase))
                result.ArtistModel = "gpt-image-2";
            result.TechnicianWorkspace = Text("technician.workspace", result.TechnicianWorkspace);
            result.CodyWorkspace = Text("cody.workspace", result.CodyWorkspace);
            result.CodyAgentProvider = Text("cody.agentProvider", result.CodyAgentProvider);
            result.City = Text("general.city", result.City);
            result.Country = Text("general.country", result.Country);
            return result;

            string Text(string key, string fallback) => collection.FindById(key)?.Value is { IsString: true } value ? value.AsString : fallback;
            bool Bool(string key, bool fallback) => collection.FindById(key)?.Value is { IsBoolean: true } value ? value.AsBoolean : fallback;
        }

        public AppSettings Clone() => (AppSettings)MemberwiseClone();
    }
}
