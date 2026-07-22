using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace App.Services
{
    internal sealed class SecureSettingsService
    {
        private readonly string _path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings.json");

        public async Task<AppSettings> LoadAsync()
        {
            if (!File.Exists(_path)) return new AppSettings();
            try
            {
                await using var stream = File.OpenRead(_path);
                var stored = await JsonSerializer.DeserializeAsync<StoredSettings>(stream) ?? new StoredSettings();
                var apiKey = string.IsNullOrWhiteSpace(stored.ProtectedGeminiApiKey)
                    ? string.Empty
                    : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedGeminiApiKey), null, DataProtectionScope.CurrentUser));
                return new AppSettings { GeminiApiKey = apiKey, LastGeminiModel = stored.LastGeminiModel };
            }
            catch (Exception)
            {
                return new AppSettings();
            }
        }

        public async Task SaveAsync(AppSettings settings)
        {
            var protectedKey = string.IsNullOrWhiteSpace(settings.GeminiApiKey) ? string.Empty : Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.GeminiApiKey), null, DataProtectionScope.CurrentUser));
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, new StoredSettings
                {
                    ProtectedGeminiApiKey = protectedKey,
                    LastGeminiModel = settings.LastGeminiModel
                }, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporaryPath, _path, true);
        }

        private sealed class StoredSettings
        {
            public string ProtectedGeminiApiKey { get; set; } = string.Empty;
            public string LastGeminiModel { get; set; } = string.Empty;
        }
    }

    internal sealed class AppSettings
    {
        public string GeminiApiKey { get; set; } = string.Empty;
        public string LastGeminiModel { get; set; } = string.Empty;
    }
}
