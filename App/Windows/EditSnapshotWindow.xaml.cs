using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using App.Services;

namespace App.Windows
{
    public sealed partial class EditSnapshotWindow : Window
    {
        private enum ActiveActions
        {
            Crop,
            Arrow,
            Box,
            Text,
            Blur,
        }

        private class DrawnShape
        {
            public ActiveActions Type { get; set; }
            public Point Start { get; set; }
            public Point End { get; set; }
            public string Text { get; set; } = string.Empty;
        }

        private readonly CanvasBitmap Snapshot;
        private ActiveActions action = ActiveActions.Crop;
        private Rect? cropSelection;
        private readonly List<Rect> blurRegions = new();
        private bool isDragging;
        private Point? dragStart;
        private Point? dragEnd;
        private readonly List<DrawnShape> shapes = new();
        private DateTime? lastClickTime;
        private bool isCopying;
        private bool isCopyingText;

        // Text editing state
        private bool isTextEditing;
        private Point textOrigin;
        private string currentText = string.Empty;
        private int cursorIndex = 0;
        private bool showCursor = true;
        private readonly DispatcherTimer cursorTimer;

        private static readonly CanvasTextFormat TextFormat = new()
        {
            FontSize = 16,
            FontFamily = "Segoe UI",
            WordWrapping = CanvasWordWrapping.NoWrap
        };

        private const float BlurAmount = 3f;

        public event EventHandler<SavedImageResult>? ImageSaved;

        public EditSnapshotWindow(CanvasBitmap snapshot)
        {
            InitializeComponent();
            AppWindow.SetIcon("Assets/WindowIcon.ico");
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            this.Snapshot = snapshot;
            UpdateActiveButton();

            cursorTimer = new DispatcherTimer();
            cursorTimer.Interval = TimeSpan.FromMilliseconds(530);
            cursorTimer.Tick += (s, e) => { showCursor = !showCursor; MyCanvas.Invalidate(); };
        }

        private static Rect NormalizeRect(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            double w = Math.Abs(b.X - a.X);
            double h = Math.Abs(b.Y - a.Y);
            return new Rect(x, y, w, h);
        }

        private void MyCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;

            ds.DrawImage(this.Snapshot);

            Rect? cropRect = cropSelection;
            Rect? blurPreview = null;
            if (isDragging && dragStart.HasValue && dragEnd.HasValue)
            {
                if (action == ActiveActions.Crop)
                {
                    cropRect = NormalizeRect(dragStart.Value, dragEnd.Value);
                }
                else if (action == ActiveActions.Blur)
                {
                    blurPreview = NormalizeRect(dragStart.Value, dragEnd.Value);
                }
            }

            if (cropRect.HasValue)
            {
                var selection = cropRect.Value;
                double left = Math.Clamp(selection.Left, 0, sender.ActualWidth);
                double top = Math.Clamp(selection.Top, 0, sender.ActualHeight);
                double right = Math.Clamp(selection.Right, left, sender.ActualWidth);
                double bottom = Math.Clamp(selection.Bottom, top, sender.ActualHeight);
                var tintColor = Color.FromArgb(160, 0, 0, 0);

                ds.FillRectangle(new Rect(0, 0, sender.ActualWidth, top), tintColor);
                ds.FillRectangle(new Rect(0, bottom, sender.ActualWidth, sender.ActualHeight - bottom), tintColor);
                ds.FillRectangle(new Rect(0, top, left, bottom - top), tintColor);
                ds.FillRectangle(new Rect(right, top, sender.ActualWidth - right, bottom - top), tintColor);

                var style = new CanvasStrokeStyle
                {
                    DashStyle = CanvasDashStyle.Dash,
                    CustomDashStyle = new float[] { 4, 4 }
                };
                ds.DrawRectangle(cropRect.Value, Colors.White, 2, style);
            }
            else if (action == ActiveActions.Crop)
            {
                ds.FillRectangle(new Rect(0, 0, sender.ActualWidth, sender.ActualHeight), Color.FromArgb(160, 0, 0, 0));
            }

            DrawBlurRegions(ds, blurRegions, 0, 0);

            if (blurPreview.HasValue)
            {
                ds.DrawRectangle(blurPreview.Value, Colors.White, 2);
            }

            foreach (var shape in shapes)
            {
                if (shape.Type == ActiveActions.Arrow)
                    DrawArrow(sender, ds, shape.Start, shape.End);
                else if (shape.Type == ActiveActions.Box)
                    DrawBox(ds, shape.Start, shape.End);
                else if (shape.Type == ActiveActions.Text)
                    DrawTextLeftAligned(sender, ds, shape.Text, shape.Start, null);
            }

