using System.Collections.Generic;

namespace RomM.AchievementViews
{
    /// <summary>The selected game's achievement totals, bound to by themes.</summary>
    public class AchievementsSummary : ObservableObject
    {
        private bool _hasData;
        private int _unlocked;
        private int _total;
        private int _percent;
        private int _points;
        private int _pointsTotal;
        private bool _showUnlocked = true;
        private bool _showLocked = true;
        private bool _needsFirstPlay;

        public bool HasData { get => _hasData; set => SetValue(ref _hasData, value); }

        /// <summary>
        /// No achievements yet because the game's emulator keeps them itself (Xenia, RPCS3) and the
        /// game has not been played in it: the achievements section says to play it once.
        /// </summary>
        public bool NeedsFirstPlay { get => _needsFirstPlay; set => SetValue(ref _needsFirstPlay, value); }
        public int Unlocked { get => _unlocked; set => SetValue(ref _unlocked, value); }
        public int Total { get => _total; set => SetValue(ref _total, value); }
        public int Percent { get => _percent; set => SetValue(ref _percent, value); }
        public int Points { get => _points; set => SetValue(ref _points, value); }
        public int PointsTotal { get => _pointsTotal; set => SetValue(ref _pointsTotal, value); }

        // Names kept from SuccessStory so themes written for it work unchanged: whether the
        // game details show the unlocked and the locked badge rows.
        public bool EnableIntegrationCompactUnlocked { get => _showUnlocked; set => SetValue(ref _showUnlocked, value); }
        public bool EnableIntegrationCompactLocked { get => _showLocked; set => SetValue(ref _showLocked, value); }

        public void Show(GameAchievements data, bool needsFirstPlay = false)
        {
            bool has = data != null && data.Total > 0;
            NeedsFirstPlay = !has && needsFirstPlay;
            Unlocked = has ? data.Unlocked : 0;
            Total = has ? data.Total : 0;
            Percent = has ? (int)System.Math.Round(100.0 * data.Unlocked / data.Total) : 0;
            Points = has ? data.PointsUnlocked : 0;
            PointsTotal = has ? data.Points : 0;
            HasData = has;
        }
    }
}
