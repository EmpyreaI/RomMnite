using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Account
{
    /// <summary>
    /// What each account has made of its RomM games: favorite, completion status, score, play
    /// time, play count, last played and notes. Playnite keeps these on the game itself, which
    /// every account shares, so on switching accounts the leaving account's are kept in its own
    /// file and the arriving account's put back (an account new to them starts from nothing:
    /// not played, no score, no notes). Games from other libraries are everyone's and are not
    /// touched, as with the library itself.
    /// </summary>
    public static class AccountGameData
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        public sealed class Entry
        {
            public bool Favorite { get; set; }
            public Guid CompletionStatusId { get; set; }
            public int? UserScore { get; set; }
            public ulong Playtime { get; set; }
            public ulong PlayCount { get; set; }
            public DateTime? LastActivity { get; set; }
            public string Notes { get; set; }

            public static Entry Of(Game game) => new Entry
            {
                Favorite = game.Favorite,
                CompletionStatusId = game.CompletionStatusId,
                UserScore = game.UserScore,
                Playtime = game.Playtime,
                PlayCount = game.PlayCount,
                LastActivity = game.LastActivity,
                Notes = game.Notes,
            };

            // Puts this on the game; whether anything changed.
            public bool ApplyTo(Game game)
            {
                bool changed = game.Favorite != Favorite || game.CompletionStatusId != CompletionStatusId
                    || game.UserScore != UserScore || game.Playtime != Playtime || game.PlayCount != PlayCount
                    || game.LastActivity != LastActivity || !string.Equals(game.Notes ?? "", Notes ?? "", StringComparison.Ordinal);
                if (!changed)
                    return false;
                game.Favorite = Favorite;
                game.CompletionStatusId = CompletionStatusId;
                game.UserScore = UserScore;
                game.Playtime = Playtime;
                game.PlayCount = PlayCount;
                game.LastActivity = LastActivity;
                game.Notes = Notes;
                return true;
            }
        }

        /// <summary>
        /// Keeps the leaving account's data in <paramref name="leavingFile"/> and puts the
        /// arriving account's (from <paramref name="arrivingFile"/>) on the games. On the UI thread.
        /// </summary>
        public static void Swap(IPlayniteAPI api, Guid pluginId, string leavingFile, string arrivingFile, Action<Guid> ownWrite)
        {
            List<Game> romm = api.Database.Games.Where(g => g.PluginId == pluginId).ToList();

            var leaving = romm.ToDictionary(g => g.Id, Entry.Of);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(leavingFile));
                File.WriteAllText(leavingFile, JsonConvert.SerializeObject(leaving));
            }
            catch (Exception ex)
            {
                // Without the leaving account's data kept, putting the other's on would lose it.
                s_log.Warn($"[Accounts] Could not keep the account's game data, so it stays on the games: {ex.Message}");
                return;
            }

            Dictionary<Guid, Entry> arriving = null;
            try
            {
                if (File.Exists(arrivingFile))
                    arriving = JsonConvert.DeserializeObject<Dictionary<Guid, Entry>>(File.ReadAllText(arrivingFile));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Accounts] Could not read the account's game data: {ex.Message}");
            }
            arriving = arriving ?? new Dictionary<Guid, Entry>();

            // Nothing yet for a game: not played.
            Guid notPlayed = api.Database.CompletionStatuses.FirstOrDefault(c => c.Name == "Not Played")?.Id ?? Guid.Empty;
            var changed = new List<Game>();
            foreach (Game game in romm)
            {
                Entry entry = arriving.TryGetValue(game.Id, out Entry kept) ? kept : new Entry { CompletionStatusId = notPlayed };
                if (entry.ApplyTo(game))
                    changed.Add(game);
            }
            if (changed.Count == 0)
                return;
            try
            {
                using (api.Database.BufferedUpdate())
                {
                    foreach (Game game in changed)
                    {
                        ownWrite?.Invoke(game.Id);
                        api.Database.Games.Update(game);
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Accounts] Could not put the account's game data on the games: {ex.Message}");
            }
            s_log.Info($"[Accounts] Put the account's favorites, statuses, play time and notes on {changed.Count} games.");
        }
    }
}
