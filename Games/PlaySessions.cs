using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Games
{
    /// <summary>
    /// When each game was played and for how long, kept for a few weeks so the library can say
    /// how much a game was played lately ("Last two weeks: 13 min"), as Steam does. Playnite
    /// itself only keeps a game's total, so this only knows about sessions played since it was
    /// added. Kept in one small file, for every game in the library, not only RomM's.
    /// </summary>
    public class PlaySessions
    {
        private const int KeepDays = 60;

        public sealed class Session
        {
            public DateTime EndUtc { get; set; }
            public ulong Seconds { get; set; }
        }

        private static readonly ILogger s_log = LogManager.GetLogger();

        private string _path;
        private readonly object _lock = new object();
        private Dictionary<Guid, List<Session>> _data;

        /// <summary>A session was recorded (the game's id; on the recording thread).</summary>
        public event Action<Guid> Changed;

        public PlaySessions(string path)
        {
            _path = path;
        }

        /// <summary>Switches to another file (another account's sessions), as GameCollections.UseFile.</summary>
        public void UseFile(string path, string legacyPath = null)
        {
            lock (_lock)
            {
                if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _data != null)
                    return;
                AccountFiles.Adopt(path, legacyPath);
                _path = path;
                _data = null;
            }
        }

        private Dictionary<Guid, List<Session>> Data()
        {
            if (_data != null)
                return _data;
            try
            {
                if (File.Exists(_path))
                    _data = JsonConvert.DeserializeObject<Dictionary<Guid, List<Session>>>(File.ReadAllText(_path));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sessions] Could not read {_path}: {ex.Message}");
            }
            return _data ?? (_data = new Dictionary<Guid, List<Session>>());
        }

        /// <summary>Records a session of <paramref name="seconds"/> that has just ended.</summary>
        public void Add(Guid gameId, ulong seconds)
        {
            if (seconds == 0)
                return;
            lock (_lock)
            {
                var data = Data();
                if (!data.TryGetValue(gameId, out List<Session> list))
                    data[gameId] = list = new List<Session>();
                list.Add(new Session { EndUtc = DateTime.UtcNow, Seconds = seconds });

                // Old sessions are of no further use.
                DateTime cutoff = DateTime.UtcNow.AddDays(-KeepDays);
                foreach (Guid id in data.Keys.ToList())
                {
                    data[id].RemoveAll(s => s.EndUtc < cutoff);
                    if (data[id].Count == 0)
                        data.Remove(id);
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, JsonConvert.SerializeObject(data));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Sessions] Could not write {_path}: {ex.Message}");
                }
            }
            try
            {
                Changed?.Invoke(gameId);
            }
            catch
            {
                // A listener's own trouble.
            }
        }

        /// <summary>The game's sessions still kept (the last two months), oldest first.</summary>
        public List<Session> Of(Guid gameId)
        {
            lock (_lock)
            {
                return Data().TryGetValue(gameId, out List<Session> list)
                    ? list.OrderBy(s => s.EndUtc).Select(s => new Session { EndUtc = s.EndUtc, Seconds = s.Seconds }).ToList()
                    : new List<Session>();
            }
        }

        /// <summary>Seconds the game was played in the last <paramref name="days"/> days.</summary>
        public ulong PlayedWithin(Guid gameId, int days)
        {
            lock (_lock)
            {
                if (!Data().TryGetValue(gameId, out List<Session> list))
                    return 0;
                DateTime since = DateTime.UtcNow.AddDays(-days);
                ulong total = 0;
                foreach (Session session in list)
                {
                    if (session.EndUtc >= since)
                        total += session.Seconds;
                }
                return total;
            }
        }
    }
}
