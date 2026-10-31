using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Models.RomM.Platform;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RomM.Store
{
    /// <summary>
    /// A game's platform's small icon: Playnite's own for the platform when it has one (set in
    /// Playnite's platform settings), else the one RomM's web interface shows for it, which the
    /// server ships for every platform at /assets/platforms/&lt;slug&gt;.ico (by its slug, then
    /// its folder's slug). Its largest image; fetched once a run, in the background.
    /// </summary>
    internal static class PlatformIcons
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly ConcurrentDictionary<string, Task<ImageSource>> s_fetched = new ConcurrentDictionary<string, Task<ImageSource>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The icon, or null (none, or not yet fetched: <paramref name="ready"/> runs on the UI thread when it is).</summary>
        public static ImageSource For(RomM plugin, Game game, Action ready)
        {
            if (game == null)
                return null;
            try
            {
                Platform platform = game.Platforms?.FirstOrDefault();
                if (!string.IsNullOrEmpty(platform?.Icon))
                {
                    string path = platform.Icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? null : plugin.Playnite.Database.GetFullFilePath(platform.Icon);
                    if (path != null && File.Exists(path))
                        return Await("file:" + path, () => Task.FromResult(Decode(File.ReadAllBytes(path))), ready);
                }
                RomMPlatform romm = plugin.KnownPlatformOf(game);
                string host = plugin.Settings?.RomMHost;
                if (romm == null || string.IsNullOrEmpty(host))
                    return null;
                return Await("romm:" + host + "|" + romm.Slug + "|" + romm.FsSlug, () => FetchAsync(plugin, host, romm), ready);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[PlatformIcons] {ex.Message}");
                return null;
            }
        }

        private static ImageSource Await(string key, Func<Task<ImageSource>> start, Action ready)
        {
            Task<ImageSource> task = s_fetched.GetOrAdd(key, _ => start());
            if (task.IsCompleted)
                return task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (ready != null && dispatcher != null)
                task.ContinueWith(_ => dispatcher.BeginInvoke(ready));
            return null;
        }

        private static async Task<ImageSource> FetchAsync(RomM plugin, string host, RomMPlatform platform)
        {
            foreach (string slug in new[] { platform.Slug, platform.FsSlug }.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string url = plugin.CombineUrl(host, $"assets/platforms/{Uri.EscapeDataString(slug.ToLowerInvariant())}.ico");
                    using (HttpResponseMessage response = await HttpClientSingleton.Instance.GetAsync(url).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            continue;
                        byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        ImageSource image = Decode(bytes);
                        if (image != null)
                            return image;
                    }
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[PlatformIcons] {platform.PlayniteName} ({slug}): {ex.Message}");
                }
            }
            return null;
        }

        private static readonly ConditionalWeakTable<ImageSource, ImageSource> s_grey = new ConditionalWeakTable<ImageSource, ImageSource>();

        /// <summary>
        /// The icon in greys, light on a coloured button with its detail kept: each pixel's
        /// brightness (Rec. 709), the icon's own range of it stretched to the whole range (an
        /// icon of dark greys alone would otherwise come out as one flat grey), turned the other
        /// way round for a mostly dark icon (a black PSP or PS3 pad: its body white, its screen
        /// and buttons the greys), then set between GreyFloor and white. Its transparency kept.
        /// Made once per icon; the icon itself when it cannot be.
        /// </summary>
        public static ImageSource Grey(ImageSource icon)
        {
            if (!(icon is BitmapSource bitmap))
                return icon;
            return s_grey.GetValue(icon, _ => MakeGrey(bitmap) ?? icon);
        }

        // The darkest grey a pixel becomes (the icon's darkest part); its lightest becomes white.
        private const double GreyFloor = 80;

        private static ImageSource MakeGrey(BitmapSource bitmap)
        {
            try
            {
                var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
                var pixels = new byte[stride * height];
                bgra.CopyPixels(pixels, stride, 0);
                // Bgra32 keeps colour and transparency apart (not premultiplied).
                var luma = new double[pixels.Length / 4];
                double low = 255, high = 0, sum = 0, weight = 0;
                for (int i = 0, p = 0; i < pixels.Length; i += 4, p++)
                {
                    luma[p] = 0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2];
                    sum += luma[p] * pixels[i + 3];
                    weight += pixels[i + 3];
                    // The range from the pixels that show (the faint edges of a shape would
                    // otherwise widen it).
                    if (pixels[i + 3] >= 128)
                    {
                        low = Math.Min(low, luma[p]);
                        high = Math.Max(high, luma[p]);
                    }
                }
                // A one-tone icon (no range to stretch): its shape alone, white.
                double span = high - low;
                bool dark = weight > 0 && sum / weight < 128;
                for (int i = 0, p = 0; i < pixels.Length; i += 4, p++)
                {
                    if (pixels[i + 3] == 0)
                        continue;
                    double level = span < 8 ? 1 : Math.Max(0, Math.Min(1, (luma[p] - low) / span));
                    if (dark && span >= 8)
                        level = 1 - level;
                    byte grey = (byte)Math.Round(GreyFloor + level * (255 - GreyFloor));
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = grey;
                }
                var result = BitmapSource.Create(width, height, bitmap.DpiX, bitmap.DpiY, PixelFormats.Bgra32, null, pixels, stride);
                result.Freeze();
                return result;
            }
            catch (Exception ex)
            {
                s_log.Debug($"[PlatformIcons] Could not grey an icon: {ex.Message}");
                return null;
            }
        }

        // An .ico's largest image, or a .png's (or any picture WPF reads); null when it is none.
        private static ImageSource Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return null;
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                    if (frame == null)
                        return null;
                    frame.Freeze();
                    return frame;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
