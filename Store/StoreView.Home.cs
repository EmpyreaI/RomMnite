using Newtonsoft.Json.Linq;
using RomM.Games;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
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
    /// The Store's front page, after Steam's: a "Featured &amp; Recommended" carousel, rows of
    /// suggestions, and a feed of large cards that keeps growing as it is scrolled.
    ///
    /// What is recommended is the RomM server's own doing: its recommendation feed, built from
    /// what the signed-in user has played and rated. This page only lays it out. On a server
    /// without that feed, the page falls back to recently added games and random picks.
    /// </summary>
    public partial class StoreView
    {
        private const int FeaturedCount = 12;

        // Carousel layout: the page's side margin, an arrow's width, and the least a glimpse gets.
        private const double PageMargin = 24;
        private const double ArrowWidth = 40;
        private const double MinPeek = 110;
        private const int QueueBatch = 3;

        // One game on the home page, with why it is there.
        private sealed class HomeItem
        {
            public RomMRom Rom;
            public JObject Raw;
            public string Why;
            public string Seed;
            public string Detail;   // the server's finer reason ("Genre: Shooter")
            public bool Fresh;      // one of the three newest on the server, in its first week: marked in the carousel
            public bool Upgraded;   // moved up to "plays well" in its emulator's list this week: marked in the carousel, as Fresh
        }

        private readonly ScrollViewer _home = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };
        private SmoothScroll _homeGlide;
        private StackPanel _homeBody;
        private StackPanel _queuePanel;
        private bool _homeBuilt;

        // The endless feed: recommendations not used above, then random picks.
        private readonly List<HomeItem> _queue = new List<HomeItem>();
        private readonly HashSet<int> _homeShown = new HashSet<int>();
        private bool _queueLoading;
        private bool _queueDry;

        // The carousel.
        private List<HomeItem> _featured = new List<HomeItem>();
        private int _featuredIndex;
        private Action _showFeatured;
        private DispatcherTimer _carouselTimer;
        private bool _carouselHeld;
        private FrameworkElement _featuredBlock;
        private Action<HomeItem> _homeBanner;      // moves the banner behind the carousel to the game on show   // the carousel and its heading; the nav bar goes once it has passed

        private void ShowHome()
        {
            ShowBrowse();
            HideCategory();
            _heading.Text = "Home";
            // Home always opens at its top.
            _homeGlide?.ToTop();
            RecordStore(new Account.NavEntry { Store = true, Kind = "home" }, true);
            _gridArea.Visibility = Visibility.Collapsed;
            _home.Visibility = Visibility.Visible;

            if (!_homeBuilt)
            {
                _homeBuilt = true;
                if (Host.Length > 0)
                    LowerCurtain();
                BuildHome();
            }
        }

        private static string FacetWords(string facet)
        {
            switch (facet)
            {
                case "franchise":
                case "collection": return "Same series";
                case "genre": return "Genre";
                case "theme": return "Theme";
                case "keyword": return "Tagged";
                case "perspective": return "Perspective";
                case "developer": return "Developer";
                case "publisher": return "Publisher";
                default: return null;
            }
        }

        // The server's own explanation of a recommendation, in a few words.
        private static string Reasons(JToken entry)
        {
            var parts = new List<string>();
            foreach (JToken reason in entry["reasons"] as JArray ?? new JArray())
            {
                string words = FacetWords(Str(reason, "facet"));
                string value = Str(reason, "value");
                if (words == null)
                    continue;
                string part = value != null ? $"{words}: {value}" : words;
                if (!parts.Contains(part))
                    parts.Add(part);
                if (parts.Count == 2)
                    break;
            }
            return string.Join("  \u00B7  ", parts);
        }

        private static HomeItem ItemFrom(JObject rom, string why, string seed)
        {
            if (rom == null || rom["id"] == null)
                return null;
            try
            {
                return new HomeItem { Rom = rom.ToObject<RomMRom>(), Raw = rom, Why = why, Seed = seed };
            }
            catch
            {
                return null;
            }
        }

        private JToken Get(string path)
        {
            WaitForForeground();
            using (var response = HttpClientSingleton.Json.GetAsync(Host + path).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    s_log.Info($"[Store] {path} answered {(int)response.StatusCode}.");
                    return null;
                }
                return JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        }

        private void BuildHome()
        {
            _featuredBlock = null;
            _homeBody = new StackPanel { MaxWidth = ContentWidth, Margin = new Thickness(PageMargin, 8, PageMargin, 40) };
            var loading = Text("Loading...", 14, false);
            loading.Opacity = 0.7;
            _homeBody.Children.Add(loading);
            _homeInset.Child = _homeBody;

            if (Host.Length == 0)
            {
                loading.Text = "Sign in to RomM from the account menu to browse the store.";
                _homeBuilt = false;
                return;
            }

            // A fresh look at this week in history with each new Home (an earlier one may have
            // found the server unreachable).
            lock (_historyLock)
                _history = null;

            Task.Run(() =>
            {
                var recommended = new List<HomeItem>();
                var recent = new List<HomeItem>();
                bool reached = false;

                // The three asks at once rather than one after another: the page waits for the
                // slowest, not for all three in turn.
                DateTime day = DateTime.Now.Date;
                Task<JToken> feedAsk = Task.Run(() => Get("/api/recommendations?limit=50"));
                Task<JToken> latestAsk = Task.Run(() => Get("/api/roms?limit=16&order_by=created_at&order_dir=desc"));
                Task<HistoryDays> historyAsk = Task.Run(() => History(day));

                // Games that moved up to "plays well" in their emulator's list this week (seen by
                // earlier scans), and the next scan started when the last is a day old.
                Task<List<HomeItem>> upgradedAsk = Task.Run(() =>
                {
                    var items = new List<HomeItem>();
                    if (_plugin.CompatWatch == null)
                        return items;
                    foreach (CompatWatch.Upgrade upgrade in _plugin.CompatWatch.RecentUpgrades(TimeSpan.FromDays(7)).Take(6))
                    {
                        try
                        {
                            HomeItem item = ItemFrom(Get($"/api/roms/{upgrade.RomId}") as JObject, null, null);
                            if (item == null)
                                continue;
                            double days = (DateTime.UtcNow - upgrade.When).TotalDays;
                            item.Upgraded = true;
                            item.Why = $"Now plays well in {upgrade.Emulator} \u00B7 " +
                                       (days < 1 ? "rated today" : days < 2 ? "rated yesterday" : $"rated {(int)days} days ago");
                            items.Add(item);
                        }
                        catch (Exception ex)
                        {
                            s_log.Info($"[Store] Could not load upgraded game {upgrade.RomId}: {ex.Message}");
                        }
                    }
                    _plugin.CompatWatch.ScanSoon(Get);
                    return items;
                });

                try
                {
                    // The server's personal feed. Absent on older servers, which is fine.
                    var feed = feedAsk.GetAwaiter().GetResult() as JArray;
                    reached = true;
                    foreach (JToken entry in feed ?? new JArray())
                    {
                        string seed = Str(entry, "seed_rom_name");
                        string why = seed != null ? $"Because you played {seed}" : Reasons(entry);
                        HomeItem item = ItemFrom(entry["rom"] as JObject, why, seed);
                        if (item != null)
                        {
                            item.Detail = Reasons(entry);
                            recommended.Add(item);
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not load recommendations: {ex.Message}");
                }

                try
                {
                    var latest = latestAsk.GetAwaiter().GetResult();
                    reached = true;
                    foreach (JToken rom in latest?["items"] as JArray ?? new JArray())
                    {
                        HomeItem item = ItemFrom(rom as JObject, null, null);
                        if (item != null)
                            recent.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not load recently added games: {ex.Message}");
                }

                // This week in gaming history, for the carousel (its row comes with the live sections).
                var history = new List<HomeItem>();
                try
                {
                    HistoryDays days = historyAsk.GetAwaiter().GetResult();
                    if (reached && days != null)
                        history.AddRange(days.Today.Concat(days.Week));
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Store] No anniversaries from the server: {ex.Message}");
                }

                var upgraded = new List<HomeItem>();
                try
                {
                    if (reached)
                        upgraded = upgradedAsk.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Store] No compatibility upgrades: {ex.Message}");
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        FillHome(recommended, recent, reached, history, upgraded);
                    }
                    finally
                    {
                        // The page's pictures have all been asked for now; the curtain counts them.
                        CurtainDataReady(reached);
                    }
                }), BuildPriority);
            });
        }

        private void FillHome(List<HomeItem> recommended, List<HomeItem> recent, bool reached, List<HomeItem> history = null, List<HomeItem> upgraded = null)
        {
            foreach (HomeItem item in upgraded ?? new List<HomeItem>())
                _raw[item.Rom.Id] = item.Raw;
            upgraded = ForStore(upgraded ?? new List<HomeItem>());
            // The three newest games on the server (whether in the library or not): the ones
            // marked new, for their first week.
            DateTime freshSince = DateTime.UtcNow.AddDays(-7);
            var freshIds = new HashSet<int>(recent.OrderByDescending(i => i.Rom.CreatedAt).Take(3)
                .Where(i => i.Rom.CreatedAt.ToUniversalTime() > freshSince).Select(i => i.Rom.Id));
            foreach (HomeItem item in history ?? new List<HomeItem>())
                _raw[item.Rom.Id] = item.Raw;
            var historyForStore = ForStore(history ?? new List<HomeItem>());
            // The Store offers what is not in the library yet, playable platforms first.
            foreach (HomeItem item in recommended.Concat(recent))
                _raw[item.Rom.Id] = item.Raw;
            recommended = ForStore(recommended);
            recent = ForStore(recent);

            _homeBody.Children.Clear();
            _queue.Clear();
            _homeShown.Clear();
            _queueDry = false;

            if (!reached)
            {
                var failed = Text("Could not reach the RomM server. Pick Home again to retry.", 14, false);
                failed.Opacity = 0.7;
                _homeBody.Children.Add(failed);
                _homeBuilt = false;
                return;
            }

            foreach (HomeItem item in recommended.Concat(recent))
                _raw[item.Rom.Id] = item.Raw;
            _feedItems = recommended;

            // Featured, up to FeaturedCount (12), only games with both fan art and a logo (the
            // carousel is built on that pairing):
            //   the three newest on the server, in their first week, marked NEW;
            //   three from this week in gaming history, the best rated;
            //   six "because you played", no more than two for any one game played;
            //   then, should those run short, the rest of the feed, its hidden gems (the best
            //   rated) first, then the newest arrivals.
            // Interleaved: the new ones first, then history and "because you played" in turn.
            var used = new HashSet<int>();
            Func<HomeItem, bool> fits = i => Showcase(i) && !used.Contains(i.Rom.Id);
            Func<IEnumerable<HomeItem>, int, List<HomeItem>> take = (from, count) =>
            {
                var got = new List<HomeItem>();
                foreach (HomeItem i in from)
                {
                    if (got.Count >= count)
                        break;
                    if (!fits(i))
                        continue;
                    used.Add(i.Rom.Id);
                    got.Add(i);
                }
                return got;
            };

            List<HomeItem> fresh = take(recent.Where(i => freshIds.Contains(i.Rom.Id)).OrderByDescending(i => i.Rom.CreatedAt), 3);
            foreach (HomeItem i in fresh)
            {
                double days = (DateTime.UtcNow - i.Rom.CreatedAt.ToUniversalTime()).TotalDays;
                i.Fresh = true;
                i.Why = days < 1 ? "New on the server \u00B7 added today" : days < 2 ? "New on the server \u00B7 added yesterday" : $"New on the server \u00B7 added {(int)days} days ago";
            }

            // Newly playing well, for their week, marked as the new ones are and right after them.
            List<HomeItem> better = take(upgraded, 3);

            DateTime today = DateTime.Now.Date;
            List<HomeItem> past = take(historyForStore.OrderByDescending(i => RatingOf(i.Rom)), 3);
            foreach (HomeItem i in past)
            {
                DateTime? released = ReleaseDay(i.Rom);
                i.Why = released.HasValue
                    ? $"This week in gaming history \u00B7 {today.Year - released.Value.Year} years ago ({released.Value:MMM d, yyyy})"
                    : "This week in gaming history";
            }

            var perSeed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var played = new List<HomeItem>();
            foreach (HomeItem i in recommended.Where(r => r.Seed != null))
            {
                if (played.Count >= 6 || !fits(i))
                    continue;
                perSeed.TryGetValue(i.Seed, out int n);
                if (n >= 2)
                    continue;
                perSeed[i.Seed] = n + 1;
                used.Add(i.Rom.Id);
                played.Add(i);
            }

            var showcase = new List<HomeItem>(fresh);
            showcase.AddRange(better);
            int h = 0, p = 0;
            while (h < past.Count || p < played.Count)
            {
                if (h < past.Count)
                    showcase.Add(past[h++]);
                for (int k = 0; k < 2 && p < played.Count; k++)
                    showcase.Add(played[p++]);
            }
            if (showcase.Count < FeaturedCount)
            {
                foreach (HomeItem gem in take(recommended.OrderByDescending(r => RatingOf(r.Rom)), FeaturedCount - showcase.Count))
                {
                    if (string.IsNullOrEmpty(gem.Why))
                        gem.Why = "A hidden gem on your server";
                    showcase.Add(gem);
                }
            }
            if (showcase.Count < FeaturedCount)
                showcase.AddRange(take(recent, FeaturedCount - showcase.Count));
            bool fromFeed = played.Count > 0;
            _featured = showcase.Take(FeaturedCount).ToList();
            if (_featured.Count > 0)
            {
                // The carousel runs the full width of the window, so its glimpses can reach past
                // the page; its heading and everything after it keep to the page's width.
                var head = new StackPanel { MaxWidth = ContentWidth, Margin = new Thickness(PageMargin, 8, PageMargin, 0) };
                head.Children.Add(HomeTitle(fromFeed ? "Featured & Recommended" : "Featured", 0));

                var outer = new StackPanel();
                // Kept to its own height: in the same cell as the banner, a stretched block
                // would grow with the banner, and the banner with it, without end.
                var featuredBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                featuredBlock.Children.Add(head);
                featuredBlock.Children.Add(BuildCarousel());
                _featuredBlock = featuredBlock;

                // Behind the heading and the carousel, the fan art of the game on show, as on
                // the category pages: an oval banner that follows the carousel.
                var featuredHost = new Grid();
                Grid banner = BannerBackdrop(out Action<HomeItem> showBanner);
                featuredHost.Children.Add(banner);
                featuredHost.Children.Add(featuredBlock);
                featuredBlock.SizeChanged += (s, e) =>
                {
                    if (e.HeightChanged && e.NewSize.Height > 0)
                        banner.Height = Math.Round(e.NewSize.Height + 40);
                };
                _homeBanner = showBanner;
                if (_featured.Count > 0)
                    showBanner(_featured[((_featuredIndex % _featured.Count) + _featured.Count) % _featured.Count]);
                outer.Children.Add(featuredHost);
                _homeInset.Child = null;
                outer.Children.Add(_homeBody);
                _homeInset.Child = outer;

                foreach (HomeItem item in _featured)
                    _homeShown.Add(item.Rom.Id);
            }

            // Everything recommended that the carousel did not take, in the feed's order.
            var rest = recommended.Where(i => !_homeShown.Contains(i.Rom.Id)).ToList();

            // What is on today: anniversaries and the game of the day (filled in shortly).
            AddLiveTop();

            // This week in history, the spotlight and series to complete (filled in shortly).
            AddLiveMiddle();

            // New on the server: the newest additions, marked NEW for their first week.
            _liveNew = new StackPanel();
            _homeBody.Children.Add(_liveNew);
            _newThisWeek = 0;
            if (recent.Count > 0 && recommended.Count > 0)
            {
                DateTime weekAgo = DateTime.UtcNow.AddDays(-7);
                _newThisWeek = recent.Count(i => i.Rom.CreatedAt.ToUniversalTime() > weekAgo);
                var newRow = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (HomeItem item in recent.Take(16))
                {
                    double days = (DateTime.UtcNow - item.Rom.CreatedAt.ToUniversalTime()).TotalDays;
                    string added = days < 1 ? "NEW · added today" : days < 2 ? "NEW · added yesterday" : days < 7 ? $"NEW · added {(int)days} days ago" : "Added " + item.Rom.CreatedAt.ToLocalTime().ToString("MMM d");
                    newRow.Children.Add(BuildCapsuleTile(item.Rom, added));
                }
                _liveNew.Children.Add(LiveHeading("New on the Server", _newThisWeek > 0 ? $"{_newThisWeek} added this week" : "The latest additions", null, null));
                _liveNew.Children.Add(SideScroll.Row(newRow));
            }

            // "Because you played ...", below everything special: the (at most three) played
            // games that pulled in the most suggestions.
            foreach (var group in rest.Where(i => i.Seed != null && !_homeShown.Contains(i.Rom.Id)).GroupBy(i => i.Seed).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).Take(3))
            {
                AddHomeRow($"Because You Played {group.Key}", group.ToList(), true);
                foreach (HomeItem item in group)
                    _homeShown.Add(item.Rom.Id);
            }

            // Everything recommended and not yet shown leads the endless feed.
            _queue.AddRange(rest.Where(i => !_homeShown.Contains(i.Rom.Id)));

            _homeBody.Children.Add(HomeTitle("Keep Exploring", 36));
            _queuePanel = new StackPanel();
            _homeBody.Children.Add(_queuePanel);

            _home.ScrollChanged -= OnHomeScrolled;
            _home.ScrollChanged += OnHomeScrolled;
            AppendQueue();
            LoadLive();
        }

        private static bool HasVideo(HomeItem item)
        {
            var ss = item.Rom.SSMetadata;
            return !string.IsNullOrEmpty(ss?.VideoPath) || !string.IsNullOrEmpty(ss?.VideoNormalizedPath);
        }

        // Whether a game has what the carousel needs: fan art and a logo, both kept by the server.
        private static bool Showcase(HomeItem item)
        {
            var ss = item.Rom.SSMetadata;
            return !string.IsNullOrEmpty(ss?.FanartPath) && !string.IsNullOrEmpty(ss?.LogoPath);
        }

                private UIElement HomeTitle(string text, double top)
        {
            var title = Text(text, 17, true);
            title.Margin = new Thickness(0, top, 0, 10);
            return title;
        }

        private void AddHomeRow(string title, List<HomeItem> items, bool showReasons)
        {
            if (items.Count == 0)
                return;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (HomeItem item in items.Take(16))
            {
                // On a "because you played" row the title already says why; show the finer reason.
                string why = showReasons ? item.Detail : null;
                row.Children.Add(BuildCapsuleTile(item.Rom, string.IsNullOrEmpty(why) ? PlatformOf(item) : why));
            }

            _homeBody.Children.Add(HomeTitle(title, 36));
            _homeBody.Children.Add(SideScroll.Row(row));
        }

        private static string PlatformOf(HomeItem item)
        {
            return !string.IsNullOrEmpty(item.Rom.PlatformDisplayName) ? item.Rom.PlatformDisplayName : item.Rom.PlatformName;
        }

        // The widest picture a game has: fan art, else a screenshot, else its cover.
        private string Wide(HomeItem item)
        {
            return FanartPath(item.Rom) ?? MediaPaths(item.Rom).FirstOrDefault() ?? item.Rom.PathCoverL ?? item.Rom.PathCoverS;
        }

        // The carousel's mark for a game new on the server: a ribbon at the picture's top left,
        // gold, with a band of light sweeping across it every few seconds, and (returned in
        // edge) a gold edge round the picture whose glow breathes.
        private static UIElement FreshMark(out Border edge, out TextBlock words)
        {
            var gold = Color.FromRgb(0xF2, 0xC2, 0x4B);
            var ribbonFill = new LinearGradientBrush(Color.FromRgb(0xF7, 0xD4, 0x6A), Color.FromRgb(0xD9, 0x9A, 0x1E), 0);
            var text = new TextBlock
            {
                Text = "\u2605  NEW ON THE SERVER", FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x1D, 0x05)), Margin = new Thickness(12, 5, 14, 5),
            };
            var shine = new System.Windows.Shapes.Rectangle
            {
                Width = 46, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(0xB0, 255, 255, 255), 0.5),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                    },
                },
                RenderTransform = new SkewTransform(-20, 0),
            };
            var sweep = new TranslateTransform(-60, 0);
            shine.RenderTransform = new TransformGroup { Children = { new SkewTransform(-20, 0), sweep } };
            words = text;
            var body = new Grid { ClipToBounds = true };
            body.Children.Add(text);
            body.Children.Add(shine);
            var ribbon = new Border
            {
                Background = ribbonFill, CornerRadius = new CornerRadius(0, 0, 4, 0), Child = body,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = gold, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 },
                Visibility = Visibility.Collapsed,
            };
            // The sweep: across in 0.9 s, then a pause, over and over.
            var run = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            run.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(-60, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
            run.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(260, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.9)))
            { EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } });
            run.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(260, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.2))));
            sweep.BeginAnimation(TranslateTransform.XProperty, run);

            var glow = new System.Windows.Media.Effects.DropShadowEffect { Color = gold, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.5 };
            edge = new Border
            {
                BorderBrush = new SolidColorBrush(gold), BorderThickness = new Thickness(2), IsHitTestVisible = false,
                Effect = glow, Visibility = Visibility.Collapsed,
            };
            glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.25, 0.9, TimeSpan.FromSeconds(1.4))
            {
                AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase(),
            });
            return ribbon;
        }

        // ---- Capsules -------------------------------------------------------------------------

        // Four to a row across the page, as Steam's home page has them.
        private const double CapsuleWidth = 336;
        private const double CapsuleImageHeight = 157;   // Steam's 460 x 215 header, scaled

        /// <summary>
        /// A game on the home page's rows, as Steam's capsules: a wide picture (fan art, else a
        /// screenshot, else the cover) with the logo over it, and under it a strip with up to
        /// three genres and a line of its own (the platform, or why it is there).
        /// </summary>
        private UIElement BuildCapsuleTile(RomMRom rom, string caption)
        {
            var frame = new Grid { Width = CapsuleWidth, Height = CapsuleImageHeight, ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) };
            string wide = FanartPath(rom) ?? MediaPaths(rom).FirstOrDefault();
            var picture = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.Fant);
            ShowPicture(picture, Url(wide ?? rom.PathCoverL ?? rom.PathCoverS), 700);
            frame.Children.Add(picture);
            UIElement logo = wide != null ? LogoOverlay(rom, 1, 3, 500) : null;
            if (logo != null)
                frame.Children.Add(logo);
            else if (wide != null)
            {
                // No logo: the name over a shade at the foot of the picture.
                var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                shade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
                shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0, 0, 0), 1));
                frame.Children.Add(new Border { Background = shade, IsHitTestVisible = false });
                frame.Children.Add(new TextBlock
                {
                    Text = rom.Name ?? rom.FileNameNoExt ?? "", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 10),
                    IsHitTestVisible = false,
                });
            }

            if (_inLibrary.Contains(rom.Id))
            {
                frame.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(6),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "IN LIBRARY", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
                });
            }
            else if (Unavailable(rom))
            {
                var mark = UnavailableTag(10, new Thickness(6, 1, 6, 2), new Thickness(6), MarkOf(rom));
                mark.HorizontalAlignment = HorizontalAlignment.Left;
                mark.VerticalAlignment = VerticalAlignment.Top;
                frame.Children.Add(mark);
            }

            // The strip under the picture: genres as chips, then the line of its own.
            var strip = new StackPanel { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x22, 0x33, 0x48)) };
            var name = Text(rom.Name ?? rom.FileNameNoExt ?? "", 13, true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(10, 8, 10, 4);
            strip.Children.Add(name);
            var chips = new WrapPanel { Margin = new Thickness(8, 0, 8, 0), MaxHeight = 26, ClipToBounds = true };
            foreach (string genre in GenresOf(rom).Where(g => !string.IsNullOrWhiteSpace(g)).Take(3))
            {
                chips.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x50, 0x67, 0xC1, 0xF5)), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(2, 0, 2, 4),
                    Child = new TextBlock { Text = genre, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x67, 0xC1, 0xF5)) },
                });
            }
            if (chips.Children.Count > 0)
                strip.Children.Add(chips);
            var line = Text(caption ?? "", 11, false);
            line.Opacity = 0.65;
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.HorizontalAlignment = HorizontalAlignment.Right;
            line.Margin = new Thickness(10, 2, 10, 8);
            strip.Children.Add(line);

            var stack = new StackPanel { Width = CapsuleWidth };
            stack.Children.Add(frame);
            stack.Children.Add(strip);

            var tile = new Border { Margin = new Thickness(0, 0, 14, 0), Cursor = Cursors.Hand, Child = stack, Opacity = 0.95 };
            tile.MouseEnter += (s, e) =>
            {
                tile.Opacity = 1;
                strip.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x2A, 0x47, 0x5E));
            };
            tile.MouseLeave += (s, e) =>
            {
                tile.Opacity = 0.95;
                strip.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x22, 0x33, 0x48));
            };
            tile.MouseLeftButtonUp += (s, e) => ShowGame(rom);
            AttachHoverCard(tile, rom);
            return tile;
        }

        // ---- Featured carousel ---------------------------------------------------------------

        private UIElement BuildCarousel()
        {
            var stageImage = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(stageImage, BitmapScalingMode.Fant);
            var stage = new Grid { Background = Brushes.Black, ClipToBounds = true, Cursor = Cursors.Hand };
            stage.Children.Add(stageImage);

            // The game's logo over its art; swapped with each game.
            var logoHost = new Grid { IsHitTestVisible = false };
            stage.Children.Add(logoHost);

            // Over both, the screenshot being previewed: faded in over the art and logo while a
            // screenshot beside it is pointed at, and faded away again after.
            var preview = new FadeSlides(double.NaN, 1280, true, 350);
            stage.Children.Add(preview.Stage);

            // New on the server (HomeItem.Fresh): a glowing gold edge around the picture and a
            // "NEW ON THE SERVER" ribbon with a shine running across it.
            UIElement freshMark = FreshMark(out Border freshEdge, out TextBlock freshWords);
            stage.Children.Add(freshEdge);
            stage.Children.Add(freshMark);

            var name = Text("", 20, false);
            var why = Text("", 12, false);
            why.Opacity = 0.75;
            why.Margin = new Thickness(0, 4, 0, 10);
            var thumbs = new UniformGrid2();
            // Moving from one screenshot to the next fades between them (the gaps count as part
            // of the screenshots, so crossing one does not flash the art); leaving them
            // altogether fades back to the art and logo.
            thumbs.Panel.Background = Brushes.Transparent;
            thumbs.Panel.MouseLeave += (s, e) => preview.FadeOut();
            var platform = Text("", 12, false);
            platform.Opacity = 0.6;
            platform.VerticalAlignment = VerticalAlignment.Bottom;
            var owned = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x10)),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = "IN LIBRARY", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            };

            var words = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
            var foot = new Grid();
            foot.Children.Add(platform);
            foot.Children.Add(owned);
            var unavailable = UnavailableTag(10, new Thickness(6, 2, 6, 2), new Thickness(0));
            unavailable.HorizontalAlignment = HorizontalAlignment.Right;
            unavailable.VerticalAlignment = VerticalAlignment.Bottom;
            foot.Children.Add(unavailable);
            DockPanel.SetDock(foot, Dock.Bottom);
            words.Children.Add(foot);
            DockPanel.SetDock(name, Dock.Top);
            words.Children.Add(name);
            DockPanel.SetDock(why, Dock.Top);
            words.Children.Add(why);
            DockPanel.SetDock(thumbs.Panel, Dock.Top);
            words.Children.Add(thumbs.Panel);

            // Under the screenshots: a few lines of the description, then the game's genres.
            var blurb = Text("", 12, false);
            blurb.Opacity = 0.8;
            blurb.LineHeight = 17;
            blurb.MaxHeight = 17 * 5;
            blurb.TextTrimming = TextTrimming.CharacterEllipsis;
            blurb.Margin = new Thickness(0, 8, 0, 8);
            DockPanel.SetDock(blurb, Dock.Top);
            words.Children.Add(blurb);
            var tags = new WrapPanel();
            DockPanel.SetDock(tags, Dock.Top);
            words.Children.Add(tags);

            words.Children.Add(new Border());   // takes the slack

            // The games before and after, glimpsed either side of the one on show, as on Steam:
            // dimmed, a little shorter, and a click away.
            Image peekBackImage, peekForwardImage;
            Grid peekBackLogo, peekForwardLogo;
            Grid peekBack = Peek(true, out peekBackImage, out peekBackLogo);
            Grid peekForward = Peek(false, out peekForwardImage, out peekForwardLogo);
            peekBack.MouseLeftButtonUp += (s, e) => Slide(-1);
            peekForward.MouseLeftButtonUp += (s, e) => Slide(1);


            var side = new Border { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), Child = words, Cursor = Cursors.Hand };
            Grid.SetColumn(side, 1);

            var panel = new Grid { Height = 340 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            panel.Children.Add(stage);
            panel.Children.Add(side);
            panel.SizeChanged += (s, e) =>
            {
                // Steam's stage is 616 x 353 beside a panel of the same height.
                if (e.WidthChanged && e.NewSize.Width > 0)
                    panel.Height = Math.Round(e.NewSize.Width * 0.7 * 353.0 / 616.0);
            };

            panel.SizeChanged += (s, e) =>
            {
                if (panel.ActualHeight > 0)
                    peekBack.Height = peekForward.Height = Math.Round(panel.ActualHeight * 0.86);
            };

            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };

            _showFeatured = () =>
            {
                if (_featured.Count == 0)
                    return;
                _featuredIndex = ((_featuredIndex % _featured.Count) + _featured.Count) % _featured.Count;
                HomeItem item = _featured[_featuredIndex];
                _homeBanner?.Invoke(item);

                string wide = Wide(item);
                ShowPicture(stageImage, Url(wide), 1280);
                preview.Clear();
                logoHost.Children.Clear();
                UIElement logo = LogoOverlay(item.Rom, 1, 2, 900);
                if (logo != null)
                    logoHost.Children.Add(logo);
                name.Text = item.Rom.Name ?? item.Rom.FileNameNoExt ?? "";
                bool marked = item.Fresh || item.Upgraded;
                freshMark.Visibility = freshEdge.Visibility = marked ? Visibility.Visible : Visibility.Collapsed;
                freshWords.Text = item.Upgraded ? "\u2714  NOW PLAYS WELL" : "\u2605  NEW ON THE SERVER";
                if (marked)
                    why.Foreground = Gold;
                else
                    why.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                why.Text = item.Why ?? "";
                why.Visibility = string.IsNullOrEmpty(item.Why) ? Visibility.Collapsed : Visibility.Visible;
                platform.Text = PlatformOf(item) ?? "";
                owned.Visibility = _inLibrary.Contains(item.Rom.Id) ? Visibility.Visible : Visibility.Collapsed;
                string mark = MarkOf(item.Rom);
                unavailable.Visibility = mark != null ? Visibility.Visible : Visibility.Collapsed;
                if (mark != null && unavailable.Child is TextBlock markText)
                    markText.Text = mark;

                // Four screenshots; pointing at one previews it on the stage, as on Steam.
                thumbs.Clear();
                foreach (string path in MediaPaths(item.Rom).Where(p => p != wide).Take(4))
                {
                    var thumb = new Image { Stretch = Stretch.UniformToFill };
                    ShowPicture(thumb, Url(path), 320);
                    var cell = new Border { Background = Brushes.Black, Margin = new Thickness(0, 0, 6, 6), ClipToBounds = true, Child = thumb };
                    string shown = path;
                    cell.MouseEnter += (s, e) => preview.FadeTo(Url(shown));
                    thumbs.Add(cell);
                }

                string summary = ShortText(item.Rom);
                blurb.Text = summary;
                blurb.Visibility = summary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                WhenSteamText(item.Rom, steam =>
                {
                    // Still the game on show: Steam's text in place of RomM's.
                    if (_featured.Count == 0 || _featured[_featuredIndex].Rom.Id != item.Rom.Id)
                        return;
                    string steamSummary = ShortText(item.Rom);
                    blurb.Text = steamSummary;
                    blurb.Visibility = steamSummary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                });
                tags.Children.Clear();
                foreach (string genre in (item.Rom.Metadatum?.Genres ?? item.Rom.Genres ?? new List<string>()).Take(4))
                    tags.Children.Add(GenreChip(genre, new Thickness(8, 3, 8, 3), new Thickness(0, 0, 6, 6)));

                // Neighbours; with fewer than three games there is nothing distinct to glimpse.
                bool glimpse = _featured.Count >= 3;
                peekBack.Visibility = peekForward.Visibility = glimpse ? Visibility.Visible : Visibility.Hidden;
                if (glimpse)
                {
                    ShowPeek(_featured[(_featuredIndex - 1 + _featured.Count) % _featured.Count], peekBackImage, peekBackLogo);
                    ShowPeek(_featured[(_featuredIndex + 1) % _featured.Count], peekForwardImage, peekForwardLogo);
                }

                for (int i = 0; i < dots.Children.Count; i++)
                    ((Border)dots.Children[i]).Opacity = i == _featuredIndex ? 1.0 : 0.35;
            };

            for (int i = 0; i < _featured.Count; i++)
            {
                int index = i;
                var dot = new Border { Width = 16, Height = 8, CornerRadius = new CornerRadius(2), Background = Brushes.White, Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand };
                dot.MouseLeftButtonUp += (s, e) =>
                {
                    int step = index - _featuredIndex;
                    if (step != 0)
                        Slide(step);
                };
                dots.Children.Add(dot);
            }

            Action open = () =>
            {
                if (_featured.Count > 0)
                    ShowGame(_featured[_featuredIndex].Rom);
            };
            stage.MouseLeftButtonUp += (s, e) => open();

            // Resting on the main picture plays the game's video in its place and opens its
            // details card beside the carousel, as Steam does.
            stage.MouseEnter += (s, e) =>
            {
                if (_featured.Count == 0)
                    return;
                HomeItem item = _featured[_featuredIndex];
                PlayVideoSoon(stage, item, () => OpenHoverCard(panel, item.Rom));
            };
            stage.MouseLeave += (s, e) =>
            {
                StopHoverVideo();
                CloseHoverCard();
            };
            side.MouseLeftButtonUp += (s, e) => open();

            // Arrows either side, and a slow turn of its own while nobody is pointing at it.
            // Glimpse, arrow, the game on show, arrow, glimpse.
            var whole = new Grid();
            // The game on show, with its arrows, is exactly as wide as the rest of the page; the
            // glimpses take whatever the window has beyond that, out to its edges.
            var centre = new ColumnDefinition { Width = new GridLength(700) };
            whole.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            whole.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowWidth) });
            whole.ColumnDefinitions.Add(centre);
            whole.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowWidth) });
            whole.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            UIElement back = Arrow("\u2039", () => Slide(-1));
            UIElement forward = Arrow("\u203A", () => Slide(1));
            _sliding = new FrameworkElement[] { peekBack, panel, peekForward };
            Grid.SetColumn(back, 1);
            Grid.SetColumn(panel, 2);
            Grid.SetColumn(forward, 3);
            Grid.SetColumn(peekForward, 4);
            whole.Children.Add(peekBack);
            whole.Children.Add(back);
            whole.Children.Add(panel);
            whole.Children.Add(forward);
            whole.Children.Add(peekForward);
            whole.MouseEnter += (s, e) => _carouselHeld = true;
            whole.MouseLeave += (s, e) => _carouselHeld = false;

            if (_carouselTimer == null)
            {
                _carouselTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                _carouselTimer.Tick += (s, e) =>
                {
                    // Only while it is actually on screen and left alone.
                    if (_carouselHeld || !IsVisible || !_home.IsVisible || _gamePage.IsVisible || _featured.Count < 2)
                        return;
                    // Turning on its own leaves alone any card or video the pointer has open
                    // elsewhere on the page; those belong to other tiles, not the carousel.
                    Slide(1, false);
                };
                _carouselTimer.Start();
            }

            _featuredIndex = 0;
            _showFeatured();

            var holder = new StackPanel();
            holder.SizeChanged += (s, e) =>
            {
                if (!e.WidthChanged || e.NewSize.Width <= 0)
                    return;

                // The page's own width inside its margins, but never so wide that a glimpse
                // has less than MinPeek to show in.
                double page = Math.Min(ContentWidth, e.NewSize.Width) - 2 * PageMargin;
                double room = e.NewSize.Width - 2 * MinPeek;
                centre.Width = new GridLength(Math.Max(320, Math.Min(page, room) - 2 * ArrowWidth));
            };
            holder.Children.Add(whole);
            holder.Children.Add(dots);
            return holder;
        }

        // ---- Video on hover ----------------------------------------------------------------

        private MediaElement _hoverVideo;
        private Panel _hoverVideoHost;
        private DispatcherTimer _hoverVideoDelay;
        private Panel _pendingVideoHost;
        private HomeItem _pendingVideoItem;
        private Action _pendingVideoExtra;

        /// <summary>
        /// After a short rest of the pointer on <paramref name="host"/>, plays the game's video
        /// over it, muted and looping, streamed from the server like every other video here.
        /// <paramref name="alongside"/> runs at the same moment, video or not (the details card).
        /// The pause keeps a sweep across the page from starting a stream per picture.
        /// </summary>
        private void PlayVideoSoon(Panel host, HomeItem item, Action alongside)
        {
            if (_hoverVideoDelay == null)
            {
                _hoverVideoDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _hoverVideoDelay.Tick += (s, e) =>
                {
                    _hoverVideoDelay.Stop();
                    Panel target = _pendingVideoHost;
                    if (target == null || !target.IsMouseOver || !target.IsVisible)
                        return;
                    _pendingVideoExtra?.Invoke();
                    PlayHoverVideo(target, _pendingVideoItem);
                };
            }

            StopHoverVideo();
            _pendingVideoHost = host;
            _pendingVideoItem = item;
            _pendingVideoExtra = alongside;
            _hoverVideoDelay.Start();
        }

        private void PlayHoverVideo(Panel host, HomeItem item)
        {
            if (item == null || !HasVideo(item))
                return;

            try
            {
                var ss = item.Rom.SSMetadata;
                string path = !string.IsNullOrEmpty(ss.VideoPath) ? ss.VideoPath : ss.VideoNormalizedPath;
                string local = _plugin.VideoRelay.LocalUrl($"{Host}/assets/romm/resources/{path}");
                if (local == null)
                    return;

                var player = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Close,
                    IsMuted = true,
                    Stretch = Stretch.UniformToFill,
                    IsHitTestVisible = false,
                    Opacity = 0,
                };
                player.MediaOpened += (s, e) => player.Opacity = 1;   // the picture stays until frames arrive
                player.MediaEnded += (s, e) =>
                {
                    player.Position = TimeSpan.Zero;
                    player.Play();
                };
                player.MediaFailed += (s, e) =>
                {
                    s_log.Warn($"[Store] Could not play the video for {item.Rom.Name}: {e.ErrorException?.Message}");
                    StopHoverVideo();
                };

                host.Children.Add(player);
                _hoverVideo = player;
                _hoverVideoHost = host;
                player.Source = new Uri(local, UriKind.Absolute);
                player.Play();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not start the video for {item.Rom.Name}: {ex.Message}");
                StopHoverVideo();
            }
        }

        private void StopHoverVideo()
        {
            _hoverVideoDelay?.Stop();
            _pendingVideoHost = null;

            MediaElement player = _hoverVideo;
            Panel host = _hoverVideoHost;
            _hoverVideo = null;
            _hoverVideoHost = null;
            if (player == null)
                return;

            try
            {
                player.Stop();
                player.Close();
                player.Source = null;
                host?.Children.Remove(player);
            }
            catch
            {
                // Nothing useful to do if the player refuses to close.
            }
        }

        // The parts of the carousel that move when it turns.
        private FrameworkElement[] _sliding = new FrameworkElement[0];

        /// <summary>
        /// Turns the carousel by <paramref name="step"/> games, with the new games sliding in
        /// from the side they come from, as Steam's does. <paramref name="byHand"/> is false when
        /// it turns by itself, which never happens while the pointer is on it, so then the hover
        /// card and video (which are on some other tile) are left as they are.
        /// </summary>
        private void Slide(int step, bool byHand = true)
        {
            if (_showFeatured == null || _featured.Count == 0)
                return;

            if (byHand)
            {
                StopHoverVideo();
                CloseHoverCard();
            }
            _featuredIndex += step;
            _showFeatured();

            double from = Math.Sign(step) * 90;
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(380);
            foreach (FrameworkElement part in _sliding)
            {
                var shift = part.RenderTransform as TranslateTransform;
                if (shift == null)
                    part.RenderTransform = shift = new TranslateTransform();
                shift.BeginAnimation(TranslateTransform.XProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(from, 0, duration) { EasingFunction = ease });

                // The panel also fades up as it arrives; the glimpses keep their own fading.
                if (ReferenceEquals(part, _sliding.Length > 1 ? _sliding[1] : null))
                    part.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, duration) { EasingFunction = ease });
            }
        }

        // One of the two glimpses beside the carousel: a picture and a place for its logo. It
        // fades away towards the window's edge, from three-quarters strength beside the game on
        // show to nothing at the outside.
        private Grid Peek(bool onLeft, out Image image, out Grid logoHost)
        {
            image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
            logoHost = new Grid { IsHitTestVisible = false };

            var fade = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(onLeft ? (byte)0x00 : (byte)0xBF, 0, 0, 0), 0));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(onLeft ? (byte)0xBF : (byte)0x00, 0, 0, 0), 1));

            var peek = new Grid { Background = Brushes.Black, ClipToBounds = true, Opacity = 0.8, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Height = 280, OpacityMask = fade };
            peek.Children.Add(image);
            peek.Children.Add(logoHost);
            peek.MouseEnter += (s, e) => peek.Opacity = 1;
            peek.MouseLeave += (s, e) => peek.Opacity = 0.8;
            return peek;
        }

        private void ShowPeek(HomeItem item, Image image, Grid logoHost)
        {
            ShowPicture(image, Url(Wide(item)), 640);
            logoHost.Children.Clear();
            UIElement logo = LogoOverlay(item.Rom, 1, 6, 500);
            if (logo != null)
                logoHost.Children.Add(logo);
        }

        private UIElement Arrow(string glyph, Action click)
        {
            var text = Text(glyph, 40, false);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            var arrow = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Opacity = 0.6, Child = text };
            arrow.MouseEnter += (s, e) => arrow.Opacity = 1;
            arrow.MouseLeave += (s, e) => arrow.Opacity = 0.6;
            arrow.MouseLeftButtonUp += (s, e) => { click(); e.Handled = true; };
            return arrow;
        }

        // A two-by-two grid of equal cells that keeps Steam's thumbnail proportions.
        private sealed class UniformGrid2
        {
            public readonly Grid Panel = new Grid();
            private int _count;

            public UniformGrid2()
            {
                Panel.ColumnDefinitions.Add(new ColumnDefinition());
                Panel.ColumnDefinitions.Add(new ColumnDefinition());
                Panel.RowDefinitions.Add(new RowDefinition());
                Panel.RowDefinitions.Add(new RowDefinition());
                Panel.SizeChanged += (s, e) =>
                {
                    if (e.WidthChanged && e.NewSize.Width > 0)
                        Panel.Height = Math.Round(e.NewSize.Width * 9.0 / 16.0);
                };
            }

            public void Clear()
            {
                Panel.Children.Clear();
                _count = 0;
            }

            public void Add(UIElement cell)
            {
                if (_count >= 4)
                    return;
                Grid.SetColumn(cell, _count % 2);
                Grid.SetRow(cell, _count / 2);
                Panel.Children.Add(cell);
                _count++;
            }
        }

        // ---- The endless feed ----------------------------------------------------------------

        private void OnHomeScrolled(object sender, ScrollChangedEventArgs e)
        {
            // Within a screen or so of the end: bring in the next few.
            if (_home.VerticalOffset + _home.ViewportHeight >= _home.ExtentHeight - 900)
                AppendQueue();
        }

        private void AppendQueue()
        {
            if (_queueLoading || _queueDry || _queuePanel == null)
                return;

            var batch = new List<HomeItem>();
            while (batch.Count < QueueBatch && _queue.Count > 0)
            {
                HomeItem next = _queue[0];
                _queue.RemoveAt(0);
                if (_homeShown.Add(next.Rom.Id))
                    batch.Add(next);
            }

            if (batch.Count == QueueBatch)
            {
                foreach (HomeItem item in batch)
                    _queuePanel.Children.Add(BuildQueueCard(item));
                return;
            }

            // The recommendations have run out: top up with random picks from the server.
            _queueLoading = true;
            int wanted = QueueBatch - batch.Count;
            var seen = new HashSet<int>(_homeShown);
            Task.Run(() =>
            {
                var picks = new List<HomeItem>();
                int misses = 0;
                while (picks.Count < wanted && misses < 6)
                {
                    try
                    {
                        HomeItem item = ItemFrom(Get("/api/roms/random") as JObject, null, null);
                        if (item != null && seen.Add(item.Rom.Id) && !_inLibrary.Contains(item.Rom.Id))
                            picks.Add(item);
                        else
                            misses++;
                    }
                    catch (Exception ex)
                    {
                        misses++;
                        s_log.Debug($"[Store] Random pick failed: {ex.Message}");
                    }
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (HomeItem item in batch.Concat(picks))
                    {
                        _homeShown.Add(item.Rom.Id);
                        _raw[item.Rom.Id] = item.Raw;
                        _queuePanel.Children.Add(BuildQueueCard(item));
                    }

                    // Nothing new after several tries: the library has been shown through.
                    if (picks.Count == 0)
                        _queueDry = true;
                    _queueLoading = false;
                }));
            });
        }

        // A large card, as in Steam's discovery feed: name and reason over a wide picture
        // beside four screenshots.
        private UIElement BuildQueueCard(HomeItem item)
        {
            var name = Text(item.Rom.Name ?? item.Rom.FileNameNoExt ?? "", 20, true);
            string reason = !string.IsNullOrEmpty(item.Why) ? item.Why : PlatformOf(item);
            var why = Text(reason ?? "", 13, false);
            why.Opacity = 0.7;
            why.Margin = new Thickness(0, 2, 0, 12);

            var view = Text("View Page", 12, false);
            var viewButton = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                Padding = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = view,
            };

            var head = new Grid();
            var titles = new StackPanel();
            titles.Children.Add(name);
            titles.Children.Add(why);
            head.Children.Add(titles);
            head.Children.Add(viewButton);

            var media = MediaPaths(item.Rom);
            var big = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(big, BitmapScalingMode.Fant);
            ShowPicture(big, Url(Wide(item)), 1000);
            var bigHolder = new Grid();
            bigHolder.Children.Add(big);
            UIElement bigLogo = LogoOverlay(item.Rom, 1, 2, 700);
            if (bigLogo != null)
                bigHolder.Children.Add(bigLogo);
            var bigFrame = new Border { Background = Brushes.Black, ClipToBounds = true, Child = bigHolder, Margin = new Thickness(0, 0, 10, 0) };

            // The game's video plays over the picture while the pointer rests on it. (No details
            // card here: the card itself already shows them.)
            bigHolder.Background = Brushes.Transparent;
            bigHolder.MouseEnter += (s, e) => PlayVideoSoon(bigHolder, item, null);
            bigHolder.MouseLeave += (s, e) => StopHoverVideo();

            var shots = new UniformGrid2();
            string wideShot = Wide(item);
            foreach (string path in media.Where(p => p != wideShot).Take(4))
            {
                var shot = new Image { Stretch = Stretch.UniformToFill };
                ShowPicture(shot, Url(path), 400);
                shots.Add(new Border { Background = Brushes.Black, Margin = new Thickness(0, 0, 6, 6), ClipToBounds = true, Child = shot });
            }
            Grid.SetColumn(shots.Panel, 1);
            shots.Panel.VerticalAlignment = VerticalAlignment.Top;

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10, GridUnitType.Star) });
            body.Children.Add(bigFrame);
            body.Children.Add(shots.Panel);
            body.SizeChanged += (s, e) =>
            {
                // The wide picture is as tall as the block of four beside it.
                if (e.WidthChanged && e.NewSize.Width > 0)
                    bigFrame.Height = Math.Round(e.NewSize.Width * 10.0 / 21.0 * 9.0 / 16.0);
            };

            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(body);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x48, 0, 0, 0)),
                Padding = new Thickness(18, 14, 18, 16),
                Margin = new Thickness(0, 0, 0, 22),
                Cursor = Cursors.Hand,
                Child = stack,
            };
            card.MouseEnter += (s, e) => card.Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
            card.MouseLeave += (s, e) => card.Background = new SolidColorBrush(Color.FromArgb(0x48, 0, 0, 0));
            card.MouseLeftButtonUp += (s, e) => ShowGame(item.Rom);
            return card;
        }
    }
}
