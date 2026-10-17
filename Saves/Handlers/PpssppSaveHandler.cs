using System;
using System.IO;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// PPSSPP's saves: folders under PSP\SAVEDATA named after the game's title id
    /// (ULUS10041, ULUS10041DATA00, ...). See <see cref="LearnedFolderSaveHandler"/>.
    /// </summary>
    internal sealed class PpssppSaveHandler : LearnedFolderSaveHandler
    {
        public override string EmulatorTag => "ppsspp";

        protected override string Keyword => "ppsspp";

        protected override string SaveRoot(string installDir) => FindSaveRoot(installDir);

        /// <summary>
        /// A portable PPSSPP keeps its memory stick beside the executable; an installed one keeps
        /// it under Documents\PPSSPP. The portable location is also the answer when neither
        /// exists yet, since that is where a PPSSPP unpacked into a folder creates it.
        /// </summary>
        internal static string FindSaveRoot(string installDir)
        {
            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "memstick")))
                return Path.Combine(installDir, "memstick", "PSP", "SAVEDATA");

            string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PPSSPP");
            if (Directory.Exists(documents))
                return Path.Combine(documents, "PSP", "SAVEDATA");

            return string.IsNullOrEmpty(installDir) ? null : Path.Combine(installDir, "memstick", "PSP", "SAVEDATA");
        }
    }
}
