using System;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Which user an emulator with users of its own is set to start as, read from its settings:
    /// RPCS3's [Users] active_user (GuiConfigs\persistent_settings.dat) and Xenia Canary's
    /// [Profiles] logged_profile_slot_0_xuid (xenia-canary.config.toml). The plugin sets them per
    /// account (Account.EmulatorUsers); the save handlers follow them. Kept free of the plugin so
    /// the handlers stay testable on their own.
    /// </summary>
    internal static class EmulatorActiveUser
    {
        /// <summary>The user RPCS3 is set to start as, or null when none is set.</summary>
        internal static string Rpcs3ConfiguredUser(string installDir)
        {
            try
            {
                if (string.IsNullOrEmpty(installDir))
                    return null;
                string file = Path.Combine(installDir, "GuiConfigs", "persistent_settings.dat");
                if (!File.Exists(file))
                    return null;
                string section = null;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal))
                        section = line;
                    else if (string.Equals(section, "[Users]", StringComparison.OrdinalIgnoreCase) && line.StartsWith("active_user", StringComparison.OrdinalIgnoreCase))
                    {
                        string value = line.Substring(line.IndexOf('=') + 1).Trim();
                        return value.Length == 8 && value.All(char.IsDigit) ? value : null;
                    }
                }
            }
            catch
            {
                // Not set, as far as can be told.
            }
            return null;
        }

        /// <summary>
        /// Where Xenia keeps its content and config: beside its executable (its default on
        /// Windows), or Documents\Xenia when only that is in use.
        /// </summary>
        internal static string XeniaRoot(string installDir)
        {
            if (!string.IsNullOrEmpty(installDir)
                && (Directory.Exists(Path.Combine(installDir, "content")) || File.Exists(Path.Combine(installDir, "xenia-canary.config.toml"))))
                return installDir;
            string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Xenia");
            return Directory.Exists(Path.Combine(documents, "content")) || string.IsNullOrEmpty(installDir) ? documents : installDir;
        }

        /// <summary>The profile Xenia is set to sign in (its xuid, upper case), or null.</summary>
        internal static string XeniaSignedIn(string installDir)
        {
            try
            {
                string config = Path.Combine(XeniaRoot(installDir), "xenia-canary.config.toml");
                if (!File.Exists(config))
                    return null;
                string line = File.ReadAllLines(config).FirstOrDefault(l => l.TrimStart().StartsWith("logged_profile_slot_0_xuid", StringComparison.OrdinalIgnoreCase));
                string value = line?.Substring(line.IndexOf('=') + 1).Split('#')[0].Trim().Trim('"');
                return string.IsNullOrEmpty(value) ? null : value.ToUpperInvariant();
            }
            catch
            {
                return null;
            }
        }
    }
}
