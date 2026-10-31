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

        /// <summary>
        /// The share of players who have it (Steam's, in percent), where the set has no points of
        /// its own to show instead; null when not known.
        /// </summary>
        public double? Rarity { get; set; }

        /// <summary>
        /// How many players have it (RetroAchievements'), where the share is not known: no count
        /// of the game's players comes with it, so it is only compared with the set's others.
        /// </summary>
        public int? Players { get; set; }

        /// <summary>A hidden trophy or secret achievement not unlocked yet: shown as such.</summary>
        public bool Hidden { get; set; }

        /// <summary>
        /// A locked one's progress, where its source keeps it (a Steam emulator's, for achievements
        /// tied to a stat): so far, and what unlocks it. Null when not known.
        /// </summary>
        public double? ProgressValue { get; set; }
        public double? ProgressMax { get; set; }

        /// <summary>How far along a locked one is, 0 to 1; null when not known (or unlocked).</summary>
        [JsonIgnore]
        public double? ProgressShare => !Unlocked && ProgressMax.HasValue && ProgressMax.Value > 0 && ProgressValue.HasValue
            ? Math.Max(0, Math.Min(1, ProgressValue.Value / ProgressMax.Value))
            : (double?)null;

        /// <summary>A locked one with some progress made.</summary>
        [JsonIgnore]
        public bool InProgress => ProgressShare.HasValue && ProgressShare.Value > 0;

        /// <summary>"12 / 20" (thousands grouped, fractions only where there are any).</summary>
        [JsonIgnore]
        public string ProgressText => !ProgressShare.HasValue ? null
            : Number(ProgressValue.Value) + " / " + Number(ProgressMax.Value);

        private static string Number(double value) =>
            Math.Abs(value - Math.Round(value)) < 1e-6
                ? Math.Round(value).ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
                : value.ToString("N1", System.Globalization.CultureInfo.CurrentCulture);

        [JsonIgnore]
        public bool Unlocked => UnlockedUtc.HasValue || UnlockedHardcoreUtc.HasValue;

        [JsonIgnore]
        public DateTime? When => UnlockedHardcoreUtc ?? UnlockedUtc;

        /// <summary>
        /// How rare it is, 0 (rarest) to 100: the share of players (Rarity), or for
        /// RetroAchievements its players compared with its set's most earned achievement's.
        /// Worked out when the set is read (GameAchievements.ScoreRarity); null when not known.
        /// </summary>
        [JsonIgnore]
        public double? RarityScore { get; set; }

        /// <summary>A rare one, drawn with a glowing edge (RareGlow): a tenth or fewer.</summary>
        [JsonIgnore]
        public bool Rare => RarityScore.HasValue && RarityScore.Value <= RareGlow.RareAt;
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

        /// <summary>Each achievement's <see cref="AchievementItem.RarityScore"/>, from the set as a whole.</summary>
        public GameAchievements ScoreRarity()
        {
            int most = Items.Count == 0 ? 0 : Items.Max(i => i.Players ?? 0);
            foreach (AchievementItem item in Items)
                item.RarityScore = item.Rarity
                    ?? (item.Players.HasValue && most > 0 ? 100.0 * item.Players.Value / most : (double?)null);
            return this;
        }

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
                _cache[gameId] = loaded?.ScoreRarity();
                return loaded;
            }
        }

        public void Save(Guid gameId, GameAchievements data)
        {
            lock (_lock)
            {
                _cache[gameId] = data?.ScoreRarity();
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
