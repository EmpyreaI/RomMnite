using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves
{
    /// <summary>
    /// Remembers which save folders belong to a game, for emulators that name them after a title
    /// id instead of the ROM (PPSSPP's SAVEDATA\ULUS10041...). One small JSON file per game. The
    /// names are learned two ways: from folders the emulator wrote to while the game ran, and from
    /// the folders inside a save downloaded from RomM.
    /// </summary>
    internal sealed class SaveFolderMap
    {
        private static readonly object s_lock = new object();
        private readonly string _path;

        public SaveFolderMap(string dataDirectory, Guid gameId)
        {
            _path = string.IsNullOrEmpty(dataDirectory) ? null : Path.Combine(dataDirectory, gameId + ".json");
        }

        public List<string> Load()
        {
            lock (s_lock)
            {
                try
                {
                    if (_path != null && File.Exists(_path))
                        return JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(_path)) ?? new List<string>();
                }
                catch
                {
                    // An unreadable map is the same as none: the names are learned again.
                }
                return new List<string>();
            }
        }

        /// <summary>Adds names to the map and returns the full set.</summary>
        public List<string> Add(IEnumerable<string> names)
        {
            lock (s_lock)
            {
                var all = Load();
                var added = (names ?? Enumerable.Empty<string>())
                    .Where(n => !string.IsNullOrEmpty(n) && !all.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (added.Count == 0 || _path == null)
                    return all;

                all.AddRange(added);
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(all));
                return all;
            }
        }
    }
}
