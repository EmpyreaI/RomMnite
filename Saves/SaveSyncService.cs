using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using RomM.Models.RomM.Save;
using RomM.Saves.Handlers;
using RomM.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace RomM.Saves
{
    /// <summary>
    /// Synchronises a game's local save with the RomM server using the API sync mode (see romm PRs
    /// #3137 / #3479): register a device once, POST /sync/negotiate to let the server decide
    /// upload / download / conflict / no_op per save, execute the returned operations, then POST
    /// the session complete. Conflicts are resolved most-recent-wins.
    ///
    /// Where a save lives, and whether it is one file or a packed directory, is the business of
    /// an <see cref="ISaveHandler"/>; this class only moves bytes. RetroArch is the handler that
    /// exists today.
    /// </summary>
    internal partial class SaveSyncService
    {
        private const string DeviceClient = "playnite";

        // RomM's timestamps do not always carry a zone, and a naive "2024-05-17T09:30:00" would
        // otherwise deserialise as DateTimeKind.Unspecified and be read as local time -- shifting
        // every comparison against a file's real UTC write time by the machine's offset. Fixing the
        // kind where the value is born means no call site has to remember to.
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        private readonly IRomM _romM;
        private readonly SaveHandlerRegistry _handlers = new SaveHandlerRegistry();
        private readonly object _deviceLock = new object();

        // One sync at a time per ROM: the post-play sync and the manual menu action can be in
        // flight together, and two negotiate sessions writing the same file would clobber each
        // other.
        private readonly ConcurrentDictionary<int, object> _romLocks = new ConcurrentDictionary<int, object>();

        public SaveSyncService(IRomM romM)
        {
            _romM = romM;
        }

        /// <summary>
        /// Whether the save on disk for a game may be synced as the signed-in account's. Unset:
        /// always. When it says no, the save on disk is another account's (see SaveAccounts) and
        /// is neither uploaded nor replaced until that game is launched and the saves swapped.
        /// </summary>
        public Func<Game, bool> MayUseLocalSave { get; set; }

        /// <summary>Where a game's save is, for SaveAccounts; null with a reason when unknown.</summary>
        internal SaveTarget TargetFor(Game game, out string reason) => ResolveTarget(game, out reason);

        /// <summary>The emulator a game launches with (and the save handler for it, if any).</summary>
        internal SaveEmulatorResolution EmulatorFor(Game game) => ResolveEmulator(game);

        /// <summary>The lock syncs of this ROM hold, so a swap does not run under one.</summary>
        internal object LockFor(int romId) => _romLocks.GetOrAdd(romId, _ => new object());

        private ILogger Logger => _romM.Logger;
        private SettingsViewModel Settings => _romM.Settings;

        // Cloud saves are on for every RomM game, unless switched off for it.
        private bool SyncsSaves(Game game) => _romM.SaveSyncOff?.IsOn(game) != false;

        /// <summary>The emulators sync covers today, for the message shown when none applied.</summary>
        public string SupportedEmulators => string.Join(", ", _handlers.SupportedEmulatorTags);

        #region Cloud status

        private readonly ConcurrentDictionary<Guid, byte> _syncing = new ConcurrentDictionary<Guid, byte>();
        private CloudStatusStore _statusStore;

        private CloudStatusStore StatusStore => _statusStore ?? (_statusStore = new CloudStatusStore(
            Path.Combine(_romM.Playnite.Paths.ExtensionsDataPath, _romM.Id.ToString(), "cloud-status.json")));

        /// <summary>Raised with a game's id whenever its cloud save status may have changed.</summary>
        public event Action<Guid> StatusChanged;

        private void RaiseStatusChanged(Guid gameId)
        {
            try
            {
                StatusChanged?.Invoke(gameId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SaveSync] A cloud status listener failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The cloud save status to show for a game: a short headline and a longer detail line.
        /// Both are null when cloud saves do not apply to it -- not a RomM game, the option is
        /// off, or its emulator is not one whose saves can be synced.
        /// </summary>
        public void DescribeStatus(Game game, out string headline, out string detail)
        {
            headline = null;
            detail = null;

            try
            {
                if (game == null || game.PluginId != _romM.Id || !SyncsSaves(game))
                    return;
                if (!SavesSupported(game, ResolveEmulator(game), out bool known))
                {
                    if (!known)
                        return;
                    headline = "Not supported for this platform";
                    detail = "Cloud saves are not supported for this platform: its emulator's saves cannot be synced with RomM.";
                    return;
                }

                if (_syncing.ContainsKey(game.Id))
                {
                    headline = "Syncing...";
                    detail = "Cloud saves are syncing with RomM.";
                    return;
                }

                if (HasUnsyncedProgress(game.Id))
                {
                    headline = "Waiting to sync";
                    detail = "Progress saved while RomM couldn't be reached. It's kept on this computer and uploads as soon as RomM is back.";
                    return;
                }

                var last = StatusStore.Get(game.Id);
                if (last == null)
                {
                    headline = "Not synced yet";
                    detail = "This game's saves have not been synced with RomM from this computer.";
                    return;
                }

                string when = last.LastSyncUtc.ToLocalTime().ToString("g");
                if (!string.IsNullOrEmpty(last.Headline))
                {
                    headline = last.Headline;
                    detail = string.IsNullOrEmpty(last.Detail) ? $"Last checked {when}." : $"{last.Detail} Last checked {when}.";
                    return;
                }

                if (last.Ok)
                {
                    headline = "Up to date";
                    detail = $"Last synced with RomM {when}.";
                }
                else
                {
                    headline = "Sync failed";
                    detail = string.IsNullOrEmpty(last.Message)
                        ? $"The last sync, {when}, failed. See extensions.log."
                        : $"The last sync, {when}, failed: {last.Message}";
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SaveSync] Could not work out the cloud status of {game?.Name}: {ex.Message}");
            }
        }

        /// <summary>When the game's saves were last synced with RomM without a problem, if ever (on this computer).</summary>
        public DateTime? LastSyncedUtc(Guid gameId)
        {
            try
            {
                var last = StatusStore.Get(gameId);
                return last != null && last.Ok && string.IsNullOrEmpty(last.Headline) ? last.LastSyncUtc : (DateTime?)null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Whether the game's saves can be synced. known: whether that could be told -- a game
        /// whose platform has no emulator set up yet is told by the emulator setting it up would
        /// install (none: the platform is not supported here at all); a Windows game not set up
        /// yet is not told (its saves go through Ludusavi once it is).
        /// </summary>
        private bool SavesSupported(Game game, SaveEmulatorResolution resolution, out bool known)
        {
            known = true;
            if (resolution.Emulator != null)
                return resolution.Handler != null && !RetroArchSaveHandler.WritesNoSaves(resolution.Handler, resolution.Profile);

            Models.RomM.Platform.RomMPlatform platform = _romM.KnownPlatformOf(game);
            if (platform == null || Setup.EmulatorSetup.IsWindowsPlatform(platform) || _romM.EmulatorSetup == null)
            {
                known = false;
                return true;
            }
            Setup.EmulatorSetup.Plan plan = _romM.EmulatorSetup.PlanFor(platform.Id);
            if (plan == null)
                return false;   // no emulator for the platform: UNSUPPORTED PLATFORM in the store
            ISaveHandler handler = _handlers.Find(new Emulator(plan.Definition.Name) { BuiltInConfigId = plan.Definition.Id });
            if (handler == null)
                return false;
            if (handler is RetroArchSaveHandler && plan.Core != null)
                return !RetroArchSaveHandler.CoreWritesNoSaves(plan.Core);
            return true;
        }

        #endregion

        #region Play sessions

        // When each running game was launched, and the watcher on its save directory.
        private readonly ConcurrentDictionary<Guid, DateTime> _sessionStarts = new ConcurrentDictionary<Guid, DateTime>();
        private readonly ConcurrentDictionary<Guid, SaveWatch> _watches = new ConcurrentDictionary<Guid, SaveWatch>();

        /// <summary>
        /// Call once the pre-launch sync is done and the game is about to run. Stamps the session
        /// and, where the save can be watched, uploads it each time the game writes a new one.
        /// Uploads only: nothing is ever downloaded over a save the emulator has open.
        /// </summary>
        public void BeginSession(Game game)
        {
            try
            {
                if (game == null || game.PluginId != _romM.Id || !SyncsSaves(game))
                    return;

                _sessionStarts[game.Id] = DateTime.UtcNow;

                string directory = ResolveTarget(game, out string reason)?.WatchDirectory;
                if (string.IsNullOrEmpty(directory))
                {
                    Logger.Info($"[SaveSync] {game.Name}: not watching for saves during play. " +
                                (reason ?? "This emulator's saves are only synced at launch and close."));
                    return;
                }

                Directory.CreateDirectory(directory);
                var watch = new SaveWatch(directory, () => Sync(game, default(CancellationToken), allowDownload: false));
                _watches.AddOrUpdate(game.Id, watch, (id, previous) =>
                {
                    previous.Dispose();
                    return watch;
                });
                Logger.Info($"[SaveSync] Watching {directory} for new saves while {game.Name} runs.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SaveSync] Could not watch saves for {game?.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Call when the game has stopped: stops watching, runs the closing sync, then forgets the
        /// session so a later sync does not mistake another game's saves for this one's.
        /// </summary>
        public SyncOutcome EndSession(Game game)
        {
            if (game != null && _watches.TryRemove(game.Id, out SaveWatch watch))
                watch.Dispose();

            try
            {
                return Sync(game);
            }
            finally
            {
                if (game != null)
                    _sessionStarts.TryRemove(game.Id, out DateTime _);
            }
        }

        #endregion

        public class SyncOutcome
        {
            public bool Applicable { get; set; }
            public int Uploaded { get; set; }
            public int Downloaded { get; set; }
            public int Conflicts { get; set; }
            public int Failed { get; set; }
            public string Message { get; set; }
            // A standing problem reported by the save itself; see SaveTarget.StatusHeadline.
            public string StatusHeadline { get; set; }
            public string StatusDetail { get; set; }
        }

        /// <summary>Where the Ludusavi working folders and custom game names are kept.</summary>
        internal string LudusaviDirectory => Path.Combine(_romM.Playnite.Paths.ExtensionsDataPath, _romM.Id.ToString(), "Ludusavi");

        /// <summary>Whether this game's saves are handled through Ludusavi.</summary>
        public bool UsesLudusavi(Game game)
        {
            try
            {
                return game != null && game.PluginId == _romM.Id && ResolveEmulator(game).Handler is Handlers.LudusaviSaveHandler;
            }
            catch
            {
                return false;
            }
        }

        public string GetLudusaviTitle(Game game) => LudusaviTitles.Get(LudusaviDirectory, game.Id);

        public void SetLudusaviTitle(Game game, string title)
        {
            LudusaviTitles.Set(LudusaviDirectory, game.Id, title);

            // Drop the local backup copy made under the old name: it may hold a different game's
            // saves, and everything in that folder is treated as this game's. Ludusavi rebuilds
            // it on the next sync. The actual saves are not in this folder and are not touched.
            try
            {
                string workDir = Path.Combine(LudusaviDirectory, game.Id.ToString());
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SaveSync] Could not clear the old Ludusavi backup of {game.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs a full negotiate + apply cycle for a single game. Safe to call off the UI thread.
        /// Never throws; failures are logged and reflected in the returned <see cref="SyncOutcome"/>.
        ///
        /// <paramref name="cancellationToken"/> bounds the whole cycle rather than any one request,
        /// which is what the launch path needs: an unreachable RomM would otherwise cost the
        /// HTTP client's default timeout per request before the game is allowed to start.
        /// </summary>
        public SyncOutcome Sync(Game game, CancellationToken cancellationToken = default(CancellationToken), bool allowDownload = true)
        {
            // Background loading (the Store's, the artwork's) waits while saves sync.
            using (Foreground.Busy())
                return SyncTracked(game, cancellationToken, allowDownload);
        }

        private SyncOutcome SyncTracked(Game game, CancellationToken cancellationToken, bool allowDownload)
        {
            bool tracked = game != null && game.PluginId == _romM.Id && SyncsSaves(game);
            if (tracked)
            {
                _syncing[game.Id] = 0;
                RaiseStatusChanged(game.Id);
            }

            var outcome = SyncCore(game, cancellationToken, allowDownload);

            if (tracked)
            {
                _syncing.TryRemove(game.Id, out byte _);
                if (outcome.Applicable || outcome.Failed > 0)
                    StatusStore.Record(game.Id, outcome.Failed == 0, outcome.Message, outcome.StatusHeadline, outcome.StatusDetail);
                // Offline: noted to sync once RomM is back (SaveSyncService.Offline).
                NoteOutcome(game, outcome, allowDownload);
                RaiseStatusChanged(game.Id);
            }

            return outcome;
        }

        private SyncOutcome SyncCore(Game game, CancellationToken cancellationToken, bool allowDownload)
        {
            var outcome = new SyncOutcome();
            try
            {
                if (game == null || game.PluginId != _romM.Id)
                {
                    return outcome;
                }

                if (!SyncsSaves(game))
                {
                    Logger.Info($"[SaveSync] {game.Name}: skipped, cloud saves are off for this game.");
                    return outcome;
                }

                if (!RomMGameId.TryParse(game.GameId, out int romId, out string _))
                {
                    Logger.Warn($"[SaveSync] {game?.Name} has a malformed GameId, skipping.");
                    return outcome;
                }

                if (MayUseLocalSave != null && !MayUseLocalSave(game))
                {
                    Logger.Info($"[SaveSync] {game.Name}: skipped, the save on this computer is another account's. " +
                                "It is swapped for this account's when the game is launched.");
                    outcome.Message = "The save on this computer is another account's; launch the game to switch to yours.";
                    return outcome;
                }

                lock (_romLocks.GetOrAdd(romId, _ => new object()))
                {
                    var target = ResolveTarget(game, out string reason);
                    if (target == null)
                    {
                        Logger.Info($"[SaveSync] {game.Name}: skipped, {reason}");
                        outcome.Message = reason;
                        return outcome;
                    }

                    Logger.Info($"[SaveSync] {game.Name}: {target.EmulatorTag}, local save {(target.Exists ? "found" : "not found")}, " +
                                $"{target.Describe()}{(allowDownload ? "" : " (game running: upload only)")}");

                    outcome.Applicable = true;

                    // Known by now: the line above has already made the target look at the save.
                    outcome.StatusHeadline = target.StatusHeadline;
                    outcome.StatusDetail = target.StatusDetail;

                    var deviceId = EnsureDeviceRegistered(cancellationToken);
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        outcome.Message = "Could not register this device with RomM (check token scopes).";
                        outcome.Failed++;
                        return outcome;
                    }

                    var negotiation = Negotiate(deviceId, romId, target, cancellationToken);
                    if (negotiation == null)
                    {
                        outcome.Message = "Save sync negotiation with RomM failed.";
                        outcome.Failed++;
                        return outcome;
                    }

                    // Negotiate may surface operations for saves we didn't report (e.g. created on another
                    // device). We only resolved a local path for THIS game, so apply only its operations;
                    // other ROMs are handled when their own games sync.
                    var operations = negotiation.Operations ?? new List<RomMSyncOperation>();
                    var mine = operations.Where(o => o.RomId == romId).ToList();
                    Logger.Info($"[SaveSync] {game.Name}: RomM answered with {operations.Count} operations, " +
                                $"{mine.Count} for this game: {(mine.Count == 0 ? "nothing to do" : string.Join(", ", mine.Select(o => o.Action)))}");
                    foreach (var op in operations.Where(o => o.RomId == romId))
                    {
                        ApplyOperation(op, deviceId, negotiation.SessionId, target, outcome, cancellationToken, allowDownload);
                    }

                    // No local save, and negotiate offered none. RomM reads a save this device
                    // already synced and no longer has as deleted on purpose, and only servers
                    // from 5.4.0 on can be asked not to. Here a missing save means lost, so the
                    // slot's newest save is fetched directly.
                    if (allowDownload && !target.Exists && outcome.Downloaded == 0 && outcome.Failed == 0)
                    {
                        RestoreMissing(game, romId, deviceId, negotiation.SessionId, target, outcome, cancellationToken);
                    }

                    CompleteSession(negotiation.SessionId,
                        outcome.Uploaded + outcome.Downloaded,
                        outcome.Failed,
                        cancellationToken);

                    Logger.Info($"[SaveSync] {game.Name}: {outcome.Uploaded} uploaded, {outcome.Downloaded} downloaded, " +
                                $"{outcome.Conflicts} conflicts, {outcome.Failed} failed.");
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warn($"[SaveSync] Sync of {game?.Name} hit its deadline and was abandoned.");
                outcome.Failed++;
                outcome.Message = "RomM did not answer in time.";
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[SaveSync] Unexpected failure syncing {game?.Name}.");
                outcome.Failed++;
                outcome.Message = ex.Message;
            }

            return outcome;
        }

        #region Negotiate / session

        private RomMSyncNegotiateResponse Negotiate(string deviceId, int romId, SaveTarget target, CancellationToken ct)
        {
            // A save missing here is treated as lost, not deleted: RomM is the source of truth,
            // so it is restored. Removing a save for good means deleting it in RomM as well.
            var payload = new RomMSyncNegotiatePayload
            {
                DeviceId = deviceId,
                RomIds = new List<int> { romId },
                RestoreUnlisted = true,
            };

            if (target.Exists)
            {
                payload.Saves.Add(new RomMClientSaveState
                {
                    RomId = romId,
                    FileName = target.FileName,
                    Slot = target.Slot,
                    Emulator = target.EmulatorTag,
                    ContentHash = target.ContentHash(),
                    UpdatedAt = target.UpdatedAtUtc,
                    FileSizeBytes = target.SizeBytes,
                });
            }

            var url = RomMUrl.Combine(Settings.RomMHost, "api/sync/negotiate");
            var body = PostJson(url, payload, ct);
            return body == null ? null : JsonConvert.DeserializeObject<RomMSyncNegotiateResponse>(body, JsonSettings);
        }

        private void CompleteSession(int sessionId, int completed, int failed, CancellationToken ct)
        {
            try
            {
                var url = RomMUrl.Combine(Settings.RomMHost, $"api/sync/sessions/{sessionId}/complete");
                PostJson(url, new RomMSyncCompletePayload
                {
                    OperationsCompleted = completed,
                    OperationsFailed = failed,
                }, ct);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[SaveSync] Failed to complete sync session {sessionId}.");
            }
        }

        #endregion

        #region Operation handling

        private void ApplyOperation(RomMSyncOperation op, string deviceId, int sessionId, SaveTarget target, SyncOutcome outcome, CancellationToken ct, bool allowDownload = true)
        {
            try
            {
                // A conflict is not a third kind of transfer: it resolves to one of the other two,
                // so it decides and then falls into the same dispatch.
                var action = op.Action;
                if (action == RomMSyncAction.Conflict)
                {
                    outcome.Conflicts++;
                    action = ResolveConflict(op, target);
                    // Both sides moved on: the one about to be replaced is kept first.
                    if (action == RomMSyncAction.Download && allowDownload)
                        KeepLosingSide(op, target, serverWins: true, ct: ct);
                    else if (action == RomMSyncAction.Upload)
                        KeepLosingSide(op, target, serverWins: false, ct: ct);
                }

                // While the game is running the emulator owns the save: replacing it underneath
                // would be ignored at best and corrupt it at worst.
                if (action == RomMSyncAction.Download && !allowDownload)
                {
                    Logger.Info($"[SaveSync] Skipping a download for rom {op.RomId} while the game is running.");
                    return;
                }

                switch (action)
                {
                    case RomMSyncAction.Upload:
                        if (Upload(op, deviceId, sessionId, target, ct))
                            outcome.Uploaded++;
                        else
                            outcome.Failed++;
                        break;

                    case RomMSyncAction.Download:
                        if (Download(op, deviceId, sessionId, target, ct))
                            outcome.Downloaded++;
                        else
                            outcome.Failed++;
                        break;

                    case RomMSyncAction.NoOp:
                    default:
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // The deadline is the whole cycle's, not this operation's: let it end the sync
                // rather than being counted as one more failed transfer.
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[SaveSync] Operation '{op.Action}' failed for save {op.SaveId} (rom {op.RomId}).");
                outcome.Failed++;
            }
        }

        /// <summary>
        /// Downloads the newest save RomM holds in this target's slot, for a game with no local
        /// save at all. A save written by a different emulator is left alone: its format would
        /// not be one this target can unpack.
        /// </summary>
        private void RestoreMissing(Game game, int romId, string deviceId, int sessionId, SaveTarget target, SyncOutcome outcome, CancellationToken ct)
        {
            try
            {
                var url = RomMUrl.Combine(Settings.RomMHost,
                    $"api/saves?rom_id={romId}&slot={WebUtility.UrlEncode(target.Slot)}");

                List<RomMServerSave> saves;
                using (var response = HttpClientSingleton.Instance.GetAsync(url, ct).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Warn($"[SaveSync] {game.Name}: could not list its saves on RomM (HTTP {(int)response.StatusCode}).");
                        return;
                    }

                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    saves = JsonConvert.DeserializeObject<List<RomMServerSave>>(body, JsonSettings) ?? new List<RomMServerSave>();
                }

                var newest = saves
                    .Where(s => s != null && string.Equals(s.Slot, target.Slot, StringComparison.OrdinalIgnoreCase))
                    .Where(s => string.IsNullOrEmpty(s.Emulator)
                                || string.Equals(s.Emulator, target.EmulatorTag, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.UpdatedAt ?? DateTime.MinValue)
                    .FirstOrDefault();

                if (newest == null)
                {
                    Logger.Info($"[SaveSync] {game.Name}: no local save, and RomM has none to restore " +
                                $"({saves.Count} listed for this game in slot \"{target.Slot}\").");
                    return;
                }

                Logger.Info($"[SaveSync] {game.Name}: no local save; restoring save {newest.Id} from RomM.");

                var op = new RomMSyncOperation
                {
                    Action = RomMSyncAction.Download,
                    RomId = romId,
                    SaveId = newest.Id,
                    ServerUpdatedAt = newest.UpdatedAt,
                };

                if (Download(op, deviceId, sessionId, target, ct))
                    outcome.Downloaded++;
                else
                    outcome.Failed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[SaveSync] {game.Name}: restoring its save from RomM failed.");
                outcome.Failed++;
            }
        }

        /// <summary>
        /// Most-recent-wins: whichever side was modified later overwrites the other. Returns the
        /// transfer that decision comes down to.
        /// </summary>
        private string ResolveConflict(RomMSyncOperation op, SaveTarget target)
        {
            var localTime = target.Exists ? (DateTime?)target.UpdatedAtUtc : null;
            var serverTime = op.ServerUpdatedAt;

            bool serverWins = serverTime.HasValue && (!localTime.HasValue || serverTime.Value > localTime.Value);

            Logger.Warn($"[SaveSync] Conflict for rom {op.RomId} ({op.Reason}); " +
                        $"resolving most-recent-wins -> {(serverWins ? "download" : "upload")}.");

            return serverWins ? RomMSyncAction.Download : RomMSyncAction.Upload;
        }

        private bool Upload(RomMSyncOperation op, string deviceId, int sessionId, SaveTarget target, CancellationToken ct)
        {
            if (!target.Exists)
            {
                Logger.Warn($"[SaveSync] Asked to upload rom {op.RomId} but there is no local save for it.");
                return false;
            }

            using (var prepared = target.PrepareUpload())
            using (var content = BuildSaveContent(prepared))
            {
                HttpResponseMessage response;
                if (op.SaveId.HasValue)
                {
                    var url = RomMUrl.Combine(Settings.RomMHost,
                        $"api/saves/{op.SaveId.Value}?device_id={WebUtility.UrlEncode(deviceId)}");
                    response = HttpClientSingleton.Instance.PutAsync(url, content, ct).GetAwaiter().GetResult();
                }
                else
                {
                    var url = RomMUrl.Combine(Settings.RomMHost,
                        $"api/saves?rom_id={op.RomId}&emulator={WebUtility.UrlEncode(target.EmulatorTag)}" +
                        $"&slot={WebUtility.UrlEncode(target.Slot)}" +
                        $"&device_id={WebUtility.UrlEncode(deviceId)}&session_id={sessionId}");
                    response = HttpClientSingleton.Instance.PostAsync(url, content, ct).GetAwaiter().GetResult();
                }

                using (response)
                {
                    response.EnsureSuccessStatusCode();
                }
            }

            return true;
        }

        private bool Download(RomMSyncOperation op, string deviceId, int sessionId, SaveTarget target, CancellationToken ct)
        {
            if (!op.SaveId.HasValue)
            {
                Logger.Warn($"[SaveSync] Download requested for rom {op.RomId} without a save id.");
                return false;
            }

            var url = RomMUrl.Combine(Settings.RomMHost,
                $"api/saves/{op.SaveId.Value}/content?device_id={WebUtility.UrlEncode(deviceId)}&session_id={sessionId}");

            byte[] bytes;
            using (var response = HttpClientSingleton.Instance.GetAsync(url, ct).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }

            // The handler decides what "apply" means -- overwrite a file, or unpack an archive over
            // the save directory. It also aligns the local timestamp with the server's so the next
            // negotiate sees the two sides as in sync rather than as a fresh local edit.
            try
            {
                target.ApplyDownload(bytes, op.ServerUpdatedAt);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[SaveSync] Could not write the downloaded save for rom {op.RomId}.");
                return false;
            }

            ConfirmDownloaded(op.SaveId.Value, deviceId, ct);
            return true;
        }

        private void ConfirmDownloaded(int saveId, string deviceId, CancellationToken ct)
        {
            try
            {
                var url = RomMUrl.Combine(Settings.RomMHost, $"api/saves/{saveId}/downloaded");
                PostJson(url, new { device_id = deviceId }, ct);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SaveSync] Could not confirm download of save {saveId}: {ex.Message}");
            }
        }

        private static HttpContent BuildSaveContent(PreparedUpload upload)
        {
            var fileContent = new ByteArrayContent(File.ReadAllBytes(upload.FilePath));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var form = new MultipartFormDataContent();
            form.Add(fileContent, "saveFile", upload.FileName);
            return form;
        }

        #endregion

        #region Device registration

        /// <summary>
        /// Registers this machine as a RomM device once per server, persisting the returned id in
        /// settings. A device id is only meaningful on the host that issued it, so pointing the
        /// plugin at a different RomM re-registers rather than sending an unknown id forever.
        /// </summary>
        private string EnsureDeviceRegistered(CancellationToken ct)
        {
            var host = Settings.RomMHost ?? string.Empty;

            if (HasDeviceFor(host))
                return Settings.SaveSyncDeviceId;

            lock (_deviceLock)
            {
                if (HasDeviceFor(host))
                    return Settings.SaveSyncDeviceId;

                try
                {
                    // SyncMode and AllowExisting keep the model's defaults; what they are and why
                    // is documented there.
                    var payload = new RomMDeviceCreate
                    {
                        Name = Environment.MachineName,
                        Platform = "Windows",
                        Client = DeviceClient,
                        ClientVersion = PluginVersion.Current,
                    };

                    var url = RomMUrl.Combine(host, "api/devices");
                    var body = PostJson(url, payload, ct);
                    if (body == null)
                        return null;

                    var created = JsonConvert.DeserializeObject<RomMDeviceCreateResponse>(body, JsonSettings);
                    if (created == null || string.IsNullOrEmpty(created.DeviceId))
                        return null;

                    Settings.SaveSyncDeviceId = created.DeviceId;
                    Settings.SaveSyncDeviceHost = host;
                    Settings.SaveSyncDeviceMachine = Environment.MachineName;
                    Settings.Persist();
                    Logger.Info($"[SaveSync] Registered device '{created.DeviceId}' with RomM.");
                    return created.DeviceId;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[SaveSync] Device registration failed.");
                    return null;
                }
            }
        }

        private bool HasDeviceFor(string host)
        {
            return !string.IsNullOrEmpty(Settings.SaveSyncDeviceId)
                   && string.Equals(Settings.SaveSyncDeviceHost, host, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Settings.SaveSyncDeviceMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Save location

        /// <summary>
        /// Finds the emulator this game's saves belong to, hands it to whichever handler recognises
        /// it, and lets that handler locate the save. Null when no emulator is set, none is
        /// supported, or the handler cannot work out a path -- <paramref name="reason"/> says
        /// which, because "somewhere in these three" is not something a user can act on.
        /// </summary>
        private SaveTarget ResolveTarget(Game game, out string reason)
        {
            reason = null;

            var contentPath = game.Roms?.FirstOrDefault()?.Path;
            if (string.IsNullOrEmpty(contentPath))
            {
                reason = $"{game.Name} has no ROM file for save sync to work from.";
                return null;
            }

            var resolution = ResolveEmulator(game);
            if (resolution.Emulator == null)
            {
                reason = $"{game.Name} has no emulator set. Choose one in the game's Actions, " +
                         "or map its platform under RomMnite settings.";
                return null;
            }

            if (resolution.Handler == null)
            {
                Logger.Info($"[SaveSync] No save handler for emulator '{resolution.Emulator.Name}', skipping {game.Name}.");
                reason = $"Save sync does not support {resolution.Emulator.Name} yet. " +
                         $"Supported: {SupportedEmulators}.";
                if (resolution.PassedOverMappingEmulator != null)
                {
                    reason += $" Its RomM platform mapping uses {resolution.PassedOverMappingEmulator.Name}: " +
                              "point the game's play action at it to sync its saves.";
                }
                return null;
            }

            if (resolution.FromMapping)
            {
                Logger.Info($"[SaveSync] {game.Name}'s play action names no emulator; " +
                            $"using {resolution.Emulator.Name} from its RomM platform mapping instead.");
            }

            var request = new SaveTargetRequest
            {
                Game = game,
                EmulatorInstallDir = PlaynitePath.Resolve(_romM.Playnite, resolution.Emulator.InstallDir),
                Profile = resolution.Profile,
                ContentPath = _romM.Playnite.ExpandGameVariables(game, contentPath),
                DataDirectory = Path.Combine(_romM.Playnite.Paths.ExtensionsDataPath, _romM.Id.ToString(), "SaveFolders"),
                SessionStartUtc = _sessionStarts.TryGetValue(game.Id, out DateTime sessionStart) ? sessionStart : (DateTime?)null,
                Logger = Logger,
            };
            var target = resolution.Handler.ResolveTarget(request);

            if (target == null)
            {
                reason = request.Reason ?? $"Could not work out where {resolution.Emulator.Name} keeps this game's saves.";
            }

            return target;
        }

        /// <summary>
        /// The play action decides, since it is what launches; the mapping fills in when the action
        /// names no emulator or no profile. See <see cref="SaveEmulatorResolver"/> for the rules;
        /// this half is only the Playnite lookups.
        /// </summary>
        private SaveEmulatorResolution ResolveEmulator(Game game)
        {
            var candidates = new List<SaveEmulatorCandidate>();

            var action = RomMPlayAction.Find(game.GameActions);
            SaveEmulatorCandidate fromAction = null;
            if (action != null && action.EmulatorId != Guid.Empty)
            {
                var emulator = _romM.Playnite.Database.Emulators?.Get(action.EmulatorId);
                fromAction = new SaveEmulatorCandidate
                {
                    Emulator = emulator,
                    Profile = ProfileOf(emulator, action.EmulatorProfileId),
                };
                candidates.Add(fromAction);
            }

            // Reading the mapping means reading the ROM's sidecar off disk, and this runs on the
            // pre-launch path, so it is skipped when the action can answer on its own -- which it
            // cannot if its emulator is unsupported, or if it names no profile for the handler to
            // take the core from. A platform two mappings cover records whichever import ran last,
            // which says nothing about this game, so its mapping is not consulted at all.
            if (fromAction?.Profile == null || _handlers.Find(fromAction.Emulator) == null)
            {
                var mapping = _romM.MappingFor(game);
                if (mapping != null && !Settings.HasRivalMapping(mapping))
                {
                    candidates.Add(new SaveEmulatorCandidate
                    {
                        FromMapping = true,
                        Emulator = mapping.Emulator,
                        Profile = mapping.EmulatorProfile,
                    });
                }
            }

            return SaveEmulatorResolver.Resolve(_handlers, candidates);
        }

        private static EmulatorProfile ProfileOf(Emulator emulator, string profileId)
        {
            if (emulator == null || string.IsNullOrEmpty(profileId))
                return null;

            return emulator.SelectableProfiles?.FirstOrDefault(p => p.Id == profileId);
        }

        #endregion

        #region HTTP helper

        private string PostJson(string url, object payload, CancellationToken ct)
        {
            var json = JsonConvert.SerializeObject(payload);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = HttpClientSingleton.Instance.PostAsync(url, content, ct).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    var error = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Logger.Error($"[SaveSync] POST {url} -> {(int)response.StatusCode}: {error}");
                    return null;
                }

                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        #endregion
    }
}
