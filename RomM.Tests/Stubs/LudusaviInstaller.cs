using System;

namespace RomM.Saves.Handlers
{
    // Stands in for the plugin's LudusaviInstaller, which sets Ludusavi up through EmulatorSetup
    // (WPF, not built here). In tests Ludusavi is never fetched.
    internal static class LudusaviInstaller
    {
#pragma warning disable 67
        public static event Action Installed;
#pragma warning restore 67

        public static bool EnsureInBackground() => false;
    }
}
