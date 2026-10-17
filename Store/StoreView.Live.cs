using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using RomM.Games;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RomM.Store
{
    /// <summary>
    /// What makes the Store's Home page change from day to day, as a live store's does:
    ///   - On This Day: games released on today's date in an earlier year, the first as a
    ///     birthday banner ("Happy 20th birthday!"), round numbers marked as milestones;
    ///   - Game of the Day: one game picked for the date, the same all day, with the time
    ///     left until the next;
    ///   - This Week in Gaming History: anniversaries in the days around today;
    ///   - the Spotlight: a genre of the week, changing on Mondays;
    ///   - Complete the Series: series you own part of, with the games you do not have;
    ///   - New on the Server: the newest additions, marked NEW for their first week.
    /// A strip under the carousel sums up what is on today and jumps to each.
    ///
    /// The anniversaries use the server's release-day filter (newer RomM). On a server without
    /// it every game would come back, so each answer is checked against its release date.
    /// </summary>
    public partial class StoreView
    {
        private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x4B));

        // Places for the live sections, laid out with the page and filled once loaded.
        private StackPanel _liveStrip;
        private StackPanel _liveBirthday;
        private StackPanel _liveDaily;
        private StackPanel _liveHistory;
        private StackPanel _liveSpotlight;
        private StackPanel _liveSeries;
        private DispatcherTimer _dailyClock;
        private int _liveRequest;
        private StackPanel _liveNew;      // "New on the Server"
        private int _newThisWeek;         // games added to the server in the last seven days

        private sealed class LiveData
        {
            public List<HomeItem> Today = new List<HomeItem>();
            public List<HomeItem> Week = new List<HomeItem>();
            public HomeItem Daily;
            public string SpotlightGenre;
            public List<HomeItem> Spotlight = new List<HomeItem>();
            public List<Tuple<string, int, List<HomeItem>>> Series = new List<Tuple<string, int, List<HomeItem>>>();
        }

        // The places, in page order; called while the page is laid out.
        private void AddLiveTop()
        {
            _liveStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            _liveBirthday = new StackPanel();
            _liveDaily = new StackPanel();
            _homeBody.Children.Add(_liveStrip);
            _homeBody.Children.Add(_liveBirthday);
            _homeBody.Children.Add(_liveDaily);
        }

        private void AddLiveMiddle()
        {
            _liveHistory = new StackPanel();
            _liveSpotlight = new StackPanel();
            _liveSeries = new StackPanel();
            _homeBody.Children.Add(_liveHistory);
            _homeBody.Children.Add(_liveSpotlight);
            _homeBody.Children.Add(_liveSeries);
        }

        // ---- Loading ---------------------------------------------------------------------------

        private static string Ordinal(int n)
        {
            int last2 = n % 100;
            string suffix = last2 >= 11 && last2 <= 13 ? "th" : (n % 10 == 1 ? "st" : n % 10 == 2 ? "nd" : n % 10 == 3 ? "rd" : "th");
            return n + suffix;
        }

        // A rom's release date (a date, without a time of day), or null.
        private static DateTime? ReleaseDay(RomMRom rom)
        {
            long seconds = ReleasedOf(rom);
            if (seconds <= 0)
                return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).Date;
        }

        private List<HomeItem> Roms(string query)
        {
            var items = new List<HomeItem>();
            foreach (JToken rom in Get("/api/roms?" + query)?["items"] as JArray ?? new JArray())
            {
                HomeItem item = ItemFrom(rom as JObject, null, null);
                if (item != null)
                    items.Add(item);
            }
            return items;
        }

        private void LoadLive()
        {
            int request = ++_liveRequest;
            DateTime today = DateTime.Now.Date;

            // Read here, on the page's thread: the genres the server has, and the series the
            // library owns part of.
            var genres = new List<string>();
            for (int i = 1; i < _genre.Items.Count; i++)
                genres.Add(_genre.Items[i]?.ToString());
            genres = genres.Where(g => !string.IsNullOrEmpty(g)).ToList();
            List<string> ownedSeries = OwnedSeries(3);
            HashSet<int> owned = new HashSet<int>(_inLibrary);

            Task.Run(() =>
            {
                var data = new LiveData();

                // Every part asks the server on its own, all at once: the page's live sections
                // wait for the slowest part, not for each in turn (the spotlight can wait a while
                // for the genres to be counted).
                Task history = Task.Run(() =>
                {
                    HistoryDays days = History(today);
                    data.Today.AddRange(days.Today);
                    data.Week.AddRange(days.Week);
                });

                Task daily = Task.Run(() =>
                {
                    try
                    {
                        // Game of the day: picked by the date, so it is the same all day.
                        int total = (int?)Get("/api/roms?limit=1")?["total"] ?? 0;
                        if (total > 0)
                        {
                            var pick = new Random(today.Year * 1000 + today.DayOfYear);
                            // The day's ten draws, asked at once; the first that suits, in order.
                            int[] offsets = Enumerable.Range(0, 10).Select(_ => pick.Next(total)).ToArray();
                            Task<HomeItem>[] draws = offsets.Select(offset => Task.Run(() =>
                                Roms($"limit=1&offset={offset}&order_by=id&order_dir=asc").FirstOrDefault())).ToArray();
                            HomeItem fallback = null;
                            for (int attempt = 0; attempt < draws.Length && data.Daily == null; attempt++)
                            {
                                HomeItem candidate;
                                try
                                {
                                    candidate = draws[attempt].GetAwaiter().GetResult();
                                }
                                catch
                                {
                                    continue;
                                }
                                if (candidate == null || owned.Contains(candidate.Rom.Id))
                                    continue;
                                if (Showcase(candidate))
                                    data.Daily = candidate;
                                else if (fallback == null && FanartPath(candidate.Rom) != null)
                                    fallback = candidate;
                            }
                            data.Daily = data.Daily ?? fallback;
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Info($"[Store] No game of the day: {ex.Message}");
                    }
                });

                Task spotlight = Task.Run(() =>
                {
                    try
                    {
                        // The genre of the week, changing on Mondays, from the server's 20 most used
                        // genres only: the week's pick, or the next after it with enough games.
                        List<string> top = genres.Count > 0 ? TopGenres(genres, 20) : null;
                        if (top != null && top.Count > 0)
                            genres = top;
                        if (genres.Count > 0)
                        {
                            int week = (int)((today - new DateTime(2024, 1, 1)).TotalDays / 7);
                            for (int tried = 0; tried < Math.Min(genres.Count, 15); tried++)
                            {
                                string genre = genres[(week + tried) % genres.Count];
                                int? known = CategoryCount("genre", genre);
                                if (known.HasValue && known.Value < MinCategoryGames)
                                    continue;
                                JToken answer = Get($"/api/roms?limit=20&genres={Uri.EscapeDataString(genre)}&order_by=average_rating&order_dir=desc");
                                var items = answer?["items"] as JArray ?? new JArray();
                                int total = (int?)answer?["total"] ?? items.Count;
                                if (total < MinCategoryGames)
                                    continue;
                                data.SpotlightGenre = genre;
                                data.Spotlight = items.Select(r => ItemFrom(r as JObject, null, null)).Where(i => i != null).ToList();
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Info($"[Store] No spotlight: {ex.Message}");
                    }
                });

                var found = new Tuple<string, int, List<HomeItem>>[ownedSeries.Count];
                Task[] seriesAsks = ownedSeries.Select((series, index) => Task.Run(() =>
                {
                    try
                    {
                        // The series as its page lists it (SeriesGames), oldest first.
                        var members = SeriesGames(series, 60).Select(o => ItemFrom(o, null, null)).Where(i => i != null)
                            .OrderBy(i => ReleasedOf(i.Rom) == 0 ? long.MaxValue : ReleasedOf(i.Rom)).ToList();
                        // One copy of each game (the same game on several platforms counts once).
                        var distinct = members.GroupBy(m => (m.Rom.Name ?? "").ToLowerInvariant()).Select(g => g.First()).ToList();
                        var missing = distinct.Where(m => !owned.Contains(m.Rom.Id) && !members.Any(o => owned.Contains(o.Rom.Id) && string.Equals(o.Rom.Name, m.Rom.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                        // A series of one or two games is not a series to complete.
                        if (missing.Count > 0 && distinct.Count >= MinCategoryGames)
                            found[index] = Tuple.Create(series, distinct.Count, missing);
                    }
                    catch (Exception ex)
                    {
                        s_log.Info($"[Store] Could not look into the series {series}: {ex.Message}");
                    }
                })).ToArray();

                try
                {
                    Task.WaitAll(new[] { history, daily, spotlight }.Concat(seriesAsks).ToArray());
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Store] Part of the live sections could not be loaded: {ex.Message}");
                }
                // In the library's order of most-owned series.
                data.Series.AddRange(found.Where(f => f != null));

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (request == _liveRequest && _liveStrip != null)
                        ShowLive(data, today);
                }));
            });
        }

        // This week in gaming history, asked once a day: Home's carousel and its live row share it.
        private sealed class HistoryDays
        {
            public DateTime Day;
            public List<HomeItem> Today = new List<HomeItem>();
            public List<HomeItem> Week = new List<HomeItem>();
        }

        private HistoryDays _history;
        private readonly object _historyLock = new object();

        // Off the UI thread. A day's answer is kept until the Store is opened again.
        private HistoryDays History(DateTime today)
        {
            lock (_historyLock)
            {
                if (_history != null && _history.Day == today)
                    return _history;
                var days = new HistoryDays { Day = today };
                LoadAroundToday(today, days.Today, days.Week);
                _history = days;
                return days;
            }
        }

        /// <summary>
        /// Games released on today's date (into <paramref name="onTheDay"/>) and in the three
        /// days either side (into <paramref name="aroundIt"/>), in earlier years. Off the UI thread.
        /// </summary>
        private void LoadAroundToday(DateTime today, List<HomeItem> onTheDay, List<HomeItem> aroundIt)
        {
            try
            {
                var days = new List<string>();
                for (int offset = -3; offset <= 3; offset++)
                {
                    DateTime day = today.AddDays(offset);
                    // 1 January is where many servers put games known only by their year.
                    if (day.Month == 1 && day.Day == 1)
                        continue;
                    days.Add($"released_days={day.Month}-{day.Day}");
                }
                var around = Roms($"limit=60&{string.Join("&", days)}&released_before_year={today.Year}&order_by=first_release_date&order_dir=asc");
                foreach (HomeItem item in around)
                {
                    DateTime? released = ReleaseDay(item.Rom);
                    if (!released.HasValue || released.Value.Year >= today.Year || (released.Value.Month == 1 && released.Value.Day == 1))
                        continue;
                    // Matched by month and day only, across the year's turn too.
                    DateTime thisYear;
                    try { thisYear = new DateTime(today.Year, released.Value.Month, Math.Min(released.Value.Day, DateTime.DaysInMonth(today.Year, released.Value.Month))); }
                    catch { continue; }
                    double distance = Math.Abs((thisYear - today).TotalDays);
                    if (distance > 3 && distance < 362)
                        continue;   // not near today: a server that ignored the filter
                    if (thisYear == today)
                        onTheDay.Add(item);
                    else
                        aroundIt.Add(item);
                }
            }
            catch (Exception ex)
            {
                s_log.Info($"[Store] No anniversaries from the server: {ex.Message}");
            }
        }

        /// <summary>
        /// The genres the spotlight may take: the server's 20 most used, by their number of games
        /// (counted in the background as the Store opens: waited for a little while here). Null
        /// when the counts are not in.
        /// </summary>
        private List<string> TopGenres(List<string> genres, int count)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until && genres.Count(g => CategoryCount("genre", g).HasValue) < genres.Count * 0.9)
                System.Threading.Thread.Sleep(250);
            var counted = genres.Select(g => Tuple.Create(g, CategoryCount("genre", g))).Where(t => t.Item2.HasValue).ToList();
            if (counted.Count == 0)
                return null;
            return counted.OrderByDescending(t => t.Item2.Value).ThenBy(t => t.Item1, StringComparer.OrdinalIgnoreCase)
                .Take(count).Select(t => t.Item1).ToList();
        }

        // The series the library has the most RomM games of.
        private List<string> OwnedSeries(int count)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Game game in _plugin.Playnite.Database.Games)
                {
                    if (game.PluginId != _plugin.Id || game.Series == null || !_plugin.InAccountLibrary(game))
                        continue;
                    // Sub-series counted as their series, once per game.
                    foreach (string series in game.Series.Where(s => !string.IsNullOrEmpty(s?.Name)).Select(s => SeriesRoot(s.Name)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        counts.TryGetValue(series, out int n);
                        counts[series] = n + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not read the library's series: {ex.Message}");
            }
            return counts.OrderByDescending(c => c.Value).Take(count).Select(c => c.Key).ToList();
        }

        // ---- Showing ---------------------------------------------------------------------------

        private void ShowLive(LiveData data, DateTime today)
        {
            foreach (var item in data.Today.Concat(data.Week).Concat(data.Spotlight))
                _raw[item.Rom.Id] = item.Raw;
            if (data.Daily != null)
                _raw[data.Daily.Rom.Id] = data.Daily.Raw;

            var birthdays = ForStore(data.Today);
            var week = ForStore(data.Week);
            var spotlight = ForStore(data.Spotlight).Take(15).ToList();

            _liveBirthday.Children.Clear();
            _liveDaily.Children.Clear();
            _liveHistory.Children.Clear();
            _liveSpotlight.Children.Clear();
            _liveSeries.Children.Clear();

            // The anniversaries and the game of the day are at the top of Home: part of its
            // first look, which the loading curtain waits for.
            StoreImages.Critical = true;
            try
            {
                if (birthdays.Count > 0)
                    _liveBirthday.Children.Add(BuildBirthday(birthdays, today));
                if (data.Daily != null)
                    _liveDaily.Children.Add(BuildDaily(data.Daily));
            }
            finally
            {
                StoreImages.Critical = false;
                _liveTopReady = true;
            }

            if (week.Count > 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (HomeItem item in week.OrderBy(i => Math.Abs((DayThisYear(i.Rom, today) - today).TotalDays)).Take(16))
                {
                    DateTime? released = ReleaseDay(item.Rom);
                    int years = today.Year - (released?.Year ?? today.Year);
                    row.Children.Add(BuildCapsuleTile(item.Rom, $"{years} years · {released:MMM d}"));
                }
                _liveHistory.Children.Add(LiveHeading("This Week in Gaming History", "Released in the days around today, in years gone by", null, null));
                _liveHistory.Children.Add(SideScroll.Row(row));
            }

            if (data.SpotlightGenre != null && spotlight.Count >= 3)
            {
                string genre = data.SpotlightGenre;
                var pages = new StackPanel { Orientation = Orientation.Horizontal };
                for (int start = 0; start < spotlight.Count; start += 5)
                    pages.Children.Add(BuildCapsulePage(spotlight.Skip(start).Take(5).ToList()));
                int untilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
                DateTime nextMonday = today.AddDays(untilMonday == 0 ? 7 : untilMonday);
                _liveSpotlight.Children.Add(LiveHeading($"Spotlight: {genre}", $"This week's featured genre · the next one arrives {nextMonday:dddd d MMM}", "Browse all", () => BrowseGenre(genre)));
                _liveSpotlight.Children.Add(SideScroll.Row(pages));
            }

            foreach (var series in data.Series)
            {
                string name = series.Item1;
                var missing = ForStore(series.Item3);
                if (missing.Count == 0)
                    continue;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (HomeItem item in missing.Take(16))
                    row.Children.Add(BuildCapsuleTile(item.Rom, PlatformOf(item)));
                int have = series.Item2 - series.Item3.Count;
                _liveSeries.Children.Add(LiveHeading($"Complete the Series: {name}", $"You have {have} of {series.Item2}", "See the series", () =>
                {
                    ClearFilters(null);
                    ShowCategory(CategoryKind.Series, name, null);
                }));
                _liveSeries.Children.Add(SideScroll.Row(row));
            }

            // The strip: what is on today, each a jump to its section.
            _liveStrip.Children.Clear();
            if (birthdays.Count > 0)
                _liveStrip.Children.Add(StripChip(birthdays.Count == 1 ? "1 anniversary today" : $"{birthdays.Count} anniversaries today", _liveBirthday, true));
            if (data.Daily != null)
                _liveStrip.Children.Add(StripChip("Game of the Day: " + (data.Daily.Rom.Name ?? ""), _liveDaily, false));
            if (week.Count > 0)
                _liveStrip.Children.Add(StripChip($"{week.Count} more this week in history", _liveHistory, false));
            if (_newThisWeek > 0)
                _liveStrip.Children.Add(StripChip($"{_newThisWeek} new on the server this week", _liveNew, false));
            if (data.SpotlightGenre != null && spotlight.Count >= 3)
                _liveStrip.Children.Add(StripChip("Spotlight: " + data.SpotlightGenre, _liveSpotlight, false));
        }

        private static DateTime DayThisYear(RomMRom rom, DateTime today)
        {
            DateTime? released = ReleaseDay(rom);
            if (!released.HasValue)
                return today;
            int day = Math.Min(released.Value.Day, DateTime.DaysInMonth(today.Year, released.Value.Month));
            return new DateTime(today.Year, released.Value.Month, day);
        }

        private UIElement StripChip(string text, FrameworkElement target, bool special)
        {
            var words = new TextBlock { Text = text, FontSize = 12, Foreground = special ? Brushes.Black : Brushes.White, FontWeight = special ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 };
            var normal = special ? Gold : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            var chip = new Border { Background = normal, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Child = words };
            chip.MouseEnter += (s, e) => chip.Opacity = 0.85;
            chip.MouseLeave += (s, e) => chip.Opacity = 1;
            chip.MouseLeftButtonUp += (s, e) => target?.BringIntoView(new Rect(0, -80, 1, 200));
            return chip;
        }

        private UIElement LiveHeading(string title, string subtitle, string link, Action follow)
        {
            var panel = new DockPanel { Margin = new Thickness(0, 36, 0, 10), LastChildFill = true };
            if (link != null)
            {
                var more = Text(link + "  ›", 12, false);
                more.Opacity = 0.7;
                more.Cursor = Cursors.Hand;
                more.VerticalAlignment = VerticalAlignment.Bottom;
                more.MouseEnter += (s, e) => more.Opacity = 1;
                more.MouseLeave += (s, e) => more.Opacity = 0.7;
                more.MouseLeftButtonUp += (s, e) => follow?.Invoke();
                DockPanel.SetDock(more, Dock.Right);
                panel.Children.Add(more);
            }
            var words = new StackPanel();
            words.Children.Add(Text(title, 17, true));
            if (!string.IsNullOrEmpty(subtitle))
            {
                var under = Text(subtitle, 12, false);
                under.Opacity = 0.6;
                under.Margin = new Thickness(0, 2, 0, 0);
                words.Children.Add(under);
            }
            panel.Children.Add(words);
            return panel;
        }

        // The birthday banner: the game's art behind a gold-edged card, its age as a headline,
        // round numbers marked, and any others celebrating today along the bottom.
        private UIElement BuildBirthday(List<HomeItem> games, DateTime today)
        {
            HomeItem star = games.OrderByDescending(Showcase).ThenByDescending(i => (today.Year - (ReleaseDay(i.Rom)?.Year ?? today.Year)) % 5 == 0).First();
            RomMRom rom = star.Rom;
            DateTime released = ReleaseDay(rom) ?? today;
            int years = today.Year - released.Year;
            bool milestone = years % 5 == 0;

            var art = new Grid { Height = 260, ClipToBounds = true, Background = Brushes.Black };
            string wide = Wide(star);
            if (wide != null)
            {
                var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Right };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                ShowPicture(image, Url(wide), 1280);
                art.Children.Add(image);
            }
            var shade = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0x0B, 0x0E, 0x18), 0));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0x0B, 0x0E, 0x18), 0.45));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0x0B, 0x0E, 0x18), 1));
            art.Children.Add(new Border { Background = shade });
            UIElement logo = FanartPath(rom) != null ? LogoOverlay(rom, 1, 2, 600) : null;
            if (logo != null)
            {
                var logoBox = new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Right };
                ((Panel)logo).Background = null;
                logoBox.Children.Add(logo);
                art.Children.Add(logoBox);
            }

            var words = new StackPanel { Margin = new Thickness(28, 26, 28, 20), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            tags.Children.Add(new TextBlock { Text = "ON THIS DAY", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Gold, VerticalAlignment = VerticalAlignment.Center });
            if (milestone)
            {
                tags.Children.Add(new Border
                {
                    Background = Gold,
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(10, 0, 0, 0),
                    Child = new TextBlock { Text = $"{years}-YEAR MILESTONE", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.Black },
                });
            }
            words.Children.Add(tags);
            words.Children.Add(new TextBlock
            {
                Text = $"Happy {Ordinal(years)} birthday, {rom.Name ?? rom.FileNameNoExt}!",
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
            });
            var when = Text($"Released {released.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)} on {PlatformOf(star)} — {years} years ago today", 13, false);
            when.Opacity = 0.8;
            when.Margin = new Thickness(0, 6, 0, 14);
            words.Children.Add(when);

            var view = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)),
                Padding = new Thickness(18, 7, 18, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "Celebrate in the Store", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
            };
            view.MouseEnter += (s, e) => view.Opacity = 0.85;
            view.MouseLeave += (s, e) => view.Opacity = 1;
            view.MouseLeftButtonUp += (s, e) => ShowGame(rom);
            words.Children.Add(view);
            art.Children.Add(words);

            var card = new StackPanel();
            card.Children.Add(art);

            // Others released on this day.
            var others = games.Where(g => g.Rom.Id != rom.Id).Take(8).ToList();
            if (others.Count > 0)
            {
                var strip = new WrapPanel { Margin = new Thickness(16, 10, 16, 12) };
                var label = Text("Also celebrating today:", 12, false);
                label.Opacity = 0.7;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, 0, 10, 0);
                strip.Children.Add(label);
                foreach (HomeItem other in others)
                {
                    RomMRom game = other.Rom;
                    int age = today.Year - (ReleaseDay(game)?.Year ?? today.Year);
                    var link = Text($"{game.Name} ({age})", 12, false);
                    link.Foreground = Gold;
                    link.Cursor = Cursors.Hand;
                    link.Margin = new Thickness(0, 2, 14, 2);
                    link.MouseEnter += (s, e) => link.TextDecorations = TextDecorations.Underline;
                    link.MouseLeave += (s, e) => link.TextDecorations = null;
                    link.MouseLeftButtonUp += (s, e) => ShowGame(game);
                    strip.Children.Add(link);
                }
                card.Children.Add(strip);
            }

            var frame = new Border
            {
                Margin = new Thickness(0, 22, 0, 0),
                BorderBrush = new LinearGradientBrush(Color.FromRgb(0xF2, 0xC2, 0x4B), Color.FromRgb(0x9A, 0x6B, 0x16), 0),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x0B, 0x0E, 0x18)),
                Child = card,
            };
            AttachHoverCard(view, rom);
            return frame;
        }

        // The game of the day: its art and logo beside its name, blurb and tags, with the time
        // left until the next pick, counted down.
        private UIElement BuildDaily(HomeItem item)
        {
            RomMRom rom = item.Rom;
            var capsule = BuildWideCapsule(item, 520, Math.Round(520 * 215 / 460.0));

            var words = new StackPanel { Margin = new Thickness(22, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = "GAME OF THE DAY", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x67, 0xC1, 0xF5)) });
            var name = Text(rom.Name ?? rom.FileNameNoExt ?? "", 24, true);
            name.Margin = new Thickness(0, 4, 0, 8);
            words.Children.Add(name);
            var blurb = Text(ShortText(rom), 13, false);
            blurb.Opacity = 0.85;
            blurb.LineHeight = 19;
            blurb.MaxHeight = 19 * 4;
            blurb.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(blurb);
            WhenSteamText(rom, steam => blurb.Text = ShortText(rom));
            var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
            foreach (string genre in GenresOf(rom).Take(5))
                chips.Children.Add(GenreChip(genre, new Thickness(6, 2, 6, 2), new Thickness(0, 0, 4, 4)));
            words.Children.Add(chips);
            var clock = Text("", 12, false);
            clock.Opacity = 0.6;
            words.Children.Add(clock);

            Action tick = () =>
            {
                TimeSpan left = DateTime.Now.Date.AddDays(1) - DateTime.Now;
                clock.Text = $"A new pick in {(int)left.TotalHours}h {left.Minutes:00}m";
            };
            tick();
            _dailyClock?.Stop();
            _dailyClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _dailyClock.Tick += (s, e) => tick();
            _dailyClock.Start();

            var row = new DockPanel();
            DockPanel.SetDock(capsule, Dock.Left);
            row.Children.Add(capsule);
            row.Children.Add(words);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x50, 0x16, 0x20, 0x2D)),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 22, 0, 0),
                Child = row,
            };
        }
    
        // ---- A game's own anniversary, on its Store page ---------------------------------------

        /// <summary>
        /// How far a game's anniversary is from today: 0 on the day, up to 3 either side for
        /// "anniversary week", null otherwise (or with no usable release date). Games dated
        /// 1 January are left out, as that is where games known only by their year are put.
        /// </summary>
        private static int? AnniversaryDistance(RomMRom rom, out int years, out DateTime released)
        {
            years = 0;
            released = DateTime.MinValue;
            DateTime? day = ReleaseDay(rom);
            DateTime today = DateTime.Now.Date;
            if (!day.HasValue || day.Value.Year >= today.Year || (day.Value.Month == 1 && day.Value.Day == 1))
                return null;
            released = day.Value;
            DateTime thisYear = DayThisYear(rom, today);
            int distance = (int)Math.Round((thisYear - today).TotalDays);
            // Near the year's turn the nearest anniversary may be last year's or next year's.
            if (distance > 180) { thisYear = thisYear.AddYears(-1); distance = (int)Math.Round((thisYear - today).TotalDays); }
            if (distance < -180) { thisYear = thisYear.AddYears(1); distance = (int)Math.Round((thisYear - today).TotalDays); }
            if (Math.Abs(distance) > 3)
                return null;
            years = thisYear.Year - day.Value.Year;
            return years > 0 ? distance : (int?)null;
        }

        /// <summary>
        /// The top of a game's Store page on its anniversary: a gold banner ("Fable turns 20
        /// today"), a milestone tag on round numbers; in the days around it, a quieter line.
        /// Null on any other day.
        /// </summary>
        private UIElement AnniversaryBanner(RomMRom rom)
        {
            int? distance = AnniversaryDistance(rom, out int years, out DateTime released);
            if (!distance.HasValue)
                return null;
            string name = rom.Name ?? rom.FileNameNoExt ?? "";

            if (distance.Value != 0)
            {
                // Anniversary week: a gold-edged pill.
                string when = distance.Value < 0
                    ? $"turned {years} {(distance.Value == -1 ? "yesterday" : $"{-distance.Value} days ago")}"
                    : $"turns {years} {(distance.Value == 1 ? "tomorrow" : $"in {distance.Value} days")}";
                var line = new TextBlock { Text = $"ANNIVERSARY WEEK  \u00B7  {name} {when}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Gold };
                return new Border
                {
                    BorderBrush = Gold,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 4, 12, 5),
                    Margin = new Thickness(0, 12, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0xF2, 0xC2, 0x4B)),
                    Child = line,
                };
            }

            bool milestone = years % 5 == 0;
            var star = new TextBlock
            {
                Text = "\uE735",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 30,
                Foreground = Gold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xF2, 0xC2, 0x4B), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9 },
            };

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            tags.Children.Add(new TextBlock { Text = "ON THIS DAY", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Gold, VerticalAlignment = VerticalAlignment.Center });
            if (milestone)
            {
                tags.Children.Add(new Border
                {
                    Background = Gold,
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(10, 0, 0, 0),
                    Child = new TextBlock { Text = $"{years}-YEAR MILESTONE", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.Black },
                });
            }
            words.Children.Add(tags);
            words.Children.Add(new TextBlock { Text = $"{name} turns {years} today!", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
            string platform = !string.IsNullOrEmpty(rom.PlatformDisplayName) ? rom.PlatformDisplayName : rom.PlatformName;
            var sub = Text($"Released {released.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}{(string.IsNullOrEmpty(platform) ? "" : " on " + platform)} \u2014 happy {Ordinal(years)} birthday.", 13, false);
            sub.Opacity = 0.8;
            sub.Margin = new Thickness(0, 4, 0, 0);
            words.Children.Add(sub);

            var row = new DockPanel();
            DockPanel.SetDock(star, Dock.Left);
            row.Children.Add(star);
            row.Children.Add(words);

            var glow = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0xF2, 0xC2, 0x4B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 0xF2, 0xC2, 0x4B), 0.6));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xF2, 0xC2, 0x4B), 1));
            return new Border
            {
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(20, 14, 20, 14),
                Background = glow,
                BorderBrush = new LinearGradientBrush(Color.FromRgb(0xF2, 0xC2, 0x4B), Color.FromArgb(0x00, 0xF2, 0xC2, 0x4B), 0),
                BorderThickness = new Thickness(3, 1, 0, 1),
                Child = row,
            };
        }

        /// <summary>Whether today is the game's anniversary (for the page's gold touches).</summary>
        private static bool AnniversaryToday(RomMRom rom)
        {
            return AnniversaryDistance(rom, out int _, out DateTime _) == 0;
        }
}
}
