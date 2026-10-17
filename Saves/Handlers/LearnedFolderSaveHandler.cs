using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Shared by emulators that keep each game's save as one or more folders named after a title
    /// id rather than the ROM (PPSSPP's SAVEDATA\ULUS10041..., RPCS3's savedata\BLUS30443...).
    /// Instead of digging the id out of the game image -- which would need a reader per format --
    /// the folders are learned: any folder written to while the game runs is this game's, and so
    /// is any folder inside a save downloaded for it.
    ///
    /// A new emulator of this kind only has to say what it is called and where its saves live.
    /// </summary>
    internal abstract class LearnedFolderSaveHandler : ISaveHandler
    {
        public abstract string EmulatorTag { get; }

        /// <summary>The word that identifies this emulator in Playnite's name, type or install folder.</summary>
        protected abstract string Keyword { get; }

        /// <summary>The directory the per-game save folders sit in, or null when it cannot be told.</summary>
        protected abstract string SaveRoot(string installDir);

        public bool CanHandle(Emulator emulator)
        {
            if (emulator == null)
                return false;

            return Mentions(emulator.BuiltInConfigId) || Mentions(emulator.Name) || Mentions(emulator.InstallDir);
        }

        private bool Mentions(string value)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                string root = SaveRoot(request.EmulatorInstallDir);
                if (root == null || request.Game == null)
                    return null;

                var map = new SaveFolderMap(request.DataDirectory, request.Game.Id);
                List<string> folders = map.Load();

                if (request.SessionStartUtc.HasValue)
                {
                    List<string> written = UnitsWrittenSince(root, request.SessionStartUtc.Value);
                    request.Logger?.Info($"[SaveSync] {request.Game.Name}: folders under \"{root}\" written since launch " +
                                         $"({request.SessionStartUtc.Value:u}): {(written.Count == 0 ? "none" : string.Join(", ", written))}");
                    folders = map.Add(written);
                }

                return new FolderSetSaveTarget(EmulatorTag, root, folders, ArchiveName(request), learned => map.Add(learned), UnitOfEntry, IsBlankSaveFile);
            }
            catch (Exception ex)
            {
                request.Logger?.Warn($"[SaveSync] Could not locate {EmulatorTag} saves for {request.Game?.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The save folders, as paths relative to the root, that the session wrote to. The default
        /// fits saves that are single folders directly under the root.
        /// </summary>
        protected virtual List<string> UnitsWrittenSince(string root, DateTime sinceUtc) => FoldersWrittenSince(root, sinceUtc);

        /// <summary>
        /// Which save folder an archive entry belongs to, or null to leave the entry out. The
        /// default is the entry's first folder.
        /// </summary>
        protected virtual string UnitOfEntry(string entryName)
        {
            int slash = entryName.IndexOf('/');
            return slash <= 0 ? null : entryName.Substring(0, slash);
        }

        /// <summary>
        /// For saves that are single files: whether this file holds no save yet. Emulators that
        /// create an empty memory card as a game starts override this, so the empty card is not
        /// mistaken for a save. The default says every file is a real save.
        /// </summary>
        protected virtual bool IsBlankSaveFile(string path) => false;

        /// <summary>
        /// The ROM's file name where it says something; a PS3 game launches from EBOOT.BIN, which
        /// says nothing, so those are named after the game instead.
        /// </summary>
        internal static string ArchiveName(SaveTargetRequest request)
        {
            string name = string.IsNullOrEmpty(request.ContentPath)
                ? null
                : Path.GetFileNameWithoutExtension(request.ContentPath.TrimEnd('\\', '/'));

            if (string.IsNullOrEmpty(name) || name.Equals("EBOOT", StringComparison.OrdinalIgnoreCase))
                name = request.Game?.Name ?? "save";

            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            return name + ".zip";
        }

        /// <summary>The save folders holding a file written at or after the given moment.</summary>
        internal static List<string> FoldersWrittenSince(string root, DateTime sinceUtc)
        {
            var result = new List<string>();
            if (!Directory.Exists(root))
                return result;

            // A little slack for clock granularity between the launch stamp and the first write.
            DateTime threshold = sinceUtc.AddSeconds(-2);

            foreach (var folder in new DirectoryInfo(root).GetDirectories())
            {
                // Scratch folders: this plugin's own staging, and the temporary copies some
                // emulators write a save into before swapping it into place.
                if (folder.Name.StartsWith(".", StringComparison.Ordinal)
                    || folder.Name.StartsWith("$", StringComparison.Ordinal)
                    || folder.Name.EndsWith(".romm-old", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (folder.GetFiles("*", SearchOption.AllDirectories).Any(f => f.LastWriteTimeUtc >= threshold))
                    result.Add(folder.Name);
            }
            return result;
        }
    }
}
