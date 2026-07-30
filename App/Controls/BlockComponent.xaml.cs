using System;
using App.Models;
using App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace App.Controls
{
    public sealed partial class Noteblock : UserControl
    {
        private NotebookEntry? _entry;
        private NotebookAttachmentStorageService? _attachmentStorage;
        private bool _isPointerOver;
        private bool _isSearchHighlighted;

        public event EventHandler? EditRequested;

        internal NotebookEntry Entry => _entry ?? throw new InvalidOperationException("The noteblock has not been configured.");

        public Noteblock()
        {
            InitializeComponent();
            HorizontalAlignment = HorizontalAlignment.Stretch;
            AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(Noteblock_DoubleTapped), true);
        }

        internal void Configure(NotebookEntry entry, NotebookAttachmentStorageService attachmentStorage)
        {
            _entry = entry;
            _attachmentStorage = attachmentStorage;
            ShowPreview();
        }

        internal void HighlightSearchResult()
        {
            _isSearchHighlighted = true;
            SetHoverBackground();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _isSearchHighlighted = false;
                SetHoverBackground();
            };
            timer.Start();
        }

        private void Noteblock_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            EditRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            SetHoverBackground();
        }

        private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            SetHoverBackground();
        }

        private void SetHoverBackground()
        {
            if (_isSearchHighlighted)
            {
                Root.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 0, 120, 212));
                return;
            }

            Root.Background = _isPointerOver
                ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private void ShowPreview()
        {
            SetHoverBackground();
            ContentHost.Children.Clear();
            if (_entry is null) return;

            var preview = new MarkdownView { Markdown = _entry.Content };
            preview.ConfigureNotebook(id => _attachmentStorage?.GetFullPath(id));
            ContentHost.Children.Add(preview);
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                BlockScrollViewer.ChangeView(null, BlockScrollViewer.ScrollableHeight, null, true));
        }

        private void BlockScroll_Loaded(object sender, RoutedEventArgs e)
        {
            var scrollViewer = (ScrollViewer)sender;
            SizeChangedEventHandler? sizeChangedHandler = null;
            sizeChangedHandler = (_, _) =>
            {
                scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, true);
                if (scrollViewer.Content is FrameworkElement content)
                    content.SizeChanged -= sizeChangedHandler;
            };
            if (scrollViewer.Content is FrameworkElement content)
                content.SizeChanged += sizeChangedHandler;
            scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, true);
        }
    }
}
