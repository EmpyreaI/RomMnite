using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RomM.Overlay
{
    /// <summary>
    /// Pictures of the buttons to press, of the controller in use: an Xbox pad's A, a
    /// PlayStation pad's cross, a Switch pad's B (the same place: the south face button). From
    /// Kenney's Input Prompts (CC0), shipped in the extension's Glyphs folder, one folder per kind
    /// (xbox, ps4, ps5, switch) and keyboard.
    ///
    /// Text with buttons in it is written with their names in braces, "{A} Select  {B} Close",
    /// and <see cref="Fill"/> puts the pictures in their place. The names are XInput's buttons,
    /// by place: A (south), B (east), LB, RB, DPad (up and down), DPadH (left and right), Guide,
    /// Start, Back, L3, R3; and the keys Shift, Tab, Esc, Enter, Arrows (up and down), ArrowsH.
    ///
    /// The kind shown is the controller used last (the overlay notes each press: XInput's are an
    /// Xbox pad's, HidPads' its layout's), else one connected that is read directly (Switch,
    /// PlayStation), else an Xbox pad's, which most others copy.
    /// </summary>
    internal static class ControllerGlyphs
    {
        public enum Family
        {
            Xbox,
            PlayStation4,
            PlayStation5,
            Switch,
        }

        private static readonly object s_lock = new object();
        private static Family? s_lastUsed;
        private static readonly Dictionary<string, ImageSource> s_images = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex s_token = new Regex(@"\{([A-Za-z0-9]+)\}");

        private static readonly HashSet<string> s_keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shift", "Tab", "Esc", "Enter", "Arrows", "ArrowsH",
        };

        /// <summary>A press on a controller of this kind (the overlay's poll).</summary>
        public static void Used(Family family)
        {
            bool changed;
            lock (s_lock)
            {
                changed = s_lastUsed != family;
                s_lastUsed = family;
            }
            if (changed)
                Refresh();
        }

        // ---- Prompts that follow what is in use ---------------------------------------------------

        private sealed class Bound
        {
            public WeakReference<TextBlock> Block;
            public string Pad;
            public string Keys;
        }

        private static readonly List<Bound> s_bound = new List<Bound>();

        static ControllerGlyphs()
        {
            InputActivity.Changed += Refresh;
        }

        /// <summary>
        /// <paramref name="block"/> showing <paramref name="pad"/> while a controller is what is in
        /// use, <paramref name="keys"/> while the keyboard and mouse are (InputActivity), and
        /// changing as that does, and with the controller's kind; hidden while its text is null.
        /// Kept only as long as the block is.
        /// </summary>
        public static void Bind(TextBlock block, string pad, string keys)
        {
            var bound = new Bound { Block = new WeakReference<TextBlock>(block), Pad = pad, Keys = keys };
            lock (s_lock)
                s_bound.Add(bound);
            Render(block, bound, InputActivity.ControllerInUse);
        }

        private static void Render(TextBlock block, Bound bound, bool controller)
        {
            string text = controller ? bound.Pad : bound.Keys;
            block.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            Fill(block, text ?? "");
        }

        // Every prompt again, on its own thread.
        private static void Refresh()
        {
            List<Bound> all;
            lock (s_lock)
            {
                s_bound.RemoveAll(b => !b.Block.TryGetTarget(out _));
                all = s_bound.ToList();
            }
            bool controller = InputActivity.ControllerInUse;
            foreach (Bound bound in all)
            {
                if (!bound.Block.TryGetTarget(out TextBlock block))
                    continue;
                try
                {
                    block.Dispatcher.BeginInvoke(new Action(() => Render(block, bound, controller)));
                }
                catch
                {
                    // Its window closed.
                }
            }
        }

        /// <summary>The kind of controller whose buttons to show now.</summary>
        public static Family Current
        {
            get
            {
                lock (s_lock)
                {
                    if (s_lastUsed.HasValue)
                        return s_lastUsed.Value;
                }
                return HidPads.ConnectedFamily ?? Family.Xbox;
            }
        }

        private static string Folder(Family family)
        {
            switch (family)
            {
                case Family.PlayStation4: return "ps4";
                case Family.PlayStation5: return "ps5";
                case Family.Switch: return "switch";
                default: return "xbox";
            }
        }

        private static string Root => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Glyphs");

        /// <summary>The picture of a button (or key), or null when there is none by that name.</summary>
        public static ImageSource Image(string name, Family? family = null)
        {
            string folder = s_keys.Contains(name) ? "keyboard" : Folder(family ?? Current);
            string path = Path.Combine(Root, folder, name + ".png");
            lock (s_lock)
            {
                if (s_images.TryGetValue(path, out ImageSource kept))
                    return kept;
            }
            ImageSource image = null;
            try
            {
                if (File.Exists(path))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;   // the file not held open
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
            }
            catch
            {
                image = null;
            }
            lock (s_lock)
                s_images[path] = image;
            return image;
        }

        /// <summary>
        /// <paramref name="block"/>'s text set to <paramref name="text"/>, each {Button} in it
        /// drawn as its picture, a little taller than the letters. A name without a picture
        /// stays as words.
        /// </summary>
        public static void Fill(TextBlock block, string text, Family? family = null)
        {
            block.Inlines.Clear();
            text = text ?? "";
            int at = 0;
            foreach (Match match in s_token.Matches(text))
            {
                if (match.Index > at)
                    block.Inlines.Add(new Run(text.Substring(at, match.Index - at)));
                string name = match.Groups[1].Value;
                ImageSource picture = Image(name, family);
                if (picture != null)
                {
                    // The pictures are trimmed to their edges. A key's word ("SHIFT") is small
                    // on its key, so keys are drawn taller than a controller's buttons.
                    double size = block.FontSize * (s_keys.Contains(name) ? 2.1 : 1.6);
                    var image = new Image { Source = picture, Height = size, Width = size * picture.Width / Math.Max(1, picture.Height), Margin = new Thickness(1, 0, 1, 0) };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                    block.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
                }
                else
                {
                    block.Inlines.Add(new Run(match.Groups[1].Value));
                }
                at = match.Index + match.Length;
            }
            if (at < text.Length)
                block.Inlines.Add(new Run(text.Substring(at)));
        }

        /// <summary>The text with its {Button} names as words, for where pictures cannot go (a log line).</summary>
        public static string Plain(string text) => s_token.Replace(text ?? "", m => m.Groups[1].Value);
    }
}
