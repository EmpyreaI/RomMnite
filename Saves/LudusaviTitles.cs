using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace RomM.Saves
{
    /// <summary>
    /// The name Ludusavi should look a game up under, for games whose library name it does not
    /// recognise. Set per game from the game's right-click menu; one small JSON file kept in the
    /// plugin's data, so it travels with a portable Playnite.
    /// </summary>
    internal static class LudusaviTitles
    {
        private static readonly object s_lock = new object();

        private static string FilePath(string ludusaviDir) => Path.Combine(ludusaviDir, "titles.json");

        private static Dictionary<Guid, string> Load(string ludusaviDir)
        {
            try
            {
                string path = FilePath(ludusaviDir);
                if (File.Exists(path))
                    return JsonConvert.DeserializeObject<Dictionary<Guid, string>>(File.ReadAllText(path)) ?? new Dictionary<Guid, string>();
            }
            catch
            {
                // An unreadable file only loses the custom names; they can be set again.
            }
            return new Dictionary<Guid, string>();
        }

        /// <summary>The custom name for a game, or null when it has none.</summary>
        public static string Get(string ludusaviDir, Guid gameId)
        {
            lock (s_lock)
                return Load(ludusaviDir).TryGetValue(gameId, out string title) && !string.IsNullOrWhiteSpace(title) ? title : null;
        }

        /// <summary>Sets a game's custom name; an empty name removes it.</summary>
        public static void Set(string ludusaviDir, Guid gameId, string title)
        {
            lock (s_lock)
            {
                var all = Load(ludusaviDir);
                if (string.IsNullOrWhiteSpace(title))
                    all.Remove(gameId);
                else
                    all[gameId] = title.Trim();

                Directory.CreateDirectory(ludusaviDir);
                File.WriteAllText(FilePath(ludusaviDir), JsonConvert.SerializeObject(all, Formatting.Indented));
            }
        }
    }
}
