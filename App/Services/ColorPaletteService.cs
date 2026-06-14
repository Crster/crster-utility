using Microsoft.Graphics.Canvas;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;

namespace App.Services
{
    public static class ColorPaletteService
    {
        public static List<Color> ExtractPalette(CanvasBitmap bitmap, int colorCount = 10)
        {
            var bytes = bitmap.GetPixelBytes();
            var colorCounts = new Dictionary<int, int>();

            int totalPixels = bytes.Length / 4;
            int sampleStep = Math.Max(1, totalPixels / 5000);

            for (int i = 0; i < totalPixels; i += sampleStep)
            {
                int offset = i * 4;
                if (offset + 3 >= bytes.Length) continue;

                byte b = bytes[offset];
                byte g = bytes[offset + 1];
                byte r = bytes[offset + 2];
                byte a = bytes[offset + 3];

                if (a < 128) continue;

                byte qr = (byte)((r >> 3) << 3);
                byte qg = (byte)((g >> 3) << 3);
                byte qb = (byte)((b >> 3) << 3);

                int key = (qr << 16) | (qg << 8) | qb;

                if (colorCounts.ContainsKey(key))
                    colorCounts[key]++;
                else
                    colorCounts[key] = 1;
            }

            return colorCounts
                .OrderByDescending(kvp => kvp.Value)
                .Take(colorCount)
                .Select(kvp => Color.FromArgb(255, (byte)((kvp.Key >> 16) & 0xFF), (byte)((kvp.Key >> 8) & 0xFF), (byte)(kvp.Key & 0xFF)))
                .ToList();
        }
    }
}
