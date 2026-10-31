using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace RomM.Store
{
    /// <summary>
    /// The light words of encouragement under a long wait (the window's curtain while the library
    /// syncs, the Store's while Home loads): none for the first few seconds, then a new line every
    /// few seconds, faded through, from a different place in the list each time.
    /// </summary>
    internal static class LoadingCheer
    {
        /// <summary>From this long into a wait, a line.</summary>
        public static readonly TimeSpan After = TimeSpan.FromSeconds(4);

        /// <summary>A new line this often.</summary>
        public static readonly TimeSpan Every = TimeSpan.FromSeconds(4.5);

        private static readonly string[] s_lines =
        {
            "Dusting off the cartridges...",
            "Blowing on the contacts, just in case...",
            "Every great adventure starts with a short wait.",
            "Lining up the shelves...",
            "Your backlog believes in you.",
            "Polishing the discs...",
            "Good things come to those who wait.",
            "Rewinding the tapes...",
            "Almost there. Grab a drink?",
            "Sorting the classics from the hidden gems...",
            "Checkpoint reached. Your progress is safe.",
            "Saving your spot...",
        };
        private static int s_at = new Random().Next(s_lines.Length);

        /// <summary>A line of encouragement, built hidden; Next shows one.</summary>
        public static TextBlock Line(double fontSize)
        {
            return new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = fontSize,
                FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Opacity = 0,
                MinHeight = fontSize * 1.4,
            };
        }

        /// <summary>The next line, faded in (through the last one). On the UI thread.</summary>
        public static void Next(TextBlock line)
        {
            string text = s_lines[s_at % s_lines.Length];
            s_at++;
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            Action show = () =>
            {
                line.Text = text;
                line.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.55, TimeSpan.FromMilliseconds(400)) { EasingFunction = ease });
            };
            if (string.IsNullOrEmpty(line.Text))
            {
                show();
                return;
            }
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease };
            fadeOut.Completed += (s, e) => show();
            line.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        /// <summary>The line hidden and emptied, for the next wait.</summary>
        public static void Clear(TextBlock line)
        {
            line.BeginAnimation(UIElement.OpacityProperty, null);
            line.Opacity = 0;
            line.Text = "";
        }
    }
}
