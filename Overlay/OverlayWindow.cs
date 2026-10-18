using Playnite.SDK.Models;
using RomM.AchievementViews;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RomM.Overlay
{
    /// <summary>
    /// The overlay itself (GameOverlay opens it): the game's screen dimmed, a bar along the top
    /// with the game, the clock and the time played (this session, and in all), the game's
    /// achievements in a panel (unlocked first, newest first, then the rest dimmed), and Resume
    /// and Quit game. Keyboard and mouse work as anywhere; the controller's D-pad moves between
    /// the buttons, A presses, B closes (GameOverlay reads it). Esc closes too.
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        private static readonly Brush PanelBrush = Frozen(Color.FromArgb(0xE6, 0x16, 0x1A, 0x22));
        private static readonly Brush Edge = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Muted = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Blue = Frozen(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush Red = Frozen(Color.FromRgb(0xC2, 0x3B, 0x3B));
        private static readonly Brush Gold = Frozen(Color.FromRgb(0xE5, 0xB1, 0x43));

        private readonly RomM _plugin;
        private readonly Game _game;
        private readonly DateTime _startedUtc;
        private readonly List<Button> _buttons = new List<Button>();
        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private TextBlock _time;
        private TextBlock _session;
        private readonly StackPanel _achievements = new StackPanel();
        private TextBlock _achievementSummary;
        private readonly bool _keepsRunning;
        private readonly GameAudio _audio;
        private Button _volumeButton;
        private TextBlock _volumeText;
        private Border _volumeFill;
        private ColumnDefinition _volumeOn, _volumeOff;

        public event Action ResumeRequested;
        public event Action QuitRequested;

        /// <param name="keepsRunning">A Windows game, not paused while this is open.</param>
        public OverlayWindow(RomM plugin, Game game, DateTime startedUtc, Int32Rect screen, bool keepsRunning = false, GameAudio audio = null)
        {
            _keepsRunning = keepsRunning;
            _audio = audio;
            _plugin = plugin;
            _game = game;
            _startedUtc = startedUtc;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x05, 0x07, 0x0C));
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Title = "RomM overlay";
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (!screen.IsEmpty)
            {
                // Placed on the game's screen, then maximised there to cover it.
                double scale = DpiScale();
                Left = screen.X / scale + 20;
                Top = screen.Y / scale + 20;
                Width = 400;
                Height = 300;
            }
            Loaded += (s, e) =>
            {
                WindowState = WindowState.Maximized;
                Fill();
                // Resume first; the game's volume is just above it.
                if (_buttons.Count > 0)
                    (_buttons.FirstOrDefault(b => b != _volumeButton) ?? _buttons[0]).Focus();
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    ResumeRequested?.Invoke();
                }
            };
            _clock.Tick += (s, e) => Tick();
            Closed += (s, e) => _clock.Stop();

            Content = Build();
            Tick();
            _clock.Start();
        }

        private static double DpiScale()
        {
            try
            {
                Window main = Application.Current?.MainWindow;
                PresentationSource source = main != null ? PresentationSource.FromVisual(main) : null;
                return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        // ---- Layout ---------------------------------------------------------------------------

        private UIElement Build()
        {
            var root = new Grid { Margin = new Thickness(48, 32, 48, 40) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Top bar: the game, then the clock and the time played on the right.
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 28), LastChildFill = true };
            var times = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(times, Dock.Right);
            _session = Words("", 16, FontWeights.Normal, Muted);
            _session.Margin = new Thickness(0, 0, 28, 0);
            _session.VerticalAlignment = VerticalAlignment.Center;
            _time = Words("", 28, FontWeights.SemiBold, Brushes.White);
            times.Children.Add(_session);
            times.Children.Add(_time);
            bar.Children.Add(times);

            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(Words(_keepsRunning ? "NOW PLAYING  \u00B7  STILL RUNNING" : "NOW PLAYING  \u00B7  PAUSED", 12, FontWeights.SemiBold, Muted));
            title.Children.Add(Words(_game.Name ?? "", 30, FontWeights.Bold, Brushes.White));
            bar.Children.Add(title);
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            // Below: achievements on the left, the buttons on the right.
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 760 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var achievementsPanel = new DockPanel();
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(heading, Dock.Top);
            heading.Children.Add(Words("ACHIEVEMENTS", 13, FontWeights.SemiBold, Muted));
            _achievementSummary = Words("", 20, FontWeights.SemiBold, Brushes.White);
            heading.Children.Add(_achievementSummary);
            achievementsPanel.Children.Add(heading);
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _achievements,
                Focusable = false,
            };
            achievementsPanel.Children.Add(scroller);
            body.Children.Add(Card(achievementsPanel, new Thickness(0)));

            var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            if (_audio != null)
                actions.Children.Add(VolumeRow());
            actions.Children.Add(ActionButton("Resume", Blue, () => ResumeRequested?.Invoke()));
            actions.Children.Add(ActionButton("Quit game", Red, () => QuitRequested?.Invoke()));
            // Only the buttons of what is in use.
            string how = InputActivity.ControllerInUse ? "D-pad to choose, A to press, B to close." : "Arrow keys or the mouse to choose, Enter to press, Esc to close.";
            if (_audio != null)
                how += InputActivity.ControllerInUse ? " Left and right change the game's volume." : " Left and right arrows change the game's volume.";
            if (_keepsRunning)
                how += InputActivity.ControllerInUse
                    ? " The game keeps running and may see these buttons too."
                    : " The game keeps running while this is open.";
            var hint = Words(how, 12, FontWeights.Normal, Muted);
            hint.Margin = new Thickness(0, 14, 0, 0);
            actions.Children.Add(hint);
            Grid.SetColumn(actions, 2);
            body.Children.Add(actions);

            return root;
        }

        private Button ActionButton(string text, Brush color, Action action)
        {
            var label = Words(text, 18, FontWeights.SemiBold, Brushes.White);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var face = new Border { Background = color, CornerRadius = new CornerRadius(4), Padding = new Thickness(18, 12, 18, 12), Child = label };
            var ring = new Border { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(6), BorderBrush = Brushes.Transparent, Child = face };
            var button = new Button
            {
                Content = ring,
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = Cursors.Hand,
                Template = BareTemplate(),
                FocusVisualStyle = null,
            };
            // The chosen button: a white ring around it.
            button.GotKeyboardFocus += (s, e) => ring.BorderBrush = Brushes.White;
            button.LostKeyboardFocus += (s, e) => ring.BorderBrush = Brushes.Transparent;
            button.MouseEnter += (s, e) => button.Focus();
            button.Click += (s, e) => action();
            _buttons.Add(button);
            return button;
        }

        // ---- The game's volume ----------------------------------------------------------------

        // "Game volume" with its level as a bar and in words: chosen like the other buttons; left
        // and right turn it down and up by a tenth, pressing it mutes and unmutes. Only the game
        // (its own sound, as Windows' volume mixer has it), nothing else on the PC.
        private UIElement VolumeRow()
        {
            var title = Words("Game volume", 14, FontWeights.SemiBold, Brushes.White);
            _volumeText = Words("", 14, FontWeights.Normal, Muted);
            _volumeText.HorizontalAlignment = HorizontalAlignment.Right;
            var heading = new Grid();
            heading.Children.Add(title);
            heading.Children.Add(_volumeText);

            _volumeOn = new ColumnDefinition();
            _volumeOff = new ColumnDefinition();
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
            bar.ColumnDefinitions.Add(_volumeOn);
            bar.ColumnDefinitions.Add(_volumeOff);
            _volumeFill = new Border { Background = Blue, CornerRadius = new CornerRadius(3) };
            var track = new Border { Background = Edge, CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            bar.Children.Add(_volumeFill);

            var panel = new StackPanel();
            panel.Children.Add(heading);
            panel.Children.Add(bar);
            var face = new Border { Background = PanelBrush, CornerRadius = new CornerRadius(4), Padding = new Thickness(16, 12, 16, 14), Child = panel };
            var ring = new Border { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(6), BorderBrush = Brushes.Transparent, Child = face };
            var button = new Button
            {
                Content = ring,
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = Cursors.Hand,
                Template = BareTemplate(),
                FocusVisualStyle = null,
                ToolTip = "Left and right: volume. Press: mute.",
            };
            button.GotKeyboardFocus += (s, e) => ring.BorderBrush = Brushes.White;
            button.LostKeyboardFocus += (s, e) => ring.BorderBrush = Brushes.Transparent;
            button.MouseEnter += (s, e) => button.Focus();
            button.Click += (s, e) => ToggleMute();
            button.MouseWheel += (s, e) =>
            {
                e.Handled = true;
                Step(e.Delta > 0 ? 1 : -1);
            };
            button.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Left || e.Key == Key.Right)
                {
                    e.Handled = true;
                    Step(e.Key == Key.Right ? 1 : -1);
                }
            };
            _volumeButton = button;
            _buttons.Add(button);
            ShowVolume();
            return button;
        }

        /// <summary>
        /// The controller's left or right: the game's volume down or up a tenth when it is the
        /// chosen button (and true), else nothing (false).
        /// </summary>
        public bool Adjust(int step)
        {
            if (_volumeButton == null || !_volumeButton.IsKeyboardFocused)
                return false;
            Step(step);
            return true;
        }

        private void Step(int step)
        {
            if (_audio == null)
                return;
            // To the nearest tenth, so steps land on 0%, 10%, ... 100%.
            double level = Math.Round(_audio.Volume * 10) / 10.0 + step * 0.1;
            level = Math.Max(0, Math.Min(1, level));
            _audio.SetVolume((float)level);
            // Turning it up from muted unmutes it.
            if (step > 0 && _audio.Muted)
                _audio.SetMuted(false);
            ShowVolume();
        }

        private void ToggleMute()
        {
            if (_audio == null)
                return;
            _audio.SetMuted(!_audio.Muted);
            ShowVolume();
        }

        private void ShowVolume()
        {
            if (_audio == null || _volumeText == null)
                return;
            float level = _audio.Volume;
            bool muted = _audio.Muted;
            _volumeText.Text = muted ? "Muted" : $"{Math.Round(level * 100)}%";
            double shown = muted ? 0 : level;
            _volumeOn.Width = new GridLength(Math.Max(0.0001, shown), GridUnitType.Star);
            _volumeOff.Width = new GridLength(Math.Max(0.0001, 1 - shown), GridUnitType.Star);
            _volumeFill.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // A button that draws only its content.
        private static ControlTemplate BareTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
            return template;
        }

        /// <summary>The controller's D-pad: the next or previous button.</summary>
        public void Move(int step)
        {
            if (_buttons.Count == 0)
                return;
            int at = _buttons.FindIndex(b => b.IsKeyboardFocused);
            at = at < 0 ? 0 : (at + step + _buttons.Count) % _buttons.Count;
            _buttons[at].Focus();
        }

        /// <summary>The controller's A: the chosen button.</summary>
        public void Press()
        {
            Button chosen = _buttons.FirstOrDefault(b => b.IsKeyboardFocused) ?? _buttons.FirstOrDefault();
            chosen?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        // ---- Content --------------------------------------------------------------------------

        private void Tick()
        {
            _time.Text = DateTime.Now.ToString("t");
            TimeSpan session = DateTime.UtcNow - _startedUtc;
            ulong total = _game.Playtime + (ulong)Math.Max(0, session.TotalSeconds);
            _session.Text = $"This session {Duration((ulong)session.TotalSeconds)}   ·   Played {Duration(total)} in all";
        }

        private void Fill()
        {
            GameAchievements set = _plugin.AchievementStore?.Get(_game.Id);
            _achievements.Children.Clear();
            if (set == null || set.Items == null || set.Items.Count == 0)
            {
                _achievementSummary.Text = "None for this game";
                _achievements.Children.Add(Words(_keepsRunning
                    ? "RomMnite has no achievements for this game."
                    : "This game has no achievements here yet, or they have not been loaded.", 14, FontWeights.Normal, Muted));
                return;
            }
            _achievementSummary.Text = $"{set.Unlocked} of {set.Total} unlocked" + (set.Points > 0 ? $"   ·   {set.PointsUnlocked} of {set.Points} points" : "");
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 0, 0, 14) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(set.Unlocked, 0), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(set.Total - set.Unlocked, 0), GridUnitType.Star) });
            bar.Children.Add(new Border { Background = Blue, CornerRadius = new CornerRadius(3, 0, 0, 3) });
            var rest = new Border { Background = Edge, CornerRadius = new CornerRadius(0, 3, 3, 0) };
            Grid.SetColumn(rest, 1);
            bar.Children.Add(rest);
            _achievements.Children.Add(bar);

            IEnumerable<AchievementItem> ordered = set.Items.Where(i => i.Unlocked).OrderByDescending(i => i.When)
                .Concat(set.Items.Where(i => !i.Unlocked).OrderBy(i => i.Order));
            foreach (AchievementItem item in ordered)
                _achievements.Children.Add(Row(item));
        }

        private UIElement Row(AchievementItem item)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Opacity = item.Unlocked ? 1.0 : 0.5 };
            var badge = new Image { Width = 48, Height = 48, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.Fant);
            Store.StoreImages.Show(badge, item.Unlocked ? item.BadgeUrl : (item.BadgeLockedUrl ?? item.BadgeUrl), 96);
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);

            if (item.Points > 0)
            {
                var points = Words(item.Points.ToString(), 14, FontWeights.SemiBold, Gold);
                points.VerticalAlignment = VerticalAlignment.Top;
                points.Margin = new Thickness(12, 2, 0, 0);
                DockPanel.SetDock(points, Dock.Right);
                row.Children.Add(points);
            }

            var words = new StackPanel();
            bool secret = item.Hidden && !item.Unlocked;
            words.Children.Add(Words(secret ? "Hidden achievement" : item.Title ?? "", 15, FontWeights.SemiBold, Brushes.White));
            string detail = secret ? "Keep playing to discover this achievement." : item.Description ?? "";
            if (item.Unlocked && item.When.HasValue)
                detail += (detail.Length > 0 ? "   ·   " : "") + "Unlocked " + item.When.Value.ToLocalTime().ToString("d MMM yyyy");
            if (detail.Length > 0)
                words.Children.Add(Words(detail, 13, FontWeights.Normal, Muted));
            row.Children.Add(words);
            return row;
        }

        private static Border Card(UIElement child, Thickness margin) => new Border
        {
            Background = PanelBrush,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = margin,
            Child = child,
        };

        private static TextBlock Words(string text, double size, FontWeight weight, Brush color) =>
            new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = color, TextWrapping = TextWrapping.Wrap };

        private static string Duration(ulong seconds)
        {
            ulong minutes = seconds / 60;
            if (minutes < 60)
                return minutes + " min";
            ulong hours = minutes / 60, rest = minutes % 60;
            return rest == 0 ? hours + " h" : $"{hours} h {rest} min";
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
