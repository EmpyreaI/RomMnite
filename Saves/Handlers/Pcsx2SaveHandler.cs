using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// PCSX2's saves, when slot 1 holds a *folder* memory card: the card is a directory and every
    /// save on it is a folder of its own, named after the game's serial (BASLUS-20063MC Save).
    /// A classic single-file card holds every game's saves in one blob and cannot be split per
    /// game, so it is not synced; converting the card to the folder type in PCSX2 is the fix.
    ///
    /// Which card is in slot 1, and where cards live, is read from PCSX2's own settings file, so
    /// nothing has to be configured here. See <see cref="LearnedFolderSaveHandler"/>.
    /// </summary>
    internal sealed class Pcsx2SaveHandler : LearnedFolderSaveHandler
    {
        // The console's own settings entry (BADATA-SYSTEM, BEDATA-SYSTEM, ...). PCSX2 rewrites it
        // on every boot, so it would otherwise be taken for the running game's save.
        private static readonly Regex SystemFolder = new Regex("^B.DATA-SYSTEM$", RegexOptions.IgnoreCase);

        public override string EmulatorTag => "pcsx2";

        protected override string Keyword => "pcsx2";

        protected override string SaveRoot(string installDir) => FindSaveRoot(installDir);

        /// <summary>
        /// The folder card in slot 1, or null when PCSX2's settings cannot be found, slot 1 is
        /// off or empty, or its card is a single file. A portable PCSX2 keeps its settings beside
        /// the executable; otherwise they are under Documents\PCSX2.
        /// </summary>
        internal static string FindSaveRoot(string installDir)
        {
            string dataRoot = null;
            if (!string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "inis", "PCSX2.ini")))
                dataRoot = installDir;

            if (dataRoot == null)
            {
                string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2");
                if (File.Exists(Path.Combine(documents, "inis", "PCSX2.ini")))
                    dataRoot = documents;
            }

            if (dataRoot == null)
                return null;

            var ini = ParseIni(File.ReadAllLines(Path.Combine(dataRoot, "inis", "PCSX2.ini")));

            if (string.Equals(Value(ini, "MemoryCards", "Slot1_Enable"), "false", StringComparison.OrdinalIgnoreCase))
                return null;

            string card = Value(ini, "MemoryCards", "Slot1_Filename");
            if (string.IsNullOrEmpty(card))
                return null;

            string cardsDir = Value(ini, "Folders", "MemoryCards");
            if (string.IsNullOrEmpty(cardsDir))
                cardsDir = "memcards";
            if (!Path.IsPathRooted(cardsDir))
                cardsDir = Path.Combine(dataRoot, cardsDir);

            string cardPath = Path.Combine(cardsDir, card);
            return Directory.Exists(cardPath) ? cardPath : null;
        }

        /// <summary>Section -> key -> value, both case-insensitive. Later duplicates win.</summary>
        internal static Dictionary<string, Dictionary<string, string>> ParseIni(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string section = string.Empty;

            foreach (string raw in lines ?? Enumerable.Empty<string>())
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                if (!result.TryGetValue(section, out var values))
                    result[section] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
            }
            return result;
        }

        private static string Value(Dictionary<string, Dictionary<string, string>> ini, string section, string key)
        {
            return ini.TryGetValue(section, out var values) && values.TryGetValue(key, out string value) ? value : null;
        }

        internal static bool IsSystemFolder(string name) => SystemFolder.IsMatch(name ?? string.Empty);

        protected override List<string> UnitsWrittenSince(string root, DateTime sinceUtc)
        {
            return FoldersWrittenSince(root, sinceUtc).Where(name => !IsSystemFolder(name)).ToList();
        }

        protected override string UnitOfEntry(string entryName)
        {
            string unit = base.UnitOfEntry(entryName);
            return unit == null || IsSystemFolder(unit) ? null : unit;
        }
    }
}
