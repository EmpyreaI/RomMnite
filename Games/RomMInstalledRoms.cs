using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Games
{
    /// <summary>
    /// Works out, for a game being imported, whether its files are already on disk and which of
    /// them to launch -- the same answer an install gives when it finishes.
    ///
    /// Looking for the downloaded file by name is not enough: an archive that was extracted is
    /// deleted afterwards, and a folder download never had a file of that name at all, so a game
    /// installed either way would import as "not installed" with nothing to play.
    /// </summary>
    internal static class RomMInstalledRoms
    {
        /// <param name="gamePath">Where the downloaded file itself would be, as a real path.</param>
        /// <param name="installDir">The game's install folder, as a real path.</param>
        /// <param name="flatLayout">
        /// Whether the game shares its folder with the rest of the platform. Files there cannot
        /// be attributed to one game, so only the exact file counts.
        /// </param>
        /// <param name="pendingArchive">
        /// The archive a folder download arrives as. While it is still there the download has
        /// not finished extracting, so the folder's contents are not yet an install.
        /// </param>
        /// <returns>Real paths of the files to launch; empty when the game is not installed.</returns>
        public static List<string> Detect(string gamePath, string installDir, bool flatLayout, bool singleRomInFolder,
            string playableFile, string pendingArchive, IList<string> supportedTypes, bool preferM3u)
        {
            var found = new List<string>();

            if (!string.IsNullOrEmpty(gamePath) && File.Exists(gamePath))
            {
                // An archive cut short (its download interrupted) is not an install; a whole one
                // is, and is unpacked before it is played if it should have been (PendingUnpack).
                if (PendingUnpack.LooksLikeArchive(gamePath) && !PendingUnpack.IsComplete(gamePath))
                    return found;
                found.Add(gamePath);
                return found;
            }

            if (flatLayout || string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return found;

            // The folder's archive still there: downloaded, but not (or not wholly) unpacked. A
            // whole archive counts as installed, to be unpacked before it is played; one cut
            // short does not.
            if (!string.IsNullOrEmpty(pendingArchive) && File.Exists(pendingArchive))
            {
                if (PendingUnpack.IsComplete(pendingArchive))
                    found.Add(pendingArchive);
                return found;
            }

            // A single ROM kept in a folder has one launchable file; its extras are not ROMs.
            if (singleRomInFolder && !string.IsNullOrEmpty(playableFile) && RomMInstallPaths.IsContained(playableFile))
            {
                string primary = Path.Combine(installDir, playableFile);
                if (File.Exists(primary))
                {
                    found.Add(primary);
                    return found;
                }
            }

            // Otherwise whatever the emulator can open. With no list of types to go by, a file of
            // any kind would count, which says too little to call the game installed.
            var types = (supportedTypes ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t) && t.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (types.Count == 0)
                return found;

            var files = types
                .SelectMany(t => Directory.GetFiles(installDir, "*." + t, SearchOption.AllDirectories))
                // "*.iso" also matches "x.iso2" on Windows; keep to the exact extension.
                .Where(f => types.Any(t => f.EndsWith("." + t, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (preferM3u)
            {
                string playlist = files.FirstOrDefault(f => f.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase));
                if (playlist != null)
                {
                    found.Add(playlist);
                    return found;
                }
            }

            found.AddRange(files.Where(f => !f.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)));
            return found;
        }

        /// <summary>The file types the mapping's emulator profile can open, or null when unknown.</summary>
        public static List<string> SupportedTypes(EmulatorMapping mapping)
        {
            try
            {
                if (mapping?.EmulatorProfile is CustomEmulatorProfile custom)
                    return custom.ImageExtensions;

                if (mapping?.EmulatorProfile is BuiltInEmulatorProfile builtIn)
                {
                    return API.Instance.Emulation.Emulators
                        .FirstOrDefault(e => e.Id == mapping.Emulator?.BuiltInConfigId)?
                        .Profiles
                        .FirstOrDefault(p => p.Name == builtIn.Name)?
                        .ImageExtensions;
                }
            }
            catch
            {
                // Not knowing the types only means the folder is not scanned.
            }
            return null;
        }
    }
}
