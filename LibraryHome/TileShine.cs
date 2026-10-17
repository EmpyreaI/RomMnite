using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace RomM.LibraryHome
{
    /// <summary>
    /// The lift of a cover pointed at in Steam's library: it grows a little, casts a shadow over
    /// its neighbours, and a band of light lies across it that follows the pointer, as a glossy
    /// card catching the light.
    ///
    /// The shadow is an element of its own, blurred, behind the cover: an effect on the cover
    /// itself would have WPF draw the cover (its words included) as a picture and then scale
    /// that picture, which blurs the text.
    /// </summary>
    internal static class TileShine
    {
        private static readonly Duration Quick = new Duration(TimeSpan.FromMilliseconds(170));
        private static readonly IEasingFunction Ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        /// <param name="tile">What grows (the whole cover, with its edge).</param>
        /// <param name="frame">The cover's clipped picture area, which the light is laid over.</param>
        /// <param name="grow">How much it grows: 1.05 is five percent.</param>
        /// <param name="zoomOnly">
        /// For a card with words and buttons on it (the Recent Games hero): only this (its
        /// picture) grows, inside the card's frame, and the card itself stays put, so its text
        /// and buttons are never scaled.
        /// </param>
        public static void Apply(FrameworkElement tile, Grid frame, double grow = 1.05, FrameworkElement zoomOnly = null)
        {
            if (tile == null || frame == null)
                return;

            var scale = new ScaleTransform(1, 1);
            FrameworkElement grower = zoomOnly ?? tile;
            grower.RenderTransformOrigin = new Point(0.5, 0.5);
            grower.RenderTransform = scale;

            // The light: a soft diagonal band across a strip twice the cover's width, slid so
            // the band sits under the pointer. The frame clips what lies outside it.
            var light = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.45) };
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.36));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 0.45));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), 0.5));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 0.55));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.64));
            // On a canvas, which lets the strip run past the cover's edges (the frame clips it);
            // an element wider than its place would be cut to that place before sliding.
            var sheen = new Border
            {
                Background = light,
                IsHitTestVisible = false,
                Opacity = 0,
            };
            var stage = new Canvas { IsHitTestVisible = false };
            stage.Children.Add(sheen);
            frame.Children.Add(stage);

            Func<double> width = () => !double.IsNaN(frame.Width) && frame.Width > 0 ? frame.Width : frame.ActualWidth;
            Func<double> height = () => !double.IsNaN(frame.Height) && frame.Height > 0 ? frame.Height : frame.ActualHeight;
            Action<Point> follow = at =>
            {
                double w = width();
                if (w <= 0)
                    return;
                sheen.Width = w * 2;
                sheen.Height = Math.Max(0, height());
                // The band (the strip's middle) under the pointer, leaning a little ahead of it
                // so the light seems to come from above.
                Canvas.SetLeft(sheen, Math.Max(-w * 1.3, Math.Min(w * 0.3, at.X - w * 1.1)));
            };

            // The shadow: a blurred dark copy of the cover's shape behind it, shown only while
            // lifted (a blur on every cover would slow scrolling). Only a cover that lifts has one.
            Border shadow = null;
            if (zoomOnly == null && tile is Border box && box.Child is UIElement inside)
            {
                box.Child = null;
                var holder = new Grid();
                shadow = new Border
                {
                    Background = Brushes.Black,
                    IsHitTestVisible = false,
                    Opacity = 0,
                    Visibility = Visibility.Collapsed,
                    RenderTransform = new TranslateTransform(0, 6),
                    Effect = new BlurEffect { Radius = 22, KernelType = KernelType.Gaussian },
                };
                holder.Children.Add(shadow);
                holder.Children.Add(inside);
                box.Child = holder;
            }
            bool over = false;

            tile.MouseEnter += (s, e) =>
            {
                over = true;
                if (zoomOnly == null)
                    Lift(tile, 1);
                if (shadow != null)
                {
                    shadow.Visibility = Visibility.Visible;
                    Animate(shadow, UIElement.OpacityProperty, 0.65);
                }
                Animate(scale, ScaleTransform.ScaleXProperty, grow);
                Animate(scale, ScaleTransform.ScaleYProperty, grow);
                follow(e.GetPosition(frame));
                Animate(sheen, UIElement.OpacityProperty, 1);
            };
            tile.MouseMove += (s, e) => follow(e.GetPosition(frame));
            tile.MouseLeave += (s, e) =>
            {
                over = false;
                Animate(scale, ScaleTransform.ScaleXProperty, 1);
                Animate(scale, ScaleTransform.ScaleYProperty, 1);
                Animate(sheen, UIElement.OpacityProperty, 0);
                if (shadow == null)
                {
                    if (zoomOnly == null)
                        Lift(tile, 0);
                    return;
                }
                var fade = new DoubleAnimation(0, Quick) { EasingFunction = Ease };
                fade.Completed += (a, b) =>
                {
                    // Settled: back in line with its neighbours, and no shadow left to draw.
                    if (over)
                        return;
                    shadow.Visibility = Visibility.Collapsed;
                    Lift(tile, 0);
                };
                shadow.BeginAnimation(UIElement.OpacityProperty, fade);
            };
        }

        private static void Animate(Animatable target, DependencyProperty property, double to)
        {
            target.BeginAnimation(property, new DoubleAnimation(to, Quick) { EasingFunction = Ease });
        }

        private static void Animate(UIElement target, DependencyProperty property, double to)
        {
            target.BeginAnimation(property, new DoubleAnimation(to, Quick) { EasingFunction = Ease });
        }

        // Drawn over its neighbours while lifted: the cover, and the groups holding it, are
        // put in front within their own rows.
        private static void Lift(FrameworkElement tile, int z)
        {
            DependencyObject node = tile;
            for (int depth = 0; node != null && depth < 4; depth++)
            {
                DependencyObject parent = VisualTreeHelper.GetParent(node);
                if (parent is Panel && node is UIElement element)
                    Panel.SetZIndex(element, z);
                if (parent is ScrollViewer || parent is ScrollContentPresenter)
                    break;
                node = parent;
            }
        }
    }
}
