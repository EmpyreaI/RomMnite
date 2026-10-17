using Playnite.SDK;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Ludusavi (github.com/mtkennerly/ludusavi, MIT), which finds Windows games' saves, fetched
    /// when a Windows game's saves are to be synced and it is not there: the newest release's
    /// Windows build, unpacked into the plugin's Tools folder (LudusaviSaveHandler.ToolsFolder).
    /// Once a Playnite session, in the background; <see cref="Installed"/> when it is in.
    /// </summary>
    internal static class LudusaviInstaller
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static int s_state;   // 0 not tried, 1 fetching, 2 done, 3 failed

        /// <summary>Raised (off the UI thread) once Ludusavi has been fetched.</summary>
        public static event Action Installed;

        /// <summary>
        /// Starts fetching Ludusavi if it has not been tried this session. Whether it is being
        /// fetched now (false: it failed this session, or there is nowhere to put it).
        /// </summary>
        public static bool EnsureInBackground()
        {
            string folder = LudusaviSaveHandler.ToolsFolder;
            if (folder == null)
                return false;
            int state = Interlocked.CompareExchange(ref s_state, 1, 0);
            if (state == 1)
                return true;
            if (state != 0)
                return false;
            Task.Run(() =>
            {
                try
                {
                    s_log.Info("[SaveSync] Downloading Ludusavi, which finds Windows games' saves.");
                    string exe = Setup.EmulatorSetup.InstallLudusavi(folder);
                    if (exe == null)
                        throw new InvalidOperationException("no Windows build in its newest releases");
                    Interlocked.Exchange(ref s_state, 2);
                    s_log.Info($"[SaveSync] Ludusavi is ready ({exe}).");
                    Installed?.Invoke();
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref s_state, 3);
                    s_log.Warn($"[SaveSync] Could not download Ludusavi: {ex.Message}");
                }
            });
            return true;
        }
    }
}
