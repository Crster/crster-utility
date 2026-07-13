using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using App.Controls;
using App.Models;
using App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class NotebookPage : Page
    {
        private readonly NotebookDatabaseService _database = new();
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private NotebookAttachmentStorageService? _attachmentStorage;
        private readonly ObservableCollection<NotebookEntry> _entries = [];
        private bool _isLoading;
        private bool _isSaving;
        private bool _saveQueued;

        public NotebookPage()
        {
            InitializeComponent();
            Loaded += NotebookPage_Loaded;
            _saveTimer.Tick += async (_, _) =>
            {
                _saveTimer.Stop();
                await SaveNotebookAsync();
            };
        }

        private async void NotebookPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoading = true;
            _attachmentStorage = new NotebookAttachmentStorageService(_database.RootPath);
            foreach (var entry in (await _database.LoadAsync()).OrderByDescending(entry => entry.Index)) _entries.Add(entry);
            _isLoading = false;
            BlocksHost.ItemsSource = _entries;
        }

        private async void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string type }) return;
            ParagraphTool.IsChecked = type == "paragraph";
            if (type is "image" or "file")
            {
                await AddAttachmentAsync(type);
                return;
            }

            AddEntry(type, type switch
            {
                "table" => "| Column 1 | Column 2 |\n| --- | --- |\n| Value | Value |",
                "todo" => "- [ ] New task",
                _ => string.Empty
            });
        }

        private async Task AddAttachmentAsync(string type)
        {
            if (App.MainWindow is null || _attachmentStorage is null) return;
            var picker = new FileOpenPicker();
            if (type == "image")
            {
                picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".gif"); picker.FileTypeFilter.Add(".webp");
            }
            else picker.FileTypeFilter.Add("*");

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is not null) AddEntry(type, await _attachmentStorage.CopyFromPathAsync(file.Path));
        }

        private void AddEntry(string type, string content)
        {
            var index = _entries.Count == 0 ? 1 : _entries.Max(entry => entry.Index) + 1;
            _entries.Insert(0, new NotebookEntry { Type = type, Content = content, Index = index });
            ScheduleSave();
        }

        private void Block_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Noteblock { DataContext: NotebookEntry entry } block || _attachmentStorage is null) return;
            block.Configure(entry, _attachmentStorage.GetFullPath);
            block.ContentChanged -= Block_ContentChanged;
            block.RemoveRequested -= Block_RemoveRequested;
            block.ContentChanged += Block_ContentChanged;
            block.RemoveRequested += Block_RemoveRequested;
        }

        private void Block_ContentChanged(object? sender, EventArgs e) => ScheduleSave();

        private void Block_RemoveRequested(object? sender, EventArgs e)
        {
            if (sender is not Noteblock block) return;
            var entry = _entries.FirstOrDefault(candidate => ReferenceEquals(candidate, block.Entry));
            if (entry is null) return;
            _entries.Remove(entry);
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            if (_isLoading) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private async Task SaveNotebookAsync()
        {
            if (_isSaving)
            {
                _saveQueued = true;
                return;
            }

            _isSaving = true;
            try
            {
                do
                {
                    _saveQueued = false;
                    var snapshot = _entries.Select(entry => new NotebookEntry
                    {
                        Type = entry.Type,
                        Content = entry.Content,
                        Index = entry.Index
                    }).ToList();

                    await Task.Run(() => _database.SaveAsync(snapshot));
                }
                while (_saveQueued);
            }
            finally
            {
                _isSaving = false;
            }
        }
    }
}
