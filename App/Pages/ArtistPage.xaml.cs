using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using App.Models;
using App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace App.Pages
{
    public sealed partial class ArtistPage : Page
    {
        private GeneratedImage? _image;
        private GeneratedImage? _pendingAttachment;
        private uint _pixelWidth;
        private uint _pixelHeight;
        private Rect? _selection;
        private Rect? _normalizedSelection;
        private Point? _dragStart;
        private bool _isBusy;
        private bool _includePreviewOnNextSend;
        private bool _hasGeneratedPreview;
        private readonly CancellationTokenSource _pageCancellation = new();

        public ArtistPage()
        {
            InitializeComponent();
            SizeChanged += (_, _) => UpdateSelectionOverlay();
            UpdateControls();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _pageCancellation.Cancel();
            PreviewImage.Source = null;
            _image = null;
            _pendingAttachment = null;
            base.OnNavigatedFrom(e);
        }

        private async void PreviewSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isBusy) return;
            if (IsInsideComposer(e.OriginalSource as DependencyObject)) return;
            if (_image is null)
            {
                await PickImageAsync();
                return;
            }
            if (SelectionButton.IsChecked != true) return;

            var point = e.GetCurrentPoint(PreviewHost).Position;
            var imageBounds = GetRenderedImageBounds();
            if (!imageBounds.Contains(point)) return;
            _dragStart = Clamp(point, imageBounds);
            _selection = new Rect(_dragStart.Value, _dragStart.Value);
            _normalizedSelection = null;
            PreviewSurface.CapturePointer(e.Pointer);
            UpdateSelectionOverlay();
            e.Handled = true;
        }

        private void PreviewSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_dragStart is null) return;
            _selection = Normalize(_dragStart.Value, Clamp(e.GetCurrentPoint(PreviewHost).Position, GetRenderedImageBounds()));
            UpdateSelectionOverlay();
        }

        private void PreviewSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragStart is null) return;
            var selection = Normalize(_dragStart.Value, Clamp(e.GetCurrentPoint(PreviewHost).Position, GetRenderedImageBounds()));
            _dragStart = null;
            PreviewSurface.ReleasePointerCapture(e.Pointer);
            _selection = selection.Width >= 3 && selection.Height >= 3 ? selection : null;
            _normalizedSelection = _selection.HasValue ? NormalizeToImage(_selection.Value) : null;
            UpdateSelectionOverlay();
            UpdateControls();
        }

        private void SelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectionButton.IsChecked != true)
            {
                _selection = null;
                _normalizedSelection = null;
                UpdateSelectionOverlay();
            }
            UpdateControls();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            _image = null;
            _pixelWidth = 0;
            _pixelHeight = 0;
            _selection = null;
            _normalizedSelection = null;
            _dragStart = null;
            PreviewImage.Source = null;
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
            PreviewHost.Width = double.NaN;
            PreviewHost.Height = double.NaN;
            EmptyState.Visibility = Visibility.Visible;
            SelectionButton.IsChecked = false;
            PromptBox.Text = string.Empty;
            _pendingAttachment = null;
            _includePreviewOnNextSend = false;
            _hasGeneratedPreview = false;
            UpdateSelectionOverlay();
            UpdateControls();
        }

        private async Task PickImageAsync()
        {
            if (_isBusy || App.MainWindow is null) return;
            try
            {
                var picker = new FileOpenPicker();
                foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff" })
                    picker.FileTypeFilter.Add(extension);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;
                var buffer = await FileIO.ReadBufferAsync(file);
                await SetImageAsync(new GeneratedImage(buffer.ToArray(), NormalizeMimeType(file.ContentType, file.FileType)));
                _includePreviewOnNextSend = true;
                _hasGeneratedPreview = false;
            }
            catch (Exception exception)
            {
                await ShowErrorAsync("The image could not be opened", exception.Message);
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e) => await GenerateAsync();

        private async Task GenerateAsync()
        {
            var prompt = PromptBox.Text.Trim();
            if (_isBusy || prompt.Length == 0) return;
            if (string.IsNullOrWhiteSpace(App.Settings.Current.GeminiApiKey))
            {
                await ShowErrorAsync("Gemini API key required", "Add a Gemini API key in Settings before using Artist.");
                return;
            }

            var contextImages = new System.Collections.Generic.List<GeneratedImage>();
            try
            {
                if (_normalizedSelection.HasValue)
                {
                    var selection = new GeneratedImage(await CropSelectionAsync(), "image/jpeg");
                    contextImages.Add(await CreateJpegContextImageAsync(selection));
                }
                else if ((_includePreviewOnNextSend || _hasGeneratedPreview) && _image is not null)
                {
                    contextImages.Add(await CreateJpegContextImageAsync(_image));
                }
                if (_pendingAttachment is not null) contextImages.Add(_pendingAttachment);

                SetBusy(true);
                using var client = new GeminiClient(App.Settings.Current.GeminiApiKey);
                var generated = await client.GenerateImageAsync(prompt, contextImages, _pageCancellation.Token);
                _pageCancellation.Token.ThrowIfCancellationRequested();
                await SetImageAsync(generated);
                _pendingAttachment = null;
                _includePreviewOnNextSend = false;
                _hasGeneratedPreview = true;
                PromptBox.Text = string.Empty;
                UpdateControls();
                CompletionNotificationService.ShowWhenMainWindowIsInactive(
                    "Artist generation complete",
                    "Your image is ready to review.");
            }
            catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await ShowErrorAsync("Artist could not generate the image", exception.Message);
            }
            finally
            {
                if (!_pageCancellation.IsCancellationRequested) SetBusy(false);
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _image is null || App.MainWindow is null) return;
            try
            {
                var selected = _normalizedSelection.HasValue;
                var data = selected ? await CropSelectionAsync() : _image.Data;
                var mimeType = selected ? "image/jpeg" : _image.MimeType;
                var extension = ExtensionForMimeType(mimeType);
                var picker = new FileSavePicker { SuggestedFileName = selected ? "artist-selection" : "artist-image" };
                picker.FileTypeChoices.Add(NameForExtension(extension), [extension]);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                var file = await picker.PickSaveFileAsync();
                if (file is not null)
                {
                    await FileIO.WriteBytesAsync(file, data);
                }
            }
            catch (Exception exception)
            {
                await ShowErrorAsync("The image could not be saved", exception.Message);
            }
        }

        private async Task SetImageAsync(GeneratedImage image)
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(image.Data.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            _pixelWidth = decoder.PixelWidth;
            _pixelHeight = decoder.PixelHeight;
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            _image = image;
            PreviewImage.Source = bitmap;
            PreviewImage.Width = _pixelWidth;
            PreviewImage.Height = _pixelHeight;
            PreviewHost.Width = _pixelWidth;
            PreviewHost.Height = _pixelHeight;
            EmptyState.Visibility = Visibility.Collapsed;
            _selection = null;
            _normalizedSelection = null;
            SelectionButton.IsChecked = false;
            UpdateSelectionOverlay();
            UpdateControls();
        }

        private async Task<byte[]> CropSelectionAsync()
        {
            if (_image is null || !_normalizedSelection.HasValue) throw new InvalidOperationException("There is no image selection.");
            var sourceRect = ToPixelRect(_normalizedSelection.Value);
            using var source = new InMemoryRandomAccessStream();
            await source.WriteAsync(_image.Data.AsBuffer());
            source.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(source);
            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds
                {
                    X = sourceRect.X,
                    Y = sourceRect.Y,
                    Width = sourceRect.Width,
                    Height = sourceRect.Height
                }
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                sourceRect.Width, sourceRect.Height, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();
            output.Seek(0);
            var result = new byte[output.Size];
            using var reader = new DataReader(output);
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(result);
            return result;
        }

        private BitmapBounds ToPixelRect(Rect normalized)
        {
            var x = (uint)Math.Floor(normalized.Left * _pixelWidth);
            var y = (uint)Math.Floor(normalized.Top * _pixelHeight);
            var rightPixel = Math.Min(_pixelWidth, (uint)Math.Ceiling(normalized.Right * _pixelWidth));
            var bottomPixel = Math.Min(_pixelHeight, (uint)Math.Ceiling(normalized.Bottom * _pixelHeight));
            return new BitmapBounds
            {
                X = x,
                Y = y,
                Width = Math.Max(1u, rightPixel - x),
                Height = Math.Max(1u, bottomPixel - y)
            };
        }

        private Rect GetRenderedImageBounds()
        {
            if (_pixelWidth == 0 || _pixelHeight == 0) return new Rect();
            return new Rect(0, 0, _pixelWidth, _pixelHeight);
        }

        private void UpdateSelectionOverlay()
        {
            if (_image is null || (!_selection.HasValue && !_normalizedSelection.HasValue))
            {
                SelectionCanvas.Visibility = Visibility.Collapsed;
                return;
            }
            var image = GetRenderedImageBounds();
            var selection = _dragStart is not null && _selection.HasValue
                ? _selection.Value
                : FromNormalized(_normalizedSelection!.Value, image);
            SelectionCanvas.Visibility = Visibility.Visible;
            SetRect(ShadeTop, image.Left, image.Top, image.Width, Math.Max(0, selection.Top - image.Top));
            SetRect(ShadeLeft, image.Left, selection.Top, Math.Max(0, selection.Left - image.Left), selection.Height);
            SetRect(ShadeRight, selection.Right, selection.Top, Math.Max(0, image.Right - selection.Right), selection.Height);
            SetRect(ShadeBottom, image.Left, selection.Bottom, image.Width, Math.Max(0, image.Bottom - selection.Bottom));
            SetRect(SelectionBorder, selection.Left, selection.Top, selection.Width, selection.Height);
        }

        private static void SetRect(FrameworkElement element, double left, double top, double width, double height)
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            element.Width = width;
            element.Height = height;
        }

        private void PromptBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateControls();

        private async void PromptBox_Paste(object sender, TextControlPasteEventArgs e)
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap)) return;

            e.Handled = true;
            try
            {
                var bitmapReference = await content.GetBitmapAsync();
                using var input = await bitmapReference.OpenReadAsync();
                _pendingAttachment = new GeneratedImage(await ConvertToJpegAsync(input), "image/jpeg");
                UpdateControls();
            }
            catch (Exception exception)
            {
                await ShowErrorAsync("The pasted image could not be attached", exception.Message);
            }
        }

        private static async Task<GeneratedImage> CreateJpegContextImageAsync(GeneratedImage image)
        {
            using var source = new InMemoryRandomAccessStream();
            await source.WriteAsync(image.Data.AsBuffer());
            source.Seek(0);
            return new GeneratedImage(await ConvertToJpegAsync(source), "image/jpeg");
        }

        private static async Task<byte[]> ConvertToJpegAsync(IRandomAccessStream source)
        {
            var decoder = await BitmapDecoder.CreateAsync(source);
            const uint maximumDimension = 720;
            var scale = Math.Min(
                1d,
                maximumDimension / (double)Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
            var width = Math.Max(1u, (uint)Math.Round(decoder.OrientedPixelWidth * scale));
            var height = Math.Max(1u, (uint)Math.Round(decoder.OrientedPixelHeight * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = width,
                ScaledHeight = height,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                width,
                height,
                decoder.DpiX,
                decoder.DpiY,
                pixels.DetachPixelData());
            await encoder.FlushAsync();
            output.Seek(0);
            var data = new byte[output.Size];
            using var reader = new DataReader(output);
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(data);
            return data;
        }

        private void RemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingAttachment = null;
            UpdateControls();
            PromptBox.Focus(FocusState.Programmatic);
        }

        private async void PromptBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown)
            {
                e.Handled = true;
                await GenerateAsync();
            }
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            BusyRing.IsActive = busy;
            BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SendIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            UpdateControls();
        }

        private void UpdateControls()
        {
            PromptBox.IsEnabled = !_isBusy;
            SendButton.IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(PromptBox.Text);
            DownloadButton.IsEnabled = !_isBusy && _image is not null;
            SelectionButton.IsEnabled = !_isBusy && _image is not null;
            ClearButton.IsEnabled = !_isBusy &&
                (_image is not null || _pendingAttachment is not null || !string.IsNullOrWhiteSpace(PromptBox.Text));
            RemoveAttachmentButton.Visibility = _pendingAttachment is null ? Visibility.Collapsed : Visibility.Visible;
            RemoveAttachmentButton.IsEnabled = !_isBusy;
            PreviewSurface.IsHitTestVisible = !_isBusy;
        }

        private async Task ShowErrorAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }

        private static Rect Normalize(Point first, Point second) =>
            new(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
                Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));

        private static Point Clamp(Point point, Rect bounds) =>
            new(Math.Clamp(point.X, bounds.Left, bounds.Right), Math.Clamp(point.Y, bounds.Top, bounds.Bottom));

        private Rect NormalizeToImage(Rect selection)
        {
            var bounds = GetRenderedImageBounds();
            return new Rect(
                Math.Clamp((selection.Left - bounds.Left) / bounds.Width, 0, 1),
                Math.Clamp((selection.Top - bounds.Top) / bounds.Height, 0, 1),
                Math.Clamp(selection.Width / bounds.Width, 0, 1),
                Math.Clamp(selection.Height / bounds.Height, 0, 1));
        }

        private static Rect FromNormalized(Rect normalized, Rect bounds) =>
            new(bounds.Left + normalized.Left * bounds.Width,
                bounds.Top + normalized.Top * bounds.Height,
                normalized.Width * bounds.Width,
                normalized.Height * bounds.Height);

        private static string ExtensionForMimeType(string mimeType) => mimeType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            "image/tiff" => ".tiff",
            _ => ".png"
        };

        private static string NameForExtension(string extension) => extension switch
        {
            ".jpg" => "JPEG image",
            ".webp" => "WebP image",
            ".bmp" => "Bitmap image",
            ".gif" => "GIF image",
            ".tiff" => "TIFF image",
            _ => "PNG image"
        };

        private static string NormalizeMimeType(string contentType, string extension) =>
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? contentType
                : extension.ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    ".gif" => "image/gif",
                    ".tif" or ".tiff" => "image/tiff",
                    _ => "image/png"
                };

        private bool IsInsideComposer(DependencyObject? element)
        {
            while (element is not null)
            {
                if (ReferenceEquals(element, ComposerBorder)) return true;
                element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
            }
            return false;
        }
    }
}
