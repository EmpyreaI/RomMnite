using Newtonsoft.Json.Linq;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RomM.Games
{
    /// <summary>
    /// Which RomM entries are one game on different platforms (the Store lists them once, the
    /// library shows one copy), and the media one lends another that lacks it.
    ///
    /// The same game, strongest evidence first:
    ///   IGDB says so: the same IGDB id, or one lists the other among its ports or remasters
    ///     (igdb_metadata; never its remakes, which are other games: Resident Evil 2 of 1998 and
    ///     of 2019).
    ///   The same ScreenScraper id on different platforms. ScreenScraper's ids are per platform,
    ///     so two platforms share one only when someone gave it to both: their saying so.
    ///   The same MobyGames id, unless their IGDB ids differ.
    ///   One's name is the other's with an edition's words after it ("Metal Gear Solid: Peace
    ///     Walker - Master Collection Version", "Okami HD", "... Definitive Edition"): a port or
    ///     remaster under its own IGDB id, so this holds even where the IGDB ids differ.
    ///   The same name where one of them has neither id. Two with the same plain name and
    ///     different IGDB ids are never one by their name alone (Doom of 1993 and of 2016).
    /// </summary>
    internal static class SameGame
    {
        /// <summary>What an entry is matched by.</summary>
        internal sealed class Facts
        {
            public int Igdb;
            public int Moby;
            public int Ss;
            public int PlatformId;
            public string Name;
            /// <summary>IGDB ids of its ports and remasters.</summary>
            public List<int> Linked = new List<int>();
        }

        // Edition words, at the end of a name after a separator (" - ", ": ", " (", or a space for
        // the short ones). Not "Remake": a remake is another game.
        private static readonly string[] s_editions =
        {
            "master collection version", "master collection",
            "hd remaster", "hd remastered", "remastered", "remaster", "hd",
            "definitive edition", "complete edition", "game of the year edition", "goty edition", "goty",
            "enhanced edition", "special edition", "deluxe edition", "ultimate edition", "legendary edition",
            "anniversary edition", "directors cut", "director's cut", "the final cut", "final cut",
        };

        /// <summary>The name without the edition at its end ("Okami HD" → "Okami"), as given otherwise.</summary>
        public static string EditionBase(string name)
        {
            string text = (name ?? "").Trim();
            for (bool more = true; more;)
            {
                more = false;
                foreach (string edition in s_editions)
                {
                    string stripped = StripEnding(text, edition);
                    if (stripped != null)
                    {
                        text = stripped;
                        more = true;
                        break;
                    }
                }
            }
            return text;
        }

        // The text before "<sep><edition>[)]" at its end, or null.
        private static string StripEnding(string text, string edition)
        {
            string t = text.TrimEnd();
            bool bracket = t.EndsWith(")", StringComparison.Ordinal);
            if (bracket)
                t = t.Substring(0, t.Length - 1).TrimEnd();
            if (!t.EndsWith(edition, StringComparison.OrdinalIgnoreCase))
                return null;
            string before = t.Substring(0, t.Length - edition.Length);
            if (before.Length == 0)
                return null;
            string rest;
            if (bracket)
            {
                if (!before.EndsWith("(", StringComparison.Ordinal))
                    return null;
                rest = before.Substring(0, before.Length - 1);
            }
            else if (before.EndsWith(" - ", StringComparison.Ordinal) || before.EndsWith(": ", StringComparison.Ordinal)
                || before.EndsWith(" – ", StringComparison.Ordinal))
                rest = before.Substring(0, before.Length - 2);
            else if (before.EndsWith(" ", StringComparison.Ordinal))
                rest = before;
            else
                return null;
            rest = rest.TrimEnd(' ', '-', ':', '–');
            return rest.Length == 0 ? null : rest;
        }

        /// <summary>Letters and digits, lower case: names compared this way.</summary>
        public static string Plain(string name)
        {
            var key = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (char.IsLetterOrDigit(c))
                    key.Append(char.ToLowerInvariant(c));
            }
            return key.ToString();
        }

        /// <summary>The name a game is grouped by: its plain name without the edition.</summary>
        public static string Key(string name) => Plain(EditionBase(name));

        /// <summary>Whether the name has an edition's words at its end.</summary>
        public static bool IsEdition(string name) => Plain(name) != Key(name);

        /// <summary>The facts of a ROM (and its raw JSON, for what the model leaves out).</summary>
        public static Facts Of(RomMRom rom, JObject raw = null)
        {
            var facts = new Facts
            {
                Igdb = rom?.IgdbId ?? 0,
                Moby = IdOf(raw?["moby_id"]),
                Ss = rom?.SSId ?? 0,
                PlatformId = rom?.PlatformId ?? 0,
                Name = rom?.Name ?? rom?.FileNameNoExt ?? "",
            };
            if (facts.Moby == 0)
                facts.Moby = IdOf(rom?.MobyId);
            if (rom?.IgdbMetadata?.Ports != null)
                facts.Linked.AddRange(rom.IgdbMetadata.Ports.Where(p => p != null && p.Id > 0).Select(p => p.Id));
            if (rom?.IgdbMetadata?.Remasters != null)
                facts.Linked.AddRange(rom.IgdbMetadata.Remasters.Where(p => p != null && p.Id > 0).Select(p => p.Id));
            return facts;
        }

        private static int IdOf(object value)
        {
            if (value == null)
                return 0;
            if (value is JToken token && token.Type == JTokenType.Null)
                return 0;
            return int.TryParse(value.ToString(), out int id) ? id : 0;
        }

        /// <summary>
        /// The entries in groups of one game each, as lists of their indexes, in the order the
        /// entries came (a group where its first entry was).
        /// </summary>
        public static List<List<int>> Group(IList<Facts> items)
        {
            int n = items.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            Func<int, int> find = null;
            find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
            Action<int, int> join = (a, b) =>
            {
                int ra = find(a), rb = find(b);
                if (ra != rb)
                    parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            };

            var byIgdb = new Dictionary<int, int>();
            var byMoby = new Dictionary<int, int>();
            var bySs = new Dictionary<int, int>();
            var byNameAny = new Dictionary<string, int>();
            var byNameBare = new Dictionary<string, int>();     // entries with neither id
            var byKeyAny = new Dictionary<string, int>();
            var byKeyEdition = new Dictionary<string, int>();   // entries named as an edition
            for (int i = 0; i < n; i++)
            {
                Facts f = items[i];
                if (f.Igdb > 0)
                {
                    if (byIgdb.TryGetValue(f.Igdb, out int other))
                        join(i, other);
                    else
                        byIgdb[f.Igdb] = i;
                }
                if (f.Moby > 0)
                {
                    // Not across two different IGDB games.
                    if (byMoby.TryGetValue(f.Moby, out int other) && (f.Igdb == 0 || items[other].Igdb == 0 || items[other].Igdb == f.Igdb))
                        join(i, other);
                    else if (!byMoby.ContainsKey(f.Moby))
                        byMoby[f.Moby] = i;
                }
                if (f.Ss > 0)
                {
                    // Different platforms only: on one platform the same id is RomM's siblings
                    // (regions, revisions), which are kept as they are.
                    if (bySs.TryGetValue(f.Ss, out int other) && items[other].PlatformId != f.PlatformId)
                        join(i, other);
                    else if (!bySs.ContainsKey(f.Ss))
                        bySs[f.Ss] = i;
                }

                string name = Plain(f.Name);
                if (name.Length == 0)
                    continue;
                bool bare = f.Igdb == 0 && f.Moby == 0;
                if (bare && byNameAny.TryGetValue(name, out int named))
                    join(i, named);
                else if (!bare && byNameBare.TryGetValue(name, out int unnamed))
                    join(i, unnamed);
                if (!byNameAny.ContainsKey(name))
                    byNameAny[name] = i;
                if (bare && !byNameBare.ContainsKey(name))
                    byNameBare[name] = i;

                // An edition and the game it is of.
                string key = Key(f.Name);
                bool edition = key.Length > 0 && key != name;
                if (key.Length > 0)
                {
                    if (edition && byKeyAny.TryGetValue(key, out int game))
                        join(i, game);
                    else if (!edition && byKeyEdition.TryGetValue(key, out int ofIt))
                        join(i, ofIt);
                    if (!byKeyAny.ContainsKey(key))
                        byKeyAny[key] = i;
                    if (edition && !byKeyEdition.ContainsKey(key))
                        byKeyEdition[key] = i;
                }
            }

            // IGDB's ports and remasters, either way round.
            for (int i = 0; i < n; i++)
            {
                foreach (int linked in items[i].Linked)
                {
                    if (byIgdb.TryGetValue(linked, out int other))
                        join(i, other);
                }
            }

            var groups = new Dictionary<int, List<int>>();
            var order = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int root = find(i);
                if (!groups.TryGetValue(root, out List<int> members))
                {
                    groups[root] = members = new List<int>();
                    order.Add(root);
                }
                members.Add(i);
            }
            return order.Select(root => groups[root]).ToList();
        }

        // ---- Media one version lends another ---------------------------------------------------

        // What is lent: ScreenScraper's media that belongs to the game, not to one platform's
        // release (fan art, logo, video, a screenshot), and screenshots. Not the box art or the
        // mix image, which show the other platform's box.

        /// <summary>How much of the lendable media a ROM has (0 to 4: fan art, logo, video, screenshots).</summary>
        public static int MediaScore(RomMRom rom)
        {
            var ss = rom?.SSMetadata;
            int score = 0;
            if (ss != null)
            {
                if (!string.IsNullOrEmpty(ss.FanartPath) || !string.IsNullOrEmpty(ss.FanartUrl))
                    score++;
                if (!string.IsNullOrEmpty(ss.LogoPath) || !string.IsNullOrEmpty(ss.LogoUrl))
                    score++;
                if (!string.IsNullOrEmpty(ss.VideoPath) || !string.IsNullOrEmpty(ss.VideoNormalizedPath))
                    score++;
            }
            if (rom?.MergedScreenshots != null && rom.MergedScreenshots.Any(s => !string.IsNullOrEmpty(s)))
                score++;
            return score;
        }

        /// <summary>Whether a ROM lacks some of the media another version could lend it.</summary>
        public static bool LacksMedia(RomMRom rom) => MediaScore(rom) < 4;

        /// <summary>The version with the most media to lend <paramref name="rom"/> (not itself), or null when none has any it lacks.</summary>
        public static RomMRom DonorFor(RomMRom rom, IEnumerable<RomMRom> group)
        {
            if (rom == null || !LacksMedia(rom))
                return null;
            return group.Where(r => r != null && r.Id != rom.Id && Lends(r, rom))
                .OrderByDescending(MediaScore)
                .FirstOrDefault();
        }

        private static bool Lends(RomMRom donor, RomMRom rom)
        {
            var a = rom.SSMetadata ?? new RomMSSMetadata();
            var d = donor.SSMetadata ?? new RomMSSMetadata();
            return (Missing(a.FanartPath) && Missing(a.FanartUrl) && !(Missing(d.FanartPath) && Missing(d.FanartUrl)))
                || (Missing(a.LogoPath) && Missing(a.LogoUrl) && !(Missing(d.LogoPath) && Missing(d.LogoUrl)))
                || (Missing(a.VideoPath) && Missing(a.VideoNormalizedPath) && !(Missing(d.VideoPath) && Missing(d.VideoNormalizedPath)))
                || ((rom.MergedScreenshots == null || rom.MergedScreenshots.Count == 0) && donor.MergedScreenshots != null && donor.MergedScreenshots.Count > 0);
        }

        private static bool Missing(string value) => string.IsNullOrEmpty(value);

        /// <summary>
        /// Fills what <paramref name="rom"/> lacks of the lendable media from <paramref name="donor"/>:
        /// each kind (fan art, logo, video) only when it has none of that kind, its screenshots
        /// only when it has none. What it has of its own stays. True when anything was lent.
        /// </summary>
        public static bool Lend(RomMRom rom, RomMRom donor)
        {
            if (rom == null || donor == null || ReferenceEquals(rom, donor))
                return false;
            var d = donor.SSMetadata;
            bool lent = false;
            if (d != null)
            {
                if (rom.SSMetadata == null)
                    rom.SSMetadata = new RomMSSMetadata();
                var a = rom.SSMetadata;
                if (Missing(a.FanartPath) && Missing(a.FanartUrl) && !(Missing(d.FanartPath) && Missing(d.FanartUrl)))
                {
                    a.FanartPath = d.FanartPath;
                    a.FanartUrl = d.FanartUrl;
                    lent = true;
                }
                if (Missing(a.LogoPath) && Missing(a.LogoUrl) && !(Missing(d.LogoPath) && Missing(d.LogoUrl)))
                {
                    a.LogoPath = d.LogoPath;
                    a.LogoUrl = d.LogoUrl;
                    lent = true;
                }
                if (Missing(a.VideoPath) && Missing(a.VideoNormalizedPath) && !(Missing(d.VideoPath) && Missing(d.VideoNormalizedPath)))
                {
                    a.VideoPath = d.VideoPath;
                    a.VideoNormalizedPath = d.VideoNormalizedPath;
                    lent = true;
                }
                if (Missing(a.ScreenshotUrl) && !Missing(d.ScreenshotUrl))
                    a.ScreenshotUrl = d.ScreenshotUrl;
            }
            if ((rom.MergedScreenshots == null || rom.MergedScreenshots.Count == 0)
                && donor.MergedScreenshots != null && donor.MergedScreenshots.Count > 0)
            {
                rom.MergedScreenshots = donor.MergedScreenshots.ToList();
                lent = true;
            }
            return lent;
        }

        /// <summary>The same as <see cref="Lend(RomMRom, RomMRom)"/>, on the ROMs' JSON (the Store reads both).</summary>
        public static void Lend(JObject raw, JObject donor)
        {
            if (raw == null || donor == null || ReferenceEquals(raw, donor))
                return;
            if (donor["ss_metadata"] is JObject d)
            {
                if (!(raw["ss_metadata"] is JObject a))
                    raw["ss_metadata"] = a = new JObject();
                foreach (string[] kind in new[]
                {
                    new[] { "fanart_path", "fanart_url" }, new[] { "logo_path", "logo_url" },
                    new[] { "video_path", "video_normalized_path" }, new[] { "screenshot_url" },
                })
                {
                    if (kind.All(k => Empty(a[k])) && kind.Any(k => !Empty(d[k])))
                    {
                        foreach (string k in kind)
                            a[k] = d[k]?.DeepClone();
                    }
                }
            }
            bool hasShots = raw["merged_screenshots"] is JArray own && own.Count > 0;
            if (!hasShots && donor["merged_screenshots"] is JArray lent && lent.Count > 0)
                raw["merged_screenshots"] = donor["merged_screenshots"].DeepClone();
        }

        private static bool Empty(JToken token) =>
            token == null || token.Type == JTokenType.Null || (token.Type == JTokenType.String && string.IsNullOrEmpty((string)token));

        /// <summary>
        /// Every ROM given that lacks media and has another version among them with it, lent that
        /// version's (each group's ROMs lending one another). The number of ROMs lent to.
        /// </summary>
        public static int LendAcross(IList<RomMRom> roms)
        {
            if (roms == null || roms.Count < 2)
                return 0;
            List<List<int>> groups = Group(roms.Select(r => Of(r)).ToList());
            int count = 0;
            foreach (List<int> group in groups)
            {
                if (group.Count < 2 || group.Select(i => roms[i].PlatformId).Distinct().Count() < 2)
                    continue;
                // Donors as the server has them, before any lending: one lent to is not then a donor.
                var members = group.Select(i => roms[i]).ToList();
                var originals = members.ToDictionary(r => r, r => Snapshot(r));
                foreach (RomMRom rom in members)
                {
                    // Another platform's version only: a sibling on the same platform is the same release.
                    RomMRom donor = DonorFor(rom, originals.Values.Where(o => o.PlatformId != rom.PlatformId));
                    if (donor != null && Lend(rom, donor))
                        count++;
                }
            }
            return count;
        }

        // A copy of what a ROM lends, so lending in a group never chains.
        private static RomMRom Snapshot(RomMRom rom) => new RomMRom
        {
            Id = rom.Id,
            PlatformId = rom.PlatformId,
            SSMetadata = rom.SSMetadata == null ? null : new RomMSSMetadata
            {
                FanartPath = rom.SSMetadata.FanartPath,
                FanartUrl = rom.SSMetadata.FanartUrl,
                LogoPath = rom.SSMetadata.LogoPath,
                LogoUrl = rom.SSMetadata.LogoUrl,
                VideoPath = rom.SSMetadata.VideoPath,
                VideoNormalizedPath = rom.SSMetadata.VideoNormalizedPath,
                ScreenshotUrl = rom.SSMetadata.ScreenshotUrl,
            },
            MergedScreenshots = rom.MergedScreenshots?.ToList(),
        };
    }
}
