using Newtonsoft.Json.Linq;
using Playnite.SDK;
using SharpCompress.Archives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace RomM.Games
{
    /// <summary>How a game runs in its emulator, as that emulator's own list rates it.</summary>
    public sealed class CompatReport
    {
        public string Emulator;
        /// <summary>The emulator's own rating word ("Ingame", "Playable", "GraphicalAudioIssues"...).</summary>
        public string Rating;
        /// <summary>The section's headline, in plain words.</summary>
        public string Headline;
        /// <summary>0 good, 1 issues, 2 does not get far; -1 not rated.</summary>
        public int Tone = -1;
        public List<string> Notes = new List<string>();
        /// <summary>Fixes the emulator applies to the game by itself (PCSX2's game database).</summary>
        public List<string> AutoFixes = new List<string>();
        /// <summary>What the game was matched as: its serial or title id.</summary>
        public string MatchedAs;
        public string Source;
        public string Url;
    }

    /// <summary>
    /// Compatibility lists other than Xenia's (XeniaCompat), for the library's Emulation section:
    ///
    ///   PlayStation (DuckStation): its game database (data/resources/gamedb.yaml), which rates
    ///   a game NoIssues / GraphicalAudioIssues / CrashesInGame / CrashesInIntro / DoesntBoot, with
    ///   a comment;
    ///   PlayStation 2 (PCSX2): its game database (bin/resources/GameIndex.yaml): compat 1-6
    ///   (Nothing, Intro, Menus, In-game, Playable, Perfect) and the fixes PCSX2 itself applies to
    ///   the game, each commented with what it fixes;
    ///   PlayStation 3 (RPCS3): rpcs3.net's compatibility list, the export RPCS3 itself downloads
    ///   (Playable, Ingame, Intro, Loadable, Nothing), by the game's serial;
    ///   PlayStation Vita (Vita3K): the list Vita3K downloads (Vita3K/compatibility's
    ///   app_compat_db.xml: Playable, Ingame +/-, Menu, Intro, Bootable, Nothing), by title id.
    ///
    /// The emulator's own copy is read when it is installed (DuckStation and PCSX2 ship theirs;
    /// RPCS3 keeps the one it downloads), else a copy fetched here and kept for a day (RPCS3,
    /// Vita3K) or a week (the game databases). Games are found by serial or title id where the
    /// file name or the game's own PARAM.SFO gives one, else (PlayStation and PlayStation 2) by
    /// name. Dolphin, Cemu, PPSSPP, Azahar, Eden, Flycast and xemu publish no list a program can
    /// read, so their games show none.
    /// </summary>
    public sealed class CompatLists
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly HttpClient s_http = CreateClient();

        private readonly string _folder;
        private readonly SemaphoreSlim _one = new SemaphoreSlim(1);
        private readonly Dictionary<string, object> _loaded = new Dictionary<string, object>();

        public CompatLists(string folder)
        {
            _folder = folder;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RomMnite");
            return client;
        }

        /// <summary>Whether a platform (Playnite's specification id) has a list here.</summary>
        public static bool Covers(string spec) =>
            spec == "sony_playstation" || spec == "sony_playstation2" || spec == "sony_playstation3" || spec == "sony_vita";

        /// <param name="names">The game's name and its file names, to find its serial or title id in.</param>
        /// <param name="installed">The game's installed files (a PARAM.SFO is looked for in them), or null.</param>
        /// <param name="emulatorFolder">The emulator's folder, for its own copy of the list, or null.</param>
        public async Task<CompatReport> FindAsync(string spec, string gameName, IList<string> names, IList<string> installed, string emulatorFolder)
        {
            try
            {
                switch (spec)
                {
                    case "sony_playstation": return await DuckStation(gameName, names, emulatorFolder).ConfigureAwait(false);
                    case "sony_playstation2": return await Pcsx2(gameName, names, emulatorFolder).ConfigureAwait(false);
                    case "sony_playstation3": return await Rpcs3(names, installed, emulatorFolder).ConfigureAwait(false);
                    case "sony_vita": return await Vita3K(names, installed).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                s_log.Info($"[Compat] Could not look {gameName} up: {ex.Message}");
            }
            return null;
        }

        // ---- PlayStation: DuckStation's game database ---------------------------------------------

        private sealed class YamlGame
        {
            public string Serial;
            public string Name;
            public string NameEn;
            public string Rating;       // DuckStation: compatibility.rating; PCSX2: compat
            public string Comment;
            public List<string> Fixes = new List<string>();
        }

        private async Task<CompatReport> DuckStation(string gameName, IList<string> names, string folder)
        {
            List<YamlGame> games = await Database("duckstation", folder == null ? null : Path.Combine(folder, "resources", "gamedb.yaml"),
                "https://raw.githubusercontent.com/stenzek/duckstation/master/data/resources/gamedb.yaml", TimeSpan.FromDays(7)).ConfigureAwait(false);
            YamlGame game = Pick(games, gameName, names, @"\b([A-Z]{4})[-_ ]?(\d{3})\.?(\d{2})\b", m => $"{m.Groups[1].Value}-{m.Groups[2].Value}{m.Groups[3].Value}");
            if (game == null)
                return null;
            var report = new CompatReport { Emulator = "DuckStation", MatchedAs = game.Serial, Source = "DuckStation's game database", Rating = game.Rating };
            switch (game.Rating)
            {
                case "NoIssues": report.Headline = "No known issues in DuckStation"; report.Tone = 0; break;
                case "GraphicalAudioIssues": report.Headline = "Plays in DuckStation, with graphical or audio issues"; report.Tone = 1; break;
                case "CrashesInGame": report.Headline = "Crashes partway through in DuckStation"; report.Tone = 1; break;
                case "CrashesInIntro": report.Headline = "Crashes at the start in DuckStation"; report.Tone = 2; break;
                case "DoesntBoot": report.Headline = "Doesn't start in DuckStation"; report.Tone = 2; break;
                default: report.Headline = "Not rated in DuckStation's game database"; break;
            }
            if (!string.IsNullOrEmpty(game.Comment))
                report.Notes.Add(game.Comment);
            return report;
        }

        // ---- PlayStation 2: PCSX2's game database -------------------------------------------------

        private async Task<CompatReport> Pcsx2(string gameName, IList<string> names, string folder)
        {
            List<YamlGame> games = await Database("pcsx2", folder == null ? null : Path.Combine(folder, "resources", "GameIndex.yaml"),
                "https://raw.githubusercontent.com/PCSX2/pcsx2/master/bin/resources/GameIndex.yaml", TimeSpan.FromDays(7)).ConfigureAwait(false);
            YamlGame game = Pick(games, gameName, names, @"\b([A-Z]{4})[-_ ]?(\d{3})\.?(\d{2})\b", m => $"{m.Groups[1].Value}-{m.Groups[2].Value}{m.Groups[3].Value}");
            if (game == null)
                return null;
            var report = new CompatReport { Emulator = "PCSX2", MatchedAs = game.Serial, Source = "PCSX2's game database", Rating = game.Rating };
            switch (game.Rating)
            {
                case "6": report.Headline = "Runs perfectly in PCSX2"; report.Tone = 0; break;
                case "5": report.Headline = "Plays well in PCSX2"; report.Tone = 0; break;
                case "4": report.Headline = "Playable in PCSX2, with issues"; report.Tone = 1; break;
                case "3": report.Headline = "Gets to the menus in PCSX2, not into play"; report.Tone = 2; break;
                case "2": report.Headline = "Shows its intro in PCSX2, no further"; report.Tone = 2; break;
                case "1": report.Headline = "Doesn't run in PCSX2"; report.Tone = 2; break;
                default: report.Headline = "Not rated in PCSX2's game database"; break;
            }
            report.AutoFixes.AddRange(game.Fixes.Distinct());
            return report;
        }

        // The game in a YAML game database: by a serial in its file names, else by its name (the
        // rated entry first, of several regions under one name).
        private static YamlGame Pick(List<YamlGame> games, string gameName, IList<string> names, string serialPattern, Func<Match, string> serial)
        {
            if (games == null || games.Count == 0)
                return null;
            foreach (string name in names ?? new List<string>())
            {
                Match found = Regex.Match(name ?? "", serialPattern, RegexOptions.IgnoreCase);
                if (!found.Success)
                    continue;
                string id = serial(found).ToUpperInvariant();
                YamlGame bySerial = games.FirstOrDefault(g => string.Equals(g.Serial, id, StringComparison.OrdinalIgnoreCase));
                if (bySerial != null)
                    return bySerial;
            }
            string wanted = XeniaCompat.Normalize(gameName);
            if (wanted.Length == 0)
                return null;
            var same = games.Where(g => XeniaCompat.Normalize(g.NameEn ?? g.Name) == wanted || XeniaCompat.Normalize(g.Name) == wanted).ToList();
            return same.FirstOrDefault(g => !string.IsNullOrEmpty(g.Rating) && g.Rating != "0" && g.Rating != "Unknown") ?? same.FirstOrDefault();
        }

        // A game database: the emulator's own copy when it has one, else one fetched and kept.
        private async Task<List<YamlGame>> Database(string key, string local, string url, TimeSpan keep)
        {
            await _one.WaitAsync().ConfigureAwait(false);
            try
            {
                string kept = Path.Combine(_folder, key + ".yaml");
                string file = local != null && File.Exists(local) ? local : kept;
                string cacheKey = key + "|" + file;
                if (_loaded.TryGetValue(cacheKey, out object done))
                    return (List<YamlGame>)done;
                if (file == kept && Older(kept, keep))
                    await Fetch(url, kept).ConfigureAwait(false);
                if (!File.Exists(file))
                    return null;
                List<YamlGame> games = ParseYaml(file);
                _loaded[cacheKey] = games;
                return games;
            }
            finally
            {
                _one.Release();
            }
        }

        // The few fields read from either database, line by line (both keep one game per top-level
        // key, two-space indents): its name, rating and comment, and PCSX2's fixes' comments.
        private static List<YamlGame> ParseYaml(string file)
        {
            var games = new List<YamlGame>();
            YamlGame game = null;
            bool inCompat = false;
            foreach (string raw in File.ReadLines(file))
            {
                if (raw.Length == 0 || raw[0] == '#')
                    continue;
                if (raw[0] != ' ')
                {
                    Match head = Regex.Match(raw, @"^([A-Za-z0-9_.-]+):\s*$");
                    game = head.Success ? new YamlGame { Serial = head.Groups[1].Value } : null;
                    if (game != null)
                        games.Add(game);
                    inCompat = false;
                    continue;
                }
                if (game == null)
                    continue;
                string line = raw.TrimEnd();
                if (line.StartsWith("  ") && !line.StartsWith("   "))
                {
                    inCompat = line.Trim() == "compatibility:";
                    if (TryValue(line, "name", out string name))
                        game.Name = name;
                    else if (TryValue(line, "name-en", out string nameEn))
                        game.NameEn = nameEn;
                    else if (TryValue(line, "compat", out string compat))
                        game.Rating = compat;
                }
                else if (inCompat)
                {
                    if (TryValue(line, "rating", out string rating))
                        game.Rating = rating;
                    else if (TryValue(line, "comments", out string comment))
                        game.Comment = comment;
                }
                else
                {
                    // PCSX2: "    eeClampMode: 2 # Fixes game freezes during ..." -- what it is for.
                    Match fix = Regex.Match(line, @"^\s{4}[A-Za-z]+:\s*[^#]*#\s*(.+)$");
                    if (fix.Success && Regex.IsMatch(fix.Groups[1].Value, @"\b(fix|fixes|helps|reduces|improves|prevents|corrects|removes|stops)\b", RegexOptions.IgnoreCase))
                        game.Fixes.Add(fix.Groups[1].Value.Trim().TrimEnd('.') + ".");
                }
            }
            return games;
        }

        private static bool TryValue(string line, string key, out string value)
        {
            Match match = Regex.Match(line, @"^\s*" + Regex.Escape(key) + @":\s*(.*)$");
            value = null;
            if (!match.Success)
                return false;
            string text = match.Groups[1].Value.Trim();
            int hash = text.StartsWith("\"") ? -1 : text.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0)
                text = text.Substring(0, hash).Trim();
            if (text.Length >= 2 && text[0] == '"' && text.LastIndexOf('"') > 0)
                text = text.Substring(1, text.LastIndexOf('"') - 1).Replace("\\\"", "\"");
            value = text;
            return true;
        }

        // ---- PlayStation 3: rpcs3.net -------------------------------------------------------------

        private async Task<CompatReport> Rpcs3(IList<string> names, IList<string> installed, string folder)
        {
            string serial = SerialIn(names, @"\b([A-Z]{4}\d{5})\b") ?? SfoTitleId(installed, "PARAM.SFO");
            if (serial == null)
                return Unidentified("RPCS3", installed, "PARAM.SFO");
            JObject results = await Rpcs3List(folder).ConfigureAwait(false);
            var report = new CompatReport { Emulator = "RPCS3", MatchedAs = serial, Source = "RPCS3's compatibility list", Url = "https://rpcs3.net/compatibility?g=" + serial };
            string status = (string)results?[serial]?["status"];
            report.Rating = status;
            switch (status)
            {
                case "Playable": report.Headline = "Plays well in RPCS3"; report.Tone = 0; break;
                case "Ingame": report.Headline = "Gets in-game in RPCS3, with serious issues"; report.Tone = 1; break;
                case "Intro": report.Headline = "Shows its intro in RPCS3, but doesn't get past the menus"; report.Tone = 2; break;
                case "Loadable": report.Headline = "Starts in RPCS3, but shows a black screen"; report.Tone = 2; break;
                case "Nothing": report.Headline = "Doesn't run in RPCS3"; report.Tone = 2; break;
                default: report.Headline = "Not on RPCS3's compatibility list yet"; report.Url = null; break;
            }
            string date = (string)results?[serial]?["date"];
            if (!string.IsNullOrEmpty(date) && report.Tone >= 0)
                report.Notes.Add("Last tested " + date + ".");
            return report;
        }

        private async Task<JObject> Rpcs3List(string folder)
        {
            await _one.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_loaded.TryGetValue("rpcs3", out object done))
                    return (JObject)done;
                string kept = Path.Combine(_folder, "rpcs3.json");
                string local = folder == null ? null : Path.Combine(folder, "GuiConfigs", "compat_database.dat");
                string file = local != null && File.Exists(local) && !Older(local, TimeSpan.FromDays(7)) ? local : kept;
                if (file == kept && Older(kept, TimeSpan.FromDays(1)))
                    await Fetch("https://rpcs3.net/compatibility?api=v1&export", kept).ConfigureAwait(false);
                if (!File.Exists(file))
                    return null;
                JObject results = JObject.Parse(File.ReadAllText(file))["results"] as JObject;
                _loaded["rpcs3"] = results;
                return results;
            }
            finally
            {
                _one.Release();
            }
        }

        // ---- PlayStation Vita: Vita3K's list ------------------------------------------------------

        // Vita3K's status labels (GitHub label ids), as Vita3K itself reads them (compat.cpp).
        private static readonly Dictionary<uint, Tuple<string, string, int>> VitaStates = new Dictionary<uint, Tuple<string, string, int>>
        {
            [920344019] = Tuple.Create("Playable", "Plays well in Vita3K", 0),
            [1260231985] = Tuple.Create("Ingame +", "Playable in Vita3K, with minor issues", 1),
            [1344752299] = Tuple.Create("Ingame -", "Gets in-game in Vita3K, with serious issues", 1),
            [1344751053] = Tuple.Create("Menu", "Gets to the menus in Vita3K, not into play", 2),
            [1260231381] = Tuple.Create("Intro", "Shows its intro in Vita3K, no further", 2),
            [1344750319] = Tuple.Create("Bootable", "Starts in Vita3K, but no further", 2),
            [1260231569] = Tuple.Create("Nothing", "Doesn't run in Vita3K", 2),
        };

        private async Task<CompatReport> Vita3K(IList<string> names, IList<string> installed)
        {
            string id = SerialIn(names, @"\b([A-Z]{4}\d{5})\b") ?? SfoTitleId(installed, "sce_sys/param.sfo");
            if (id == null)
                return Unidentified("Vita3K", installed, "sce_sys/param.sfo");
            XDocument list = await VitaList().ConfigureAwait(false);
            XElement app = list?.Root?.Elements("app").FirstOrDefault(a => string.Equals((string)a.Attribute("title_id"), id, StringComparison.OrdinalIgnoreCase));
            var report = new CompatReport { Emulator = "Vita3K", MatchedAs = id, Source = "Vita3K's compatibility list" };
            Tuple<string, string, int> state = app?.Element("labels")?.Elements("label")
                .Select(l => uint.TryParse(l.Value, out uint v) && VitaStates.TryGetValue(v, out var s) ? s : null)
                .FirstOrDefault(s => s != null);
            if (state == null)
            {
                report.Headline = "Not on Vita3K's compatibility list yet";
                return report;
            }
            report.Rating = state.Item1;
            report.Headline = state.Item2;
            report.Tone = state.Item3;
            string issue = (string)app.Element("issue_id");
            if (!string.IsNullOrEmpty(issue))
                report.Url = "https://github.com/Vita3K/compatibility/issues/" + issue;
            return report;
        }

        private async Task<XDocument> VitaList()
        {
            await _one.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_loaded.TryGetValue("vita3k", out object done))
                    return (XDocument)done;
                string kept = Path.Combine(_folder, "vita3k.xml");
                if (Older(kept, TimeSpan.FromDays(1)))
                    await Fetch("https://github.com/Vita3K/compatibility/releases/download/compat_db/app_compat_db.xml", kept).ConfigureAwait(false);
                if (!File.Exists(kept))
                    return null;
                XDocument list = XDocument.Load(kept);
                _loaded["vita3k"] = list;
                return list;
            }
            finally
            {
                _one.Release();
            }
        }

        // A game whose list is by serial, with no serial in its file names: read from its own
        // files (PARAM.SFO) once it is installed.
        private static CompatReport Unidentified(string emulator, IList<string> installed, string file)
        {
            bool has = installed != null && installed.Count > 0;
            var report = new CompatReport
            {
                Emulator = emulator,
                Source = emulator + "'s compatibility list",
                Headline = has
                    ? $"Could not tell which game this is for {emulator}'s compatibility list"
                    : $"Install the game to check {emulator}'s compatibility list",
            };
            report.Notes.Add(has
                ? $"Its files have no {file} with a title id."
                : $"{emulator}'s list goes by the game's serial, which is read from its installed files.");
            return report;
        }

        // ---- Serials -----------------------------------------------------------------------------

        private static string SerialIn(IList<string> names, string pattern)
        {
            foreach (string name in names ?? new List<string>())
            {
                Match found = Regex.Match(Path.GetFileName(name ?? ""), pattern, RegexOptions.IgnoreCase);
                if (found.Success)
                    return found.Groups[1].Value.ToUpperInvariant();
            }
            return null;
        }

        /// <summary>
        /// The TITLE_ID in a game's PARAM.SFO: found under an installed folder (a PS3 game's
        /// PS3_GAME\PARAM.SFO), or inside an archive (a Vita .vpk's sce_sys/param.sfo).
        /// </summary>
        private static string SfoTitleId(IList<string> installed, string inside)
        {
            foreach (string path in installed ?? new List<string>())
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        string sfo = Directory.EnumerateFiles(path, Path.GetFileName(inside), SearchOption.AllDirectories).FirstOrDefault();
                        if (sfo != null)
                            return TitleIdOf(File.ReadAllBytes(sfo));
                    }
                    else if (File.Exists(path) && Regex.IsMatch(path, @"\.(vpk|zip)$", RegexOptions.IgnoreCase))
                    {
                        using (var archive = ArchiveFactory.Open(path))
                        {
                            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Key.Replace('\\', '/'), inside, StringComparison.OrdinalIgnoreCase));
                            if (entry == null)
                                continue;
                            using (var stream = entry.OpenEntryStream())
                            using (var memory = new MemoryStream())
                            {
                                stream.CopyTo(memory);
                                return TitleIdOf(memory.ToArray());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Compat] No title id in {path}: {ex.Message}");
                }
            }
            return null;
        }

        // PARAM.SFO: "\0PSF", the key and data tables' offsets, the entry count, then 16-byte
        // entries (key offset, format, length, room, data offset).
        internal static string TitleIdOf(byte[] sfo)
        {
            if (sfo == null || sfo.Length < 20 || sfo[0] != 0 || sfo[1] != 'P' || sfo[2] != 'S' || sfo[3] != 'F')
                return null;
            int keys = BitConverter.ToInt32(sfo, 8), data = BitConverter.ToInt32(sfo, 12), count = BitConverter.ToInt32(sfo, 16);
            for (int i = 0; i < count && 20 + i * 16 + 16 <= sfo.Length; i++)
            {
                int at = 20 + i * 16;
                int keyOffset = BitConverter.ToUInt16(sfo, at);
                int length = BitConverter.ToInt32(sfo, at + 4);
                int dataOffset = BitConverter.ToInt32(sfo, at + 12);
                int keyStart = keys + keyOffset;
                int keyEnd = Array.IndexOf(sfo, (byte)0, keyStart);
                if (keyStart < 0 || keyEnd < 0)
                    continue;
                if (Encoding.ASCII.GetString(sfo, keyStart, keyEnd - keyStart) != "TITLE_ID")
                    continue;
                int start = data + dataOffset;
                if (start < 0 || start + length > sfo.Length)
                    return null;
                string id = Encoding.ASCII.GetString(sfo, start, length).TrimEnd('\0').Trim();
                return Regex.IsMatch(id, @"^[A-Z]{4}\d{5}$") ? id : null;
            }
            return null;
        }

        // ---- Files -------------------------------------------------------------------------------

        private static bool Older(string file, TimeSpan age) => !File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > age;

        private static async Task Fetch(string url, string target)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                byte[] bytes = await s_http.GetByteArrayAsync(url).ConfigureAwait(false);
                File.WriteAllBytes(target + ".new", bytes);
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(target + ".new", target);
                s_log.Info($"[Compat] Fetched {Path.GetFileName(target)}.");
            }
            catch (Exception ex)
            {
                s_log.Info($"[Compat] Could not fetch {url}: {ex.Message}");
            }
        }
    }
}
