using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// DuckStation's saves: with its default per-game cards, each game has its own memory card
    /// file in the memcards folder, named after the game's title in DuckStation's database
    /// ("Tony Hawk's Pro Skater 2 (USA)_1.mcd"), with _2 for the second slot. That title is not
    /// the ROM's file name, so the card is learned like the folder-based saves: whichever card is
    /// written while the game runs is this game's. See <see cref="LearnedFolderSaveHandler"/>.
    ///
    /// A card shared between all games (shared_card_1.mcd) cannot be split per game and is left
    /// alone.
    /// </summary>
    internal sealed class DuckStationSaveHandler : LearnedFolderSaveHandler
    {
        private const string CardExtension = ".mcd";
        private const string SharedCardPrefix = "shared_card_";

        public override string EmulatorTag => "duckstation";

        protected override string Keyword => "duckstation";

        protected override string SaveRoot(string installDir) => FindSaveRoot(installDir);

        /// <summary>
        /// A portable DuckStation (portable.txt beside the executable) keeps everything in its own
        /// folder; otherwise it is under Documents\DuckStation. The card folder can be moved in
        /// settings.ini, which is honoured when set.
        /// </summary>
        internal static string FindSaveRoot(string installDir)
        {
            string dataRoot = null;
            if (!string.IsNullOrEmpty(installDir)
                && (File.Exists(Path.Combine(installDir, "portable.txt")) || File.Exists(Path.Combine(installDir, "settings.ini"))))
                dataRoot = installDir;

            if (dataRoot == null)
            {
                string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DuckStation");
                if (Directory.Exists(documents))
                    dataRoot = documents;
            }

            if (dataRoot == null)
                return string.IsNullOrEmpty(installDir) ? null : Path.Combine(installDir, "memcards");

            string cards = "memcards";
            try
            {
                string settings = Path.Combine(dataRoot, "settings.ini");
                if (File.Exists(settings))
                {
                    var ini = Pcsx2SaveHandler.ParseIni(File.ReadAllLines(settings));
                    if (ini.TryGetValue("MemoryCards", out var section)
                        && section.TryGetValue("Directory", out string configured)
                        && !string.IsNullOrWhiteSpace(configured))
                        cards = configured;
                }
            }
            catch
            {
                // An unreadable settings file leaves the default folder.
            }

            return Path.IsPathRooted(cards) ? cards : Path.Combine(dataRoot, cards);
        }

        internal static bool IsGameCard(string fileName)
        {
            return !string.IsNullOrEmpty(fileName)
                   && fileName.EndsWith(CardExtension, StringComparison.OrdinalIgnoreCase)
                   && !fileName.StartsWith(SharedCardPrefix, StringComparison.OrdinalIgnoreCase);
        }

        protected override List<string> UnitsWrittenSince(string root, DateTime sinceUtc) => CardsWrittenSince(root, sinceUtc);

        internal static List<string> CardsWrittenSince(string root, DateTime sinceUtc)
        {
            if (!Directory.Exists(root))
                return new List<string>();

            DateTime threshold = sinceUtc.AddSeconds(-2);
            return new DirectoryInfo(root).GetFiles()
                .Where(f => IsGameCard(f.Name) && f.LastWriteTimeUtc >= threshold)
                .Select(f => f.Name)
                .ToList();
        }

        protected override bool IsBlankSaveFile(string path) => IsBlankCard(path);

        /// <summary>
        /// Whether a PlayStation memory card image has no saves on it. DuckStation creates a
        /// formatted card the moment a game starts, and a card is always the same size whatever
        /// it holds, so only its directory tells an empty one from a used one.
        ///
        /// The card starts with a header frame ("MC") followed by 15 directory frames of 128 bytes,
        /// one per block. A frame's first byte is the block's state: 0x51, 0x52 and 0x53 mark the
        /// first, middle and last block of a save. None of those means nothing is saved.
        ///
        /// Anything that cannot be read as such a card is reported as not blank, so a file this
        /// does not understand is always treated as a real save and never discarded.
        /// </summary>
        internal static bool IsBlankCard(string path)
        {
            const int FrameSize = 0x80;
            const int DirectoryFrames = 15;

            try
            {
                var header = new byte[FrameSize * (DirectoryFrames + 1)];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read = 0;
                    while (read < header.Length)
                    {
                        int n = stream.Read(header, read, header.Length - read);
                        if (n <= 0)
                            return false;
                        read += n;
                    }
                }

                if (header[0] != (byte)'M' || header[1] != (byte)'C')
                    return false;

                for (int frame = 1; frame <= DirectoryFrames; frame++)
                {
                    byte state = header[frame * FrameSize];
                    if (state == 0x51 || state == 0x52 || state == 0x53)
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // A card is a file directly in the card folder, so the entry name is the unit.
        protected override string UnitOfEntry(string entryName) => CardOfEntry(entryName);

        internal static string CardOfEntry(string entryName)
        {
            return !string.IsNullOrEmpty(entryName) && entryName.IndexOf('/') < 0 && IsGameCard(entryName) ? entryName : null;
        }
    }
}
