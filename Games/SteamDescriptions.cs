using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>A game's text from its Steam store page.</summary>
    public class SteamText
    {
        public int AppId { get; set; }

        /// <summary>The line or two under the header picture ("short_description"), plain text.</summary>
        public string Short { get; set; }

        /// <summary>"About This Game" as Steam's page shows it, in Steam's HTML.</summary>
        public string AboutHtml { get; set; }

        public DateTime FetchedUtc { get; set; }

        [JsonIgnore] public bool IsEmpty => string.IsNullOrWhiteSpace(Short) && string.IsNullOrWhiteSpace(AboutHtml);

        [JsonIgnore] public string AboutText => SteamDescriptions.ToPlainText(AboutHtml);

        /// <summary>
        /// Steam's description for Playnite's own description field, or null when it has no
        /// words: some games' "About" is nothing but looping banner videos (Cyberpunk 2077's
        /// is), which Playnite cannot show, so RomM's text is better there. Videos are taken
        /// out; pictures stay.
        /// </summary>
        [JsonIgnore] public string AboutHtmlForPlaynite
        {
            get
            {
                if (string.IsNullOrWhiteSpace(AboutText))
                    return null;
                return System.Text.RegularExpressions.Regex.Replace(AboutHtml, @"<video[\s\S]*?</video>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }
    }

    /// <summary>
    /// Steam's descriptions for games RomM has matched on Steam (a rom's steam_id), which
    /// replace RomM's own summary where the setting allows. Read from Steam's public store API,
    /// one game at a time and spaced out (Steam allows roughly 200 a five minutes), and kept on
    /// disk for a month so each game is asked about rarely.
    ///
    /// Steam is asked with a client of its own: the RomM client carries the RomM credentials,
    /// which must never be sent anywhere else.
    /// </summary>
    public class SteamDescriptions
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
        private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(1600);
        private static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly ILogger s_log = LogManager.GetLogger();

        /// <summary>The one in use, for code with no plugin at hand (the metadata mapper).</summary>
        public static SteamDescriptions Current { get; private set; }

        /// <summary>Marks a Playnite description as Steam's, so it is not fetched again.</summary>
        public const string Marker = "<!-- steam-description -->";

        private readonly string _folder;
        private readonly Func<bool> _enabled;
        private readonly ConcurrentDictionary<int, SteamText> _known = new ConcurrentDictionary<int, SteamText>();
        private readonly ConcurrentDictionary<int, Task<SteamText>> _asking = new ConcurrentDictionary<int, Task<SteamText>>();
        private readonly SemaphoreSlim _one = new SemaphoreSlim(1);
        private DateTime _lastAsked = DateTime.MinValue;
        private DateTime _quietUntil = DateTime.MinValue;

        public SteamDescriptions(string folder, Func<bool> enabled)
        {
            _folder = folder;
            _enabled = enabled;
            Current = this;
        }

        public bool Enabled => _enabled == null || _enabled();

        /// <summary>The text when it is already here (memory or disk), without asking Steam.</summary>
        public SteamText Cached(int? appId)
        {
            if (!Enabled || appId == null || appId.Value <= 0)
                return null;
            int id = appId.Value;
            if (_known.TryGetValue(id, out SteamText known))
                return known.IsEmpty ? null : known;

            try
            {
                string file = FileFor(id);
                if (File.Exists(file))
                {
                    var text = JsonConvert.DeserializeObject<SteamText>(File.ReadAllText(file));
                    if (text != null)
                    {
                        _known[id] = text;
                        return text.IsEmpty ? null : text;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Steam] Could not read the kept text for {id}: {ex.Message}");
            }
            return null;
        }

        /// <summary>The text, from Steam when it is not here or is old. Null when there is none.</summary>
        public Task<SteamText> GetAsync(int? appId)
        {
            if (!Enabled || appId == null || appId.Value <= 0)
                return Task.FromResult<SteamText>(null);
            int id = appId.Value;

            SteamText cached = Cached(id);
            _known.TryGetValue(id, out SteamText any);
            if (any != null && DateTime.UtcNow - any.FetchedUtc < KeepFor)
                return Task.FromResult(cached);

            return _asking.GetOrAdd(id, key => Task.Run(async () =>
            {
                try
                {
                    return await Fetch(key).ConfigureAwait(false) ?? cached;
                }
                finally
                {
                    _asking.TryRemove(key, out Task<SteamText> _);
                }
            }));
        }

        private string FileFor(int appId) => Path.Combine(_folder, appId + ".json");

        private async Task<SteamText> Fetch(int appId)
        {
            await _one.WaitAsync().ConfigureAwait(false);
            try
            {
                // Steam turned us away recently: leave it be for a while.
                if (DateTime.UtcNow < _quietUntil)
                    return null;
                TimeSpan wait = _lastAsked + Spacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait).ConfigureAwait(false);
                _lastAsked = DateTime.UtcNow;

                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                using (var response = await s_http.GetAsync(url).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        s_log.Info("[Steam] Steam asked us to slow down; trying again in a few minutes.");
                        _quietUntil = DateTime.UtcNow.AddMinutes(5);
                        return null;
                    }
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var entry = root[appId.ToString()];
                    var data = entry?["success"]?.Value<bool>() == true ? entry["data"] : null;

                    // An app Steam does not list (any more) is remembered as having nothing, so
                    // it is not asked about again for a month.
                    var text = new SteamText
                    {
                        AppId = appId,
                        Short = data == null ? null : WebUtility.HtmlDecode(data.Value<string>("short_description") ?? "").Trim(),
                        AboutHtml = data == null ? null : FirstNonEmpty(data.Value<string>("about_the_game"), data.Value<string>("detailed_description")),
                        FetchedUtc = DateTime.UtcNow,
                    };
                    _known[appId] = text;
                    try
                    {
                        Directory.CreateDirectory(_folder);
                        File.WriteAllText(FileFor(appId), JsonConvert.SerializeObject(text));
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Steam] Could not keep the text for {appId}: {ex.Message}");
                    }
                    return text.IsEmpty ? null : text;
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Steam] Could not fetch the description of app {appId}: {ex.Message}");
                return null;
            }
            finally
            {
                _one.Release();
            }
        }

        private static string FirstNonEmpty(string a, string b) => !string.IsNullOrWhiteSpace(a) ? a.Trim() : (string.IsNullOrWhiteSpace(b) ? null : b.Trim());

        /// <summary>Steam's HTML as plain text: paragraphs and list items on their own lines.</summary>
        public static string ToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return "";
            string text = Regex.Replace(html, @"\r?\n", " ");
            text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<\s*li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<\s*/\s*(p|div|h[1-6]|ul|ol)\s*>", "\n\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<\s*(h[1-6])[^>]*>", "\n\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", "");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"[ \t ]+", " ");
            text = Regex.Replace(text, @" *\n *", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }
    }
}
