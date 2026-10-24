using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;

namespace RomM.Games
{
    /// <summary>
    /// One game in the library on several platforms: one copy shown, the others hidden, and
    /// reached from the drop-down beside its Play / Install button (GameActionButton).
    ///
    /// The same game: RomM games of this account's library with the same name (letters and
    /// digits, letter case aside, an edition's words at its end left out: SameGame.Key) on
    /// different platforms. The shown copy: an installed one if
    /// any (the best platform among them), else one whose emulator is ready, else the best
    /// platform (PlatformRanks). Re-worked out as the library changes (installs and
    /// uninstalls, games added and removed).
    ///
    /// Picking another copy from the drop-down swaps the library over to it (<see cref="ShowCopy"/>):
    /// it is shown and looked at in place of the usual one, its own button playing or
    /// installing it, until another game is looked at, when the group goes back to its usual
    /// copy (<see cref="Selected"/>).
    ///
    /// The copy picked last is also the group's usual copy from then on, whenever it is
    /// installed (picked before it was, from when its install is done), until another copy is
    /// picked or it is uninstalled (kept in VersionsPreferred.json, by the game's name).
    ///
    /// The others are hidden with Playnite's own Hidden flag, and only those this hid are ever
    /// shown again by it (kept in VersionsHidden.json), so a game the user hid stays hidden; a
    /// game the user hid is not chosen to be shown either, while another copy can be.
    /// </summary>
    internal sealed class LibraryVersions
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly RomM _plugin;
        private readonly string _file;
        private readonly object _lock = new object();
        private HashSet<Guid> _hiddenByUs = new HashSet<Guid>();
        private Dictionary<Guid, List<Guid>> _groups = new Dictionary<Guid, List<Guid>>();
        private DispatcherTimer _soon;
        private bool _working;
        private bool _logged;   // what was found is logged once a start, then when it changes

        // A copy picked from the drop-down, shown in its group's place for now (empty: none),
        // and that group's copies.
        private Guid _picked = Guid.Empty;
        private HashSet<Guid> _pickedGroup = new HashSet<Guid>();

        // The copy picked last of each game (by its name's key), the usual copy while installed.
        private readonly string _preferredFile;
        private Dictionary<string, Guid> _preferred = new Dictionary<string, Guid>();

