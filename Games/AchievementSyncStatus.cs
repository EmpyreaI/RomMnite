using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace RomM.Games
{
    /// <summary>
    /// How each game's achievements last synced with RomM, as the cloud saves' status does for
    /// saves. Two kinds of sync report here:
    ///
    ///   RetroAchievements  the set and the account's progress read from RomM (RefreshAchievements,
    ///                      after RomM was asked to fetch new unlocks from RetroAchievements when a
    ///                      game was played)
    ///   Emulators          RPCS3's trophies and Xenia's achievements, merged with the account's
    ///                      note on RomM (EmulatorAchievements.SyncNote)
    ///
    /// The last result of each game is kept per account (AchievementSync.json in its folder);
    /// a sync under way is known only while it runs.
    /// </summary>
    internal sealed class AchievementSyncStatus
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        public sealed class Entry
        {
            public bool Ok { get; set; }
            public DateTime AtUtc { get; set; }
            public string Message { get; set; }
            public string Source { get; set; }   // "ra" or the emulator's kind ("rpcs3", "xenia")
        }

        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<Guid, int> _running = new ConcurrentDictionary<Guid, int>();
        private Dictionary<Guid, Entry> _last = new Dictionary<Guid, Entry>();
        private string _file;

        /// <summary>A game's status changed (on any thread).</summary>
        public event Action<Guid> Changed;

        /// <summary>The account's own record (switched with the account).</summary>
        public void UseFile(string path)
        {
            lock (_lock)
            {
                _file = path;
                _last = new Dictionary<Guid, Entry>();
                try
                {
                    if (File.Exists(path))
                        _last = JsonConvert.DeserializeObject<Dictionary<Guid, Entry>>(File.ReadAllText(path)) ?? new Dictionary<Guid, Entry>();
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Achievements] Could not read the sync status: {ex.Message}");
                }
            }
        }

        public void Begin(Guid gameId)
        {
            _running.AddOrUpdate(gameId, 1, (id, n) => n + 1);
            Changed?.Invoke(gameId);
        }

        /// <summary>Begun, but there was nothing to sync after all (no set): no result kept.</summary>
        public void Cancel(Guid gameId) => End(gameId, null);

        public void Succeeded(Guid gameId, string source) =>
            End(gameId, new Entry { Ok = true, AtUtc = DateTime.UtcNow, Source = source });

        public void Failed(Guid gameId, string source, string message) =>
            End(gameId, new Entry { Ok = false, AtUtc = DateTime.UtcNow, Source = source, Message = message });

        private void End(Guid gameId, Entry result)
        {
            _running.AddOrUpdate(gameId, 0, (id, n) => Math.Max(0, n - 1));
            if (_running.TryGetValue(gameId, out int left) && left == 0)
                _running.TryRemove(gameId, out int _);
            if (result != null)
            {
                lock (_lock)
                {
                    _last[gameId] = result;
                    try
                    {
                        if (_file != null)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(_file));
                            File.WriteAllText(_file, JsonConvert.SerializeObject(_last));
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Achievements] Could not keep the sync status: {ex.Message}");
                    }
                }
            }
            Changed?.Invoke(gameId);
        }

        /// <summary>Whether the game has synced before (with any result).</summary>
        public bool Known(Guid gameId)
        {
            lock (_lock)
                return _last.ContainsKey(gameId);
        }

        /// <summary>The status as the theme shows it: a word or two, and a sentence.</summary>
        public void Describe(Guid gameId, out string headline, out string detail)
        {
            if (_running.ContainsKey(gameId))
            {
                headline = "Syncing...";
                detail = "Achievements are syncing with RomM.";
                return;
            }
            Entry last;
            lock (_lock)
                _last.TryGetValue(gameId, out last);
            if (last == null)
            {
                headline = "Not synced yet";
                detail = "This game's achievements have not been synced with RomM from this computer.";
                return;
            }
            string when = last.AtUtc.ToLocalTime().ToString("g");
            string what = last.Source == "ra"
                ? "RetroAchievements progress, through RomM"
                : last.Source == "rpcs3" ? "Trophies, kept on RomM for this account"
                : "Achievements, kept on RomM for this account";
            if (last.Ok)
            {
                headline = "Up to date";
                detail = $"{what}: last synced {when}.";
            }
            else
            {
                headline = "Sync failed";
                detail = string.IsNullOrEmpty(last.Message)
                    ? $"{what}: the last sync, {when}, failed. See extensions.log."
                    : $"{what}: the last sync, {when}, failed: {last.Message}";
            }
        }
    }

    /// <summary>
    /// The selected game's achievement sync status, for themes: {PluginSettings Plugin=RomM,
    /// Path=AchievementSync.Text} (and Visible, Detail), beside the cloud saves' own.
    /// </summary>
    public class AchievementSyncSummary : ObservableObject
    {
        private bool _visible;
        private string _text = "";
        private string _detail = "";

        /// <summary>Whether the selected game has achievements to sync at all.</summary>
        public bool Visible { get => _visible; set => SetValue(ref _visible, value); }

        /// <summary>"Up to date", "Syncing...", "Sync failed", "Not synced yet".</summary>
        public string Text { get => _text; set => SetValue(ref _text, value); }

        /// <summary>A sentence with what is synced and when, or why it failed.</summary>
        public string Detail { get => _detail; set => SetValue(ref _detail, value); }
    }
}
