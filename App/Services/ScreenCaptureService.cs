using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage.Streams;

namespace App.Services
{
    public static class ScreenCaptureService
    {
        public static async Task<CanvasBitmap?> CaptureAsync()
        {
            try
            {
                var item = await GetMainDisplayItem();
                if (item == null) return null;

                var device = CanvasDevice.GetSharedDevice();
                using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    item.Size);

                using var session = framePool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = true;
                session.StartCapture();

                var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>();
                TypedEventHandler<Direct3D11CaptureFramePool, object>? handler = null;
                handler = (s, args) =>
                {
                    var frame = s.TryGetNextFrame();
                    if (frame != null)
                    {
                        s.FrameArrived -= handler;
                        tcs.TrySetResult(frame);
                    }
                };
                framePool.FrameArrived += handler;

                var currentFrame = await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task
                    ? await tcs.Task
                    : null;

                if (currentFrame != null)
                {
                    return CanvasBitmap.CreateFromDirect3D11Surface(device, currentFrame.Surface);
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error capturing screen: {ex.Message}");
                return null;
            }
        }

        private static async Task<GraphicsCaptureItem?> GetMainDisplayItem()
        {
            try
            {
                var accessResult = await AppCapability.Create("graphicsCaptureProgrammatic").RequestAccessAsync();
                if (accessResult != AppCapabilityAccessStatus.Allowed)
                {
                    Debug.WriteLine("Graphics capture access was not granted.");
                    return null;
                }

                var primaryDisplay = Microsoft.UI.Windowing.DisplayArea.Primary;
                var graphicsDisplayId = new DisplayId(primaryDisplay.DisplayId.Value);
                return GraphicsCaptureItem.TryCreateFromDisplayId(graphicsDisplayId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating capture item: {ex.Message}");
                return null;
            }
        }
    }
}
