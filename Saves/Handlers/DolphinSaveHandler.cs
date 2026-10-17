using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Dolphin's GameCube saves, in its "GCI Folder" memory card mode (the default): every save is
    /// its own file, GC\&lt;region&gt;\Card A\01-GM8E-MetroidPrime A.gci, named after the game's id
    /// and the save's own name rather than the ROM. So the files are learned like the other
    /// emulators' saves: whichever .gci files are written while the game runs are this game's.
    /// See <see cref="LearnedFolderSaveHandler"/>.
    ///
    /// A save is filed by its path under GC ("USA/Card A/x.gci"), so it restores into the same
    /// region and card slot on another machine. A single-file memory card (MemoryCardA.USA.raw)
    /// holds every game's saves together, cannot be split per game, and is not synced.
    /// </summary>
    internal sealed class DolphinSaveHandler : LearnedFolderSaveHandler
    {
        private const string SaveExtension = ".gci";
        private const string CardFolderPrefix = "Card ";

        public override string EmulatorTag => "dolphin";

        protected override string Keyword => "dolphin";

        protected override string SaveRoot(string installDir) => FindSaveRoot(installDir);

        /// <summary>
        /// A portable Dolphin (portable.txt beside the executable) keeps its data in a User folder
        /// of its own; otherwise it is under Documents or the roaming profile.
        /// </summary>
        internal static string FindSaveRoot(string installDir)
        {
            if (!string.IsNullOrEmpty(installDir)
                && (File.Exists(Path.Combine(installDir, "portable.txt")) || Directory.Exists(Path.Combine(installDir, "User"))))
                return Path.Combine(installDir, "User", "GC");

            string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Dolphin Emulator");
            if (Directory.Exists(documents))
                return Path.Combine(documents, "GC");

            string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dolphin Emulator");
            if (Directory.Exists(roaming))
                return Path.Combine(roaming, "GC");

            return string.IsNullOrEmpty(installDir) ? null : Path.Combine(installDir, "User", "GC");
        }

        protected override List<string> UnitsWrittenSince(string root, DateTime sinceUtc) => SavesWrittenSince(root, sinceUtc);

        internal static List<string> SavesWrittenSince(string root, DateTime sinceUtc)
        {
            var result = new List<string>();
            if (!Directory.Exists(root))
                return result;

            DateTime threshold = sinceUtc.AddSeconds(-2);

            foreach (var region in new DirectoryInfo(root).GetDirectories())
            {
                foreach (var card in region.GetDirectories().Where(d => d.Name.StartsWith(CardFolderPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    // Extension checked by hand: a "*.gci" pattern also matches the
                    // ".gci.deleted" files Dolphin leaves behind for removed saves.
                    result.AddRange(card.GetFiles()
                        .Where(f => f.Extension.Equals(SaveExtension, StringComparison.OrdinalIgnoreCase)
                                    && f.LastWriteTimeUtc >= threshold)
                        .Select(f => region.Name + "/" + card.Name + "/" + f.Name));
                }
            }
            return result;
        }

        // A save is one file, so the entry name is the unit.
        protected override string UnitOfEntry(string entryName) => SaveOfEntry(entryName);

        /// <summary>"USA/Card A/x.gci" is a save; anything shaped differently is not.</summary>
        internal static string SaveOfEntry(string entryName)
        {
            string[] parts = (entryName ?? string.Empty).Split('/');
            bool isSave = parts.Length == 3
                          && parts[0].Length > 0
                          && parts[1].StartsWith(CardFolderPrefix, StringComparison.OrdinalIgnoreCase)
                          && parts[2].EndsWith(SaveExtension, StringComparison.OrdinalIgnoreCase);
            return isSave ? entryName : null;
        }
    }
}
