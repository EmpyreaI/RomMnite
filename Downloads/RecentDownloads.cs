using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Downloads
{
    /// <summary>
    /// When each game last finished downloading on this computer. The library's Recent Games
    /// shelf puts a game just downloaded at its front (as Steam does) without calling it played
    /// (Playnite's "last played" is left alone), and the Downloads page lists them under
    /// Completed until cleared. Kept on disk; the newest 100.
    /// </summary>
    public class RecentDownloads
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        // Kept in the same file, under an id no game has: when Completed was last cleared.
        private static readonly Guid ClearedKey = Guid.Empty;

        private readonly string _path;
        private readonly object _lock = new object();
        private Dictionary<Guid, DateTime> _when;
        // Games the user added to the library themselves (the Store's Add to Library), and when:
        // kept beside, in a file of its own, so they count for Recent Games but are not downloads.
        private Dictionary<Guid, DateTime> _added;
        private string AddedPath => Path.Combine(Path.GetDirectoryName(_path) ?? "", Path.GetFileNameWithoutExtension(_path) + ".Added.json");

        /// <summary>Something was marked or cleared.</summary>
        public event Action Changed;

        public RecentDownloads(string path)
        {
            _path = path;
        }

        private Dictionary<Guid, DateTime> Load()
        {
            if (_when != null)
                return _when;
            try
            {
                _when = File.Exists(_path)
                    ? JsonConvert.DeserializeObject<Dictionary<Guid, DateTime>>(File.ReadAllText(_path)) ?? new Dictionary<Guid, DateTime>()
                    : new Dictionary<Guid, DateTime>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Downloads] Could not read the recent downloads: {ex.Message}");
                _when = new Dictionary<Guid, DateTime>();
            }
            return _when;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(_when));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Downloads] Could not keep the recent downloads: {ex.Message}");
            }
        }

        /// <summary>A game finished downloading now.</summary>
        public void Mark(Guid gameId)
        {
            lock (_lock)
            {
                var when = Load();
                when[gameId] = DateTime.UtcNow;
                if (when.Count > 101)
                    _when = when.OrderByDescending(p => p.Key == ClearedKey ? DateTime.MaxValue : p.Value).Take(101).ToDictionary(p => p.Key, p => p.Value);
                Save();
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// When the game last finished downloading here, or was added to the library by the user,
        /// whichever was later (local time); or null.
        /// </summary>
        public DateTime? When(Guid gameId)
        {
            lock (_lock)
            {
                DateTime? downloaded = gameId != ClearedKey && Load().TryGetValue(gameId, out DateTime utc) ? utc : (DateTime?)null;
                DateTime? added = LoadAdded().TryGetValue(gameId, out DateTime addedUtc) ? addedUtc : (DateTime?)null;
                DateTime? latest = downloaded == null ? added : added == null ? downloaded : (added.Value > downloaded.Value ? added : downloaded);
                return latest?.ToLocalTime();
            }
        }

        /// <summary>
        /// A game was added to the library by the user, or started downloading, now: at the front
        /// of Recent Games (not listed as a finished download).
        /// </summary>
        public void MarkAdded(Guid gameId)
        {
            lock (_lock)
            {
                var added = LoadAdded();
                added[gameId] = DateTime.UtcNow;
                if (added.Count > 100)
                    _added = added.OrderByDescending(p => p.Value).Take(100).ToDictionary(p => p.Key, p => p.Value);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(AddedPath));
                    File.WriteAllText(AddedPath, JsonConvert.SerializeObject(_added));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Downloads] Could not keep the recently added games: {ex.Message}");
                }
            }
            Changed?.Invoke();
        }

        private Dictionary<Guid, DateTime> LoadAdded()
        {
            if (_added != null)
                return _added;
            try
            {
                _added = File.Exists(AddedPath)
                    ? JsonConvert.DeserializeObject<Dictionary<Guid, DateTime>>(File.ReadAllText(AddedPath)) ?? new Dictionary<Guid, DateTime>()
                    : new Dictionary<Guid, DateTime>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Downloads] Could not read the recently added games: {ex.Message}");
                _added = new Dictionary<Guid, DateTime>();
            }
            return _added;
        }

        /// <summary>The games finished since Completed was last cleared, newest first (local times).</summary>
        public List<KeyValuePair<Guid, DateTime>> Completed()
        {
            lock (_lock)
            {
                var when = Load();
                DateTime cleared = when.TryGetValue(ClearedKey, out DateTime c) ? c : DateTime.MinValue;
                return when.Where(p => p.Key != ClearedKey && p.Value > cleared)
                    .OrderByDescending(p => p.Value)
                    .Select(p => new KeyValuePair<Guid, DateTime>(p.Key, p.Value.ToLocalTime()))
                    .ToList();
            }
        }

        /// <summary>Empties Completed. The games keep their place on Recent Games.</summary>
        public void ClearCompleted()
        {
            lock (_lock)
            {
                Load()[ClearedKey] = DateTime.UtcNow;
                Save();
            }
            Changed?.Invoke();
        }
    }
}
