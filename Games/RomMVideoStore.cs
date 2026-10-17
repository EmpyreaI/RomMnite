using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;

namespace RomM.Games
{
    /// <summary>
    /// Remembers, per Playnite game, where on the RomM server its video is. Only the path is
    /// kept (never the video), relative to the server, so it survives a change of server address
    /// and travels with a portable Playnite folder.
    /// </summary>
    public class RomMVideoStore
    {
        private readonly string _file;
        private readonly object _lock = new object();
        private Dictionary<Guid, string> _paths = new Dictionary<Guid, string>();
        private bool _dirty;

        public RomMVideoStore(string file)
        {
            _file = file;
            try
            {
                if (File.Exists(_file))
                {
                    _paths = JsonConvert.DeserializeObject<Dictionary<Guid, string>>(File.ReadAllText(_file))
                        ?? new Dictionary<Guid, string>();
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn($"[Video] Could not read {_file}: {ex.Message}");
            }
        }

        public string Get(Guid gameId)
        {
            lock (_lock)
                return _paths.TryGetValue(gameId, out string path) ? path : null;
        }

        /// <summary>Records a game's video path; an empty path forgets the game.</summary>
        public void Set(Guid gameId, string path)
        {
            lock (_lock)
            {
                _paths.TryGetValue(gameId, out string current);
                if (string.IsNullOrEmpty(path))
                {
                    if (current != null)
                    {
                        _paths.Remove(gameId);
                        _dirty = true;
                    }
                }
                else if (current != path)
                {
                    _paths[gameId] = path;
                    _dirty = true;
                }
            }
        }

        public void Save()
        {
            try
            {
                string json;
                lock (_lock)
                {
                    if (!_dirty)
                        return;
                    json = JsonConvert.SerializeObject(_paths);
                    _dirty = false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllText(_file, json);
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn($"[Video] Could not save {_file}: {ex.Message}");
            }
        }
    }
}