        public LibraryVersions(RomM plugin, string folder)
        {
            _plugin = plugin;
            _file = Path.Combine(folder, "VersionsHidden.json");
            _preferredFile = Path.Combine(folder, "VersionsPreferred.json");
            try
            {
                if (File.Exists(_preferredFile))
                    _preferred = JsonConvert.DeserializeObject<Dictionary<string, Guid>>(File.ReadAllText(_preferredFile)) ?? new Dictionary<string, Guid>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Versions] Could not read the preferred copies: {ex.Message}");
            }
            try
            {
                if (File.Exists(_file))
                    _hiddenByUs = JsonConvert.DeserializeObject<HashSet<Guid>>(File.ReadAllText(_file)) ?? new HashSet<Guid>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Versions] Could not read the hidden copies: {ex.Message}");
            }
        }

        /// <summary>Whether this hid the game (another copy of it is shown).</summary>
        public bool HidThis(Guid gameId)
        {
            lock (_lock)
                return _hiddenByUs.Contains(gameId);
        }

        /// <summary>Starts following the library. On the UI thread, once the library is loaded.</summary>
        public void Start()
        {
            _soon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _soon.Tick += (s, e) =>
            {
                _soon.Stop();
                Reconcile();
            };
            _plugin.Playnite.Database.Games.ItemCollectionChanged += (s, e) => Soon();
            _plugin.Playnite.Database.Games.ItemUpdated += (s, e) =>
            {
                // A preferred copy uninstalled: no longer preferred.
                var uninstalled = e.UpdatedItems.Where(u => u.OldData.IsInstalled && !u.NewData.IsInstalled).Select(u => u.NewData.Id).ToList();
                if (uninstalled.Count > 0)
                {
                    bool dropped;
                    lock (_lock)
                    {
                        var keys = _preferred.Where(p => uninstalled.Contains(p.Value)).Select(p => p.Key).ToList();
                        foreach (string key in keys)
                            _preferred.Remove(key);
                        dropped = keys.Count > 0;
                    }
                    if (dropped)
                        SavePreferred();
                }
                if (_working)
                    return;
                // Only what decides the groups or the shown copy.
                if (e.UpdatedItems.Any(u => u.NewData.PluginId == _plugin.Id
                        && (u.OldData.IsInstalled != u.NewData.IsInstalled || u.OldData.Name != u.NewData.Name
                            || u.OldData.Hidden != u.NewData.Hidden || !SamePlatforms(u.OldData, u.NewData))))
                    Soon();
            };
            Reconcile();
        }

        private static bool SamePlatforms(Game a, Game b) =>
            (a.PlatformIds ?? new List<Guid>()).SequenceEqual(b.PlatformIds ?? new List<Guid>());

        /// <summary>Soon (on the UI thread): once changes have settled.</summary>
        public void Soon()
        {
            try
            {
                _plugin.Playnite.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    _soon?.Stop();
                    _soon?.Start();
                }));
            }
            catch
            {
                // Closing.
            }
        }

        /// <summary>The game's other copies (hidden or not), best first; empty when it has none.</summary>
        public List<Game> Others(Game game)
        {
            if (game == null)
                return new List<Game>();
            List<Guid> group;
            lock (_lock)
            {
                if (!_groups.TryGetValue(game.Id, out group))
                    return new List<Game>();
            }
            return group.Where(id => id != game.Id)
                .Select(id => _plugin.Playnite.Database.Games.Get(id))
                .Where(g => g != null)
                .OrderByDescending(Score)
                .ToList();
        }

        /// <summary>
        /// The library swapped over to another copy of the game being looked at: that copy shown
        /// (and this one hidden) and looked at. On the UI thread.
        /// </summary>
        public void ShowCopy(Game current, Game other)
        {
            if (current == null || other == null)
                return;
            lock (_lock)
            {
                if (!_groups.TryGetValue(current.Id, out List<Guid> group) || !group.Contains(other.Id))
                    return;
                _picked = other.Id;
                _pickedGroup = new HashSet<Guid>(group);
                _preferred[Key(other.Name)] = other.Id;
            }
            s_log.Info($"[Versions] {other.Name}: {other.Platforms?.FirstOrDefault()?.Name ?? "?"} is now the usual copy"
                + (other.IsInstalled ? "." : ", once installed."));
            SavePreferred();
            Reconcile();
            _plugin.Playnite.MainView.SelectGame(other.Id);
        }

        /// <summary>
        /// A game was looked at (OnGameSelected). Another game than the swapped-to copy's group:
        /// that group goes back to its usual copy.
        /// </summary>
        public void Selected(Guid gameId)
        {
            if (gameId == Guid.Empty)
                return;   // nothing looked at for a moment (the swap itself does this)
            bool back;
            lock (_lock)
                back = _picked != Guid.Empty && !_pickedGroup.Contains(gameId);
            if (!back)
                return;
            lock (_lock)
            {
                _picked = Guid.Empty;
                _pickedGroup = new HashSet<Guid>();
            }
            Soon();
        }

        // The game's name without an edition's words (SameGame.Key): "Metal Gear Solid: Peace
        // Walker - Master Collection Version" is a copy of "Metal Gear Solid: Peace Walker".
        private static string Key(string name) => SameGame.Key(name);

        // Its platform's specification id, or the one its platform's name stands for (platforms
        // made by name have none).
        private static string SpecOf(Game game)
        {
            Platform platform = game.Platforms?.FirstOrDefault(p => p != null);
            return platform == null ? "" : PlatformRanks.SpecOf(platform.SpecificationId, platform.Name);
        }

        // Which platform it is: its first platform's id (two copies of one game are on different ones).
        private static Guid PlatformOf(Game game) => game.PlatformIds?.FirstOrDefault() ?? Guid.Empty;

        // Best first: installed, then ready to play (its emulator here), then the platform.
        private int Score(Game game)
        {
            int score = PlatformRanks.OfSpec(SpecOf(game));
            if (game.IsInstalled)
                score += 10000;
            try
            {
                if (!_plugin.NeedsEmulator(game))
                    score += 1000;
            }
            catch
            {
                // Unknown: no bonus.
            }
            return score;
        }

        /// <summary>The groups worked out again, and each group's copies shown or hidden. On the UI thread.</summary>
        public void Reconcile()
        {
            if (_working)
                return;
            _working = true;
            try
            {
                IItemCollection<Game> games = _plugin.Playnite.Database.Games;
                var mine = games.Where(g => g.PluginId == _plugin.Id && _plugin.InAccountLibrary(g)).ToList();

                // Copies of one game: same name, more than one platform.
                var groups = mine.GroupBy(g => Key(g.Name))
                    .Where(g => g.Key.Length > 0 && g.Select(PlatformOf).Distinct().Count() > 1)
                    .Select(g => g.ToList())
                    .ToList();

                var byGame = new Dictionary<Guid, List<Guid>>();
                var hidden = new HashSet<Guid>();
                var changed = new List<Game>();
                Guid selected = _plugin.Playnite.MainView.SelectedGames?.FirstOrDefault()?.Id ?? Guid.Empty;
                Guid select = Guid.Empty;
                lock (_lock)
                {
                    foreach (List<Game> group in groups)
                    {
                        var ids = group.Select(g => g.Id).ToList();
                        foreach (Guid id in ids)
                            byGame[id] = ids;

                        // Shown: the copy picked from the drop-down, for now; else the one picked
                        // last, if installed; else the best of those the user has not hidden
                        // themselves.
                        var shownable = group.Where(g => !g.Hidden || _hiddenByUs.Contains(g.Id)).ToList();
                        Game preferred = _preferred.TryGetValue(Key(group[0].Name), out Guid preferredId)
                            ? shownable.FirstOrDefault(g => g.Id == preferredId && g.IsInstalled)
                            : null;
                        Game shown = group.FirstOrDefault(g => g.Id == _picked)
                                     ?? preferred
                                     ?? shownable.OrderByDescending(Score).FirstOrDefault();
                        if (shown == null)
                            continue;
                        foreach (Game game in group)
                        {
                            if (game.Id == shown.Id)
                            {
                                if (game.Hidden && _hiddenByUs.Contains(game.Id))
                                {
                                    game.Hidden = false;
                                    changed.Add(game);
                                }
                                continue;
                            }
                            if (!game.Hidden)
                            {
                                game.Hidden = true;
                                changed.Add(game);
                                hidden.Add(game.Id);
                                // The copy being looked at gave way: the shown one is looked at.
                                if (game.Id == selected)
                                    select = shown.Id;
                            }
                            else if (_hiddenByUs.Contains(game.Id))
                                hidden.Add(game.Id);
                        }
                    }

                    // Hidden here before, but no longer a lesser copy (its other copies gone).
                    foreach (Guid id in _hiddenByUs.Where(id => !hidden.Contains(id) && !byGame.ContainsKey(id)).ToList())
                    {
                        Game game = games.Get(id);
                        if (game != null && game.Hidden && _plugin.InAccountLibrary(game))
                        {
                            game.Hidden = false;
                            changed.Add(game);
                        }
                    }
                    // Kept: those still hidden as lesser copies, and those of other accounts' libraries.
                    _hiddenByUs = new HashSet<Guid>(hidden.Concat(_hiddenByUs.Where(id => games.Get(id) is Game g && !_plugin.InAccountLibrary(g))));
                    _groups = byGame;
                }

                if (changed.Count > 0)
                {
                    using (games.BufferedUpdate())
                    {
                        foreach (Game game in changed)
                            games.Update(game);
                    }
                }
                if (changed.Count > 0 || !_logged)
                {
                    _logged = true;
                    s_log.Info($"[Versions] {groups.Count} games with copies on several platforms"
                        + (groups.Count > 0 ? $" ({string.Join(", ", groups.Take(10).Select(g => g[0].Name + ": " + string.Join(" / ", g.Select(c => c.Platforms?.FirstOrDefault()?.Name ?? "?"))))})" : "")
                        + $"; {changed.Count} copies shown or hidden.");
                }
                Save();
                if (select != Guid.Empty)
                    _plugin.Playnite.MainView.SelectGame(select);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Versions] Could not sort out games on several platforms: {ex.Message}");
            }
            finally
            {
                _working = false;
            }
        }

        private void SavePreferred()
        {
            try
            {
                lock (_lock)
                    File.WriteAllText(_preferredFile, JsonConvert.SerializeObject(_preferred));
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Versions] Could not keep the preferred copies: {ex.Message}");
            }
        }

        private void Save()
        {
            try
            {
                lock (_lock)
                    File.WriteAllText(_file, JsonConvert.SerializeObject(_hiddenByUs));
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Versions] Could not keep the hidden copies: {ex.Message}");
            }
        }
    }
}