            if (isDragging && dragStart.HasValue && dragEnd.HasValue)
            {
                if (action == ActiveActions.Arrow)
                {
                    DrawArrow(sender, ds, dragStart.Value, dragEnd.Value);
                }
                else if (action == ActiveActions.Box)
                {
                    DrawBox(ds, dragStart.Value, dragEnd.Value);
                }
            }

            if (isTextEditing)
            {
                var metrics = DrawTextLeftAligned(sender, ds, currentText, textOrigin, cursorIndex);
                if (showCursor)
                {
                    ds.DrawLine(metrics.CursorX, metrics.CursorTop, metrics.CursorX, metrics.CursorBottom, Colors.Red, 5);
                }
            }
        }

        private static TextMetrics DrawTextLeftAligned(CanvasControl sender, CanvasDrawingSession ds, string text, Point origin, int? cursorIdx)
        {
            float drawX = (float)origin.X;
            float drawY = (float)origin.Y;

            float cursorX = drawX;
            float cursorTop = drawY + 2;
            float cursorBottom = drawY + 20;

            if (!string.IsNullOrEmpty(text))
            {
                using (var layout = new CanvasTextLayout(sender, text, TextFormat, float.MaxValue, float.MaxValue))
                {
                    ds.DrawTextLayout(layout, drawX, drawY, Colors.Red);

                    int idx = cursorIdx ?? text.Length;
                    if (idx < 0) idx = 0;
                    if (idx > text.Length) idx = text.Length;
                    var caretPos = layout.GetCaretPosition(idx, false);
                    cursorX = drawX + caretPos.X + 3;
                    cursorTop = drawY + caretPos.Y + 2;
                    cursorBottom = drawY + caretPos.Y + 20;
                }
            }
            else
            {
                ds.DrawText(text, drawX, drawY, Colors.Red, TextFormat);
            }

            return new TextMetrics
            {
                DrawX = drawX,
                DrawY = drawY,
                CursorX = cursorX,
                CursorTop = cursorTop,
                CursorBottom = cursorBottom
            };
        }

        private void DrawArrow(CanvasControl sender, CanvasDrawingSession ds, Point start, Point end)
        {
            const float strokeWidth = 3f;
            const float arrowSize = 10f;
            var color = Colors.Red;

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double angle;

            if (dist < 8)
            {
                var centerX = sender.ActualWidth / 2;
                var centerY = sender.ActualHeight / 2;
                angle = Math.Atan2(centerY - start.Y, centerX - start.X);
            }
            else
            {
                angle = Math.Atan2(dy, dx);
                ds.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, color, strokeWidth);
            }

            float x1 = (float)(end.X - arrowSize * Math.Cos(angle - Math.PI / 6));
            float y1 = (float)(end.Y - arrowSize * Math.Sin(angle - Math.PI / 6));
            float x2 = (float)(end.X - arrowSize * Math.Cos(angle + Math.PI / 6));
            float y2 = (float)(end.Y - arrowSize * Math.Sin(angle + Math.PI / 6));

