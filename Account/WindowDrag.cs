using Playnite.SDK;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace RomM.Account
{
    /// <summary>
    /// The window's bar (its top 64 pixels: the menu button, the tabs, the account) moves the
    /// window when dragged, and maximizes or restores it on a double-click, as Steam's does.
    ///
    /// The theme's window chrome marks that strip as the window's caption, but the strip is not
    /// empty: the window's content lies under it too, and a press there reached the content, not
    /// the caption. So the drag is done here, on any press in the strip that is not on something
    /// to click (a button, a box, a tab, anything the theme marks as clickable in the bar).
    ///
    /// Dragged while maximized, the window is restored under the pointer at the same place along
    /// its width, then follows it; Windows' snapping (to an edge, to the top) still works, as the
    /// move is Windows' own (DragMove).
    ///
    /// The window's edges: the theme's chrome made a 16-pixel band all round for resizing, which
    /// took presses meant for what lies along the edges (the scroll bar on the right, the top of
    /// the bar), and kept doing so when maximized, where there is nothing to resize. The band is
    /// 5 pixels (Windows' own feel), and none while maximized.
    /// </summary>
    internal static class WindowDrag
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private const double BarHeight = 64;
        private const double Threshold = 4;
        private static readonly Thickness ResizeBand = new Thickness(5);

        private static Point? s_pressedAt;   // in the window, while a press in the bar may become a drag

        public static void Install(Window window)
        {
            if (window == null)
                return;
            window.PreviewMouseLeftButtonDown += OnDown;
            window.PreviewMouseMove += OnMove;
            window.PreviewMouseLeftButtonUp += (s, e) => s_pressedAt = null;
            window.LostMouseCapture += (s, e) => s_pressedAt = null;
            window.StateChanged += (s, e) => FitEdges(window);
            FitEdges(window);
        }

        // The resize band: none while maximized, a thin one otherwise. The theme's chrome is
        // frozen (a style's), so a copy is set with the band changed.
        private static void FitEdges(Window window)
        {
            try
            {
                WindowChrome chrome = WindowChrome.GetWindowChrome(window);
                if (chrome == null)
                    return;
                Thickness wanted = window.WindowState == WindowState.Maximized ? new Thickness(0) : ResizeBand;
                if (chrome.ResizeBorderThickness == wanted)
                    return;
                var copy = (WindowChrome)chrome.Clone();
                copy.ResizeBorderThickness = wanted;
                WindowChrome.SetWindowChrome(window, copy);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Window] Could not set the window's edges: {ex.Message}");
            }
        }

        // The strip's height, below the frame's padding when maximized (the theme adds 8).
        private static double Bottom(Window window) =>
            BarHeight + (window.WindowState == WindowState.Maximized ? 8 : 0);

        private static void OnDown(object sender, MouseButtonEventArgs e)
        {
            var window = (Window)sender;
            s_pressedAt = null;
            Point at = e.GetPosition(window);
            if (at.Y < 0 || at.Y >= Bottom(window) || Clickable(e.OriginalSource as DependencyObject, window))
                return;

            if (e.ClickCount == 2)
            {
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
                return;
            }
            s_pressedAt = at;
            e.Handled = true;
        }

        private static void OnMove(object sender, MouseEventArgs e)
        {
            if (s_pressedAt == null)
                return;
            var window = (Window)sender;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                s_pressedAt = null;
                return;
            }
            Point at = e.GetPosition(window);
            Point from = s_pressedAt.Value;
            if (Math.Abs(at.X - from.X) < Threshold && Math.Abs(at.Y - from.Y) < Threshold)
                return;
            s_pressedAt = null;
            try
            {
                if (window.WindowState == WindowState.Maximized)
                {
                    // Restored under the pointer: the same share of the way across, the bar
                    // where it was grabbed.
                    double share = window.ActualWidth > 0 ? from.X / window.ActualWidth : 0.5;
                    Point screen = ToDips(window, window.PointToScreen(at));
                    Rect restore = window.RestoreBounds;
                    window.WindowState = WindowState.Normal;
                    double width = restore.IsEmpty ? window.Width : restore.Width;
                    window.Left = screen.X - width * share;
                    window.Top = screen.Y - Math.Min(from.Y, BarHeight / 2);
                }
                window.DragMove();
            }
            catch (InvalidOperationException)
            {
                // The button was let go in between.
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Window] Could not move the window: {ex.Message}");
            }
        }

        private static Point ToDips(Visual visual, Point device)
        {
            PresentationSource source = PresentationSource.FromVisual(visual);
            return source?.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.Transform(device) : device;
        }

        // Something to click: a control that takes the press, or anything inside what the theme
        // (or this extension) marks as clickable in the window's caption.
        private static bool Clickable(DependencyObject hit, Window window)
        {
            DependencyObject at = hit;
            while (at != null && at != window)
            {
                if (at is ButtonBase || at is TextBoxBase || at is PasswordBox || at is Selector
                    || at is Thumb || at is ScrollBar || at is Slider || at is Hyperlink)
                    return true;
                object marked = at.ReadLocalValue(WindowChrome.IsHitTestVisibleInChromeProperty);
                if (marked is bool on && on)
                    return true;
                if (at is FrameworkElement element && !string.IsNullOrEmpty(element.Name)
                    && element.Name.StartsWith("PART_Button", StringComparison.Ordinal))
                    return true;
                at = Parent(at);
            }
            return false;
        }

        private static DependencyObject Parent(DependencyObject child)
        {
            if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
            {
                DependencyObject visual = VisualTreeHelper.GetParent(child);
                if (visual != null)
                    return visual;
            }
            return LogicalTreeHelper.GetParent(child);
        }
    }
}
