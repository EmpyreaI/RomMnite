using Newtonsoft.Json.Linq;
using RomM.Games;
using RomM.Models.RomM.Platform;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace RomM.Store
{
    /// <summary>
    /// A category's page, after Steam's: a genre, a series, a platform, or the results of a
    /// search. The category's name over a blurred backdrop with a carousel of its showcase
    /// games, a strip of related genres to narrow by, rows of picks, and then the "content
    /// hub": a sidebar of filters with counts beside a list of every game in long rows, with
    /// tabs to order them. A search shows only the hub.
    ///
    /// The whole category is fetched once (up to <see cref="CategoryCap"/> games) and
    /// everything else (counts, orders, filters, the strip) is worked out here, so narrowing
    /// and re-ordering are instant.
    /// </summary>
    public partial class StoreView
    {
        private const int CategoryCap = 1000;
        private const int AllGamesCap = 3000;
        private const int HubBatch = 20;
        private static readonly TimeSpan CarouselTurn = TimeSpan.FromSeconds(12);

        private enum CategoryKind { Genre, Series, Platform, Search, All }

        private readonly ScrollViewer _category = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };
        private SmoothScroll _categoryGlide;
        private int _categoryRequest;

        // The home feed, kept for the category pages' "Recommended for you".
        private List<HomeItem> _feedItems;

        // The category on show.
        private CategoryKind _catKind;
        private string _catValue;
        private RomMPlatform _catPlatform;
        private bool _catTruncated;
        private List<HomeItem> _catAll = new List<HomeItem>();
        private string _catTab;                      // a related genre narrowing the page; null for "Featured"
        private StackPanel _catSections;
        private StackPanel _catTabs;

        // The content hub's filters, order and text.
        private string _hubPlatform;
        private string _hubGenre;
        private string _hubSeries;
        private string _hubMode;
        private string _hubText = "";
        private int _hubOrder;
        private int _hubShown;
        private StackPanel _hubList;
        private StackPanel _hubSide;
        private StackPanel _hubTabs;
        private TextBlock _hubMatches;
        private Button _hubMore;
        private List<HomeItem> _hubItems = new List<HomeItem>();
        private readonly HashSet<string> _hubOpenSections = new HashSet<string>();

        // The category carousel.
        private DispatcherTimer _catTimer;
        private Action _catCarouselShowAgain;

        private void InitCategory()
        {
            _browse.Children.Add(_category);
            _categoryGlide = SmoothScroll.Attach(_category);
            if (_navBand != null)
            {
                _category.Content = _categoryInset;
                _category.ScrollChanged += (s, e) => UpdateNavBand();
            }
            _category.IsVisibleChanged += (s, e) =>
            {
                // Back from a game's page: the carousel's video starts again.
                if (_category.IsVisible)
                    _catCarouselShowAgain?.Invoke();
                UpdateNavBand();
            };
        }

        private void HideCategory()
        {
            _category.Visibility = Visibility.Collapsed;
            _catTimer?.Stop();
        }

        private static string CategoryTitle(CategoryKind kind, string value, RomMPlatform platform)
        {
            switch (kind)
            {
                case CategoryKind.Platform: return platform?.PlayniteName ?? value ?? "";
                case CategoryKind.Search: return $"Results for \"{value}\"";
                case CategoryKind.All: return "All Games";
                default: return value ?? "";
            }
        }

        // ---- Opening a category ------------------------------------------------------------

        private void ShowCategory(CategoryKind kind, string value, RomMPlatform platform)
        {
            if (Host.Length == 0)
                return;

            ShowBrowse();
            _home.Visibility = Visibility.Collapsed;
            _gridArea.Visibility = Visibility.Collapsed;
            _category.Visibility = Visibility.Visible;
            _catTimer?.Stop();
            _catCarouselShowAgain = null;

            RecordStore(new Account.NavEntry { Store = true, Kind = "category", SubKind = kind.ToString(), Value = value, Id = platform?.Id ?? 0 }, true);
            _catKind = kind;
            _catValue = value;
            _catPlatform = platform;
            _catTab = null;
            _heading.Text = CategoryTitle(kind, value, platform);
            _hubPlatform = _hubGenre = _hubSeries = _hubMode = null;
            _hubText = "";
            _hubOrder = 0;
            _hubOpenSections.Clear();

            var loading = Text("Loading...", 14, false);
            loading.Opacity = 0.7;
            loading.Margin = new Thickness(PageMargin, 24, PageMargin, 0);
            _categoryInset.Child = loading;
            _categoryGlide.ToTop();

            int request = ++_categoryRequest;
            Task.Run(() =>
            {
                var items = new List<HomeItem>();
                bool truncated = false;
                bool reached = false;
                try
                {
                    string filter;
                    switch (kind)
                    {
                        case CategoryKind.Genre: filter = "&genres=" + Uri.EscapeDataString(value) + "&order_by=name&order_dir=asc"; break;
                        case CategoryKind.Series: filter = "&" + SeriesQuery(value) + "&order_by=name&order_dir=asc"; break;
                        case CategoryKind.Platform: filter = "&platform_ids=" + platform?.Id + "&order_by=name&order_dir=asc"; break;
                        case CategoryKind.All: filter = "&order_by=name&order_dir=asc"; break;
                        default: filter = "&search_term=" + Uri.EscapeDataString(value); break;   // best matches first
                    }

                    const int pageSize = 250;
                    // Every game reaches further than one category; past that, search finds the rest.
                    int cap = kind == CategoryKind.All ? AllGamesCap : CategoryCap;

                    // The personal feed, for "Recommended for you", if Home has not fetched it:
                    // asked alongside the games rather than after them.
                    Task<JToken> feedAsk = _feedItems == null && kind != CategoryKind.Search
                        ? Task.Run(() => Get("/api/recommendations?limit=50"))
                        : null;

                    // The first page says how many there are; the rest are then asked for at once.
                    // A series: its games by the same rule as everywhere else (SeriesGames).
                    var first = kind == CategoryKind.Series
                        ? new JObject { ["items"] = new JArray(SeriesGames(value, pageSize)) }
                        : Get($"/api/roms?limit={pageSize}&offset=0{filter}");
                    if (first != null)
                    {
                        reached = true;
                        var pages = new List<JArray> { first["items"] as JArray ?? new JArray() };
                        int total = (int?)first["total"] ?? pages[0].Count;
                        if (pages[0].Count >= pageSize && total > pageSize)
                        {
                            int last = Math.Min(total, cap);
                            truncated = total > cap;
                            var asks = new List<Task<JToken>>();
                            for (int offset = pageSize; offset < last; offset += pageSize)
                            {
                                int at = offset;
                                asks.Add(Task.Run(() => Get($"/api/roms?limit={pageSize}&offset={at}{filter}")));
                            }
                            foreach (Task<JToken> ask in asks)
                            {
                                try
                                {
                                    pages.Add(ask.GetAwaiter().GetResult()?["items"] as JArray ?? new JArray());
                                }
                                catch (Exception ex)
                                {
                                    s_log.Info($"[Store] A page of {value} could not be loaded: {ex.Message}");
                                    truncated = true;
                                }
                            }
                        }
                        var seen = new HashSet<int>();
                        foreach (JToken rom in pages.SelectMany(p => p))
                        {
                            HomeItem item = ItemFrom(rom as JObject, null, null);
                            if (item != null && seen.Add(item.Rom.Id))
                                items.Add(item);
                        }
                    }

                    if (feedAsk != null)
                    {
                        var feed = new List<HomeItem>();
                        foreach (JToken entry in feedAsk.GetAwaiter().GetResult() as JArray ?? new JArray())
                        {
                            HomeItem item = ItemFrom(entry["rom"] as JObject, Reasons(entry), Str(entry, "seed_rom_name"));
                            if (item != null)
                                feed.Add(item);
                        }
                        _feedItems = feed;
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not load the category {value}: {ex.Message}");
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (request != _categoryRequest)
                        return;
                    if (!reached)
                    {
                        loading.Text = "Could not reach the RomM server.";
                        return;
                    }
                    foreach (HomeItem item in items)
                        _raw[item.Rom.Id] = item.Raw;
                    // Not what is in the library already; playable platforms first.
                    // A search finds games in the library too (they are marked); browsing does not.
                    // A series is shown whole, in its own order, games already in the library
                    // included (marked "In library"): a series page missing the games you own
                    // reads as broken, or empty when you own them all. A platform's page shows
                    // them too, after the games not in the library, everywhere on the page.
                    // One game once (its best platform's version, or the one the library has), but
                    // a platform's page lists all of its own.
                    _catAll = kind == CategoryKind.Search
                        ? OneVersionEach(items, ownedDrops: false).OrderBy(i => Playable(i.Rom) ? 0 : 1).ToList()
                        : kind == CategoryKind.Series
                            ? OneVersionEach(items, ownedDrops: false)
                            : kind == CategoryKind.Platform
                                ? items.Where(i => i?.Rom != null).OrderBy(Owned).ThenBy(i => Playable(i.Rom) ? 0 : 1).ToList()
                                : ForStore(items);
                    _catTruncated = truncated;
                    BuildCategoryPage();
                }));
            });
        }

        // ---- What belongs where ------------------------------------------------------------

        private static List<string> GenresOf(RomMRom rom) => rom.Metadatum?.Genres?.Count > 0 ? rom.Metadatum.Genres : (rom.Genres ?? new List<string>());
        // A game's series: IGDB's collections, its franchises only when it has none (SeriesNames).
        // Sub-series named as the series they belong to, as the server lists them (SeriesRoot).
        private List<string> SeriesOf(RomMRom rom) => Rooted(SeriesNames.For(
            rom.Metadatum?.Collections?.Count > 0 ? rom.Metadatum.Collections : rom.Collections,
            rom.Metadatum?.Franchises?.Count > 0 ? rom.Metadatum.Franchises : rom.Franchises,
            rom.Name, _seriesMembers.Keys));

        private List<string> SeriesOfData(JObject data) => Rooted(SeriesNames.For(
            Strings(data, "metadatum.collections"), Strings(data, "metadatum.franchises"), Str(data, "name"), _seriesMembers.Keys));

        private List<string> Rooted(List<string> names) =>
            names.Select(SeriesRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // The server's series, each with the series it belongs to ("The Legend of Zelda: Four
        // Swords" -> "The Legend of Zelda"; SeriesNames.Roots), and each series with its own
        // sub-series, set when the filters are read. Only the top ones are listed.
        private Dictionary<string, string> _seriesRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> _seriesMembers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private string SeriesRoot(string name) =>
            name != null && _seriesRoots.TryGetValue(name, out string root) ? root : name;

        // The server asked for a series and its sub-series at once (RomM matches any of a
        // repeated field), e.g. "collections=The%20Legend%20of%20Zelda&collections=...".
        private string SeriesQuery(string series, string field = null)
        {
            string root = SeriesRoot(series);
            List<string> names = root != null && _seriesMembers.TryGetValue(root, out List<string> members) ? members : new List<string> { series };
            return string.Join("&", names.Take(20).Select(n => (field ?? _seriesField) + "=" + Uri.EscapeDataString(n)));
        }

        /// <summary>
        /// Every game of a series on the server, by the same rule a game's series are worked out
        /// by (SeriesOf), not only those IGDB files under it: those it does (collections), those
        /// whose franchise of that name their title carries, and those whose title begins with
        /// it when IGDB gives them no series at all (found by a search for its name). Each
        /// kept only if SeriesOf names the series. Off the UI thread.
        /// </summary>
        private List<JObject> SeriesGames(string series, int limit)
        {
            string root = SeriesRoot(series);
            var found = new Dictionary<int, JObject>();
            var queries = new List<string> { SeriesQuery(root) };
            if (_seriesField == "collections")
                queries.Add(SeriesQuery(root, "franchises"));
            queries.Add("search_term=" + Uri.EscapeDataString(root));
            bool filedUnder = true;
            foreach (string query in queries)
            {
                foreach (JToken rom in Get($"/api/roms?limit={limit}&offset=0&{query}")?["items"] as JArray ?? new JArray())
                {
                    if (!(rom is JObject data) || !((int?)data["id"] is int id) || found.ContainsKey(id))
                        continue;
                    // The server's own series answer is taken as it is; the others are checked.
                    if (filedUnder || SeriesOfData(data).Contains(root, StringComparer.OrdinalIgnoreCase))
                        found[id] = data;
                }
                filedUnder = false;
            }
            return found.Values.ToList();
        }

        // Takes the server's series list: the roots and members worked out; the top ones returned.
        private List<string> SetSeriesFamilies(List<string> series)
        {
            Dictionary<string, string> roots = SeriesNames.Roots(series);
            var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in roots)
            {
                if (!members.TryGetValue(pair.Value, out List<string> list))
                    members[pair.Value] = list = new List<string>();
                list.Add(pair.Key);
            }
            // The series itself first.
            foreach (KeyValuePair<string, List<string>> family in members)
                family.Value.Sort((a, b) => string.Equals(a, family.Key, StringComparison.OrdinalIgnoreCase) ? -1 : string.Equals(b, family.Key, StringComparison.OrdinalIgnoreCase) ? 1 : a.Length.CompareTo(b.Length));
            _seriesRoots = roots;
            _seriesMembers = members;
            int folded = roots.Count(r => !string.Equals(r.Key, r.Value, StringComparison.OrdinalIgnoreCase));
            if (folded > 0)
                s_log.Info($"[Store] {folded} sub-series folded into their series ({members.Count} series).");
            return members.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The server's field series are asked for by: "collections", or "franchises" on a
        // server that lists no collections (set when the filters are read).
        private string _seriesField = "collections";
        private static List<string> ModesOf(RomMRom rom) => rom.Metadatum?.Gamemodes?.Count > 0 ? rom.Metadatum.Gamemodes : (rom.GameModes ?? new List<string>());
        private static double RatingOf(RomMRom rom) => rom.Metadatum?.Average_Rating ?? 0;

        // Release date in seconds; the server gives milliseconds (older ones gave seconds).
        private static long ReleasedOf(RomMRom rom)
        {
            long value = rom.Metadatum?.Release_Date ?? rom.FirstReleaseDate ?? 0;
            return value > 100000000000 ? value / 1000 : value;
        }

        private static bool Has(List<string> values, string value)
        {
            return value != null && values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
        }

        // Whether a game (from anywhere, such as the feed) belongs on this page.
        private bool InCategory(RomMRom rom)
        {
            bool inside;
            switch (_catKind)
            {
                case CategoryKind.Genre: inside = Has(GenresOf(rom), _catValue); break;
                case CategoryKind.Series: inside = Has(SeriesOf(rom), _catValue); break;
                case CategoryKind.Platform: inside = _catPlatform != null && rom.PlatformId == _catPlatform.Id; break;
                case CategoryKind.All: inside = true; break;
                default: inside = _catAll.Any(i => i.Rom.Id == rom.Id); break;
            }
            return inside && (_catTab == null || Has(GenresOf(rom), _catTab));
        }

        // On a platform's page, games already in the library come after the rest (1), wherever
        // games are ordered; elsewhere nothing is told apart by it (0).
        private int Owned(HomeItem item) =>
            _catKind == CategoryKind.Platform && item?.Rom != null && _inLibrary.Contains(item.Rom.Id) ? 1 : 0;

        // The category, narrowed by the strip's genre if one is picked.
        private List<HomeItem> Scope()
        {
            return _catTab == null ? _catAll : _catAll.Where(i => Has(GenresOf(i.Rom), _catTab)).ToList();
        }

        // ---- The page ----------------------------------------------------------------------

        private void BuildCategoryPage()
        {
            var page = new StackPanel();
            if (_catKind != CategoryKind.Search)
            {
                page.Children.Add(BuildCategoryHero());
                UIElement strip = BuildCategoryTabs();
                if (strip != null)
                    page.Children.Add(strip);
            }
            else
            {
                var head = new StackPanel { MaxWidth = ContentWidth, Margin = new Thickness(PageMargin, 18, PageMargin, 0) };
                head.Children.Add(Text(CategoryTitle(_catKind, _catValue, _catPlatform), 24, true));
                page.Children.Add(head);
            }

            _catSections = new StackPanel { MaxWidth = ContentWidth, Margin = new Thickness(PageMargin, 0, PageMargin, 40) };
            page.Children.Add(_catSections);
            FillCategorySections();
            _categoryInset.Child = page;
        }

        private void FillCategorySections()
        {
            _catSections.Children.Clear();
            List<HomeItem> scope = Scope();

            if (_catKind != CategoryKind.Search)
            {
                // Recommended for you: the personal feed's games that belong here.
                var picks = ForStore((_feedItems ?? new List<HomeItem>()).Where(i => InCategory(i.Rom))).Take(16).ToList();
                if (picks.Count > 0)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    foreach (HomeItem item in picks)
                        row.Children.Add(BuildSimilarTile(item.Rom, item.Seed != null ? "Because you played " + item.Seed : PlatformOf(item)));
                    _catSections.Children.Add(CategoryHeading("RECOMMENDED FOR YOU", null, 34));
                    _catSections.Children.Add(SideScroll.Row(row));
                }

                // Top rated, in Steam's "Popular titles" pages: two large, three small.
                var rated = scope.Where(i => RatingOf(i.Rom) > 0).OrderBy(Owned).ThenBy(i => Playable(i.Rom) ? 0 : 1).ThenByDescending(i => RatingOf(i.Rom)).Take(15).ToList();
                if (rated.Count >= 3)
                {
                    var pages = new StackPanel { Orientation = Orientation.Horizontal };
                    for (int start = 0; start < rated.Count; start += 5)
                        pages.Children.Add(BuildCapsulePage(rated.Skip(start).Take(5).ToList()));
                    _catSections.Children.Add(CategoryHeading("TOP RATED", null, 34));
                    _catSections.Children.Add(SideScroll.Row(pages));
                }

                // Recently added to the server.
                var recent = scope.OrderBy(Owned).ThenBy(i => Playable(i.Rom) ? 0 : 1).ThenByDescending(i => i.Rom.CreatedAt).Take(16).ToList();
                if (recent.Count > 0)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    foreach (HomeItem item in recent)
                        row.Children.Add(BuildSimilarTile(item.Rom, PlatformOf(item)));
                    _catSections.Children.Add(CategoryHeading("RECENTLY ADDED", null, 34));
                    _catSections.Children.Add(SideScroll.Row(row));
                }
            }

            _catSections.Children.Add(BuildHub());
        }

        private UIElement CategoryHeading(string title, string subtitle, double top)
        {
            var panel = new StackPanel { Margin = new Thickness(6, top, 0, 10) };
            var name = Text(title, 16, false);
            panel.Children.Add(name);
            if (!string.IsNullOrEmpty(subtitle))
            {
                var under = Text(subtitle.ToUpperInvariant(), 11, false);
                under.Opacity = 0.6;
                under.Margin = new Thickness(0, 2, 0, 0);
                panel.Children.Add(under);
            }
            return panel;
        }

        // ---- Hero: the name and the carousel ---------------------------------------------------

        private UIElement BuildCategoryHero()
        {
            var showcase = _catAll.Where(Showcase).OrderBy(Owned).ThenBy(i => Playable(i.Rom) ? 0 : 1).ThenByDescending(HasVideo).Take(FeaturedCount).ToList();
            if (showcase.Count == 0)
                showcase = _catAll.Where(i => Wide(i) != null && MediaPaths(i.Rom).Count > 0).OrderBy(Owned).Take(FeaturedCount).ToList();

            var hero = new Grid { ClipToBounds = true };

            // The backdrop: the fan art of the game the carousel is showing, as a banner behind
            // the name and the top of the carousel, fading out toward its edges in an oval and
            // following the carousel from game to game. Its height is set from what it sits
            // behind, so the picture's own size never stretches the page.
            Grid backdropFrame = BannerBackdrop(out Action<HomeItem> showBackdrop);
            hero.Children.Add(backdropFrame);

            HomeItem first = showcase.FirstOrDefault() ?? _catAll.FirstOrDefault();
            if (first != null)
                showBackdrop(first);

            var stack = new StackPanel { Margin = new Thickness(0, 26, 0, 24), VerticalAlignment = VerticalAlignment.Top };
            var title = new TextBlock
            {
                Text = CategoryTitle(_catKind, _catValue, _catPlatform).ToUpperInvariant(),
                FontSize = 64,
                FontWeight = FontWeights.Black,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(PageMargin, 0, PageMargin, 4),
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.7 },
            };
            stack.Children.Add(title);

            string kindWord = _catKind == CategoryKind.Genre ? "in this genre" : _catKind == CategoryKind.Series ? "in this series"
                : _catKind == CategoryKind.All ? "on the server not in your library" : "in this platform";
            int owned = _catKind == CategoryKind.Series || _catKind == CategoryKind.Platform ? _catAll.Count(i => _inLibrary.Contains(i.Rom.Id)) : 0;
            var count = Text($"{_catAll.Count}{(_catTruncated ? "+" : "")} game{(_catAll.Count == 1 ? "" : "s")} {kindWord}"
                + (owned > 0 ? $"  \u00B7  {owned} in your library" : ""), 13, false);
            count.Opacity = 0.7;
            count.HorizontalAlignment = HorizontalAlignment.Center;
            count.Margin = new Thickness(0, 0, 0, 22);
            stack.Children.Add(count);

            if (showcase.Count > 0)
                stack.Children.Add(BuildCategoryCarousel(showcase, showBackdrop));
            hero.Children.Add(stack);

            // The banner reaches from the top to a little past the middle of the carousel.
            stack.SizeChanged += (s, e) =>
            {
                if (e.HeightChanged && e.NewSize.Height > 0)
                    backdropFrame.Height = Math.Round(e.NewSize.Height * 0.78);
            };
            return hero;
        }

        /// <summary>
        /// The banner behind a carousel: the fan art of the game on show, fading out toward its
        /// edges in an oval, darkened a little, and crossfading as the carousel turns (call
        /// <paramref name="show"/> with each game). Its height is set by the caller from what
        /// it sits behind, so the picture's own size never stretches the page.
        /// </summary>
        private Grid BannerBackdrop(out Action<HomeItem> show)
        {
            var backdrop = new FadeSlides(double.NaN, 1600, true, 700);
            var oval = new RadialGradientBrush { Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.55, RadiusY = 0.62 };
            oval.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0));
            oval.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0, 0, 0), 0.55));
            oval.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            var frame = new Grid
            {
                MaxWidth = 1800,
                Height = 420,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = false,
                ClipToBounds = true,
                Opacity = 0.6,
                OpacityMask = oval,
            };
            frame.Children.Add(backdrop.Stage);
            // Darkened a little, as Steam's is, so text stays readable over bright art.
            frame.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)) });

            show = item =>
            {
                string art = item == null ? null : FanartPath(item.Rom) ?? Wide(item);
                if (art != null)
                    backdrop.FadeTo(Url(art));
            };
            return frame;
        }

        private UIElement BuildCategoryCarousel(List<HomeItem> games, Action<HomeItem> onShow)
        {
            int index = 0;

            // Left: the stage, playing the game's video or showing its screenshots.
            var stageHost = new Grid { Background = Brushes.Black, ClipToBounds = true, Cursor = Cursors.Hand };
            var stageLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
                Padding = new Thickness(6, 3, 6, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            var stageText = new TextBlock { FontSize = 12, Foreground = Brushes.White };
            stageLabel.Child = stageText;
            var stage = new Grid { ClipToBounds = true };
            stage.Children.Add(stageHost);
            stage.Children.Add(stageLabel);
            Panel.SetZIndex(stageLabel, 5);

            // Right: the capsule (art and logo), tags, the blurb and the release date.
            var capsule = new Grid { Background = Brushes.Black, ClipToBounds = true, Cursor = Cursors.Hand };
            var capsuleImage = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(capsuleImage, BitmapScalingMode.Fant);
            capsule.Children.Add(capsuleImage);
            var capsuleLogo = new Grid { IsHitTestVisible = false };
            capsule.Children.Add(capsuleLogo);
            var tags = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
            var blurb = Text("", 12, false);
            blurb.Opacity = 0.8;
            blurb.LineHeight = 17;
            blurb.MaxHeight = 17 * 4;
            blurb.TextTrimming = TextTrimming.CharacterEllipsis;
            var released = Text("", 11, false);
            released.Opacity = 0.6;
            released.VerticalAlignment = VerticalAlignment.Bottom;
            var words = new DockPanel { Margin = new Thickness(14, 0, 14, 12) };
            DockPanel.SetDock(released, Dock.Bottom);
            words.Children.Add(released);
            DockPanel.SetDock(tags, Dock.Top);
            words.Children.Add(tags);
            DockPanel.SetDock(blurb, Dock.Top);
            words.Children.Add(blurb);
            words.Children.Add(new Border());
            var side = new DockPanel { Background = new SolidColorBrush(Color.FromArgb(0x90, 0x16, 0x20, 0x2D)) };
            var capsuleBox = new Border { Padding = new Thickness(12, 12, 12, 0), Child = capsule };
            DockPanel.SetDock(capsuleBox, Dock.Top);
            side.Children.Add(capsuleBox);
            side.Children.Add(words);

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(side, 1);
            content.Children.Add(stage);
            content.Children.Add(side);
            content.SizeChanged += (s, e) =>
            {
                if (!e.WidthChanged || e.NewSize.Width <= 0)
                    return;
                // A 16:9 stage, and Steam's header shape (460 x 215) for the capsule.
                double stageWidth = e.NewSize.Width * 2 / 3;
                content.Height = Math.Round(stageWidth * 9 / 16);
                capsule.Height = Math.Round((e.NewSize.Width / 3 - 24) * 215 / 460);
            };

            // Under it: "Next game in 9 seconds..." with its progress, and the Auto Advance box.
            var countdown = Text("", 12, false);
            countdown.VerticalAlignment = VerticalAlignment.Center;
            countdown.Margin = new Thickness(0, 0, 10, 0);
            var track = new Grid { Width = 280, Height = 3, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) };
            var progress = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = new SolidColorBrush(Color.FromRgb(0xC6, 0xD4, 0xDF)) };
            track.Children.Add(progress);
            var auto = new CheckBox { Content = Text("Auto Advance", 11, false), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), Focusable = false };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8, 12, 8) };
            bar.Children.Add(countdown);
            bar.Children.Add(track);
            bar.Children.Add(auto);

            var panel = new StackPanel { Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0B, 0x0F, 0x16)) };
            panel.Children.Add(content);
            panel.Children.Add(bar);

            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };

            DateTime turnAt = DateTime.UtcNow + CarouselTurn;
            bool held = false;
            FadeSlides slides = null;

            Action show = () =>
            {
                index = ((index % games.Count) + games.Count) % games.Count;
                HomeItem item = games[index];
                RomMRom rom = item.Rom;
                turnAt = DateTime.UtcNow + CarouselTurn;
                onShow?.Invoke(item);

                // The stage: its screenshots fading one into the next, with the video on top
                // as soon as it plays.
                StopHoverVideo();
                slides?.Stop();
                stageHost.Children.Clear();
                var shots = MediaPaths(rom).Select(p => Url(p)).Where(u => !string.IsNullOrEmpty(u)).ToList();
                if (shots.Count == 0 && Wide(item) != null)
                    shots.Add(Url(Wide(item)));
                slides = new FadeSlides(double.NaN, 1280);
                stageHost.Children.Add(slides.Stage);
                if (shots.Count > 0)
                {
                    slides.ShowNow(shots[0]);
                    slides.Play(shots, true);
                }
                bool video = HasVideo(item);
                stageText.Inlines.Clear();
                if (video)
                {
                    stageText.Inlines.Add(new System.Windows.Documents.Run("Trailer Preview") { FontWeight = FontWeights.Bold });
                    stageText.Inlines.Add(new System.Windows.Documents.Run("  |  "));
                }
                stageText.Inlines.Add(new System.Windows.Documents.Run(rom.Name ?? rom.FileNameNoExt ?? ""));
                if (video)
                    PlayHoverVideo(stageHost, item);

                ShowPicture(capsuleImage, Url(Wide(item)), 640);
                capsuleLogo.Children.Clear();
                UIElement logo = LogoOverlay(rom, 1, 3, 500);
                if (logo != null)
                    capsuleLogo.Children.Add(logo);

                tags.Children.Clear();
                foreach (string genre in GenresOf(rom).Take(5))
                    tags.Children.Add(GenreChip(genre, new Thickness(6, 2, 6, 2), new Thickness(0, 0, 4, 4)));
                string summary = ShortText(rom);
                blurb.Text = summary;
                WhenSteamText(rom, steam =>
                {
                    if (games[index].Rom.Id == rom.Id)
                        blurb.Text = ShortText(rom);
                });
                string date = ReleaseText(rom);
                string platformName = !string.IsNullOrEmpty(rom.PlatformDisplayName) ? rom.PlatformDisplayName : rom.PlatformName;
                released.Text = string.Join("   ·   ", new[] { date != null ? "Release date: " + date : null, platformName }.Where(x => !string.IsNullOrEmpty(x)));

                for (int i = 0; i < dots.Children.Count; i++)
                    ((Border)dots.Children[i]).Opacity = i == index ? 1.0 : 0.35;
            };

            for (int i = 0; i < games.Count; i++)
            {
                int at = i;
                var dot = new Border { Width = 16, Height = 8, CornerRadius = new CornerRadius(2), Background = Brushes.White, Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand };
                dot.MouseLeftButtonUp += (s, e) => { index = at; show(); };
                dots.Children.Add(dot);
            }

            Action open = () => ShowGame(games[index].Rom);
            stageHost.MouseLeftButtonUp += (s, e) => open();
            capsule.MouseLeftButtonUp += (s, e) => open();

            var whole = new Grid { MaxWidth = 1240, Margin = new Thickness(PageMargin, 0, PageMargin, 0) };
            whole.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowWidth) });
            whole.ColumnDefinitions.Add(new ColumnDefinition());
            whole.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowWidth) });
            UIElement back = Arrow("‹", () => { index--; show(); });
            UIElement forward = Arrow("›", () => { index++; show(); });
            Grid.SetColumn(panel, 1);
            Grid.SetColumn(forward, 2);
            whole.Children.Add(back);
            whole.Children.Add(panel);
            whole.Children.Add(forward);
            panel.MouseEnter += (s, e) => held = true;
            panel.MouseLeave += (s, e) => held = false;

            // The countdown; paused while the pointer is on the carousel or the page is not showing.
            _catTimer?.Stop();
            _catTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _catTimer.Tick += (s, e) =>
            {
                if (!panel.IsVisible || games.Count < 2)
                    return;
                if (auto.IsChecked != true)
                {
                    countdown.Text = "";
                    progress.Width = 0;
                    turnAt = DateTime.UtcNow + CarouselTurn;
                    return;
                }
                if (held)
                {
                    turnAt = turnAt.AddMilliseconds(100);
                    return;
                }
                double left = (turnAt - DateTime.UtcNow).TotalSeconds;
                if (left <= 0)
                {
                    index++;
                    show();
                    return;
                }
                countdown.Text = $"Next game in {Math.Ceiling(left):0} second{(Math.Ceiling(left) == 1 ? "" : "s")}...";
                progress.Width = track.Width * (1 - left / CarouselTurn.TotalSeconds);
            };
            _catTimer.Start();
            _catCarouselShowAgain = () => show();

            var holder = new StackPanel();
            holder.Children.Add(whole);
            if (games.Count > 1)
                holder.Children.Add(dots);
            show();
            return holder;
        }

        // ---- The strip of related genres ---------------------------------------------------

        private UIElement BuildCategoryTabs()
        {
            // The genres most common among this category's games, its own aside.
            var related = _catAll.SelectMany(i => GenresOf(i.Rom).Distinct(StringComparer.OrdinalIgnoreCase))
                .Where(g => !(_catKind == CategoryKind.Genre && string.Equals(g, _catValue, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count())
                .Take(7)
                .Select(g => g.Key)
                .ToList();
            if (related.Count == 0)
                return null;

            _catTabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Action paint = null;
            Func<string, string, UIElement> tab = (label, genre) =>
            {
                var text = new TextBlock { Text = label.ToUpperInvariant(), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
                var underline = new Border { Height = 2, Margin = new Thickness(0, 4, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)) };
                var box = new StackPanel { Margin = new Thickness(16, 0, 16, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = genre ?? "" };
                box.Children.Add(text);
                box.Children.Add(underline);
                box.MouseEnter += (s, e) => text.Foreground = new SolidColorBrush(Color.FromRgb(0x67, 0xC1, 0xF5));
                box.MouseLeave += (s, e) => paint();
                box.MouseLeftButtonUp += (s, e) =>
                {
                    _catTab = genre;
                    _hubGenre = null;
                    paint();
                    FillCategorySections();
                };
                return box;
            };
            paint = () =>
            {
                foreach (StackPanel box in _catTabs.Children.OfType<StackPanel>())
                {
                    bool on = string.Equals((string)box.Tag, _catTab ?? "", StringComparison.OrdinalIgnoreCase);
                    var text = (TextBlock)box.Children[0];
                    text.Foreground = on ? new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)) : Brushes.White;
                    box.Children[1].Visibility = on ? Visibility.Visible : Visibility.Hidden;
                }
            };
            _catTabs.Children.Add(tab("Featured", null));
            foreach (string relatedGenre in related)
                _catTabs.Children.Add(tab(relatedGenre, relatedGenre));
            paint();

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x0B, 0x0F, 0x16)),
                Padding = new Thickness(0, 22, 0, 20),
                Margin = new Thickness(0, 0, 0, 6),
                Child = _catTabs,
            };
        }

        // ---- "Top rated" pages ------------------------------------------------------------------

        private const double CapsulePageWidth = 1160;

        // Five games: two large capsules above three smaller ones, as on Steam.
        private UIElement BuildCapsulePage(List<HomeItem> games)
        {
            var page = new StackPanel { Width = CapsulePageWidth, Margin = new Thickness(0, 0, 16, 0) };
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            double bigWidth = (CapsulePageWidth - 14) / 2;
            double smallWidth = (CapsulePageWidth - 28) / 3;
            for (int i = 0; i < games.Count; i++)
            {
                bool big = i < 2;
                double width = big ? bigWidth : smallWidth;
                var capsule = BuildWideCapsule(games[i], width, Math.Round(width * 215 / 460));
                ((FrameworkElement)capsule).Margin = new Thickness(0, 0, (big ? i == 1 : i == 4) ? 0 : 14, 0);
                (big ? top : bottom).Children.Add(capsule);
            }
            page.Children.Add(top);
            if (bottom.Children.Count > 0)
                page.Children.Add(bottom);
            return page;
        }

        // A wide capsule: art and logo, with a black strip below carrying the rating.
        private UIElement BuildWideCapsule(HomeItem item, double width, double height)
        {
            RomMRom rom = item.Rom;
            var art = new Grid { Width = width, Height = height, Background = Brushes.Black, ClipToBounds = true };
            var image = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
            ShowPicture(image, Url(Wide(item)), (int)Math.Min(1000, width * 1.5));
            art.Children.Add(image);
            UIElement logo = FanartPath(rom) != null ? LogoOverlay(rom, 1, 3, 500) : null;
            if (logo != null)
                art.Children.Add(logo);

            var strip = new DockPanel { Background = Brushes.Black, Height = 32, LastChildFill = false };
            var where = Text(PlatformOf(item) ?? "", 12, false);
            where.Opacity = 0.6;
            where.VerticalAlignment = VerticalAlignment.Center;
            where.Margin = new Thickness(10, 0, 0, 0);
            strip.Children.Add(where);
            double rating = RatingOf(rom);
            if (rating > 0)
            {
                var score = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x4C, 0x6B, 0x22)),
                    Padding = new Thickness(10, 0, 10, 0),
                    Child = new TextBlock { Text = $"{rating:0}", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xBE, 0xEE, 0x11)), VerticalAlignment = VerticalAlignment.Center },
                };
                DockPanel.SetDock(score, Dock.Right);
                strip.Children.Add(score);
            }
            if (_inLibrary.Contains(rom.Id))
            {
                var owned = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)),
                    Padding = new Thickness(8, 0, 8, 0),
                    Child = new TextBlock { Text = "IN LIBRARY", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                };
                DockPanel.SetDock(owned, Dock.Right);
                strip.Children.Add(owned);
            }
            else if (Unavailable(rom))
            {
                var tag = UnavailableTag(11, new Thickness(8, 0, 8, 0), new Thickness(0), MarkOf(rom));
                DockPanel.SetDock(tag, Dock.Right);
                strip.Children.Add(tag);
            }

            var whole = new StackPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent };
            whole.Children.Add(art);
            whole.Children.Add(strip);
            whole.MouseEnter += (s, e) => art.Opacity = 0.85;
            whole.MouseLeave += (s, e) => art.Opacity = 1;
            whole.MouseLeftButtonUp += (s, e) => ShowGame(rom);
            AttachHoverCard(whole, rom);
            return whole;
        }

        // ---- The content hub -------------------------------------------------------------------

        private static readonly string[] HubTabNames = { "All", "New & trending", "Most recent", "Top rated" };

        private UIElement BuildHub()
        {
            var hub = new Grid { Margin = new Thickness(0, 34, 0, 0) };
            hub.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            hub.ColumnDefinitions.Add(new ColumnDefinition());

            _hubSide = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
            var sideBox = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x70, 0x3D, 0x44, 0x50)),
                Padding = new Thickness(12, 14, 12, 16),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 54, 0, 0),
                Child = _hubSide,
            };
            hub.Children.Add(sideBox);

            var main = new StackPanel();
            var mainBox = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x60, 0x3D, 0x44, 0x50)),
                Padding = new Thickness(18, 12, 18, 18),
                Child = main,
            };
            Grid.SetColumn(mainBox, 1);
            hub.Children.Add(mainBox);

            // Tabs ordering the list, and the search by name on the right.
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
            _hubTabs = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < HubTabNames.Length; i++)
            {
                int order = i;
                string label = _catKind == CategoryKind.Search && i == 0 ? "Best match" : HubTabNames[i];
                var text = new TextBlock { Text = label.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
                var underline = new Border { Height = 2, Margin = new Thickness(0, 4, 0, 0), Background = Brushes.White };
                var tab = new StackPanel { Margin = new Thickness(0, 0, 24, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                tab.Children.Add(text);
                tab.Children.Add(underline);
                tab.MouseLeftButtonUp += (s, e) =>
                {
                    _hubOrder = order;
                    RefreshHub();
                };
                _hubTabs.Children.Add(tab);
            }
            head.Children.Add(_hubTabs);

            var findHint = Text("Search by name...", 12, false);
            findHint.Opacity = 0.5;
            findHint.IsHitTestVisible = false;
            findHint.VerticalAlignment = VerticalAlignment.Center;
            findHint.Margin = new Thickness(8, 0, 0, 0);
            var find = new TextBox { Width = 220, Text = _hubText, VerticalContentAlignment = VerticalAlignment.Center };
            var findBox = new Grid();
            findBox.Children.Add(find);
            findBox.Children.Add(findHint);
            findHint.Visibility = _hubText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var findPause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            findPause.Tick += (s, e) =>
            {
                findPause.Stop();
                _hubText = (find.Text ?? "").Trim();
                RefreshHub();
            };
            find.TextChanged += (s, e) =>
            {
                findHint.Visibility = find.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                findPause.Stop();
                findPause.Start();
            };
            DockPanel.SetDock(findBox, Dock.Right);
            head.Children.Add(findBox);
            main.Children.Add(head);

            _hubList = new StackPanel();
            main.Children.Add(_hubList);

            _hubMore = new Button { Content = "Show more", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(18, 4, 18, 4) };
            _hubMore.Click += (s, e) => AddHubRows();
            main.Children.Add(_hubMore);

            var outer = new StackPanel();
            outer.Children.Add(CategoryHeading(_catKind == CategoryKind.Search ? "BROWSE THE RESULTS" : "BROWSE " + CategoryTitle(_catKind, _catTab ?? _catValue, _catTab != null ? null : _catPlatform).ToUpperInvariant(), null, 0));
            outer.Children.Add(hub);
            RefreshHub();
            return outer;
        }

        // The hub's games: the page's scope, through the sidebar's filters and the name search.
        private IEnumerable<HomeItem> HubFiltered(string skip)
        {
            IEnumerable<HomeItem> items = Scope();
            if (skip != "platform" && _hubPlatform != null)
                items = items.Where(i => string.Equals(PlatformOf(i), _hubPlatform, StringComparison.OrdinalIgnoreCase));
            if (skip != "genre" && _hubGenre != null)
                items = items.Where(i => Has(GenresOf(i.Rom), _hubGenre));
            if (skip != "series" && _hubSeries != null)
                items = items.Where(i => Has(SeriesOf(i.Rom), _hubSeries));
            if (skip != "mode" && _hubMode != null)
                items = items.Where(i => Has(ModesOf(i.Rom), _hubMode));
            if (_hubText.Length > 0)
                items = items.Where(i => (i.Rom.Name ?? i.Rom.FileNameNoExt ?? "").IndexOf(_hubText, StringComparison.OrdinalIgnoreCase) >= 0);
            return items;
        }

        private void RefreshHub()
        {
            if (_hubList == null)
                return;

            var items = HubFiltered(null).ToList();
            switch (_hubOrder)
            {
                case 1: items = items.OrderByDescending(i => i.Rom.CreatedAt).ToList(); break;
                case 2: items = items.OrderByDescending(i => ReleasedOf(i.Rom)).ToList(); break;
                case 3: items = items.OrderByDescending(i => RatingOf(i.Rom)).ThenBy(i => i.Rom.Name).ToList(); break;
                default:
                    if (_catKind != CategoryKind.Search)
                        items = items.OrderBy(i => i.Rom.Name ?? i.Rom.FileNameNoExt, StringComparer.OrdinalIgnoreCase).ToList();
                    break;   // a search keeps the server's best-match order
            }
            // Whatever the order, games on platforms that are not set up go to the back, and on a
            // platform's page, behind everything, the games already in the library.
            items = items.OrderBy(Owned).ThenBy(i => Playable(i.Rom) ? 0 : 1).ToList();
            _hubItems = items;

            for (int i = 0; i < _hubTabs.Children.Count; i++)
            {
                var tab = (StackPanel)_hubTabs.Children[i];
                ((TextBlock)tab.Children[0]).Opacity = i == _hubOrder ? 1 : 0.6;
                tab.Children[1].Visibility = i == _hubOrder ? Visibility.Visible : Visibility.Hidden;
            }

            BuildHubSide();
            _hubList.Children.Clear();
            _hubShown = 0;
            if (items.Count == 0)
            {
                var none = Text("No games match these filters.", 13, false);
                none.Opacity = 0.7;
                none.Margin = new Thickness(0, 20, 0, 20);
                _hubList.Children.Add(none);
            }
            AddHubRows();
        }

        private void AddHubRows()
        {
            foreach (HomeItem item in _hubItems.Skip(_hubShown).Take(HubBatch))
                _hubList.Children.Add(BuildHubRow(item));
            _hubShown = Math.Min(_hubItems.Count, _hubShown + HubBatch);
            _hubMore.Visibility = _hubShown < _hubItems.Count ? Visibility.Visible : Visibility.Collapsed;
            _hubMore.Content = $"Show more ({_hubItems.Count - _hubShown} left)";
        }

        private void BuildHubSide()
        {
            _hubSide.Children.Clear();

            var title = Text("FILTERS", 15, false);
            _hubSide.Children.Add(title);

            // The filters in force, each with a cross to drop it.
            var active = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            Action<string, Action> addActive = (label, clear) =>
            {
                var chip = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 4, 4),
                    Cursor = Cursors.Hand,
                    Child = Text("×  " + label, 11, false),
                };
                chip.MouseLeftButtonUp += (s, e) =>
                {
                    clear();
                    RefreshHub();
                };
                active.Children.Add(chip);
            };
            if (_hubPlatform != null) addActive(_hubPlatform, () => _hubPlatform = null);
            if (_hubGenre != null) addActive(_hubGenre, () => _hubGenre = null);
            if (_hubSeries != null) addActive(_hubSeries, () => _hubSeries = null);
            if (_hubMode != null) addActive(_hubMode, () => _hubMode = null);
            if (active.Children.Count > 0)
                _hubSide.Children.Add(active);

            var matches = Text($"{_hubItems.Count} MATCHES", 11, false);
            matches.Opacity = 0.65;
            matches.Margin = new Thickness(0, 6, 0, 4);
            _hubSide.Children.Add(matches);
            _hubMatches = matches;

            if (_catKind != CategoryKind.Platform)
                AddFilterSection("PLATFORMS", "platform", i => new[] { PlatformOf(i) }, _hubPlatform, v => _hubPlatform = v);
            AddFilterSection("GENRES", "genre", i => GenresOf(i.Rom)
                .Where(g => !(_catKind == CategoryKind.Genre && string.Equals(g, _catValue, StringComparison.OrdinalIgnoreCase)))
                .Where(g => !string.Equals(g, _catTab, StringComparison.OrdinalIgnoreCase)),
                _hubGenre, v => _hubGenre = v);
            if (_catKind != CategoryKind.Series)
                AddFilterSection("SERIES", "series", i => SeriesOf(i.Rom), _hubSeries, v => _hubSeries = v);
            AddFilterSection("PLAYERS", "mode", i => ModesOf(i.Rom), _hubMode, v => _hubMode = v);
        }

        // A sidebar section: its values with how many games each would leave, the most common
        // first, six shown until "Show more".
        private void AddFilterSection(string title, string key, Func<HomeItem, IEnumerable<string>> values, string current, Action<string> set)
        {
            var counts = HubFiltered(key)
                .SelectMany(i => values(i).Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (counts.Count == 0)
                return;

            var heading = Text(title, 14, false);
            heading.Margin = new Thickness(0, 16, 0, 6);
            _hubSide.Children.Add(heading);

            bool open = _hubOpenSections.Contains(key);
            foreach (var entry in counts.Take(open ? 60 : 6))
            {
                string name = entry.Name;
                bool on = string.Equals(name, current, StringComparison.OrdinalIgnoreCase);
                var row = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 1, 0, 1) };
                var number = Text(entry.Count.ToString(), 11, false);
                number.Opacity = 0.5;
                number.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(number, Dock.Right);
                row.Children.Add(number);
                var label = Text(name, 12, on);
                label.TextWrapping = TextWrapping.NoWrap;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.Margin = new Thickness(on ? 8 : 10, 2, 6, 2);
                var line = new Border
                {
                    BorderThickness = new Thickness(on ? 2 : 0, 0, 0, 0),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x24)),
                    Child = label,
                };
                row.Children.Add(line);
                row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    set(on ? null : name);
                    RefreshHub();
                };
                _hubSide.Children.Add(row);
            }

            if (counts.Count > 6)
            {
                var more = Text(open ? "Show less" : "Show more", 11, false);
                more.Opacity = 0.55;
                more.Cursor = Cursors.Hand;
                more.Margin = new Thickness(10, 2, 0, 0);
                more.MouseLeftButtonUp += (s, e) =>
                {
                    if (open) _hubOpenSections.Remove(key);
                    else _hubOpenSections.Add(key);
                    BuildHubSide();
                };
                _hubSide.Children.Add(more);
            }
        }

        // One game in the hub's list: its cover; its blurb, tags and facts; its screenshots;
        // and a strip below with the platform and whether it is in the library.
        private UIElement BuildHubRow(HomeItem item)
        {
            RomMRom rom = item.Rom;
            _raw.TryGetValue(rom.Id, out JObject data);

            const double coverWidth = 210;
            const double coverHeight = 280;

            var cover = new Grid { Width = coverWidth, Height = coverHeight, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), ClipToBounds = true };
            var coverName = Text(rom.Name ?? rom.FileNameNoExt ?? "", 14, false);
            coverName.TextAlignment = TextAlignment.Center;
            coverName.VerticalAlignment = VerticalAlignment.Center;
            coverName.Margin = new Thickness(10);
            cover.Children.Add(coverName);
            var coverImage = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(coverImage, BitmapScalingMode.Fant);
            ShowPicture(coverImage, Url(rom.PathCoverL ?? rom.PathCoverS), 420);
            cover.Children.Add(coverImage);

            // The words in the middle.
            var words = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            var name = Text(rom.Name ?? rom.FileNameNoExt ?? "", 16, true);
            name.Margin = new Thickness(0, 0, 0, 6);
            words.Children.Add(name);
            string summary = ShortText(rom);
            if (summary.Length > 0)
            {
                var blurb = Text(summary, 13, false);
                blurb.LineHeight = 18;
                blurb.MaxHeight = 18 * 5;
                blurb.TextTrimming = TextTrimming.CharacterEllipsis;
                blurb.Opacity = 0.85;
                blurb.Margin = new Thickness(0, 0, 0, 10);
                words.Children.Add(blurb);
                WhenSteamText(rom, steam => blurb.Text = ShortText(rom));
            }
            var genres = GenresOf(rom);
            if (genres.Count > 0)
            {
                var tagRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var tagLabel = Text("TAGS", 10, false);
                tagLabel.Opacity = 0.55;
                tagLabel.Width = 96;
                tagLabel.Margin = new Thickness(0, 4, 0, 0);
                DockPanel.SetDock(tagLabel, Dock.Left);
                tagRow.Children.Add(tagLabel);
                var chips = new WrapPanel();
                foreach (string genre in genres.Take(6))
                    chips.Children.Add(GenreChip(genre, new Thickness(6, 2, 6, 2), new Thickness(0, 0, 4, 4)));
                tagRow.Children.Add(chips);
                words.Children.Add(tagRow);
            }
            Action<string, string> fact = (label, value) =>
            {
                if (string.IsNullOrEmpty(value))
                    return;
                var line = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var key = Text(label, 10, false);
                key.Opacity = 0.55;
                key.Width = 96;
                DockPanel.SetDock(key, Dock.Left);
                line.Children.Add(key);
                var text = Text(value, 11, true);
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.TextWrapping = TextWrapping.NoWrap;
                line.Children.Add(text);
                words.Children.Add(line);
            };
            fact("RELEASE DATE:", ReleaseText(rom));
            var developers = Strings(data, "metadatum.developers");
            var publishers = Strings(data, "metadatum.publishers");
            if (developers.Count > 0 || publishers.Count > 0)
            {
                fact("DEVELOPED BY:", string.Join(", ", developers.Take(3)));
                fact("PUBLISHED BY:", string.Join(", ", publishers.Take(3)));
            }
            else
            {
                fact("COMPANIES:", string.Join(", ", (rom.Metadatum?.Companies ?? rom.Companies ?? new List<string>()).Take(3)));
            }
            double rating = RatingOf(rom);
            if (rating > 0)
            {
                var score = Text($"Rated {rating:0} / 100", 12, false);
                score.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xC0, 0xF4));
                score.Margin = new Thickness(0, 10, 0, 0);
                words.Children.Add(score);
            }

            // The screenshots on the right: a large one and three small; pointing at a small one
            // fades it into the large.
            var media = new StackPanel { Width = 440, Margin = new Thickness(0, 12, 12, 12) };
            var shots = MediaPaths(rom).Select(p => Url(p)).Where(u => !string.IsNullOrEmpty(u)).ToList();
            if (shots.Count == 0 && FanartPath(rom) != null)
                shots.Add(Url(FanartPath(rom)));
            if (shots.Count > 0)
            {
                var big = new FadeSlides(Math.Round(440 * 9 / 16.0), 900, false, 300);
                big.ShowNow(shots[0]);
                media.Children.Add(big.Stage);
                var smalls = new UniformGrid2Row(3);
                smalls.Panel.Margin = new Thickness(0, 6, 0, 0);
                smalls.Panel.Background = Brushes.Transparent;
                foreach (string shot in shots.Skip(1).Take(3))
                {
                    var thumb = new Image { Stretch = Stretch.UniformToFill, Height = 76 };
                    RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.Fant);
                    ShowPicture(thumb, shot, 300);
                    var cell = new Border { Margin = new Thickness(0, 0, 6, 0), ClipToBounds = true, Background = Brushes.Black, Child = thumb };
                    string shown = shot;
                    cell.MouseEnter += (s, e) => big.FadeTo(shown);
                    smalls.Add(cell);
                }
                smalls.Panel.MouseLeave += (s, e) => big.FadeTo(shots[0]);
                media.Children.Add(smalls.Panel);
            }

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(words, 1);
            Grid.SetColumn(media, 2);
            body.Children.Add(cover);
            body.Children.Add(words);
            body.Children.Add(media);

            // The strip along the bottom: platform on the left, library state on the right.
            var strip = new DockPanel { Background = Brushes.Black, Height = 34, LastChildFill = false };
            var platformText = Text(PlatformOf(item) ?? "", 12, false);
            platformText.VerticalAlignment = VerticalAlignment.Center;
            platformText.Margin = new Thickness(coverWidth + 16, 0, 0, 0);
            platformText.Opacity = 0.75;
            strip.Children.Add(platformText);
            if (_inLibrary.Contains(rom.Id))
            {
                var owned = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)),
                    Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(0, 4, 8, 4),
                    Child = new TextBlock { Text = "IN LIBRARY", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                };
                DockPanel.SetDock(owned, Dock.Right);
                strip.Children.Add(owned);
            }
            else if (Unavailable(rom))
            {
                var tag = UnavailableTag(11, new Thickness(10, 0, 10, 0), new Thickness(0, 4, 8, 4), MarkOf(rom));
                DockPanel.SetDock(tag, Dock.Right);
                strip.Children.Add(tag);
            }

            var whole = new StackPanel { Margin = new Thickness(0, 0, 0, 10), Cursor = Cursors.Hand };
            var normal = new SolidColorBrush(Color.FromArgb(0x50, 0x16, 0x20, 0x2D));
            var lit = new SolidColorBrush(Color.FromArgb(0x90, 0x2A, 0x47, 0x5E));
            whole.Background = normal;
            whole.Children.Add(body);
            whole.Children.Add(strip);
            whole.MouseEnter += (s, e) => whole.Background = lit;
            whole.MouseLeave += (s, e) => whole.Background = normal;
            whole.MouseLeftButtonUp += (s, e) => ShowGame(rom);
            return whole;
        }

        // A single row of equal cells.
        private sealed class UniformGrid2Row
        {
            public readonly Grid Panel = new Grid();
            private int _count;

            public UniformGrid2Row(int columns)
            {
                for (int i = 0; i < columns; i++)
                    Panel.ColumnDefinitions.Add(new ColumnDefinition());
            }

            public void Add(UIElement cell)
            {
                if (_count >= Panel.ColumnDefinitions.Count)
                    return;
                Grid.SetColumn(cell, _count++);
                Panel.Children.Add(cell);
            }
        }
    }
}