            using (var builder = new CanvasPathBuilder(sender))
            {
                builder.BeginFigure((float)end.X, (float)end.Y);
                builder.AddLine(x1, y1);
                builder.AddLine(x2, y2);
                builder.EndFigure(CanvasFigureLoop.Closed);
                using (var geometry = CanvasGeometry.CreatePath(builder))
                {
                    ds.FillGeometry(geometry, color);
                    ds.DrawGeometry(geometry, color, strokeWidth);
                }
            }
        }

        private void DrawBox(CanvasDrawingSession ds, Point start, Point end)
        {
            var rect = NormalizeRect(start, end);
            ds.DrawRectangle(rect, Colors.Red, 3);
        }

        private void DrawBlurRegions(CanvasDrawingSession ds, IEnumerable<Rect> regions, double offsetX, double offsetY)
        {
            if (regions is ICollection<Rect> collection && collection.Count == 0) return;

            var blurEffect = new GaussianBlurEffect
            {
                Source = Snapshot,
                BlurAmount = BlurAmount
            };

            foreach (var region in regions)
            {
                var clippedRegion = new Rect(region.X - offsetX, region.Y - offsetY, region.Width, region.Height);
                if (clippedRegion.Width <= 0 || clippedRegion.Height <= 0) continue;

                using (ds.CreateLayer(1, clippedRegion))
                {
                    ds.DrawImage(blurEffect, (float)-offsetX, (float)-offsetY);
                }
            }
        }

        private void MyCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (isDragging) return;

            var point = e.GetCurrentPoint(MyCanvas).Position;
            var now = DateTime.Now;
            bool isDoubleClick = lastClickTime.HasValue && (now - lastClickTime.Value).TotalMilliseconds < 300;
            lastClickTime = now;

            if (action == ActiveActions.Crop)
            {
                if (isDoubleClick)
                {
                    cropSelection = new Rect(0, 0, MyCanvas.ActualWidth, MyCanvas.ActualHeight);
                    isDragging = false;
                    dragStart = null;
                    dragEnd = null;
                    MyCanvas.Invalidate();
                    return;
                }

                isDragging = true;
                dragStart = point;
                dragEnd = point;
                cropSelection = null;
                MyCanvas.Invalidate();
            }
            else if (action == ActiveActions.Arrow || action == ActiveActions.Box || action == ActiveActions.Blur)
            {
                isDragging = true;
                dragStart = point;
                dragEnd = point;
                MyCanvas.Invalidate();
            }
            else if (action == ActiveActions.Text)
            {
                if (isTextEditing)
                {
                    CommitText();
                }
                textOrigin = point;
                currentText = string.Empty;
                cursorIndex = 0;
                isTextEditing = true;
                showCursor = true;
                cursorTimer.Start();
                RootGrid.Focus(FocusState.Programmatic);
                MyCanvas.Invalidate();
            }
        }

        private void MyCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!isDragging) return;

            var point = e.GetCurrentPoint(MyCanvas).Position;
            dragEnd = point;

            MyCanvas.Invalidate();
        }

        private void MyCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!isDragging) return;

            var point = e.GetCurrentPoint(MyCanvas).Position;
            isDragging = false;

            if (action == ActiveActions.Crop)
            {
                cropSelection = NormalizeRect(dragStart!.Value, point);
            }
            else if (action == ActiveActions.Blur)
            {
                var region = NormalizeRect(dragStart!.Value, point);
                if (region.Width > 0 && region.Height > 0)
                {
                    blurRegions.Add(region);
                }
            }
            else if (action == ActiveActions.Arrow || action == ActiveActions.Box)
            {
                Point end = point;
                if (action == ActiveActions.Arrow)
                {
                    double dx = point.X - dragStart!.Value.X;
                    double dy = point.Y - dragStart!.Value.Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 5)
                    {
                        end = dragStart!.Value;
                    }
                }

                shapes.Add(new DrawnShape
                {
                    Type = action,
                    Start = dragStart!.Value,
                    End = end
                });
            }

            dragStart = null;
            dragEnd = null;
            MyCanvas.Invalidate();
        }

        private async void MyCanvas_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            var key = e.Key;

            if (key == VirtualKey.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            var controlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            bool isControlDown = (controlState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            if (key == VirtualKey.C && isControlDown)
            {
                e.Handled = true;
                await CopyAndCloseAsync();
                return;
            }

            if (!isTextEditing) return;

            if (key == VirtualKey.Enter)
            {
                currentText = currentText.Insert(cursorIndex, "\n");
                cursorIndex++;
                showCursor = true;
                MyCanvas.Invalidate();
                e.Handled = true;
                return;
            }

            if (key == VirtualKey.Left)
            {
                if (cursorIndex > 0)
                {
                    cursorIndex--;
                    showCursor = true;
                    MyCanvas.Invalidate();
                }
                e.Handled = true;
                return;
            }

            if (key == VirtualKey.Right)
            {
                if (cursorIndex < currentText.Length)
                {
                    cursorIndex++;
                    showCursor = true;
                    MyCanvas.Invalidate();
                }
                e.Handled = true;
                return;
            }

            if (key == VirtualKey.Home)
            {
                cursorIndex = 0;
                showCursor = true;
                MyCanvas.Invalidate();
                e.Handled = true;
                return;
            }

            if (key == VirtualKey.End)
            {
                cursorIndex = currentText.Length;
                showCursor = true;
                MyCanvas.Invalidate();
                e.Handled = true;
                return;
            }

            if (key == VirtualKey.Delete)
            {
                if (cursorIndex < currentText.Length)
                {
                    currentText = currentText.Remove(cursorIndex, 1);
                    showCursor = true;
                    MyCanvas.Invalidate();
                }
                e.Handled = true;
                return;
            }

            if (key == VirtualKey.Back)
            {
                if (cursorIndex > 0)
                {
                    currentText = currentText.Remove(cursorIndex - 1, 1);
                    cursorIndex--;
                    showCursor = true;
                    MyCanvas.Invalidate();
                }
                e.Handled = true;
                return;
            }

            if (key == VirtualKey.Space)
            {
                currentText = currentText.Insert(cursorIndex, " ");
                cursorIndex++;
                showCursor = true;
                MyCanvas.Invalidate();
                e.Handled = true;
                return;
            }

            char? character = null;

            if (key >= VirtualKey.A && key <= VirtualKey.Z)
            {
                character = (char)key;
                var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                bool isShiftDown = (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
                if (!isShiftDown)
                    character = char.ToLower((char)character);
            }
            else if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            {
                character = (char)key;
            }
            else if (key == VirtualKey.NumberPad0) character = '0';
            else if (key == VirtualKey.NumberPad1) character = '1';
            else if (key == VirtualKey.NumberPad2) character = '2';
            else if (key == VirtualKey.NumberPad3) character = '3';
            else if (key == VirtualKey.NumberPad4) character = '4';
            else if (key == VirtualKey.NumberPad5) character = '5';
            else if (key == VirtualKey.NumberPad6) character = '6';
            else if (key == VirtualKey.NumberPad7) character = '7';
            else if (key == VirtualKey.NumberPad8) character = '8';
            else if (key == VirtualKey.NumberPad9) character = '9';
            else if (key == (VirtualKey)190) character = '.';
            else if (key == (VirtualKey)188) character = ',';
            else if (key == (VirtualKey)186) character = ';';
            else if (key == (VirtualKey)191) character = '/';
            else if (key == (VirtualKey)189) character = '-';
            else if (key == (VirtualKey)187) character = '=';
            else if (key == (VirtualKey)219) character = '[';
            else if (key == (VirtualKey)221) character = ']';
            else if (key == (VirtualKey)222) character = '\'';
            else if (key == (VirtualKey)192) character = '`';
            else if (key == (VirtualKey)220) character = '\\';

            if (character.HasValue)
            {
                currentText = currentText.Insert(cursorIndex, character.Value.ToString());
                cursorIndex++;
                showCursor = true;
                MyCanvas.Invalidate();
                e.Handled = true;
            }
        }

        private void CancelKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            Close();
        }

        private async void CopyKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            await CopyAndCloseAsync();
        }

        private void CommitText()
        {
            if (isTextEditing)
            {
                cursorTimer.Stop();
                if (!string.IsNullOrEmpty(currentText))
                {
                    shapes.Add(new DrawnShape
                    {
                        Type = ActiveActions.Text,
                        Start = textOrigin,
                        Text = currentText
                    });
                }
                isTextEditing = false;
                currentText = string.Empty;
                cursorIndex = 0;
                MyCanvas.Invalidate();
            }
        }

        private void UpdateActiveButton()
        {
            CropButton.IsChecked = action == ActiveActions.Crop;
            ArrowButton.IsChecked = action == ActiveActions.Arrow;
            BoxButton.IsChecked = action == ActiveActions.Box;
            TextButton.IsChecked = action == ActiveActions.Text;
            BlurButton.IsChecked = action == ActiveActions.Blur;
        }

        private void CropButton_Click(object sender, RoutedEventArgs e)
        {
            action = ActiveActions.Crop;
            CommitText();
            UpdateActiveButton();
            MyCanvas.Invalidate();
        }

        private void ArrowButton_Click(object sender, RoutedEventArgs e)
        {
            action = ActiveActions.Arrow;
            CommitText();
            UpdateActiveButton();
            MyCanvas.Invalidate();
        }

        private void BoxButton_Click(object sender, RoutedEventArgs e)
        {
            action = ActiveActions.Box;
            CommitText();
            UpdateActiveButton();
            MyCanvas.Invalidate();
        }

        private void TextButton_Click(object sender, RoutedEventArgs e)
        {
            action = ActiveActions.Text;
            UpdateActiveButton();
            MyCanvas.Invalidate();
        }

        private void BlurButton_Click(object sender, RoutedEventArgs e)
        {
            action = ActiveActions.Blur;
            CommitText();
            UpdateActiveButton();
            MyCanvas.Invalidate();
        }

        private async void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            await CopyAndCloseAsync();
        }

        private async void CopyTextButton_Click(object sender, RoutedEventArgs e)
        {
            await CopyTextAndCloseAsync();
        }

        private async Task CopyAndCloseAsync()
        {
            if (isCopying) return;
            isCopying = true;

            try
            {
                CommitText();
                var image = await RenderFinalImageAsync();
                if (image is null) return;

                var stream = new InMemoryRandomAccessStream();
                await image.SaveAsync(stream, CanvasBitmapFileFormat.Png);
                stream.Seek(0);

                var dataPackage = new DataPackage();
                dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(dataPackage);

                var result = await RenderSavedImageResultAsync();
                if (result is not null)
                {
                    ImageSaved?.Invoke(this, result);
                }

                image.Dispose();
                Close();
            }
            finally
            {
                isCopying = false;
            }
        }

        private async Task CopyTextAndCloseAsync()
        {
            if (isCopyingText) return;
            if (string.IsNullOrWhiteSpace(App.Settings.Current.GeminiApiKey))
            {
                await ShowCopyTextErrorAsync("A Gemini API key is required to copy text from a snapshot.");
                return;
            }

            isCopyingText = true;
            CopyTextButton.IsEnabled = false;
            CopyTextButton.Label = "Reading…";
            CanvasBitmap? image = null;
            string? temporaryImagePath = null;
            string? remoteFileName = null;

            try
            {
                CommitText();
                image = await RenderFinalImageAsync() ?? throw new InvalidOperationException("The selected snapshot could not be rendered.");
                temporaryImagePath = Path.Combine(Path.GetTempPath(), $"CrsterSnapshot-{Guid.NewGuid():N}.png");
                await image.SaveAsync(temporaryImagePath, CanvasBitmapFileFormat.Png);

                using var client = new GeminiClient(App.Settings.Current.GeminiApiKey);
                var attachment = await client.UploadFileAsync(temporaryImagePath, CancellationToken.None);
                remoteFileName = attachment.RemoteName;
                var request = GeminiClient.CreateUserStep(
                    "Analyze the attached image. First, extract every readable piece of text exactly, preserving useful line breaks. If there is no readable text, provide one short, factual description of the image. Return only the extracted text or description, with no introduction or labels.",
                    [attachment]);
                var response = await client.CreateSimpleInteractionAsync(
                    "gemini-2.5-flash-lite",
                    [],
                    [request],
                    "You perform precise OCR. Prefer verbatim transcription whenever the image contains readable text; describe the image only when no text can be read.",
                    null,
                    CancellationToken.None);
                var text = response.Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Gemini returned no text for the selected image.");

                var dataPackage = new DataPackage();
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);

                var result = await RenderSavedImageResultAsync();
                if (result is not null)
                {
                    ImageSaved?.Invoke(this, result);
                }

                Close();
            }
            catch (Exception exception)
            {
                await ShowCopyTextErrorAsync($"Copy Text failed: {exception.Message}");
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(remoteFileName))
                {
                    try
                    {
                        using var client = new GeminiClient(App.Settings.Current.GeminiApiKey);
                        await client.DeleteFileAsync(remoteFileName, CancellationToken.None);
                    }
                    catch { }
                }
                if (!string.IsNullOrWhiteSpace(temporaryImagePath))
                {
                    try { File.Delete(temporaryImagePath); }
                    catch { }
                }
                image?.Dispose();
                CopyTextButton.Label = "Read";
                CopyTextButton.IsEnabled = true;
                isCopyingText = false;
            }
        }

        private async Task ShowCopyTextErrorAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Read",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = RootGrid.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            CommitText();
            var image = await RenderFinalImageAsync();
            if (image is null) return;

            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg", ".jpeg" });
            picker.FileTypeChoices.Add("Bitmap image", new List<string> { ".bmp" });
            picker.SuggestedFileName = "snapshot";

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                CanvasBitmapFileFormat format = file.FileType.ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => CanvasBitmapFileFormat.Jpeg,
                    ".bmp" => CanvasBitmapFileFormat.Bmp,
                    _ => CanvasBitmapFileFormat.Png
                };
                await image.SaveAsync(file.Path, format);
            }

            var result = await RenderSavedImageResultAsync();
            if (result is not null)
            {
                ImageSaved?.Invoke(this, result);
            }

            image.Dispose();
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async Task<SavedImageResult?> RenderSavedImageResultAsync()
        {
            var bitmap = await RenderFinalImageAsync();
            if (bitmap is null) return null;

            var stream = new InMemoryRandomAccessStream();
            await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png);
            stream.Seek(0);

            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            stream.Seek(0);
            var data = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(data);
            }

            var palette = ColorPaletteService.ExtractTopUniqueColors(bitmap);

            bitmap.Dispose();
            return new SavedImageResult
            {
                Image = image,
                Data = data,
                PaletteColors = palette
            };
        }

        private async Task<CanvasBitmap?> RenderFinalImageAsync()
        {
            var device = CanvasDevice.GetSharedDevice();
            float width = (float)MyCanvas.ActualWidth;
            float height = (float)MyCanvas.ActualHeight;

            if (width <= 0 || height <= 0)
                return null;

            Rect cropRect = new Rect(0, 0, width, height);
            if (cropSelection.HasValue)
            {
                cropRect = cropSelection.Value;
            }

            float cropWidth = (float)cropRect.Width;
            float cropHeight = (float)cropRect.Height;
            if (cropWidth <= 0 || cropHeight <= 0)
                return null;

            var renderTarget = new CanvasRenderTarget(device, cropWidth, cropHeight, 96);
            using (var ds = renderTarget.CreateDrawingSession())
            {
                ds.DrawImage(Snapshot, new Rect(0, 0, cropWidth, cropHeight), cropRect);

                DrawBlurRegions(ds, blurRegions, cropRect.X, cropRect.Y);

                foreach (var shape in shapes)
                {
                    if (shape.Type == ActiveActions.Arrow)
                        DrawArrowForExport(ds, shape.Start, shape.End, cropRect.X, cropRect.Y);
                    else if (shape.Type == ActiveActions.Box)
                        DrawBoxForExport(ds, shape.Start, shape.End, cropRect.X, cropRect.Y);
                    else if (shape.Type == ActiveActions.Text)
                        DrawTextForExport(ds, shape.Text, shape.Start, cropRect.X, cropRect.Y);
                }
            }

            return CanvasBitmap.CreateFromDirect3D11Surface(device, renderTarget);
        }

        private void DrawArrowForExport(CanvasDrawingSession ds, Point start, Point end, double offsetX, double offsetY)
        {
            const float strokeWidth = 3f;
            const float arrowSize = 10f;
            var color = Colors.Red;

            float sx = (float)(start.X - offsetX);
            float sy = (float)(start.Y - offsetY);
            float ex = (float)(end.X - offsetX);
            float ey = (float)(end.Y - offsetY);

            double dx = ex - sx;
            double dy = ey - sy;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double angle;

            if (dist < 8)
            {
                angle = Math.Atan2(ey - sy, ex - sx);
            }
            else
            {
                angle = Math.Atan2(dy, dx);
                ds.DrawLine(sx, sy, ex, ey, color, strokeWidth);
            }

            float x1 = (float)(ex - arrowSize * Math.Cos(angle - Math.PI / 6));
            float y1 = (float)(ey - arrowSize * Math.Sin(angle - Math.PI / 6));
            float x2 = (float)(ex - arrowSize * Math.Cos(angle + Math.PI / 6));
            float y2 = (float)(ey - arrowSize * Math.Sin(angle + Math.PI / 6));

            using (var builder = new CanvasPathBuilder(CanvasDevice.GetSharedDevice()))
            {
                builder.BeginFigure(ex, ey);
                builder.AddLine(x1, y1);
                builder.AddLine(x2, y2);
                builder.EndFigure(CanvasFigureLoop.Closed);
                using (var geometry = CanvasGeometry.CreatePath(builder))
                {
                    ds.FillGeometry(geometry, color);
                    ds.DrawGeometry(geometry, color, strokeWidth);
                }
            }
        }

        private void DrawBoxForExport(CanvasDrawingSession ds, Point start, Point end, double offsetX, double offsetY)
        {
            var rect = NormalizeRect(
                new Point(start.X - offsetX, start.Y - offsetY),
                new Point(end.X - offsetX, end.Y - offsetY));
            ds.DrawRectangle(rect, Colors.Red, 3);
        }

        private void DrawTextForExport(CanvasDrawingSession ds, string text, Point origin, double offsetX, double offsetY)
        {
            float drawX = (float)(origin.X - offsetX);
            float drawY = (float)(origin.Y - offsetY);
            ds.DrawText(text, drawX, drawY, Colors.Red, TextFormat);
        }

        private struct TextMetrics
        {
            public float DrawX;
            public float DrawY;
            public float CursorX;
            public float CursorTop;
            public float CursorBottom;
        }
    }
}
