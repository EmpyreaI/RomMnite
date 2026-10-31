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
    /// game, so before each game starts the card in slot 1 is made a folder card when it is a
    /// file (<see cref="UseFolderCard"/>): its saves are taken out of the file (Ps2MemoryCard),
    /// the file is kept as it was, and slot 1 is pointed at the folder.
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

        /// <summary>
        /// Slot 1 made a folder card, when it holds a file card: a folder named as the file
        /// without its extension (Mcd001.ps2: Mcd001) beside it, holding the file's saves and
        /// superblock, as PCSX2's own "convert" makes it; the file stays, untouched, as a backup.
        /// A folder of that name made before is used as it is. Nothing is done while the file
        /// does not exist yet (PCSX2 makes and formats it when it first starts a game; it is
        /// converted at the next start) or cannot be read. Run before PCSX2 starts.
        /// </summary>
        internal static void UseFolderCard(string installDir, global::Playnite.SDK.ILogger log)
        {
            try
            {
                string dataRoot = DataRoot(installDir);
                if (dataRoot == null)
                    return;
                string iniPath = Path.Combine(dataRoot, "inis", "PCSX2.ini");
                var ini = ParseIni(File.ReadAllLines(iniPath));
                if (string.Equals(Value(ini, "MemoryCards", "Slot1_Enable"), "false", StringComparison.OrdinalIgnoreCase))
                    return;
                string card = Value(ini, "MemoryCards", "Slot1_Filename");
                if (string.IsNullOrEmpty(card))
                    return;
                string cardsDir = CardsFolder(ini, dataRoot);
                string cardPath = Path.Combine(cardsDir, card);
                if (Directory.Exists(cardPath))
                    return;   // a folder card already

                string folderName = Path.GetFileNameWithoutExtension(card);
                if (string.IsNullOrEmpty(folderName) || string.Equals(folderName, card, StringComparison.OrdinalIgnoreCase))
                    folderName = card + "_folder";
                string folder = Path.Combine(cardsDir, folderName);
                if (!Directory.Exists(folder))
                {
                    if (!File.Exists(cardPath))
                        return;   // made by PCSX2 at its first game; converted next time
                    string staging = folder + ".romm-converting";
                    if (Directory.Exists(staging))
                        Directory.Delete(staging, true);
                    List<string> saves = Ps2MemoryCard.Read(File.ReadAllBytes(cardPath)).WriteFolderCard(staging);
                    Directory.Move(staging, folder);
                    log?.Info($"[SaveSync] PCSX2: memory card \"{card}\" made a folder card, \"{folder}\", so each game's saves sync on their own " +
                              $"({saves.Count} saves: {string.Join(", ", saves)}). The file is kept as it was.");
                }

                if (Account.SettingsFile.SetIni(iniPath, "MemoryCards", new Dictionary<string, string> { ["Slot1_Filename"] = folderName }))
                    log?.Info($"[SaveSync] PCSX2: slot 1 now holds the folder card \"{folderName}\" (was \"{card}\").");
            }
            catch (Exception ex)
            {
                log?.Warn($"[SaveSync] PCSX2: could not make the memory card in slot 1 a folder card: {ex.Message}");
            }
        }

        // Where PCSX2 keeps its settings: beside it (portable), or Documents\PCSX2.
        private static string DataRoot(string installDir)
        {
            if (!string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "inis", "PCSX2.ini")))
                return installDir;
            string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2");
            return File.Exists(Path.Combine(documents, "inis", "PCSX2.ini")) ? documents : null;
        }

        private static string CardsFolder(Dictionary<string, Dictionary<string, string>> ini, string dataRoot)
        {
            string cardsDir = Value(ini, "Folders", "MemoryCards");
            if (string.IsNullOrEmpty(cardsDir))
                cardsDir = "memcards";
            return Path.IsPathRooted(cardsDir) ? cardsDir : Path.Combine(dataRoot, cardsDir);
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
