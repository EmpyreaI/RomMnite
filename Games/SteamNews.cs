using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>One news post of a game's, from its Steam page.</summary>
    public class SteamNewsItem
    {
        public int AppId { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public DateTime PostedUtc { get; set; }
        public string Image { get; set; }     // the post's first picture, if it has one
        public string Feed { get; set; }
        public string Summary { get; set; }   // the post's opening text, without markup
    }

    /// <summary>
    /// The latest news of games on Steam (their developers' announcements), from Steam's public
    /// news API, for the library's What's New shelf. Each game is asked about at most every
    /// few hours; answers are kept on disk. Steam is asked with a client of its own, so the
    /// RomM credentials never go to it.
    /// </summary>
    public class SteamNews
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromHours(3);
        // A game with no announcements is asked again sooner, in case one is posted.
        private static readonly TimeSpan KeepEmptyFor = TimeSpan.FromMinutes(45);
        // Answers kept by an earlier way of asking are not trusted (version 1 kept only the
        // announcements among a game's six latest posts, which for a much-covered game were
        // often all press articles, so nothing was kept).
        // Version 3 keeps each post's opening text (Summary) too.
        private const int Version = 5;   // 5: each post's own banner (Steam's event capsule) as its picture   // 4: pictures given as [img src="..."] found, tags with attributes taken out of the text
        private static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly ILogger s_log = LogManager.GetLogger();
        // Pictures in a post: BBCode's [img]address[/img], the newer [img src="address"], or HTML's <img src="address">.
        private static readonly Regex ImageTag = new Regex(@"\[img\]([^\[]+?)\[/img\]|\[img\b[^\]]*?\bsrc\s*=\s*[""']?([^""'\]\s]+)|<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex YouTube = new Regex(@"\[previewyoutube=([A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Steam ids of groups ("clans") start here; the rest is the group's account id, which names
        // the folder its pictures are in.
        private const ulong ClanIdBase = 103582791429521408UL;

        private sealed class Kept
        {
            public int Version { get; set; }
            public DateTime FetchedUtc { get; set; }
            public List<SteamNewsItem> Items { get; set; } = new List<SteamNewsItem>();
        }

        private readonly string _folder;
        private readonly ConcurrentDictionary<int, Kept> _known = new ConcurrentDictionary<int, Kept>();
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(4);

        public SteamNews(string folder)
        {
            _folder = folder;
        }

        private string FileFor(int appId) => Path.Combine(_folder, appId + ".json");

        /// <summary>The game's latest announcements, newest first (from disk when recent enough).</summary>
        public async Task<List<SteamNewsItem>> GetAsync(int appId)
        {
            if (appId <= 0)
                return new List<SteamNewsItem>();

            if (!_known.TryGetValue(appId, out Kept kept))
            {
                try
                {
                    string file = FileFor(appId);
                    if (File.Exists(file))
                    {
                        kept = JsonConvert.DeserializeObject<Kept>(File.ReadAllText(file));
                        if (kept != null && kept.Version == Version)
                            _known[appId] = kept;
                        else
                            kept = null;
                    }
                }
                catch
                {
                    kept = null;
                }
            }
            if (kept != null && DateTime.UtcNow - kept.FetchedUtc < (kept.Items.Count > 0 ? KeepFor : KeepEmptyFor))
                return kept.Items;

            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                // Only the game's own announcements (its Steam community feed), not the press
                // articles Steam also carries, which for a popular game crowd them out.
                string url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count=10&maxlength=0&format=json&feeds=steam_community_announcements";
                using (var response = await s_http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        s_log.Info($"[SteamNews] Steam answered {(int)response.StatusCode} for the news of {appId}.");
                        return kept?.Items ?? new List<SteamNewsItem>();
                    }
                    var root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var items = new List<SteamNewsItem>();
                    Dictionary<string, string> banners = await Banners(appId).ConfigureAwait(false);
                    var posts = root.SelectToken("appnews.newsitems") as JArray ?? new JArray();
                    foreach (JToken post in posts)
                    {
                        // The game's own announcements, not press articles about it (asked for
                        // above; checked again in case Steam ignores the request).
                        string feedName = (string)post["feedname"] ?? "";
                        if (feedName != "steam_community_announcements" && (int?)post["feed_type"] != 1)
                            continue;
                        string contents = (string)post["contents"] ?? "";
                        items.Add(new SteamNewsItem
                        {
                            AppId = appId,
                            Title = System.Net.WebUtility.HtmlDecode((string)post["title"] ?? ""),
                            Url = (string)post["url"],
                            PostedUtc = DateTimeOffset.FromUnixTimeSeconds((long?)post["date"] ?? 0).UtcDateTime,
                            // The banner Steam shows the post with; else its first picture; else
                            // the picture of its first video (posts that are only a trailer).
                            Image = Banner(banners, (string)post["gid"], (string)post["title"]) ?? FirstImage(contents) ?? VideoPicture(contents),
                            Feed = (string)post["feedlabel"],
                            Summary = Excerpt(contents, 360),
                        });
                    }
                    s_log.Debug($"[SteamNews] {appId}: {posts.Count} posts, {items.Count} announcements kept.");
                    kept = new Kept { Version = Version, FetchedUtc = DateTime.UtcNow, Items = items };
                    _known[appId] = kept;
                    try
                    {
                        Directory.CreateDirectory(_folder);
                        File.WriteAllText(FileFor(appId), JsonConvert.SerializeObject(kept));
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[SteamNews] Could not keep the news of {appId}: {ex.Message}");
                    }
                    return items;
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[SteamNews] Could not fetch the news of {appId}: {ex.Message}");
                return kept?.Items ?? new List<SteamNewsItem>();
            }
            finally
            {
                _slots.Release();
            }
        }

        private static readonly Regex Markup = new Regex(@"\[img\].*?\[/img\]|\[previewyoutube[^\]]*\].*?\[/previewyoutube\]|\[/?[a-z0-9*]+(?:[=\s][^\]]*)?\]|<[^>]+>|\{STEAM_CLAN_IMAGE\}\S*|https?://\S+",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Space = new Regex(@"\s+", RegexOptions.Compiled);

        // The post's opening words as plain text: Steam's markup (BBCode, or HTML for some
        // posts), pictures, videos and bare links taken out; cut at a word near the length.
        internal static string Excerpt(string contents, int length)
        {
            string text = Space.Replace(System.Net.WebUtility.HtmlDecode(Markup.Replace(contents ?? "", " ")), " ").Trim();
            if (text.Length <= length)
                return text;
            int cut = text.LastIndexOf(' ', length);
            return text.Substring(0, cut > length / 2 ? cut : length).TrimEnd(',', '.', ';', ':', ' ') + "...";
        }

        /// <summary>
        /// The banner of each of the game's recent announcements, as Steam's library and store show
        /// them: the "capsule" picture of the event each announcement is posted as, by the
        /// announcement's id and by its title. From the store's events list (the one its news
        /// pages read, not a documented API): when it cannot be read, the posts fall back to
        /// their own first picture. Pictures live at
        /// clan.akamai.steamstatic.com/images/&lt;group account id&gt;/&lt;file&gt;.
        /// </summary>
        private static async Task<Dictionary<string, string>> Banners(int appId)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string url = $"https://store.steampowered.com/events/ajaxgetpartnereventspageable/?clan_accountid=0&appid={appId}&offset=0&count=20&l=english";
                using (var response = await s_http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        s_log.Debug($"[SteamNews] The events of {appId}: Steam answered {(int)response.StatusCode}.");
                        return result;
                    }
                    var root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    foreach (JToken ev in root["events"] as JArray ?? new JArray())
                    {
                        if (!ulong.TryParse((string)ev["clan_steamid"], out ulong clan) || clan <= ClanIdBase)
                            continue;
                        JObject json;
                        try
                        {
                            json = JObject.Parse((string)ev["jsondata"] ?? "{}");
                        }
                        catch
                        {
                            continue;
                        }
                        // One per language, English first; the first one given.
                        string file = (json["localized_capsule_image"] as JArray ?? new JArray())
                            .Select(t => t.Type == JTokenType.String ? (string)t : null)
                            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
                        if (file == null)
                            continue;
                        string picture = $"https://clan.akamai.steamstatic.com/images/{clan - ClanIdBase}/{file}";
                        string gid = (string)ev.SelectToken("announcement_body.gid");
                        if (!string.IsNullOrEmpty(gid))
                            result[gid] = picture;
                        string title = (string)ev.SelectToken("announcement_body.headline") ?? (string)ev["event_name"];
                        if (!string.IsNullOrEmpty(title))
                            result["title:" + title.Trim()] = picture;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[SteamNews] Could not read the events of {appId}: {ex.Message}");
            }
            return result;
        }

        private static string Banner(Dictionary<string, string> banners, string gid, string title)
        {
            if (!string.IsNullOrEmpty(gid) && banners.TryGetValue(gid, out string byId))
                return byId;
            if (!string.IsNullOrEmpty(title) && banners.TryGetValue("title:" + System.Net.WebUtility.HtmlDecode(title).Trim(), out string byTitle))
                return byTitle;
            return null;
        }

        // A post that is only a video: the video's own picture, from YouTube.
        private static string VideoPicture(string contents)
        {
            Match match = YouTube.Match(contents ?? "");
            return match.Success ? $"https://img.youtube.com/vi/{match.Groups[1].Value}/hqdefault.jpg" : null;
        }

        // The first picture in a post; Steam writes its own image host as a placeholder.
        private static string FirstImage(string contents)
        {
            Match match = ImageTag.Match(contents ?? "");
            if (!match.Success)
                return null;
            string url = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Trim();
            url = url.Replace("{STEAM_CLAN_IMAGE}", "https://clan.akamai.steamstatic.com/images");
            return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
        }
    }
}
