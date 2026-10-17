using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Flycast's saves, all single files in its data folder (beside the executable on Windows):
    ///
    ///   &lt;game id&gt;_vmu_save_A1.bin    a Dreamcast game's own VMU (Flycast's default, one per game,
    ///                                named after the disc's product id, e.g. MK-51000_vmu_save_A1.bin)
    ///   &lt;rom&gt;.nvmem / .nvmem2 / .eeprom   an arcade (NAOMI, Atomiswave) game's save
    ///
    /// The shared vmu_save_A1.bin (per-game VMUs switched off) holds every game's saves and is not
    /// synced, nor is the console's own flash (dc_flash.bin, rewritten on every boot). The files
    /// are learned like the other emulators': those written while the game runs are its own.
    /// Flycast makes a game's VMU, formatted and empty, as the game starts; an empty VMU is not
    /// taken for a save (see <see cref="IsBlankVmu"/>).
    /// </summary>
    internal sealed class FlycastSaveHandler : LearnedFolderSaveHandler
    {
        private const string VmuSuffix = "_vmu_save_A1.bin";
        private static readonly string[] ArcadeExtensions = { ".nvmem", ".nvmem2", ".eeprom" };

        public override string EmulatorTag => "flycast";

        protected override string Keyword => "flycast";

        protected override string SaveRoot(string installDir) => FindSaveRoot(installDir);

        internal static string FindSaveRoot(string installDir) =>
            string.IsNullOrEmpty(installDir) ? null : Path.Combine(installDir, "data");

        /// <summary>Whether a file in the data folder is one game's own save.</summary>
        internal static bool IsGameSave(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            if (fileName.EndsWith(VmuSuffix, StringComparison.OrdinalIgnoreCase))
                return fileName.Length > VmuSuffix.Length;   // not the shared "vmu_save_A1.bin"
            return ArcadeExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        }

        protected override List<string> UnitsWrittenSince(string root, DateTime sinceUtc) => SavesWrittenSince(root, sinceUtc);

        internal static List<string> SavesWrittenSince(string root, DateTime sinceUtc)
        {
            if (!Directory.Exists(root))
                return new List<string>();
            DateTime threshold = sinceUtc.AddSeconds(-2);
            return new DirectoryInfo(root).GetFiles()
                .Where(f => IsGameSave(f.Name) && f.LastWriteTimeUtc >= threshold)
                .Select(f => f.Name)
                .ToList();
        }

        // A save is one file directly in the data folder, so the entry name is the unit.
        protected override string UnitOfEntry(string entryName) =>
            !string.IsNullOrEmpty(entryName) && entryName.IndexOf('/') < 0 && IsGameSave(entryName) ? entryName : null;

        protected override bool IsBlankSaveFile(string path) =>
            path.EndsWith(VmuSuffix, StringComparison.OrdinalIgnoreCase) && IsBlankVmu(path);

        /// <summary>
        /// A VMU image with no files on it. A VMU is 256 blocks of 512 bytes; block 255 is the root
        /// block (16 bytes of 0x55 when formatted), which gives the directory's last block (at 0x4A)
        /// and its length in blocks (at 0x4C), little-endian. The directory runs downwards from
        /// there in 32-byte entries whose first byte is the file type (0x00 for an empty entry,
        /// 0x33 data, 0xCC game). Anything unexpected counts as not blank, so a real save is never
        /// mistaken for an empty card.
        /// </summary>
        internal static bool IsBlankVmu(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length != 256 * 512)
                    return false;
                if (data.All(b => b == 0))
                    return true;

                int root = 255 * 512;
                for (int i = 0; i < 16; i++)
                {
                    if (data[root + i] != 0x55)
                        return false;
                }
                int directoryLast = data[root + 0x4A] | (data[root + 0x4B] << 8);
                int directoryBlocks = data[root + 0x4C] | (data[root + 0x4D] << 8);
                if (directoryLast >= 256 || directoryBlocks <= 0 || directoryBlocks > directoryLast + 1)
                    return false;

                for (int block = directoryLast; block > directoryLast - directoryBlocks; block--)
                {
                    for (int entry = 0; entry < 512; entry += 32)
                    {
                        if (data[block * 512 + entry] != 0x00)
                            return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
