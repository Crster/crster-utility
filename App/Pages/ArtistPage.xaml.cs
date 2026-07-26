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
        private uint _pixelWidth;
        private uint _pixelHeight;
        private Rect? _selection;
        private Rect? _normalizedSelection;
        private Point? _dragStart;
        private bool _isBusy;

        public ArtistPage()
        {
            InitializeComponent();
            SizeChanged += (_, _) => UpdateSelectionOverlay();
            UpdateControls();
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
            if (SelectionButton.IsChecked == true)
            {
                StatusText.Text = "Drag over the image to select an area.";
            }
            else
            {
                _selection = null;
                _normalizedSelection = null;
                StatusText.Text = string.Empty;
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
            StatusText.Text = string.Empty;
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
                StatusText.Text = string.Empty;
            }
            catch (Exception exception)
            {
                StatusText.Text = $"The image could not be opened: {exception.Message}";
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e) => await GenerateAsync();

        private async Task GenerateAsync()
        {
            var prompt = PromptBox.Text.Trim();
            if (_isBusy || prompt.Length == 0) return;
            if (string.IsNullOrWhiteSpace(App.Settings.Current.GeminiApiKey))
            {
                StatusText.Text = "Add a Gemini API key in Settings before using Artist.";
                return;
            }

            byte[]? contextBytes = _image?.Data;
            string? contextMimeType = _image?.MimeType;
            try
            {
                if (_normalizedSelection.HasValue)
                {
                    contextBytes = await CropSelectionAsync();
                    contextMimeType = "image/png";
                }
                _selection = null;
                _normalizedSelection = null;
                SelectionButton.IsChecked = false;
                UpdateSelectionOverlay();
                SetBusy(true);
                using var client = new GeminiClient(App.Settings.Current.GeminiApiKey);
                var generated = await client.GenerateImageAsync(prompt, contextBytes, contextMimeType, CancellationToken.None);
                await SetImageAsync(generated);
                PromptBox.Text = string.Empty;
                StatusText.Text = "Image ready.";
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Artist could not generate the image: {exception.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _image is null || App.MainWindow is null) return;
            try
            {
                var selected = _normalizedSelection.HasValue;
                var data = selected ? await CropSelectionAsync() : _image.Data;
                var mimeType = selected ? "image/png" : _image.MimeType;
                var extension = ExtensionForMimeType(mimeType);
                var picker = new FileSavePicker { SuggestedFileName = selected ? "artist-selection" : "artist-image" };
                picker.FileTypeChoices.Add(NameForExtension(extension), [extension]);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                var file = await picker.PickSaveFileAsync();
                if (file is not null)
                {
                    await FileIO.WriteBytesAsync(file, data);
                    StatusText.Text = "Image saved.";
                }
            }
            catch (Exception exception)
            {
                StatusText.Text = $"The image could not be saved: {exception.Message}";
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
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
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
            StatusText.Text = busy ? "Creating your image..." : StatusText.Text;
            UpdateControls();
        }

        private void UpdateControls()
        {
            PromptBox.IsEnabled = !_isBusy;
            SendButton.IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(PromptBox.Text);
            DownloadButton.IsEnabled = !_isBusy && _image is not null;
            SelectionButton.IsEnabled = !_isBusy && _image is not null;
            ClearButton.IsEnabled = !_isBusy && (_image is not null || !string.IsNullOrWhiteSpace(PromptBox.Text));
            PreviewSurface.IsHitTestVisible = !_isBusy;
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
