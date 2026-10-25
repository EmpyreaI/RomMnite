using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.AchievementViews;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace RomM.Games
{
    /// <summary>
    /// Achievements RetroAchievements has no sets for, read from the emulators themselves:
    /// RPCS3's trophies and Xenia Canary's achievements. Each account keeps its own record of
    /// what it unlocked, here and as a private RomM note on the game ("Playnite Achievements"),
    /// so it follows the account to other computers; RomM itself has nowhere to keep
    /// achievements from other sources.
    ///
    /// The emulators keep one unlock state per emulator user, whoever plays, so what an account
    /// unlocked is told apart by time: unlocks timed within its own play sessions are its own.
    /// (In the emulator, a trophy another account already unlocked shows as unlocked and cannot
    /// be earned again; only per-account emulator users would avoid that.)
    ///
    /// Where a game's set is, is learned from what the emulator wrote during a session (and, for
    /// Xenia, from the save folders learned for the game).
    ///
    /// Windows games too, whose copy keeps its achievements through a Steam emulator in place of
    /// Steam: read from the emulator's files (EmulatorAchievements.SteamEmu, SteamEmuFiles).
    ///
    ///   RPCS3   dev_hdd0\home\&lt;user&gt;\trophy\&lt;NPWR...&gt;\ TROPCONF.SFM (the list), TROPUSR.DAT
    ///           (unlocks and times), TROP###.PNG (icons)
    ///   Xenia   content\&lt;xuid&gt;\FFFE07D1\00010000\&lt;xuid&gt;\&lt;title id&gt;.gpd (an XDBF file: the
    ///           achievements, their images, unlock flags and times)
    /// </summary>
    public sealed partial class EmulatorAchievements
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        public const string NoteTitle = "Playnite Achievements";
        private const string FormatTag = "playnite-achievements-1";
        private static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

        public sealed class Definition
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public string LockedDescription { get; set; }
            public int Points { get; set; }
            public bool Hidden { get; set; }
            public int Order { get; set; }
        }

        /// <summary>An account's record for one game; also the content of its RomM note.</summary>
        public sealed class Record
        {
            public string Format { get; set; } = FormatTag;
            public string Kind { get; set; }      // "rpcs3" or "xenia"
            public string SetId { get; set; }     // NPWR... or the title id
            public List<Definition> Definitions { get; set; } = new List<Definition>();
            public Dictionary<string, DateTime> Unlocked { get; set; } = new Dictionary<string, DateTime>();
            public DateTime UpdatedUtc { get; set; }
        }

        // A set as the emulator has it now.
        private sealed class EmulatorSet
        {
            public string Kind;
            public string SetId;
            public List<Definition> Definitions = new List<Definition>();
            public Dictionary<string, DateTime?> Unlocked = new Dictionary<string, DateTime?>();
        }

        private sealed class Source
        {
            public string Kind { get; set; }
            public string Path { get; set; }      // RPCS3: the trophy folder; Xenia: the .gpd file; a Steam emulator's: its file (last found)
            public int AppId { get; set; }        // a Steam emulator's: the game's Steam app id
            public string DllFolder { get; set; } // a Steam emulator's: the folder of its steam_api dll
        }

        private readonly RomM _plugin;
        private readonly string _dataFolder;
        private readonly string _sourcesPath;
        private readonly string _iconsFolder;
        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<Guid, DateTime> _sessionStarts = new ConcurrentDictionary<Guid, DateTime>();
        private readonly ConcurrentDictionary<Guid, DateTime> _pulled = new ConcurrentDictionary<Guid, DateTime>();
        private Dictionary<Guid, Source> _sources;
        private string _accountFolder;
        private int _me = -1;

        public EmulatorAchievements(RomM plugin, string dataFolder)
        {
            _plugin = plugin;
            _dataFolder = dataFolder;
            _sourcesPath = Path.Combine(dataFolder, "EmulatorAchievements", "Sources.json");
            _iconsFolder = Path.Combine(dataFolder, "EmulatorAchievements", "Icons");
        }

        /// <summary>The signed-in account's own records.</summary>
        public void UseAccount(string accountFolder)
        {
            _accountFolder = Path.Combine(accountFolder, "EmulatorAchievements");
            _me = -1;
            _pulled.Clear();
        }

        // ---- When games are played and looked at ------------------------------------------------

        /// <summary>The emulator kind whose achievements this reads for a game, or null.</summary>
        private string KindOf(Game game, out Emulator emulator, out string installDir)
        {
            emulator = null;
            installDir = null;
            if (game == null || game.PluginId != _plugin.Id)
                return null;
            if (Overlay.GameOverlay.IsPc(game))
                return SteamEmuGame(game, out installDir) ? SteamEmu : null;
            Saves.SaveEmulatorResolution resolution = _plugin.SaveSync.EmulatorFor(game);
            emulator = resolution?.Emulator;
            if (emulator == null)
                return null;
            installDir = PlaynitePath.Resolve(_plugin.Playnite, emulator.InstallDir);
            string id = ((emulator.BuiltInConfigId ?? "") + " " + (emulator.Name ?? "") + " " + (installDir ?? "")).ToLowerInvariant();
            if (id.Contains("rpcs3"))
                return "rpcs3";
            if (id.Contains("xenia"))
                return "xenia";
            return null;
        }

        /// <summary>
        /// Whether the game's achievements wait on its first play: its emulator keeps them itself
        /// (Xenia, RPCS3), and nothing is known of them yet (no record for the account, no set
        /// found in the emulator; RPCS3 games whose trophy package is in their files need none).
        /// The emulator's name, for saying so. Quick: read on the UI thread.
        /// </summary>
        public bool NeedsFirstPlay(Game game, out string emulatorName)
        {
            emulatorName = null;
            try
            {
                string kind = KindOf(game, out Emulator emulator, out string installDir);
                if (kind == null || kind == SteamEmu || _accountFolder == null)
                    return false;
                emulatorName = kind == "rpcs3" ? "RPCS3" : "Xenia";
                Record record = LoadRecord(_accountFolder, game.Id);
                if (record != null && record.Definitions.Count > 0)
                    return false;
                Source known = Known(game, kind, installDir);
                if (known != null && (File.Exists(known.Path) || Directory.Exists(known.Path)))
                    return false;
                if (kind == "rpcs3" && FindTrophyPackage(game, _plugin.Playnite, out string _) != null)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Just before the game starts in Xenia: the account's unlocks it is missing put into
        /// Xenia's record of the game (its title GPD), when Xenia is set to the account's own
        /// profile. Xenia keeps an achievement's picture only for those it has as unlocked
        /// (adding them as the game loads), so unlocks earned elsewhere (another computer, synced
        /// through RomM) would otherwise never get theirs, and Xenia's own list would disagree.
        /// Only the flags and time of existing entries change, in place. On the calling thread.
        /// </summary>
        public void BeforeLaunch(Game game)
        {
            try
            {
                string kind = KindOf(game, out Emulator _, out string installDir);
                if (kind != "xenia" || _accountFolder == null || !_plugin.EmulatorUsers.IsActiveFor(game))
                    return;
                Record record = LoadRecord(_accountFolder, game.Id);
                Source source = Known(game, kind, installDir);
                if (record == null || record.Unlocked.Count == 0 || source == null || !File.Exists(source.Path))
                    return;
                int written = MarkUnlockedInGpd(source.Path, record.Unlocked);
                if (written > 0)
                    s_log.Info($"[Achievements] {game.Name}: {written} of the account's unlocks put into Xenia's record, for their pictures.");
            }
            catch (Exception ex)
            {
                s_log.Info($"[Achievements] Could not bring Xenia's record of {game.Name} up to date: {ex.Message}");
            }
        }

        // Sets the unlocked flag (0x20000, as Xenia writes it) and time on the GPD's achievement
        // entries the record has unlocked; the number changed.
        private static int MarkUnlockedInGpd(string gpd, Dictionary<string, DateTime> unlocked)
        {
            byte[] data = ReadShared(gpd);
            if (data.Length < 0x18 || BE32(data, 0) != 0x58444246)
                return 0;
            uint entryCount = BE32(data, 8);
            uint entryUsed = BE32(data, 12);
            uint freeCount = BE32(data, 16);
            long dataStart = 0x18 + 18L * entryCount + 8L * freeCount;
            int changed = 0;
            for (int i = 0; i < entryUsed && i < entryCount; i++)
            {
                int at = 0x18 + i * 18;
                if (at + 18 > data.Length || BE16(data, at) != 1)
                    continue;
                long offset = dataStart + BE32(data, at + 10);
                uint size = BE32(data, at + 14);
                if (size < 0x1C || offset < 0 || offset + size > data.Length)
                    continue;
                int a = (int)offset;
                string id = BE32(data, a + 4).ToString();
                uint flags = BE32(data, a + 16);
                if ((flags & 0x30000) != 0 || !unlocked.TryGetValue(id, out DateTime when))
                    continue;
                PutBE32(data, a + 16, flags | 0x20000);
                long fileTime = when.Year > 1601 ? when.ToUniversalTime().ToFileTimeUtc() : DateTime.UtcNow.ToFileTimeUtc();
                PutBE32(data, a + 20, (uint)((ulong)fileTime >> 32));
                PutBE32(data, a + 24, (uint)((ulong)fileTime & 0xFFFFFFFF));
                changed++;
            }
            if (changed > 0)
            {
                string backup = gpd + ".rommnite.bak";
                if (!File.Exists(backup))
                    File.Copy(gpd, backup);
                File.WriteAllBytes(gpd, data);
            }
            return changed;
        }

        private static void PutBE32(byte[] b, int at, uint value)
        {
            b[at] = (byte)(value >> 24);
            b[at + 1] = (byte)(value >> 16);
            b[at + 2] = (byte)(value >> 8);
            b[at + 3] = (byte)value;
        }

        public void SessionStarted(Game game)
        {
            if (game == null)
                return;
            _sessionStarts[game.Id] = DateTime.UtcNow;
            StartLive(game, _sessionStarts[game.Id]);
        }

        // ---- Unlocks as they happen ---------------------------------------------------------------

        /// <summary>
        /// An achievement unlocked during play, seen as the emulator writes it (RPCS3 saves the
        /// trophy file when one is earned; Xenia its record of the game). On a worker thread.
        /// </summary>
        public event Action<Game, AchievementItem> UnlockedLive;

        private sealed class LiveWatch
        {
            public Game Game;
            public string Kind;
            public string InstallDir;
            public DateTime Started;
            public FileSystemWatcher Watcher;
            public System.Threading.Timer Settle;
            public readonly HashSet<string> Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly object Lock = new object();
            public bool Primed;   // a Steam emulator's: its unlocks before the session noted
        }

        private readonly ConcurrentDictionary<Guid, LiveWatch> _live = new ConcurrentDictionary<Guid, LiveWatch>();

        // Watches the emulator's trophy / achievement files for the session. What is unlocked
        // already (in the emulator and in the account's record) is not announced.
        private void StartLive(Game game, DateTime started)
        {
            StopLive(game.Id);
            try
            {
                string kind = KindOf(game, out Emulator _, out string installDir);
                if (kind == null)
                    return;
                string root = kind == SteamEmu ? null : kind == "rpcs3" ? Rpcs3TrophyRoot(installDir) : XeniaGpdFolder(installDir);
                if (kind != SteamEmu && (root == null || !Directory.Exists(root)))
                    return;
                var watch = new LiveWatch { Game = game, Kind = kind, InstallDir = installDir, Started = started };
                // A Steam emulator's: what is unlocked already is noted at the first look (CheckLive),
                // off this thread (finding its files means looking through the game's folder).
                Source known = kind == SteamEmu ? null : Known(game, kind, installDir);
                EmulatorSet now = known != null && (File.Exists(known.Path) || Directory.Exists(known.Path)) ? Read(known) : null;
                foreach (string id in now?.Unlocked.Keys ?? Enumerable.Empty<string>())
                    watch.Seen.Add(id);
                if (_accountFolder != null)
                {
                    foreach (string id in LoadRecord(_accountFolder, game.Id)?.Unlocked.Keys ?? Enumerable.Empty<string>())
                        watch.Seen.Add(id);
                }
                watch.Settle = new System.Threading.Timer(_ => CheckLive(watch), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                if (kind == SteamEmu)
                {
                    // The emulator's file may be in any of several folders, and made only at the
                    // first unlock: looked at every few seconds rather than watched.
                    watch.Settle.Change(4000, 4000);
                    _live[game.Id] = watch;
                    return;
                }
                watch.Watcher = new FileSystemWatcher(root)
                {
                    Filter = kind == "rpcs3" ? "TROPUSR.DAT" : "*.gpd",
                    IncludeSubdirectories = kind == "rpcs3",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                // Read once the emulator has finished writing (writes come in bursts).
                FileSystemEventHandler changed = (s, e) => watch.Settle.Change(1500, System.Threading.Timeout.Infinite);
                watch.Watcher.Changed += changed;
                watch.Watcher.Created += changed;
                watch.Watcher.Renamed += (s, e) => watch.Settle.Change(1500, System.Threading.Timeout.Infinite);
                watch.Watcher.EnableRaisingEvents = true;
                _live[game.Id] = watch;
            }
            catch (Exception ex)
            {
                s_log.Info($"[Achievements] Unlocks in {game.Name} won't be shown as they happen: {ex.Message}");
            }
        }

        private void StopLive(Guid gameId)
        {
            if (!_live.TryRemove(gameId, out LiveWatch watch))
                return;
            try
            {
                if (watch.Watcher != null)
                {
                    watch.Watcher.EnableRaisingEvents = false;
                    watch.Watcher.Dispose();
                }
                watch.Settle.Dispose();
            }
            catch
            {
                // Gone.
            }
        }

        private void CheckLive(LiveWatch watch)
        {
            try
            {
                lock (watch.Lock)
                {
                    Source source = Learn(watch.Game, watch.Kind, watch.InstallDir, watch.Started);
                    EmulatorSet set = source == null ? null : Read(source);
                    if (set == null)
                        return;
                    if (!watch.Primed)
                    {
                        watch.Primed = true;
                        if (watch.Kind == SteamEmu)
                        {
                            // Unlocked before the session (or with no time to tell): not announced.
                            foreach (KeyValuePair<string, DateTime?> before in set.Unlocked)
                            {
                                if (!before.Value.HasValue || before.Value.Value < watch.Started - Slack)
                                    watch.Seen.Add(before.Key);
                            }
                        }
                    }
                    string icons = Path.Combine(_iconsFolder, set.Kind + "_" + set.SetId);
                    foreach (KeyValuePair<string, DateTime?> unlock in set.Unlocked)
                    {
                        if (!watch.Seen.Add(unlock.Key))
                            continue;
                        // Unlocked in this session (not an older one only now noticed).
                        if (unlock.Value.HasValue && unlock.Value.Value < watch.Started - Slack)
                            continue;
                        Definition definition = set.Definitions.FirstOrDefault(d => d.Id == unlock.Key);
                        if (definition == null)
                            continue;
                        string icon = IconFile(icons, set.Kind, definition.Id);
                        UnlockedLive?.Invoke(watch.Game, new AchievementItem
                        {
                            Title = definition.Title,
                            Description = definition.Description,
                            Points = definition.Points,
                            BadgeUrl = File.Exists(icon) ? icon : null,
                            UnlockedUtc = unlock.Value ?? DateTime.UtcNow,
                            Order = definition.Order,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Achievements] Could not read {watch.Game.Name}'s unlocks: {ex.Message}");
            }
        }

        /// <summary>After a session: what it unlocked becomes the account's. In the background.</summary>
        public void SessionEnded(Game game)
        {
            if (game != null)
                StopLive(game.Id);
            if (game == null || !_sessionStarts.TryRemove(game.Id, out DateTime started))
                return;
            string kind = KindOf(game, out Emulator _, out string installDir);
            if (kind == null || _accountFolder == null)
                return;
            string account = _accountFolder;
            Task.Run(() =>
            {
                try
                {
                    Source source = Learn(game, kind, installDir, started);
                    EmulatorSet set = source == null ? null : Read(source);
                    if (set == null)
                    {
                        s_log.Info($"[Achievements] {game.Name}: no {kind} achievements found for this session.");
                        return;
                    }
                    Record record = LoadRecord(account, game.Id) ?? new Record();
                    bool first = record.Definitions.Count == 0 && record.Unlocked.Count == 0;
                    record.Kind = set.Kind;
                    record.SetId = set.SetId;
                    record.Definitions = set.Definitions;

                    // With the account's own user in the emulator (see Account.EmulatorUsers),
                    // everything unlocked there is the account's. Otherwise this session's unlocks
                    // are, and everything already unlocked when it is the only account here.
                    bool own = _plugin.EmulatorUsers.IsActiveFor(game);
                    bool takeAll = own || (first && _plugin.Libraries.KnownAccounts <= 1);
                    int added = 0;
                    foreach (KeyValuePair<string, DateTime?> unlock in set.Unlocked)
                    {
                        if (record.Unlocked.ContainsKey(unlock.Key))
                            continue;
                        bool inSession = unlock.Value.HasValue && unlock.Value.Value >= started - Slack && unlock.Value.Value <= DateTime.UtcNow + Slack;
                        if (inSession || takeAll)
                        {
                            record.Unlocked[unlock.Key] = unlock.Value ?? DateTime.UtcNow;
                            added++;
                        }
                    }
                    record.UpdatedUtc = DateTime.UtcNow;
                    if (account != _accountFolder)
                        return;   // signed in as someone else meanwhile
                    SaveRecord(account, game.Id, record);
                    s_log.Info($"[Achievements] {game.Name}: {added} new {kind} achievements for this account ({record.Unlocked.Count}/{record.Definitions.Count}).");
                    Show(game, record);
                    SyncNote(game, record, account);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Achievements] Could not read {game.Name}'s achievements: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// A game looked at: its achievements from the account's record, brought up to date with
        /// its note on the server now and then. In the background.
        /// </summary>
        public void Refresh(Game game)
        {
            if (game == null || _accountFolder == null)
                return;
            string kind;
            try
            {
                kind = KindOf(game, out Emulator _, out string _);
            }
            catch
            {
                return;
            }
            if (kind == null)
                return;
            bool notePull = !_pulled.TryGetValue(game.Id, out DateTime last) || DateTime.UtcNow - last >= TimeSpan.FromMinutes(15);
            if (notePull)
                _pulled[game.Id] = DateTime.UtcNow;
            string account = _accountFolder;
            Task.Run(() =>
            {
                try
                {
                    // The emulator's own files on every look (they are small); the note now and then.
                    Record record = ReadLocal(game, kind, account, LoadRecord(account, game.Id));
                    if (record != null)
                        Show(game, record);
                    if (!notePull)
                        return;
                    // A Windows game without a Steam emulator here and nothing kept: nothing to sync.
                    if (record == null && kind == SteamEmu)
                        return;
                    Record merged = SyncNote(game, record, account);
                    if (merged != null && account == _accountFolder)
                        Show(game, merged);
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Achievements] Could not bring {game.Name}'s achievements up to date: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// The game's set as the emulator has it now, folded into the account's record: the list
        /// (for RPCS3 from the game's own trophy package when it has not booted in this user
        /// yet), and, when the emulator is set to the account's own user, everything unlocked
        /// there. The record, saved when it changed.
        /// </summary>
        private Record ReadLocal(Game game, string kind, string account, Record record)
        {
            KindOf(game, out Emulator _, out string installDir);
            // A Steam emulator keeps one record for the computer: everything in it is the
            // account's when it is the only one here.
            bool own = kind == SteamEmu ? _plugin.Libraries.KnownAccounts <= 1 : _plugin.EmulatorUsers.IsActiveFor(game);

            // A Windows game: its emulator's files where it has one; else Steam's list alone (and,
            // installed, said to be unable to earn them: its copy was looked through and has none).
            Source source;
            if (kind == SteamEmu)
            {
                source = LearnSteamEmu(game, installDir);
                bool cannot = source == null && !string.IsNullOrEmpty(installDir) && Directory.Exists(installDir);
                _cannotEarnHere[game.Id] = cannot;
                source = source ?? SteamListSource(game);
            }
            else
                source = Known(game, kind, installDir);
            string trp = null;
            string setId = null;
            if (kind == "rpcs3")
            {
                trp = FindTrophyPackage(game, _plugin.Playnite, out setId);
                if (source == null && setId != null)
                {
                    // Matched from the game's own files: where RPCS3 puts the set once it boots.
                    string trophies = Rpcs3TrophyRoot(installDir);
                    if (trophies != null)
                    {
                        source = new Source { Kind = kind, Path = Path.Combine(trophies, setId) };
                        Remember(game.Id, source);
                    }
                }
            }

            EmulatorSet set = source != null && (source.Kind == SteamEmu || File.Exists(source.Path) || Directory.Exists(source.Path)) ? Read(source) : null;
            if (set == null && trp != null)
                set = ReadTrophyPackage(trp, setId);
            if (set == null)
                return record;

            bool changed = record == null;
            record = record ?? new Record();
            if (record.Definitions.Count != set.Definitions.Count || record.SetId != set.SetId)
            {
                record.Kind = set.Kind;
                record.SetId = set.SetId;
                record.Definitions = set.Definitions;
                changed = true;
            }
            if (own)
            {
                foreach (KeyValuePair<string, DateTime?> unlock in set.Unlocked)
                {
                    if (!record.Unlocked.ContainsKey(unlock.Key))
                    {
                        record.Unlocked[unlock.Key] = unlock.Value ?? DateTime.UtcNow;
                        changed = true;
                    }
                }
            }
            if (changed && account == _accountFolder)
            {
                record.UpdatedUtc = DateTime.UtcNow;
                SaveRecord(account, game.Id, record);
            }
            return record;
        }

        // The set learned for the game before, under the user in use now (each account has its own).
        private Source Known(Game game, string kind, string installDir)
        {
            Source known;
            lock (_lock)
                known = Sources.TryGetValue(game.Id, out Source kept) && kept.Kind == kind ? kept : null;
            if (known == null || kind == SteamEmu)
                return known;
            string root = kind == "rpcs3" ? Rpcs3TrophyRoot(installDir) : XeniaGpdFolder(installDir);
            return root == null ? known : new Source { Kind = kind, Path = Path.Combine(root, Path.GetFileName(known.Path.TrimEnd('\\', '/'))) };
        }

        // The record, as the plugin's achievement views show it.
        private void Show(Game game, Record record)
        {
            if (record == null || record.Definitions.Count == 0)
                return;
            string icons = Path.Combine(_iconsFolder, record.Kind + "_" + record.SetId);
            bool trophies = record.Kind == "rpcs3";
            var items = new List<AchievementItem>();
            foreach (Definition definition in record.Definitions.OrderBy(d => d.Order))
            {
                bool unlocked = record.Unlocked.TryGetValue(definition.Id, out DateTime when);
                bool hidden = definition.Hidden && !unlocked;
                string icon = IconFile(icons, record.Kind, definition.Id);
                string gray = IconFile(icons, record.Kind, definition.Id, gray: true);
                items.Add(new AchievementItem
                {
                    Title = hidden ? (trophies ? "Hidden trophy" : "Secret achievement") : definition.Title,
                    Description = hidden ? "Keep playing to reveal it." : unlocked ? definition.Description : (definition.LockedDescription ?? definition.Description),
                    Points = definition.Points,
                    BadgeUrl = !hidden && File.Exists(icon) ? icon : null,
                    BadgeLockedUrl = !hidden && File.Exists(gray) ? gray : null,
                    UnlockedUtc = unlocked ? when : (DateTime?)null,
                    Order = definition.Order,
                    Hidden = hidden,
                });
            }
            _plugin.AchievementStore.Save(game.Id, new GameAchievements
            {
                Source = record.Kind,
                UpdatedUtc = DateTime.UtcNow,
                Items = items,
                Unavailable = record.Kind == SteamEmu && _cannotEarnHere.TryGetValue(game.Id, out bool cannot) && cannot
                    ? CannotEarnHere(game.Name) : null,
            });
        }

        // ---- The account's records --------------------------------------------------------------

        private static string RecordPath(string account, Guid gameId) => Path.Combine(account, gameId + ".json");

        private Record LoadRecord(string account, Guid gameId)
        {
            try
            {
                string path = RecordPath(account, gameId);
                return File.Exists(path) ? JsonConvert.DeserializeObject<Record>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Achievements] Could not read the account's achievements of {gameId}: {ex.Message}");
                return null;
            }
        }

        private void SaveRecord(string account, Guid gameId, Record record)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(account);
                File.WriteAllText(RecordPath(account, gameId), JsonConvert.SerializeObject(record, Formatting.Indented));
            }
        }

        // ---- The note on the server ---------------------------------------------------------------

        /// <summary>
        /// Merges the record with the account's note on the server (unlocks from both, the earlier
        /// time kept) and writes back whichever side lacked something. The merged record, or null
        /// when the server could not be asked.
        /// </summary>
        private Record SyncNote(Game game, Record local, string account)
        {
            string host = _plugin.Settings?.RomMHost?.TrimEnd('/');
            if (string.IsNullOrEmpty(host) || !RomMGameId.TryParse(game.GameId, out int romId, out string _))
                return null;
            string kind = local?.Kind;
            if (kind == null)
            {
                try
                {
                    kind = KindOf(game, out Emulator _, out string _);
                }
                catch
                {
                    // Unknown: described as achievements.
                }
            }
            AchievementSyncStatus status = _plugin.AchievementSync;
            status.Begin(game.Id);
            bool ended = false;
            try
            {
                int me = Me(host);
                JObject note = null;
                foreach (JToken candidate in GetJson($"{host}/api/roms/{romId}/notes") as JArray ?? new JArray())
                {
                    if ((int?)candidate["user_id"] == me && string.Equals((string)candidate["title"], NoteTitle, StringComparison.Ordinal))
                    {
                        note = candidate as JObject;
                        break;
                    }
                }

                Record remote = null;
                if (note != null)
                {
                    try
                    {
                        remote = JsonConvert.DeserializeObject<Record>((string)note["content"] ?? "");
                    }
                    catch (JsonException)
                    {
                        s_log.Warn($"[Achievements] {game.Name}: the \"{NoteTitle}\" note on the server is not readable; it is left as it is.");
                        status.Failed(game.Id, kind, $"the \"{NoteTitle}\" note on RomM is not readable.");
                        ended = true;
                        return local;
                    }
                }
                if (local == null && remote == null)
                    return null;   // nothing either side (the finally ends it without a result)

                Record merged = Merge(local, remote);
                bool localLacks = local == null || merged.Unlocked.Count > local.Unlocked.Count || (local.Definitions.Count == 0 && merged.Definitions.Count > 0);
                bool remoteLacks = remote == null || merged.Unlocked.Count > remote.Unlocked.Count || (remote.Definitions.Count == 0 && merged.Definitions.Count > 0);

                // A record with nothing unlocked is not written to the server (a Windows game's
                // Steam list, looked at, would otherwise leave a note on every game).
                if (merged.Unlocked.Count == 0)
                    remoteLacks = false;
                if (remoteLacks)
                {
                    var body = new JObject { ["title"] = NoteTitle, ["content"] = JsonConvert.SerializeObject(merged), ["is_public"] = false };
                    if (note != null)
                        Send(HttpMethod.Put, $"{host}/api/roms/{romId}/notes/{(int)note["id"]}", body);
                    else
                        Send(HttpMethod.Post, $"{host}/api/roms/{romId}/notes", body);
                }
                if (localLacks && account == _accountFolder)
                    SaveRecord(account, game.Id, merged);
                status.Succeeded(game.Id, kind);
                ended = true;
                return merged;
            }
            catch (Exception ex)
            {
                s_log.Info($"[Achievements] {game.Name}: the achievements note could not be synced ({ex.Message}).");
                status.Failed(game.Id, kind, ex.Message);
                ended = true;
                return null;
            }
            finally
            {
                if (!ended)
                    status.Cancel(game.Id);
            }
        }

        private static Record Merge(Record local, Record remote)
        {
            Record basis = local != null && local.Definitions.Count > 0 ? local : (remote ?? local);
            var merged = new Record
            {
                Kind = basis.Kind,
                SetId = basis.SetId,
                Definitions = basis.Definitions,
                UpdatedUtc = DateTime.UtcNow,
            };
            foreach (Record side in new[] { local, remote })
            {
                if (side?.Unlocked == null)
                    continue;
                foreach (KeyValuePair<string, DateTime> unlock in side.Unlocked)
                {
                    if (!merged.Unlocked.TryGetValue(unlock.Key, out DateTime known) || unlock.Value < known)
                        merged.Unlocked[unlock.Key] = unlock.Value;
                }
            }
            return merged;
        }

        private int Me(string host)
        {
            if (_me < 0)
                _me = (int?)GetJson(host + "/api/users/me")?["id"] ?? -1;
            return _me;
        }

        private static JToken GetJson(string url)
        {
            using (var response = HttpClientSingleton.Instance.GetAsync(url).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                return JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        }

        private static void Send(HttpMethod method, string url, JObject body)
        {
            using (var request = new HttpRequestMessage(method, url) { Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json") })
            using (var response = HttpClientSingleton.Instance.SendAsync(request).GetAwaiter().GetResult())
                response.EnsureSuccessStatusCode();
        }

        // ---- Where a game's set is ------------------------------------------------------------------

        private Dictionary<Guid, Source> Sources
        {
            get
            {
                if (_sources != null)
                    return _sources;
                try
                {
                    if (File.Exists(_sourcesPath))
                        _sources = JsonConvert.DeserializeObject<Dictionary<Guid, Source>>(File.ReadAllText(_sourcesPath));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Achievements] Could not read where the emulators' achievements are: {ex.Message}");
                }
                return _sources ?? (_sources = new Dictionary<Guid, Source>());
            }
        }

        private void Remember(Guid gameId, Source source)
        {
            lock (_lock)
            {
                Sources[gameId] = source;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_sourcesPath));
                    File.WriteAllText(_sourcesPath, JsonConvert.SerializeObject(Sources, Formatting.Indented));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Achievements] Could not note where {gameId}'s achievements are: {ex.Message}");
                }
            }
        }

        // The game's set: the one the session wrote (learned), or the one learned before.
        private Source Learn(Game game, string kind, string installDir, DateTime started)
        {
            if (kind == SteamEmu)
                return LearnSteamEmu(game, installDir);
            DateTime since = started - TimeSpan.FromSeconds(5);
            Source found = null;
            if (kind == "rpcs3")
            {
                string trophies = Rpcs3TrophyRoot(installDir);
                if (trophies != null && Directory.Exists(trophies))
                {
                    DirectoryInfo written = new DirectoryInfo(trophies).GetDirectories()
                        .Where(d => File.Exists(Path.Combine(d.FullName, "TROPUSR.DAT")))
                        .Select(d => new { Dir = d, When = File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "TROPUSR.DAT")) })
                        .Where(x => x.When >= since)
                        .OrderByDescending(x => x.When)
                        .Select(x => x.Dir)
                        .FirstOrDefault();
                    if (written != null)
                        found = new Source { Kind = kind, Path = written.FullName };
                }
            }
            else
            {
                string gpds = XeniaGpdFolder(installDir);
                if (gpds != null && Directory.Exists(gpds))
                {
                    FileInfo written = new DirectoryInfo(gpds).GetFiles("*.gpd")
                        .Where(f => !f.Name.StartsWith("FFFE07D1", StringComparison.OrdinalIgnoreCase) && f.LastWriteTimeUtc >= since)
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (written == null)
                    {
                        // The title id from the save folders learned for the game (4D5307F1/00000001).
                        string title = new Saves.SaveFolderMap(Path.Combine(_dataFolder, "SaveFolders"), game.Id).Load()
                            .Select(u => u.Split('/')[0])
                            .FirstOrDefault(t => t.Length == 8 && t.All(Uri.IsHexDigit));
                        if (title != null && File.Exists(Path.Combine(gpds, title + ".gpd")))
                            written = new FileInfo(Path.Combine(gpds, title + ".gpd"));
                    }
                    if (written != null)
                        found = new Source { Kind = kind, Path = written.FullName };
                }
            }

            if (found != null)
            {
                Remember(game.Id, found);
                return found;
            }
            // The set learned before, under the user in use now; for RPCS3, else the one the
            // game's own files name.
            Source known = Known(game, kind, installDir);
            if (known != null || kind != "rpcs3")
                return known;
            FindTrophyPackage(game, _plugin.Playnite, out string setId);
            string trophyRoot = setId == null ? null : Rpcs3TrophyRoot(installDir);
            if (trophyRoot == null)
                return null;
            var matched = new Source { Kind = kind, Path = Path.Combine(trophyRoot, setId) };
            Remember(game.Id, matched);
            return matched;
        }

        private static string Rpcs3TrophyRoot(string installDir)
        {
            if (string.IsNullOrEmpty(installDir))
                return null;
            string home = Path.Combine(installDir, "dev_hdd0", "home");
            string user = null;
            try
            {
                // RPCS3's own record of the active user ([Users] active_user=00000001).
                string persistent = Path.Combine(installDir, "GuiConfigs", "persistent_settings.dat");
                if (File.Exists(persistent))
                {
                    string line = File.ReadAllLines(persistent).FirstOrDefault(l => l.Trim().StartsWith("active_user=", StringComparison.OrdinalIgnoreCase));
                    string value = line?.Substring(line.IndexOf('=') + 1).Trim();
                    if (!string.IsNullOrEmpty(value) && Directory.Exists(Path.Combine(home, value)))
                        user = value;
                }
            }
            catch
            {
                // The most recently used user is good enough.
            }
            if (user == null && Directory.Exists(home))
            {
                user = new DirectoryInfo(home).GetDirectories()
                    .Where(d => d.Name.Length == 8 && d.Name.All(char.IsDigit))
                    .OrderByDescending(d => Directory.Exists(Path.Combine(d.FullName, "trophy")) ? Directory.GetLastWriteTimeUtc(Path.Combine(d.FullName, "trophy")) : DateTime.MinValue)
                    .Select(d => d.Name)
                    .FirstOrDefault();
            }
            return Path.Combine(home, user ?? "00000001", "trophy");
        }

        private static string XeniaGpdFolder(string installDir)
        {
            string content = null;
            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "content")))
                content = Path.Combine(installDir, "content");
            else
            {
                string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Xenia", "content");
                if (Directory.Exists(documents))
                    content = documents;
            }
            if (content == null)
                return null;

            // The profile Xenia is set to sign in (each account's own; see Account.EmulatorUsers).
            string xuid = string.IsNullOrEmpty(installDir) ? null : Saves.Handlers.EmulatorActiveUser.XeniaSignedIn(installDir);
            if (xuid != null && !Directory.Exists(Path.Combine(content, xuid)))
                xuid = null;
            if (xuid == null)
            try
            {
                // The profile Xenia signs in by itself, when set (logged_profile_slot_0_xuid = "...").
                string config = string.IsNullOrEmpty(installDir) ? null : Path.Combine(installDir, "xenia-canary.config.toml");
                if (config != null && File.Exists(config))
                {
                    string line = File.ReadAllLines(config).FirstOrDefault(l => l.TrimStart().StartsWith("logged_profile_slot_0_xuid", StringComparison.OrdinalIgnoreCase));
                    if (line != null)
                    {
                        string value = line.Substring(line.IndexOf('=') + 1).Split('#')[0].Trim().Trim('"');
                        if (value.Length == 16 && Directory.Exists(Path.Combine(content, value)))
                            xuid = value.ToUpperInvariant();
                    }
                }
            }
            catch
            {
                // The most recently used profile is good enough.
            }
            if (xuid == null)
            {
                xuid = new DirectoryInfo(content).GetDirectories()
                    .Where(d => d.Name.Length == 16 && d.Name.All(Uri.IsHexDigit))
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .Select(d => d.Name)
                    .FirstOrDefault();
            }
            return xuid == null ? null : Path.Combine(content, xuid, "FFFE07D1", "00010000", xuid);
        }

        // ---- Reading the emulators' files --------------------------------------------------------

        private EmulatorSet Read(Source source)
        {
            if (source.Kind == SteamEmu)
                return ReadSteamEmu(source);
            return source.Kind == "rpcs3" ? ReadRpcs3(source.Path) : ReadXenia(source.Path);
        }

        private EmulatorSet ReadRpcs3(string folder)
        {
            string conf = Path.Combine(folder, "TROPCONF.SFM");
            string usr = Path.Combine(folder, "TROPUSR.DAT");
            if (!File.Exists(conf))
                return null;

            // The list: RPCS3 keeps the language's TROP_xx.SFM as TROPCONF.SFM, names included.
            EmulatorSet set = ParseTrophyList(File.ReadAllBytes(conf), Path.GetFileName(folder), number =>
            {
                string png = Path.Combine(folder, $"TROP{number:000}.PNG");
                return File.Exists(png) ? File.ReadAllBytes(png) : null;
            });
            if (set == null)
                return null;

            // Unlocks: TROPUSR.DAT's table 6 (big-endian): per trophy its id, state and times.
            if (File.Exists(usr))
            {
                byte[] data = ReadShared(usr);
                if (data.Length >= 0x30 && BE32(data, 0) == 0x818F54AD)
                {
                    uint tables = BE32(data, 8);
                    for (int t = 0; t < tables; t++)
                    {
                        int header = 0x30 + t * 0x20;
                        if (header + 0x20 > data.Length || BE32(data, header) != 6)
                            continue;
                        uint count = BE32(data, header + 12);
                        long offset = (long)BE64(data, header + 16);
                        for (int i = 0; i < count; i++)
                        {
                            long entry = offset + i * 0x70L;
                            if (entry + 0x70 > data.Length)
                                break;
                            uint trophyId = BE32(data, (int)entry + 16);
                            uint state = BE32(data, (int)entry + 20);
                            ulong tick = BE64(data, (int)entry + 40);
                            if (state != 0)
                                set.Unlocked[trophyId.ToString()] = FromCellTick(tick);
                        }
                    }
                }
            }
            return set;
        }

        // A trophy list (an .SFM: XML, after a header in some), with its icons; null when unreadable.
        private EmulatorSet ParseTrophyList(byte[] sfm, string fallbackId, Func<int, byte[]> iconOf)
        {
            string text = Encoding.UTF8.GetString(sfm);
            int start = text.IndexOf("<trophyconf", StringComparison.Ordinal);
            if (start < 0)
                return null;
            int end = text.LastIndexOf("</trophyconf>", StringComparison.Ordinal);
            XElement root = XElement.Parse(end > start ? text.Substring(start, end - start + "</trophyconf>".Length) : text.Substring(start));

            var set = new EmulatorSet { Kind = "rpcs3", SetId = (string)root.Element("npcommid") ?? fallbackId };
            string icons = Path.Combine(_iconsFolder, "rpcs3_" + set.SetId);
            Directory.CreateDirectory(icons);
            foreach (XElement trophy in root.Elements("trophy"))
            {
                if (!int.TryParse((string)trophy.Attribute("id"), out int number))
                    continue;
                string grade = ((string)trophy.Attribute("ttype") ?? "B").ToUpperInvariant();
                string id = number.ToString();
                set.Definitions.Add(new Definition
                {
                    Id = id,
                    Title = (string)trophy.Element("name") ?? $"Trophy {number}",
                    Description = (string)trophy.Element("detail") ?? "",
                    Points = grade == "P" ? 180 : grade == "G" ? 90 : grade == "S" ? 30 : 15,
                    Hidden = string.Equals((string)trophy.Attribute("hidden"), "yes", StringComparison.OrdinalIgnoreCase),
                    Order = number,
                });
                string icon = Path.Combine(icons, id + ".png");
                if (!File.Exists(icon))
                {
                    try
                    {
                        byte[] png = iconOf(number);
                        if (png != null && png.Length > 0)
                            File.WriteAllBytes(icon, png);
                    }
                    catch (Exception ex)
                    {
                        s_log.Info($"[Achievements] Could not keep the icon of trophy {number} ({set.SetId}): {ex.Message}");
                    }
                }
            }
            return set.Definitions.Count == 0 ? null : set;
        }

        /// <summary>
        /// A game's own trophy package (TROPDIR\NPWR...\TROPHY.TRP), which RPCS3 installs on the
        /// game's first boot: the list and icons, nothing unlocked. Big-endian: a 0x40-byte header
        /// (magic DCA24D00, file count at 0x10, entry size at 0x14), then per file its name
        /// (32 bytes), offset and size (as RPCS3's TRPLoader reads it).
        /// </summary>
        private EmulatorSet ReadTrophyPackage(string trp, string setId)
        {
            byte[] data = ReadShared(trp);
            if (data.Length < 0x40 || BE32(data, 0) != 0xDCA24D00)
                return null;
            uint count = BE32(data, 0x10);
            uint entrySize = BE32(data, 0x14);
            if (entrySize < 0x30)
                entrySize = 0x40;
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                long at = 0x40 + i * (long)entrySize;
                if (at + 0x30 > data.Length)
                    break;
                string name = Encoding.ASCII.GetString(data, (int)at, 32).TrimEnd('\0');
                long offset = (long)BE64(data, (int)at + 32);
                long size = (long)BE64(data, (int)at + 40);
                if (offset < 0 || size <= 0 || offset + size > data.Length)
                    continue;
                byte[] content = new byte[size];
                Array.Copy(data, offset, content, 0, size);
                files[name] = content;
            }

            // The English list when there is one (RPCS3 picks TROP_xx.SFM by system language),
            // else the default, else the bare configuration.
            byte[] sfm = null;
            foreach (string name in new[] { "TROP_01.SFM", "TROP.SFM", "TROPCONF.SFM" })
            {
                if (files.TryGetValue(name, out sfm))
                    break;
            }
            if (sfm == null)
                return null;
            return ParseTrophyList(sfm, setId, number => files.TryGetValue($"TROP{number:000}.PNG", out byte[] png) ? png : null);
        }

        // A PS3 game's trophy package and set id, from its folder: disc games keep it in
        // PS3_GAME\TROPDIR\NPWRxxxxx_xx, installed (PSN) games in TROPDIR\NPWR... beside USRDIR.
        // An .iso is not looked into.
        private static string FindTrophyPackage(Game game, IPlayniteAPI api, out string setId)
        {
            setId = null;
            string rom = game.Roms?.FirstOrDefault()?.Path;
            if (string.IsNullOrEmpty(rom))
                return null;
            try
            {
                rom = api.ExpandGameVariables(game, rom);
                string dir = Directory.Exists(rom) ? rom : Path.GetDirectoryName(rom);
                for (int up = 0; up < 5 && !string.IsNullOrEmpty(dir); up++)
                {
                    foreach (string tropdir in new[] { Path.Combine(dir, "PS3_GAME", "TROPDIR"), Path.Combine(dir, "TROPDIR") })
                    {
                        if (!Directory.Exists(tropdir))
                            continue;
                        foreach (DirectoryInfo candidate in new DirectoryInfo(tropdir).GetDirectories("NPWR*").OrderBy(d => d.Name, StringComparer.Ordinal))
                        {
                            string trp = Path.Combine(candidate.FullName, "TROPHY.TRP");
                            if (File.Exists(trp))
                            {
                                setId = candidate.Name;
                                return trp;
                            }
                        }
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex)
            {
                s_log.Info($"[Achievements] Could not look for {game.Name}'s trophies: {ex.Message}");
            }
            return null;
        }

        // A CellRtcTick: microseconds since 0001-01-01 (UTC).
        private static DateTime? FromCellTick(ulong tick)
        {
            try
            {
                if (tick == 0)
                    return null;
                DateTime when = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks((long)tick * 10);
                return when.Year >= 2000 && when.Year <= 2200 ? when : (DateTime?)null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private EmulatorSet ReadXenia(string gpd)
        {
            if (!File.Exists(gpd))
                return null;
            byte[] data = ReadShared(gpd);
            if (data.Length < 0x18 || BE32(data, 0) != 0x58444246)   // "XDBF"
                return null;

            uint entryCount = BE32(data, 8);
            uint entryUsed = BE32(data, 12);
            uint freeCount = BE32(data, 16);
            long dataStart = 0x18 + 18L * entryCount + 8L * freeCount;
            string titleId = Path.GetFileNameWithoutExtension(gpd).ToUpperInvariant();
            var set = new EmulatorSet { Kind = "xenia", SetId = titleId };
            var images = new Dictionary<ulong, byte[]>();
            var imageOf = new Dictionary<string, ulong>();

            for (int i = 0; i < entryUsed && i < entryCount; i++)
            {
                int at = 0x18 + i * 18;
                if (at + 18 > data.Length)
                    break;
                ushort section = BE16(data, at);
                ulong id = BE64(data, at + 2);
                long offset = dataStart + BE32(data, at + 10);
                uint size = BE32(data, at + 14);
                if (offset < 0 || offset + size > data.Length)
                    continue;

                if (section == 2)
                {
                    images[id] = data.Skip((int)offset).Take((int)size).ToArray();
                    continue;
                }
                if (section != 1 || size < 0x1C)
                    continue;

                int a = (int)offset;
                uint achievementId = BE32(data, a + 4);
                uint imageId = BE32(data, a + 8);
                uint gamerscore = BE32(data, a + 12);
                uint flags = BE32(data, a + 16);
                ulong fileTime = BE64(data, a + 20);
                int cursor = a + 0x1C;
                int limit = (int)(offset + size);
                string label = ReadUtf16(data, ref cursor, limit);
                string description = ReadUtf16(data, ref cursor, limit);
                string unachieved = ReadUtf16(data, ref cursor, limit);

                string key = achievementId.ToString();
                set.Definitions.Add(new Definition
                {
                    Id = key,
                    Title = label,
                    Description = description,
                    LockedDescription = string.IsNullOrEmpty(unachieved) ? null : unachieved,
                    Points = (int)gamerscore,
                    Hidden = (flags & 0x8) == 0,
                    Order = (int)achievementId,
                });
                imageOf[key] = imageId;
                if ((flags & 0x30000) != 0)
                {
                    DateTime? when = null;
                    try
                    {
                        if (fileTime != 0)
                            when = DateTime.FromFileTimeUtc((long)fileTime);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        when = null;
                    }
                    set.Unlocked[key] = when;
                }
            }

            string icons = Path.Combine(_iconsFolder, "xenia_" + titleId);
            Directory.CreateDirectory(icons);
            foreach (KeyValuePair<string, ulong> pair in imageOf)
            {
                string icon = Path.Combine(icons, pair.Key + ".png");
                if (!File.Exists(icon) && images.TryGetValue(pair.Value, out byte[] png) && png.Length > 0)
                    File.WriteAllBytes(icon, png);
            }
            return set.Definitions.Count == 0 ? null : set;
        }

        private static string ReadUtf16(byte[] data, ref int cursor, int limit)
        {
            var text = new StringBuilder();
            while (cursor + 1 < limit)
            {
                char c = (char)((data[cursor] << 8) | data[cursor + 1]);
                cursor += 2;
                if (c == '\0')
                    break;
                text.Append(c);
            }
            return text.ToString();
        }

        private static void CopyIcon(string from, string to)
        {
            try
            {
                if (File.Exists(from) && !File.Exists(to))
                    File.Copy(from, to);
            }
            catch (Exception ex)
            {
                s_log.Info($"[Achievements] Could not copy the icon {from}: {ex.Message}");
            }
        }

        // The emulator may still have the file open as it closes.
        private static byte[] ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        private static ushort BE16(byte[] b, int at) => (ushort)((b[at] << 8) | b[at + 1]);

        private static uint BE32(byte[] b, int at) => ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

        private static ulong BE64(byte[] b, int at) => ((ulong)BE32(b, at) << 32) | BE32(b, at + 4);
    }
}
