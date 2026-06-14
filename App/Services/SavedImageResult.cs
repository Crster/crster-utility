using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Generic;

namespace App.Services
{
    public class SavedImageResult
    {
        public BitmapImage Image { get; set; } = null!;
        public List<ThemeColor> PaletteColors { get; set; } = new();
    }
}
