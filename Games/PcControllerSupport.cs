using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>How well a PC game is played with a controller, as far as is known.</summary>
    public enum PadSupport
    {
        /// <summary>Nothing says (not on Steam or PCGamingWiki, or neither records it).</summary>
        Unknown,
        /// <summary>The whole game can be played with a controller.</summary>
        Full,
        /// <summary>Controllers work, but some of it needs the keyboard and mouse.</summary>
        Partial,
        /// <summary>Controllers work only with a fix or mod (PCGamingWiki's "hackable").</summary>
        NeedsFix,
        /// <summary>Controllers are not supported.</summary>
        None,
    }

    /// <summary>What is known of one PC game's controller support, and where it came from.</summary>
    public class PadInfo
    {
        public PadSupport Support { get; set; }
        /// <summary>Steam's own "controller_support": "full", "partial", or null (not declared).</summary>
        public string Steam { get; set; }
        /// <summary>PCGamingWiki's page for the game, when it has one.</summary>
        public string WikiPage { get; set; }
        /// <summary>PCGamingWiki's values: true, false, limited, hackable, unknown (or null).</summary>
        public string WikiController { get; set; }
        public string WikiFull { get; set; }
        public string WikiXInput { get; set; }
        public string WikiDualShock { get; set; }
        public string WikiOther { get; set; }
        public DateTime FetchedUtc { get; set; }

        /// <summary>
        /// Reads Xbox (XInput) controllers only, by PCGamingWiki: XInput yes, PlayStation and
        /// other controllers no. Such a game sees a PlayStation or Switch controller only through
        /// something that presents it as an Xbox one (DS4Windows, Steam Input).
        /// </summary>
        [JsonIgnore]
        public bool XInputOnly => Is(WikiXInput, "true") && !Is(WikiDualShock, "true") && !Is(WikiOther, "true")
                                  && (Is(WikiDualShock, "false") || Is(WikiOther, "false"));

        [JsonIgnore]
        public string WikiUrl => string.IsNullOrEmpty(WikiPage) ? null : "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(WikiPage.Replace(' ', '_'));

        private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Controller support of PC games, from the two places that record it: Steam's store
    /// (the developer's own "Full" or "Partial Controller Support"; nothing said is not a "no")
    /// and PCGamingWiki (tested by its editors: controllers supported, playable with a
    /// controller alone, Xbox / PlayStation / other controllers, and "hackable": works with a
    /// fix). Neither records a game that needs a controller, so that is not told apart.
    ///
    /// PCGamingWiki's page is found by the game's Steam app id, else by its name; its Input
    /// table is read from the page's wikitext ({{Input|controller support = ...}}), whose
    /// parameter names are the template's documented ones. Kept on disk for a month. Neither
    /// is asked anything that identifies the user; their own clients, not RomM's.
    /// </summary>
    public class PcControllerSupport
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
        private static readonly HttpClient s_http = CreateClient();
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly string _folder;
        private readonly ConcurrentDictionary<string, PadInfo> _known = new ConcurrentDictionary<string, PadInfo>();
        private readonly ConcurrentDictionary<string, Task<PadInfo>> _asking = new ConcurrentDictionary<string, Task<PadInfo>>();
        private readonly SemaphoreSlim _one = new SemaphoreSlim(1);

        public PcControllerSupport(string folder)
        {
            _folder = folder;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RomM-Playnite-Plugin");
            return client;
        }

        private static string KeyOf(int? steamId, string name) =>
            steamId > 0 ? "steam-" + steamId.Value : "name-" + Regex.Replace((name ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

        /// <summary>What is kept already (memory or disk), without asking; null when nothing is.</summary>
        public PadInfo Cached(int? steamId, string name)
        {
            string key = KeyOf(steamId, name);
            if (key.EndsWith("-"))
                return null;
            if (_known.TryGetValue(key, out PadInfo known))
                return known;
            try
            {
                string file = Path.Combine(_folder, key + ".json");
                if (File.Exists(file))
                {
                    var info = JsonConvert.DeserializeObject<PadInfo>(File.ReadAllText(file));
                    if (info != null)
                    {
                        _known[key] = info;
                        return info;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Controllers] Could not read the kept support for {key}: {ex.Message}");
            }
            return null;
        }

        /// <summary>What is known, asking Steam and PCGamingWiki when nothing (recent) is kept.</summary>
        public Task<PadInfo> GetAsync(int? steamId, string name)
        {
            string key = KeyOf(steamId, name);
            PadInfo cached = Cached(steamId, name);
            if (cached != null && DateTime.UtcNow - cached.FetchedUtc < KeepFor)
                return Task.FromResult(cached);
            if (key.EndsWith("-"))
                return Task.FromResult<PadInfo>(null);
            return _asking.GetOrAdd(key, k => Task.Run(async () =>
            {
                try
                {
                    return await Fetch(k, steamId, name).ConfigureAwait(false) ?? cached;
                }
                finally
                {
                    _asking.TryRemove(k, out Task<PadInfo> _);
                }
            }));
        }

        private async Task<PadInfo> Fetch(string key, int? steamId, string name)
        {
            await _one.WaitAsync().ConfigureAwait(false);
            try
            {
                var info = new PadInfo { FetchedUtc = DateTime.UtcNow };
                if (steamId > 0)
                    info.Steam = await SteamSupport(steamId.Value).ConfigureAwait(false);

                info.WikiPage = await WikiPageBySteamId(steamId).ConfigureAwait(false) ?? await WikiPageByName(name).ConfigureAwait(false);
                if (info.WikiPage != null)
                {
                    string text = await WikiText(info.WikiPage).ConfigureAwait(false);
                    info.WikiController = Param(text, "controller support");
                    info.WikiFull = Param(text, "full controller");
                    info.WikiXInput = Param(text, "xinput controllers");
                    info.WikiDualShock = Param(text, "dualshock 4");
                    info.WikiOther = Param(text, "other controllers");
                }
                info.Support = Decide(info);

                _known[key] = info;
                try
                {
                    Directory.CreateDirectory(_folder);
                    File.WriteAllText(Path.Combine(_folder, key + ".json"), JsonConvert.SerializeObject(info));
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Controllers] Could not keep the support for {key}: {ex.Message}");
                }
                return info;
            }
            catch (Exception ex)
            {
                s_log.Info($"[Controllers] Could not find out the controller support of {name}: {ex.Message}");
                return null;
            }
            finally
            {
                _one.Release();
            }
        }

        /// <summary>
        /// The answer from both: the better-tested "full" of either wins; then PCGamingWiki's
        /// own word (it is tested); then Steam's. Steam saying nothing is not a "no".
        /// </summary>
        internal static PadSupport Decide(PadInfo info)
        {
            string wiki = (info.WikiController ?? "").ToLowerInvariant();
            string full = (info.WikiFull ?? "").ToLowerInvariant();
            string steam = (info.Steam ?? "").ToLowerInvariant();
            if (full == "true" || steam == "full")
                return PadSupport.Full;
            switch (wiki)
            {
                case "true":
                case "limited":
                    return PadSupport.Partial;
                case "hackable":
                    return steam == "partial" ? PadSupport.Partial : PadSupport.NeedsFix;
                case "false":
                    return steam == "partial" ? PadSupport.Partial : PadSupport.None;
            }
            if (steam == "partial")
                return PadSupport.Partial;
            return PadSupport.Unknown;
        }

        // ---- Steam --------------------------------------------------------------------------

        private static async Task<string> SteamSupport(int appId)
        {
            try
            {
                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                using (var response = await s_http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    JObject root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    JToken entry = root[appId.ToString()];
                    JToken data = entry?["success"]?.Value<bool>() == true ? entry["data"] : null;
                    if (data == null)
                        return null;
                    string support = data.Value<string>("controller_support");
                    if (!string.IsNullOrEmpty(support))
                        return support.ToLowerInvariant();
                    // The same as the store page's features: 28 "Full Controller Support", 18 "Partial".
                    var categories = (data["categories"] as JArray)?.Select(c => c.Value<int?>("id") ?? 0).ToList();
                    if (categories?.Contains(28) == true)
                        return "full";
                    if (categories?.Contains(18) == true)
                        return "partial";
                    return null;
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Controllers] Steam did not answer for {appId}: {ex.Message}");
                return null;
            }
        }

        // ---- PCGamingWiki -------------------------------------------------------------------

        private const string Wiki = "https://www.pcgamingwiki.com/w/api.php";

        private static async Task<string> WikiPageBySteamId(int? steamId)
        {
            if (!(steamId > 0))
                return null;
            string url = Wiki + "?action=cargoquery&tables=Infobox_game&fields=Infobox_game._pageName=Page"
                         + "&where=" + Uri.EscapeDataString($"Infobox_game.Steam_AppID HOLDS \"{steamId.Value}\"") + "&format=json";
            return await FirstPage(url).ConfigureAwait(false);
        }

        private static async Task<string> WikiPageByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            // "(Black Edition)", "[GOTY]": not in the wiki's titles.
            string plain = Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
            foreach (string title in new[] { plain, name.Trim() }.Distinct())
            {
                string escaped = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string url = Wiki + "?action=cargoquery&tables=Infobox_game&fields=Infobox_game._pageName=Page"
                             + "&where=" + Uri.EscapeDataString($"Infobox_game._pageName=\"{escaped}\"") + "&format=json";
                string page = await FirstPage(url).ConfigureAwait(false);
                if (page != null)
                    return page;
            }
            return null;
        }

        private static async Task<string> FirstPage(string url)
        {
            try
            {
                using (var response = await s_http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    JObject root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var first = (root["cargoquery"] as JArray)?.FirstOrDefault();
                    string page = first?["title"]?.Value<string>("Page");
                    return string.IsNullOrWhiteSpace(page) ? null : WebUtility.HtmlDecode(page);
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Controllers] PCGamingWiki did not answer: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> WikiText(string page)
        {
            try
            {
                string url = Wiki + "?action=parse&prop=wikitext&redirects=1&format=json&page=" + Uri.EscapeDataString(page);
                using (var response = await s_http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    JObject root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    return root["parse"]?["wikitext"]?.Value<string>("*");
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Controllers] Could not read PCGamingWiki's {page}: {ex.Message}");
                return null;
            }
        }

        // A parameter of the page's {{Input}} table: "|controller support = true".
        internal static string Param(string wikitext, string name)
        {
            if (string.IsNullOrEmpty(wikitext))
                return null;
            int start = wikitext.IndexOf("{{Input", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return null;
            string input = wikitext.Substring(start);
            Match match = Regex.Match(input, @"^\s*\|\s*" + Regex.Escape(name) + @"\s*=\s*([A-Za-z/]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }
    }
}
