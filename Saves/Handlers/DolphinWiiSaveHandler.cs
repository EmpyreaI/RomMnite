using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Dolphin's Wii saves, in its emulated NAND: Wii\title\&lt;type&gt;\&lt;title id&gt;\data\, where the
    /// type is 00010000 for disc games and 00010001 (or 00010004) for channels and WiiWare, and the
    /// title id is the game's four-letter id in hex (RMGE -> 524d4745). Only the data folder is the
    /// save; beside it, content\ holds a channel's own files, which can be large and are not synced.
    /// The folders are learned like the other emulators' saves. A save is filed by its path under
    /// title ("00010000/524d4745/data/..."), so it restores to the same title on another machine.
    /// </summary>
    internal sealed class DolphinWiiSaveHandler : LearnedFolderSaveHandler
    {
        private static readonly string[] TitleTypes = { "00010000", "00010001", "00010004" };

        public override string EmulatorTag => "dolphin";

        protected override string Keyword => "dolphin";

        protected override string SaveRoot(string installDir) => FindSaveRoot(installDir);

        /// <summary>Wii\title beside Dolphin's GC folder (portable, Documents or the roaming profile).</summary>
        internal static string FindSaveRoot(string installDir)
        {
            string gc = DolphinSaveHandler.FindSaveRoot(installDir);
            string user = string.IsNullOrEmpty(gc) ? null : Path.GetDirectoryName(gc.TrimEnd('\\', '/'));
            return user == null ? null : Path.Combine(user, "Wii", "title");
        }

        protected override List<string> UnitsWrittenSince(string root, DateTime sinceUtc) => SavesWrittenSince(root, sinceUtc);

        internal static List<string> SavesWrittenSince(string root, DateTime sinceUtc)
        {
            var result = new List<string>();
            if (!Directory.Exists(root))
                return result;
            DateTime threshold = sinceUtc.AddSeconds(-2);
            foreach (string type in TitleTypes)
            {
                string typeFolder = Path.Combine(root, type);
                if (!Directory.Exists(typeFolder))
                    continue;
                foreach (DirectoryInfo title in new DirectoryInfo(typeFolder).GetDirectories())
                {
                    var data = new DirectoryInfo(Path.Combine(title.FullName, "data"));
                    if (data.Exists && data.GetFiles("*", SearchOption.AllDirectories).Any(f => f.LastWriteTimeUtc >= threshold))
                        result.Add(type + "/" + title.Name + "/data");
                }
            }
            return result;
        }

        // "00010000/524d4745/data/..." belongs to the unit "00010000/524d4745/data".
        protected override string UnitOfEntry(string entryName) => SaveOfEntry(entryName);

        internal static string SaveOfEntry(string entryName)
        {
            string[] parts = (entryName ?? string.Empty).Split('/');
            if (parts.Length < 4 || !TitleTypes.Contains(parts[0]) || parts[1].Length != 8
                || !string.Equals(parts[2], "data", StringComparison.OrdinalIgnoreCase))
                return null;
            return parts[0] + "/" + parts[1] + "/" + parts[2];
        }

        /// <summary>
        /// Whether a game is a Wii game (rather than GameCube), by its Playnite platform, or else
        /// by a Wii-only image format.
        /// </summary>
        internal static bool IsWiiGame(Game game, string contentPath)
        {
            try
            {
                var platforms = game?.Platforms;
                if (platforms != null && platforms.Count > 0)
                {
                    if (platforms.Any(p => string.Equals(p.SpecificationId, "nintendo_wii", StringComparison.OrdinalIgnoreCase)))
                        return true;
                    if (platforms.Any(p => string.Equals(p.SpecificationId, "nintendo_gamecube", StringComparison.OrdinalIgnoreCase)))
                        return false;
                    if (platforms.Any(p => (p.Name ?? "").IndexOf("Wii", StringComparison.OrdinalIgnoreCase) >= 0
                                           && (p.Name ?? "").IndexOf("Wii U", StringComparison.OrdinalIgnoreCase) < 0))
                        return true;
                }
            }
            catch
            {
                // Platforms are looked up in the database; without it, the file decides.
            }
            string extension = Path.GetExtension(contentPath ?? "").ToLowerInvariant();
            return extension == ".wbfs" || extension == ".wad";
        }
    }

    /// <summary>
    /// Dolphin plays two consoles that keep saves differently: GameCube saves on memory cards
    /// (<see cref="DolphinSaveHandler"/>) and Wii saves in the NAND
    /// (<see cref="DolphinWiiSaveHandler"/>). This hands each game to the right one.
    /// </summary>
    internal sealed class DolphinConsolesSaveHandler : ISaveHandler
    {
        private readonly DolphinSaveHandler _gameCube = new DolphinSaveHandler();
        private readonly DolphinWiiSaveHandler _wii = new DolphinWiiSaveHandler();

        public string EmulatorTag => "dolphin";

        public bool CanHandle(Emulator emulator) => _gameCube.CanHandle(emulator);

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            return DolphinWiiSaveHandler.IsWiiGame(request?.Game, request?.ContentPath)
                ? _wii.ResolveTarget(request)
                : _gameCube.ResolveTarget(request);
        }
    }
}
