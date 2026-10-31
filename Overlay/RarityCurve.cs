using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RomM.Overlay
{
    /// <summary>
    /// How rare one achievement is among its game's others: a smooth curve of how the set's
    /// achievements spread from rare (left) to common (right), with this one marked on it and the
    /// part rarer than it shaded. A single achievement has only one number (the share of players
    /// with it), so this is the set's spread, the one placed in it.
    ///
    /// The curve is a kernel density estimate of the set's scores (0-100: the share of players,
    /// or for RetroAchievements, players compared with the set's most earned), with Silverman's
    /// bandwidth (at least 4 points, so a small set still draws a smooth hill).
    /// </summary>
    internal sealed class RarityCurve : FrameworkElement
    {
        private static readonly Brush Fill = Frozen(Color.FromArgb(0x55, 0x1A, 0x9F, 0xFF));
        private static readonly Brush Rarer = Frozen(Color.FromArgb(0x60, 0xE5, 0xB1, 0x43));
        private static readonly Brush Gold = Frozen(Color.FromRgb(0xE5, 0xB1, 0x43));
        private static readonly Brush Muted = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        private static readonly Pen Line = FrozenPen(Color.FromRgb(0x1A, 0x9F, 0xFF), 2);
        private static readonly Pen Marker = FrozenPen(Color.FromRgb(0xE5, 0xB1, 0x43), 2);
        private static readonly Pen Axis = FrozenPen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 1);

        private readonly double[] _scores;
        private readonly double _mark;
        private readonly string _left, _right;
        private const double LabelRoom = 16;

        /// <param name="scores">Every achievement's score in the set (0 rarest, 100 most common).</param>
        /// <param name="mark">This one's.</param>
        public RarityCurve(IEnumerable<double> scores, double mark, string left, string right)
        {
            _scores = scores.Select(s => Math.Max(0, Math.Min(100, s))).ToArray();
            _mark = Math.Max(0, Math.Min(100, mark));
            _left = left;
            _right = right;
            Height = 86;
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        /// <summary>The share of the set's achievements rarer than this one (0-100).</summary>
        public static double RarerThan(IList<double> scores, double mark) =>
            scores.Count == 0 ? 0 : 100.0 * scores.Count(s => s > mark) / scores.Count;

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth, height = ActualHeight - LabelRoom;
            if (width < 20 || height < 20 || _scores.Length == 0)
                return;

            // The density at 0, 1, ... 100.
            double n = _scores.Length;
            double mean = _scores.Average();
            double sd = Math.Sqrt(_scores.Sum(s => (s - mean) * (s - mean)) / Math.Max(1, n - 1));
            double h = Math.Max(4, 1.06 * sd * Math.Pow(n, -0.2));
            var density = new double[101];
            for (int x = 0; x <= 100; x++)
            {
                double sum = 0;
                foreach (double s in _scores)
                {
                    double u = (x - s) / h;
                    sum += Math.Exp(-0.5 * u * u);
                }
                density[x] = sum;
            }
            double top = density.Max();
            if (top <= 0)
                return;
            Func<int, Point> at = x => new Point(width * x / 100.0, height - (height - 6) * density[x] / top);

            // The part rarer than this one, then the whole hill, its edge, and the mark.
            int markAt = (int)Math.Round(_mark);
            dc.DrawGeometry(Rarer, null, Area(at, 0, markAt, height));
            dc.DrawGeometry(Fill, null, Area(at, 0, 100, height));
            var edge = new StreamGeometry();
            using (StreamGeometryContext g = edge.Open())
            {
                g.BeginFigure(at(0), false, false);
                for (int x = 1; x <= 100; x++)
                    g.LineTo(at(x), true, true);
            }
            edge.Freeze();
            dc.DrawGeometry(null, Line, edge);
            dc.DrawLine(Axis, new Point(0, height), new Point(width, height));
            Point mark = at(markAt);
            dc.DrawLine(Marker, new Point(mark.X, height), new Point(mark.X, Math.Max(0, mark.Y - 6)));
            dc.DrawEllipse(Gold, null, mark, 4, 4);

            // Which way is which.
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var left = new FormattedText(_left, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Muted, dpi);
            var right = new FormattedText(_right, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Muted, dpi);
            dc.DrawText(left, new Point(0, height + 2));
            dc.DrawText(right, new Point(width - right.Width, height + 2));
        }

        private static Geometry Area(Func<int, Point> at, int from, int to, double baseLine)
        {
            var area = new StreamGeometry();
            using (StreamGeometryContext g = area.Open())
            {
                g.BeginFigure(new Point(at(from).X, baseLine), true, true);
                for (int x = from; x <= to; x++)
                    g.LineTo(at(x), true, true);
                g.LineTo(new Point(at(to).X, baseLine), true, true);
            }
            area.Freeze();
            return area;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Color color, double thickness)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness);
            pen.Freeze();
            return pen;
        }
    }
}
