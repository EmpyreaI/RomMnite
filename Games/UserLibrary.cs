using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Games
{
    /// <summary>
    /// Which of the server's games the signed-in user has in their library, by RomM game number.
    /// One small file per user under the plugin's data ("Library\&lt;user&gt;.json"), so each
    /// account keeps its own list and the list travels with a portable Playnite.
    ///
    /// It is a record of choices, never a reason to delete: a game is only ever taken out of
    /// Playnite by the user removing it. If this file is lost, the games already in Playnite are
    /// simply written back into it at the next library update.
    /// </summary>
    public sealed class UserLibrary
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly string _folder;
        private readonly Func<string> _user;
        private readonly object _lock = new object();
        private string _loadedFor;
        private HashSet<int> _ids = new HashSet<int>();

        public UserLibrary(string folder, Func<string> user)
        {
            _folder = folder;
            _user = user;
        }

        // The file for whoever is signed in now; re-read when that changes.
        private string FileFor(out string key)
        {
            string user = (_user() ?? "").Trim();
            if (user.Length == 0 || user.Trim('-').Length == 0)
                user = "default";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                user = user.Replace(invalid, '_');
            key = user.ToLowerInvariant();
            return Path.Combine(_folder, key + ".json");
        }

        private void EnsureLoaded()
        {
            string file = FileFor(out string key);
            if (key == _loadedFor)
                return;

            _ids = new HashSet<int>();
            try
            {
                if (File.Exists(file))
                    _ids = new HashSet<int>(JsonConvert.DeserializeObject<List<int>>(File.ReadAllText(file)) ?? new List<int>());
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Library] Could not read {file}: {ex.Message}");
            }
            _loadedFor = key;
        }

        private void Save()
        {
            try
            {
                string file = FileFor(out string _);
                Directory.CreateDirectory(_folder);
                string temp = file + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(_ids.OrderBy(i => i).ToList()));
                if (File.Exists(file))
                    File.Delete(file);
                File.Move(temp, file);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Library] Could not save the library list: {ex.Message}");
            }
        }

        public bool Contains(int romId)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _ids.Contains(romId);
            }
        }

        public void Add(int romId) => AddMany(new[] { romId });

        public void AddMany(IEnumerable<int> romIds)
        {
            lock (_lock)
            {
                EnsureLoaded();
                bool changed = false;
                foreach (int id in romIds)
                    changed |= _ids.Add(id);
                if (changed)
                    Save();
            }
        }

        public void Remove(int romId)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (_ids.Remove(romId))
                    Save();
            }
        }
    }
}
