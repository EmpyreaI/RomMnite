using System;
using System.IO;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Cemu's (Wii U) saves: mlc01\usr\save\00050000\&lt;title id, low half&gt; folders, one per game,
    /// each holding every Wii U account's save and the common one. mlc01 is in Cemu's user data
    /// folder (ActiveSettings::GetDefaultMLCPath): "portable" beside it, as set up here, else
    /// %APPDATA%\Cemu. A custom mlc path set in Cemu is not followed.
    /// See <see cref="LearnedFolderSaveHandler"/>.
    /// </summary>
    internal sealed class CemuSaveHandler : LearnedFolderSaveHandler
    {
        public override string EmulatorTag => "cemu";

        protected override string Keyword => "cemu";

        protected override string SaveRoot(string installDir)
        {
            string data = !string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "portable"))
                ? Path.Combine(installDir, "portable")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cemu");
            return Path.Combine(data, "mlc01", "usr", "save", "00050000");
        }
    }
}
