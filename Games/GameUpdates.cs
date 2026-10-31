using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Downloads;
using RomM.Models.RomM.Rom;
using SharpCompress.Archives;
using SharpCompress.Readers.Zip;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>
    /// A game updated on the server, as Steam updates one: told (the files RomM lists for it now
    /// against those it was installed from, InstalledManifest), announced, and fetched in part.
    ///
    ///   One zip, unpacked (how Windows games are kept): the new zip's index read from the server
    ///     (RemoteZip) and compared with the old one's, file by file, by CRC-32 and size; only the
    ///     changed files' bytes are downloaded (HTTP ranges), unpacked in place and checked against
    ///     their CRC-32; files the new zip no longer has are removed.
    ///   A folder of files (a PlayStation game's .bin files): only the changed files, fetched
    ///     together as one zip (RomM's file_ids), unpacked in place; files gone are removed.
    ///   One file kept as it is (a disc image): downloaded again in full.
    ///
    /// Anything else in the game's folder (its settings, saves, mods) is never touched. When a
    /// partial update cannot be done (the server will not serve ranges, a file is packed in a way
    /// that cannot be read alone), the whole zip is downloaded and unpacked over the game instead.
    /// "Verify files" does the same comparison against the files on disk, by their CRC-32.
    /// </summary>
    public sealed class GameUpdates
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private const string PartName = ".rommnite-update.part";

        private readonly RomM _plugin;
        private readonly string _folder;
        private readonly string _offPath;
        private readonly object _lock = new object();
        private HashSet<int> _off;

        public GameUpdates(RomM plugin, string folder)
        {
            _plugin = plugin;
            _folder = folder;
            _offPath = Path.Combine(folder, "UpdatesOff.json");
        }

        public InstalledManifest Get(Guid gameId) => InstalledManifest.Load(_folder, gameId);

        /// <summary>The words for a game with an update waiting (and updates on for it), or null.</summary>
        public string Waiting(Game game) => game != null && game.IsInstalled && IsOn(game) ? Get(game.Id)?.UpdateAvailable : null;

        // ---- Updates on or off, per game ------------------------------------------------------------

        // The games not checked for updates, by RomM game number on this computer (UpdatesOff.json),
        // as cloud saves are switched off (Saves.SaveSyncOptOut).
        private HashSet<int> Off()
        {
            if (_off != null)
                return _off;
            try
            {
                _off = File.Exists(_offPath)
                    ? new HashSet<int>(Newtonsoft.Json.JsonConvert.DeserializeObject<List<int>>(File.ReadAllText(_offPath)) ?? new List<int>())
                    : new HashSet<int>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Updates] Could not read which games have updates off: {ex.Message}");
                _off = new HashSet<int>();
            }
            return _off;
        }

        /// <summary>Whether the game is checked for updates (any RomM game, unless switched off).</summary>
        public bool IsOn(Game game)
        {
            if (game == null || !RomMGameId.TryParse(game.GameId, out int romId, out string _))
                return true;
            lock (_lock)
                return !Off().Contains(romId);
        }

        /// <summary>Switches checking for updates on or off for the games.</summary>
        public void Set(IEnumerable<Game> games, bool on)
        {
            lock (_lock)
            {
                HashSet<int> off = Off();
                foreach (Game game in games)
                {
                    if (!RomMGameId.TryParse(game.GameId, out int romId, out string _))
                        continue;
                    if (on)
                        off.Remove(romId);
                    else
                    {
                        off.Add(romId);
                        _plugin.Playnite.Notifications.Remove("rommnite-update-" + game.Id);
                    }
                    s_log.Info($"[Updates] {game.Name}: updates {(on ? "on" : "off")}.");
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_offPath));
                    File.WriteAllText(_offPath, Newtonsoft.Json.JsonConvert.SerializeObject(off.OrderBy(i => i).ToList()));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Updates] Could not keep which games have updates off: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The game played with its update waiting ("Play without updating"). The second time
        /// (since last asked), whether to stop checking it for updates at all is asked. On the UI thread.
        /// </summary>
        public void PlayWithoutUpdating(Game game)
        {
            if (game == null)
                return;
            InstalledManifest manifest = Get(game.Id);
            if (manifest == null)
                return;
            manifest.Skipped++;
            bool ask = manifest.Skipped >= 2;
            if (ask)
                manifest.Skipped = 0;
            manifest.Save(_folder, game.Id);
            if (!ask)
                return;
            var answer = _plugin.Playnite.Dialogs.ShowMessage(
                $"You've played {game.Name} without updating it twice.\n\nStop checking for updates to it? You can turn them back on from its right-click menu (Check for updates).",
                "Game updates", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer == System.Windows.MessageBoxResult.Yes)
                Set(new[] { game }, false);
        }

        // ---- Recording an install ----------------------------------------------------------------

        /// <summary>
        /// A game installed: what it came from, for telling updates later. In the background
        /// (asks the server for its files).
        /// </summary>
        public void Installed(Game game, int romId, string installDir, DownloadRequest req)
        {
            if (game == null || romId <= 0)
                return;
            Task.Run(() =>
            {
                try
                {
                    RomMRom rom = _plugin.FetchRom(romId.ToString(CultureInfo.InvariantCulture));
                    string kind = req.Extracted ? (req.DownloadAsArchive ? "folder" : "zip") : "file";
                    var manifest = new InstalledManifest
                    {
                        Kind = kind,
                        RomId = romId,
                        InstallDir = installDir,
                        FilePath = kind == "file" ? req.GamePath : null,
                        RomPath = rom?.FullPath,
                        Files = (rom?.Files ?? new List<RomMFile>()).Select(InstalledManifest.ServerFile.From).ToList(),
                        Entries = req.ArchiveEntries ?? new List<InstalledManifest.ZipEntryRecord>(),
                        InstalledUtc = DateTime.UtcNow,
                    };
                    manifest.Save(_folder, game.Id);
                    s_log.Info($"[Updates] {game.Name}: installed as {kind} ({manifest.Files.Count} server files, {manifest.Entries.Count} in the zip) noted for updates.");
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Updates] Could not note what {game.Name} was installed from: {ex.Message}");
                }
            });
        }

        public void Uninstalled(Guid gameId) => InstalledManifest.Delete(_folder, gameId);

        // ---- Telling one ----------------------------------------------------------------------------

        /// <summary>
        /// The server's ROM for an installed game, as a library update reads it: an update waiting
        /// when its files differ from those it was installed from (announced once per update).
        /// A game installed before updates were followed is taken as up to date from now on.
        /// </summary>
        public void Check(Game game, RomMRom rom)
        {
            if (game == null || rom == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallDirectory) || rom.Files == null)
                return;
            try
            {
                if (!IsOn(game))
                    return;   // not checked (the record is kept, so turning it back on finds what changed)
                List<InstalledManifest.ServerFile> now = rom.Files.Select(InstalledManifest.ServerFile.From).ToList();
                InstalledManifest manifest = Get(game.Id);
                if (manifest == null)
                {
                    Baseline(game, rom, now);
                    return;
                }
                if (manifest.RomId != rom.Id)
                    return;   // installed from another version of it (a sibling): that one's own
                InstalledManifest.Changes changes = InstalledManifest.Compare(manifest.Files, now);
                if (!changes.Any)
                {
                    if (manifest.UpdateAvailable != null)
                    {
                        manifest.UpdateAvailable = null;
                        manifest.Save(_folder, game.Id);
                    }
                    return;
                }
                string signature = InstalledManifest.Signature(now);
                manifest.UpdateAvailable = changes.ToString();
                bool tell = manifest.NotifiedFor != signature;
                manifest.NotifiedFor = signature;
                manifest.Save(_folder, game.Id);
                if (tell)
                {
                    s_log.Info($"[Updates] {game.Name}: an update on the server ({changes}).");
                    Guid id = game.Id;
                    // Downloaded at once when chosen in the settings (not while it is being played).
                    if (_plugin.Settings?.AutoUpdateGames == true && !game.IsRunning && !game.IsLaunching && !game.IsInstalling)
                    {
                        s_log.Info($"[Updates] {game.Name}: downloading the update (automatic updates are on).");
                        _plugin.Playnite.MainView.UIDispatcher.BeginInvoke(new Action(() => Start(_plugin.Playnite.Database.Games.Get(id), verify: false)));
                        return;
                    }
                    _plugin.Playnite.Notifications.Add(new NotificationMessage(
                        "rommnite-update-" + game.Id,
                        $"An update for {game.Name} is on your RomM server ({changes}). Click to update it now, or right-click the game later.",
                        NotificationType.Info,
                        () => Start(_plugin.Playnite.Database.Games.Get(id), verify: false)));
                }
            }
            catch (Exception ex)
            {
                s_log.Info($"[Updates] Could not check {game.Name} for an update: {ex.Message}");
            }
        }

        // A game installed before updates were followed: its kind told from its folder, its files
        // taken as the server's now (its zip's index unknown: "Verify files" reads its files).
        private void Baseline(Game game, RomMRom rom, List<InstalledManifest.ServerFile> now)
        {
            string installDir = PlaynitePath.Resolve(_plugin.Playnite, game.InstallDirectory);
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return;
            RomMRevision revision = RomMRevisionFactory.Build(rom, _plugin.Settings.RomMHost);
            if (revision == null)
                return;
            string kept = Path.Combine(installDir, revision.FileName ?? "");
            string kind = revision.DownloadAsArchive ? "folder"
                : File.Exists(kept) ? "file"
                : (revision.FileName ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip"
                : null;
            if (kind == null)
                return;
            new InstalledManifest
            {
                Kind = kind,
                RomId = rom.Id,
                InstallDir = installDir,
                FilePath = kind == "file" ? kept : null,
                RomPath = rom.FullPath,
                Files = now,
                InstalledUtc = DateTime.UtcNow,
            }.Save(_folder, game.Id);
        }

        // ---- Updating ------------------------------------------------------------------------------

        /// <summary>
        /// The game brought up to the server's files (or, <paramref name="verify"/>, its files on
        /// disk checked against them and any that differ fetched again), in the downloads queue.
        /// </summary>
        public void Start(Game game, bool verify)
        {
            if (game == null)
                return;
            InstalledManifest manifest = Get(game.Id);
            if (manifest == null || string.IsNullOrEmpty(manifest.InstallDir) || !Directory.Exists(manifest.InstallDir))
            {
                _plugin.Playnite.Dialogs.ShowMessage($"{game.Name} can't be {(verify ? "verified" : "updated")}: RomMnite doesn't know what it was installed from. Reinstall it once to make that possible.", "RomMnite");
                return;
            }
            if (_plugin.DownloadQueueController.Find(game.Id) != null)
                return;   // already in the queue
            _plugin.Playnite.Notifications.Remove("rommnite-update-" + game.Id);
            Game current = game;
            var req = new DownloadRequest
            {
                GameId = game.Id,
                GameName = game.Name,
                InstallDir = manifest.InstallDir,
                GamePath = Path.Combine(manifest.InstallDir, PartName),
                KeepInstallOnFailure = true,
                Work = (item, ct) => Run(current, manifest, verify, item, ct),
                OnFailed = ex => _plugin.Playnite.Notifications.Add(
                    "rommnite-update-failed-" + current.Id,
                    $"{current.Name} could not be {(verify ? "verified" : "updated")}: {ex.Message} The game is as it was before, except for any files already replaced.",
                    NotificationType.Error),
            };
            _plugin.DownloadQueueController.Enqueue(req);
        }

        private async Task Run(Game game, InstalledManifest manifest, bool verify, DownloadQueueItem item, CancellationToken ct)
        {
            item.SetStatus(DownloadStatus.Downloading, verify ? "Checking files..." : "Looking for changes...");
            item.SetProgress(0, 1, true);
            RomMRom rom = _plugin.FetchRom(manifest.RomId.ToString(CultureInfo.InvariantCulture))
                ?? throw new InvalidOperationException("RomM could not be asked for the game's files.");
            List<InstalledManifest.ServerFile> now = (rom.Files ?? new List<RomMFile>()).Select(InstalledManifest.ServerFile.From).ToList();

            string done;
            switch (manifest.Kind)
            {
                case "zip":
                    done = await UpdateZip(game, rom, manifest, verify, item, ct).ConfigureAwait(false);
                    break;
                case "folder":
                    done = await UpdateFolder(game, rom, manifest, now, verify, item, ct).ConfigureAwait(false);
                    break;
                default:
                    done = await UpdateFile(game, rom, manifest, now, verify, item, ct).ConfigureAwait(false);
                    break;
            }

            manifest.Files = now;
            manifest.RomPath = rom.FullPath ?? manifest.RomPath;
            manifest.UpdateAvailable = null;
            manifest.NotifiedFor = InstalledManifest.Signature(now);
            manifest.Skipped = 0;
            manifest.Save(_folder, game.Id);
            s_log.Info($"[Updates] {game.Name}: {done}");
            _plugin.Playnite.Notifications.Add("rommnite-update-done-" + game.Id, $"{game.Name}: {done}", NotificationType.Info);
        }

        // ---- One zip, unpacked ----------------------------------------------------------------------

        private async Task<string> UpdateZip(Game game, RomMRom rom, InstalledManifest manifest, bool verify, DownloadQueueItem item, CancellationToken ct)
        {
            string url = RomMRevisionFactory.Build(rom, _plugin.Settings.RomMHost)?.DownloadURL
                ?? throw new InvalidOperationException("The game has no file on the server.");
            string installDir = manifest.InstallDir;

            // The new zip's index, by ranges; without them (or for a zip that is not one), all of it.
            RemoteZip.CentralDirectory dir = null;
            List<RemoteZip.Entry> entries = null;
            long total = 0;
            try
            {
                Tuple<byte[], long> tail = await Range(url, -RemoteZip.TailLength, 0, ct).ConfigureAwait(false);
                total = tail.Item2;
                dir = tail.Item1 == null ? null : RemoteZip.FindDirectory(tail.Item1, total - tail.Item1.Length, total);
                if (dir != null && dir.Zip64RecordOffset >= 0)
                {
                    byte[] record = (await Range(url, dir.Zip64RecordOffset, 56, ct).ConfigureAwait(false)).Item1;
                    RemoteZip.ReadZip64Record(record, 0, dir);
                }
                if (dir != null)
                    entries = RemoteZip.ParseDirectory((await Range(url, dir.Offset, dir.Size, ct).ConfigureAwait(false)).Item1);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                s_log.Info($"[Updates] {game.Name}: the zip's index could not be read from the server ({ex.Message}); downloading it whole.");
                entries = null;
            }
            if (entries == null)
                return await WholeZip(game, url, manifest, item, ct).ConfigureAwait(false);

            // What changed: by the old zip's index where it is known (and the file is there, at
            // its size), else by the file on disk's own CRC-32.
            var before = manifest.Entries.GroupBy(e => Norm(e.Name)).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            List<RemoteZip.Entry> files = entries.Where(e => !e.IsDirectory).ToList();
            var wanted = new List<RemoteZip.Entry>();
            long checkedBytes = 0, toCheck = verify || before.Count == 0 ? files.Sum(e => e.Size) : 0;
            foreach (RemoteZip.Entry entry in files)
            {
                ct.ThrowIfCancellationRequested();
                string local = RomMInstallPaths.ResolveWithin(installDir, entry.Name);
                var info = new FileInfo(local);
                bool need;
                if (!info.Exists || info.Length != entry.Size)
                    need = true;
                else if (!verify && before.TryGetValue(Norm(entry.Name), out InstalledManifest.ZipEntryRecord old))
                    need = old.Crc != entry.Crc || old.Size != entry.Size;
                else
                {
                    using (var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        need = RemoteZip.Crc32(stream) != entry.Crc;
                    checkedBytes += entry.Size;
                    item.SetStatus(DownloadStatus.Downloading, "Checking files...");
                    item.SetProgress(checkedBytes, Math.Max(1, toCheck), false);
                }
                if (need)
                    wanted.Add(entry);
            }
            var newNames = new HashSet<string>(files.Select(e => Norm(e.Name)), StringComparer.OrdinalIgnoreCase);
            List<string> gone = before.Keys.Where(n => !newNames.Contains(n)).ToList();

            if (wanted.Any(e => !RemoteZip.Readable(e)))
            {
                s_log.Info($"[Updates] {game.Name}: a changed file is packed in a way that can't be read alone; downloading the zip whole.");
                return await WholeZip(game, url, manifest, item, ct).ConfigureAwait(false);
            }

            List<RemoteZip.Span> spans = RemoteZip.Plan(wanted, entries, dir.Offset);
            long bytes = spans.Sum(s => s.Length);
            long got = 0;
            string part = Path.Combine(installDir, PartName);
            try
            {
                foreach (RemoteZip.Span span in spans)
                {
                    ct.ThrowIfCancellationRequested();
                    long before0 = got;
                    await RangeToFile(url, span.Start, span.Length, part, n =>
                    {
                        item.SetStatus(DownloadStatus.Downloading, "Updating...");
                        item.SetProgress(before0 + n, Math.Max(1, bytes), false);
                    }, ct).ConfigureAwait(false);
                    got += span.Length;
                    using (var stream = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        foreach (RemoteZip.Entry entry in span.Entries)
                            Unpack(stream, span, entry, installDir);
                    }
                }
            }
            finally
            {
                TryDelete(part);
            }
            foreach (string name in gone)
                TryDelete(RomMInstallPaths.ResolveWithin(installDir, name));

            manifest.Entries = files.Select(e => new InstalledManifest.ZipEntryRecord { Name = e.Name, Crc = e.Crc, Size = e.Size }).ToList();
            if (wanted.Count == 0 && gone.Count == 0)
                return verify ? "all files are as on the server." : "already up to date.";
            return $"{(verify ? "repaired" : "updated")}: {wanted.Count} file{(wanted.Count == 1 ? "" : "s")} fetched ({Size(bytes)} of {Size(total)}), {gone.Count} removed.";
        }

        // One file of the zip, from the span fetched (in <paramref name="stream"/>), into the game's folder.
        private static void Unpack(Stream stream, RemoteZip.Span span, RemoteZip.Entry entry, string installDir)
        {
            long at = entry.LocalHeaderOffset - span.Start;
            var header = new byte[30];
            stream.Position = at;
            if (stream.Read(header, 0, 30) != 30)
                throw new InvalidDataException($"{entry.Name}: its header was not fetched whole.");
            long dataAt = RemoteZip.DataStart(header, 0, entry.LocalHeaderOffset) - span.Start;

            string target = RomMInstallPaths.ResolveWithin(installDir, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temp = target + ".rommnite-new";
            uint crc = 0;
            long length = 0;
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                Stream input;
                IDisposable owner = null;
                if (entry.Method == 0)
                    input = new SubStream(stream, dataAt, entry.CompressedSize);
                else
                {
                    // SharpCompress reads the local header and the data after it, whatever the method.
                    var reader = ZipReader.Open(new SubStream(stream, at, span.End - entry.LocalHeaderOffset));
                    owner = reader;
                    if (!reader.MoveToNextEntry())
                        throw new InvalidDataException($"{entry.Name}: not found where the index says.");
                    input = reader.OpenEntryStream();
                }
                try
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        crc = RemoteZip.Crc32(crc, buffer, 0, read);
                        length += read;
                    }
                }
                finally
                {
                    input.Dispose();
                    owner?.Dispose();
                }
            }
            if (crc != entry.Crc || length != entry.Size)
            {
                TryDelete(temp);
                throw new InvalidDataException($"{entry.Name} did not come out as the zip says (CRC or size differs).");
            }
            Replace(temp, target);
        }

        // The whole zip downloaded and unpacked over the game; files the new one lacks removed.
        private async Task<string> WholeZip(Game game, string url, InstalledManifest manifest, DownloadQueueItem item, CancellationToken ct)
        {
            string installDir = manifest.InstallDir;
            string part = Path.Combine(installDir, PartName);
            try
            {
                long size = await DownloadToFile(url, part, item, ct).ConfigureAwait(false);
                var files = new List<InstalledManifest.ZipEntryRecord>();
                item.SetStatus(DownloadStatus.Extracting, "Extracting...");
                using (var archive = ArchiveFactory.Open(part))
                {
                    var all = archive.Entries.Where(e => !e.IsDirectory).ToList();
                    int done = 0;
                    foreach (var entry in all)
                    {
                        ct.ThrowIfCancellationRequested();
                        string target = RomMInstallPaths.ResolveWithin(installDir, entry.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string temp = target + ".rommnite-new";
                        using (var input = entry.OpenEntryStream())
                        using (var output = File.Create(temp))
                            input.CopyTo(output);
                        Replace(temp, target);
                        files.Add(new InstalledManifest.ZipEntryRecord { Name = entry.Key, Crc = unchecked((uint)entry.Crc), Size = entry.Size });
                        item.SetProgress(++done, Math.Max(1, all.Count), false);
                    }
                }
                var keep = new HashSet<string>(files.Select(f => Norm(f.Name)), StringComparer.OrdinalIgnoreCase);
                int removed = 0;
                foreach (InstalledManifest.ZipEntryRecord old in manifest.Entries.Where(e => !keep.Contains(Norm(e.Name))))
                {
                    if (TryDelete(RomMInstallPaths.ResolveWithin(installDir, old.Name)))
                        removed++;
                }
                manifest.Entries = files;
                return $"updated: the whole zip downloaded ({Size(size)}), {removed} file{(removed == 1 ? "" : "s")} removed.";
            }
            finally
            {
                TryDelete(part);
            }
        }

        // ---- A folder of files ----------------------------------------------------------------------

        private async Task<string> UpdateFolder(Game game, RomMRom rom, InstalledManifest manifest, List<InstalledManifest.ServerFile> now,
            bool verify, DownloadQueueItem item, CancellationToken ct)
        {
            string installDir = manifest.InstallDir;
            InstalledManifest.Changes changes = InstalledManifest.Compare(manifest.Files, now);
            var fetch = new HashSet<int>(changes.Added.Concat(changes.Changed).Select(f => f.Id));
            // Missing or of another size on disk (and, verifying, by hash where RomM has one).
            foreach (InstalledManifest.ServerFile file in now)
            {
                string local = RomMInstallPaths.ResolveWithin(installDir, manifest.LocalPathOf(file));
                var info = new FileInfo(local);
                if (!info.Exists || info.Length != file.Size)
                    fetch.Add(file.Id);
                else if (verify && !string.IsNullOrEmpty(file.Crc))
                {
                    using (var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (!string.Equals(RemoteZip.Crc32(stream).ToString("x8"), file.Crc.PadLeft(8, '0'), StringComparison.OrdinalIgnoreCase))
                            fetch.Add(file.Id);
                    }
                }
            }

            long bytes = now.Where(f => fetch.Contains(f.Id)).Sum(f => f.Size);
            if (fetch.Count > 0)
            {
                string name = Uri.EscapeDataString(rom.FileName ?? "game");
                string url = _plugin.CombineUrl(_plugin.Settings.RomMHost,
                    $"api/roms/{rom.Id}/content/{name}?file_ids={string.Join(",", fetch.OrderBy(i => i))}");
                string part = Path.Combine(installDir, PartName);
                try
                {
                    await DownloadToFile(url, part, item, ct).ConfigureAwait(false);
                    item.SetStatus(DownloadStatus.Extracting, "Extracting...");
                    using (var archive = ArchiveFactory.Open(part))
                    {
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                        {
                            ct.ThrowIfCancellationRequested();
                            // Not the playlist RomM adds when the folder has none: the game's own stays.
                            if (entry.Key.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                                && !now.Any(f => string.Equals(manifest.LocalPathOf(f), entry.Key, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            string target = RomMInstallPaths.ResolveWithin(installDir, entry.Key);
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            string temp = target + ".rommnite-new";
                            using (var input = entry.OpenEntryStream())
                            using (var output = File.Create(temp))
                                input.CopyTo(output);
                            Replace(temp, target);
                        }
                    }
                }
                finally
                {
                    TryDelete(part);
                }
            }
            int removed = 0;
            foreach (InstalledManifest.ServerFile file in changes.Removed)
            {
                if (TryDelete(RomMInstallPaths.ResolveWithin(installDir, manifest.LocalPathOf(file))))
                    removed++;
            }
            if (fetch.Count == 0 && removed == 0)
                return verify ? "all files are as on the server." : "already up to date.";
            return $"{(verify ? "repaired" : "updated")}: {fetch.Count} file{(fetch.Count == 1 ? "" : "s")} fetched ({Size(bytes)}), {removed} removed.";
        }

        // ---- One file, kept as it is ----------------------------------------------------------------

        private async Task<string> UpdateFile(Game game, RomMRom rom, InstalledManifest manifest, List<InstalledManifest.ServerFile> now,
            bool verify, DownloadQueueItem item, CancellationToken ct)
        {
            string target = manifest.FilePath;
            if (string.IsNullOrEmpty(target))
                throw new InvalidOperationException("Where the game's file is is not known.");
            InstalledManifest.ServerFile file = now.FirstOrDefault();
            var info = new FileInfo(target);
            bool changed = InstalledManifest.Compare(manifest.Files, now).Any || !info.Exists || (file != null && info.Length != file.Size);
            if (!changed && verify && file != null && !string.IsNullOrEmpty(file.Crc))
            {
                using (var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    changed = !string.Equals(RemoteZip.Crc32(stream).ToString("x8"), file.Crc.PadLeft(8, '0'), StringComparison.OrdinalIgnoreCase);
            }
            if (!changed)
                return verify ? "its file is as on the server." : "already up to date.";
            string url = RomMRevisionFactory.Build(rom, _plugin.Settings.RomMHost)?.DownloadURL
                ?? throw new InvalidOperationException("The game has no file on the server.");
            string temp = target + ".rommnite-new";
            long size = await DownloadToFile(url, temp, item, ct).ConfigureAwait(false);
            Replace(temp, target);
            return $"{(verify ? "repaired" : "updated")}: downloaded again ({Size(size)}).";
        }

        // ---- The server ----------------------------------------------------------------------------

        /// <summary>
        /// Bytes of a file on the server: <paramref name="length"/> from <paramref name="start"/>,
        /// or (start negative) the last -start bytes. With the file's whole length. Item1 null
        /// when the server sends the whole file instead (it does not serve ranges).
        /// </summary>
        /// <summary>
        /// A file on the server's size (Item1, 0 when the server does not say), and, for a zip,
        /// what it unpacks to (Item2: the sum of its files' sizes, from its index read by ranges;
        /// null when it is not a zip or the server does not serve ranges). Nothing is downloaded
        /// beyond the zip's index.
        /// </summary>
        internal static async Task<Tuple<long, long?>> RemoteSizes(string url, bool zip, CancellationToken ct)
        {
            Tuple<byte[], long> tail = await Range(url, zip ? -RemoteZip.TailLength : -1, 0, ct).ConfigureAwait(false);
            long total = tail.Item2;
            if (!zip || tail.Item1 == null || total <= 0)
                return Tuple.Create(total, (long?)null);
            RemoteZip.CentralDirectory dir = RemoteZip.FindDirectory(tail.Item1, total - tail.Item1.Length, total);
            if (dir == null)
                return Tuple.Create(total, (long?)null);
            if (dir.Zip64RecordOffset >= 0)
            {
                byte[] record = (await Range(url, dir.Zip64RecordOffset, 56, ct).ConfigureAwait(false)).Item1;
                if (record == null)
                    return Tuple.Create(total, (long?)null);
                RemoteZip.ReadZip64Record(record, 0, dir);
            }
            byte[] cd = (await Range(url, dir.Offset, dir.Size, ct).ConfigureAwait(false)).Item1;
            if (cd == null)
                return Tuple.Create(total, (long?)null);
            long unpacked = RemoteZip.ParseDirectory(cd).Where(e => !e.IsDirectory).Sum(e => e.Size);
            return Tuple.Create(total, (long?)unpacked);
        }

        private static async Task<Tuple<byte[], long>> Range(string url, long start, long length, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Range = start < 0
                    ? new RangeHeaderValue(null, -start)
                    : new RangeHeaderValue(start, start + length - 1);
                using (var response = await HttpClientSingleton.Instance.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        response.EnsureSuccessStatusCode();
                        return Tuple.Create<byte[], long>(null, response.Content.Headers.ContentLength ?? 0);
                    }
                    long total = response.Content.Headers.ContentRange?.Length ?? 0;
                    byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return Tuple.Create(data, total);
                }
            }
        }

        // A range of the file written to a file (spans can be large), reporting the bytes so far.
        private static async Task RangeToFile(string url, long start, long length, string path, Action<long> progress, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Range = new RangeHeaderValue(start, start + length - 1);
                using (var response = await HttpClientSingleton.Instance.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                        throw new InvalidOperationException("The server stopped serving parts of the file.");
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        var buffer = new byte[1 << 20];
                        long written = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                            written += read;
                            progress(written);
                        }
                        if (written != length)
                            throw new IOException($"The connection closed at {written} of {length} bytes.");
                    }
                }
            }
        }

        // The whole file to a file, with progress on the queue's entry; its size.
        private static async Task<long> DownloadToFile(string url, string path, DownloadQueueItem item, CancellationToken ct)
        {
            using (var response = await HttpClientSingleton.Instance.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[1 << 20];
                    long written = 0;
                    int read;
                    item.SetStatus(DownloadStatus.Downloading, "Updating...");
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        written += read;
                        item.SetProgress(written, Math.Max(1, total > 0 ? total : written), total <= 0);
                    }
                    return written;
                }
            }
        }

        // ---- Files ----------------------------------------------------------------------------------

        private static string Norm(string name) => (name ?? "").Replace('\\', '/').TrimStart('/');

        // A new file in place of the old (which may be read-only).
        private static void Replace(string temp, string target)
        {
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
                File.Delete(target);
            }
            File.Move(temp, target);
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string Size(long bytes)
        {
            if (bytes >= 1L << 30)
                return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
            if (bytes >= 1L << 20)
                return (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
            return Math.Max(1, bytes / 1024).ToString(CultureInfo.CurrentCulture) + " KB";
        }
    }
}
