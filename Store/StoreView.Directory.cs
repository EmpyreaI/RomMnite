using Playnite.SDK.Models;
using RomM.Models.RomM.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace RomM.Store
{
    /// <summary>
    /// The Store's Platforms, Genres and Series pages (from the STORE tab's menu), each laid out
    /// for what it holds:
    ///
    ///   Platforms  a picture card for every platform, grouped by maker (Nintendo, Sony, Sega...),
    ///              biggest first, each with its games, how many are in the library and whether
    ///              it is ready to play here (its emulator set up)
    ///   Genres     the library's three favourites as large cards, then every genre as a card,
    ///              most games first (there are a few dozen at most)
    ///   Series     the library's favourites as cards, then all of them as a list: a filter box,
    ///              A to Z / most games / in your library, and letters that jump down the list
    ///              (there can be hundreds)
    ///
    /// Counts the server has not answered yet are left out, and filled in when they arrive.
    /// </summary>
    public partial class StoreView
    {
        private static readonly Brush DirAccent = Frozen(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush DirReady = Frozen(Color.FromRgb(0x5C, 0xB8, 0x5C));
        private static readonly Brush DirSoft = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        private static readonly Brush DirSoftLit = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        private static readonly Brush DirRule = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        private string _pendingDirectory;      // asked for before the Store had loaded
        private string _directory;             // the one showing, or null
        private int _directoryRequest;
        private DispatcherTimer _directoryRetry;

        // The Series list's filter and order, kept while the page is rebuilt (counts arriving).
        private string _dirFilter = "";
        private string _dirSort = "az";        // az, most, owned
        private bool _dirShowSingles;          // series of one game on the server, hidden at first
        private StackPanel _dirList;
        private readonly Dictionary<string, FrameworkElement> _dirLetters = new Dictionary<string, FrameworkElement>();

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static string DirectoryTitle(string which) =>
            which == "Platforms" ? "Platforms" : which == "Genres" ? "Genres" : "Series";

        private MenuSpec DirectorySpec(string which) =>
            which == "Platforms" ? PlatformMenu : which == "Genres" ? GenreMenu : SeriesMenu;

        private List<MenuChoice> DirectoryChoices(string which, MenuSpec spec)
        {
            List<MenuChoice> choices = which == "Platforms" ? PlatformChoices() : FilterChoices(which == "Genres" ? _genre : _series);
            // A genre of one game is not one to browse (tags such as "Blood" or "Mouse-only" that
            // one source files as genres): not listed.
            return choices.Where(c => !c.Special && !string.IsNullOrEmpty(NameOf(c))
                && (spec.Count(c) ?? 1) > (which == "Genres" ? 1 : 0)).ToList();
        }

        // ---- Going there ----------------------------------------------------------------------

        private void ShowDirectory(string which)
        {
            if (Host.Length == 0)
                return;
            if (_platforms.Items.Count == 0)
            {
                _pendingDirectory = which;   // shown once the platforms are in (ShowPlatforms)
                return;
            }
            _pendingDirectory = null;

            ShowBrowse();
            CloseNavMenu();
            _home.Visibility = Visibility.Collapsed;
            _gridArea.Visibility = Visibility.Collapsed;
            _category.Visibility = Visibility.Visible;
            _catTimer?.Stop();
            _catCarouselShowAgain = null;
            ++_categoryRequest;   // a category still loading does not replace this page

            RecordStore(new Account.NavEntry { Store = true, Kind = "directory", Value = which }, true);
            _heading.Text = DirectoryTitle(which);
            if (_directory != which)
            {
                _dirFilter = "";
                _dirSort = "az";
                _dirShowSingles = false;
            }
            _directory = which;
            BuildDirectory(which);
            _categoryGlide.ToTop();
        }

        private bool _directoriesPrefetched;

        /// <summary>
        /// The Platforms, Genres and Series pages' pictures got ready before they are first opened,
        /// last of all: after the library, then the Store's Home (StoreView.Preload). Begun once
        /// Home's curtain has lifted, the genres and series are counted (the pages are laid out
        /// from them) and no picture is still on its way (Home's, the library's). One page at a
        /// time, when Playnite is idle; their pictures in StoreImages' background lane and their
        /// art asked of the server one card at a time, so whatever is on screen goes first. Not
        /// if one of the pages is already open (it is loading its own).
        /// </summary>
        private void PrefetchDirectories(int tries = 0)
        {
            if (_directoriesPrefetched || Host.Length == 0)
                return;
            bool picturesPending = StoreImages.Settled < StoreImages.Asked;
            if (_platforms.Items.Count == 0 || !_categoriesReady || (picturesPending && tries < 60))
            {
                if (tries >= 60)
                    return;   // the server not reached: the pages load as they open
                var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                later.Tick += (s, e) =>
                {
                    later.Stop();
                    PrefetchDirectories(tries + 1);
                };
                later.Start();
                return;
            }
            _directoriesPrefetched = true;
            var pages = new Queue<string>(new[] { "Platforms", "Genres", "Series" });
            Action next = null;
            next = () =>
            {
                bool pageOpen = _directory != null && _category.Visibility == Visibility.Visible;
                if (pages.Count == 0 || pageOpen)
                    return;
                string which = pages.Dequeue();
                s_prefetching = true;
                try
                {
                    BuildDirectory(which, prefetch: true);
                    s_log.Info($"[Store] The {which} page's pictures are loading in the background.");
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Store] Could not get the {which} page ready: {ex.Message}");
                }
                finally
                {
                    s_prefetching = false;
                }
                Dispatcher.BeginInvoke(next, DispatcherPriority.ApplicationIdle);
            };
            Dispatcher.BeginInvoke(next, DispatcherPriority.ApplicationIdle);
        }

        // Built again where it stands, once the server's counts are in.
        private void RefreshDirectory()
        {
            if (_directory == null || _category.Visibility != Visibility.Visible || !(_categoryInset.Child is FrameworkElement shown) || !Equals(shown.Tag, "directory"))
                return;
            double at = _category.VerticalOffset;
            BuildDirectory(_directory);
            Dispatcher.BeginInvoke(new Action(() => _category.ScrollToVerticalOffset(at)), DispatcherPriority.Loaded);
        }

        /// <param name="prefetch">Built out of sight and thrown away, only so its pictures are
        /// chosen and loaded before the page is first opened (<see cref="PrefetchDirectories"/>).
        /// The cards ask for the same pictures at the same sizes as the page will, so they are
        /// in the picture cache by then, and each card keeps the picture chosen for it.</param>
        private void BuildDirectory(string which, bool prefetch = false)
        {
            int request = prefetch ? _directoryRequest : ++_directoryRequest;
            if (!prefetch)
                _directoryRetry?.Stop();
            MenuSpec spec = DirectorySpec(which);
            List<MenuChoice> choices = DirectoryChoices(which, spec);
            if (prefetch && choices.Count == 0)
                return;
            _menuOpenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var page = new StackPanel { MaxWidth = ContentWidth, Margin = new Thickness(PageMargin, 22, PageMargin, 48), Tag = "directory" };
            if (!prefetch)
                _categoryInset.Child = page;

            var title = Text(DirectoryTitle(which), 34, true);
            page.Children.Add(title);
            var sub = Text("", 14, false);
            sub.Opacity = 0.6;
            sub.Margin = new Thickness(0, 2, 0, 26);
            page.Children.Add(sub);

            if (choices.Count == 0)
            {
                sub.Text = _loadedOnce ? "Nothing here on this server." : "Loading...";
                // The genres and series arrive after the platforms: look again for a while.
                int tries = 0;
                _directoryRetry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _directoryRetry.Tick += (s, e) =>
                {
                    bool stillHere = request == _directoryRequest && _directory == which && _category.Visibility == Visibility.Visible;
                    bool ready = DirectoryChoices(which, spec).Count > 0;
                    if (!stillHere || ready || ++tries > 30)
                    {
                        _directoryRetry.Stop();
                        if (stillHere && ready)
                            BuildDirectory(which);
                    }
                };
                _directoryRetry.Start();
                return;
            }

            // A "series" of one game on the server is not one to browse (a film tie-in, a one-off):
            // left out of the Series page unless asked for. Its game's own page still names it.
            int singles = 0;
            if (which == "Series" && !_dirShowSingles)
            {
                singles = choices.Count(c => spec.Count(c) == 1);
                choices = choices.Where(c => spec.Count(c) != 1).ToList();
            }

            Dictionary<string, int> owned = OwnedCounts(spec);
            Func<MenuChoice, int> ownedOf = c => (spec.Keys(c) ?? new string[0])
                .Select(k => !string.IsNullOrEmpty(k) && owned.TryGetValue(k, out int n) ? n : 0)
                .DefaultIfEmpty(0).Max();

            var counted = choices.Where(c => spec.Count(c).HasValue).ToList();
            int games = counted.Sum(c => spec.Count(c).Value);
            string noun = which == "Platforms" ? "platforms" : which == "Genres" ? "genres" : "series";
            var parts = new List<string> { $"{choices.Count} {noun}" };
            if (which == "Platforms" && counted.Count > 0)
                parts.Add($"{games:N0} games");
            int inLibrary = choices.Count(c => ownedOf(c) > 0);
            if (inLibrary > 0)
                parts.Add($"{inLibrary} in your library");
            if (which == "Platforms")
            {
                int ready = choices.Count(c => c.Tag is RomMPlatform p && _playable.Contains(p.Id));
                if (ready > 0)
                    parts.Add($"{ready} ready to play");
            }
            sub.Text = string.Join("   ·   ", parts);

            switch (which)
            {
                case "Platforms": BuildPlatformsDirectory(page, spec, choices, ownedOf); break;
                case "Genres": BuildGenresDirectory(page, spec, choices, ownedOf); break;
                default: BuildSeriesDirectory(page, spec, choices, ownedOf, singles); break;
            }
        }

        // How many of the account's games carry each name (a platform, genre or series).
        private Dictionary<string, int> OwnedCounts(MenuSpec spec)
        {
            var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Game game in _plugin.Playnite.Database.Games)
                {
                    if (game.PluginId != _plugin.Id || !_plugin.InAccountLibrary(game))
                        continue;
                    foreach (string name in (spec.LibraryNames(game) ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        owned.TryGetValue(name, out int n);
                        owned[name] = n + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not count the library's games: {ex.Message}");
            }
            return owned;
        }

        // ---- Platforms -------------------------------------------------------------------------

        private static readonly string[] Makers =
        {
            "Nintendo", "Sony", "Microsoft", "Sega", "Atari", "NEC", "SNK", "Bandai", "Commodore",
            "Philips", "Mattel", "Coleco", "Amstrad", "Sinclair", "Panasonic", "Magnavox", "Arcade",
        };

        private static string MakerOf(RomMPlatform platform)
        {
            string name = (platform?.PlayniteName ?? platform?.Name ?? "").Trim();
            if (name.StartsWith("PC", StringComparison.OrdinalIgnoreCase) || name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                || name.StartsWith("DOS", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MS-DOS", StringComparison.OrdinalIgnoreCase))
                return "PC";
            string first = name.Split(' ').FirstOrDefault() ?? "";
            if (first.Equals("Game", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Virtual Boy", StringComparison.OrdinalIgnoreCase))
                return "Nintendo";
            if (first.Equals("PlayStation", StringComparison.OrdinalIgnoreCase))
                return "Sony";
            if (first.Equals("Xbox", StringComparison.OrdinalIgnoreCase))
                return "Microsoft";
            if (first.Equals("Neo", StringComparison.OrdinalIgnoreCase) || first.Equals("Neo-Geo", StringComparison.OrdinalIgnoreCase))
                return "SNK";
            if (first.Equals("TurboGrafx", StringComparison.OrdinalIgnoreCase) || first.Equals("PC-FX", StringComparison.OrdinalIgnoreCase))
                return "NEC";
            if (first.Equals("WonderSwan", StringComparison.OrdinalIgnoreCase))
                return "Bandai";
            return Makers.FirstOrDefault(m => m.Equals(first, StringComparison.OrdinalIgnoreCase)) ?? "Other";
        }

        private void BuildPlatformsDirectory(StackPanel page, MenuSpec spec, List<MenuChoice> choices, Func<MenuChoice, int> ownedOf)
        {
            var groups = choices
                .GroupBy(c => MakerOf(c.Tag as RomMPlatform))
                .Select(g => new { Maker = g.Key, Items = g.OrderByDescending(c => spec.Count(c) ?? 0).ToList(), Games = g.Sum(c => spec.Count(c) ?? 0) })
                .OrderBy(g => g.Maker == "Other" ? 1 : 0)
                .ThenByDescending(g => g.Games)
                .ToList();
            foreach (var group in groups)
            {
                page.Children.Add(SectionHeading(group.Maker, $"{group.Items.Count} platform{(group.Items.Count == 1 ? "" : "s")}   ·   {group.Games:N0} games"));
                var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -10, 30) };
                foreach (MenuChoice choice in group.Items)
                {
                    var platform = choice.Tag as RomMPlatform;
                    bool ready = platform != null && _playable.Contains(platform.Id);
                    grid.Children.Add(DirectoryCard(spec, choice, 150, 18, CardDetail(spec.Count(choice), ownedOf(choice)),
                        ready ? "READY TO PLAY" : null, ready ? DirReady : null));
                }
                page.Children.Add(grid);
            }
        }

        // ---- Genres ----------------------------------------------------------------------------

        private void BuildGenresDirectory(StackPanel page, MenuSpec spec, List<MenuChoice> choices, Func<MenuChoice, int> ownedOf)
        {
            Func<MenuChoice, bool> ownedWhole = OwnedWhole(spec);
            List<MenuChoice> top = RankChoices(spec, choices)
                .Where(c => ownedOf(c) > 0 && !ownedWhole(c) && (spec.Count(c) ?? MinCategoryGames) >= MinCategoryGames)
                .Take(3).ToList();
            if (top.Count == 3)
            {
                page.Children.Add(SectionHeading("Your favourites", "The genres you play most"));
                var big = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -10, 34) };
                foreach (MenuChoice choice in top)
                    big.Children.Add(DirectoryCard(spec, choice, 200, 24, CardDetail(spec.Count(choice), ownedOf(choice)), null, null));
                page.Children.Add(big);
            }

            page.Children.Add(SectionHeading("All genres", "Most games first"));
            var grid = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, -10, 0) };
            foreach (MenuChoice choice in choices.OrderByDescending(c => spec.Count(c) ?? -1).ThenBy(c => NameOf(c), StringComparer.CurrentCultureIgnoreCase))
                grid.Children.Add(DirectoryCard(spec, choice, 120, 16, CardDetail(spec.Count(choice), ownedOf(choice)), null, null));
            page.Children.Add(grid);
        }

        // ---- Series ----------------------------------------------------------------------------

        private void BuildSeriesDirectory(StackPanel page, MenuSpec spec, List<MenuChoice> choices, Func<MenuChoice, int> ownedOf, int singles)
        {
            Func<MenuChoice, bool> ownedWhole = OwnedWhole(spec);
            List<MenuChoice> top = RankChoices(spec, choices)
                .Where(c => ownedOf(c) > 0 && !ownedWhole(c) && (spec.Count(c) ?? MinCategoryGames) >= MinCategoryGames)
                .Take(8).ToList();
            if (top.Count >= 4)
            {
                page.Children.Add(SectionHeading("Your top series", "From what you play most"));
                var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -10, 34) };
                foreach (MenuChoice choice in top.Take(top.Count >= 8 ? 8 : 4))
                    tiles.Children.Add(DirectoryCard(spec, choice, 130, 17, CardDetail(spec.Count(choice), ownedOf(choice)), null, null));
                page.Children.Add(tiles);
            }

            // Prefetching: only the tiles have pictures; the list below has none to get ready.
            if (s_prefetching)
                return;

            page.Children.Add(SectionHeading("All series", null));
            if (singles > 0 || _dirShowSingles)
            {
                var note = new TextBlock { FontSize = 12, Margin = new Thickness(0, -6, 0, 12), TextWrapping = TextWrapping.Wrap };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                note.Inlines.Add(new System.Windows.Documents.Run(_dirShowSingles
                    ? "Showing series with only one game on the server.  "
                    : $"{singles} series with only one game on the server are hidden.  ") { Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) });
                var toggle = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(_dirShowSingles ? "Hide them" : "Show them")) { Foreground = DirAccent, TextDecorations = null };
                toggle.Click += (s, e) =>
                {
                    _dirShowSingles = !_dirShowSingles;
                    RefreshDirectory();
                };
                note.Inlines.Add(toggle);
                page.Children.Add(note);
            }

            // The tools: a filter, the order, and the letters.
            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
            var filter = new TextBox
            {
                Width = 300,
                Height = 32,
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = _dirFilter,
                FontSize = 13,
            };
            var hint = Text("Filter series", 13, false);
            hint.Opacity = 0.45;
            hint.IsHitTestVisible = false;
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(11, 0, 0, 0);
            hint.Visibility = _dirFilter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var filterHost = new Grid();
            filterHost.Children.Add(filter);
            filterHost.Children.Add(hint);
            DockPanel.SetDock(filterHost, Dock.Left);
            tools.Children.Add(filterHost);

            var sorts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sorts, Dock.Left);
            tools.Children.Add(sorts);
            page.Children.Add(tools);

            var letters = new WrapPanel { Margin = new Thickness(0, 0, 0, 18) };
            page.Children.Add(letters);

            _dirList = new StackPanel();
            page.Children.Add(_dirList);

            Action fill = null;
            Action paintSorts = null;
            var sortButtons = new List<Tuple<string, Border, TextBlock>>();
            foreach (var option in new[] { Tuple.Create("az", "A to Z"), Tuple.Create("most", "Most games"), Tuple.Create("owned", "In your library") })
            {
                var words = Text(option.Item2, 13, false);
                var button = new Border { Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Child = words };
                string key = option.Item1;
                button.MouseEnter += (s, e) => { if (_dirSort != key) button.Background = DirSoftLit; };
                button.MouseLeave += (s, e) => paintSorts();
                button.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    _dirSort = key;
                    paintSorts();
                    fill();
                };
                sortButtons.Add(Tuple.Create(key, button, words));
                sorts.Children.Add(button);
            }
            paintSorts = () =>
            {
                foreach (var b in sortButtons)
                {
                    bool on = b.Item1 == _dirSort;
                    b.Item2.Background = on ? DirAccent : DirSoft;
                    b.Item3.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                }
            };
            paintSorts();

            fill = () =>
            {
                string text = _dirFilter.Trim();
                List<MenuChoice> shown = choices.Where(c => text.Length == 0 || NameOf(c).IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                _dirList.Children.Clear();
                _dirLetters.Clear();
                letters.Children.Clear();

                if (shown.Count == 0)
                {
                    var none = Text($"No series match \"{text}\".", 14, false);
                    none.Opacity = 0.6;
                    _dirList.Children.Add(none);
                    return;
                }

                if (_dirSort == "az")
                {
                    var groups = shown.OrderBy(c => NameOf(c), StringComparer.CurrentCultureIgnoreCase)
                        .GroupBy(c => char.IsLetter(NameOf(c)[0]) ? char.ToUpperInvariant(NameOf(c)[0]).ToString() : "#")
                        .ToList();
                    foreach (var group in groups)
                    {
                        var head = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
                        var letter = Text(group.Key, 20, true);
                        letter.Foreground = DirAccent;
                        letter.Width = 36;
                        DockPanel.SetDock(letter, Dock.Left);
                        head.Children.Add(letter);
                        head.Children.Add(new Border { Height = 1, Background = DirRule, VerticalAlignment = VerticalAlignment.Center });
                        _dirList.Children.Add(head);
                        _dirLetters[group.Key] = head;
                        _dirList.Children.Add(SeriesGrid(spec, group.ToList(), ownedOf));
                    }
                }
                else
                {
                    IEnumerable<MenuChoice> ordered = _dirSort == "owned"
                        ? shown.Where(c => ownedOf(c) > 0).OrderByDescending(ownedOf).ThenBy(c => NameOf(c), StringComparer.CurrentCultureIgnoreCase)
                        : shown.OrderByDescending(c => spec.Count(c) ?? -1).ThenBy(c => NameOf(c), StringComparer.CurrentCultureIgnoreCase);
                    List<MenuChoice> list = ordered.ToList();
                    if (list.Count == 0)
                    {
                        var none = Text("None of these series are in your library yet.", 14, false);
                        none.Opacity = 0.6;
                        _dirList.Children.Add(none);
                    }
                    else
                        _dirList.Children.Add(SeriesGrid(spec, list, ownedOf));
                }

                // The letters, for jumping down the A to Z list (dimmed where none start with it).
                foreach (string key in new[] { "#" }.Concat(Enumerable.Range('A', 26).Select(i => ((char)i).ToString())))
                {
                    bool has = _dirLetters.ContainsKey(key);
                    var face = Text(key, 13, true);
                    face.HorizontalAlignment = HorizontalAlignment.Center;
                    var cell = new Border { Width = 30, Height = 28, Margin = new Thickness(0, 0, 3, 3), CornerRadius = new CornerRadius(3), Background = has ? DirSoft : Brushes.Transparent, Child = face, Opacity = has ? 1 : 0.25 };
                    face.VerticalAlignment = VerticalAlignment.Center;
                    if (has)
                    {
                        string jump = key;
                        cell.Cursor = Cursors.Hand;
                        cell.MouseEnter += (s, e) => cell.Background = DirAccent;
                        cell.MouseLeave += (s, e) => cell.Background = DirSoft;
                        cell.MouseLeftButtonUp += (s, e) =>
                        {
                            e.Handled = true;
                            JumpTo(_dirLetters[jump]);
                        };
                    }
                    letters.Children.Add(cell);
                }
                letters.Visibility = _dirSort == "az" ? Visibility.Visible : Visibility.Collapsed;
            };

            filter.TextChanged += (s, e) =>
            {
                _dirFilter = filter.Text;
                hint.Visibility = _dirFilter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                fill();
            };
            fill();
        }

        // Scrolls the page so an element sits just under the Store's bar.
        private void JumpTo(FrameworkElement element)
        {
            try
            {
                Point at = element.TransformToAncestor(_categoryInset).Transform(new Point(0, 0));
                _category.ScrollToVerticalOffset(Math.Max(0, at.Y - _categoryInset.Padding.Top - 12));
            }
            catch (InvalidOperationException)
            {
                element.BringIntoView();
            }
        }

        // Series in four columns: the name, its number of games beside it, and a mark when the
        // library has some.
        private UIElement SeriesGrid(MenuSpec spec, List<MenuChoice> choices, Func<MenuChoice, int> ownedOf)
        {
            var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 6) };
            foreach (MenuChoice choice in choices)
            {
                int? count = spec.Count(choice);
                int have = ownedOf(choice);
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var name = Text(NameOf(choice), 14, false);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.TextWrapping = TextWrapping.NoWrap;
                name.MaxWidth = 250;
                name.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(name);
                if (count.HasValue)
                {
                    var pill = new Border
                    {
                        Background = DirSoft,
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(7, 0, 7, 1),
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = Text(count.Value.ToString(), 11, false),
                    };
                    pill.Opacity = 0.75;
                    row.Children.Add(pill);
                }
                if (have > 0)
                {
                    var mark = Text("✓ " + have, 11, true);
                    mark.Foreground = DirAccent;
                    mark.VerticalAlignment = VerticalAlignment.Center;
                    mark.Margin = new Thickness(6, 0, 0, 0);
                    mark.ToolTip = $"{have} in your library";
                    row.Children.Add(mark);
                }
                var cell = new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 0, 8, 2),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Child = row,
                    ToolTip = NameOf(choice),
                };
                cell.MouseEnter += (s, e) => cell.Background = DirSoftLit;
                cell.MouseLeave += (s, e) => cell.Background = Brushes.Transparent;
                cell.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    choice.Pick();
                };
                grid.Children.Add(cell);
            }
            return grid;
        }

        // ---- Pieces ----------------------------------------------------------------------------

        private static string CardDetail(int? count, int owned)
        {
            var parts = new List<string>();
            if (count.HasValue)
                parts.Add(count.Value == 1 ? "1 game" : $"{count.Value:N0} games");
            if (owned > 0)
                parts.Add($"{owned} in your library");
            return string.Join("   ·   ", parts);
        }

        private UIElement SectionHeading(string title, string note)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var words = Text(title, 18, true);
            words.VerticalAlignment = VerticalAlignment.Bottom;
            row.Children.Add(words);
            if (!string.IsNullOrEmpty(note))
            {
                var extra = Text(note, 13, false);
                extra.Opacity = 0.5;
                extra.Margin = new Thickness(12, 0, 0, 2);
                extra.VerticalAlignment = VerticalAlignment.Bottom;
                row.Children.Add(extra);
            }
            return row;
        }

        // A picture card: a game's art, darkened toward the bottom, the name and a line under it
        // at the bottom left, and a badge at the top right if given.
        private UIElement DirectoryCard(MenuSpec spec, MenuChoice choice, double height, double nameSize, string detail, string badge, Brush badgeColor)
        {
            var card = new Grid
            {
                Height = height,
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = Cursors.Hand,
                ClipToBounds = true,
                Background = new LinearGradientBrush(ThemeColors.Main, ThemeColors.MainDark, 45),
            };
            var image = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.75, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
            card.Children.Add(image);
            card.Children.Add(new Border
            {
                Background = new LinearGradientBrush(Color.FromArgb(0x00, 0, 0, 0), Color.FromArgb(0xE0, 0x05, 0x08, 0x10), 90),
                IsHitTestVisible = false,
            });

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 14, 12) };
            var name = Text(NameOf(choice), nameSize, true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            name.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.8 };
            words.Children.Add(name);
            if (!string.IsNullOrEmpty(detail))
            {
                var line = Text(detail, 12, false);
                line.Opacity = 0.8;
                line.Margin = new Thickness(0, 2, 0, 0);
                line.TextTrimming = TextTrimming.CharacterEllipsis;
                line.TextWrapping = TextWrapping.NoWrap;
                words.Children.Add(line);
            }
            card.Children.Add(words);

            if (badge != null)
            {
                card.Children.Add(new Border
                {
                    Background = badgeColor ?? DirAccent,
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(0, 10, 10, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Child = new TextBlock { Text = badge, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
                });
            }

            var edge = new Border { BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2), IsHitTestVisible = false };
            card.Children.Add(edge);
            var zoom = (ScaleTransform)image.RenderTransform;
            card.MouseEnter += (s, e) =>
            {
                image.Opacity = 1;
                zoom.ScaleX = zoom.ScaleY = 1.04;
                edge.BorderBrush = DirAccent;
            };
            card.MouseLeave += (s, e) =>
            {
                image.Opacity = 0.75;
                zoom.ScaleX = zoom.ScaleY = 1;
                edge.BorderBrush = Brushes.Transparent;
            };
            card.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                choice.Pick();
            };
            card.ToolTip = NameOf(choice);
            ShowChoiceArt(spec, choice, image, (int)Math.Max(400, height * 3));
            return card;
        }
    }
}
