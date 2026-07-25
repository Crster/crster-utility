using App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Threading;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App.Windows
{
    public sealed partial class FirstRunWindow : Window
    {
        public event EventHandler? SetupCompleted;

        public FirstRunWindow()
        {
            InitializeComponent();
            AppWindow.Resize(new SizeInt32(720, 520));
            AppWindow.SetIcon("Assets/WindowIcon.ico");
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }
            CenterOnCurrentScreen();
            DatabaseFolderBox.Text = App.Settings.Current.DatabaseFolder;
        }

        private void CenterOnCurrentScreen()
        {
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var size = AppWindow.Size;
            AppWindow.Move(new PointInt32(
                workArea.X + (workArea.Width - size.Width) / 2,
                workArea.Y + (workArea.Height - size.Height) / 2));
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) DatabaseFolderBox.Text = folder.Path;
        }

        private async void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            var folder = DatabaseFolderBox.Text.Trim();
            var apiKey = GeminiApiKeyBox.Password.Trim();
            if (folder.Length == 0 || apiKey.Length == 0)
            {
                StatusText.Text = "Database folder and Gemini API key are required.";
                return;
            }

            ContinueButton.IsEnabled = false;
            StatusText.Text = "Verifying storage and Gemini...";
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $".crster-access-{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(probe, "probe");
                File.Delete(probe);
                using (var client = new GeminiClient(apiKey))
                    _ = await client.EmbedRetrievalQueryAsync("Crster Utility setup", CancellationToken.None);
                App.Settings.Configure(folder, apiKey);
                SetupCompleted?.Invoke(this, EventArgs.Empty);
                Close();
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Setup failed: {exception.Message}";
                ContinueButton.IsEnabled = true;
            }
        }
    }
}
