using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace RomM.Saves
{
    /// <summary>
    /// What was learned about sending saves too big for the server (SaveTrim): each server's
    /// upload limit, as it stated it when it turned a save away; and, for each game whose save
    /// went up trimmed, the digest of the save here then and of the copy sent. While the save here
    /// is unchanged, negotiate reports the sent copy's digest, which is what RomM holds, so the
    /// two are seen as in step rather than sent again on every sync.
    ///
    /// SaveTrims.json in the plugin's data.
    /// </summary>
    internal sealed class SaveTrimStore
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private Data _data;

        private sealed class Data
        {
            public Dictionary<string, long> Limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<int, Sent> Trimmed = new Dictionary<int, Sent>();
        }

        private sealed class Sent
        {
            public string Local;
            public string Uploaded;
        }

        public SaveTrimStore(string path)
        {
            _path = path;
        }

        private Data Load()
        {
            if (_data != null)
                return _data;
            try
            {
                if (File.Exists(_path))
                    _data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(_path));
            }
            catch
            {
                _data = null;
            }
            _data = _data ?? new Data();
            if (_data.Limits == null)
                _data.Limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            else
                _data.Limits = new Dictionary<string, long>(_data.Limits, StringComparer.OrdinalIgnoreCase);
            _data.Trimmed = _data.Trimmed ?? new Dictionary<int, Sent>();
            return _data;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
            catch
            {
                // Learned again at the next refusal.
            }
        }

        /// <summary>The server's upload limit in bytes, or 0 when not known.</summary>
        public long LimitFor(string host)
        {
            lock (_lock)
                return Load().Limits.TryGetValue(host ?? "", out long limit) ? limit : 0;
        }

        public void SetLimit(string host, long limit)
        {
            lock (_lock)
            {
                Load().Limits[host ?? ""] = limit;
                Save();
            }
        }

        /// <summary>The digest to report for the save here: the sent copy's while it is unchanged, else its own.</summary>
        public string AsSent(int romId, string local)
        {
            lock (_lock)
                return Load().Trimmed.TryGetValue(romId, out Sent sent) && sent != null && sent.Local == local ? sent.Uploaded : local;
        }

        public void Remember(int romId, string local, string uploaded)
        {
            lock (_lock)
            {
                Load().Trimmed[romId] = new Sent { Local = local, Uploaded = uploaded };
                Save();
            }
        }

        public void Forget(int romId)
        {
            lock (_lock)
            {
                if (Load().Trimmed.Remove(romId))
                    Save();
            }
        }
    }
}
