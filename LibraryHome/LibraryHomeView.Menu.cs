using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using RomM.Games;
using RomM.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.LibraryHome
{
    /// <summary>
    /// The right-click menu of a cover on the library's Home page, after the one Playnite gives
    /// a game in its own views: play or install, the game's page, favourite, hide, completion
    /// status, then what every installed extension adds to a game's menu (this plugin's Store
    /// page, SuccessStory's, and so on). Playnite's own Edit and Remove are offered when its
    /// window has them to lend.
    /// </summary>
    public partial class LibraryHomeView
    {
        private void AttachGameMenu(FrameworkElement tile, Game game)
        {
            // Filled each time it opens, so it shows the game as it is then.
            tile.ContextMenu = new ContextMenu();
            tile.ContextMenuOpening += (s, e) =>
            {
                Game current = _api.Database.Games.Get(game.Id) ?? game;
                try
                {
                    FillGameMenu(tile.ContextMenu, current);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Home] Could not build the menu of {current.Name}: {ex.Message}");
                    e.Handled = true;
                }
            };
        }

        private void FillGameMenu(ContextMenu menu, Game game)
        {
            menu.Items.Clear();

            if (game.IsInstalled)
            {
                menu.Items.Add(MenuEntry("Play", () => _api.StartGame(game.Id), bold: true));
            }
            else
            {
                menu.Items.Add(MenuEntry("Install", () => _api.InstallGame(game.Id), bold: true));
            }
            menu.Items.Add(MenuEntry("Show in library", () => Open(game)));
            menu.Items.Add(new Separator());

            menu.Items.Add(MenuEntry(game.Favorite ? "Remove from Favorites" : "Add to Favorites", () =>
            {
                game.Favorite = !game.Favorite;
                _api.Database.Games.Update(game);
            }));
            var statuses = _api.Database.CompletionStatuses.OrderBy(c => c.Name).ToList();
            if (statuses.Count > 0)
            {
                var status = new MenuItem { Header = "Set Completion Status" };
                foreach (CompletionStatus choice in statuses)
                {
                    Guid id = choice.Id;
                    var item = MenuEntry(choice.Name, () =>
                    {
                        game.CompletionStatusId = id;
                        _api.Database.Games.Update(game);
                    });
                    item.IsCheckable = true;
                    item.IsChecked = game.CompletionStatusId == id;
                    status.Items.Add(item);
                }
                menu.Items.Add(status);
            }

            // The user's collections: each ticked when the game is in it, a click toggles it.
            if (_collections != null)
            {
                var collect = new MenuItem { Header = GameCollections.MenuSection };
                var ids = new List<Guid> { game.Id };
                List<GameCollection> collections = _collections.All();
                foreach (GameCollection collection in collections)
                {
                    Guid collectionId = collection.Id;
                    var item = MenuEntry(collection.Name, () => _collections.Toggle(collectionId, ids));
                    item.IsCheckable = true;
                    item.IsChecked = collection.Games.Contains(game.Id);
                    collect.Items.Add(item);
                }
                if (collections.Count > 0)
                    collect.Items.Add(new Separator());
                collect.Items.Add(MenuEntry("New collection...", () => _collections.CreateWithPrompt(_api, ids)));
                menu.Items.Add(collect);
            }

            // What the extensions add, in their sections ("A|B" nests B under A). Their "Manage"
            // section is this menu's own Manage, below.
            var added = ExtensionEntries(game);
            MenuItem pluginManage = added.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == ManageHeader);
            if (pluginManage != null)
                added.Remove(pluginManage);
            if (added.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (object entry in added)
                    menu.Items.Add(entry);
            }

            // Manage, as Steam's: what changes the game itself rather than plays it.
            //   Edit (Playnite's own), Hide, Uninstall (asked first), then the extensions' own
            //   (RomM's Remove from my library, Reinstall emulator, Uninstall platform) and,
            //   for a game not from RomM, Playnite's Remove. Not Playnite's Remove for a RomM
            //   game: it would offer the exclusion list, keeping it out of the Store for good.
            var manage = new MenuItem { Header = ManageHeader };
            MenuItem edit = PlayniteEntry("Edit...", game, "EditGameCommand");
            if (edit != null)
                manage.Items.Add(edit);
            manage.Items.Add(MenuEntry(game.Hidden ? "Unhide" : "Hide this game", () =>
            {
                game.Hidden = !game.Hidden;
                _api.Database.Games.Update(game);
            }));
            if (game.IsInstalled && !game.IsUninstalling && !game.IsRunning && !game.IsLaunching)
                manage.Items.Add(MenuEntry("Uninstall...", () => UninstallConfirm.AskAndUninstall(_api, new List<Game> { game })));
            var extra = new List<object>();
            if (pluginManage != null)
            {
                extra.AddRange(pluginManage.Items.Cast<object>().ToList());
                pluginManage.Items.Clear();
            }
            bool romMRemoves = extra.OfType<MenuItem>().Any(HasRomMRemove) || added.OfType<MenuItem>().Any(HasRomMRemove);
            MenuItem remove = romMRemoves ? null : PlayniteEntry("Remove...", game, "RemoveGameCommand");
            if (remove != null)
                extra.Add(remove);
            if (extra.Count > 0)
            {
                manage.Items.Add(new Separator());
                foreach (object entry in extra)
                    manage.Items.Add(entry);
            }
            SyncFirst(manage);
            menu.Items.Add(new Separator());
            menu.Items.Add(manage);
        }

        /// <summary>The submenu gathering what manages a game (Edit, Hide, Uninstall...).</summary>
        internal const string ManageHeader = "Manage";

        /// <summary>Inside Manage, at its top: cloud saves and achievements (an extension's MenuSection).</summary>
        internal const string SyncHeader = "Sync";
        internal const string SyncSection = ManageHeader + "|" + SyncHeader;

        /// <summary>Puts Manage's Sync submenu first, with a line after it.</summary>
        internal static void SyncFirst(MenuItem manage)
        {
            MenuItem sync = manage?.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == SyncHeader && m.HasItems);
            if (sync == null)
                return;
            manage.Items.Remove(sync);
            manage.Items.Insert(0, sync);
            manage.Items.Insert(1, new Separator());
            // No line twice in a row, and none at the end, now that it has moved.
            for (int i = manage.Items.Count - 1; i >= 1; i--)
            {
                bool line = manage.Items[i] is Separator;
                if (line && (i == manage.Items.Count - 1 || manage.Items[i - 1] is Separator))
                    manage.Items.RemoveAt(i);
            }
        }

        private static bool HasRomMRemove(MenuItem item) =>
            (item.Header as string) == global::RomM.RomM.RemoveMenuItem || item.Items.OfType<MenuItem>().Any(HasRomMRemove);

        private MenuItem MenuEntry(string header, Action pick, bool bold = false)
        {
            var item = new MenuItem { Header = header };
            if (bold)
                item.FontWeight = FontWeights.Bold;
            item.Click += (s, e) =>
            {
                try
                {
                    pick();
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Home] \"{header}\" failed: {ex.Message}");
                }
            };
            return item;
        }

        // ---- Collections on the page ----------------------------------------------------------

        // Right-clicking a collection's shelf heading: rename or delete it, or make another.
        private ContextMenu CollectionMenu(GameCollection collection)
        {
            Guid id = collection.Id;
            var menu = new ContextMenu();
            menu.Items.Add(MenuEntry("Rename...", () =>
            {
                var answer = _api.Dialogs.SelectString("Rename the collection:", "Rename collection", collection.Name);
                if (answer != null && answer.Result && !string.IsNullOrWhiteSpace(answer.SelectedString))
                    _collections.Rename(id, answer.SelectedString);
            }));
            menu.Items.Add(MenuEntry("Delete collection", () =>
            {
                var sure = _api.Dialogs.ShowMessage($"Delete the collection \"{collection.Name}\"? Its games stay in your library.",
                    "Delete collection", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (sure == MessageBoxResult.Yes)
                    _collections.Delete(id);
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("New collection...", () => _collections.CreateWithPrompt(_api, new Guid[0])));
            return menu;
        }

        // Every extension's entries for the game, nested by their sections.
        private List<object> ExtensionEntries(Game game)
        {
            var top = new List<object>();
            var sections = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);
            var args = new GetGameMenuItemsArgs { Games = new List<Game> { game } };

            foreach (Plugin plugin in _api.Addons.Plugins)
            {
                IEnumerable<GameMenuItem> items;
                try
                {
                    items = plugin.GetGameMenuItems(args)?.ToList();
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Home] {plugin.GetType().Name} gave no menu: {ex.Message}");
                    continue;
                }
                if (items == null)
                    continue;

                foreach (GameMenuItem item in items)
                {
                    if (item == null || string.IsNullOrEmpty(item.Description))
                        continue;
                    // This plugin's collection entries (for Playnite's own views) are above already.
                    if (item.MenuSection == GameCollections.MenuSection)
                        continue;

                    // Where it goes: at the top, or under its section (made on first use).
                    Action<object> into = o => top.Add(o);
                    string path = (item.MenuSection ?? "").TrimStart('@');
                    if (path.Length > 0)
                    {
                        string sofar = "";
                        foreach (string part in path.Split('|').Where(p => p.Length > 0))
                        {
                            sofar = sofar.Length == 0 ? part : sofar + "|" + part;
                            if (!sections.TryGetValue(sofar, out MenuItem section))
                            {
                                section = new MenuItem { Header = part };
                                sections[sofar] = section;
                                into(section);
                            }
                            MenuItem under = section;
                            into = o => under.Items.Add(o);
                        }
                    }

                    if (item.Description == "-")
                    {
                        into(new Separator());
                        continue;
                    }
                    GameMenuItem chosen = item;
                    into(MenuEntry(item.Description, () => chosen.Action?.Invoke(new GameMenuItemActionArgs
                    {
                        Games = new List<Game> { game },
                        SourceItem = chosen,
                    })));
                }
            }
            return top;
        }

        // One of Playnite's own commands for a game, borrowed from its main window, if it has
        // one by that name that accepts the game.
        private static MenuItem PlayniteEntry(string header, Game game, string commandName)
        {
            try
            {
                object model = Application.Current?.MainWindow?.DataContext;
                if (model == null)
                    return null;
                PropertyInfo property = model.GetType().GetProperty(commandName, BindingFlags.Public | BindingFlags.Instance);
                if (!(property?.GetValue(model) is ICommand command) || !command.CanExecute(game))
                    return null;
                var item = new MenuItem { Header = header };
                item.Click += (s, e) =>
                {
                    try
                    {
                        command.Execute(game);
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Home] Playnite's {commandName} failed: {ex.Message}");
                    }
                };
                return item;
            }
            catch
            {
                return null;
            }
        }
    }
}
