using Playnite.SDK;
using Playnite.SDK.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace RomM.Games
{
    /// <summary>
    /// The question before a game is uninstalled (Playnite asks none): its files go, the game
    /// stays in the library to install again. Asked by the menus that offer Uninstall (Home's,
    /// and Playnite's own, whose item is taken over for it).
    /// </summary>
    internal static class UninstallConfirm
    {
        /// <summary>Asks, then uninstalls those confirmed. On the UI thread.</summary>
        public static void AskAndUninstall(IPlayniteAPI api, IList<Game> games)
        {
            var installed = (games ?? new List<Game>()).Where(g => g != null && g.IsInstalled && !g.IsUninstalling && !g.IsRunning && !g.IsLaunching).ToList();
            if (installed.Count == 0)
                return;
            string what = installed.Count == 1 ? installed[0].Name : $"{installed.Count} games";
            string message = installed.Count == 1
                ? $"Uninstall {what}?\n\nIts files are deleted from this computer. It stays in your library, to install again."
                : $"Uninstall {what}?\n\nTheir files are deleted from this computer. They stay in your library, to install again.";
            if (api.Dialogs.ShowMessage(message, "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            foreach (Game game in installed)
                api.UninstallGame(game.Id);
        }
    }
}
