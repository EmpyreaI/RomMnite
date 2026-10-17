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
    /// Mouse-wheel scrolling that glides to where the wheel sent it. WPF's own jumps a fixed
    /// distance per notch, which reads as a stutter on a long page of pictures.
    ///
    /// Each notch moves a target position; every frame the view closes a fixed share of the
    /// remaining distance, so a single notch eases to a stop and fast spinning stays responsive.
    /// Everywhere (InstallEverywhere): one handler for every scroll viewer in the application,
    /// Playnite's own included, run before anything inside them sees the wheel. It glides the
    /// innermost view under the pointer that can still move that way (so a list inside a page
    /// scrolls first, and the page once the list is at its end); sideways-only rows are passed
    /// over, so they never swallow it. Views whose scrolling is not in pixels (a list scrolling a
    /// whole row per step) are switched to pixels where WPF allows it, else left to WPF's own
    /// scrolling. With Ctrl or Shift held the wheel is left alone (zoom, sideways).
    /// </summary>
    internal sealed class SmoothScroll
    {
        private const double PixelsPerNotch = 110;
        private const double Stiffness = 14;      // per second; higher settles faster

        private readonly ScrollViewer _viewer;
        private readonly Stopwatch _clock = new Stopwatch();
        private double _target;
        private double _lastSet;   // where the glide last put the view

        /// <summary>
        /// Gives the scroll bars inside <paramref name="owner"/> a wider, easier to grab bar
        /// than the theme's slim one, keeping the theme's look otherwise.
        /// </summary>
        public static void WidenScrollBars(FrameworkElement owner, double thickness = 14)
        {
            if (owner == null)
                return;

            RoutedEventHandler apply = null;
            apply = (s, e) =>
            {
                owner.Loaded -= apply;
                var themed = owner.TryFindResource(typeof(ScrollBar)) as Style;
                var style = new Style(typeof(ScrollBar), themed);
                var vertical = new Trigger { Property = ScrollBar.OrientationProperty, Value = Orientation.Vertical };
                vertical.Setters.Add(new Setter(FrameworkElement.WidthProperty, thickness));
                vertical.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, thickness));
                var horizontal = new Trigger { Property = ScrollBar.OrientationProperty, Value = Orientation.Horizontal };
                horizontal.Setters.Add(new Setter(FrameworkElement.HeightProperty, thickness));
                horizontal.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, thickness));
                style.Triggers.Add(vertical);
                style.Triggers.Add(horizontal);
                owner.Resources[typeof(ScrollBar)] = style;
            };
            owner.Loaded += apply;
        }
        private bool _animating;

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, SmoothScroll> s_all =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, SmoothScroll>();
        private static bool s_installed;

        /// <summary>Smooth wheel scrolling for every scroll viewer in the application. Once, on the UI thread.</summary>
        public static void InstallEverywhere()
        {
            if (s_installed)
                return;
            s_installed = true;
            EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnAnyWheel));
        }

        // The tunnel reaches the outermost scroll viewer first: the view to glide is worked out
        // from where the pointer is, innermost first, and the wheel taken there.
        private static void OnAnyWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.None)
                return;
            for (DependencyObject node = e.OriginalSource as DependencyObject; node != null; node = Parent(node))
            {
                if (!(node is ScrollViewer viewer))
                    continue;
                if (viewer.ScrollableHeight <= 0 || !InPixels(viewer))
                    continue;
                if (For(viewer).OnWheel(e))
                    return;
                // At its end that way: the next view out.
            }
        }

        private static DependencyObject Parent(DependencyObject node)
        {
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
            return LogicalTreeHelper.GetParent(node);
        }

        private static SmoothScroll For(ScrollViewer viewer) => s_all.GetValue(viewer, v => new SmoothScroll(v));

        // Whether the view's offsets are in pixels. A list that scrolls by items (a virtualizing
        // stack panel) is switched to pixels, which WPF supports while still virtualizing; the
        // switch counts from the next notch. Other item-scrolling panels are left to WPF, except
        // Playnite's grid view, whose offsets are pixels already.
        private static bool InPixels(ScrollViewer viewer)
        {
            if (!viewer.CanContentScroll)
                return true;
            DependencyObject info = ScrollInfoOf(viewer);
            if (info is VirtualizingStackPanel panel)
            {
                ItemsControl owner = ItemsControl.GetItemsOwner(panel);
                DependencyObject target = (DependencyObject)owner ?? panel;
                if (VirtualizingPanel.GetScrollUnit(target) == ScrollUnit.Pixel)
                    return true;
                VirtualizingPanel.SetScrollUnit(target, ScrollUnit.Pixel);
                return false;
            }
            return info != null && info.GetType().Name == "GridViewPanel";
        }

        // What actually scrolls inside a scroll viewer: the first element below it that does
        // its scrolling for it.
        private static DependencyObject ScrollInfoOf(ScrollViewer viewer)
        {
            var queue = new System.Collections.Generic.Queue<DependencyObject>();
            queue.Enqueue(viewer);
            int looked = 0;
            while (queue.Count > 0 && looked++ < 200)
            {
                DependencyObject node = queue.Dequeue();
                if (node != viewer && node is IScrollInfo info && info.ScrollOwner == viewer)
                    return node;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
            return null;
        }

        private SmoothScroll(ScrollViewer viewer)
        {
            _viewer = viewer;
            _viewer.Unloaded += (s, e) => Stop();

            // Grabbing the scroll bar (or anything else) ends a glide at once, so the glide never
            // pulls against a drag.
            _viewer.PreviewMouseDown += (s, e) => Stop();
        }

        /// <summary>
        /// The view's glide (for ToTop), with smooth scrolling installed everywhere if it is not
        /// yet. The wheel itself is taken by the one handler of InstallEverywhere.
        /// </summary>
        public static SmoothScroll Attach(ScrollViewer viewer)
        {
            InstallEverywhere();
            return viewer == null ? null : For(viewer);
        }

        /// <summary>For a list box and the like, whose scrolling part only exists once shown.</summary>
        public static void AttachInside(FrameworkElement owner)
        {
            if (owner == null)
                return;

            // Lists scroll a whole row per step unless told to scroll by distance.
            ScrollViewer.SetCanContentScroll(owner, false);

            bool attached = false;
            RoutedEventHandler onLoaded = (s, e) =>
            {
                if (attached)
                    return;
                var inner = Find(owner);
                if (inner != null)
                {
                    attached = true;
                    Attach(inner);
                }
            };
            owner.Loaded += onLoaded;
            if (owner.IsLoaded)
                onLoaded(owner, null);
        }

        private static ScrollViewer Find(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer viewer)
                    return viewer;
                ScrollViewer deeper = Find(child);
                if (deeper != null)
                    return deeper;
            }
            return null;
        }

        /// <summary>Jumps to the top at once, dropping any glide in progress.</summary>
        public void ToTop()
        {
            Stop();
            _viewer.ScrollToTop();
        }

        // Whether the wheel was taken (false: this view is at its end that way).
        private bool OnWheel(MouseWheelEventArgs e)
        {
            if (_viewer.ScrollableHeight <= 0)
                return false;

            double from = _animating ? _target : _viewer.VerticalOffset;
            double to = Math.Max(0, Math.Min(_viewer.ScrollableHeight, from - e.Delta / 120.0 * PixelsPerNotch));

            // Already at the end in that direction: leave the wheel for whatever contains this.
            if (Math.Abs(to - _viewer.VerticalOffset) < 0.5 && Math.Abs(to - from) < 0.5)
                return false;

            _target = to;
            e.Handled = true;

            if (!_animating)
            {
                _animating = true;
                _lastSet = _viewer.VerticalOffset;
                _clock.Restart();
                CompositionTarget.Rendering += Step;
            }
            return true;
        }

        private void Step(object sender, EventArgs e)
        {
            double seconds = Math.Min(0.1, _clock.Elapsed.TotalSeconds);
            _clock.Restart();

            // The page may have shrunk under the glide (content replaced).
            _target = Math.Max(0, Math.Min(_viewer.ScrollableHeight, _target));

            double current = _viewer.VerticalOffset;

            // Something other than the glide moved the view since the last frame (a drag, a
            // jump to the top): give way to it.
            if (Math.Abs(current - _lastSet) > 1.5)
            {
                Stop();
                return;
            }

            double remaining = _target - current;
            if (Math.Abs(remaining) < 0.5)
            {
                _viewer.ScrollToVerticalOffset(_target);
                Stop();
                return;
            }

            _lastSet = current + remaining * (1 - Math.Exp(-Stiffness * seconds));
            _viewer.ScrollToVerticalOffset(_lastSet);
        }

        private void Stop()
        {
            if (!_animating)
                return;
            _animating = false;
            CompositionTarget.Rendering -= Step;
        }
    }
}
