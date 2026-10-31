using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;

namespace RomM.Games
{
    /// <summary>
    /// The Windows games installed on this computer whose program to start was not sure
    /// (WindowsLaunch) and has not been chosen yet: asked about at their first Play. This
    /// computer's alone (LaunchChoices.json in the extension's data), nothing on the server; each
    /// computer asks once.
    /// </summary>
    internal sealed class LaunchChoices
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private readonly string _path;
        private readonly object _lock = new object();
        private HashSet<Guid> _unsure;

        public LaunchChoices(string dataFolder)
        {
            _path = Path.Combine(dataFolder, "LaunchChoices.json");
        }

        private HashSet<Guid> Load()
        {
            if (_unsure != null)
                return _unsure;
            try
            {
                _unsure = File.Exists(_path)
                    ? JsonConvert.DeserializeObject<HashSet<Guid>>(File.ReadAllText(_path)) ?? new HashSet<Guid>()
                    : new HashSet<Guid>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Launch] Could not read {_path}: {ex.Message}");
                _unsure = new HashSet<Guid>();
            }
            return _unsure;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(_unsure));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Launch] Could not write {_path}: {ex.Message}");
            }
        }

        /// <summary>The game's program was a guess: ask at its first Play.</summary>
        public void MarkUnsure(Guid gameId)
        {
            lock (_lock)
            {
                if (Load().Add(gameId))
                    Save();
            }
        }

        public bool IsUnsure(Guid gameId)
        {
            lock (_lock)
                return Load().Contains(gameId);
        }

        /// <summary>Chosen (or nothing to choose any more: sure, uninstalled).</summary>
        public void Settled(Guid gameId)
        {
            lock (_lock)
            {
                if (Load().Remove(gameId))
                    Save();
            }
        }
    }
}
