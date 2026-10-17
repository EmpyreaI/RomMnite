using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>A game's standing in its emulator's list, as the Store shows it: the verdict only.</summary>
    public sealed class StoreCompat
    {
        public string Emulator;
        public string Headline;
        /// <summary>0 plays well, 1 issues, 2 does not get far (never unrated: those are not shown).</summary>
        public int Tone;
        public List<string> Notes = new List<string>();
        /// <summary>Fixes exist (community patches, or ones the emulator applies itself): said, not listed.</summary>
        public string FixesNote;
        public string Source;
        public string Url;
        public string MatchedAs;
    }

    /// <summary>
    /// The Store's view of the emulators' compatibility lists.
    ///
    /// For one game (<see cref="ForRomAsync"/>): its verdict, only when the lists settle it from
    /// what the server knows of the game (its name and file names). A game the list has not
    /// rated, or one that could only be found from its installed files (PlayStation 3 and Vita
    /// without a serial in the file name), gives nothing.
    ///
    /// Over time (<see cref="ScanSoon"/>, at most daily): each game's verdict on the covered
    /// platforms is kept, so a game that moves up to the list's top rating ("plays well") from a
    /// lower one is known as upgraded, and featured in the Store for a week
    /// (<see cref="RecentUpgrades"/>). The first look at a game sets where it stands; only a later
    /// change counts, so nothing is featured on the first scan.
    /// </summary>
    public sealed class CompatWatch
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly TimeSpan ScanEvery = TimeSpan.FromDays(1);
        private const int PerPlatformCap = 5000;

        private readonly RomM _plugin;
        private readonly string _path;
        private readonly object _lock = new object();
        private Dictionary<int, Entry> _entries;
        private int _scanning;

        private sealed class Entry
        {
            public int Tone { get; set; }
            public string Rating { get; set; }
            public string Emulator { get; set; }
            public DateTime SeenUtc { get; set; }
            public DateTime? UpgradedUtc { get; set; }
        }

        private sealed class State
        {
            public DateTime LastScanUtc { get; set; }
            public Dictionary<int, Entry> Games { get; set; } = new Dictionary<int, Entry>();
        }

        private DateTime _lastScanUtc;

        public CompatWatch(RomM plugin, string folder)
        {
            _plugin = plugin;
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, "history.json");
        }

        /// <summary>A game recently moved up to "plays well".</summary>
        public sealed class Upgrade
        {
            public int RomId;
            public string Emulator;
            public DateTime When;
        }

        // RomM's platform names for the platforms with lists, as Playnite's specification ids.
        public static string SpecOfSlug(string slug)
        {
            switch ((slug ?? "").ToLowerInvariant())
            {
                case "xbox360": return "xbox360";
                case "psx": case "ps": case "ps1": return "sony_playstation";
                case "ps2": return "sony_playstation2";
                case "ps3": return "sony_playstation3";
                case "psvita": case "vita": return "sony_vita";
                default: return null;
            }
        }

        /// <summary>
        /// The game's verdict for the Store, or null when the lists don't settle it. Off the UI
        /// thread. Noted for the upgrade watch too.
        /// </summary>
        public async Task<StoreCompat> ForRomAsync(int romId, int platformId, string slug, string name, IList<string> names)
        {
            string spec = SpecOfSlug(slug);
            if (spec == null)
                return null;
            StoreCompat found = null;
            try
            {
                if (spec == "xbox360")
                {
                    XeniaReport report = await _plugin.XeniaCompat.FindAsync(name).ConfigureAwait(false);
                    found = FromXenia(report);
                }
                else
                {
                    // installed: null. What only installed files could tell is not guessed at.
                    CompatReport report = await _plugin.CompatLists.FindAsync(spec, name, names, null, EmulatorFolder(platformId)).ConfigureAwait(false);
                    found = FromList(report);
                }
            }
            catch (Exception ex)
            {
                s_log.Info($"[Compat] Could not look {name} up for the Store: {ex.Message}");
                return null;
            }
            if (found != null)
                Note(romId, found);
            return found;
        }

        private static StoreCompat FromXenia(XeniaReport report)
        {
            if (report == null)
                return null;
            var compat = new StoreCompat { Emulator = "Xenia", Source = "Xenia's compatibility list", Url = report.Url, MatchedAs = report.TitleId };
            switch (report.State)
            {
                case "Playable": compat.Headline = "Plays well in Xenia"; compat.Tone = 0; break;
                case "Gameplay": compat.Headline = "Playable in Xenia, with some issues"; compat.Tone = 1; break;
                case "Loads": compat.Headline = "Starts in Xenia, but doesn't get into play yet"; compat.Tone = 2; break;
                case "Unplayable": compat.Headline = "Doesn't run in Xenia yet"; compat.Tone = 2; break;
                default: return null;
            }
            compat.Notes.AddRange(EmulationStatusControl.Issues(report));
            if (report.Patches.Any(p => p.IsFix))
                compat.FixesNote = "Community fixes are available; they can be switched on from the game's page once it's in your library.";
            else if (report.Patches.Count > 0)
                compat.FixesNote = "Community patches are available from the game's page once it's in your library.";
            return compat;
        }

        private static StoreCompat FromList(CompatReport report)
        {
            if (report == null || report.Tone < 0 || string.IsNullOrEmpty(report.MatchedAs))
                return null;
            var compat = new StoreCompat
            {
                Emulator = report.Emulator,
                Headline = report.Headline,
                Tone = report.Tone,
                Source = report.Source,
                Url = report.Url,
                MatchedAs = report.MatchedAs,
            };
            compat.Notes.AddRange(report.Notes);
            if (report.AutoFixes.Count > 0)
                compat.FixesNote = $"{report.Emulator} applies {report.AutoFixes.Count} fix{(report.AutoFixes.Count == 1 ? "" : "es")} for this game by itself.";
            return compat;
        }

        private string EmulatorFolder(int platformId)
        {
            try
            {
                var mapping = _plugin.MappingForPlatform(platformId);
                string folder = PlaynitePath.Resolve(_plugin.Playnite, mapping?.Emulator?.InstallDir);
                return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
            }
            catch
            {
                return null;
            }
        }

        // ---- The upgrade watch ------------------------------------------------------------------

        private void Load()
        {
            if (_entries != null)
                return;
            try
            {
                State state = File.Exists(_path) ? JsonConvert.DeserializeObject<State>(File.ReadAllText(_path)) : null;
                _entries = state?.Games ?? new Dictionary<int, Entry>();
                _lastScanUtc = state?.LastScanUtc ?? DateTime.MinValue;
            }
            catch (Exception ex)
            {
                s_log.Info($"[Compat] Could not read the compatibility history: {ex.Message}");
                _entries = new Dictionary<int, Entry>();
                _lastScanUtc = DateTime.MinValue;
            }
        }

        private void Save()
        {
            try
            {
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(new State { LastScanUtc = _lastScanUtc, Games = _entries }));
                if (File.Exists(_path))
                    File.Delete(_path);
                File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                s_log.Info($"[Compat] Could not save the compatibility history: {ex.Message}");
            }
        }

        // Where the game stands now. Moving up to "plays well" from a known lower rating is an
        // upgrade; dropping below it again ends one.
        private void Note(int romId, StoreCompat compat, bool save = true)
        {
            lock (_lock)
            {
                Load();
                DateTime now = DateTime.UtcNow;
                if (_entries.TryGetValue(romId, out Entry before))
                {
                    if (compat.Tone == 0 && before.Tone > 0)
                    {
                        before.UpgradedUtc = now;
                        s_log.Info($"[Compat] ROM {romId} now plays well in {compat.Emulator} (was {before.Rating ?? before.Tone.ToString()}).");
                    }
                    else if (compat.Tone > 0)
                        before.UpgradedUtc = null;
                    before.Tone = compat.Tone;
                    before.Rating = compat.Headline;
                    before.Emulator = compat.Emulator;
                    before.SeenUtc = now;
                }
                else
                {
                    _entries[romId] = new Entry { Tone = compat.Tone, Rating = compat.Headline, Emulator = compat.Emulator, SeenUtc = now };
                }
                if (save)
                    Save();
            }
        }

        /// <summary>Games moved up to "plays well" within <paramref name="within"/>, newest first.</summary>
        public List<Upgrade> RecentUpgrades(TimeSpan within)
        {
            lock (_lock)
            {
                Load();
                DateTime since = DateTime.UtcNow - within;
                return _entries.Where(e => e.Value.UpgradedUtc.HasValue && e.Value.UpgradedUtc.Value > since && e.Value.Tone == 0)
                    .OrderByDescending(e => e.Value.UpgradedUtc.Value)
                    .Select(e => new Upgrade { RomId = e.Key, Emulator = e.Value.Emulator, When = e.Value.UpgradedUtc.Value })
                    .ToList();
            }
        }

        /// <summary>
        /// Every game on the server's covered platforms looked up again, in the background, when
        /// the last look is a day old. <paramref name="get"/> asks the server (the Store's own
        /// requests, signed in as the account).
        /// </summary>
        public void ScanSoon(Func<string, JToken> get)
        {
            lock (_lock)
            {
                Load();
                if (DateTime.UtcNow - _lastScanUtc < ScanEvery)
                    return;
            }
            if (Interlocked.Exchange(ref _scanning, 1) == 1)
                return;
            Task.Run(async () =>
            {
                try
                {
                    await Scan(get).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Compat] The compatibility scan stopped: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _scanning, 0);
                }
            });
        }

        private async Task Scan(Func<string, JToken> get)
        {
            JArray platforms = get("/api/platforms") as JArray;
            if (platforms == null)
                return;
            int seen = 0;
            foreach (JToken platform in platforms)
            {
                string slug = (string)platform["slug"];
                int? id = (int?)platform["id"];
                if (SpecOfSlug(slug) == null || id == null)
                    continue;
                const int page = 250;
                for (int offset = 0; offset < PerPlatformCap; offset += page)
                {
                    JToken answer = get($"/api/roms?limit={page}&offset={offset}&platform_ids={id}");
                    var items = answer?["items"] as JArray;
                    if (items == null || items.Count == 0)
                        break;
                    foreach (JToken rom in items)
                    {
                        int? romId = (int?)rom["id"];
                        if (romId == null)
                            continue;
                        string name = (string)rom["name"] ?? (string)rom["fs_name_no_ext"];
                        var names = new List<string> { (string)rom["fs_name"], (string)rom["fs_name_no_ext"] }.Where(n => !string.IsNullOrEmpty(n)).ToList();
                        StoreCompat compat = await Look(slug, id.Value, name, names).ConfigureAwait(false);
                        if (compat != null)
                        {
                            Note(romId.Value, compat, save: false);
                            seen++;
                        }
                    }
                    if (items.Count < page)
                        break;
                }
            }
            lock (_lock)
            {
                _lastScanUtc = DateTime.UtcNow;
                Save();
            }
            s_log.Info($"[Compat] Compatibility scan done: {seen} rated games on the server.");
        }

        // As ForRomAsync, without noting (the scan saves once, at the end).
        private async Task<StoreCompat> Look(string slug, int platformId, string name, IList<string> names)
        {
            string spec = SpecOfSlug(slug);
            try
            {
                if (spec == "xbox360")
                    return FromXenia(await _plugin.XeniaCompat.FindAsync(name).ConfigureAwait(false));
                return FromList(await _plugin.CompatLists.FindAsync(spec, name, names, null, EmulatorFolder(platformId)).ConfigureAwait(false));
            }
            catch
            {
                return null;
            }
        }
    }
}
