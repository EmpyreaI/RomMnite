using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.Store
{
    /// <summary>
    /// A sideways-scrolling row of covers with an arrow at each end, as Steam's shelves have.
    /// The arrows sit over the row's edges, appear only while there is more to see in their
    /// direction, and glide the row along by most of its visible width.
    /// </summary>
    internal static class SideScroll
    {
        private const double ArrowWidth = 36;
        private const double Stiffness = 12;

        /// <param name="bar">Also a scroll bar under the row (shown only when it overflows), for
        /// pages where the rows are worth dragging through, as a game's page.</param>
        public static UIElement Row(UIElement content, bool bar = false)
        {
            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = bar ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content,
                Padding = new Thickness(0, 0, 0, 6),
            };

            Border back = Arrow("‹", HorizontalAlignment.Left);
            Border forward = Arrow("›", HorizontalAlignment.Right);
            if (bar)
            {
                // The arrows stop above the bar, so its ends stay in reach. The bar is as tall
                // as the page's widened bars, once it shows.
                Action fit = () =>
                {
                    double below = 6 + (scroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? BarHeight(scroller) : 0);
                    back.Margin = new Thickness(0, 0, 0, below);
                    forward.Margin = new Thickness(0, 0, 0, below);
                };
                scroller.LayoutUpdated += (s, e) => fit();
            }
            back.MouseLeftButtonUp += (s, e) => { e.Handled = true; Glide(scroller, -1); };
            forward.MouseLeftButtonUp += (s, e) => { e.Handled = true; Glide(scroller, 1); };

            scroller.ScrollChanged += (s, e) =>
            {
                back.Visibility = scroller.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
                forward.Visibility = scroller.HorizontalOffset < scroller.ScrollableWidth - 1 ? Visibility.Visible : Visibility.Collapsed;
            };

            var holder = new Grid();
            holder.Children.Add(scroller);
            holder.Children.Add(back);
            holder.Children.Add(forward);
            return holder;
        }

        /// <summary>
        /// A sideways row whose arrows sit in its heading instead (as on Steam's library shelves):
        /// <paramref name="arrows"/> is the pair, for the heading's right end. Each dims when
        /// there is nothing more that way; the mouse wheel over the row scrolls it sideways too.
        /// </summary>
        public static UIElement RowWithHeaderArrows(UIElement content, out FrameworkElement arrows)
        {
            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content,
                Padding = new Thickness(0, 0, 0, 6),
            };

            TextBlock back = HeaderArrow("\u2039");
            TextBlock forward = HeaderArrow("\u203A");
            back.MouseLeftButtonUp += (s, e) => { e.Handled = true; Glide(scroller, -1); };
            forward.MouseLeftButtonUp += (s, e) => { e.Handled = true; Glide(scroller, 1); };
            Action paint = () =>
            {
                back.Opacity = scroller.HorizontalOffset > 1 ? 0.85 : 0.25;
                forward.Opacity = scroller.HorizontalOffset < scroller.ScrollableWidth - 1 ? 0.85 : 0.25;
            };
            scroller.ScrollChanged += (s, e) => paint();

            var pair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pair.Children.Add(back);
            pair.Children.Add(forward);
            arrows = pair;
            return scroller;
        }

        private static TextBlock HeaderArrow(string glyph)
        {
            var arrow = new TextBlock
            {
                Text = glyph,
                FontSize = 30,
                Foreground = Brushes.White,
                Margin = new Thickness(10, -10, 4, 0),
                Cursor = Cursors.Hand,
                Opacity = 0.25,
                Background = Brushes.Transparent,
            };
            return arrow;
        }

        // The height of a scroll viewer's sideways bar, as laid out (14 when not yet known).
        private static double BarHeight(ScrollViewer scroller)
        {
            var bar = scroller.Template?.FindName("PART_HorizontalScrollBar", scroller) as FrameworkElement;
            return bar != null && bar.ActualHeight > 0 ? bar.ActualHeight : 14;
        }

        private static Border Arrow(string glyph, HorizontalAlignment side)
        {
            var text = new TextBlock
            {
                Text = glyph,
                FontSize = 34,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            };
            var arrow = new Border
            {
                Width = ArrowWidth,
                HorizontalAlignment = side,
                Margin = new Thickness(0, 0, 0, 6),
                Background = new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x14, 0x1C)),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                Child = text,
                Opacity = 0.8,
            };
            arrow.MouseEnter += (s, e) => arrow.Opacity = 1;
            arrow.MouseLeave += (s, e) => arrow.Opacity = 0.8;
            return arrow;
        }

        // A row's one glide: where it is heading, and the frame handler moving it there.
        private sealed class GlideState
        {
            public double Target;
            public EventHandler Tick;
            public Stopwatch Clock;
        }

        private static readonly ConditionalWeakTable<ScrollViewer, GlideState> s_glides = new ConditionalWeakTable<ScrollViewer, GlideState>();

        /// <summary>
        /// Moves the row by most of its visible width, easing to a stop. One glide per row: a
        /// click while it moves retargets it rather than starting another. Clicks the same way
        /// add up (from where it was heading); a click the other way starts from where the row
        /// is now, so earlier clicks are never still pulling it back.
        /// </summary>
        private static void Glide(ScrollViewer scroller, int direction)
        {
            GlideState state = s_glides.GetOrCreateValue(scroller);
            double step = Math.Max(150, scroller.ViewportWidth - 160);
            bool moving = state.Tick != null;
            bool sameWay = moving && Math.Sign(state.Target - scroller.HorizontalOffset) == direction;
            double from = sameWay ? state.Target : scroller.HorizontalOffset;
            state.Target = Math.Max(0, Math.Min(scroller.ScrollableWidth, from + direction * step));
            if (moving)
                return;

            state.Clock = Stopwatch.StartNew();
            state.Tick = (s, e) =>
            {
                double seconds = Math.Min(0.1, state.Clock.Elapsed.TotalSeconds);
                state.Clock.Restart();
                double remaining = state.Target - scroller.HorizontalOffset;
                if (Math.Abs(remaining) < 0.5 || !scroller.IsVisible)
                {
                    scroller.ScrollToHorizontalOffset(state.Target);
                    CompositionTarget.Rendering -= state.Tick;
                    state.Tick = null;
                    return;
                }
                scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + remaining * (1 - Math.Exp(-Stiffness * seconds)));
            };
            CompositionTarget.Rendering += state.Tick;
        }
    }
}
