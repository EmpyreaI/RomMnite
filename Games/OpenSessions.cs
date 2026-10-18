using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace RomM.Games
{
    /// <summary>
    /// The games playing right now, kept on disk (OpenSessions.json in the plugin's data) from
    /// OnGameStarted to OnGameStopped. Playnite drops a session when it quits while the game is
    /// still running (GamesEditor.Dispose only marks the game stopped: no playtime added, no
    /// OnGameStopped), and so does a crash or a killed Playnite. What is left in the file on the
    /// next start is such a session, and is finished then (RomM.RecoverSessions): its playtime,
    /// its save synced, its play session sent.
    ///
    /// While a game runs the entry's "seen" time is renewed every minute, so a session cut short
    /// by Playnite ending is counted to about when it ended, not to the next start.
    /// </summary>
    public sealed class OpenSessions
    {
        public sealed class Entry
        {
            public Guid GameId { get; set; }
            public string Name { get; set; }
            public DateTime StartedUtc { get; set; }
            public DateTime SeenUtc { get; set; }
            /// <summary>The process Playnite started (0 when it started none it knows of, e.g. by a URL).</summary>
            public int ProcessId { get; set; }
            /// <summary>That process's start time (UTC ticks), so a later process given the same id is not taken for it.</summary>
            public long ProcessStartTicks { get; set; }
            /// <summary>The game's playtime when it started: unchanged later means Playnite did not add this session.</summary>
            public ulong PlaytimeAtStart { get; set; }
            /// <summary>The account signed in when it started.</summary>
            public string AccountKey { get; set; }
        }

        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(1);

        private readonly string _file;
        private readonly object _lock = new object();
        private Dictionary<Guid, Entry> _open = new Dictionary<Guid, Entry>();
        private readonly Timer _timer;

        public OpenSessions(string file)
        {
            _file = file;
            try
            {
                if (File.Exists(_file))
                    _open = JsonConvert.DeserializeObject<Dictionary<Guid, Entry>>(File.ReadAllText(_file)) ?? new Dictionary<Guid, Entry>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sessions] Could not read the open sessions ({_file}): {ex.Message}");
            }
            _timer = new Timer(_ => Beat(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>The sessions left open by the last run, taken out of the file (to be finished).</summary>
        public List<Entry> TakeLeftOver()
        {
            lock (_lock)
            {
                List<Entry> left = _open.Values.ToList();
                _open.Clear();
                Save();
                return left;
            }
        }

        /// <summary>The games playing now (by Playnite's account of it).</summary>
        public List<Entry> Running()
        {
            lock (_lock)
                return _open.Values.ToList();
        }

        public bool IsOpen(Guid gameId)
        {
            lock (_lock)
                return _open.ContainsKey(gameId);
        }

        public void Started(Guid gameId, string name, int processId, ulong playtime, string accountKey)
        {
            DateTime now = DateTime.UtcNow;
            var entry = new Entry
            {
                GameId = gameId,
                Name = name,
                StartedUtc = now,
                SeenUtc = now,
                ProcessId = processId,
                ProcessStartTicks = StartTicks(processId),
                PlaytimeAtStart = playtime,
                AccountKey = accountKey,
            };
            lock (_lock)
            {
                _open[gameId] = entry;
                Save();
                _timer.Change(Heartbeat, Heartbeat);
            }
        }

        public void Stopped(Guid gameId)
        {
            lock (_lock)
            {
                if (!_open.Remove(gameId))
                    return;
                Save();
                if (_open.Count == 0)
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>
        /// The entry's process, if it is still the one that was started (same id and start time);
        /// null otherwise. The caller disposes it.
        /// </summary>
        public static Process StillRunning(Entry entry)
        {
            if (entry == null || entry.ProcessId <= 0)
                return null;
            Process process = null;
            try
            {
                process = Process.GetProcessById(entry.ProcessId);
                if (process.HasExited || (entry.ProcessStartTicks != 0 && process.StartTime.ToUniversalTime().Ticks != entry.ProcessStartTicks))
                {
                    process.Dispose();
                    return null;
                }
                return process;
            }
            catch
            {
                process?.Dispose();
                return null;
            }
        }

        private void Beat()
        {
            lock (_lock)
            {
                if (_open.Count == 0)
                    return;
                DateTime now = DateTime.UtcNow;
                foreach (Entry entry in _open.Values)
                    entry.SeenUtc = now;
                Save();
            }
        }

        private static long StartTicks(int processId)
        {
            if (processId <= 0)
                return 0;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                    return process.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                return 0;   // gone already, or not ours to read
            }
        }

        // Under _lock.
        private void Save()
        {
            try
            {
                if (_open.Count == 0)
                {
                    if (File.Exists(_file))
                        File.Delete(_file);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                string temp = _file + ".new";
                File.WriteAllText(temp, JsonConvert.SerializeObject(_open));
                if (File.Exists(_file))
                    File.Delete(_file);
                File.Move(temp, _file);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sessions] Could not write the open sessions ({_file}): {ex.Message}");
            }
        }
    }
}
