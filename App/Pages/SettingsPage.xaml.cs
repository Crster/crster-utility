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
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private bool _loading;
        private bool _isRebuilding;
        private AppSettings _settings = null!;
        private string _loadedModelEndpoint = string.Empty;

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
            Unloaded += SettingsPage_Unloaded;
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _loading = true;
            _settings = App.Settings.Current.Clone();
            await SynchronizeStartupSettingAsync();
            StartupToggle.IsOn = _settings.StartWithWindows;
            NotebookPathBox.Text = _settings.DatabaseFolder;
            OpenAiCompatibleBaseUrlBox.Text = _settings.OpenAiCompatibleBaseUrl;
            OpenAiCompatibleApiKeyBox.Password = _settings.OpenAiCompatibleApiKey;
            CityBox.Text = _settings.City;
            CountryBox.Text = _settings.Country;
            SnapshotShortcutBox.ItemsSource = SnapshotShortcuts;
            SnapshotShortcutBox.SelectedItem = FindShortcut(SnapshotShortcuts, _settings.SnapshotShortcut);
            CursorToggle.IsOn = _settings.SnapshotCaptureMouseCursor;
            CaffeineShortcutBox.ItemsSource = CaffeineShortcuts;
            CaffeineShortcutBox.SelectedItem = FindShortcut(CaffeineShortcuts, _settings.CaffeineShortcut);
            await LoadMicrophonesAsync();
            await LoadModelsAsync();
            UpdateEmbeddingRebuildUi();
            UpdatePermissionWarnings();
            App.Settings.Changed += Settings_Changed;
            if (App.MainWindow is not null) App.MainWindow.Activated += MainWindow_Activated;
            _loading = false;
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            App.Settings.Changed -= Settings_Changed;
            if (App.MainWindow is not null) App.MainWindow.Activated -= MainWindow_Activated;
        }

        private void Settings_Changed(object? sender, AppSettings settings)
        {
            _settings = settings.Clone();
            UpdateEmbeddingRebuildUi();
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) => UpdatePermissionWarnings();

        private void UpdatePermissionWarnings()
        {
            ScreenCapturePermissionWarning.Visibility = AppCapability.Create("graphicsCaptureProgrammatic").CheckAccess() == AppCapabilityAccessStatus.DeniedByUser
                ? Visibility.Visible
                : Visibility.Collapsed;
            MicrophonePermissionWarning.Visibility = AppCapability.Create("microphone").CheckAccess() == AppCapabilityAccessStatus.DeniedByUser
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void OpenScreenCapturePrivacyButton_Click(object sender, RoutedEventArgs e) =>
            await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-graphicscaptureprogrammatic"));

        private async void OpenMicrophonePrivacyButton_Click(object sender, RoutedEventArgs e) =>
            await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));

        private async Task SynchronizeStartupSettingAsync()
        {
            try
            {
                var isEnabled = await StartupService.IsEnabledAsync();
                if (_settings.StartWithWindows == isEnabled)
                    return;

                _settings.StartWithWindows = isEnabled;
                App.Settings.Save(_settings);
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Windows startup status could not be checked: {exception.Message}";
            }
        }

        /// <summary>Reloads the model lists when the endpoint the current lists came from is no longer the saved one.</summary>
        private async Task ReloadModelsIfEndpointChangedAsync()
        {
            if (string.Equals(_loadedModelEndpoint, ModelEndpointKey(_settings), StringComparison.Ordinal)) return;
            var wasLoading = _loading;
            _loading = true;
            try { await LoadModelsAsync(); }
            finally { _loading = wasLoading; }
        }

        private static string ModelEndpointKey(AppSettings settings) =>
            $"{settings.OpenAiCompatibleBaseUrl}\n{settings.OpenAiCompatibleApiKey}";

        private async Task LoadModelsAsync()
        {
            SetModelBoxesEnabled(false);
            _loadedModelEndpoint = string.Empty;
            try
            {
                using var client = new OpenAiCompatibleClient(_settings.OpenAiCompatibleApiKey);
                var models = await client.ListModelsAsync(System.Threading.CancellationToken.None);
                SetModelChoices(EmbeddingModelBox, models.Where(model => model.SupportsEmbedding), _settings.EmbeddingModel);
                SetModelChoices(LowCostModelBox, models.Where(model => model.SupportsChat && !model.SupportsImageGeneration), _settings.LowCostModel);
                SetModelChoices(HighCostModelBox, models.Where(model => model.SupportsChat && !model.SupportsImageGeneration), _settings.HighCostModel);
                SetModelChoices(ArtistModelBox, models.Where(model => model.SupportsImageGeneration), _settings.ArtistModel);
                _loadedModelEndpoint = ModelEndpointKey(_settings);
                SetModelBoxesEnabled(true);
            }
            catch (Exception exception)
            {
                StatusText.Text = $"AI models could not be loaded: {exception.Message}";
            }
        }

        private static void SetModelChoices(ComboBox box, IEnumerable<OpenAiCompatibleModel> models, string selectedId)
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
            UpdateEmbeddingRebuildUi();
        }

        private void UpdateEmbeddingRebuildUi()
        {
            var needsRebuild = _settings.EmbeddingsNeedRebuild;
            RebuildEmbeddingsButton.Visibility = needsRebuild ? Visibility.Visible : Visibility.Collapsed;
            EmbeddingRebuildHint.Visibility = needsRebuild ? Visibility.Visible : Visibility.Collapsed;
            RebuildEmbeddingsButton.IsEnabled = needsRebuild && !_isRebuilding && !EmbeddingMaintenanceService.IsRunning;
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

        private void OpenAiCompatibleApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) { if (!_loading) Save(settings => settings.OpenAiCompatibleApiKey = OpenAiCompatibleApiKeyBox.Password.Trim()); }
        private async void OpenAiCompatibleApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            await ReloadModelsIfEndpointChangedAsync();
        }
        private async void OpenAiCompatibleBaseUrlBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            try
            {
                Save(settings => settings.OpenAiCompatibleBaseUrl = OpenAiCompatibleBaseUrlBox.Text.Trim());
                OpenAiCompatibleBaseUrlBox.Text = _settings.OpenAiCompatibleBaseUrl;
            }
            catch (InvalidOperationException exception)
            {
                OpenAiCompatibleBaseUrlBox.Text = _settings.OpenAiCompatibleBaseUrl;
                StatusText.Text = exception.Message;
                return;
            }
            await ReloadModelsIfEndpointChangedAsync();
        }
        private void EmbeddingModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && EmbeddingModelBox.SelectedItem is ModelChoice choice) Save(settings => settings.EmbeddingModel = choice.Id); }
        private async void RebuildEmbeddingsButton_Click(object sender, RoutedEventArgs e)
        {
            var rebuiltConfiguration = EmbeddingMaintenanceService.CurrentConfigurationKey;
            SetRebuildEmbeddingsBusy(true);
            StatusText.Text = "Rebuilding embeddings…";
            try
            {
                var rebuilt = await EmbeddingMaintenanceService.RebuildAllAsync(System.Threading.CancellationToken.None);
                if (EmbeddingMaintenanceService.MarkCurrentConfigurationRebuilt(rebuiltConfiguration))
                {
                    _settings = App.Settings.Current.Clone();
                    StatusText.Text = $"Rebuilt {rebuilt} embedding{(rebuilt == 1 ? string.Empty : "s")} with {_settings.EmbeddingModel}.";
                }
                else
                {
                    _settings = App.Settings.Current.Clone();
                    StatusText.Text = "Embeddings were rebuilt, but the endpoint or model changed during the rebuild. Rebuild again.";
                }
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Embeddings could not be rebuilt: {exception.Message}";
            }
            finally { SetRebuildEmbeddingsBusy(false); }
        }

        private void SetRebuildEmbeddingsBusy(bool isBusy)
        {
            _isRebuilding = isBusy;
            RebuildEmbeddingsLabel.Text = isBusy ? "Rebuilding" : "Rebuild";
            RebuildEmbeddingsProgress.IsActive = isBusy;
            RebuildEmbeddingsProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            UpdateEmbeddingRebuildUi();
        }

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
            UpdateEmbeddingRebuildUi();
        }
    }
}
