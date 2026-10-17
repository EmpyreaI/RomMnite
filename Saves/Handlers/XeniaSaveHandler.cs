using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Xenia's saves. Under content\&lt;profile id&gt;\ each game has a folder named after its title
    /// id (4D5307F1), and inside it:
    ///
    ///   00000001\&lt;save name&gt;\...         the saved games
    ///   Headers\00000001\...              the index Xenia lists them from
    ///
    /// The same title folder can also hold downloaded content (00000002) and title updates
    /// (000B0000), which are not saves and can be large, so only the two folders above are synced.
    /// Archive entries are relative to the profile folder, so a save restores under whatever
    /// profile id the other machine has. See <see cref="LearnedFolderSaveHandler"/>.
    /// </summary>
    internal sealed class XeniaSaveHandler : LearnedFolderSaveHandler
    {
        private const string SavedGames = "00000001";
        private const string Headers = "Headers";

        public override string EmulatorTag => "xenia";

        protected override string Keyword => "xenia";

        protected override string SaveRoot(string installDir) => FindSaveRoot(installDir);

        /// <summary>
        /// A portable Xenia keeps content beside the executable; otherwise it is under
        /// Documents\Xenia. Builds with profiles add a folder per profile (16 hex digits) and the
        /// one written most recently is taken as the one in use; older builds put the title
        /// folders straight under content.
        /// </summary>
        internal static string FindSaveRoot(string installDir)
        {
            string content = null;
            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "content")))
                content = Path.Combine(installDir, "content");

            if (content == null)
            {
                string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Xenia", "content");
                if (Directory.Exists(documents))
                    content = documents;
            }

            if (content == null)
                return string.IsNullOrEmpty(installDir) ? null : Path.Combine(installDir, "content");

            // The profile Xenia is set to sign in (each account's own; see Account.EmulatorUsers).
            string signedIn = string.IsNullOrEmpty(installDir) ? null : EmulatorActiveUser.XeniaSignedIn(installDir);
            if (signedIn != null && Directory.Exists(Path.Combine(content, signedIn)))
                return Path.Combine(content, signedIn);

            try
            {
                var profile = new DirectoryInfo(content).GetDirectories()
                    .Where(d => d.Name.Length == 16 && d.Name.All(IsHex))
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .ThenBy(d => d.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                return profile?.FullName ?? content;
            }
            catch
            {
                return content;
            }
        }

        private static bool IsHex(char c) => Uri.IsHexDigit(c);

        /// <summary>
        /// A game's title folder: 8 hex digits, and not one of the system's own (FFFE....), which
        /// is where the profile itself lives and is rewritten every session.
        /// </summary>
        private static bool IsGameTitle(string name)
        {
            return name.Length == 8 && name.All(IsHex) && !name.StartsWith("FFFE", StringComparison.OrdinalIgnoreCase);
        }

        protected override List<string> UnitsWrittenSince(string root, DateTime sinceUtc) => SaveUnitsWrittenSince(root, sinceUtc);

        internal static List<string> SaveUnitsWrittenSince(string root, DateTime sinceUtc)
        {
            var result = new List<string>();
            if (!Directory.Exists(root))
                return result;

            DateTime threshold = sinceUtc.AddSeconds(-2);

            foreach (var title in new DirectoryInfo(root).GetDirectories().Where(d => IsGameTitle(d.Name)))
            {
                var units = new[] { title.Name + "/" + SavedGames, title.Name + "/" + Headers + "/" + SavedGames }
                    .Where(u => Directory.Exists(Path.Combine(root, u.Replace('/', Path.DirectorySeparatorChar))))
                    .ToList();

                bool written = units.Any(u =>
                    new DirectoryInfo(Path.Combine(root, u.Replace('/', Path.DirectorySeparatorChar)))
                        .GetFiles("*", SearchOption.AllDirectories)
                        .Any(f => f.LastWriteTimeUtc >= threshold));

                // The saves and their index go together: one without the other is not a save
                // Xenia can list, so writing to either brings both.
                if (written)
                    result.AddRange(units);
            }
            return result;
        }

        protected override string UnitOfEntry(string entryName) => SaveUnitOf(entryName);

        /// <summary>
        /// "4D5307F1/00000001/Hero000/x" belongs to "4D5307F1/00000001";
        /// "4D5307F1/Headers/00000001/x" to "4D5307F1/Headers/00000001". Anything else is not a save.
        /// </summary>
        internal static string SaveUnitOf(string entryName)
        {
            string[] parts = (entryName ?? string.Empty).Split('/');
            if (parts.Length < 3 || !IsGameTitle(parts[0]))
                return null;

            if (parts[1].Equals(SavedGames, StringComparison.OrdinalIgnoreCase))
                return parts[0] + "/" + SavedGames;

            if (parts.Length >= 4
                && parts[1].Equals(Headers, StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals(SavedGames, StringComparison.OrdinalIgnoreCase))
                return parts[0] + "/" + Headers + "/" + SavedGames;

            return null;
        }
    }
}
