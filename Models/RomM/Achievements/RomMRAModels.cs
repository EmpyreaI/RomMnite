using System.Collections.Generic;
using Newtonsoft.Json;

namespace RomM.Models.RomM.Achievements
{
    // The RetroAchievements set RomM holds for a rom (rom.merged_ra_metadata).
    public class RomMRAMetadata
    {
        [JsonProperty("achievements")]
        public List<RomMRAAchievement> Achievements { get; set; }
    }

    public class RomMRAAchievement
    {
        [JsonProperty("ra_id")]
        public int? Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("points")]
        public int? Points { get; set; }

        // Ties an achievement to the entries in the user's earned list.
        [JsonProperty("badge_id")]
        public string BadgeId { get; set; }

        // Badge images on RetroAchievements' public image host.
        [JsonProperty("badge_url")]
        public string BadgeUrl { get; set; }

        [JsonProperty("badge_url_lock")]
        public string BadgeUrlLock { get; set; }

        // Badge images stored by RomM, already prefixed with its resource mount.
        [JsonProperty("badge_path")]
        public string BadgePath { get; set; }

        [JsonProperty("badge_path_lock")]
        public string BadgePathLock { get; set; }
    }

    // The signed-in RomM user's RetroAchievements progress (user.ra_progression).
    public class RomMRAProgression
    {
        [JsonProperty("results")]
        public List<RomMRAGameProgression> Results { get; set; }
    }

    public class RomMRAGameProgression
    {
        [JsonProperty("rom_ra_id")]
        public int? RAId { get; set; }

        [JsonProperty("earned_achievements")]
        public List<RomMRAEarned> Earned { get; set; }
    }

    public class RomMRAEarned
    {
        // The achievement's badge id.
        [JsonProperty("id")]
        public string BadgeId { get; set; }

        [JsonProperty("date")]
        public string Date { get; set; }

        [JsonProperty("date_hardcore")]
        public string DateHardcore { get; set; }
    }
}
