using System;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Eden's (Switch) saves: nand\user\save\0000000000000000\&lt;profile&gt;\&lt;title id&gt; folders,
    /// one per game, in its user folder ("user" beside it, as set up here, else %APPDATA%\eden).
    /// The profile folder is named after Eden's user profile, which is made up afresh on every
    /// install; so the saves travel as their title-id folders alone and go under whichever
    /// profile this Eden has (the one written most recently, when it has several). An Eden that
    /// has never saved anything has no profile folder yet: its saves start syncing once it has.
    /// See <see cref="LearnedFolderSaveHandler"/>.
    /// </summary>
    internal sealed class EdenSaveHandler : LearnedFolderSaveHandler
    {
        public override string EmulatorTag => "eden";

        protected override string Keyword => "eden";

        protected override string SaveRoot(string installDir)
        {
            string user = !string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "user"))
                ? Path.Combine(installDir, "user")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eden");
            string saves = Path.Combine(user, "nand", "user", "save", "0000000000000000");
            if (!Directory.Exists(saves))
                return null;
            // Profiles are 32 hex digits; an all-zero one is not a user's.
            DirectoryInfo profile = new DirectoryInfo(saves).GetDirectories()
                .Where(d => d.Name.Length == 32 && d.Name.Trim('0').Length > 0)
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .FirstOrDefault();
            return profile?.FullName;
        }
    }
}
