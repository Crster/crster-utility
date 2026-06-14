using System;
using Windows.UI;

namespace App.Services
{
    public class ThemeColor
    {
        public string Name { get; set; } = string.Empty;
        public Color Color { get; set; } = Color.FromArgb(255, 128, 128, 128);
        public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";
    }
}
