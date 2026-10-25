using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.AchievementViews
{
    /// <summary>One achievement as the plugin shows it.</summary>
    public class AchievementItem
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public int Points { get; set; }
        public string BadgeUrl { get; set; }
        public string BadgeLockedUrl { get; set; }
        public DateTime? UnlockedUtc { get; set; }
        public DateTime? UnlockedHardcoreUtc { get; set; }
        public int Order { get; set; }

        /// <summary>A hidden trophy or secret achievement not unlocked yet: shown as such.</summary>
        public bool Hidden { get; set; }

        [JsonIgnore]
        public bool Unlocked => UnlockedUtc.HasValue || UnlockedHardcoreUtc.HasValue;

        [JsonIgnore]
        public DateTime? When => UnlockedHardcoreUtc ?? UnlockedUtc;
    }

    /// <summary>A game's RetroAchievements set with the signed-in user's progress in it.</summary>
    public class GameAchievements
    {
        public int RAId { get; set; }

        /// <summary>Where the set comes from: null for RetroAchievements, else the emulator ("rpcs3", "xenia").</summary>
        public string Source { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<AchievementItem> Items { get; set; } = new List<AchievementItem>();

        /// <summary>
        /// Why the set cannot be earned on this computer (an installed Windows game whose copy
        /// keeps no achievements RomMnite can read), said over it; null when it can.
        /// </summary>
        public string Unavailable { get; set; }

        [JsonIgnore] public int Total => Items.Count;
        [JsonIgnore] public int Unlocked => Items.Count(i => i.Unlocked);
        [JsonIgnore] public int Points => Items.Sum(i => i.Points);
        [JsonIgnore] public int PointsUnlocked => Items.Where(i => i.Unlocked).Sum(i => i.Points);
    }

    /// <summary>
    /// Each game's achievements, kept as one small file per game under the plugin's data
    /// ("Achievements\&lt;game id&gt;.json"), written at library import and when a game is
    /// looked at, read by the achievement views. Replaces SuccessStory as the store.
    /// </summary>
    public class AchievementStore
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private string _folder;
        private readonly object _lock = new object();
        private readonly Dictionary<Guid, GameAchievements> _cache = new Dictionary<Guid, GameAchievements>();

        /// <summary>Raised (on any thread) after a game's achievements were saved.</summary>
        public event Action<Guid> Changed;

        /// <summary>Raised (on the calling thread) when the store switched to another account's.</summary>
        public event Action Reset;

        public AchievementStore(string folder)
        {
            _folder = folder;
        }

        /// <summary>
        /// Switches to another account's achievements. The first account to have its own takes
        /// the ones kept before accounts had their own (the folder is moved).
        /// </summary>
        public void UseFolder(string folder, string legacyFolder = null)
        {
            lock (_lock)
            {
                if (string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase))
                    return;
                try
                {
                    if (!string.IsNullOrEmpty(legacyFolder) && Directory.Exists(legacyFolder) && !Directory.Exists(folder))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(folder));
                        Directory.Move(legacyFolder, folder);
                        s_log.Info("[Achievements] The achievements kept so far now belong to the signed-in account.");
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Achievements] Could not move the achievements to the account: {ex.Message}");
                }
                _folder = folder;
                _cache.Clear();
            }
            Reset?.Invoke();
        }

        private string FileFor(Guid gameId) => Path.Combine(_folder, gameId + ".json");

        /// <summary>The game's achievements, or null when it has none.</summary>
        public GameAchievements Get(Guid gameId)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(gameId, out GameAchievements known))
                    return known;

                GameAchievements loaded = null;
                try
                {
                    string file = FileFor(gameId);
                    if (File.Exists(file))
                        loaded = JsonConvert.DeserializeObject<GameAchievements>(File.ReadAllText(file));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Achievements] Could not read the achievements of {gameId}: {ex.Message}");
                }
                _cache[gameId] = loaded;
                return loaded;
            }
        }

        public void Save(Guid gameId, GameAchievements data)
        {
            lock (_lock)
            {
                _cache[gameId] = data;
                try
                {
                    Directory.CreateDirectory(_folder);
                    string file = FileFor(gameId);
                    string temp = file + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(data));
                    if (File.Exists(file))
                        File.Delete(file);
                    File.Move(temp, file);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Achievements] Could not save the achievements of {gameId}: {ex.Message}");
                }
            }
            Changed?.Invoke(gameId);
        }
    }
}
