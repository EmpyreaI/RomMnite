using System;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// RPCS3's saves: folders under dev_hdd0\home\&lt;user&gt;\savedata named after the game's title
    /// id (BLUS30443, BLUS30443-AUTOSAVE, ...). See <see cref="LearnedFolderSaveHandler"/>.
    /// RPCS3 always keeps dev_hdd0 beside its executable, so the install folder is enough.
    /// </summary>
    internal sealed class Rpcs3SaveHandler : LearnedFolderSaveHandler
    {
        // The user RPCS3 creates on first run, and the only one most installs ever have.
        private const string DefaultUser = "00000001";

        public override string EmulatorTag => "rpcs3";

        protected override string Keyword => "rpcs3";

        protected override string SaveRoot(string installDir) => FindSaveRoot(installDir);

        internal static string FindSaveRoot(string installDir)
        {
            if (string.IsNullOrEmpty(installDir))
                return null;

            string home = Path.Combine(installDir, "dev_hdd0", "home");
            // The user RPCS3 is set to start as (each account's own; see Account.EmulatorUsers),
            // else the one in use as far as can be told.
            string configured = EmulatorActiveUser.Rpcs3ConfiguredUser(installDir);
            if (configured != null && Directory.Exists(Path.Combine(home, configured)))
                return Path.Combine(home, configured, "savedata");
            return Path.Combine(home, ActiveUser(home), "savedata");
        }

        /// <summary>
        /// With several RPCS3 users, the one whose saves were written most recently is taken to be
        /// the one in use. With one user, or none yet, it is the default.
        /// </summary>
        private static string ActiveUser(string home)
        {
            try
            {
                if (!Directory.Exists(home))
                    return DefaultUser;

                var users = new DirectoryInfo(home).GetDirectories()
                    .Where(d => d.Name.Length == 8 && d.Name.All(char.IsDigit))
                    .ToList();
                if (users.Count <= 1)
                    return users.FirstOrDefault()?.Name ?? DefaultUser;

                return users
                    .OrderByDescending(u =>
                    {
                        var savedata = new DirectoryInfo(Path.Combine(u.FullName, "savedata"));
                        return savedata.Exists ? savedata.LastWriteTimeUtc : DateTime.MinValue;
                    })
                    .ThenBy(u => u.Name, StringComparer.Ordinal)
                    .First().Name;
            }
            catch
            {
                return DefaultUser;
            }
        }
    }
}
