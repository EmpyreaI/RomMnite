using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace RomM.Games
{
    /// <summary>
    /// Windows games whose copy keeps its achievements through a Steam emulator (its own
    /// steam_api.dll in place of Steam's): read from the emulator's files (SteamEmuFiles), never
    /// written, and made the account's as RPCS3's trophies are (by session time; everything,
    /// when the account is the only one on this computer), kept in the same RomM note.
    ///
    /// Unlocks are read only where a game has an emulator's marks in its folder: a
    /// steam_settings folder (gbe_fork, Goldberg), one of the emulators' .ini files beside the
    /// dll, or a file of achievements already written for its app id.
    ///
    /// The list, though, is shown for every RomM Windows game matched on Steam, installed or not
    /// (its Steam app id from RomM): with what the account unlocked on any computer (its RomM
    /// note), or none. A record with nothing unlocked is never written to the server.
    ///
    /// The list: the game's own steam_settings\achievements.json (with its icons) where it has
    /// one; else Steam's (IPlayerService/GetGameAchievements, asked once a month and kept, its
    /// icons fetched once); else only the names the emulator's file has.
    /// </summary>
    public sealed partial class EmulatorAchievements
    {
        internal const string SteamEmu = "steamemu";
        private static readonly TimeSpan SteamListKeptFor = TimeSpan.FromDays(30);
        private static readonly HttpClient s_steam = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // Games looked through and found to have no Steam emulator, and when (looked again later,
        // or when the folder changes).
        private readonly ConcurrentDictionary<Guid, KeyValuePair<string, DateTime>> _noSteamEmu = new ConcurrentDictionary<Guid, KeyValuePair<string, DateTime>>();
        // Installed Windows games whose copy was looked through and has no Steam emulator: their
        // achievements cannot be earned here (said over the list, AchievementControls).
        private readonly ConcurrentDictionary<Guid, bool> _cannotEarnHere = new ConcurrentDictionary<Guid, bool>();

        /// <summary>What is said over an installed Windows game's achievements when its copy cannot earn them.</summary>
        internal static string CannotEarnHere(string gameName) =>
            $"This copy of {gameName} doesn't record its achievements anywhere RomMnite can read, so they can't be earned here. " +
            "RomMnite reads achievements from Steam emulators' files (gbe_fork, Goldberg, CODEX and others) in a game's copy.";

        // Steam lists that could not be fetched, and when (asked again after an hour).
        private readonly ConcurrentDictionary<int, DateTime> _steamListFailed = new ConcurrentDictionary<int, DateTime>();

        // A RomM Windows game (installed or not): its folder, when installed. Quick: nothing
        // looked through.
        private bool SteamEmuGame(Game game, out string installDir)
        {
            installDir = null;
            if (game == null)
                return false;
            if (game.IsInstalled && !string.IsNullOrEmpty(game.InstallDirectory))
                installDir = PlaynitePath.Resolve(_plugin.Playnite, game.InstallDirectory);
            return true;
        }

        // The game's emulator (its dll's folder and the app id), as learned before or looked for
        // now. Null when the game has none (or is not installed). Off the UI thread: it may look
        // through the folder.
        private Source LearnSteamEmu(Game game, string installDir)
        {
            Source known;
            lock (_lock)
                known = Sources.TryGetValue(game.Id, out Source kept) && kept.Kind == SteamEmu ? kept : null;
            if (known != null && known.AppId > 0 && !string.IsNullOrEmpty(known.DllFolder) && Directory.Exists(known.DllFolder))
                return known;
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return null;
            // Looked through lately and nothing found (looked again after a while, or once the
            // game is in another folder).
            if (_noSteamEmu.TryGetValue(game.Id, out var none) && string.Equals(none.Key, installDir, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - none.Value < TimeSpan.FromMinutes(10))
                return null;

            Source found = LocateSteamEmu(game, installDir);
            if (found == null)
            {
                _noSteamEmu[game.Id] = new KeyValuePair<string, DateTime>(installDir, DateTime.UtcNow);
                return null;
            }
            Remember(game.Id, found);
            s_log.Info($"[Achievements] {game.Name}: Steam emulator achievements, app {found.AppId}{(found.DllFolder != null ? " (" + found.DllFolder + ")" : "")}.");
            return found;
        }

        private Source LocateSteamEmu(Game game, string installDir)
        {
            List<string> dlls = SteamEmuFiles.DllFolders(installDir);
            int appId = 0;
            string dll = null;
            foreach (string folder in dlls.Concat(new[] { installDir }))
            {
                appId = SteamEmuFiles.AppIdIn(folder);
                if (appId > 0)
                {
                    dll = dlls.Contains(folder) ? folder : dlls.FirstOrDefault();
                    break;
                }
            }
            if (dlls.Count == 0)
                return null;   // no steam_api dll: not a Steam game's copy (GOG's and the like)
            dll = dll ?? dlls[0];

            // An emulator's own marks beside the dll.
            bool marked = Directory.Exists(Path.Combine(dll, "steam_settings"))
                || new[] { "steam_emu.ini", "cream_api.ini", "SmartSteamEmu.ini", "ColdClientLoader.ini", "SKIDROW.ini", "CPY.ini", "ds.ini" }
                    .Any(name => File.Exists(Path.Combine(dll, name)));
            if (appId <= 0)
            {
                // The app RomM (or Steam's store) matched the game with.
                try
                {
                    appId = _plugin.SteamAppIdAsync(game).GetAwaiter().GetResult() ?? 0;
                }
                catch
                {
                    appId = 0;
                }
            }
            if (appId <= 0)
                return null;
            string file = SteamEmuFiles.NewestFile(SteamEmuFiles.SaveFolders(appId, dll));
            if (!marked && file == null)
                return null;   // Steam's own dll, most likely: Steam keeps those achievements
            return new Source { Kind = SteamEmu, Path = file ?? "", AppId = appId, DllFolder = dll };
        }

        /// <summary>
        /// For the list alone: the game's Steam app id (RomM's match, or a Steam link on the game),
        /// as a source without an emulator. Its unlocks, if any, are still looked for where the
        /// emulators keep them (an earlier install's). Null when the game is not matched on Steam.
        /// </summary>
        private Source SteamListSource(Game game)
        {
            int appId;
            try
            {
                appId = _plugin.SteamAppIdAsync(game).GetAwaiter().GetResult() ?? 0;
            }
            catch
            {
                appId = 0;
            }
            return appId > 0 ? new Source { Kind = SteamEmu, Path = "", AppId = appId } : null;
        }

        // The game's achievements as the emulator has them now, with the list.
        private EmulatorSet ReadSteamEmu(Source source)
        {
            if (source == null || source.AppId <= 0)
                return null;
            string icons = Path.Combine(_iconsFolder, SteamEmu + "_" + source.AppId);
            List<SteamEmuFiles.Entry> list = SettingsList(source, icons) ?? SteamList(source.AppId, icons);

            var unlocks = new Dictionary<string, SteamEmuFiles.Unlock>(StringComparer.OrdinalIgnoreCase);
            string file = SteamEmuFiles.NewestFile(SteamEmuFiles.SaveFolders(source.AppId, source.DllFolder));
            if (file != null)
            {
                source.Path = file;
                try
                {
                    unlocks = SteamEmuFiles.Parse(file, ReadShared(file), list?.Select(e => e.Id));
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Achievements] Could not read {file}: {ex.Message}");
                }
            }
            if (list == null || list.Count == 0)
            {
                // Only what the emulator's file names: its names as titles.
                list = unlocks.Keys.Select((id, i) => new SteamEmuFiles.Entry { Id = id, Title = Readable(id), Description = "", Order = i }).ToList();
            }
            if (list.Count == 0)
                return null;

            var set = new EmulatorSet { Kind = SteamEmu, SetId = source.AppId.ToString() };
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SteamEmuFiles.Entry entry in list)
            {
                ids[entry.Id] = entry.Id;
                set.Definitions.Add(new Definition
                {
                    Id = entry.Id,
                    Title = entry.Title,
                    Description = entry.Description,
                    Hidden = entry.Hidden,
                    Order = entry.Order,
                });
            }
            foreach (KeyValuePair<string, SteamEmuFiles.Unlock> unlock in unlocks)
            {
                // By the list's spelling (some emulators write the names in another case).
                if (unlock.Value.Achieved && ids.TryGetValue(unlock.Key, out string id))
                    set.Unlocked[id] = unlock.Value.WhenUtc;
            }
            return set;
        }

        // gbe_fork's and Goldberg's list in the game's steam_settings, its icons copied.
        private static List<SteamEmuFiles.Entry> SettingsList(Source source, string icons)
        {
            if (string.IsNullOrEmpty(source.DllFolder))
                return null;
            string settings = Path.Combine(source.DllFolder, "steam_settings");
            string file = Path.Combine(settings, "achievements.json");
            if (!File.Exists(file))
                return null;
            try
            {
                List<SteamEmuFiles.Entry> list = SteamEmuFiles.ParseSettingsList(File.ReadAllText(file));
                if (list.Count == 0)
                    return null;
                Directory.CreateDirectory(icons);
                foreach (SteamEmuFiles.Entry entry in list)
                {
                    if (!string.IsNullOrEmpty(entry.Icon))
                        CopyIcon(Path.Combine(settings, entry.Icon), IconFile(icons, SteamEmu, entry.Id));
                    if (!string.IsNullOrEmpty(entry.IconGray))
                        CopyIcon(Path.Combine(settings, entry.IconGray), IconFile(icons, SteamEmu, entry.Id, gray: true));
                }
                return list;
            }
            catch (Exception ex)
            {
                s_log.Info($"[Achievements] Could not read {file}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Steam's list of a game's achievements (for the Store's pages): kept a month, fetched
        /// when need be; null when it cannot be had or the game has none. Its pictures are not
        /// fetched (the Store shows them from Steam, <see cref="SteamIconUrl"/>). Off the UI thread.
        /// </summary>
        internal List<SteamEmuFiles.Entry> SteamListFor(int appId) =>
            appId > 0 ? SteamList(appId, null) : null;

        /// <summary>Where Steam serves an achievement's picture, by the file name its list gives.</summary>
        internal static string SteamIconUrl(int appId, string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            return name.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{name}";
        }

        // Steam's list for the app, kept a month; its icons fetched once into icons (none when
        // icons is null). Null when it cannot be had.
        private List<SteamEmuFiles.Entry> SteamList(int appId, string icons)
        {
            string kept = Path.Combine(_dataFolder, "EmulatorAchievements", "Steam", appId + ".json");
            string text = null;
            try
            {
                if (File.Exists(kept) && DateTime.UtcNow - File.GetLastWriteTimeUtc(kept) < SteamListKeptFor)
                    text = File.ReadAllText(kept);
            }
            catch
            {
                text = null;
            }
            if (text == null && !(_steamListFailed.TryGetValue(appId, out DateTime failed) && DateTime.UtcNow - failed < TimeSpan.FromHours(1)))
            {
                try
                {
                    string url = $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?appid={appId}&language=english";
                    text = s_steam.GetStringAsync(url).GetAwaiter().GetResult();
                    if (SteamEmuFiles.ParseSteamList(text).Count > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(kept));
                        File.WriteAllText(kept, text);
                    }
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Achievements] Could not fetch Steam's achievements of app {appId}: {ex.Message}");
                    _steamListFailed[appId] = DateTime.UtcNow;
                    text = null;
                }
            }
            if (text == null && File.Exists(kept))
            {
                try
                {
                    text = File.ReadAllText(kept);   // old, but better than none
                }
                catch
                {
                    text = null;
                }
            }
            if (text == null)
                return null;

            List<SteamEmuFiles.Entry> list;
            try
            {
                list = SteamEmuFiles.ParseSteamList(text);
            }
            catch (Exception ex)
            {
                s_log.Info($"[Achievements] Steam's achievements of app {appId} are not readable: {ex.Message}");
                return null;
            }
            if (list.Count == 0)
                return null;
            if (icons != null)
                FetchSteamIcons(appId, list, icons);
            return list;
        }

        private static void FetchSteamIcons(int appId, List<SteamEmuFiles.Entry> list, string icons)
        {
            try
            {
                Directory.CreateDirectory(icons);
            }
            catch
            {
                return;
            }
            foreach (SteamEmuFiles.Entry entry in list)
            {
                foreach (var pair in new[] { new { Name = entry.Icon, Gray = false }, new { Name = entry.IconGray, Gray = true } })
                {
                    if (string.IsNullOrEmpty(pair.Name))
                        continue;
                    string to = IconFile(icons, SteamEmu, entry.Id, pair.Gray);
                    if (File.Exists(to))
                        continue;
                    try
                    {
                        string url = SteamIconUrl(appId, pair.Name);
                        byte[] bytes = s_steam.GetByteArrayAsync(url).GetAwaiter().GetResult();
                        File.WriteAllBytes(to, bytes);
                    }
                    catch (Exception ex)
                    {
                        s_log.Info($"[Achievements] Could not fetch the icon of {entry.Id} (app {appId}): {ex.Message}");
                        return;   // the rest next time
                    }
                }
            }
        }

        // An achievement's picture in the icons folder: RPCS3's and Xenia's by their number; a
        // Steam emulator's by its name, made safe for a file name ("_gray" for the locked one).
        private static string IconFile(string icons, string kind, string id, bool gray = false)
        {
            string name = kind == SteamEmu ? SteamEmuFiles.SafeName(id) : id;
            return Path.Combine(icons, name + (gray ? "_gray" : "") + ".png");
        }

        // "ACH_WIN_ONE_GAME" → "Win One Game": a title for an achievement only known by its name.
        private static string Readable(string id)
        {
            string text = id ?? "";
            if (text.StartsWith("ACH_", StringComparison.OrdinalIgnoreCase) || text.StartsWith("NEW_ACHIEVEMENT_", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(text.IndexOf('_') + 1);
            var words = text.Split(new[] { '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length > 1 ? char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant() : w.ToUpperInvariant());
            string readable = string.Join(" ", words);
            return readable.Length > 0 ? readable : id;
        }
    }
}
