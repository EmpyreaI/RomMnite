using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using RomM.Models.RomM.Save;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Saves
{
    /// <summary>
    /// Cloud saves with and without the server.
    ///
    /// At start-up, once the library is up (<see cref="StartBackground"/>): every installed game's
    /// saves are synced, newest played first, so saves made on another device are on this one
    /// before a game is even picked. The server is asked first whether it is there at all; when
    /// it is not, nothing is tried game by game (each would wait for the network to give up).
    ///
    /// Offline: a sync that fails is noted (SavePending.json), with whether there is progress on
    /// this computer the server has not got (a sync after playing, or while playing, failed).
    /// Those games show "Waiting to sync" under Cloud Saves. The server is asked every minute
    /// whether it is back; once it is, the noted games are synced (their local progress uploaded,
    /// as RomM's negotiation sees a save changed since this device last synced it) and the rest
    /// of the plugin is told (<see cref="Reconnected"/>).
    ///
    /// Nothing is lost when both sides moved on (played offline here, and on another device
    /// meanwhile): RomM calls that a conflict, and the newer save wins as before, but the side
    /// that loses is kept first, in SaveBackups\ in the plugin's data (the last ten of each game),
    /// and a notification says where.
    /// </summary>
    internal partial class SaveSyncService
    {
        private static readonly TimeSpan OfflineCheckEvery = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan RetryFailingEvery = TimeSpan.FromMinutes(5);
        private const int BackupsKept = 10;

        public sealed class PendingEntry
        {
            public bool LocalProgress { get; set; }   // progress here the server has not got
            public DateTime SinceUtc { get; set; }
            public DateTime NextTryUtc { get; set; }
            public string Reason { get; set; }
        }

        private readonly object _pendingLock = new object();
        private Dictionary<Guid, PendingEntry> _pending;
        private Timer _offlineTimer;
        private int _checking;
        private bool _wasOffline;

        /// <summary>The server is reachable again after it was not (raised off the UI thread).</summary>
        public event Action Reconnected;

        private string PendingFile => Path.Combine(_romM.Playnite.Paths.ExtensionsDataPath, _romM.Id.ToString(), "SavePending.json");
        private string BackupsFolder => Path.Combine(_romM.Playnite.Paths.ExtensionsDataPath, _romM.Id.ToString(), "SaveBackups");

        private Dictionary<Guid, PendingEntry> Pending
        {
            get
            {
                lock (_pendingLock)
                {
                    if (_pending == null)
                    {
                        try
                        {
                            _pending = File.Exists(PendingFile)
                                ? JsonConvert.DeserializeObject<Dictionary<Guid, PendingEntry>>(File.ReadAllText(PendingFile))
                                : null;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[SaveSync] Could not read the saves waiting to sync: {ex.Message}");
                        }
                        _pending = _pending ?? new Dictionary<Guid, PendingEntry>();
                    }
                    return _pending;
                }
            }
        }

        private void SavePendingFile()
        {
            try
            {
                string json;
                lock (_pendingLock)
                    json = JsonConvert.SerializeObject(Pending);
                File.WriteAllText(PendingFile, json);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[SaveSync] Could not keep the saves waiting to sync: {ex.Message}");
            }
        }

        /// <summary>The game has progress here that is waiting for the server.</summary>
        public bool HasUnsyncedProgress(Guid gameId)
        {
            lock (_pendingLock)
                return Pending.TryGetValue(gameId, out PendingEntry entry) && entry.LocalProgress;
        }

        // After each sync of a game whose saves are synced: noted when it failed, let go when it
        // went through.
        private void NoteOutcome(Game game, SyncOutcome outcome, bool allowDownload)
        {
            bool failed = outcome.Failed > 0;
            // Skipped because the save here is another account's: nothing learned either way.
            if (!failed && MayUseLocalSave != null && !MayUseLocalSave(game))
                return;
            bool changed = false;
            bool hadProgress = false;
            lock (_pendingLock)
            {
                Pending.TryGetValue(game.Id, out PendingEntry entry);
                if (!failed)
                {
                    if (entry != null)
                    {
                        hadProgress = entry.LocalProgress;
                        Pending.Remove(game.Id);
                        changed = true;
                    }
                }
                else
                {
                    // Progress here: a sync while the game runs, or after it was played.
                    bool progress = !allowDownload || _sessionStarts.ContainsKey(game.Id);
                    if (entry == null)
                    {
                        entry = new PendingEntry { SinceUtc = DateTime.UtcNow };
                        Pending[game.Id] = entry;
                    }
                    entry.LocalProgress |= progress;
                    entry.Reason = outcome.Message;
                    entry.NextTryUtc = DateTime.UtcNow + RetryFailingEvery;
                    changed = true;
                }
            }
            if (!changed)
                return;
            SavePendingFile();
            if (failed)
                Logger.Info($"[SaveSync] {game.Name}: waiting to sync ({outcome.Message ?? "the server could not be reached"}).");
            else if (hadProgress)
            {
                Logger.Info($"[SaveSync] {game.Name}: the progress made offline is synced with RomM.");
                Notify("romm-offline-saves-" + game.Id, $"{game.Name}: the progress saved while RomM couldn't be reached is now synced.", NotificationType.Info);
            }
        }

        // ---- Start-up and reconnecting ----------------------------------------------------------

        /// <summary>
        /// Once the library is up: every installed game's saves synced (when the server is there),
        /// then the server watched for while anything waits to sync.
        /// </summary>
        public void StartBackground()
        {
            if (_offlineTimer != null)
                return;
            _offlineTimer = new Timer(_ => CheckPending(), null, OfflineCheckEvery, OfflineCheckEvery);
            Task.Run(() => SyncInstalledAtStart());
        }

        private void SyncInstalledAtStart()
        {
            try
            {
                List<Game> games = _romM.Playnite.Database.Games
                    .Where(g => g.PluginId == _romM.Id && g.IsInstalled && !g.IsRunning && !g.IsLaunching && SyncsSaves(g))
                    .OrderByDescending(g => g.LastActivity ?? DateTime.MinValue)
                    .ToList();
                bool reachable = ServerReachable();
                if (!reachable)
                    _wasOffline = true;   // watched for from now on (CheckPending)
                if (games.Count == 0)
                    return;
                if (!reachable)
                {
                    _wasOffline = true;
                    lock (_pendingLock)
                    {
                        foreach (Game game in games.Where(g => !Pending.ContainsKey(g.Id)))
                            Pending[game.Id] = new PendingEntry { SinceUtc = DateTime.UtcNow, Reason = "RomM could not be reached at start-up." };
                    }
                    SavePendingFile();
                    Logger.Info($"[SaveSync] RomM is not reachable: the saves of {games.Count} installed games are synced once it is.");
                    return;
                }
                int downloaded = 0, uploaded = 0, failed = 0;
                foreach (Game game in games)
                {
                    Game current = _romM.Playnite.Database.Games.Get(game.Id);
                    if (current == null || current.IsRunning || current.IsLaunching)
                        continue;   // started meanwhile: its own launch syncs it
                    SyncOutcome outcome = Sync(current);
                    downloaded += outcome.Downloaded;
                    uploaded += outcome.Uploaded;
                    failed += outcome.Failed;
                }
                Logger.Info($"[SaveSync] Start-up check of {games.Count} installed games: {downloaded} saves downloaded, {uploaded} uploaded, {failed} failed.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SaveSync] The start-up check of the saves failed: {ex.Message}");
            }
        }

        // Every minute while anything waits: the server asked whether it is back, and then the
        // waiting games synced (each failing one at most every five minutes).
        private void CheckPending()
        {
            if (Interlocked.Exchange(ref _checking, 1) == 1)
                return;
            try
            {
                List<Guid> waiting;
                lock (_pendingLock)
                    waiting = Pending.Keys.ToList();
                bool wasOffline = _wasOffline;
                if (waiting.Count == 0 && !wasOffline)
                    return;
                if (!ServerReachable())
                {
                    _wasOffline = true;
                    return;
                }
                _wasOffline = false;
                if (wasOffline)
                {
                    Logger.Info("[SaveSync] RomM is reachable again.");
                    try
                    {
                        Reconnected?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[SaveSync] A reconnect listener failed: {ex.Message}");
                    }
                }
                foreach (Guid id in waiting)
                {
                    PendingEntry entry;
                    lock (_pendingLock)
                        Pending.TryGetValue(id, out entry);
                    if (entry == null)
                        continue;
                    // Just back: everything at once; otherwise a failing one waits its turn.
                    if (!wasOffline && DateTime.UtcNow < entry.NextTryUtc)
                        continue;
                    Game game = _romM.Playnite.Database.Games.Get(id);
                    if (game == null || game.PluginId != _romM.Id || !SyncsSaves(game))
                    {
                        lock (_pendingLock)
                            Pending.Remove(id);
                        SavePendingFile();
                        continue;
                    }
                    if (game.IsRunning || game.IsLaunching)
                        continue;   // its session's own syncs and the one at its end see to it
                    Sync(game);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SaveSync] Could not sync the saves waiting for RomM: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        }

        /// <summary>
        /// Ludusavi has just been fetched (Handlers.LudusaviInstaller): the installed Windows
        /// games' saves, which could not be found without it, synced now.
        /// </summary>
        public void SyncLudusaviGames()
        {
            Task.Run(() =>
            {
                try
                {
                    foreach (Game game in _romM.Playnite.Database.Games
                        .Where(g => g.PluginId == _romM.Id && g.IsInstalled && !g.IsRunning && !g.IsLaunching && SyncsSaves(g))
                        .ToList())
                    {
                        if (UsesLudusavi(game))
                            Sync(game);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[SaveSync] Could not sync the Windows games' saves after Ludusavi was fetched: {ex.Message}");
                }
            });
        }

        /// <summary>Whether the RomM server answers (its heartbeat, within five seconds).</summary>
        internal bool ServerReachable()
        {
            string host = Settings?.RomMHost;
            if (string.IsNullOrEmpty(host))
                return false;
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (var response = HttpClientSingleton.Instance.GetAsync(RomMUrl.Combine(host, "api/heartbeat"), cts.Token).GetAwaiter().GetResult())
                    return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // ---- Keeping the side that loses a conflict -----------------------------------------------

        /// <summary>
        /// Before a conflict is settled most-recent-wins: the save that is about to be replaced
        /// kept in SaveBackups\ (this computer's, or the server's when this computer's wins).
        /// </summary>
        private void KeepLosingSide(RomMSyncOperation op, SaveTarget target, bool serverWins, CancellationToken ct)
        {
            string gameName = _romM.Playnite.Database.Games.FirstOrDefault(g => g.PluginId == _romM.Id
                && RomMGameId.TryParse(g.GameId, out int id, out string _) && id == op.RomId)?.Name ?? ("rom " + op.RomId);
            try
            {
                string folder = Path.Combine(BackupsFolder, Safe(gameName) + " (" + op.RomId + ")");
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
                string kept;
                if (serverWins)
                {
                    if (!target.Exists)
                        return;
                    using (var prepared = target.PrepareUpload())
                    {
                        kept = Path.Combine(folder, $"{stamp} this computer - {Safe(prepared.FileName)}");
                        File.Copy(prepared.FilePath, kept, true);
                    }
                }
                else
                {
                    if (!op.SaveId.HasValue)
                        return;
                    byte[] bytes;
                    using (var response = HttpClientSingleton.Instance.GetAsync(
                        RomMUrl.Combine(Settings.RomMHost, $"api/saves/{op.SaveId.Value}/content"), ct).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    }
                    kept = Path.Combine(folder, $"{stamp} RomM - {Safe(op.FileName ?? "save")}");
                    File.WriteAllBytes(kept, bytes);
                }
                Trim(folder);
                Logger.Info($"[SaveSync] {gameName}: saves on both sides changed; the {(serverWins ? "one on this computer" : "one on RomM")} is kept in {kept}.");
                Notify("romm-save-conflict-" + op.RomId,
                    $"{gameName}: its save changed both here and on another device. The newer one is in use; the other was kept in {folder}.",
                    NotificationType.Info);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SaveSync] {gameName}: could not keep the save a conflict replaces ({ex.Message}).");
            }
        }

        private static void Trim(string folder)
        {
            foreach (FileInfo old in new DirectoryInfo(folder).GetFiles().OrderByDescending(f => f.CreationTimeUtc).Skip(BackupsKept))
            {
                try
                {
                    old.Delete();
                }
                catch
                {
                    // In use: next time.
                }
            }
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        private void Notify(string id, string text, NotificationType type)
        {
            try
            {
                _romM.Playnite.Notifications.Add(id, text, type);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[SaveSync] {ex.Message}");
            }
        }
    }
}
