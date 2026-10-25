using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using RomM.Store;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RomM.AchievementViews
{
    /// <summary>The achievement displays a theme can place, one class drawing each kind.</summary>
    public enum AchievementView
    {
        Button,
        ProgressBar,
        CompactList,
        CompactUnlocked,
        CompactLocked,
        UserStats,
        List,
        Chart,
    }

    /// <summary>
    /// The plugin's own achievement displays, in place of SuccessStory's. A theme places them
    /// with &lt;ContentControl x:Name="RomM_Achievements&lt;Kind&gt;" /&gt;, the same kinds and
    /// places SuccessStory's elements had (RomM_AchievementsList for SuccessStory_PluginList, and
    /// so on). Each shows the game it is placed for, and redraws when that game's achievements
    /// are refreshed. With no achievements for the game it hides itself.
    /// </summary>
    public class AchievementControl : PluginUserControl
    {
        private const double CompactBadge = 48;
        private const double CompactGap = 6;

        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));

        private readonly RomM _plugin;
        private readonly AchievementView _kind;
        private Guid _gameId = Guid.Empty;
        private GameAchievements _data;
        private bool _guarded;
        private bool _cannotEarn;

        public AchievementControl(RomM plugin, AchievementView kind)
        {
            _plugin = plugin;
            _kind = kind;
            Focusable = false;
            Visibility = Visibility.Collapsed;

            _plugin.AchievementStore.Changed += id =>
            {
                if (id == _gameId)
                    Dispatcher.BeginInvoke(new Action(() => Show(_plugin.AchievementStore.Get(_gameId))));
            };

            // Another account's achievements, or a RetroAchievements sign-in made or undone: redrawn.
            _plugin.AchievementStore.Reset += () => Dispatcher.BeginInvoke(new Action(() => Show(_plugin.AchievementStore.Get(_gameId))));
            _plugin.RetroAchievements.Changed += () => Dispatcher.BeginInvoke(new Action(() => Show(_plugin.AchievementStore.Get(_gameId))));

            // The unlocked and locked rows can be switched off in the settings.
            if (_kind == AchievementView.CompactLocked || _kind == AchievementView.CompactUnlocked)
            {
                _plugin.CloudStatus.Achievements.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(AchievementsSummary.EnableIntegrationCompactLocked)
                        || e.PropertyName == nameof(AchievementsSummary.EnableIntegrationCompactUnlocked))
                        Dispatcher.BeginInvoke(new Action(() => Show(_plugin.AchievementStore.Get(_gameId))));
                };
            }

            // The compact rows fit as many badges as their width allows.
            if (_kind == AchievementView.CompactList || _kind == AchievementView.CompactLocked || _kind == AchievementView.CompactUnlocked)
            {
                SizeChanged += (s, e) =>
                {
                    if (e.WidthChanged && _data != null && !_guarded)
                        Render();
                };
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _gameId = newContext?.Id ?? Guid.Empty;
            Show(newContext != null ? _plugin.AchievementStore.Get(newContext.Id) : null);
        }

        private void Show(GameAchievements data)
        {
            _data = data != null && data.Total > 0 && Enabled() ? data : null;

            // The spoiler guard: until the account is signed in to RetroAchievements, its sets are
            // blurred behind a prompt to sign in; the button and bar are not shown. Sets read
            // from the emulators (trophies, Xbox achievements) need no sign-in.
            _guarded = _data != null && _data.Source == null && !_plugin.RetroAchievements.SignedIn;
            // A Windows game's copy that cannot earn them: said once above the list (the first view
            // of each place: the achievements tab's compact list, the overview's unlocked ones),
            // the list itself shown as usual.
            _cannotEarn = _data != null && !string.IsNullOrEmpty(_data.Unavailable)
                && (_kind == AchievementView.CompactList || _kind == AchievementView.CompactUnlocked);
            if (_guarded && (_kind == AchievementView.Button || _kind == AchievementView.ProgressBar))
            {
                Visibility = Visibility.Collapsed;
                Content = null;
                return;
            }

            // None yet because the emulator keeps them itself and the game has not been played in
            // it: said once, where the unlocked ones go, the way the sign-in prompt stands in for
            // RetroAchievements sets.
            if (_data == null && _kind == AchievementView.CompactUnlocked && Enabled() && _gameId != Guid.Empty
                && (data == null || data.Total == 0))
            {
                Game game = _plugin.Playnite.Database.Games.Get(_gameId);
                if (game != null && _plugin.EmulatorAchievements.NeedsFirstPlay(game, out string emulatorName))
                {
                    Visibility = Visibility.Visible;
                    Content = BuildFirstPlay(game.Name, emulatorName);
                    return;
                }
            }

            Visibility = _data == null ? Visibility.Collapsed : Visibility.Visible;
            if (_data == null)
                Content = null;
            else if (_guarded)
                Content = BuildGuard();
            else
                Render();
        }

        private FrameworkElement BuildGuard()
        {
            var grid = new Grid { MinHeight = 140, ClipToBounds = true };

            // What is behind it, unreadable: the shape of the section without its spoilers.
            try
            {
                UIElement behind = Build();
                if (behind != null)
                {
                    behind.Effect = new BlurEffect { Radius = 24, KernelType = KernelType.Gaussian };
                    behind.Opacity = 0.5;
                    behind.IsHitTestVisible = false;
                    grid.Children.Add(behind);
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn($"[Achievements] Could not draw behind the guard: {ex.Message}");
            }

            var cover = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0x0B, 0x0E, 0x14)),
                CornerRadius = new CornerRadius(3),
            };
            var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 14, 20, 14), MaxWidth = 460 };
            var title = Text("Achievements hidden", 15, true);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            words.Children.Add(title);

            string name = _plugin.RetroAchievements.RomMUsername;
            bool named = name.Length > 0;
            var line = Text(named
                ? $"Sign in to RetroAchievements as {name} to see your achievements and earn them in your emulators."
                : "Set your RetroAchievements username in your RomM profile to see your achievements and earn them in your emulators.", 12);
            line.TextAlignment = TextAlignment.Center;
            line.Opacity = 0.8;
            line.Margin = new Thickness(0, 6, 0, 12);
            words.Children.Add(line);

            // No RetroAchievements name in the RomM profile yet: it is entered here and saved there.
            TextBox field = null;
            TextBlock problem = null;
            if (!named)
            {
                line.Text = "Enter your RetroAchievements username to see your achievements and earn them in your emulators. It is saved to your RomM profile.";
                field = new TextBox
                {
                    Width = 220,
                    Height = 30,
                    Padding = new Thickness(8, 0, 8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromRgb(0x32, 0x35, 0x3C)),
                    Foreground = Brushes.White,
                    CaretBrush = Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 0, 10),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                words.Children.Add(field);
                problem = new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E)),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8),
                    Visibility = Visibility.Collapsed,
                };
                words.Children.Add(problem);
            }

            var label = new TextBlock { Text = named ? "Sign in to RetroAchievements" : "Save username", Foreground = Brushes.White, FontSize = 13 };
            var normal = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
            var lit = new SolidColorBrush(Color.FromRgb(0x47, 0xB2, 0xFF));
            var button = new Border
            {
                Background = normal,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(16, 6, 16, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = label,
            };
            button.MouseEnter += (s, e) => button.Background = lit;
            button.MouseLeave += (s, e) => button.Background = normal;
            bool saving = false;
            Action act = () =>
            {
                if (named)
                {
                    _plugin.SignInToRetroAchievements();
                    return;
                }
                string typed = field.Text.Trim();
                if (saving || typed.Length == 0)
                    return;
                saving = true;
                label.Text = "Saving...";
                problem.Visibility = Visibility.Collapsed;
                _plugin.SetRetroAchievementsUsername(typed, error => Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Saved: the guard redraws itself to the sign-in prompt.
                    saving = false;
                    label.Text = "Save username";
                    if (error != null)
                    {
                        problem.Text = error;
                        problem.Visibility = Visibility.Visible;
                    }
                })));
            };
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                act();
            };
            if (field != null)
            {
                field.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        act();
                    }
                };
            }
            words.Children.Add(button);
            cover.Child = words;
            grid.Children.Add(cover);
            return grid;
        }

        // Why this copy cannot earn its achievements: a line above the list, which stays as it is.
        private FrameworkElement CannotEarnNotice(string why)
        {
            var words = new StackPanel();
            var title = Text("Achievements unavailable", 13, true);
            words.Children.Add(title);
            var line = Text(why, 12);
            line.Opacity = 0.8;
            line.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(line);
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x66, 0x0B, 0x0E, 0x14)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xE5, 0xB1, 0x43)),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 10),
                Child = words,
            };
        }

        private FrameworkElement BuildFirstPlay(string gameName, string emulatorName)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0x0B, 0x0E, 0x14)),
                CornerRadius = new CornerRadius(3),
                MinHeight = 90,
            };
            var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 14, 20, 14), MaxWidth = 460 };
            var title = Text("Play once to see achievements", 15, true);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            words.Children.Add(title);
            var line = Text($"{emulatorName} keeps this game's achievements itself. They are shown here after {gameName} has been started in {emulatorName} once.", 12);
            line.TextAlignment = TextAlignment.Center;
            line.Opacity = 0.8;
            line.Margin = new Thickness(0, 6, 0, 0);
            words.Children.Add(line);
            card.Child = words;
            return card;
        }

        private bool Enabled()
        {
            var summary = _plugin.CloudStatus.Achievements;
            if (_kind == AchievementView.CompactUnlocked)
                return summary.EnableIntegrationCompactUnlocked;
            if (_kind == AchievementView.CompactLocked)
                return summary.EnableIntegrationCompactLocked;
            return true;
        }

        private void Render()
        {
            try
            {
                UIElement built = Build();
                if (_cannotEarn)
                {
                    var both = new StackPanel();
                    both.Children.Add(CannotEarnNotice(_data.Unavailable));
                    if (built != null)
                        both.Children.Add(built);
                    Content = both;
                }
                else
                    Content = built;
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn($"[Achievements] Could not draw {_kind}: {ex.Message}");
            }
        }

        private UIElement Build()
        {
            switch (_kind)
            {
                case AchievementView.Button: return BuildButton();
                case AchievementView.ProgressBar: return BuildProgress();
                case AchievementView.CompactList: return BuildCompact(_data.Items.OrderByDescending(i => i.Unlocked).ThenBy(i => i.Hidden).ThenByDescending(i => i.When).ThenBy(i => i.Order));
                case AchievementView.CompactUnlocked: return BuildCompact(_data.Items.Where(i => i.Unlocked).OrderByDescending(i => i.When));
                case AchievementView.CompactLocked: return BuildCompact(_data.Items.Where(i => !i.Unlocked).OrderBy(i => i.Hidden).ThenBy(i => i.Order));
                case AchievementView.UserStats: return BuildStats(_data);
                case AchievementView.List: return BuildList(_data);
                case AchievementView.Chart: return BuildChart(_data);
                default: return null;
            }
        }

        // ---- Pieces ---------------------------------------------------------------------------

        private static TextBlock Text(string text, double size, bool bold = false)
        {
            var block = new TextBlock { Text = text ?? "", FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        // A cup on a stem and base: the badge of an achievement with no picture of its own.
        private static readonly Geometry TrophyShape = Geometry.Parse(
            "M7,3 H17 V8 A5,5 0 0 1 12,13 A5,5 0 0 1 7,8 Z " +
            "M5,4 H7 V6 H5.5 V7 A2,2 0 0 0 7,9 V10.5 A3.5,3.5 0 0 1 4,7 V4 Z " +
            "M19,4 H17 V6 H18.5 V7 A2,2 0 0 1 17,9 V10.5 A3.5,3.5 0 0 0 20,7 V4 Z " +
            "M11,13 H13 V17 H11 Z M8,18 H16 V21 H8 Z");

        // A padlock: the badge of a hidden achievement not unlocked yet.
        private static readonly Geometry LockShape = Geometry.Parse(
            "M8,10 V7 A4,4 0 0 1 16,7 V10 H14 V7 A2,2 0 0 0 10,7 V10 Z " +
            "M6,10 H18 V21 H6 Z");

        private static readonly Geometry KeyholeShape = Geometry.Parse("M12,13 A1.6,1.6 0 1 1 11.99,13 Z M11.3,15 H12.7 V18 H11.3 Z");

        private static FrameworkElement DrawnBadge(bool hidden, bool unlocked, double size)
        {
            var canvas = new Grid { Width = 24, Height = 24 };
            var brush = new SolidColorBrush(hidden ? Color.FromRgb(0x8A, 0x93, 0xA3) : unlocked ? Color.FromRgb(0xE3, 0xB3, 0x41) : Color.FromRgb(0x9A, 0xA3, 0xB0));
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = hidden ? LockShape : TrophyShape, Fill = brush });
            if (hidden)
                canvas.Children.Add(new System.Windows.Shapes.Path { Data = KeyholeShape, Fill = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x29)) });
            return new Viewbox
            {
                Width = size * 0.62,
                Height = size * 0.62,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = canvas,
                Opacity = unlocked || hidden ? 1 : 0.55,
            };
        }

        internal static FrameworkElement Badge(AchievementItem item, double size)
        {
            string picture = item.Unlocked ? item.BadgeUrl : (item.BadgeLockedUrl ?? item.BadgeUrl);
            if (item.Hidden || string.IsNullOrEmpty(picture))
            {
                return new Border
                {
                    Width = size,
                    Height = size,
                    Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x31, 0x3D), Color.FromRgb(0x1B, 0x20, 0x29), 90),
                    Child = DrawnBadge(item.Hidden && !item.Unlocked, item.Unlocked, size),
                    ToolTip = Tip(item),
                };
            }

            var image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
            string url = item.Unlocked ? item.BadgeUrl : (item.BadgeLockedUrl ?? item.BadgeUrl);
            StoreImages.Show(image, url, (int)(size * 2));
            if (!item.Unlocked && string.IsNullOrEmpty(item.BadgeLockedUrl))
                image.Opacity = 0.35;

            return new Border
            {
                Width = size,
                Height = size,
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                Child = image,
                ToolTip = Tip(item),
            };
        }

        private static string Tip(AchievementItem item)
        {
            string state = item.Unlocked
                ? "Unlocked " + item.When.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) + (item.UnlockedHardcoreUtc.HasValue ? " (hardcore)" : "")
                : "Locked";
            return $"{item.Title}\n{item.Description}\n{item.Points} points \u00B7 {state}";
        }

        private static string Percent(GameAchievements data) =>
            data.Total == 0 ? "0%" : Math.Round(100.0 * data.Unlocked / data.Total).ToString(CultureInfo.InvariantCulture) + "%";

        // A button showing progress; opens the full list in its own window.
        private UIElement BuildButton()
        {
            var text = Text($"{_data.Unlocked} / {_data.Total}", 13, true);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextWrapping = TextWrapping.NoWrap;
            var button = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                ToolTip = "Achievements",
                Child = text,
            };
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                OpenWindow();
            };
            return button;
        }

        private void OpenWindow()
        {
            try
            {
                Game game = _plugin.Playnite.Database.Games.Get(_gameId);
                var window = _plugin.Playnite.Dialogs.CreateWindow(new WindowCreationOptions { ShowMinimizeButton = false, ShowMaximizeButton = true });
                window.Title = (game?.Name ?? "Game") + " \u2014 Achievements";
                window.Width = 760;
                window.Height = 760;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Owner = _plugin.Playnite.Dialogs.GetCurrentAppWindow();

                var body = new StackPanel { Margin = new Thickness(20) };
                body.Children.Add(BuildStats(_data));
                body.Children.Add(new Border { Height = 16 });
                body.Children.Add(BuildList(_data));
                var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
                SmoothScroll.Attach(scroll);
                window.Content = scroll;
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn($"[Achievements] Could not open the achievements window: {ex.Message}");
            }
        }

        private UIElement BuildProgress()
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, _data.Total), Value = _data.Unlocked, MinHeight = 16 };
            var label = Text($"{_data.Unlocked} / {_data.Total}  ({Percent(_data)})", 11, true);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextWrapping = TextWrapping.NoWrap;
            var grid = new Grid();
            grid.Children.Add(bar);
            grid.Children.Add(label);
            return grid;
        }

        // A single row of badges, as many as fit, with "+N" for the rest.
        private UIElement BuildCompact(IEnumerable<AchievementItem> items)
        {
            var list = items.ToList();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (list.Count == 0)
            {
                var none = Text(_kind == AchievementView.CompactUnlocked ? "None unlocked yet." : "All unlocked.", 12);
                none.Opacity = 0.6;
                return none;
            }

            double width = ActualWidth > 0 ? ActualWidth : 600;
            int fits = Math.Max(1, (int)((width + CompactGap) / (CompactBadge + CompactGap)));
            int shown = list.Count <= fits ? list.Count : fits - 1;
            foreach (AchievementItem item in list.Take(shown))
            {
                FrameworkElement badge = Badge(item, CompactBadge);
                badge.Margin = new Thickness(0, 0, CompactGap, 0);
                row.Children.Add(badge);
            }
            if (list.Count > shown)
            {
                var more = Text("+" + (list.Count - shown), 14, true);
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(new Border
                {
                    Width = CompactBadge,
                    Height = CompactBadge,
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    Child = more,
                    Cursor = Cursors.Hand,
                    ToolTip = "Show all achievements",
                });
                ((Border)row.Children[row.Children.Count - 1]).MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenWindow(); };
            }
            return row;
        }

        private UIElement BuildStats(GameAchievements data)
        {
            var row = new WrapPanel();
            Action<string, string> stat = (label, value) =>
            {
                var cell = new StackPanel { Margin = new Thickness(0, 0, 32, 8) };
                var name = Text(label.ToUpperInvariant(), 10);
                name.Opacity = 0.55;
                cell.Children.Add(name);
                cell.Children.Add(Text(value, 18, true));
                row.Children.Add(cell);
            };

            stat("Unlocked", $"{data.Unlocked} / {data.Total}");
            stat("Completion", Percent(data));
            stat("Points", $"{data.PointsUnlocked} / {data.Points}");
            int hardcore = data.Items.Count(i => i.UnlockedHardcoreUtc.HasValue);
            if (hardcore > 0)
                stat("Hardcore", hardcore.ToString(CultureInfo.InvariantCulture));
            DateTime? last = data.Items.Where(i => i.Unlocked).Select(i => i.When).OrderByDescending(d => d).FirstOrDefault();
            if (last.HasValue)
                stat("Last unlocked", last.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            return row;
        }

        // Every achievement: unlocked ones first, newest first, then the locked ones in the
        // game's own order.
        private UIElement BuildList(GameAchievements data)
        {
            var list = new StackPanel();
            foreach (AchievementItem item in data.Items.OrderByDescending(i => i.Unlocked).ThenBy(i => i.Hidden).ThenByDescending(i => i.When).ThenBy(i => i.Order))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                FrameworkElement badge = Badge(item, 52);
                badge.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(badge);

                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(Text(item.Title, 14, true));
                var desc = Text(item.Description, 12);
                desc.Opacity = 0.75;
                words.Children.Add(desc);
                Grid.SetColumn(words, 1);
                row.Children.Add(words);

                var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
                var points = Text($"{item.Points} pts", 12, true);
                points.HorizontalAlignment = HorizontalAlignment.Right;
                side.Children.Add(points);
                var when = Text(item.Unlocked ? item.When.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Locked", 11);
                when.HorizontalAlignment = HorizontalAlignment.Right;
                when.Opacity = 0.6;
                if (item.UnlockedHardcoreUtc.HasValue)
                {
                    when.Text += "  \u00B7  Hardcore";
                    when.Foreground = Accent;
                    when.Opacity = 1;
                }
                side.Children.Add(when);
                Grid.SetColumn(side, 2);
                row.Children.Add(side);

                list.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(item.Unlocked ? (byte)0x30 : (byte)0x18, 0, 0, 0)),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Opacity = item.Unlocked ? 1 : 0.8,
                    Child = row,
                });
            }
            return list;
        }

        // Unlocks per month, as columns, from the first unlock (at most the last 24 months).
        private UIElement BuildChart(GameAchievements data)
        {
            var dates = data.Items.Where(i => i.Unlocked).Select(i => i.When.Value.ToLocalTime()).ToList();
            if (dates.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                return null;
            }

            DateTime last = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            DateTime first = new DateTime(dates.Min().Year, dates.Min().Month, 1);
            if (first < last.AddMonths(-23))
                first = last.AddMonths(-23);

            var months = new List<DateTime>();
            for (DateTime m = first; m <= last; m = m.AddMonths(1))
                months.Add(m);
            if (months.Count < 6)
            {
                // A little context either side of a short burst.
                while (months.Count < 6)
                    months.Insert(0, months[0].AddMonths(-1));
            }
            var counts = months.Select(m => dates.Count(d => d.Year == m.Year && d.Month == m.Month)).ToList();
            int peak = Math.Max(1, counts.Max());

            const double height = 110;
            var chart = new Grid { Height = height + 34 };
            for (int i = 0; i < months.Count; i++)
            {
                chart.ColumnDefinitions.Add(new ColumnDefinition());
                var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0) };
                if (counts[i] > 0)
                {
                    var n = Text(counts[i].ToString(CultureInfo.InvariantCulture), 10);
                    n.HorizontalAlignment = HorizontalAlignment.Center;
                    n.TextWrapping = TextWrapping.NoWrap;
                    column.Children.Add(n);
                }
                column.Children.Add(new Border { Height = Math.Max(2, height * counts[i] / peak), Background = counts[i] > 0 ? Accent : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
                var label = Text(months[i].ToString("MMM", CultureInfo.CurrentCulture), 9);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.TextWrapping = TextWrapping.NoWrap;
                label.Opacity = 0.6;
                column.Children.Add(label);
                Grid.SetColumn(column, i);
                chart.Children.Add(column);
            }

            var box = new StackPanel();
            var title = Text("UNLOCKS PER MONTH", 10);
            title.Opacity = 0.55;
            title.Margin = new Thickness(0, 0, 0, 6);
            box.Children.Add(title);
            box.Children.Add(chart);
            return box;
        }
    }
}
