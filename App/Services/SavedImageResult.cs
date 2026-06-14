using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Generic;
using Windows.UI;

namespace App.Services
{
    public class SavedImageResult
    {
        public BitmapImage Image { get; set; } = null!;
        public List<Color> PaletteColors { get; set; } = new();
    }
}
