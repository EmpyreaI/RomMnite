using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Native PC games, which are launched directly rather than through an emulator and keep
    /// their saves wherever each game likes. Finding and restoring those is left to Ludusavi;
    /// see <see cref="LudusaviSaveTarget"/>.
    ///
    /// Such games are launched here through an emulator entry that simply runs the game's file,
    /// conventionally named "Windows" or "PC", which is how they are recognised.
    /// </summary>
    internal sealed class LudusaviSaveHandler : ISaveHandler
    {
        public string EmulatorTag => "ludusavi";

        public bool CanHandle(Emulator emulator)
        {
            string name = emulator?.Name;
            if (string.IsNullOrEmpty(name))
                return false;

            return name.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.Equals("PC", StringComparison.OrdinalIgnoreCase);
        }

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            if (request.Game == null || string.IsNullOrEmpty(request.DataDirectory))
                return null;

            string exe = FindLudusavi();
            if (exe == null)
            {
                request.Logger?.Warn("[SaveSync] ludusavi.exe was not found (Playnite's \"resources\" folder, or the plugin's Tools folder); " +
                                     $"cannot sync saves for {request.Game.Name}.");
                // Fetched for it, once; the saves sync once it is in (Installed).
                bool fetching = LudusaviInstaller.EnsureInBackground();
                request.Reason = fetching
                    ? "Windows games' saves are found by Ludusavi, which is being downloaded now. They sync as soon as it's ready."
                    : "Windows games' saves are found by Ludusavi, which isn't installed and couldn't be downloaded. " +
                      $"Put ludusavi.exe in {ToolsFolder ?? "Playnite's resources folder"}.";
                return null;
            }

            // Beside the other handlers' notes, one working folder per game.
            string ludusaviDir = Path.Combine(Path.GetDirectoryName(request.DataDirectory.TrimEnd('\\', '/')), "Ludusavi");
            string workDir = Path.Combine(ludusaviDir, request.Game.Id.ToString());

            // A name set by hand for this game wins over the library's.
            string customTitle = LudusaviTitles.Get(ludusaviDir, request.Game.Id);

            string fileName = request.Game.Name ?? "save";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(invalid, '_');

            return new LudusaviSaveTarget(exe, workDir, request.Game.Name, fileName + ".zip", request.Logger, customTitle,
                SteamEmuCloudFolder(request.Game), InstallFolder(request.Game));
        }

        // The game's install folder as a real path ({PlayniteDir} expanded); null when not installed.
        private static string InstallFolder(Game game)
        {
            string dir = game?.InstallDirectory;
            if (string.IsNullOrEmpty(dir) || game.IsInstalled == false)
                return null;
            dir = dir.Replace("{PlayniteDir}", AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'));
            return Directory.Exists(dir) ? dir : null;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeyValuePair<int, string>> s_steamEmu =
            new System.Collections.Concurrent.ConcurrentDictionary<string, KeyValuePair<int, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The Steam emulator's cloud folder for a game run with one (its app id from the
        /// emulator's files beside steam_api), or null. The look through the install folder is
        /// done once per folder.
        /// </summary>
        private static string SteamEmuCloudFolder(Game game)
        {
            try
            {
                string installDir = InstallFolder(game);
                if (installDir == null)
                    return null;
                KeyValuePair<int, string> found = s_steamEmu.GetOrAdd(installDir, dir =>
                {
                    foreach (string folder in global::RomM.Games.SteamEmuFiles.DllFolders(dir, depth: 9))
                    {
                        int appId = global::RomM.Games.SteamEmuFiles.AppIdIn(folder);
                        if (appId > 0 && global::RomM.Games.SteamEmuFiles.IsEmulatorFolder(folder))
                            return new KeyValuePair<int, string>(appId, folder);
                    }
                    return new KeyValuePair<int, string>(0, null);
                });
                return found.Key > 0 ? global::RomM.Games.SteamEmuFiles.CloudFolder(found.Key, found.Value) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Ludusavi is expected in "resources" under the Playnite folder, so it travels with a
        /// copied Playnite. This runs inside Playnite, whose base directory is that folder.
        /// </summary>
        internal static string FindLudusavi()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string candidate in new[]
            {
                Path.Combine(baseDir, "resources", "ludusavi.exe"),
                Path.Combine(baseDir, "resources", "ludusavi", "ludusavi.exe"),
                Path.Combine(baseDir, "ludusavi.exe"),
                ToolsFolder == null ? null : Path.Combine(ToolsFolder, "ludusavi.exe"),
            })
            {
                if (candidate != null && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        /// <summary>Where the plugin puts the Ludusavi it downloads (its data's Tools folder).</summary>
        internal static string ToolsFolder { get; set; }
    }
}
