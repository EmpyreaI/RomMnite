using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RomM.Store
{
    /// <summary>
    /// A spinning loading circle: an open arc turning once a second. As an element of its own
    /// (<see cref="Create"/>, the Store's loading curtain), or laid over a picture still
    /// loading (<see cref="Attach"/>, by StoreImages) without touching the layout it sits in.
    /// </summary>
    internal static class Spinner
    {
        private static readonly Brush ArcBrush = Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        private static readonly Brush TrackBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>A spinner <paramref name="size"/> across, already turning.</summary>
        public static FrameworkElement Create(double size, double thickness = 3)
        {
            var grid = new Grid { Width = size, Height = size, IsHitTestVisible = false };
            grid.Children.Add(new Ellipse { Stroke = TrackBrush, StrokeThickness = thickness });
            double r = (size - thickness) / 2, c = size / 2;
            // A quarter of the circle left open.
            var arc = new Path
            {
                Stroke = ArcBrush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry(new[]
                {
                    new PathFigure(new Point(c, c - r), new[] { new ArcSegment(new Point(c - r, c), new Size(r, r), 0, true, SweepDirection.Clockwise, true) }, false),
                }),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var turn = new RotateTransform();
            arc.RenderTransform = turn;
            turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            grid.Children.Add(arc);
            return grid;
        }

        // ---- Over a picture -------------------------------------------------------------------

        private static readonly DependencyProperty AdornerProperty =
            DependencyProperty.RegisterAttached("RomMSpinner", typeof(SpinnerAdorner), typeof(Spinner));

        /// <summary>
        /// A spinner over the picture while it loads: over the image itself, or over its holder
        /// when the empty image takes no room yet. Pictures too small for one (badges, icons)
        /// get none. Once the picture is in its window, if it is not yet.
        /// </summary>
        public static void Attach(Image image)
        {
            if (image == null || image.GetValue(AdornerProperty) != null)
                return;
            if (!image.IsLoaded)
            {
                RoutedEventHandler loaded = null;
                loaded = (s, e) =>
                {
                    image.Loaded -= loaded;
                    if (image.Source == null && image.Tag != null && image.GetValue(PendingProperty) is bool pending && pending)
                        Attach(image);
                };
                image.Loaded += loaded;
                image.SetValue(PendingProperty, true);
                return;
            }
            UIElement host = image;
            if (image.ActualWidth < 40 || image.ActualHeight < 40)
            {
                var parent = image.Parent as FrameworkElement;
                bool roomy = parent != null && !(parent is StackPanel) && parent.ActualWidth >= 40 && parent.ActualHeight >= 40;
                host = roomy ? parent : null;
            }
            if (host == null)
                return;
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(host);
            if (layer == null)
                return;
            var adorner = new SpinnerAdorner(host);
            layer.Add(adorner);
            image.SetValue(AdornerProperty, adorner);
        }

        private static readonly DependencyProperty PendingProperty =
            DependencyProperty.RegisterAttached("RomMSpinnerPending", typeof(bool), typeof(Spinner), new PropertyMetadata(false));

        /// <summary>The picture is in (or will not come): its spinner taken away.</summary>
        public static void Detach(Image image)
        {
            if (image == null)
                return;
            image.SetValue(PendingProperty, false);
            if (!(image.GetValue(AdornerProperty) is SpinnerAdorner adorner))
                return;
            image.ClearValue(AdornerProperty);
            AdornerLayer.GetAdornerLayer(adorner.AdornedElement)?.Remove(adorner);
        }

        private sealed class SpinnerAdorner : Adorner
        {
            private const double Size = 26;
            private readonly FrameworkElement _spinner;

            public SpinnerAdorner(UIElement adorned) : base(adorned)
            {
                IsHitTestVisible = false;
                _spinner = Create(Size, 2.5);
                AddVisualChild(_spinner);
            }

            protected override int VisualChildrenCount => 1;

            protected override Visual GetVisualChild(int index) => _spinner;

            protected override Size MeasureOverride(Size constraint)
            {
                _spinner.Measure(new Size(Size, Size));
                return AdornedElement.RenderSize;
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                Size area = AdornedElement.RenderSize;
                _spinner.Arrange(new Rect((area.Width - Size) / 2, (area.Height - Size) / 2, Size, Size));
                return area;
            }
        }
    }
}
