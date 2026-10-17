using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.Store
{
    /// <summary>
    /// Middle-click scrolling, as in a web browser, anywhere in Playnite's window: press the
    /// middle button over something that scrolls and move the pointer away from where you
    /// pressed; the further away, the faster it scrolls. Hold-and-drag stops on release; a
    /// plain click leaves it running until the next click, a wheel turn or Esc.
    ///
    /// It is one handler on the window, not one per list, so it reaches Playnite's own lists
    /// and pages as well as the plugin's. A popup (the Store bar's drop-downs) is a window of
    /// its own that the main window's handler never hears from, so it is given one too.
    /// </summary>
    internal sealed class MiddleClickScroll
    {
        private const double DeadZone = 14;       // pixels around the anchor that do nothing
        private const double Speed = 6;           // pixels per second, per pixel past the dead zone
        private const double MaxSpeed = 6000;

        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly FrameworkElement _window;   // the main window, or a popup's content
        private ScrollViewer _target;
        private Point _anchor;
        private bool _moved;
        private bool _held;
        private Popup _mark;
        private readonly Stopwatch _clock = new Stopwatch();

        private MiddleClickScroll(FrameworkElement root)
        {
            _window = root;
            _window.PreviewMouseDown += OnMouseDown;
            _window.PreviewMouseUp += OnMouseUp;
            _window.PreviewMouseWheel += (s, e) => Stop();
            _window.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Stop(); };
            if (root is Window window)
                window.Deactivated += (s, e) => Stop();
            else
                root.Unloaded += (s, e) => Stop();   // the popup closed under it
        }

        /// <summary>For the main window, or the content of a popup.</summary>
        public static void Install(FrameworkElement root)
        {
            if (root != null)
                new MiddleClickScroll(root);
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Any click while it is running ends it, and is not passed on.
            if (_target != null)
            {
                Stop();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton != MouseButton.Middle)
                return;

            ScrollViewer viewer = ScrollerAt(e.OriginalSource as DependencyObject);
            if (viewer == null)
                return;

            _target = viewer;
            _anchor = e.GetPosition(_window);
            _moved = false;
            _held = true;
            _window.CaptureMouse();
            _window.Cursor = Cursors.ScrollAll;
            ShowMark();
            _clock.Restart();
            CompositionTarget.Rendering += Step;
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle || _target == null)
                return;

            _held = false;

            // Held and dragged: letting go ends it. A plain click keeps it going.
            if (_moved)
                Stop();
            e.Handled = true;
        }

        // The nearest thing under the pointer that can scroll up and down, or failing that
        // sideways (a row of covers).
        private static ScrollViewer ScrollerAt(DependencyObject start)
        {
            ScrollViewer sideways = null;
            for (DependencyObject node = start; node != null; node = Parent(node))
            {
                if (node is ScrollViewer viewer && viewer.IsVisible)
                {
                    if (viewer.ScrollableHeight > 0)
                        return viewer;
                    if (sideways == null && viewer.ScrollableWidth > 0)
                        sideways = viewer;
                }
            }
            return sideways;
        }

        private static DependencyObject Parent(DependencyObject node)
        {
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
            return LogicalTreeHelper.GetParent(node);
        }

        private void Step(object sender, EventArgs e)
        {
            ScrollViewer viewer = _target;
            if (viewer == null || !viewer.IsVisible)
            {
                Stop();
                return;
            }

            double seconds = Math.Min(0.1, _clock.Elapsed.TotalSeconds);
            _clock.Restart();

            Point now = Mouse.GetPosition(_window);
            double dx = now.X - _anchor.X;
            double dy = now.Y - _anchor.Y;
            if (Math.Abs(dx) > DeadZone || Math.Abs(dy) > DeadZone)
                _moved = _held || _moved;

            // Lists that scroll by whole items count their offset in items, not pixels.
            double unit = viewer.CanContentScroll ? 1.0 / 32 : 1.0;

            if (viewer.ScrollableHeight > 0)
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + Rate(dy) * seconds * unit);
            if (viewer.ScrollableWidth > 0)
                viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + Rate(dx) * seconds * unit);
        }

        private static double Rate(double distance)
        {
            double beyond = Math.Abs(distance) - DeadZone;
            if (beyond <= 0)
                return 0;
            return Math.Sign(distance) * Math.Min(MaxSpeed, beyond * Speed);
        }

        // The usual round mark where the button went down.
        private void ShowMark()
        {
            try
            {
                var ring = new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(1.5),
                    IsHitTestVisible = false,
                    Child = new TextBlock { Text = "↕", Foreground = Brushes.White, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                _mark = new Popup
                {
                    PlacementTarget = _window,
                    Placement = PlacementMode.Relative,
                    HorizontalOffset = _anchor.X - 14,
                    VerticalOffset = _anchor.Y - 14,
                    AllowsTransparency = true,
                    IsHitTestVisible = false,
                    Child = ring,
                    IsOpen = true,
                };
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Scroll] Could not show the middle-click mark: {ex.Message}");
            }
        }

        private void Stop()
        {
            if (_target == null)
                return;

            _target = null;
            CompositionTarget.Rendering -= Step;
            _window.Cursor = null;
            if (_window.IsMouseCaptured)
                _window.ReleaseMouseCapture();
            if (_mark != null)
            {
                _mark.IsOpen = false;
                _mark = null;
            }
        }
    }
}
