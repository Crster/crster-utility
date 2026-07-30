using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace App.Controls
{
    public sealed partial class ResizeHandle : UserControl
    {
        private readonly Border _surface;
        private readonly ScaleTransform _scaleTransform = new();

        public ResizeHandle()
        {
            _surface = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Content = _surface;
            RenderTransform = _scaleTransform;
            RenderTransformOrigin = new Point(0.5, 0.5);
            PointerEntered += (_, _) => ShowResizeAffordance();
            PointerExited += (_, _) => HideResizeAffordance();
        }

        public bool IsVerticalResize { get; set; }

        private void ShowResizeAffordance()
        {
            ProtectedCursor = InputSystemCursor.Create(
                IsVerticalResize ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
            _surface.Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
                ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
            _surface.Width = IsVerticalResize ? double.NaN : 2;
            _surface.Height = IsVerticalResize ? 2 : double.NaN;
            _surface.HorizontalAlignment = IsVerticalResize ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            _surface.VerticalAlignment = IsVerticalResize ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        }

        private void HideResizeAffordance()
        {
            ProtectedCursor = null;
            _surface.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            _surface.Width = double.NaN;
            _surface.Height = double.NaN;
            _surface.HorizontalAlignment = HorizontalAlignment.Stretch;
            _surface.VerticalAlignment = VerticalAlignment.Stretch;
            _scaleTransform.ScaleX = 1;
            _scaleTransform.ScaleY = 1;
        }
    }
}
