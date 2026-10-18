using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RomM.Overlay
{
    /// <summary>
    /// Steam's small pop-ups: a card in the bottom-right corner of the screen that slides in,
    /// stays a few seconds and fades out, over whatever is in front (a game included, when it
    /// is borderless or windowed) without taking the focus from it. Several stack upwards.
    /// A click on one runs its action, if it has one, and closes it.
    /// </summary>
    internal static class Toast
    {
        private const double CardWidth = 360;
        private const double Gap = 10;
        private static readonly List<Window> s_open = new List<Window>();

        /// <param name="picture">A file or web address for the picture at the card's left, or null.</param>
        /// <param name="glyph">An icon (Segoe Fluent Icons) in place of a picture.</param>
        /// <param name="screenOf">A window whose screen it shows on (the game's); else the main screen.</param>
        public static void Show(string title, string message, string picture = null, string glyph = null, Action onClick = null,
            IntPtr screenOf = default(IntPtr), double seconds = 6)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Open(title, message, picture, glyph, onClick, screenOf, seconds, null)));
        }

        /// <summary>
        /// A pop-up that stays until it is closed, whose message can change while it shows (a
        /// countdown). Callable from any thread.
        /// </summary>
        public static Live ShowLive(string title, string message, string glyph = null, IntPtr screenOf = default(IntPtr))
        {
            var live = new Live();
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Open(title, message, null, glyph, null, screenOf, 0, live)));
            return live;
        }

        /// <summary>A pop-up shown with <see cref="ShowLive"/>.</summary>
        public sealed class Live
        {
            internal TextBlock Message;
            internal Action CloseNow;
            private bool _closeWhenShown;

            /// <summary>Its message now.</summary>
            public void Update(string message) => OnUi(() =>
            {
                if (Message != null)
                    Message.Text = message ?? "";
            });

            /// <summary>Takes it away at once (no fade), now or after <paramref name="after"/>.</summary>
            public void Close(TimeSpan after = default(TimeSpan)) => OnUi(() =>
            {
                if (after > TimeSpan.Zero)
                {
                    var wait = new DispatcherTimer { Interval = after };
                    wait.Tick += (s, e) =>
                    {
                        wait.Stop();
                        Close();
                    };
                    wait.Start();
                    return;
                }
                if (CloseNow != null)
                    CloseNow();
                else
                    _closeWhenShown = true;
            });

            internal bool CloseWhenShown => _closeWhenShown;

            private static void OnUi(Action action) => Application.Current?.Dispatcher?.BeginInvoke(action);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// Steam's "Achievement Unlocked" pop-up, on the screen of the window in front (the
        /// game's): the achievement's picture, and its name with its points.
        /// </summary>
        public static void Achievement(string name, string description, string picture, int points, string pointsUnit)
        {
            string line = name ?? "";
            if (points > 0)
                line += $"  \u00B7  {points} {pointsUnit}";
            if (!string.IsNullOrEmpty(description))
                line += "\n" + description;
            Show("Achievement unlocked", line, picture, "\uE734", null, GetForegroundWindow(), 7);
        }

        private static void Open(string title, string message, string picture, string glyph, Action onClick, IntPtr screenOf, double seconds, Live live)
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Height,
                Width = CardWidth,
                Focusable = false,
                Title = "RomMnite",
            };

            // The card: picture or icon, then the title and the message.
            var row = new DockPanel();
            FrameworkElement left = Picture(picture) ?? Icon(glyph ?? "");
            DockPanel.SetDock(left, Dock.Left);
            row.Children.Add(left);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            words.Children.Add(new TextBlock { Text = title ?? "", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(message) || live != null)
            {
                var text = new TextBlock { Text = message ?? "", FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                words.Children.Add(text);
                if (live != null)
                    live.Message = text;
            }
            row.Children.Add(words);
            var card = new Border
            {
                Background = new LinearGradientBrush(Color.FromArgb(0xF2, 0x23, 0x2A, 0x36), Color.FromArgb(0xF2, 0x16, 0x1B, 0x24), 90),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x66, 0xC0, 0xF4)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12),
                Child = row,
                Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow,
                RenderTransform = new TranslateTransform(0, 0),
                Opacity = 0,
            };
            window.Content = card;

            // Never takes the focus (the game keeps it), and stays out of Alt+Tab.
            window.SourceInitialized += (s, e) =>
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                int style = GetWindowLong(handle, GWL_EXSTYLE);
                SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            };

            Rect area = WorkArea(screenOf);
            window.Left = area.Right - CardWidth - 16;
            window.Top = area.Bottom;   // placed once its height is known
            window.Loaded += (s, e) =>
            {
                s_open.Add(window);
                Restack(area);
                var slide = (TranslateTransform)card.RenderTransform;
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            };

            var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            Action close = null;
            close = () =>
            {
                life.Stop();
                var fade = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(300));
                fade.Completed += (s, e) =>
                {
                    s_open.Remove(window);
                    window.Close();
                    Restack(area);
                };
                card.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            life.Tick += (s, e) => close();
            if (live != null)
            {
                // Stays until closed (Live.Close), and then goes at once: what it counted down to
                // has happened, so it does not linger fading.
                Action gone = () =>
                {
                    life.Stop();
                    if (!s_open.Remove(window))
                        return;
                    window.Close();
                    Restack(area);
                };
                live.CloseNow = gone;
                if (live.CloseWhenShown)
                    window.Loaded += (s, e) => gone();
            }
            // Pointed at: it stays while the pointer is on it.
            card.MouseEnter += (s, e) => life.Stop();
            card.MouseLeave += (s, e) =>
            {
                if (live == null)
                    life.Start();
            };
            card.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    onClick?.Invoke();
                }
                catch
                {
                    // The action's own trouble; the card closes anyway.
                }
                close();
            };

            window.Show();
            if (live == null)
                life.Start();   // a live one stays until it is closed
        }

        // The open cards, newest at the bottom, the rest above it.
        private static void Restack(Rect area)
        {
            double bottom = area.Bottom - 16;
            for (int i = s_open.Count - 1; i >= 0; i--)
            {
                Window window = s_open[i];
                double height = window.ActualHeight > 0 ? window.ActualHeight : 80;
                bottom -= height;
                window.Top = bottom;
                bottom -= Gap;
            }
        }

        private static FrameworkElement Picture(string picture)
        {
            if (string.IsNullOrEmpty(picture))
                return null;
            try
            {
                var image = new Image { Width = 48, Height = 64, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                if (picture.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    Store.StoreImages.Show(image, picture, 96);
                }
                else
                {
                    if (!File.Exists(picture))
                        return null;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(picture);
                    bitmap.DecodePixelWidth = 96;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image.Source = bitmap;
                }
                return new Border { CornerRadius = new CornerRadius(2), ClipToBounds = true, Child = image };
            }
            catch
            {
                return null;
            }
        }

        private static FrameworkElement Icon(string glyph)
        {
            return new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(22),
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0x66, 0xC0, 0xF4)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 20,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        // ---- Where -----------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // The screen's work area (the taskbar left out), in WPF's units. The screen of the given
        // window when there is one (a game, full screen: its whole screen), else the main one.
        private static Rect WorkArea(IntPtr screenOf)
        {
            Rect fallback = SystemParameters.WorkArea;
            if (screenOf == IntPtr.Zero)
                return fallback;
            try
            {
                IntPtr monitor = MonitorFromWindow(screenOf, 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                    return fallback;
                double scale = 1.0;
                Window main = Application.Current?.MainWindow;
                PresentationSource source = main != null ? PresentationSource.FromVisual(main) : null;
                if (source?.CompositionTarget != null)
                    scale = source.CompositionTarget.TransformToDevice.M11;
                NativeRect r = info.Monitor;
                return new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
