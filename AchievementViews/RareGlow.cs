using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RomM.AchievementViews
{
    /// <summary>
    /// The look of a rare achievement wherever its badge is shown (the overlay, game details,
    /// the Store): a gold edge that glows. Rare is a tenth of players or fewer (Steam's share),
    /// or for RetroAchievements a tenth or fewer of the players of the set's most earned one.
    /// </summary>
    internal static class RareGlow
    {
        public const double RareAt = 10;

        private static readonly Brush Gold = Frozen(Color.FromRgb(0xE5, 0xB1, 0x43));

        /// <summary>The badge's frame given the gold edge and glow (fainter while locked).</summary>
        public static void Apply(Border frame, bool unlocked)
        {
            frame.BorderBrush = Gold;
            frame.BorderThickness = new Thickness(2);
            frame.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0xC8, 0x4A),
                BlurRadius = 16,
                ShadowDepth = 0,
                Opacity = unlocked ? 0.95 : 0.6,
            };
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
