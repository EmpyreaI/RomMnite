using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RomM.Store
{
    /// <summary>
    /// Fetches the Store's pictures. WPF can download a picture given only its address, but it
    /// does so with no retry and silently shows nothing when a download fails, which with dozens
    /// of covers at once happens often enough to notice. Here each picture is downloaded
    /// explicitly, a few at a time, retried once, and kept in memory for the session so going
    /// back to one is instant.
    ///
    /// A picture element keeps whatever it was showing until its new picture has arrived, so
    /// switching pictures never passes through an empty frame.
    /// </summary>
    internal static class StoreImages
    {
        private const int MaxCached = 400;

        // Background pictures (the Store getting ready out of sight) take at most this many of
        // the six download slots, so what is on screen (the library above all) is never queued
        // behind them. The Store raises it while it is showing.
        private static int s_backgroundLimit = 2;
        private static int s_backgroundRunning;

        /// <summary>How many of the six slots background pictures may use at most.</summary>
        public static int BackgroundLimit
        {
            get => Volatile.Read(ref s_backgroundLimit);
            set => Volatile.Write(ref s_backgroundLimit, Math.Max(1, Math.Min(6, value)));
        }

        // The order pictures were kept in: the oldest go first when the cache is full (it was
        // emptied whole, which made the library's pictures load again).
        private static readonly Queue<string> s_order = new Queue<string>();

        // ---- Library games' artwork, kept on disk (Games/LibraryArtCache) ----------------------

        private static string s_artFolder;
        private static HashSet<string> s_artUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The folder library games' artwork is kept in; null: none kept.</summary>
        public static string ArtFolder
        {
            get => Volatile.Read(ref s_artFolder);
            set => Volatile.Write(ref s_artFolder, value);
        }

        /// <summary>The addresses of the library games' artwork: kept on disk when downloaded.</summary>
        public static void SetArtUrls(IEnumerable<string> urls)
        {
            var set = new HashSet<string>(urls ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Volatile.Write(ref s_artUrls, set);
        }

        // The kept file of an address, when there is one on disk.
        private static string KeptArt(string url)
        {
            string folder = ArtFolder;
            if (folder == null || string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return null;
            try
            {
                string file = KeptFile(folder, url);
                return File.Exists(file) && new FileInfo(file).Length > 0 ? file : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether a picture is kept on disk.</summary>
        public static bool IsKept(string url) => KeptArt(url) != null;

        /// <summary>Removes a picture kept on disk (its game left the library).</summary>
        public static void Forget(string url)
        {
            try
            {
                string file = KeptArt(url);
                if (file != null)
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not remove kept picture {url}: {ex.Message}");
            }
        }

        /// <summary>
        /// Downloads a library game's picture to disk, without showing it, in the background lane.
        /// Whether it is on disk afterwards.
        /// </summary>
        public static async Task<bool> KeepAsync(string url)
        {
            string folder = ArtFolder;
            if (folder == null || string.IsNullOrEmpty(url))
                return false;
            if (KeptArt(url) != null)
                return true;
            await s_gate.Task.ConfigureAwait(false);
            await TakeSlot(Always).ConfigureAwait(false);
            try
            {
                byte[] bytes = await Fetch(url, folder).ConfigureAwait(false);
                return bytes != null && bytes.Length > 0;
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not keep picture {url}: {ex.Message}");
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref s_backgroundRunning);
                s_slots.Release();
            }
        }

        /// <summary>Always in the background (pictures nobody is looking at).</summary>
        public static readonly Func<bool> Always = () => true;

        // Pictures on screen waiting for a slot: the background takes none while there are any.
        private static int s_foregroundWaiting;

        /// <summary>
        /// A download slot. What is on screen first: it queues for a slot as soon as it is asked
        /// for. Background pictures (<paramref name="background"/> true, asked each time round,
        /// so a picture of a page that has just been opened stops being background) only take a
        /// slot that is free when nothing on screen is waiting, nothing the user is doing is under
        /// way (Foreground), and fewer than <see cref="BackgroundLimit"/> are already downloading.
        /// Whether the slot was taken as background (it counts toward the limit until released).
        /// </summary>
        private static async Task<bool> TakeSlot(Func<bool> background)
        {
            while (true)
            {
                if (background == null || !background())
                {
                    Interlocked.Increment(ref s_foregroundWaiting);
                    try
                    {
                        await s_slots.WaitAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref s_foregroundWaiting);
                    }
                    return false;
                }
                if (Volatile.Read(ref s_foregroundWaiting) == 0 && Foreground.Quiet
                    && Volatile.Read(ref s_backgroundRunning) < BackgroundLimit
                    && await s_slots.WaitAsync(0).ConfigureAwait(false))
                {
                    if (Interlocked.Increment(ref s_backgroundRunning) <= BackgroundLimit)
                        return true;
                    Interlocked.Decrement(ref s_backgroundRunning);
                    s_slots.Release();
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
        }

        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly SemaphoreSlim s_slots = new SemaphoreSlim(6);
        private static readonly Dictionary<string, ImageSource> s_cache = new Dictionary<string, ImageSource>();
        private static readonly object s_lock = new object();

        // Pictures asked for since StartCounting, and how many of them have arrived or failed:
        // how far the Store's first page has got (its loading curtain).
        private static int s_asked;
        private static int s_settled;

        /// <summary>Counts pictures from now on (the loading curtain's measure).</summary>
        public static void StartCounting()
        {
            Interlocked.Exchange(ref s_asked, 0);
            Interlocked.Exchange(ref s_settled, 0);
            Interlocked.Exchange(ref s_criticalAsked, 0);
            Interlocked.Exchange(ref s_criticalSettled, 0);
        }

        // ---- The first look ---------------------------------------------------------------------

        private static int s_criticalAsked;
        private static int s_criticalSettled;

        /// <summary>
        /// On while a page's first look is put together (on the UI thread: the Store's carousel
        /// and the sections at the top of Home): the pictures asked for meanwhile are its first
        /// look. They are never background, and the loading curtain waits for every one of them
        /// (<see cref="CriticalAsked"/>, <see cref="CriticalSettled"/>).
        /// </summary>
        public static bool Critical { get; set; }

        public static int CriticalAsked => Volatile.Read(ref s_criticalAsked);
        public static int CriticalSettled => Volatile.Read(ref s_criticalSettled);

        /// <summary>Whether the picture is ready to show at once (in memory at that size).</summary>
        public static bool IsCached(string url, int decodeWidth)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            lock (s_lock)
                return s_cache.ContainsKey(decodeWidth + "|" + url);
        }

        /// <summary>A picture fetched into memory at that size, shown nowhere yet, in the background lane.</summary>
        public static void Prefetch(string url, int decodeWidth)
        {
            if (!string.IsNullOrEmpty(url) && !IsCached(url, decodeWidth))
                Show(new Image(), url, decodeWidth, null, null, Always);
        }

        // Pictures wait while this is open (the Store's genres and series being counted first).
        private static TaskCompletionSource<bool> s_gate = Open();

        private static TaskCompletionSource<bool> Open()
        {
            var gate = new TaskCompletionSource<bool>();
            gate.SetResult(true);
            return gate;
        }

        /// <summary>Downloads wait until Release, or <paramref name="most"/> at the longest.</summary>
        public static void HoldFor(TimeSpan most)
        {
            var gate = new TaskCompletionSource<bool>();
            s_gate = gate;
            Task.Delay(most).ContinueWith(_ => gate.TrySetResult(true));
        }

        public static void Release() => s_gate.TrySetResult(true);

        public static int Asked => Volatile.Read(ref s_asked);
        public static int Settled => Volatile.Read(ref s_settled);

        /// <summary>Shows the picture at <paramref name="url"/> in <paramref name="target"/> once it has loaded.</summary>
        /// <param name="keepIn">
        /// A folder to keep the downloaded file in, for the few pictures that never change (rating
        /// board badges). Such a picture is downloaded once and read from the folder from then on,
        /// also after Playnite restarts. Null for pictures that should always come from the server.
        /// </param>
        /// <param name="fallback">Another address for the same picture, tried when <paramref name="url"/> fails.</param>
        /// <param name="background">Fetched in the background lane (<see cref="BackgroundLimit"/>).</param>
        public static void Show(Image target, string url, int decodeWidth, string keepIn = null, string fallback = null, bool background = false) =>
            Show(target, url, decodeWidth, keepIn, fallback, background ? Always : null);

        /// <param name="backgroundWhile">Fetched in the background lane while this says so (asked
        /// until the download starts: the Store's pictures stop being background once it shows).</param>
        public static void Show(Image target, string url, int decodeWidth, string keepIn, string fallback, Func<bool> backgroundWhile)
        {
            // Part of a first look: never background, and counted for the curtain.
            bool critical = Critical;
            Func<bool> background = critical ? null : backgroundWhile;
            if (target == null)
                return;
            if (string.IsNullOrEmpty(url))
            {
                url = fallback;
                fallback = null;
            }
            if (string.IsNullOrEmpty(url))
                return;

            string key = decodeWidth + "|" + url;

            // The element remembers what it is waiting for, so a slow earlier request cannot
            // overwrite a later one.
            target.Tag = key;

            lock (s_lock)
            {
                if (s_cache.TryGetValue(key, out ImageSource cached))
                {
                    Spinner.Detach(target);
                    target.Source = cached;
                    return;
                }
            }

            // Nothing showing yet: a spinner until it arrives (a picture being swapped keeps
            // the old one instead).
            if (target.Source == null)
                Spinner.Attach(target);
            Interlocked.Increment(ref s_asked);
            if (critical)
                Interlocked.Increment(ref s_criticalAsked);

            Task.Run(async () =>
            {
                ImageSource image = null;
                try
                {
                    image = await Load(url, decodeWidth, keepIn, background).ConfigureAwait(false);
                    if (image == null && !string.IsNullOrEmpty(fallback) && fallback != url)
                        image = await Load(fallback, decodeWidth, keepIn, background).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Increment(ref s_settled);
                    if (critical)
                        Interlocked.Increment(ref s_criticalSettled);
                }
                if (image == null)
                {
                    // Failed: no spinner left turning over an empty frame.
                    await target.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Equals(target.Tag, key))
                            Spinner.Detach(target);
                    }));
                    return;
                }

                lock (s_lock)
                {
                    if (!s_cache.ContainsKey(key))
                        s_order.Enqueue(key);
                    s_cache[key] = image;
                    while (s_cache.Count > MaxCached && s_order.Count > 0)
                        s_cache.Remove(s_order.Dequeue());
                }

                await target.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Equals(target.Tag, key))
                    {
                        Spinner.Detach(target);
                        target.Source = image;
                    }
                }));
            });
        }

        // The file a kept picture lives in: named from its address, so each address maps to one file.
        private static string KeptFile(string folder, string url)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
                string extension = Path.GetExtension(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(extension) || extension.Length > 5)
                    extension = ".img";
                return Path.Combine(folder, BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + extension);
            }
        }

        private static async Task<byte[]> Fetch(string url, string keepIn)
        {
            string file = null;
            if (!string.IsNullOrEmpty(keepIn))
            {
                try
                {
                    file = KeptFile(keepIn, url);
                    if (File.Exists(file) && new FileInfo(file).Length > 0)
                        return File.ReadAllBytes(file);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not read kept picture for {url}: {ex.Message}");
                    file = null;
                }
            }

            // A picture already on this computer (a library cover) is simply read.
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return File.ReadAllBytes(url);

            byte[] bytes = await s_http.GetByteArrayAsync(url).ConfigureAwait(false);

            if (file != null && bytes.Length > 0)
            {
                try
                {
                    // Written beside the target and moved into place, so an interrupted write
                    // never leaves half a picture to be read back later.
                    Directory.CreateDirectory(keepIn);
                    string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temp, bytes);
                    if (File.Exists(file))
                        File.Delete(temp);
                    else
                        File.Move(temp, file);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not keep picture {url}: {ex.Message}");
                }
            }

            return bytes;
        }

        private static ImageSource Decode(byte[] bytes, int decodeWidth)
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }

        private static async Task<ImageSource> Load(string url, int decodeWidth, string keepIn, Func<bool> background)
        {
            // A library game's artwork already on disk: read from there, no slot needed.
            if (keepIn == null)
            {
                string kept = KeptArt(url);
                if (kept != null)
                {
                    try
                    {
                        return Decode(File.ReadAllBytes(kept), decodeWidth);
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Store] Kept picture {url} unreadable, downloading it again: {ex.Message}");
                    }
                }
                else if (ArtFolder != null && s_artUrls.Contains(url))
                    keepIn = ArtFolder;   // one of the library's: kept once downloaded
            }
            await s_gate.Task.ConfigureAwait(false);
            bool inBackground = await TakeSlot(background).ConfigureAwait(false);
            try
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        byte[] bytes = await Fetch(url, keepIn).ConfigureAwait(false);
                        return Decode(bytes, decodeWidth);
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 2)
                            s_log.Warn($"[Store] Could not load picture {url}: {ex.Message}");
                        else
                            await Task.Delay(400).ConfigureAwait(false);
                    }
                }
                return null;
            }
            finally
            {
                if (inBackground)
                    Interlocked.Decrement(ref s_backgroundRunning);
                s_slots.Release();
            }
        }
    }
}
