using System;
using System.IO;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Vita3K's (PS Vita) saves: ux0\user\00\savedata\&lt;title id&gt; folders (PCSE00000...), one
    /// per game. The emulated memory card is "portable\fs" beside it when a "portable" folder is
    /// there (app_init.cpp), as set up here, else %APPDATA%\Vita3K\Vita3K.
    /// See <see cref="LearnedFolderSaveHandler"/>.
    /// </summary>
    internal sealed class Vita3KSaveHandler : LearnedFolderSaveHandler
    {
        public override string EmulatorTag => "vita3k";

        protected override string Keyword => "vita3k";

        protected override string SaveRoot(string installDir)
        {
            string fs = !string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "portable"))
                ? Path.Combine(installDir, "portable", "fs")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vita3K", "Vita3K");
            return Path.Combine(fs, "ux0", "user", "00", "savedata");
        }
    }
}
