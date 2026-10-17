using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>A game's article on Wikipedia: its opening section, as plain text.</summary>
    public class WikipediaArticle
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Extract { get; set; }
    }

    /// <summary>
    /// The opening of a game's English Wikipedia article, for the Store's "About this game" when
    /// there is neither a Steam description nor box art. Found with one search of Wikipedia's
    /// public API ("&lt;name&gt; video game"; the search result's introduction comes back with it),
    /// taken only when the article's title is the game's name (allowing Wikipedia's
    /// "(video game)" / "(2008 video game)" kind of suffix) and the text speaks of a game, so
    /// an unrelated article is not shown as the game's. Answers, found or not, are kept on disk
    /// for a month. Wikipedia is asked with a client of its own, naming this plugin as its
    /// rules ask, so the RomM credentials never go to it. Text is CC BY-SA: whoever shows it
    /// credits Wikipedia with a link to the article.
    /// </summary>
    public class WikipediaSummary
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly HttpClient s_http = CreateClient();
        private static readonly Regex Suffix = new Regex(@"\s*\((?:\d{4}\s+)?(?:video\s+)?game\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed class Kept
        {
            public DateTime FetchedUtc { get; set; }
            public WikipediaArticle Article { get; set; }   // null: none found
        }

        private readonly string _folder;
        private readonly ConcurrentDictionary<string, Task<WikipediaArticle>> _asking = new ConcurrentDictionary<string, Task<WikipediaArticle>>();

        public WikipediaSummary(string folder)
        {
            _folder = folder;
        }

        private static HttpClient CreateClient()
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RomM-Playnite/1.0 (Playnite extension; https://github.com/rommapp/playnite-plugin)");
            return client;
        }

        /// <summary>The game's article, or null when none was found (or Wikipedia could not be asked).</summary>
        public Task<WikipediaArticle> GetAsync(string gameName)
        {
            string key = Normalize(gameName);
            if (key.Length == 0)
                return Task.FromResult<WikipediaArticle>(null);
            return _asking.GetOrAdd(key, _ => Task.Run(() => Find(gameName, key)));
        }

        private string FileFor(string key)
        {
            using (var sha = SHA1.Create())
                return Path.Combine(_folder, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant() + ".json");
        }

        private async Task<WikipediaArticle> Find(string gameName, string key)
        {
            string file = FileFor(key);
            try
            {
                if (File.Exists(file))
                {
                    var kept = JsonConvert.DeserializeObject<Kept>(File.ReadAllText(file));
                    if (kept != null && DateTime.UtcNow - kept.FetchedUtc < KeepFor)
                        return kept.Article;
                }
            }
            catch
            {
                // Asked again below.
            }

            try
            {
                string search = Uri.EscapeDataString(gameName + " video game");
                string url = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&redirects=1" +
                             $"&generator=search&gsrsearch={search}&gsrlimit=5&prop=extracts|info&exintro=1&explaintext=1&exlimit=5&inprop=url";
                using (var response = await s_http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        s_log.Info($"[Wikipedia] Wikipedia answered {(int)response.StatusCode} for {gameName}.");
                        _asking.TryRemove(key, out _);   // asked again next time
                        return null;
                    }
                    var root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    WikipediaArticle found = null;
                    foreach (JToken page in (root.SelectToken("query.pages") as JArray ?? new JArray()).OrderBy(p => (int?)p["index"] ?? 99))
                    {
                        string title = (string)page["title"] ?? "";
                        string extract = ((string)page["extract"] ?? "").Trim();
                        if (extract.Length < 80 || Normalize(Suffix.Replace(title, "")) != key)
                            continue;
                        if (extract.IndexOf("game", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        found = new WikipediaArticle { Title = title, Url = (string)page["fullurl"], Extract = extract };
                        break;
                    }
                    try
                    {
                        Directory.CreateDirectory(_folder);
                        File.WriteAllText(file, JsonConvert.SerializeObject(new Kept { FetchedUtc = DateTime.UtcNow, Article = found }));
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Wikipedia] Could not keep the answer for {gameName}: {ex.Message}");
                    }
                    s_log.Debug($"[Wikipedia] {gameName}: {(found != null ? found.Title : "no article")}.");
                    return found;
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Wikipedia] Could not ask about {gameName}: {ex.Message}");
                _asking.TryRemove(key, out _);
                return null;
            }
        }

        // For comparing names: letters and digits only, lower case, accents off, "&" as "and".
        internal static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";
            string text = name.Replace("&", " and ").Normalize(NormalizationForm.FormD);
            var result = new StringBuilder();
            foreach (char c in text)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;
                if (char.IsLetterOrDigit(c))
                    result.Append(char.ToLowerInvariant(c));
            }
            return result.ToString();
        }
    }
}
