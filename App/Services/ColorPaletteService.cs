using Microsoft.Graphics.Canvas;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;

namespace App.Services
{
    public static class ColorPaletteService
    {
        public static List<ThemeColor> ExtractBootstrapTheme(CanvasBitmap bitmap)
        {
            var colors = ExtractDominantColors(bitmap);
            return AssignBootstrapTheme(colors);
        }

        private static List<(Color color, int freq)> ExtractDominantColors(CanvasBitmap bitmap)
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
                .Select(kvp =>
                {
                    byte r = (byte)((kvp.Key >> 16) & 0xFF);
                    byte g = (byte)((kvp.Key >> 8) & 0xFF);
                    byte b = (byte)(kvp.Key & 0xFF);
                    return (Color.FromArgb(255, r, g, b), kvp.Value);
                })
                .OrderByDescending(x => x.Item2)
                .ToList();
        }

        private static List<ThemeColor> AssignBootstrapTheme(List<(Color color, int freq)> dominantColors)
        {
            var result = new List<ThemeColor>();
            var used = new HashSet<int>();
            var candidates = dominantColors.Select(x => x.color).ToList();

            int PickBest(Func<Color, double> scoreFn)
            {
                double bestScore = double.MinValue;
                int bestIdx = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    var score = scoreFn(candidates[i]);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }
                if (bestIdx >= 0)
                    used.Add(bestIdx);
                return bestIdx;
            }

