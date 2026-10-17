using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using RomM.Downloads;
using RomM.Games;
using RomM.Saves;
using RomM.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RomM.LibraryHome
{
    /// <summary>
    /// The library's Home page, after Steam's: shelves of covers for recent games, games being
    /// played, and every game, in place of a single game's page. A theme places it over the
    /// game details with &lt;ContentControl x:Name="RomM_LibraryHome" /&gt;; it shows itself while
    /// CloudStatus.LibraryHomeVisible is on (the Home button) and steps aside when a game is
    /// picked.
    ///
    /// Everything here is read from Playnite's own library; nothing is fetched.
    /// </summary>
    /// <summary>Where a game stands with its emulator, for the big Recent card's button.</summary>
    public enum HeroEmulator
    {
        /// <summary>Nothing in the way: Play, or Install.</summary>
        Ready,
        /// <summary>One can be set up (Install Emulator).</summary>
        InstallEmulator,
        /// <summary>One could be, but a file it needs is not on the RomM server.</summary>
        Unavailable,
        /// <summary>None can be set up for its platform.</summary>
        Unsupported,
    }

    /// <summary>A game on the RomM server whose release anniversary falls near today.</summary>
    public class ServerAnniversary
    {
        public int RomId { get; set; }
        public string Name { get; set; }
        public DateTime Released { get; set; }
        public string Art { get; set; }
    }

    public partial class LibraryHomeView : PluginUserControl
    {
        // Steam's library capsules: 2:3 covers on shelves of one height, a little gap between.
        private const double TileWidth = 172;
        private const double TileHeight = 258;
        // The Recent Games shelf is a little smaller, as Steam's is.
        private const double RecentWidth = 140;
        private const double RecentHeight = 210;
        private const double Gap = 14;
        // All Games: a little larger than the shelves' covers, and further apart.
        private const double AllWidth = 190;
        private const double AllHeight = 285;
        private const double AllGap = 22;
        // Room around a sideways row, so a cover growing under the pointer is not cut off.
        private static readonly Thickness LiftRoom = new Thickness(6, 8, 6, 2);
        private const int MaxAllGames = 600;

        private static readonly ILogger s_log = LogManager.GetLogger();

        private static readonly string[] SortChoices = { "Name", "Last Played", "Recently Added", "Time Played" };

        private readonly IPlayniteAPI _api;
        private readonly CloudStatusModel _model;
        private readonly DownloadQueueController _downloads;
        private readonly PlaySessions _sessions;
        private readonly AchievementViews.AchievementStore _achievements;
        private readonly Func<Game, System.Threading.Tasks.Task<int?>> _steamIdOf;
        private readonly SteamNews _news;
        private readonly Func<System.Threading.Tasks.Task<List<ServerAnniversary>>> _serverAnniversaries;
        private readonly Action<int> _openStoreGame;
        private readonly GameCollections _collections;
        private readonly ShelfLayout _layout;
        private readonly Func<Game, HeroEmulator> _emulatorOf;
        private readonly Action<Game> _setUpEmulator;
        private List<NewsItem> _serverNews = new List<NewsItem>();

        // What's New: its place on the page, and the Steam news found for the library's games.
        private StackPanel _whatsNew;
        private List<NewsItem> _steamNews = new List<NewsItem>();
        private DateTime _steamNewsAt = DateTime.MinValue;
        private bool _steamNewsLoading;
        private UIElement _downloadsPage;
        private readonly ScrollViewer _scroll;
        private readonly StackPanel _body = new StackPanel { Margin = new Thickness(28, 18, 20, 40) };
        private readonly DispatcherTimer _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private int _sort;
        private bool _stale = true;

        public LibraryHomeView(IPlayniteAPI api, CloudStatusModel model, DownloadQueueController downloads, PlaySessions sessions = null, AchievementViews.AchievementStore achievements = null,
            Func<Game, System.Threading.Tasks.Task<int?>> steamIdOf = null, SteamNews news = null,
            Func<System.Threading.Tasks.Task<List<ServerAnniversary>>> serverAnniversaries = null, Action<int> openStoreGame = null,
            GameCollections collections = null, ShelfLayout shelves = null,
            Func<Game, HeroEmulator> emulatorOf = null, Action<Game> setUpEmulator = null)
        {
            _emulatorOf = emulatorOf;
            _setUpEmulator = setUpEmulator;
            _collections = collections;
            _layout = shelves;
            _serverAnniversaries = serverAnniversaries;
            _openStoreGame = openStoreGame;
            _steamIdOf = steamIdOf;
            _news = news;
            _api = api;
            _model = model;
            _downloads = downloads;
            _sessions = sessions;
            _achievements = achievements;
            Focusable = false;
            SetResourceReference(BackgroundProperty, "MainBackgroundBrush");

            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body };
            SmoothScroll.Attach(_scroll);
            SmoothScroll.WidenScrollBars(this);
            Content = _scroll;

            _model.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(CloudStatusModel.LibraryHomeVisible))
                    Dispatcher.BeginInvoke(new Action(ApplyVisibility));
                else if (e.PropertyName == nameof(CloudStatusModel.LibraryPage))
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _stale = true;
                        ApplyVisibility();
                    }));
            };

            // The library changing (a game played, added, removed, installed) redraws the
            // shelves: at once if they are showing, otherwise when they next are.
            _refresh.Tick += (s, e) =>
            {
                _refresh.Stop();
                if (Visibility == Visibility.Visible)
                    Build();
                else
                    _stale = true;
            };
            Action changed = () => Dispatcher.BeginInvoke(new Action(() =>
            {
                _refresh.Stop();
                _refresh.Start();
            }));
            _api.Database.Games.ItemCollectionChanged += (s, e) => changed();
            _api.Database.Games.ItemUpdated += (s, e) => changed();
            // A download starting or ending: its card comes and goes on Recent Games.
            if (_downloads != null)
                _downloads.ViewModel.Items.CollectionChanged += (s, e) => changed();
            // A game finishing its download moves to the front of Recent Games.
            if (_downloads?.Recent != null)
                _downloads.Recent.Changed += () => changed();
            if (_collections != null)
                _collections.Changed += () => changed();

            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            bool show = _model.LibraryHomeVisible;
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && _stale)
                Build();
        }

        private static TextBlock Text(string text, double size, bool bold)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        // The Downloads page, as Steam's library menu has it: the RomM download queue, the
        // same list as the "RomM Downloads" sidebar view.
        private void BuildDownloads()
        {
            _body.Children.Clear();
            var title = Text("Downloads", 22, true);
            title.Margin = new Thickness(0, 0, 0, 14);
            _body.Children.Add(title);
            if (_downloads == null)
            {
                var none = Text("Downloads are not available.", 14, false);
                none.Opacity = 0.7;
                _body.Children.Add(none);
                return;
            }
            if (_downloadsPage == null)
                _downloadsPage = new RomMDownloadQueueControl(_downloads);
            if (_downloadsPage is FrameworkElement page && page.Parent is Panel old)
                old.Children.Remove(page);
            _body.Children.Add(_downloadsPage);
        }

        private void Build()
        {
            _stale = false;
            if (_model.LibraryPage == "Downloads")
            {
                BuildDownloads();
                return;
            }
            try
            {
                double offset = _scroll.VerticalOffset;
                _body.Children.Clear();

                List<Game> games = _api.Database.Games.Where(g => !g.Hidden).ToList();
                if (games.Count == 0)
                {
                    var empty = Text("Your library is empty. Add games from the Store.", 15, false);
                    empty.Opacity = 0.7;
                    _body.Children.Add(empty);
                    return;
                }

                // Recent Games: the most recently played first, the newest as a wide card with
                // the time played; grouped under "Today", "This week" and so on, as Steam does.
                // What's New: the Steam news of the library's games, and what has happened in
                // the library lately. The Steam news comes in the background.
                _whatsNew = new StackPanel();
                _body.Children.Add(_whatsNew);
                FillWhatsNew(games);
                LoadSteamNews(games);

                // The shelves, as the user arranged them.
                BuildShelves(games);

                _scroll.ScrollToVerticalOffset(offset);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Home] Could not build the library home page: {ex.Message}");
            }
        }

        // A shelf: its name and count, an optional "SORT BY" choice, then its games.
        private FrameworkElement AddShelf(string title, int? count, UIElement sort, UIElement content, FrameworkElement arrows = null, Panel into = null)
        {
            into = into ?? _body;
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var name = Text(title, 17, false);
            name.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(name);
            if (count.HasValue)
            {
                var number = Text($" ({count.Value})", 17, false);
                number.Opacity = 0.55;
                number.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(number);
            }
            if (sort != null)
            {
                var label = Text("SORT BY", 11, false);
                label.Opacity = 0.55;
                label.Margin = new Thickness(26, 0, 8, 0);
                label.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(label);
                head.Children.Add(sort);
            }

            // The shelf's arrows at the right end of its heading, as on Steam.
            // The first shelf on the page has no space above it: What's New when it is there,
            // else the first after its (empty) place.
            bool newsEmpty = _whatsNew == null || _whatsNew.Children.Count == 0;
            bool first = ReferenceEquals(into, _whatsNew)
                || _body.Children.Count == 0
                || (newsEmpty && _body.Children.Count == 1 && ReferenceEquals(_body.Children[0], _whatsNew));
            var heading = new DockPanel { Margin = new Thickness(0, first ? 0 : 30, 0, 8), LastChildFill = true };
            if (arrows != null)
            {
                DockPanel.SetDock(arrows, Dock.Right);
                heading.Children.Add(arrows);
            }
            heading.Children.Add(head);
            into.Children.Add(heading);
            into.Children.Add(content);
            return heading;
        }

        // (Re)draws What's New from the library's own events and the Steam news found so far.
        private void FillWhatsNew(List<Game> games)
        {
            if (_whatsNew == null)
                return;
            _whatsNew.Children.Clear();
            // Today's birthdays lead, then anniversaries coming up, then everything else, newest first.
            DateTime today = DateTime.Now.Date;
            var news = _steamNews.Concat(_serverNews).Concat(WhatsNew(games))
                .OrderByDescending(n => n.Years > 0 && n.When.Date == today)
                .ThenByDescending(n => n.Upcoming)
                .ThenByDescending(n => n.When)
                .Take(24)
                .ToList();
            if (news.Count == 0)
                return;
            var newsRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (UIElement card in NewsCards(news))
                newsRow.Children.Add(card);
            UIElement newsShelf = SideScroll.RowWithHeaderArrows(newsRow, out FrameworkElement newsArrows);
            AddShelf("What's New", null, null, newsShelf, newsArrows, _whatsNew);
        }

        /// <summary>
        /// Fetches the latest announcements of the library's games that are on Steam (Playnite's
        /// Steam games, and RomM games RomM has matched on Steam), the most recently played and
        /// added first, and adds those from the last four months to What's New. At most every ten
        /// minutes; each game's news is kept for a few hours.
        /// </summary>
        private void LoadSteamNews(List<Game> games)
        {
            if (_news == null || _steamIdOf == null || _steamNewsLoading || DateTime.UtcNow - _steamNewsAt < TimeSpan.FromMinutes(10))
                return;
            _steamNewsLoading = true;

            var candidates = games
                .OrderByDescending(g => g.LastActivity ?? DateTime.MinValue)
                .ThenByDescending(g => g.Added ?? DateTime.MinValue)
                .Take(200)
                .ToList();
            var owned = new HashSet<string>(games.Select(g => g.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            System.Threading.Tasks.Task.Run(async () =>
            {
                // Anniversaries of games on the RomM server that are not in the library.
                var serverNews = new List<NewsItem>();
                try
                {
                    if (_serverAnniversaries != null)
                    {
                        DateTime today = DateTime.Now.Date;
                        foreach (ServerAnniversary party in (await _serverAnniversaries().ConfigureAwait(false)) ?? new List<ServerAnniversary>())
                        {
                            if (owned.Contains(party.Name ?? ""))
                                continue;
                            int day = Math.Min(party.Released.Day, DateTime.DaysInMonth(today.Year, party.Released.Month));
                            DateTime thisYear = new DateTime(today.Year, party.Released.Month, day);
                            int distance = (int)(thisYear - today).TotalDays;
                            int years = today.Year - party.Released.Year;
                            serverNews.Add(new NewsItem
                            {
                                When = thisYear,
                                Name = party.Name,
                                RomId = party.RomId,
                                Picture = party.Art,
                                Text = AnniversaryText(party.Name, years, distance) + " In the Store.",
                                Upcoming = distance > 0,
                                Years = years,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Home] No server anniversaries: {ex.Message}");
                }

                var found = new List<NewsItem>();
                // Announcements are far apart for many games, so a few months are looked at.
                DateTime since = DateTime.UtcNow.AddDays(-120);
                // The thirty most recently played games that are on Steam (games not on Steam
                // are passed over without asking anyone, once their answer is known).
                int onSteam = 0;
                var seenApps = new HashSet<int>();
                foreach (Game game in candidates)
                {
                    if (onSteam >= 30)
                        break;
                    try
                    {
                        int? appId = await _steamIdOf(game).ConfigureAwait(false);
                        if (!appId.HasValue || appId.Value <= 0 || !seenApps.Add(appId.Value))
                            continue;
                        onSteam++;
                        var posts = await _news.GetAsync(appId.Value).ConfigureAwait(false);
                        foreach (SteamNewsItem post in posts.Where(p => p.PostedUtc > since).OrderByDescending(p => p.PostedUtc).Take(2))
                        {
                            found.Add(new NewsItem
                            {
                                When = post.PostedUtc.ToLocalTime(),
                                Game = game,
                                Text = post.Title,
                                Picture = post.Image,
                                Url = post.Url,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Home] No Steam news for {game.Name}: {ex.Message}");
                    }
                }

                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    _steamNews = found;
                    _serverNews = serverNews.OrderByDescending(n => n.When).Take(8).ToList();
                    _steamNewsAt = DateTime.UtcNow;
                    _steamNewsLoading = false;
                    if (_whatsNew != null)
                        FillWhatsNew(_api.Database.Games.Where(g => !g.Hidden).ToList());
                }));
            });
        }

        // ---- What's New ------------------------------------------------------------------------

        private sealed class NewsItem
        {
            public DateTime When;      // local
            public Game Game;
            public string Text;
            public string Badge;       // an achievement's badge, shown over the picture
            public bool Upcoming;      // an anniversary still to come
            public string Picture;     // a picture of its own (a news post's), else the game's art
            public string Url;         // where clicking goes (a news post), else the game
            public int Years;          // an anniversary's age; 0 for anything else
            public string Name;        // a game not in the library (a server anniversary)
            public int RomId;          // ... and its Store page
        }

        /// <summary>
        /// What has happened in the library lately, newest first, as Steam's What's New shelf:
        /// games added in the last month, achievements unlocked in it, and anniversaries of the
        /// library's games in the days around today.
        /// </summary>
        private List<NewsItem> WhatsNew(List<Game> games)
        {
            var news = new List<NewsItem>();
            DateTime now = DateTime.Now;
            DateTime monthAgo = now.AddDays(-30);
            DateTime today = now.Date;

            foreach (Game game in games)
            {
                try
                {
                    if (game.Added.HasValue && game.Added.Value.ToLocalTime() > monthAgo)
                        news.Add(new NewsItem { When = game.Added.Value.ToLocalTime(), Game = game, Text = "Added to your library" });

                    var data = _achievements?.Get(game.Id);
                    if (data != null)
                    {
                        var unlocked = data.Items
                            .Where(i => i.Unlocked)
                            .Select(i => new { Item = i, At = (i.UnlockedHardcoreUtc ?? i.UnlockedUtc).Value.ToLocalTime() })
                            .Where(x => x.At > monthAgo)
                            .OrderByDescending(x => x.At)
                            .ToList();
                        if (unlocked.Count == 1)
                            news.Add(new NewsItem { When = unlocked[0].At, Game = game, Text = $"Unlocked \u201C{unlocked[0].Item.Title}\u201D", Badge = unlocked[0].Item.BadgeUrl });
                        else if (unlocked.Count > 1)
                            news.Add(new NewsItem { When = unlocked[0].At, Game = game, Text = $"Unlocked {unlocked.Count} achievements, most recently \u201C{unlocked[0].Item.Title}\u201D", Badge = unlocked[0].Item.BadgeUrl });
                    }

                    // Anniversaries, three days either side; 1 January is where games known only
                    // by their year are put, so it is left out.
                    var released = game.ReleaseDate;
                    if (released.HasValue && released.Value.Month.HasValue && released.Value.Day.HasValue
                        && !(released.Value.Month == 1 && released.Value.Day == 1) && released.Value.Year < today.Year)
                    {
                        int month = released.Value.Month.Value;
                        int day = Math.Min(released.Value.Day.Value, DateTime.DaysInMonth(today.Year, month));
                        DateTime thisYear = new DateTime(today.Year, month, day);
                        int distance = (int)(thisYear - today).TotalDays;
                        if (Math.Abs(distance) <= 7)
                        {
                            int years = today.Year - released.Value.Year;
                            news.Add(new NewsItem { When = thisYear, Game = game, Text = AnniversaryText(game.Name, years, distance), Upcoming = distance > 0, Years = years });
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Home] What's New skipped {game.Name}: {ex.Message}");
                }
            }

            return news.OrderByDescending(n => n.Upcoming).ThenByDescending(n => n.When).Take(16).ToList();
        }

        private static string Ordinal(int n)
        {
            int last2 = n % 100;
            string suffix = last2 >= 11 && last2 <= 13 ? "th" : (n % 10 == 1 ? "st" : n % 10 == 2 ? "nd" : n % 10 == 3 ? "rd" : "th");
            return n + suffix;
        }

        // "Happy 20th birthday, Fable!"; "Fable turned 20 three days ago"; "Fable turns 20 tomorrow".
        private static string AnniversaryText(string name, int years, int distance)
        {
            if (distance == 0)
                return $"Happy {Ordinal(years)} birthday, {name}! Released {years} years ago today.";
            string when = distance == -1 ? "yesterday" : distance == 1 ? "tomorrow"
                : distance < 0 ? $"{-distance} days ago" : $"in {distance} days";
            return distance < 0 ? $"{name} turned {years} {when}" : $"{name} turns {years} {when}";
        }

        // The cards, each under its time label ("Today", "This week", "Coming up" ...).
        private IEnumerable<UIElement> NewsCards(List<NewsItem> news)
        {
            const double width = 260;
            const double pictureHeight = 146;
            foreach (NewsItem item in news)
            {
                Game game = item.Game;
                bool party = item.Years > 0;
                var stack = new StackPanel { Width = width, Margin = new Thickness(0, 0, Gap, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };

                var when = Text(item.Upcoming ? "Coming up" : Bucket(item.When.ToUniversalTime()), 11, false);
                when.Opacity = 0.55;
                when.Margin = new Thickness(0, 0, 0, 6);
                stack.Children.Add(when);

                var picture = new Grid { Height = pictureHeight, ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)) };
                string art = item.Picture ?? (game == null ? null : FullPath(game.BackgroundImage) ?? FullPath(game.CoverImage));
                if (art != null)
                {
                    var image = new Image { Stretch = Stretch.UniformToFill };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                    StoreImages.Show(image, art, 520);
                    picture.Children.Add(image);
                }
                if (!string.IsNullOrEmpty(item.Badge))
                {
                    var badge = new Image { Width = 54, Height = 54, Stretch = Stretch.UniformToFill };
                    StoreImages.Show(badge, item.Badge, 108);
                    picture.Children.Add(new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(8),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1),
                        Child = badge,
                    });
                }
                if (party)
                {
                    // An anniversary: announced in gold, with a tag (a milestone every fifth year).
                    picture.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x4B)),
                        Padding = new Thickness(6, 1, 6, 2),
                        Margin = new Thickness(8),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Child = new TextBlock
                        {
                            Text = item.Years % 5 == 0 ? $"{item.Years}-YEAR MILESTONE" : "ANNIVERSARY",
                            FontSize = 10,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.Black,
                        },
                    });
                    stack.Children.Add(new Border
                    {
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x4B)),
                        BorderThickness = new Thickness(2),
                        Child = picture,
                    });
                }
                else
                {
                    stack.Children.Add(picture);
                }

                var text = Text(item.Text, 13, false);
                if (party)
                    text.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x4B));
                text.TextWrapping = TextWrapping.Wrap;
                text.MaxHeight = 36;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.Margin = new Thickness(0, 8, 0, 4);
                stack.Children.Add(text);

                var footer = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.6 };
                string icon = game == null ? null : FullPath(game.Icon);
                if (icon != null)
                {
                    var small = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), Stretch = Stretch.Uniform };
                    StoreImages.Show(small, icon, 32);
                    footer.Children.Add(small);
                }
                var name = Text(game?.Name ?? item.Name ?? "", 11, false);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.MaxWidth = width - 24;
                footer.Children.Add(name);
                stack.Children.Add(footer);

                stack.MouseEnter += (s, e) => picture.Opacity = 0.85;
                stack.MouseLeave += (s, e) => picture.Opacity = 1;
                string url = item.Url;
                int romId = item.RomId;
                if (url != null)
                    stack.ToolTip = "Read on Steam";
                else if (game == null)
                    stack.ToolTip = "See it in the Store";
                stack.MouseLeftButtonUp += (s, e) =>
                {
                    if (url == null && game == null)
                    {
                        _openStoreGame?.Invoke(romId);
                        return;
                    }
                    if (url == null)
                    {
                        Open(game);
                        return;
                    }
                    try
                    {
                        System.Diagnostics.Process.Start(url);
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Home] Could not open {url}: {ex.Message}");
                    }
                };
                yield return stack;
            }
        }

        // "Today", "Yesterday", "This week", "1 week ago" ... then the month, then the year.
        private static string Bucket(DateTime time)
        {
            DateTime local = time.ToLocalTime();
            int days = (DateTime.Now.Date - local.Date).Days;
            if (days <= 0) return "Today";
            if (days == 1) return "Yesterday";
            if (days < 7) return "This week";
            if (days < 28) return days / 7 == 1 ? "1 week ago" : $"{days / 7} weeks ago";
            if (local.Year == DateTime.Now.Year) return local.ToString("MMMM");
            return local.ToString("yyyy");
        }

        // "Oct 8", as under Steam's covers.
        private static string Short(DateTime time)
        {
            DateTime local = time.ToLocalTime();
            return local.ToString(local.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy");
        }

        private static string Duration(ulong seconds)
        {
            if (seconds < 3600)
                return $"{seconds / 60} min";
            return $"{seconds / 3600.0:0.0} hrs";
        }

        // The Recent Games row: downloads first, then games grouped by when they were played (or
        // downloaded), each group under its label.
        private UIElement RecentRow(List<Tuple<Game, DownloadQueueItem>> downloading, List<Tuple<Game, DateTime>> recent)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = LiftRoom };
            if (downloading.Count > 0)
            {
                var group = new StackPanel();
                var label = Text("Downloading", 11, false);
                label.Opacity = 0.55;
                label.Margin = new Thickness(0, 0, 0, 6);
                group.Children.Add(label);
                var cards = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var download in downloading)
                    cards.Children.Add(DownloadCard(download.Item1, download.Item2));
                group.Children.Add(cards);
                row.Children.Add(group);
            }
            StackPanel tiles = null;
            string current = null;
            for (int i = 0; i < recent.Count; i++)
            {
                Game game = recent[i].Item1;
                string bucket = Bucket(recent[i].Item2);
                if (bucket != current)
                {
                    current = bucket;
                    var group = new StackPanel();
                    var label = Text(bucket, 11, false);
                    label.Opacity = 0.55;
                    label.Margin = new Thickness(0, 0, 0, 6);
                    group.Children.Add(label);
                    tiles = new StackPanel { Orientation = Orientation.Horizontal };
                    group.Children.Add(tiles);
                    row.Children.Add(group);
                }
                // The front of the row is one large card: a download's when one is under way, else the
                // latest game's (Steam shows only one).
                tiles.Children.Add(i == 0 && downloading.Count == 0 ? HeroTile(game) : Tile(game, null, RecentWidth, RecentHeight));
            }
            return row;
        }

        private string FullPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : _api.Database.GetFullFilePath(path);
            }
            catch
            {
                return null;
            }
        }

        // A cover on a shelf, with an optional date under it. Games not installed are dimmed.
        private UIElement Tile(Game game, string caption, double width = TileWidth, double height = TileHeight, double gap = Gap)
        {
            var frame = new Grid { Width = width, Height = height, ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)) };

            // Shown under the cover, so it is what you see when a game has none.
            var title = Text(game.Name ?? "", 13, false);
            title.TextWrapping = TextWrapping.Wrap;
            title.TextAlignment = TextAlignment.Center;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(10);
            frame.Children.Add(title);

            // The whole cover, filling the tile: covers of older games are not Steam's 2:3, so
            // they are stretched to it (slightly, a few percent) rather than cropped or boxed.
            string cover = FullPath(game.CoverImage);
            if (!string.IsNullOrEmpty(cover))
            {
                var image = new Image { Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                StoreImages.Show(image, cover, 360);
                frame.Children.Add(image);
            }

            if (!string.IsNullOrEmpty(caption))
            {
                frame.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x18, 0x1C, 0x24)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 1, 8, 2),
                    Margin = new Thickness(0, 0, 0, 7),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new TextBlock { Text = caption, FontSize = 11, Foreground = Brushes.White },
                });
            }

            // Pointed at: brightened, and lifted with its gloss (TileShine); no outline.
            double rest = game.IsInstalled ? 0.95 : 0.62;
            var tile = new Border { Margin = new Thickness(0, 0, gap, gap), Cursor = Cursors.Hand, Child = frame, Opacity = rest };
            tile.MouseEnter += (s, e) => tile.Opacity = 1;
            tile.MouseLeave += (s, e) => tile.Opacity = rest;
            tile.MouseLeftButtonUp += (s, e) => Open(game);
            LibraryHoverCard.Attach(tile, game);
            TileShine.Apply(tile, frame);
            AttachGameMenu(tile, game);
            return tile;
        }

        // The newest recent game, two covers wide: its background art and logo, with a panel
        // along the bottom holding a Play (or Install) button and the time played.
        private UIElement HeroTile(Game game)
        {
            double width = RecentWidth * 2 + Gap;
            var frame = new Grid { Width = width, Height = RecentHeight, ClipToBounds = true, Background = Brushes.Black, Cursor = Cursors.Hand };

            string art = FullPath(game.BackgroundImage) ?? FullPath(game.CoverImage);
            Image heroArt = null;
            if (art != null)
            {
                heroArt = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(heroArt, BitmapScalingMode.Fant);
                StoreImages.Show(heroArt, art, 640);
                frame.Children.Add(heroArt);
            }

            // The logo over the upper part, or the name when there is none.
            string logo = null;
            try
            {
                string path = GameLogoControl.LogoPath(_api, game.Id);
                if (System.IO.File.Exists(path))
                    logo = path;
            }
            catch
            {
                logo = null;
            }
            var top = new Grid { Height = RecentHeight - 72, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            if (logo != null)
            {
                var mark = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(28, 18, 28, 10) };
                RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.Fant);
                StoreImages.Show(mark, logo, 500);
                top.Children.Add(mark);
            }
            else if (art == null || game.BackgroundImage == null)
            {
                var name = new TextBlock { Text = game.Name ?? "", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
                top.Children.Add(name);
            }
            frame.Children.Add(top);

            // The bottom panel.
            var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, 0x2A, 0x2F, 0x3A), 0));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xE8, 0x1E, 0x22, 0x2A), 1));
            var panel = new DockPanel { Height = 72, VerticalAlignment = VerticalAlignment.Bottom, Background = shade };

            HeroEmulator emulator = HeroEmulator.Ready;
            try
            {
                emulator = _emulatorOf?.Invoke(game) ?? HeroEmulator.Ready;
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Home] Could not tell {game.Name}'s emulator state: {ex.Message}");
            }
            if (emulator != HeroEmulator.Ready)
            {
                // The Store's words, on a wider button; the time played makes way for it.
                panel.Children.Add(EmulatorButton(game, emulator));
                frame.Children.Add(panel);
                var waiting = new Border { Margin = new Thickness(0, 0, Gap, Gap), Child = frame };
                waiting.MouseLeftButtonUp += (s, e) => Open(game);
                TileShine.Apply(waiting, frame, 1.06, heroArt ?? new FrameworkElement());
                AttachGameMenu(waiting, game);
                return waiting;
            }

            bool installed = game.IsInstalled;
            var glyph = new TextBlock
            {
                Text = installed ? "\uE768" : "\uE896",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var normal = new SolidColorBrush(installed ? Color.FromRgb(0x5C, 0xB8, 0x1D) : Color.FromRgb(0x1A, 0x9F, 0xFF));
            var action = new Border
            {
                Width = 46,
                Height = 46,
                Margin = new Thickness(12, 0, 12, 0),
                Background = normal,
                CornerRadius = new CornerRadius(2),
                ToolTip = installed ? "Play" : "Install",
                Child = glyph,
            };
            action.MouseEnter += (s, e) => action.Opacity = 0.85;
            action.MouseLeave += (s, e) => action.Opacity = 1;
            action.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                try
                {
                    if (game.IsInstalled)
                        _api.StartGame(game.Id);
                    else
                        _api.InstallGame(game.Id);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Home] Could not start {game.Name}: {ex.Message}");
                }
            };
            DockPanel.SetDock(action, Dock.Left);
            panel.Children.Add(action);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var head = new TextBlock { Text = "TIME PLAYED", FontSize = 11, Foreground = Brushes.White, Opacity = 0.75 };
            words.Children.Add(head);
            ulong lately = _sessions?.PlayedWithin(game.Id, 14) ?? 0;
            words.Children.Add(new TextBlock { Text = "Last two weeks: " + Duration(lately), FontSize = 11, Foreground = Brushes.White, Opacity = 0.6 });
            words.Children.Add(new TextBlock { Text = "Total: " + Duration(Math.Max(game.Playtime, lately)), FontSize = 11, Foreground = Brushes.White, Opacity = 0.6 });
            panel.Children.Add(words);
            frame.Children.Add(panel);

            var tile = new Border { Margin = new Thickness(0, 0, Gap, Gap), Child = frame };
            tile.MouseLeftButtonUp += (s, e) => Open(game);
            // Only the picture grows, inside the card: the card's words and Play button stay sharp.
            TileShine.Apply(tile, frame, 1.06, heroArt ?? new FrameworkElement());
            AttachGameMenu(tile, game);
            return tile;
        }

        // The big card's button while the game waits for an emulator: Install Emulator (blue, sets
        // it up), or Emulator Unavailable / Unsupported (grey; a click goes to the game's page,
        // as anywhere else on the card, where the Store link says more).
        private FrameworkElement EmulatorButton(Game game, HeroEmulator state)
        {
            bool canSetUp = state == HeroEmulator.InstallEmulator && _setUpEmulator != null;
            string text = state == HeroEmulator.Unsupported ? "Emulator Unsupported"
                : state == HeroEmulator.Unavailable ? "Emulator Unavailable"
                : "Install Emulator";
            var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (canSetUp)
                label.Children.Add(new TextBlock
                {
                    Text = "\uE896",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 16,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                });
            label.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = canSetUp ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var button = new Border
            {
                Height = 46,
                Margin = new Thickness(12, 0, 12, 0),
                Padding = new Thickness(16, 0, 16, 0),
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(canSetUp ? Color.FromRgb(0x1A, 0x9F, 0xFF) : Color.FromRgb(0x5B, 0x63, 0x6E)),
                Cursor = canSetUp ? Cursors.Hand : Cursors.Arrow,
                ToolTip = state == HeroEmulator.Unsupported ? "Nothing here can play this game's platform yet. Map an emulator to it in the RomMnite settings to play it."
                    : state == HeroEmulator.Unavailable ? "A file its emulator needs isn't on your RomM server. The game's Store page lists it."
                    : null,
                Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = label },
            };
            if (canSetUp)
            {
                button.MouseEnter += (s, e) => button.Opacity = 0.85;
                button.MouseLeave += (s, e) => button.Opacity = 1;
                button.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        _setUpEmulator(game);
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Home] Could not set up {game.Name}'s emulator: {ex.Message}");
                    }
                    // Redrawn: Play or Install now, or still waiting (declined, or failed).
                    _refresh.Stop();
                    _refresh.Start();
                };
            }
            return button;
        }

        // To the game's own page: select it in the library, which also closes Home.
        private void Open(Game game)
        {
            try
            {
                _model.LibraryHomeVisible = false;
                _api.MainView.SelectGame(game.Id);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Home] Could not open {game.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The "Home" button for the top of the library's left column. A theme places it with
    /// &lt;ContentControl x:Name="RomM_HomeButton" /&gt;. Lit while Home is showing.
    /// </summary>
    public class HomeButtonControl : PluginUserControl
    {
        private readonly CloudStatusModel _model;
        private readonly Border _button;
        private bool _over;

        public HomeButtonControl(CloudStatusModel model, Action showHome)
        {
            _model = model;
            Focusable = false;

            var label = new TextBlock { Text = "Home", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            _button = new Border { Height = 30, Margin = new Thickness(12, 7, 8, 7), Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Child = label };
            _button.MouseEnter += (s, e) => { _over = true; Paint(); };
            _button.MouseLeave += (s, e) => { _over = false; Paint(); };
            _button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                showHome();
            };
            Content = _button;

            _model.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(CloudStatusModel.LibraryHomeVisible) || e.PropertyName == nameof(CloudStatusModel.LibraryPage))
                    Dispatcher.BeginInvoke(new Action(Paint));
            };
            Paint();
        }

        private void Paint()
        {
            bool home = _model.LibraryHomeVisible && _model.LibraryPage == "Home";
            byte alpha = home ? (byte)0x50 : _over ? (byte)0x30 : (byte)0x18;
            _button.Background = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>
    /// A "Store Page" button for a RomM game's details, as Steam's library has: opens the
    /// Store at that game's page. A theme places it with
    /// &lt;ContentControl x:Name="RomM_StorePageButton" /&gt;; it hides itself for games
    /// that are not RomM's.
    /// </summary>
    public class StorePageButtonControl : PluginUserControl
    {
        private readonly RomM _plugin;
        private int _romId;

        /// <param name="asLink">Plain words, as the first entry of the game page's link bar
        /// (RomM_StorePageLink), rather than a button with an icon (RomM_StorePageButton).</param>
        public StorePageButtonControl(RomM plugin, bool asLink = false)
        {
            _plugin = plugin;
            Focusable = false;
            Visibility = Visibility.Collapsed;

            if (asLink)
            {
                // As the link bar's other entries: muted until pointed at, as Steam's bar is.
                var words = new TextBlock { Text = "Store Page", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
                words.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                // Matches the theme's link bar entries: a rounded box lifts it when pointed at.
                var link = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 20, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Open this game's page in the Store",
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = words,
                };
                link.MouseEnter += (s, e) => { words.Opacity = 1; link.Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)); };
                link.MouseLeave += (s, e) => { words.Opacity = 0.7; link.Background = Brushes.Transparent; };
                link.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (_romId > 0)
                        _plugin.CloudStatus.OpenStoreGame(_romId);
                };
                Content = link;
                return;
            }

            var icon = new TextBlock { Text = "\uE719", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var label = new TextBlock { Text = "Store Page", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(label);

            var button = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                ToolTip = "Open this game's page in the Store",
                VerticalAlignment = VerticalAlignment.Center,
                Child = row,
            };
            button.MouseEnter += (s, e) => button.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            button.MouseLeave += (s, e) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_romId > 0)
                    _plugin.CloudStatus.OpenStoreGame(_romId);
            };
            Content = button;
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _romId = 0;
            if (newContext != null && newContext.PluginId == _plugin.Id && RomMGameId.TryParse(newContext.GameId, out int romId, out string _))
                _romId = romId;
            Visibility = _romId > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
