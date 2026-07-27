using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using App.Models;
using App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private bool _loading;
        private AppSettings _settings = null!;

        private sealed record MicrophoneChoice(string Id, string Name)
        {
            public override string ToString() => Name;
        }
        private sealed record ShortcutChoice(string Value, string Name);
        private sealed record ModelChoice(string Id, string Name)
        {
            public override string ToString() => Name;
        }
        private static readonly IReadOnlyList<ShortcutChoice> SnapshotShortcuts =
        [
            new(string.Empty, "None"), new("PrintScreen", "PrintScreen"), new("Alt+PrintScreen", "Alt + PrintScreen"), new("Ctrl+Shift+3", "Ctrl + Shift + 3"),
            new("Alt+P", "Alt + P"), new("Ctrl+Shift+Alt+]", "Ctrl + Shift + Alt + ]")
        ];
        private static readonly IReadOnlyList<ShortcutChoice> CaffeineShortcuts =
        [
            new(string.Empty, "None"), new("Ctrl+Shift+Alt+F12", "Ctrl + Shift + Alt + F12"), new("Ctrl+Shift+Alt+[", "Ctrl + Shift + Alt + [")
        ];

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _loading = true;
            _settings = App.Settings.Current.Clone();
            StartupToggle.IsOn = _settings.StartWithWindows;
            NotebookPathBox.Text = _settings.DatabaseFolder;
            GeminiApiKeyBox.Password = _settings.GeminiApiKey;
            CityBox.Text = _settings.City;
            CountryBox.Text = _settings.Country;
            SnapshotShortcutBox.ItemsSource = SnapshotShortcuts;
            SnapshotShortcutBox.SelectedItem = FindShortcut(SnapshotShortcuts, _settings.SnapshotShortcut);
            CursorToggle.IsOn = _settings.SnapshotCaptureMouseCursor;
            CaffeineShortcutBox.ItemsSource = CaffeineShortcuts;
            CaffeineShortcutBox.SelectedItem = FindShortcut(CaffeineShortcuts, _settings.CaffeineShortcut);
            await LoadMicrophonesAsync();
            await LoadModelsAsync();
            _loading = false;
        }

        private async Task LoadModelsAsync()
        {
            SetModelBoxesEnabled(false);
            try
            {
                using var client = new GeminiClient(_settings.GeminiApiKey);
                var models = await client.ListModelsAsync(System.Threading.CancellationToken.None);
                SetModelChoices(EmbeddingModelBox, models.Where(model => model.SupportsEmbedding), _settings.EmbeddingModel);
                SetModelChoices(LowCostModelBox, models.Where(model => model.SupportsChat && !model.SupportsImageGeneration), _settings.LowCostModel);
                SetModelChoices(HighCostModelBox, models.Where(model => model.SupportsChat && !model.SupportsImageGeneration), _settings.HighCostModel);
                SetModelChoices(ArtistModelBox, models.Where(model => model.SupportsImageGeneration), _settings.ArtistModel);
                SetModelBoxesEnabled(true);
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Gemini models could not be loaded: {exception.Message}";
            }
        }

        private static void SetModelChoices(ComboBox box, IEnumerable<GeminiModel> models, string selectedId)
        {
            var choices = models.Select(model => new ModelChoice(model.Id, string.IsNullOrWhiteSpace(model.Description) ? model.DisplayName : $"{model.DisplayName} — {model.Description}"))
                .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (!choices.Any(choice => string.Equals(choice.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
                choices.Insert(0, new ModelChoice(selectedId, $"{selectedId} (saved model; unavailable)"));
            box.ItemsSource = choices;
            box.SelectedItem = choices.First(choice => string.Equals(choice.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }

        private void SetModelBoxesEnabled(bool enabled)
        {
            EmbeddingModelBox.IsEnabled = enabled;
            LowCostModelBox.IsEnabled = enabled;
            HighCostModelBox.IsEnabled = enabled;
            ArtistModelBox.IsEnabled = enabled;
        }

        private async Task LoadMicrophonesAsync()
        {
            var choices = new List<MicrophoneChoice>
            {
                new(string.Empty, "None"),
                new(ScreenRecorderService.DefaultMicrophoneDeviceId, "Default")
            };
            try
            {
                var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
                choices.AddRange(devices
                    .Where(device => device.IsEnabled)
                    .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(device => new MicrophoneChoice(device.Id, device.Name)));
            }
            catch { StatusText.Text = "Microphones could not be enumerated."; }
            MicrophoneBox.ItemsSource = choices;
            MicrophoneBox.SelectedItem = choices.FirstOrDefault(choice => choice.Id == _settings.RecordingMicrophoneDeviceId) ?? choices[0];
        }

        private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var requested = StartupToggle.IsOn;
            try
            {
                if (!await StartupService.SetEnabledAsync(requested)) throw new InvalidOperationException("Windows did not allow the startup setting to change.");
                Save(settings => settings.StartWithWindows = requested);
            }
            catch (Exception exception)
            {
                _loading = true; StartupToggle.IsOn = _settings.StartWithWindows; _loading = false;
                StatusText.Text = exception.Message;
            }
        }

        private async void NotebookBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is null) return;
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var path = folder.Path;
            try
            {
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_settings.DatabaseFolder), StringComparison.OrdinalIgnoreCase)) return;
                Save(settings => settings.DatabaseFolder = path);
                NotebookPathBox.Text = path;
            }
            catch (Exception exception) { StatusText.Text = $"Database move failed: {exception.Message}"; }
        }

        private void GeminiApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) { if (!_loading) Save(settings => settings.GeminiApiKey = GeminiApiKeyBox.Password.Trim()); }
        private void EmbeddingModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && EmbeddingModelBox.SelectedItem is ModelChoice choice) Save(settings => settings.EmbeddingModel = choice.Id); }
        private void LowCostModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && LowCostModelBox.SelectedItem is ModelChoice choice) Save(settings => settings.LowCostModel = choice.Id); }
        private void HighCostModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && HighCostModelBox.SelectedItem is ModelChoice choice) Save(settings => settings.HighCostModel = choice.Id); }
        private void ArtistModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && ArtistModelBox.SelectedItem is ModelChoice choice) Save(settings => settings.ArtistModel = choice.Id); }
        private void LocationBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Save(settings =>
            {
                settings.City = CityBox.Text.Trim();
                settings.Country = CountryBox.Text.Trim();
            });
        }
        private void CursorToggle_Toggled(object sender, RoutedEventArgs e) { if (!_loading) Save(settings => settings.SnapshotCaptureMouseCursor = CursorToggle.IsOn); }
        private void MicrophoneBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && MicrophoneBox.SelectedItem is MicrophoneChoice choice) Save(settings => settings.RecordingMicrophoneDeviceId = choice.Id); }
        private void SnapshotShortcutBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading && SnapshotShortcutBox.SelectedItem is ShortcutChoice choice) Save(settings => settings.SnapshotShortcut = choice.Value);
        }
        private void CaffeineShortcutBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading && CaffeineShortcutBox.SelectedItem is ShortcutChoice choice) Save(settings => settings.CaffeineShortcut = choice.Value);
        }
        private static ShortcutChoice FindShortcut(IEnumerable<ShortcutChoice> choices, string value) =>
            choices.FirstOrDefault(choice => string.Equals(NormalizeShortcut(choice.Value), NormalizeShortcut(value), StringComparison.OrdinalIgnoreCase)) ?? choices.First();

        private static string NormalizeShortcut(string value)
        {
            var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return string.Empty;
            return string.Join('+', parts[..^1].OrderBy(part => part, StringComparer.OrdinalIgnoreCase).Append(parts[^1]));
        }

        private void Save(Action<AppSettings> update)
        {
            var changed = _settings.Clone(); update(changed);
            App.Settings.Save(changed);
            _settings = changed;
            StatusText.Text = string.Empty;
        }
    }
}
