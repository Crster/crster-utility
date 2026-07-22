using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace App.Services
{
    public static class ColorPaletteService
    {
        private const int PixelatedMaximumDimension = 128;
        private const int PaletteSize = 10;

        public static List<ThemeColor> ExtractTopUniqueColors(CanvasBitmap bitmap)
        {
            var pixels = bitmap.GetPixelBytes();
            var width = (int)bitmap.SizeInPixels.Width;
            var height = (int)bitmap.SizeInPixels.Height;
            if (width <= 0 || height <= 0 || pixels.Length < width * height * 4) return [];

            var pixelBlockSize = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)PixelatedMaximumDimension));
            var colorCounts = new Dictionary<int, int>();

            for (var top = 0; top < height; top += pixelBlockSize)
            {
                var bottom = Math.Min(top + pixelBlockSize, height);
                for (var left = 0; left < width; left += pixelBlockSize)
                {
                    var right = Math.Min(left + pixelBlockSize, width);
                    long redTotal = 0;
                    long greenTotal = 0;
                    long blueTotal = 0;
                    var visiblePixelCount = 0;

                    for (var y = top; y < bottom; y++)
                    {
                        for (var x = left; x < right; x++)
                        {
                            var offset = ((y * width) + x) * 4;
                            var alpha = pixels[offset + 3];
                            if (alpha < 128) continue;

                            blueTotal += pixels[offset];
                            greenTotal += pixels[offset + 1];
                            redTotal += pixels[offset + 2];
                            visiblePixelCount++;
                        }
                    }

                    if (visiblePixelCount == 0) continue;

                    var red = Quantize((byte)(redTotal / visiblePixelCount));
                    var green = Quantize((byte)(greenTotal / visiblePixelCount));
                    var blue = Quantize((byte)(blueTotal / visiblePixelCount));
                    var colorKey = (red << 16) | (green << 8) | blue;
                    colorCounts[colorKey] = colorCounts.GetValueOrDefault(colorKey) + 1;
                }
            }

            return colorCounts
                .OrderByDescending(color => color.Value)
                .ThenBy(color => color.Key)
                .Take(PaletteSize)
                .Select((color, index) => new ThemeColor
                {
                    Name = $"Color {index + 1}",
                    Color = Color.FromArgb(
                        255,
                        (byte)((color.Key >> 16) & 0xFF),
                        (byte)((color.Key >> 8) & 0xFF),
                        (byte)(color.Key & 0xFF))
                })
                .ToList();
        }

        private static byte Quantize(byte value) => (byte)((value >> 4) << 4);
    }
}
