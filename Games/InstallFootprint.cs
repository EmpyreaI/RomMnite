using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>
    /// How much room an installed game takes, measured on disk, as Steam shows it: everything in
    /// the game's own folder (its unpacked files, its updates and anything it wrote there), or,
    /// for a game that shares the platform's folder with others, its own files only: what it
    /// starts from, and the files those name (a .cue's tracks, an .m3u's discs, a .gdi's tracks).
    ///
    /// Playnite measures a game with ROMs by those ROM files alone, which for a disc is its .cue
    /// (not its tracks), for a folder game (PS3, Wii U, a Windows game) its one program file, and
    /// the server's size, used until then, is the download's (an archive's, not what it unpacks
    /// to). So the size is measured here after an install, after an unpack, and once a start-up
    /// for every installed game (in the background), and set when it differs; Playnite's own
    /// library scans measure again only when a ROM file changes.
    /// </summary>
    internal static class InstallFootprint
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static int s_refreshing;

        // Differences smaller than this are not worth an update (and the events that follow it).
        private const double Tolerance = 0.005;

        /// <summary>The game's size on disk, or null when it cannot be told (nothing found on disk).</summary>
        public static ulong? Measure(IPlayniteAPI api, Game game, CancellationToken cancel = default(CancellationToken))
        {
            if (game == null)
                return null;
            string folder = PlaynitePath.Resolve(api, game.InstallDirectory);
            List<string> roms = (game.Roms ?? new System.Collections.ObjectModel.ObservableCollection<GameRom>())
                .Select(r => PlaynitePath.Resolve(api, r.Path))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && OwnsFolder(api, game, folder))
                return FolderSize(folder, cancel);

            // Shares its folder (or has none): its own files.
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rom in roms)
                AddWithCompanions(rom, files, 0);
            if (files.Count == 0)
                return null;
            ulong total = 0;
            foreach (string file in files)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    total += (ulong)new FileInfo(file).Length;
                }
                catch
                {
                    // Gone, or not readable: not counted.
                }
            }
            return total;
        }

        /// <summary>
        /// Measures the game and sets its size when it differs. Any thread; returns whether it
        /// changed. Best-effort.
        /// </summary>
        public static bool Update(IPlayniteAPI api, Guid gameId, CancellationToken cancel = default(CancellationToken))
        {
            try
            {
                Game game = api.Database.Games.Get(gameId);
                if (game == null || !game.IsInstalled)
                    return false;
                ulong? measured = Measure(api, game, cancel);
                if (!measured.HasValue || measured.Value == 0)
                    return false;
                ulong now = game.InstallSize ?? 0;
                if (now > 0 && Math.Abs((double)measured.Value - now) <= now * Tolerance)
                    return false;
                game = api.Database.Games.Get(gameId);
                if (game == null)
                    return false;
                game.InstallSize = measured.Value;
                api.Database.Games.Update(game);
                s_log.Info($"[Size] {game.Name}: {Describe(now)} -> {Describe(measured.Value)} on disk.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Size] Could not measure a game's size: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Every installed game of this plugin measured again, one at a time, in the background,
        /// once at a time.
        /// </summary>
        public static void RefreshAll(IPlayniteAPI api, Guid pluginId, CancellationToken cancel)
        {
            if (Interlocked.CompareExchange(ref s_refreshing, 1, 0) != 0)
                return;
            Task.Run(() =>
            {
                try
                {
                    var started = DateTime.UtcNow;
                    List<Guid> ids = api.Database.Games
                        .Where(g => g.PluginId == pluginId && g.IsInstalled)
                        .Select(g => g.Id)
                        .ToList();
                    int changed = 0;
                    foreach (Guid id in ids)
                    {
                        if (cancel.IsCancellationRequested)
                            return;
                        if (Update(api, id, cancel))
                            changed++;
                    }
                    s_log.Info($"[Size] Measured {ids.Count} installed game(s) in {(DateTime.UtcNow - started).TotalSeconds:0.0} s; {changed} corrected.");
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Size] Measuring the installed games stopped: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref s_refreshing, 0);
                }
            });
        }

        // The folder is the game's alone: no other game in the library has it (or a folder inside
        // it) as its folder, and it is not a drive's root.
        private static bool OwnsFolder(IPlayniteAPI api, Game game, string folder)
        {
            string mine = Normal(folder);
            if (mine == null || Path.GetPathRoot(mine)?.TrimEnd('\\') == mine)
                return false;
            foreach (Game other in api.Database.Games)
            {
                if (other.Id == game.Id || string.IsNullOrEmpty(other.InstallDirectory))
                    continue;
                string theirs = Normal(PlaynitePath.Resolve(api, other.InstallDirectory));
                if (theirs == null)
                    continue;
                if (string.Equals(theirs, mine, StringComparison.OrdinalIgnoreCase)
                    || theirs.StartsWith(mine + "\\", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static string Normal(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return null;
            }
        }

        // Every file under the folder, not following junctions or links (a link to elsewhere is
        // not this game's room).
        private static ulong FolderSize(string folder, CancellationToken cancel)
        {
            ulong total = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(folder));
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                DirectoryInfo dir = pending.Pop();
                FileSystemInfo[] entries;
                try
                {
                    entries = dir.GetFileSystemInfos();
                }
                catch
                {
                    continue;   // not readable: not counted
                }
                foreach (FileSystemInfo entry in entries)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (entry is DirectoryInfo sub)
                        pending.Push(sub);
                    else if (entry is FileInfo file)
                        total += (ulong)file.Length;
                }
            }
            return total;
        }

        // The file, and the files it names: a .cue's FILE lines, an .m3u's entries, a .gdi's
        // track files (each relative to the file naming it).
        private static void AddWithCompanions(string path, HashSet<string> files, int depth)
        {
            if (depth > 3 || string.IsNullOrEmpty(path) || !File.Exists(path) || !files.Add(path))
                return;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".cue" && ext != ".m3u" && ext != ".gdi")
                return;
            string folder = Path.GetDirectoryName(path) ?? "";
            string[] lines;
            try
            {
                // A sheet is small; a large "sheet" is not one.
                if (new FileInfo(path).Length > 256 * 1024)
                    return;
                lines = File.ReadAllLines(path);
            }
            catch
            {
                return;
            }
            foreach (string named in Named(ext, lines))
            {
                string full;
                try
                {
                    full = Path.GetFullPath(Path.Combine(folder, named));
                }
                catch
                {
                    continue;
                }
                AddWithCompanions(full, files, depth + 1);
            }
        }

        internal static IEnumerable<string> Named(string ext, IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (ext == ".m3u")
                {
                    if (!line.StartsWith("#"))
                        yield return line;
                }
                else if (ext == ".cue")
                {
                    if (!line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string rest = line.Substring(5).Trim();
                    if (rest.StartsWith("\""))
                    {
                        int close = rest.IndexOf('"', 1);
                        if (close > 1)
                            yield return rest.Substring(1, close - 1);
                    }
                    else
                    {
                        int space = rest.LastIndexOf(' ');
                        yield return space > 0 ? rest.Substring(0, space) : rest;
                    }
                }
                else if (ext == ".gdi")
                {
                    // "1 0 4 2352 track01.raw 0", or the name quoted; the first line is the count.
                    if (line.IndexOf('"') >= 0)
                    {
                        int open = line.IndexOf('"');
                        int close = line.IndexOf('"', open + 1);
                        if (close > open + 1)
                            yield return line.Substring(open + 1, close - open - 1);
                    }
                    else
                    {
                        string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 6)
                            yield return parts[4];
                    }
                }
            }
        }

        private static string Describe(ulong bytes) =>
            bytes == 0 ? "unknown" : $"{bytes / 1048576.0:0.0} MB";
    }
}
