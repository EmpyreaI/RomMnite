using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using RomM.AchievementViews;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RomM.Games
{
    /// <summary>
    /// The custom element RomM_NewsFeed: what a game's library page shows in its Overview in
    /// place of the description, as Steam's library shows a game's activity.
    ///
    ///   YOUR ACTIVITY  the player's own history with the game, newest first: days played (from
    ///                  PlaySessions, the last two months), achievements unlocked (grouped by
    ///                  day, with their badges), the last save sync with RomM, the last time
    ///                  played (when older than the sessions kept), and when it was added; with a
    ///                  line of totals above (last two weeks, total time, achievements).
    ///   RECENT NEWS    a PC game's Steam announcements (SteamNews): the newest as a large
    ///                  picture card, the rest as a grid of picture cards (the banner Steam
    ///                  shows each post with); a post with no picture is a card of words alone.
    ///                  Each opens on Steam.
    ///
    /// Shown for every game (Windows games and others alike): CloudStatus.NewsFeedVisible is on
    /// whenever a game is selected, so the theme shows neither description nor box art.
    /// </summary>
    public class OverviewFeedControl : PluginUserControl
    {
        private const int MaxPosts = 7;
        private const int MaxActivity = 8;
        private const string PlayGlyph = "";
        private const string StarGlyph = "";
        private const string CloudGlyph = "";
        private const string AddGlyph = "";
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Brush CardBrush = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        private static readonly Brush CardHoverBrush = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        private static readonly Brush DarkFrame = Frozen(Color.FromArgb(0x50, 0, 0, 0));
        private static readonly Brush Gold = Frozen(Color.FromRgb(0xE5, 0xB1, 0x43));
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x66, 0xC0, 0xF4));

        private readonly RomM _plugin;
        private readonly StackPanel _root = new StackPanel();
        private readonly StackPanel _activity = new StackPanel();
        private readonly StackPanel _news = new StackPanel();
        private Guid _gameId;

        public OverviewFeedControl(RomM plugin)
        {
            _plugin = plugin;
            _root.Children.Add(_activity);
            _root.Children.Add(_news);
            Content = _root;
            Focusable = false;

            // The activity follows the game as it changes while shown: played (its time, its
            // session), achievements fetched, saves synced. Redrawn once things settle.
            _redraw.Tick += (s, e) =>
            {
                _redraw.Stop();
                RedrawActivity();
            };
            Loaded += (s, e) => Listen(true);
            Unloaded += (s, e) => Listen(false);
        }

        private readonly System.Windows.Threading.DispatcherTimer _redraw =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private bool _listening;

        private void Listen(bool on)
        {
            if (on == _listening)
                return;
            _listening = on;
            if (on)
            {
                _plugin.Playnite.Database.Games.ItemUpdated += OnGamesUpdated;
                if (_plugin.Sessions != null)
                    _plugin.Sessions.Changed += OnGameChanged;
                if (_plugin.AchievementStore != null)
                    _plugin.AchievementStore.Changed += OnGameChanged;
                if (_plugin.SaveSync != null)
                    _plugin.SaveSync.StatusChanged += OnGameChanged;
            }
            else
            {
                _plugin.Playnite.Database.Games.ItemUpdated -= OnGamesUpdated;
                if (_plugin.Sessions != null)
                    _plugin.Sessions.Changed -= OnGameChanged;
                if (_plugin.AchievementStore != null)
                    _plugin.AchievementStore.Changed -= OnGameChanged;
                if (_plugin.SaveSync != null)
                    _plugin.SaveSync.StatusChanged -= OnGameChanged;
            }
        }

        private void OnGamesUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            if (e?.UpdatedItems != null && e.UpdatedItems.Any(u => u.NewData?.Id == _gameId))
                OnGameChanged(_gameId);
        }

        // From any thread: the shown game's activity redrawn soon (several changes, once).
        private void OnGameChanged(Guid gameId)
        {
            if (gameId == Guid.Empty || gameId != _gameId)
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _redraw.Stop();
                _redraw.Start();
            }));
        }

        private void RedrawActivity()
        {
            Game game = _gameId == Guid.Empty ? null : _plugin.Playnite.Database.Games.Get(_gameId);
            if (game == null)
                return;
            _activity.Children.Clear();
            try
            {
                ShowActivity(game);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Overview] Could not redraw the activity of {game.Name}: {ex.Message}");
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _gameId = newContext?.Id ?? Guid.Empty;
            _activity.Children.Clear();
            _news.Children.Clear();
            _plugin.CloudStatus.NewsFeedVisible = false;
            if (newContext == null)
                return;
            try
            {
                ShowActivity(newContext);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Overview] Could not show the activity of {newContext.Name}: {ex.Message}");
            }
            UpdateVisible();
            if (IsPc(newContext) && _plugin.News != null)
                LoadNews(newContext);
        }

        // The description gives way whenever a game is shown: the feed always has its activity.
        private void UpdateVisible() =>
            _plugin.CloudStatus.NewsFeedVisible = _gameId != Guid.Empty;

        // PC games only: Playnite's PC platforms (Windows, or one named PC).
        private static bool IsPc(Game game) =>
            game.Platforms?.Any(p => p != null && (string.Equals(p.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase)
                || (p.Name ?? "").IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                || (p.Name ?? "").StartsWith("PC", StringComparison.OrdinalIgnoreCase))) == true;

        // ---- Your activity -------------------------------------------------------------------

        private sealed class Entry
        {
            public DateTime WhenUtc;
            public string Glyph;
            public Brush GlyphBrush;
            public string Text;
            public string Detail;
            public List<AchievementItem> Badges;
        }

        private void ShowActivity(Game game)
        {
            var entries = new List<Entry>();

            // Days played, from the sessions kept: one line a day.
            List<PlaySessions.Session> sessions = _plugin.Sessions?.Of(game.Id) ?? new List<PlaySessions.Session>();
            foreach (var day in sessions.GroupBy(s => s.EndUtc.ToLocalTime().Date))
            {
                ulong seconds = 0;
                foreach (PlaySessions.Session session in day)
                    seconds += session.Seconds;
                int count = day.Count();
                entries.Add(new Entry
                {
                    WhenUtc = day.Max(s => s.EndUtc),
                    Glyph = PlayGlyph,
                    GlyphBrush = Accent,
                    Text = "Played for " + Duration(seconds),
                    Detail = count > 1 ? count + " sessions" : null,
                });
            }

            // Played before the sessions kept begin: the last time, as Playnite has it.
            DateTime? lastPlayed = game.LastActivity?.ToUniversalTime();
            if (lastPlayed.HasValue && (sessions.Count == 0 || lastPlayed.Value < sessions[0].EndUtc.AddMinutes(-5)))
                entries.Add(new Entry { WhenUtc = lastPlayed.Value, Glyph = PlayGlyph, GlyphBrush = Accent, Text = "Last played" });

            // Achievements, grouped by the day they were unlocked.
            GameAchievements achievements = _plugin.AchievementStore?.Get(game.Id);
            if (achievements?.Items != null)
            {
                foreach (var day in achievements.Items.Where(i => i.When.HasValue).GroupBy(i => i.When.Value.ToLocalTime().Date))
                {
                    var items = day.OrderByDescending(i => i.When).ToList();
                    entries.Add(new Entry
                    {
                        WhenUtc = items[0].When.Value,
                        Glyph = StarGlyph,
                        GlyphBrush = Gold,
                        Text = items.Count == 1 ? "Unlocked " + items[0].Title : $"Unlocked {items.Count} achievements",
                        Detail = items.Count == 1 ? items[0].Description : null,
                        Badges = items,
                    });
                }
            }

            DateTime? synced = _plugin.SaveSync?.LastSyncedUtc(game.Id);
            if (synced.HasValue)
                entries.Add(new Entry { WhenUtc = synced.Value, Glyph = CloudGlyph, GlyphBrush = Accent, Text = "Saves synced with RomM" });

            if (game.Added.HasValue)
                entries.Add(new Entry { WhenUtc = game.Added.Value.ToUniversalTime(), Glyph = AddGlyph, GlyphBrush = Accent, Text = "Added to your library" });

            // Shown for every game, even one with nothing yet (the library shows no description).
            // Laid out as the page's Details panel beside it: a title, then a card of rows, each
            // an icon and a muted label (here the day) with its value; the totals first, then a
            // separator, then what happened, newest first.
            _activity.Children.Add(SectionTitle("Activity"));
            var card = new StackPanel();
            UIElement totals = Totals(game, achievements);
            var history = new StackPanel();
            foreach (Entry entry in entries.OrderByDescending(e => e.WhenUtc).Take(MaxActivity))
                history.Children.Add(Row(entry));
            if (entries.All(e => e.Glyph == AddGlyph))
            {
                TextBlock hint = Muted("Nothing yet. Your play sessions, achievements and save syncs for " + (game.Name ?? "this game") + " will show here.");
                hint.TextWrapping = TextWrapping.Wrap;
                hint.Margin = new Thickness(0, 6, 0, 4);
                history.Children.Add(hint);
            }
            if (totals != null)
            {
                card.Children.Add(totals);
                card.Children.Add(Separated(history));
            }
            else
            {
                card.Children.Add(history);
            }
            _activity.Children.Add(Card(card, new Thickness(0, 12, 0, 24)));
        }

        // The page's card: the Details panel's own style (its background and corners).
        private static Border Card(UIElement child, Thickness margin)
        {
            var card = new Border { Padding = new Thickness(16, 12, 16, 8), Margin = margin, Child = child };
            card.SetResourceReference(StyleProperty, "DetailViewCardBorder");
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            return card;
        }

        // A thin line above, as between the Details panel's groups.
        private static Border Separated(UIElement child)
        {
            var group = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), Child = child };
            group.SetResourceReference(Border.BorderBrushProperty, "PanelSeparatorBrush");
            return group;
        }

        // Last two weeks, Total, Achievements: rows as the Details panel's.
        private UIElement Totals(Game game, GameAchievements achievements)
        {
            var rows = new StackPanel();
            ulong recent = _plugin.Sessions?.PlayedWithin(game.Id, 14) ?? 0;
            if (recent > 0)
                rows.Children.Add(FactRow("\uE823", "Last two weeks", Value(Duration(recent))));
            if (game.Playtime > 0)
                rows.Children.Add(FactRow("\uE916", "Total played", Value(Duration(game.Playtime))));
            if (achievements != null && achievements.Total > 0)
                rows.Children.Add(FactRow(StarGlyph, "Achievements", Value($"{achievements.Unlocked} of {achievements.Total}")));
            return rows.Children.Count == 0 ? null : rows;
        }

        private UIElement Row(Entry entry)
        {
            var words = new StackPanel();
            words.Children.Add(Value(entry.Text));
            if (!string.IsNullOrEmpty(entry.Detail))
            {
                TextBlock detail = Muted(entry.Detail);
                detail.TextWrapping = TextWrapping.Wrap;
                detail.Margin = new Thickness(0, -2, 0, 4);
                words.Children.Add(detail);
            }

            UIElement content = words;
            if (entry.Badges != null && entry.Badges.Count > 1)
            {
                // Several on one day: their badges in a row under the words.
                var badges = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
                foreach (AchievementItem item in entry.Badges.Take(10))
                    badges.Children.Add(Badge(item, new Thickness(0, 0, 6, 6)));
                if (entry.Badges.Count > 10)
                {
                    TextBlock more = Muted("+" + (entry.Badges.Count - 10));
                    more.VerticalAlignment = VerticalAlignment.Center;
                    badges.Children.Add(more);
                }
                words.Children.Add(badges);
            }
            else if (entry.Badges != null && entry.Badges.Count == 1 && !string.IsNullOrEmpty(entry.Badges[0].BadgeUrl))
            {
                // One: its badge beside the words.
                var beside = new DockPanel();
                Image badge = Badge(entry.Badges[0], new Thickness(0, 4, 10, 4));
                badge.Width = badge.Height = 32;
                DockPanel.SetDock(badge, Dock.Left);
                beside.Children.Add(badge);
                beside.Children.Add(words);
                content = beside;
            }
            return FactRow(entry.Glyph, Day(entry.WhenUtc), content);
        }

        // One row as the Details panel's: an icon, a muted label 130 wide, then the value.
        private static UIElement FactRow(string glyph, string label, UIElement value)
        {
            var row = new DockPanel { LastChildFill = true };
            var icon = new TextBlock { Text = glyph };
            icon.SetResourceReference(StyleProperty, "GridIcon");
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var name = new TextBlock { Text = label, Width = 130, Margin = new Thickness(0, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
            name.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
            name.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            row.Children.Add(value);
            return row;
        }

        private static TextBlock Value(string text)
        {
            var value = new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap };
            value.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
            return value;
        }

        private static TextBlock Muted(string text)
        {
            var muted = new TextBlock { Text = text };
            muted.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
            muted.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
            return muted;
        }

        private static Image Badge(AchievementItem item, Thickness margin)
        {
            var badge = new Image
            {
                Width = 40,
                Height = 40,
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = item.Title + (string.IsNullOrEmpty(item.Description) ? "" : "\n" + item.Description),
            };
            RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.Fant);
            Store.StoreImages.Show(badge, item.BadgeUrl, 80);
            return badge;
        }

        private static string Day(DateTime utc)
        {
            DateTime local = utc.ToLocalTime().Date;
            DateTime today = DateTime.Now.Date;
            if (local == today)
                return "Today";
            if (local == today.AddDays(-1))
                return "Yesterday";
            return local.ToString(local.Year == today.Year ? "d MMM" : "d MMM yyyy");
        }

        private static string Duration(ulong seconds)
        {
            ulong minutes = seconds / 60;
            if (minutes < 1)
                return "under a minute";
            if (minutes < 60)
                return minutes + " min";
            ulong hours = minutes / 60, rest = minutes % 60;
            return rest == 0 || hours >= 10 ? hours + " h" : $"{hours} h {rest} min";
        }

        // ---- Recent news ---------------------------------------------------------------------

        private async void LoadNews(Game game)
        {
            Guid asked = game.Id;
            try
            {
                int? appId = await _plugin.SteamAppIdAsync(game);
                if (appId == null || appId <= 0 || asked != _gameId)
                    return;
                List<SteamNewsItem> posts = await _plugin.News.GetAsync(appId.Value);
                if (asked != _gameId || posts == null || posts.Count == 0)
                    return;
                ShowNews(game, posts.OrderByDescending(p => p.PostedUtc).Take(MaxPosts).ToList());
                UpdateVisible();
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Overview] Could not show the news of {game.Name}: {ex.Message}");
            }
        }

        // On the UI thread (the awaits above come back to it).
        private void ShowNews(Game game, List<SteamNewsItem> posts)
        {
            _news.Children.Clear();
            _news.Children.Add(SectionTitle("News"));
            _news.Children.Add(Featured(posts[0]));
            if (posts.Count == 1)
                return;

            // The rest in a grid, as many columns as fit at about 300 pixels each (up to three).
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-6, 6, -6, 0) };
            foreach (SteamNewsItem post in posts.Skip(1))
                grid.Children.Add(Tile(post));
            grid.SizeChanged += (s, e) =>
            {
                int columns = Math.Max(1, Math.Min(3, (int)(e.NewSize.Width / 300)));
                if (grid.Columns != columns)
                    grid.Columns = columns;
            };
            _news.Children.Add(grid);
        }

        // The newest post: its picture across the width, the words over its lower part.
        private UIElement Featured(SteamNewsItem post)
        {
            bool pictured = !string.IsNullOrEmpty(post.Image);
            var stage = new Grid { Height = pictured ? 300 : double.NaN, MinHeight = 120, ClipToBounds = true, Background = DarkFrame };
            if (!pictured)
                stage.SetResourceReference(Panel.BackgroundProperty, "CardBackgroundBrush");
            if (pictured)
                stage.Children.Add(Picture(post.Image, 1280));
            stage.Children.Add(new Border
            {
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.35),
                        new GradientStop(Color.FromArgb(0xE6, 0x0B, 0x0E, 0x14), 1.0),
                    },
                    new Point(0, 0), new Point(0, 1)),
            });

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20, 0, 20, 18) };
            words.Children.Add(Meta(post, Brushes.White));
            words.Children.Add(new TextBlock
            {
                Text = post.Title ?? "",
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 6),
            });
            if (!string.IsNullOrEmpty(post.Summary))
            {
                words.Children.Add(new TextBlock
                {
                    Text = post.Summary,
                    FontSize = 13,
                    Foreground = Brushes.White,
                    Opacity = 0.85,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 2 * 19,
                    LineHeight = 19,
                    TextTrimming = TextTrimming.WordEllipsis,
                });
            }
            stage.Children.Add(words);

            var card = new Border { Margin = new Thickness(0, 12, 0, 6), ClipToBounds = true, Child = stage };
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            Clickable(card, post, null);
            return card;
        }

        // One of the other posts: picture on top, words below.
        private UIElement Tile(SteamNewsItem post)
        {
            var frame = new Grid { Height = 160, ClipToBounds = true, Background = DarkFrame };
            if (!string.IsNullOrEmpty(post.Image))
                frame.Children.Add(Picture(post.Image, 640));
            else
                frame.Visibility = Visibility.Collapsed;

            var words = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
            words.Children.Add(Meta(post, null));
            var title = Line(post.Title ?? "", 15, FontWeights.SemiBold, 1.0);
            title.Margin = new Thickness(0, 4, 0, 4);
            title.MaxHeight = 2 * 21;
            title.LineHeight = 21;
            title.TextTrimming = TextTrimming.WordEllipsis;
            words.Children.Add(title);
            if (!string.IsNullOrEmpty(post.Summary))
            {
                var summary = Line(post.Summary, 12, FontWeights.Normal, 0.75);
                summary.MaxHeight = 3 * 17;
                summary.LineHeight = 17;
                summary.TextTrimming = TextTrimming.WordEllipsis;
                words.Children.Add(summary);
            }

            var stack = new StackPanel();
            stack.Children.Add(frame);
            stack.Children.Add(words);
            var card = new Border { Margin = new Thickness(6), ClipToBounds = true, Child = stack };
            card.SetResourceReference(StyleProperty, "DetailViewCardBorder");
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            Clickable(card, post, null);
            return card;
        }

        private static TextBlock Meta(SteamNewsItem post, Brush foreground)
        {
            string feed = string.IsNullOrEmpty(post.Feed) || post.Feed.IndexOf("announcement", StringComparison.OrdinalIgnoreCase) >= 0
                ? "NEWS" : post.Feed.ToUpperInvariant();
            var meta = Line(feed + "  ·  " + Day(post.PostedUtc), 11, FontWeights.SemiBold, 0.75);
            if (foreground != null)
                meta.Foreground = foreground;
            return meta;
        }

        // The post's picture, filling its frame.
        private static Image Picture(string url, int decodeWidth)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
            Store.StoreImages.Show(image, url, decodeWidth);
            return image;
        }

        private static void Clickable(Border card, SteamNewsItem post, Brush idle)
        {
            if (string.IsNullOrEmpty(post.Url))
                return;
            card.Cursor = Cursors.Hand;
            card.ToolTip = "Open on Steam";
            card.MouseEnter += (s, e) =>
            {
                if (idle != null)
                    card.Background = CardHoverBrush;
                else
                    card.Opacity = 0.92;
            };
            card.MouseLeave += (s, e) =>
            {
                if (idle != null)
                    card.Background = idle;
                else
                    card.Opacity = 1;
            };
            card.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                try
                {
                    System.Diagnostics.Process.Start(post.Url);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Overview] Could not open {post.Url}: {ex.Message}");
                }
            };
        }

        // ---- Shared ------------------------------------------------------------------------

        private static TextBlock SectionTitle(string text)
        {
            var title = new TextBlock { Text = text, Margin = new Thickness(0) };
            title.SetResourceReference(StyleProperty, "DetailViewSectionTitleTextBlockStyle");
            return title;
        }

        private static TextBlock Line(string text, double size, FontWeight weight, double opacity)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Opacity = opacity, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
