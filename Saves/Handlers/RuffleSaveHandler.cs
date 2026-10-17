using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Ruffle's (Flash) saves: a Flash game's saves (its SharedObjects) are .sol files filed under
    /// the game's own location: saves\localhost\&lt;the .swf's path, without its drive&gt;\&lt;name&gt;.sol,
    /// the path as Ruffle reads it from the file's URL (shared_object.rs). The saves folder is
    /// the one set up beside Ruffle (EmulatorLaunch: --save-directory {EmulatorDir}\saves).
    ///
    /// The game's folder is worked out from where the .swf is on this computer, so the save
    /// travels as that one folder (named after the .swf) and lands under wherever the game is on
    /// the next. A game that files its saves elsewhere (a shared "localPath", such as "/") is
    /// not followed.
    /// </summary>
    internal sealed class RuffleSaveHandler : ISaveHandler
    {
        public string EmulatorTag => "ruffle";

        public bool CanHandle(Emulator emulator)
        {
            return emulator != null && (Mentions(emulator.BuiltInConfigId) || Mentions(emulator.Name) || Mentions(emulator.InstallDir));
        }

        private static bool Mentions(string value) =>
            !string.IsNullOrEmpty(value) && value.IndexOf("ruffle", StringComparison.OrdinalIgnoreCase) >= 0;

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                if (request.Game == null || string.IsNullOrEmpty(request.EmulatorInstallDir) || string.IsNullOrEmpty(request.ContentPath))
                    return null;
                string swf = Path.GetFullPath(request.ContentPath);
                if (!swf.EndsWith(".swf", StringComparison.OrdinalIgnoreCase))
                    return null;

                // file:///G:/Games/My%20Game/game.swf -> "Games/My%20Game/game.swf": the URL's
                // path, escaped as in the URL, less its leading "/" and drive.
                string path = new Uri(swf).AbsolutePath.TrimStart('/');
                if (path.Length >= 3 && path[1] == ':' && path[2] == '/')
                    path = path.Substring(3);
                int slash = path.LastIndexOf('/');
                string folder = slash < 0 ? "" : path.Substring(0, slash);
                string unit = slash < 0 ? path : path.Substring(slash + 1);

                string root = Path.Combine(request.EmulatorInstallDir, "saves", "localhost", folder.Replace('/', Path.DirectorySeparatorChar));
                return new FolderSetSaveTarget(EmulatorTag, root, new List<string> { unit },
                    LearnedFolderSaveHandler.ArchiveName(request), learned => { });
            }
            catch (Exception ex)
            {
                request.Logger?.Warn($"[SaveSync] Could not locate Ruffle saves for {request.Game?.Name}: {ex.Message}");
                return null;
            }
        }
    }
}
