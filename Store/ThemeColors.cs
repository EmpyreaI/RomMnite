using System;
using System.Windows;
using System.Windows.Media;

namespace RomM.Store
{
    /// <summary>
    /// Colours taken from the theme in use, so the plugin's bars and menus sit in its palette
    /// rather than in Steam's blue. Read from the theme's "MainColor" and "MainColorDark" (which
    /// FusionX, among others, defines); Steam-like defaults are used when a theme has neither.
    /// Everything returned is opaque, as content scrolls beneath the bar.
    /// </summary>
    internal static class ThemeColors
    {
        private static Color Get(string key, Color fallback)
        {
            try
            {
                object value = Application.Current?.TryFindResource(key);
                if (value is Color color)
                    return Color.FromRgb(color.R, color.G, color.B);
                if (value is SolidColorBrush brush)
                    return Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B);
            }
            catch
            {
                // Fall back below.
            }
            return fallback;
        }

        /// <summary>A colour <paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static Color Mix(Color from, Color to, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }

        public static Color Main => Get("MainColor", Color.FromRgb(0x2C, 0x42, 0x64));
        public static Color MainDark => Get("MainColorDark", Color.FromRgb(0x12, 0x18, 0x24));

        /// <summary>The Store's navigation band: the theme's main colour, darkening downward.</summary>
        public static Brush NavBand()
        {
            var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            band.GradientStops.Add(new GradientStop(Mix(MainDark, Main, 0.62), 0));
            band.GradientStops.Add(new GradientStop(Mix(MainDark, Main, 0.38), 1));
            return band;
        }

        /// <summary>Drop-down menus and the search suggestions.</summary>
        public static Brush Menu() => new SolidColorBrush(Mix(MainDark, Main, 0.5));

        /// <summary>A thin line along the band's lower edge.</summary>
        public static Brush Edge() => new SolidColorBrush(Mix(MainDark, Main, 0.85));
    }
}
