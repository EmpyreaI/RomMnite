using Playnite.SDK;
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace RomM.Account
{
    /// <summary>
    /// While nobody is signed in, Playnite's main window shows nothing of the last account's
    /// library: a plain background with the RomM logo in the middle, as Steam's window is
    /// empty behind its sign-in. It lies over the whole window (an adorner, so no theme has to
    /// place it) and fades away once someone has signed in.
    ///
    /// It also covers the window while the library is being brought in step with the server
    /// (BeginBusy / EndBusy: after a sign-in, during a library update), with the logo turning
    /// and a line saying what is going on, so the wait does not look like a hang.
    /// </summary>
    public static class SignedOutCurtain
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static Cover s_cover;
        private static bool s_wanted;       // signed out
        private static int s_busy;          // syncs under way
        private static string s_caption = "";
        private static System.Windows.Threading.DispatcherTimer s_failsafe;

        private sealed class Cover : Adorner
        {
            private readonly Grid _content;

            public Cover(UIElement adorned, Grid content) : base(adorned)
            {
                _content = content;
                AddVisualChild(_content);
                IsHitTestVisible = true;
            }

            public Grid Content => _content;
            public RotateTransform Spin;
            public TextBlock Caption;
            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => _content;

            protected override Size MeasureOverride(Size constraint)
            {
                _content.Measure(AdornedElement.RenderSize);
                return AdornedElement.RenderSize;
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                _content.Arrange(new Rect(AdornedElement.RenderSize));
                return finalSize;
            }
        }

        /// <summary>Covers the main window (now, or as soon as it exists). On the UI thread.</summary>
        public static void Show()
        {
            s_wanted = true;
            Update();
        }

        /// <summary>Uncovers the main window, fading (unless a sync still has it covered). On the UI thread.</summary>
        public static void Hide()
        {
            s_wanted = false;
            Update();
        }

        /// <summary>
        /// The window covered, the logo turning, with the caption, until as many EndBusy. Any
        /// thread. Uncovered after five minutes whatever happens, so nothing can leave it stuck.
        /// </summary>
        public static void BeginBusy(string caption)
        {
            OnUi(() =>
            {
                s_busy++;
                s_caption = caption ?? "";
                if (s_failsafe == null)
                {
                    s_failsafe = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
                    s_failsafe.Tick += (s, e) =>
                    {
                        s_failsafe.Stop();
                        if (s_busy > 0)
                        {
                            s_log.Warn("[Account] The library sync is taking long; the window is uncovered.");
                            s_busy = 0;
                            Update();
                        }
                    };
                }
                s_failsafe.Stop();
                s_failsafe.Start();
                Update();
            });
        }

        /// <summary>One sync done (see BeginBusy). Any thread.</summary>
        public static void EndBusy()
        {
            OnUi(() =>
            {
                if (s_busy > 0)
                    s_busy--;
                if (s_busy == 0)
                    s_failsafe?.Stop();
                Update();
            });
        }

        private static void OnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;
            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action);
        }

        private static void Update()
        {
            if (s_wanted || s_busy > 0)
                ShowCover();
            else
                HideCover();

            Cover cover = s_cover;
            if (cover == null)
                return;
            // Turning, with its line, while a sync is under way (not behind the sign-in window).
            bool busy = s_busy > 0 && !s_wanted;
            if (cover.Caption != null)
            {
                cover.Caption.Text = busy ? s_caption : "";
                cover.Caption.Visibility = busy && s_caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (cover.Spin != null)
            {
                if (busy)
                {
                    if (!cover.Spin.HasAnimatedProperties)
                        cover.Spin.BeginAnimation(RotateTransform.AngleProperty,
                            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
                }
                else
                {
                    cover.Spin.BeginAnimation(RotateTransform.AngleProperty, null);
                    cover.Spin.Angle = 0;
                }
            }
        }

        private static void ShowCover()
        {
            try
            {
                Window window = Application.Current?.MainWindow;
                FrameworkElement root = window?.Content as FrameworkElement;
                if (root == null)
                    return;
                if (!root.IsLoaded)
                {
                    RoutedEventHandler loaded = null;
                    loaded = (s, e) =>
                    {
                        root.Loaded -= loaded;
                        Update();
                    };
                    root.Loaded += loaded;
                    return;
                }

                if (s_cover != null)
                {
                    s_cover.Content.BeginAnimation(UIElement.OpacityProperty, null);
                    s_cover.Content.Opacity = 1;
                    return;
                }
                AdornerLayer layer = AdornerLayer.GetAdornerLayer(root);
                if (layer == null)
                    return;
                s_cover = Build(root);
                layer.Add(s_cover);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Account] Could not cover the window while signed out: {ex.Message}");
            }
        }

        private static void HideCover()
        {
            Cover cover = s_cover;
            if (cover == null)
                return;
            s_cover = null;
            try
            {
                // A moment for the library to settle into the new account's games first.
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350))
                {
                    BeginTime = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                fade.Completed += (s, e) => AdornerLayer.GetAdornerLayer(cover.AdornedElement)?.Remove(cover);
                cover.Content.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Account] Could not uncover the window: {ex.Message}");
                AdornerLayer.GetAdornerLayer(cover.AdornedElement)?.Remove(cover);
            }
        }

        private static Cover Build(UIElement root)
        {
            var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)) };
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(column);
            var spin = new RotateTransform(0);
            var caption = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                FontSize = 15,
                Margin = new Thickness(0, 22, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            try
            {
                string icon = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "icon.png");
                if (File.Exists(icon))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 320;
                    bitmap.UriSource = new Uri(icon);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    var logo = new Image
                    {
                        Source = bitmap,
                        Width = 128,
                        Height = 128,
                        Opacity = 0.9,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = spin,
                    };
                    RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.Fant);
                    column.Children.Add(logo);
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Account] Could not load the logo: {ex.Message}");
            }
            column.Children.Add(caption);
            return new Cover(root, grid) { Spin = spin, Caption = caption };
        }
    }
}
