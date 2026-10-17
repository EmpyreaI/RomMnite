using System;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Azahar's (3DS) saves: the emulated SD card's
    /// Nintendo 3DS\&lt;id0&gt;\&lt;id1&gt;\title\00040000\&lt;title id, low half&gt; folders, one per game
    /// (archive_source_sd_savedata.cpp: title\{high:08x}\{low:08x}\data\...). Azahar's two ids
    /// are fixed (all zeros), so the folders are the same on every computer.
    /// See <see cref="LearnedFolderSaveHandler"/>.
    /// </summary>
    internal sealed class AzaharSaveHandler : LearnedFolderSaveHandler
    {
        private const string Id = "00000000000000000000000000000000";

        public override string EmulatorTag => "azahar";

        protected override string Keyword => "azahar";

        protected override string SaveRoot(string installDir)
        {
            // Portable (a "user" folder beside it, as set up here), else %APPDATA%\Azahar.
            string user = !string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "user"))
                ? Path.Combine(installDir, "user")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Azahar");
            string card = Path.Combine(user, "sdmc", "Nintendo 3DS");
            // The ids as found, should a build ever use others; else Azahar's own.
            string id0 = Directory.Exists(card) ? new DirectoryInfo(card).GetDirectories().Select(d => d.Name).FirstOrDefault(n => n.Length == 32) : null;
            id0 = id0 ?? Id;
            string id0Path = Path.Combine(card, id0);
            string id1 = Directory.Exists(id0Path) ? new DirectoryInfo(id0Path).GetDirectories().Select(d => d.Name).FirstOrDefault(n => n.Length == 32) : null;
            return Path.Combine(id0Path, id1 ?? Id, "title", "00040000");
        }
    }
}
