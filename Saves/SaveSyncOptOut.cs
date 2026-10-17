using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves
{
    /// <summary>
    /// The games whose saves are kept off RomM. Cloud saves are always on otherwise; a game is
    /// switched off (and back on) from its menu. Kept by RomM game number on this computer
    /// (SaveSyncOff.json), so it outlives the game being removed and added again.
    /// </summary>
    internal sealed class SaveSyncOptOut
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private readonly string _path;
        private readonly object _lock = new object();
        private HashSet<int> _off;

        public SaveSyncOptOut(string dataFolder)
        {
            _path = Path.Combine(dataFolder, "SaveSyncOff.json");
        }

        private HashSet<int> Off()
        {
            if (_off != null)
                return _off;
            try
            {
                _off = File.Exists(_path)
                    ? new HashSet<int>(JsonConvert.DeserializeObject<List<int>>(File.ReadAllText(_path)) ?? new List<int>())
                    : new HashSet<int>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[SaveSync] Could not read which games have cloud saves off: {ex.Message}");
                _off = new HashSet<int>();
            }
            return _off;
        }

        /// <summary>Whether the game's saves are synced with RomM (any RomM game, unless switched off).</summary>
        public bool IsOn(Game game)
        {
            if (game == null || !RomMGameId.TryParse(game.GameId, out int romId, out string _))
                return true;
            lock (_lock)
                return !Off().Contains(romId);
        }

        /// <summary>Switches the games' cloud saves on or off.</summary>
        public void Set(IEnumerable<Game> games, bool on)
        {
            lock (_lock)
            {
                HashSet<int> off = Off();
                foreach (Game game in games)
                {
                    if (!RomMGameId.TryParse(game.GameId, out int romId, out string _))
                        continue;
                    if (on)
                        off.Remove(romId);
                    else
                        off.Add(romId);
                    s_log.Info($"[SaveSync] {game.Name}: cloud saves {(on ? "on" : "off")}.");
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, JsonConvert.SerializeObject(off.OrderBy(i => i).ToList()));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[SaveSync] Could not keep which games have cloud saves off: {ex.Message}");
                }
            }
        }
    }
}
