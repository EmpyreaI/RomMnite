using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using RomM.Models.RomM.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.Store
{
    /// <summary>
    /// The drop-downs of the Store's bar (Platforms, Genres, Series), after Steam's Categories
    /// menu: the ones the library plays most as picture tiles, more of them as chips, then all
    /// of them in columns, a few rows at first with "Expand" for the rest.
    /// </summary>
    public partial class StoreView
    {
        private static readonly Brush ChipBack = new SolidColorBrush(Color.FromArgb(0x33, 0x67, 0xC1, 0xF5));
        private static readonly Brush ChipBackLit = new SolidColorBrush(Color.FromArgb(0x70, 0x67, 0xC1, 0xF5));
        private static readonly Brush ChipText = new SolidColorBrush(Color.FromRgb(0x67, 0xC1, 0xF5));

        private const int MenuTiles = 6;

        /// <summary>
        /// The fewest games a genre, series or platform needs to be featured: a picture tile or
        /// chip in the bar's menus, or a live section. With fewer it is still listed in its menu's
        /// full list (a platform or series of one or two games can still be browsed), just not
        /// put forward.
        /// </summary>
        internal const int MinCategoryGames = 3;

        // Games per genre and series on the server ("genre|Action"), counted in the background
        // once the lists arrive.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _categoryCounts =
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // The server's platforms, as loaded (for counting in the background).
        private List<RomMPlatform> _serverPlatforms;

        // The games (name, server id) of the small genres, series and platforms the library has
        // games of ("series|Fable", "platform|12"): enough to tell whether the library has every
        // one of them, a game on several platforms counting once.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<KeyValuePair<string, int>>> _categoryMembers =
            new System.Collections.Concurrent.ConcurrentDictionary<string, List<KeyValuePair<string, int>>>(StringComparer.OrdinalIgnoreCase);

        // Larger categories than this are never all owned in practice, and are not fetched.
        private const int MembersUpTo = 80;

        /// <summary>
        /// Counts the games of every genre and series, four questions at a time; then, for the
        /// small ones the library has games of (the ones the menus recommend), fetches their
        /// games, so a category the library owns whole can be told exactly.
        /// </summary>
        private void CountCategories(List<string> genres, List<string> series)
        {
            // Background work: each request waits for what the user is doing (WaitForForeground).
            s_background.Value = true;
            try
            {
                // First, while the Store's curtain is down: the pictures wait for it.
                StoreImages.HoldFor(TimeSpan.FromSeconds(6));
                if (ScanCategories(genres, series))
                    return;
                CountCategoriesOneByOne(genres, series);
            }
            finally
            {
                s_background.Value = false;
                StoreImages.Release();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _categoriesReady = true;
                    // A Genres or Series page showing gets its counts.
                    RefreshDirectory();
                }));
            }
        }

        // Most servers: every game read once, in a few large pages, and every genre and series
        // counted from them by the rules the pages use (SeriesOfData), with the games of the
        // small ones kept (for OwnsEvery). False when the server has too many games for that,
        // or a page could not be read.
        private const int ScanCap = 8000;
        private const int ScanPage = 500;

        private bool ScanCategories(List<string> genres, List<string> series)
        {
            int total = (int?)Get("/api/roms?limit=1")?["total"] ?? -1;
            if (total < 0 || total > ScanCap)
                return false;
            var roms = new System.Collections.Concurrent.ConcurrentBag<JObject>();
            bool failed = false;
            System.Threading.Tasks.Parallel.ForEach(Enumerable.Range(0, (total + ScanPage - 1) / ScanPage),
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, page =>
                {
                    JArray items = null;
                    try
                    {
                        items = Get($"/api/roms?limit={ScanPage}&offset={page * ScanPage}")?["items"] as JArray;
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Store] Could not read page {page} of the games: {ex.Message}");
                    }
                    if (items == null)
                    {
                        failed = true;
                        return;
                    }
                    foreach (JToken rom in items)
                    {
                        if (rom is JObject data)
                            roms.Add(data);
                    }
                });
            if (failed)
                return false;

            var byKey = new Dictionary<string, List<KeyValuePair<string, int>>>(StringComparer.OrdinalIgnoreCase);
            Action<string, string, int> add = (key, name, id) =>
            {
                if (!byKey.TryGetValue(key, out List<KeyValuePair<string, int>> list))
                    byKey[key] = list = new List<KeyValuePair<string, int>>();
                list.Add(new KeyValuePair<string, int>(name, id));
            };
            foreach (JObject data in roms)
            {
                int id = (int?)data["id"] ?? 0;
                if (id <= 0)
                    continue;
                string name = Wishlist.Normalize(Str(data, "name") ?? Str(data, "fs_name_no_ext"));
                foreach (string genre in Strings(data, "metadatum.genres").Distinct(StringComparer.OrdinalIgnoreCase))
                    add("genre|" + genre, name, id);
                foreach (string one in SeriesOfData(data))
                    add("series|" + one, name, id);
                int platform = (int?)data["platform_id"] ?? 0;
                if (platform > 0)
                    add("platform|" + platform, name, id);
            }
            foreach (string key in genres.Select(g => "genre|" + g).Concat(series.Select(s => "series|" + s)))
                _categoryCounts[key] = byKey.TryGetValue(key, out List<KeyValuePair<string, int>> list) ? list.Count : 0;
            foreach (KeyValuePair<string, List<KeyValuePair<string, int>>> category in byKey)
            {
                if (category.Value.Count <= MembersUpTo)
                    _categoryMembers[category.Key] = category.Value;
            }
            s_log.Info($"[Store] Counted {genres.Count} genres and {series.Count} series from the server's {roms.Count} games.");
            return true;
        }

        // Larger servers: each genre and series asked about on its own.
        private void CountCategoriesOneByOne(List<string> genres, List<string> series)
        {
            var asks = genres.Select(g => Tuple.Create("genre|" + g, "genres=" + Uri.EscapeDataString(g)))
                .Concat(series.Select(s => Tuple.Create("series|" + s, (string)null)))
                .ToList();
            System.Threading.Tasks.Parallel.ForEach(asks, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, ask =>
            {
                try
                {
                    // A series counted as its page lists it (SeriesGames), so the two agree.
                    if (ask.Item2 == null)
                    {
                        _categoryCounts[ask.Item1] = SeriesGames(ask.Item1.Substring("series|".Length), 200).Count;
                        return;
                    }
                    JToken answer = Get("/api/roms?limit=1&" + ask.Item2);
                    if (answer?["total"] != null)
                        _categoryCounts[ask.Item1] = (int)answer["total"];
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Store] Could not count {ask.Item1}: {ex.Message}");
                }
            });
            s_log.Info($"[Store] Counted the games of {_categoryCounts.Count} of {asks.Count} genres and series.");

            // The members of the small categories the library has games of.
            var fetch = new List<Tuple<string, string>>();
            Action<string, string, Func<Game, IEnumerable<string>>> pick = (kind, field, namesOf) =>
            {
                var mine = LibraryWeights(g => g.PluginId == _plugin.Id ? namesOf(g) : null);
                foreach (var entry in mine.OrderByDescending(w => w.Value).Take(40))
                {
                    int? total = CategoryCount(kind, entry.Key);
                    if (total.HasValue && total.Value > 0 && total.Value <= MembersUpTo)
                        fetch.Add(Tuple.Create(kind + "|" + entry.Key, field == null ? SeriesQuery(entry.Key) : field + "=" + Uri.EscapeDataString(entry.Key)));
                }
            };
            pick("genre", "genres", g => g.Genres?.Select(x => x?.Name));
            pick("series", null, g => g.Series?.Select(x => SeriesRoot(x?.Name)));
            var ownedPlatforms = new HashSet<string>(
                _plugin.Playnite.Database.Games.Where(g => g.PluginId == _plugin.Id).SelectMany(g => g.Platforms?.Select(p => p?.Name) ?? new string[0]).Where(n => n != null),
                StringComparer.OrdinalIgnoreCase);
            foreach (RomMPlatform platform in _serverPlatforms ?? new List<RomMPlatform>())
            {
                if (platform.RomCount > 0 && platform.RomCount <= MembersUpTo
                    && (ownedPlatforms.Contains(platform.PlayniteName ?? "") || ownedPlatforms.Contains(platform.Name ?? "")))
                    fetch.Add(Tuple.Create("platform|" + platform.Id, "platform_ids=" + platform.Id));
            }

            System.Threading.Tasks.Parallel.ForEach(fetch, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, ask =>
            {
                try
                {
                    var members = new List<KeyValuePair<string, int>>();
                    foreach (JToken rom in Get($"/api/roms?limit={MembersUpTo}&" + ask.Item2)?["items"] as JArray ?? new JArray())
                    {
                        int id = (int?)rom["id"] ?? 0;
                        string name = Wishlist.Normalize(Str(rom, "name") ?? Str(rom, "fs_name_no_ext"));
                        if (id > 0 && name.Length > 0)
                            members.Add(new KeyValuePair<string, int>(name, id));
                    }
                    _categoryMembers[ask.Item1] = members;
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Store] Could not list {ask.Item1}: {ex.Message}");
                }
            });
            s_log.Info($"[Store] Listed the games of {_categoryMembers.Count} small categories the library has games of.");
        }

        /// <summary>
        /// Whether the library has every game of a category, exactly: each of its games (by
        /// name, a game on several platforms counting once) has a copy in the library. Null
        /// when its games are not known (a large category, or not fetched yet).
        /// </summary>
        private bool? OwnsEvery(string key)
        {
            if (!_categoryMembers.TryGetValue(key, out List<KeyValuePair<string, int>> members) || members.Count == 0)
                return null;
            return members.GroupBy(m => m.Key).All(game => game.Any(copy => _inLibrary.Contains(copy.Value)));
        }

        // How many games a genre or series has, or null while not yet counted.
        private int? CategoryCount(string kind, string name)
        {
            return _categoryCounts.TryGetValue(kind + "|" + name, out int count) ? count : (int?)null;
        }
        private const int MenuChips = 10;
        private const int MenuRowsFolded = 4;

        // What a menu is about: how the library's games name it, how to ask the server for its
        // games, and how to tell one of its games among those already loaded.
        private sealed class MenuSpec
        {
            public string Kind;
            public string TopTitle;
            public string AllTitle;
            public Func<Game, IEnumerable<string>> LibraryNames;
            public Func<MenuChoice, IEnumerable<string>> Keys;   // names it may go by in the library
            public Func<MenuChoice, string> Query;               // the /api/roms filter, or null
            public Func<JObject, MenuChoice, bool> Matches;
            public Func<MenuChoice, double> Size;                // bigger first, after the library's own
            public Func<MenuChoice, int?> Count;                 // its games on the server, if known
        }

        private static string NameOf(MenuChoice choice) => string.IsNullOrEmpty(choice.Name) ? choice.Label : choice.Name;

        private MenuSpec PlatformMenu => new MenuSpec
        {
            Kind = "platform",
            TopTitle = "YOUR TOP PLATFORMS",
            AllTitle = "ALL PLATFORMS",
            LibraryNames = g => g.Platforms?.Select(p => p?.Name),
            Keys = c => c.Tag is RomMPlatform p ? new[] { p.PlayniteName, p.Name } : new[] { NameOf(c) },
            Query = c => c.Tag is RomMPlatform p ? "platform_ids=" + p.Id : null,
            Matches = (data, c) => c.Tag is RomMPlatform p && (int?)data["platform_id"] == p.Id,
            Size = c => (c.Tag as RomMPlatform)?.RomCount ?? 0,
            Count = c => (c.Tag as RomMPlatform)?.RomCount,
        };

        private MenuSpec GenreMenu => new MenuSpec
        {
            Kind = "genre",
            TopTitle = "YOUR TOP GENRES",
            AllTitle = "ALL GENRES",
            LibraryNames = g => g.Genres?.Select(x => x?.Name),
            Keys = c => new[] { NameOf(c) },
            Query = c => "genres=" + Uri.EscapeDataString(NameOf(c)),
            Matches = (data, c) => Strings(data, "metadatum.genres").Any(v => string.Equals(v, NameOf(c), StringComparison.OrdinalIgnoreCase)),
            Size = c => CategoryCount("genre", NameOf(c)) ?? 0,
            Count = c => CategoryCount("genre", NameOf(c)),
        };

        private MenuSpec SeriesMenu => new MenuSpec
        {
            Kind = "series",
            TopTitle = "YOUR TOP SERIES",
            AllTitle = "ALL SERIES",
            LibraryNames = g => g.Series?.Select(x => SeriesRoot(x?.Name)).Distinct(StringComparer.OrdinalIgnoreCase),
            Keys = c => new[] { NameOf(c) },
            Query = c => SeriesQuery(NameOf(c)),
            Matches = (data, c) => SeriesOfData(data).Any(v => string.Equals(v, NameOf(c), StringComparison.OrdinalIgnoreCase)),
            Size = c => CategoryCount("series", NameOf(c)) ?? 0,
            Count = c => CategoryCount("series", NameOf(c)),
        };

        /// <summary>
        /// How much the library cares for each name a game carries (a genre, a series, a
        /// platform): one for each game, plus its hours played up to fifty.
        /// </summary>
        private Dictionary<string, double> LibraryWeights(Func<Game, IEnumerable<string>> names)
        {
            var weight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Game game in _plugin.Playnite.Database.Games)
                {
                    if (!_plugin.InAccountLibrary(game))
                        continue;
                    IEnumerable<string> carried = names(game);
                    if (carried == null)
                        continue;
                    double hours = game.Playtime / 3600.0;
                    foreach (string name in carried.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(name))
                            continue;
                        weight.TryGetValue(name, out double sum);
                        weight[name] = sum + 1 + Math.Min(hours, 50);
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not weigh the library's games: {ex.Message}");
            }
            return weight;
        }

        /// <summary>
        /// Whether the library has every game the server has of a choice: its RomM games carrying
        /// the name (by their Playnite genres, series or platforms, each game once) are at least
        /// as many as the server counts. Unknown counts are never "owned whole".
        /// </summary>
        private Func<MenuChoice, bool> OwnedWhole(MenuSpec spec)
        {
            var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Game game in _plugin.Playnite.Database.Games)
                {
                    if (game.PluginId != _plugin.Id || !_plugin.InAccountLibrary(game))
                        continue;
                    IEnumerable<string> names = spec.LibraryNames(game);
                    if (names == null)
                        continue;
                    foreach (string name in names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        owned.TryGetValue(name, out int sum);
                        owned[name] = sum + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not count the library's games: {ex.Message}");
            }
            return c =>
            {
                // Exactly, where the category's games are known.
                string category = c.Tag is RomMPlatform platform ? "platform|" + platform.Id : spec.Kind + "|" + NameOf(c);
                bool? exact = OwnsEvery(category);
                if (exact.HasValue)
                    return exact.Value;

                // Otherwise by counting (it can miss, when the server has a game on two platforms).
                int? total = spec.Count(c);
                if (!total.HasValue || total.Value <= 0)
                    return false;
                int have = 0;
                foreach (string key in spec.Keys(c) ?? new string[0])
                {
                    if (!string.IsNullOrEmpty(key) && owned.TryGetValue(key, out int n) && n > have)
                        have = n;
                }
                return have >= total.Value;
            };
        }

        // The menu's choices, the library's favourites first, then the biggest, then as listed.
        private List<MenuChoice> RankChoices(MenuSpec spec, List<MenuChoice> choices)
        {
            Dictionary<string, double> weight = LibraryWeights(spec.LibraryNames);
            Func<MenuChoice, double> liked = c =>
            {
                double best = 0;
                foreach (string key in spec.Keys(c) ?? new string[0])
                {
                    if (!string.IsNullOrEmpty(key) && weight.TryGetValue(key, out double w) && w > best)
                        best = w;
                }
                return best;
            };
            return choices
                .Where(c => !c.Special && !string.IsNullOrEmpty(NameOf(c)))
                .OrderByDescending(liked)
                .ThenByDescending(spec.Size)
                .ToList();
        }

        // Art for the menus' tiles, by kind and name: fan art (or covers) of a few of its games.
        private readonly Dictionary<string, List<MenuArt>> _menuArt = new Dictionary<string, List<MenuArt>>(StringComparer.OrdinalIgnoreCase);

        // A picture for a tile, and the game it is of.
        private sealed class MenuArt
        {
            public int RomId;
            public string Path;
        }

        // Which tile (kind|name) shows which picture, and which games and pictures are taken,
        // across all three menus, so no game appears on two tiles anywhere in the bar.
        private readonly Dictionary<string, MenuArt> _menuArtChosen = new Dictionary<string, MenuArt>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> _menuArtGames = new Dictionary<int, string>();
        private readonly Dictionary<string, string> _menuArtPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The tiles of the menu showing now, whose pictures must not change under the pointer.
        private HashSet<string> _menuOpenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string OwnerOf(MenuArt art)
        {
            if (art.RomId > 0 && _menuArtGames.TryGetValue(art.RomId, out string byGame))
                return byGame;
            return _menuArtPaths.TryGetValue(art.Path, out string byPath) ? byPath : null;
        }

        private bool TakenByOther(MenuArt art, string key)
        {
            string owner = OwnerOf(art);
            return owner != null && !string.Equals(owner, key, StringComparison.OrdinalIgnoreCase);
        }

        private MenuArt Claim(string key, MenuArt art)
        {
            Release(key);
            _menuArtChosen[key] = art;
            if (art.RomId > 0 && !_menuArtGames.ContainsKey(art.RomId))
                _menuArtGames[art.RomId] = key;
            if (!_menuArtPaths.ContainsKey(art.Path))
                _menuArtPaths[art.Path] = key;
            return art;
        }

        private void Release(string key)
        {
            _menuArtChosen.Remove(key);
            foreach (int game in _menuArtGames.Where(g => string.Equals(g.Value, key, StringComparison.OrdinalIgnoreCase)).Select(g => g.Key).ToList())
                _menuArtGames.Remove(game);
            foreach (string path in _menuArtPaths.Where(g => string.Equals(g.Value, key, StringComparison.OrdinalIgnoreCase)).Select(g => g.Key).ToList())
                _menuArtPaths.Remove(path);
        }

        /// <summary>
        /// The picture for a tile: the one it had before, if it still has it; else one of a game
        /// no other tile shows. When every candidate's game is taken, a tile not on screen that
        /// has other games to show gives its game up (a broad genre yields to a series of one
        /// game). Failing that, at least a picture no other tile shows, else the first.
        /// </summary>
        private MenuArt ChooseArt(string key, List<MenuArt> candidates)
        {
            if (_menuArtChosen.TryGetValue(key, out MenuArt kept))
                return kept;
            if (candidates == null || candidates.Count == 0)
                return null;

            MenuArt free = candidates.FirstOrDefault(c => !TakenByOther(c, key));
            if (free != null)
                return Claim(key, free);

            foreach (MenuArt wanted in candidates)
            {
                string owner = OwnerOf(wanted);
                if (owner == null || _menuOpenKeys.Contains(owner) || !_menuArt.TryGetValue(owner, out List<MenuArt> theirs))
                    continue;
                MenuArt instead = theirs.FirstOrDefault(t => t.RomId != wanted.RomId && !TakenByOther(t, owner));
                if (instead == null)
                    continue;
                Claim(owner, instead);
                return Claim(key, wanted);
            }

            return Claim(key, candidates.FirstOrDefault(c => !_menuArtPaths.ContainsKey(c.Path)) ?? candidates[0]);
        }

        private void OpenMenu(MenuSpec spec, List<MenuChoice> choices)
        {
            HookMenuClosing();
            CloseSuggestions();
            if (_menu != null)
                _menu.IsOpen = false;
            _menuOpenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var body = new StackPanel { Margin = new Thickness(20, 14, 20, 16) };

            // Everything with a game in it is listed ("All ..." stays); only what has enough games
            // (MinCategoryGames) is featured as a tile or chip, below. One not counted yet is kept
            // until its count arrives (a moment after the Store opens).
            choices = choices.Where(c => c.Special || (spec.Count(c) ?? 1) > (spec.Kind == "genre" ? 1 : 0)).ToList();
            Func<MenuChoice, bool> enoughToFeature = c => (spec.Count(c) ?? MinCategoryGames) >= MinCategoryGames;

            // Nothing the library already has all of is recommended (the tiles and chips). A genre
            // or platform owned whole is dropped from the list too: its page, which leaves owned
            // games out, would be empty. A series owned whole stays listed, since its page shows
            // the owned games (marked).
            Func<MenuChoice, bool> ownedWhole = OwnedWhole(spec);
            if (spec.Kind != "series")
                choices = choices.Where(c => c.Special || !ownedWhole(c)).ToList();
            List<MenuChoice> ranked = RankChoices(spec, choices).Where(c => !ownedWhole(c) && enoughToFeature(c)).ToList();
            if (ranked.Count == 0 && choices.Count == 0)
            {
                body.Children.Add(MenuHeading(spec.AllTitle));
                var none = Text(_loadedOnce ? "Nothing to choose from on this server." : "Still loading...", 13, false);
                none.Opacity = 0.7;
                body.Children.Add(none);
            }
            else
            {
                // The list at the bottom, and how to unfold it (also "View all" by the chips).
                var all = new ContentControl();
                bool folded = true;
                TextBlock expand = null;
                int columns = choices.Count > 40 ? 6 : choices.Count > 12 ? 5 : 4;
                int foldedCount = columns * MenuRowsFolded;
                Action fill = null;
                fill = () =>
                {
                    all.Content = MenuColumns(folded ? choices.Take(foldedCount).ToList() : choices, columns, !folded);
                    if (expand != null)
                        expand.Text = folded ? "Expand  \u2304" : "Collapse  \u2303";
                };
                Action toggle = () =>
                {
                    folded = !folded;
                    fill();
                };

                // Your top ...: picture tiles.
                if (ranked.Count >= 3)
                {
                    body.Children.Add(MenuHeading(spec.TopTitle));
                    var tiles = new UniformGrid { Columns = MenuTiles, Rows = 1, Margin = new Thickness(0, 0, 0, 12) };
                    foreach (MenuChoice choice in ranked.Take(MenuTiles))
                        tiles.Children.Add(MenuTile(spec, choice));
                    body.Children.Add(tiles);
                }

                // More of them, as chips, and "View all".
                var chips = ranked.Skip(ranked.Count >= 3 ? MenuTiles : 0).Take(MenuChips).ToList();
                if (chips.Count > 0)
                {
                    var chipRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
                    foreach (MenuChoice choice in chips)
                        chipRow.Children.Add(MenuChip(choice));
                    if (choices.Count > foldedCount)
                    {
                        var viewAll = new TextBlock { Text = "View all  \u203A", FontSize = 12, Foreground = ChipText, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 6) };
                        viewAll.MouseEnter += (s, e) => viewAll.Foreground = Brushes.White;
                        viewAll.MouseLeave += (s, e) => viewAll.Foreground = ChipText;
                        viewAll.MouseLeftButtonUp += (s, e) =>
                        {
                            e.Handled = true;
                            folded = false;
                            fill();
                        };
                        chipRow.Children.Add(viewAll);
                    }
                    body.Children.Add(chipRow);
                }

                // All of them.
                var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 0) };
                if (choices.Count > foldedCount)
                {
                    expand = new TextBlock { FontSize = 12, Foreground = ChipText, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
                    TextBlock expandText = expand;
                    expand.MouseEnter += (s, e) => expandText.Foreground = Brushes.White;
                    expand.MouseLeave += (s, e) => expandText.Foreground = ChipText;
                    expand.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        toggle();
                    };
                    DockPanel.SetDock(expand, Dock.Right);
                    head.Children.Add(expand);
                }
                head.Children.Add(MenuHeading(spec.AllTitle));
                body.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
                body.Children.Add(head);
                body.Children.Add(all);
                fill();
            }

            var panel = new Border
            {
                Width = Math.Max(400, _navBar.ActualWidth),
                Background = ThemeColors.Menu(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1, 0, 1, 1),
                Child = body,
            };
            // The menu is a window of its own, which the main window's middle-click scrolling
            // never hears from.
            MiddleClickScroll.Install(panel);

            _menu = new Popup
            {
                PlacementTarget = _navBar,
                Placement = PlacementMode.Bottom,
                StaysOpen = true,
                AllowsTransparency = true,
                Child = panel,
            };
            _menu.IsOpen = true;
            WatchMenuLeave();
        }

        private static TextBlock MenuHeading(string title)
        {
            var heading = Text(title, 11, true);
            heading.Opacity = 0.55;
            heading.Margin = new Thickness(0, 0, 0, 8);
            return heading;
        }

        // The choices in columns; unfolded, in a box of its own that scrolls.
        private UIElement MenuColumns(List<MenuChoice> choices, int columns, bool scrolls)
        {
            var grid = new UniformGrid { Columns = columns };
            foreach (MenuChoice choice in choices)
            {
                MenuChoice picked = choice;
                var label = Text(choice.Label, 13, choice.Current);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                if (choice.Current)
                    label.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
                var cell = new Border { Background = Brushes.Transparent, Padding = new Thickness(8, 5, 8, 5), Cursor = Cursors.Hand, Child = label, ToolTip = choice.Label };
                cell.MouseEnter += (s, e) => cell.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                cell.MouseLeave += (s, e) => cell.Background = Brushes.Transparent;
                cell.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    CloseNavMenu();
                    picked.Pick();
                };
                grid.Children.Add(cell);
            }
            if (!scrolls)
                return grid;
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320, Content = grid };
            SmoothScroll.Attach(scroller);
            return scroller;
        }

        private UIElement MenuChip(MenuChoice choice)
        {
            var words = new TextBlock { Text = NameOf(choice), FontSize = 12, Foreground = ChipText };
            var chip = new Border
            {
                Background = ChipBack,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Child = words,
            };
            chip.MouseEnter += (s, e) =>
            {
                chip.Background = ChipBackLit;
                words.Foreground = Brushes.White;
            };
            chip.MouseLeave += (s, e) =>
            {
                chip.Background = ChipBack;
                words.Foreground = ChipText;
            };
            chip.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                CloseNavMenu();
                choice.Pick();
            };
            return chip;
        }

        // A picture tile with the name on a white label, as Steam's are. The picture is a game
        // of its own (no two tiles of a menu share one), from those loaded or from the server.
        private UIElement MenuTile(MenuSpec spec, MenuChoice choice)
        {
            var tile = new Grid
            {
                Height = 78,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                ClipToBounds = true,
                Background = new LinearGradientBrush(ThemeColors.Main, ThemeColors.MainDark, 45),
            };
            var image = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.6 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
            tile.Children.Add(image);
            var edge = new Border { BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1) };
            tile.Children.Add(edge);
            tile.Children.Add(new Border
            {
                Background = Brushes.White,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = NameOf(choice).ToUpperInvariant(),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Black,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });
            tile.ToolTip = NameOf(choice);
            tile.MouseEnter += (s, e) =>
            {
                image.Opacity = 0.9;
                edge.BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
            };
            tile.MouseLeave += (s, e) =>
            {
                image.Opacity = 0.6;
                edge.BorderBrush = Brushes.Transparent;
            };
            tile.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                CloseNavMenu();
                choice.Pick();
            };
            ShowChoiceArt(spec, choice, image, 400);
            return tile;
        }

        // A choice's picture (a game of its own, no two tiles sharing one) into an image: from
        // what is known, the games loaded, or the server.
        // A page prefetched in the background asks the server for its art one card at a time
        // (a page opened asks for all of its cards at once).
        private static readonly System.Threading.SemaphoreSlim s_prefetchQueries = new System.Threading.SemaphoreSlim(1);

        private void ShowChoiceArt(MenuSpec spec, MenuChoice choice, Image image, int width)
        {
            string key = spec.Kind + "|" + NameOf(choice);
            _menuOpenKeys.Add(key);
            bool background = s_prefetching;
            Action<List<MenuArt>> place = candidates =>
            {
                MenuArt pick = ChooseArt(key, candidates);
                if (pick != null)
                    StoreImages.Show(image, Url(pick.Path), width, null, null, background ? StoreImages.Always : WhileOutOfSight);
            };

            if (_menuArt.TryGetValue(key, out List<MenuArt> known))
            {
                place(known);
                return;
            }

            List<MenuArt> loaded = LoadedArt(spec, choice);
            string query = spec.Query(choice);
            if (loaded.Count >= 8 || query == null || Host.Length == 0)
            {
                _menuArt[key] = loaded;
                place(loaded);
                return;
            }
            Task.Run(() =>
            {
                List<MenuArt> found;
                if (background)
                {
                    s_prefetchQueries.Wait();
                    s_background.Value = true;
                    try
                    {
                        found = ServerArt(query);
                    }
                    finally
                    {
                        s_background.Value = false;
                        s_prefetchQueries.Release();
                    }
                }
                else
                    found = ServerArt(query);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var art = new List<MenuArt>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (MenuArt one in loaded.Concat(found))
                    {
                        if (seen.Add(one.Path))
                            art.Add(one);
                    }
                    _menuArt[key] = art;
                    place(art);
                }));
            });
        }

        // Fan art of games already loaded that belong to the choice.
        private List<MenuArt> LoadedArt(MenuSpec spec, MenuChoice choice)
        {
            var art = new List<MenuArt>();
            try
            {
                foreach (JObject data in _raw.Values.ToList())
                {
                    string fanart = Str(data, "ss_metadata.fanart_path");
                    if (!string.IsNullOrEmpty(fanart) && spec.Matches(data, choice))
                    {
                        art.Add(new MenuArt { RomId = (int?)data["id"] ?? 0, Path = "/assets/romm/resources/" + fanart });
                        if (art.Count >= 12)
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not look through the loaded games for art: {ex.Message}");
            }
            return art;
        }

        // Fan art (else covers) of the choice's best-rated games, from the server; plenty, so
        // each tile has games to choose from that no other tile shows.
        private List<MenuArt> ServerArt(string query)
        {
            var fanart = new List<MenuArt>();
            var covers = new List<MenuArt>();
            try
            {
                JToken answer = Get("/api/roms?limit=24&order_by=average_rating&order_dir=desc&" + query)
                    ?? Get("/api/roms?limit=24&" + query);
                foreach (JToken rom in answer?["items"] as JArray ?? new JArray())
                {
                    int romId = (int?)rom["id"] ?? 0;
                    string path = Str(rom, "ss_metadata.fanart_path");
                    if (!string.IsNullOrEmpty(path))
                        fanart.Add(new MenuArt { RomId = romId, Path = "/assets/romm/resources/" + path });
                    string cover = Str(rom, "path_cover_large");
                    if (!string.IsNullOrEmpty(cover))
                        covers.Add(new MenuArt { RomId = romId, Path = cover });
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not find art for a menu tile ({query}): {ex.Message}");
            }
            return fanart.Concat(covers).ToList();
        }

        // The blue button at the end of the search box, as Steam's.
        private UIElement SearchButton()
        {
            var normal = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
            var lit = new SolidColorBrush(Color.FromRgb(0x4D, 0xB5, 0xFF));
            var glyph = new TextBlock
            {
                Text = "\uE721",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Border
            {
                Width = 32,
                Margin = new Thickness(3, 0, 0, 0),
                Background = normal,
                Cursor = Cursors.Hand,
                ToolTip = "Search",
                Child = glyph,
            };
            Grid.SetColumn(button, 1);
            button.MouseEnter += (s, e) => button.Background = lit;
            button.MouseLeave += (s, e) => button.Background = normal;
            button.MouseLeftButtonDown += (s, e) => e.Handled = true;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if ((_search.Text ?? "").Trim().Length == 0)
                {
                    _search.Focus();
                    return;
                }
                CloseSuggestions();
                RunSearch(true);
            };
            return button;
        }
    }
}