            // 1. Primary - blue-ish, saturated, medium-bright
            int idx = PickBest(c => ScorePrimary(RgbToHsv(c)));
            var primary = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 13, 110, 253);
            result.Add(new ThemeColor { Name = "Primary", Color = primary });

            // 2. Success - green
            idx = PickBest(c => ScoreSuccess(RgbToHsv(c)));
            var success = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 25, 135, 84);
            result.Add(new ThemeColor { Name = "Success", Color = success });

            // 3. Danger - red
            idx = PickBest(c => ScoreDanger(RgbToHsv(c)));
            var danger = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 220, 53, 69);
            result.Add(new ThemeColor { Name = "Danger", Color = danger });

            // 4. Warning - yellow/orange
            idx = PickBest(c => ScoreWarning(RgbToHsv(c)));
            var warning = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 255, 193, 7);
            result.Add(new ThemeColor { Name = "Warning", Color = warning });

            // 5. Info - cyan/light blue
            idx = PickBest(c => ScoreInfo(RgbToHsv(c)));
            var info = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 13, 202, 240);
            result.Add(new ThemeColor { Name = "Info", Color = info });

            // 6. Secondary - gray/neutral
            idx = PickBest(c => ScoreSecondary(RgbToHsv(c)));
            var secondary = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 108, 117, 125);
            result.Add(new ThemeColor { Name = "Secondary", Color = secondary });

            // 7. Container - light neutral/off-white
            idx = PickBest(c => ScoreContainer(RgbToHsv(c)));
            var container = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 248, 249, 250);
            result.Add(new ThemeColor { Name = "Container", Color = container });

            // 8. Card - white/very light
            idx = PickBest(c => ScoreCard(RgbToHsv(c)));
            var card = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 255, 255, 255);
            result.Add(new ThemeColor { Name = "Card", Color = card });

            // 9. Background1 - very light
            idx = PickBest(c => ScoreBackground1(RgbToHsv(c)));
            var bg1 = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 245, 245, 245);
            result.Add(new ThemeColor { Name = "Background 1", Color = bg1 });

            // 10. Background2 - light gray
            idx = PickBest(c => ScoreBackground2(RgbToHsv(c)));
            var bg2 = idx >= 0 ? candidates[idx] : Color.FromArgb(255, 233, 236, 239);
            result.Add(new ThemeColor { Name = "Background 2", Color = bg2 });

            return result;
        }

        private static (double H, double S, double V) RgbToHsv(Color c)
        {
            double r = c.R / 255.0;
            double g = c.G / 255.0;
            double b = c.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double diff = max - min;

            double h = 0;
            if (diff > 0.0001)
            {
                if (max == r)
                    h = (60 * ((g - b) / diff) + 360) % 360;
                else if (max == g)
                    h = (60 * ((b - r) / diff) + 120) % 360;
                else
                    h = (60 * ((r - g) / diff) + 240) % 360;
            }

            double s = max == 0 ? 0 : diff / max;
            double v = max;

            return (h, s * 100, v * 100);
        }

        private static double ScorePrimary((double H, double S, double V) hsv)
        {
            double hueScore = HueDistance(hsv.H, 225); // Bootstrap primary ~ #0d6efd
            double satScore = hsv.S > 30 ? Math.Min(100, hsv.S) : 0;
            double valScore = hsv.V > 30 ? Math.Min(100, hsv.V) : 0;
            return hueScore * 0.5 + satScore * 0.3 + valScore * 0.2;
        }

        private static double ScoreSuccess((double H, double S, double V) hsv)
        {
            double hueScore = Math.Max(HueDistance(hsv.H, 145), HueDistance(hsv.H, 120));
            double satScore = hsv.S > 30 ? Math.Min(100, hsv.S) : 0;
            double valScore = hsv.V > 25 ? Math.Min(100, hsv.V) : 0;
            return hueScore * 0.5 + satScore * 0.3 + valScore * 0.2;
        }

        private static double ScoreDanger((double H, double S, double V) hsv)
        {
            double hueScore = Math.Max(HueDistance(hsv.H, 350), HueDistance(hsv.H, 5));
            double satScore = hsv.S > 30 ? Math.Min(100, hsv.S) : 0;
            double valScore = hsv.V > 30 ? Math.Min(100, hsv.V) : 0;
            return hueScore * 0.5 + satScore * 0.3 + valScore * 0.2;
        }

        private static double ScoreWarning((double H, double S, double V) hsv)
        {
            double hueScore = Math.Max(HueDistance(hsv.H, 45), HueDistance(hsv.H, 30));
            double satScore = hsv.S > 25 ? Math.Min(100, hsv.S) : 0;
            double valScore = hsv.V > 50 ? Math.Min(100, hsv.V) : 0;
            return hueScore * 0.45 + satScore * 0.25 + valScore * 0.3;
        }

        private static double ScoreInfo((double H, double S, double V) hsv)
        {
            double hueScore = HueDistance(hsv.H, 190);
            double satScore = hsv.S > 20 ? Math.Min(100, hsv.S) : 0;
            double valScore = hsv.V > 50 ? Math.Min(100, hsv.V) : 0;
            return hueScore * 0.45 + satScore * 0.25 + valScore * 0.3;
        }

        private static double ScoreSecondary((double H, double S, double V) hsv)
        {
            double hueScore = 60; // Neutral-ish, not heavily weighted
            double satScore = hsv.S < 35 ? (35 - hsv.S) * 2 : 0;
            double valScore = hsv.V > 30 && hsv.V < 85 ? (85 - Math.Abs(hsv.V - 55)) : Math.Max(0, 100 - Math.Abs(hsv.V - 55));
            return hueScore * 0.1 + satScore * 0.5 + valScore * 0.4;
        }

        private static double ScoreContainer((double H, double S, double V) hsv)
        {
            double satScore = hsv.S < 25 ? (25 - hsv.S) * 3 : 0;
            double valScore = hsv.V > 80 ? (hsv.V - 80) * 4 : 0;
            return satScore * 0.5 + valScore * 0.5;
        }

        private static double ScoreCard((double H, double S, double V) hsv)
        {
            double satScore = hsv.S < 15 ? (15 - hsv.S) * 5 : 0;
            double valScore = hsv.V > 90 ? (hsv.V - 90) * 6 : 0;
            return satScore * 0.4 + valScore * 0.6;
        }

        private static double ScoreBackground1((double H, double S, double V) hsv)
        {
            double satScore = hsv.S < 12 ? (12 - hsv.S) * 6 : 0;
            double valScore = hsv.V > 92 ? (hsv.V - 92) * 8 : 0;
            return satScore * 0.35 + valScore * 0.65;
        }

        private static double ScoreBackground2((double H, double S, double V) hsv)
        {
            double satScore = hsv.S < 15 ? (15 - hsv.S) * 4 : 0;
            double valScore = hsv.V > 85 && hsv.V < 95 ? (10 - Math.Abs(hsv.V - 90)) * 8 : 0;
            return satScore * 0.35 + valScore * 0.65;
        }

        private static double HueDistance(double hue, double target)
        {
            double diff = Math.Abs(hue - target);
            if (diff > 180) diff = 360 - diff;
            return Math.Max(0, 100 - diff);
        }
    }
}
