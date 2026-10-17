using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RomM.Games
{
    /// <summary>
    /// A game's series, from what RomM has from IGDB. IGDB keeps two lists: collections, which
    /// are its series ("Kingdom Hearts"), and franchises, which are wider: the brands and
    /// characters a game belongs to ("Disney", "Donald Duck", "Final Fantasy" for Kingdom
    /// Hearts). RomM keeps them as metadatum.collections and metadatum.franchises
    /// (igdb_handler: IGDB's collections.name, and franchise.name with franchises.name). So a
    /// game's series are its collections. Without any, only the franchises its title names
    /// ("Kingdom Hearts" for "Kingdom Hearts 358/2 Days", not "Disney" or "Goofy"); and without
    /// those, when the series the server has are known, the one its title begins with
    /// ("KINGDOM HEARTS -HD 1.5+2.5 ReMIX-", a compilation IGDB gives no series).
    ///
    /// Of several, the one whose name is in the game's own name comes first ("Kingdom Hearts II"
    /// is first of all a Kingdom Hearts game), then as listed.
    ///
    /// IGDB also has sub-series ("The Legend of Zelda: Four Swords", "Need for Speed:
    /// Underground", "Ratchet &amp; Clank Future"). A series whose name begins with another
    /// series' whole name, followed by a break (a space, colon, dash...), belongs to that one
    /// (<see cref="Roots"/>): only into a name that is a series itself, never one made up by
    /// shortening, and never inside a word ("WarioWare" is not "Wario"'s).
    /// </summary>
    internal static class SeriesNames
    {
        /// <param name="known">The series the server has, when known (the Store), for a game
        /// with no series of its own; else null.</param>
        public static List<string> For(IEnumerable<string> collections, IEnumerable<string> franchises, string gameName, ICollection<string> known = null)
        {
            List<string> names = Clean(collections);
            string title = Key(gameName);
            if (names.Count == 0)
                names = Clean(franchises).Where(f => title.Length > 0 && Key(f).Length > 0 && title.Contains(Key(f))).ToList();
            if (names.Count == 0 && known != null && !string.IsNullOrWhiteSpace(gameName))
            {
                string begins = known.Where(k => !string.IsNullOrWhiteSpace(k)
                        && (string.Equals(gameName.Trim(), k.Trim(), StringComparison.CurrentCultureIgnoreCase) || IsUnder(gameName, k)))
                    .OrderByDescending(k => k.Length)
                    .FirstOrDefault();
                if (begins != null)
                    names.Add(begins.Trim());
            }
            // A sub-series of one of its other series is that series.
            names = names.Where(n => !names.Any(other => IsUnder(n, other))).ToList();
            if (title.Length == 0 || names.Count < 2)
                return names;
            // OrderBy is stable: the rest keep their order.
            return names.OrderBy(n => Key(n) is string k && k.Length > 0 && title.Contains(k) ? 0 : 1).ToList();
        }

        public static string Primary(IEnumerable<string> collections, IEnumerable<string> franchises, string gameName) =>
            For(collections, franchises, gameName).FirstOrDefault();

        /// <summary>
        /// Whether <paramref name="name"/> is a sub-series of <paramref name="parent"/>: it begins
        /// with the parent's whole name (letter case aside), is longer, and what follows is not a
        /// letter or digit.
        /// </summary>
        public static bool IsUnder(string name, string parent)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parent))
                return false;
            string n = name.Trim(), p = parent.Trim();
            if (n.Length <= p.Length || !n.StartsWith(p, StringComparison.CurrentCultureIgnoreCase))
                return false;
            return !char.IsLetterOrDigit(n[p.Length]);
        }

        /// <summary>
        /// Each name with the series it belongs to: the shortest of the names it is a sub-series
        /// of (directly or not), or itself. "The Legend of Zelda: Four Swords" ->
        /// "The Legend of Zelda" when both are in the list.
        /// </summary>
        public static Dictionary<string, string> Roots(IEnumerable<string> names)
        {
            List<string> all = Clean(names).OrderBy(n => n.Length).ToList();
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tops = new List<string>();
            foreach (string name in all)
            {
                string top = tops.FirstOrDefault(t => IsUnder(name, t));
                if (top == null)
                    tops.Add(name);
                roots[name] = top ?? name;
            }
            return roots;
        }

        private static List<string> Clean(IEnumerable<string> names) =>
            (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Letters and digits only, lower case: "Kingdom Hearts II: Final Mix" -> "kingdomheartsiifinalmix".
        private static string Key(string text)
        {
            var key = new StringBuilder();
            foreach (char c in text ?? "")
            {
                if (char.IsLetterOrDigit(c))
                    key.Append(char.ToLowerInvariant(c));
            }
            return key.ToString();
        }
    }
}
