using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace RomM.Saves
{
    /// <summary>
    /// Remembers, per game, when cloud saves last synced and whether that worked, so the status
    /// shown in the library survives a restart. One small JSON file.
    /// </summary>
    internal sealed class CloudStatusStore
    {
        public sealed class Entry
        {
            public DateTime LastSyncUtc { get; set; }
            public bool Ok { get; set; }
            public string Message { get; set; }
            // A standing problem that replaces the usual status text, with its explanation.
            public string Headline { get; set; }
            public string Detail { get; set; }
        }

        private readonly object _lock = new object();
        private readonly string _path;
        private Dictionary<Guid, Entry> _entries;

        public CloudStatusStore(string path)
        {
            _path = path;
        }

        private Dictionary<Guid, Entry> Entries()
        {
            if (_entries != null)
                return _entries;

            try
            {
                if (File.Exists(_path))
                    _entries = JsonConvert.DeserializeObject<Dictionary<Guid, Entry>>(File.ReadAllText(_path));
            }
            catch
            {
                // An unreadable file only costs the remembered statuses; they come back with the next sync.
            }
            return _entries ?? (_entries = new Dictionary<Guid, Entry>());
        }

        public Entry Get(Guid gameId)
        {
            lock (_lock)
                return Entries().TryGetValue(gameId, out Entry entry) ? entry : null;
        }

        public void Record(Guid gameId, bool ok, string message, string headline = null, string detail = null)
        {
            lock (_lock)
            {
                Entries()[gameId] = new Entry { LastSyncUtc = DateTime.UtcNow, Ok = ok, Message = message, Headline = headline, Detail = detail };
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, JsonConvert.SerializeObject(_entries));
                }
                catch
                {
                    // The status is still held in memory for this session.
                }
            }
        }
    }
}
