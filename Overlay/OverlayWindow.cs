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
    /// and Quit game. Keyboard and mouse work as anywhere; on the controller the bumpers choose
    /// the achievements (LB: the D-pad goes from one to the next, held it keeps going) or the
    /// buttons (RB, the D-pad moves between them), A presses, B closes (GameOverlay reads it).
    /// Esc closes too. Above the buttons, the game's cloud saves as they stand (SaveSync), looked
    /// at again every few seconds. The buttons to press are shown as the controller's own
    /// (ControllerGlyphs).
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
        private ScrollViewer _scroller;
        private Border _listCard;
        private bool _inList;   // the controller's D-pad scrolls the achievements
        private readonly List<Border> _rows = new List<Border>();
        private readonly List<string> _rowKeys = new List<string>();
        private readonly List<FrameworkElement> _rowDetails = new List<FrameworkElement>();   // the pop-out of each (null: none)
        private readonly List<bool> _rowOpen = new List<bool>();
        private int _selected = -1;
        private bool _mouseInList;
        private Border _savesCard;
        private TextBlock _savesHeadline;
        private TextBlock _savesDetail;
        private int _ticks;
        private bool _savesLooking;

        public event Action ResumeRequested;
        public event Action QuitRequested;
        public event Action EndStreamRequested;
        private StackPanel _actions;
        private Button _quitButton;
        private Button _endStream;
        private static readonly Brush Purple = Frozen(Color.FromRgb(0x7A, 0x4C, 0xC8));

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
            _clock.Tick += (s, e) =>
            {
                Tick();
                if (++_ticks % 5 == 0)
                    ShowSaves();
            };
            Closed += (s, e) => _clock.Stop();
            // An achievement unlocked while it is open: the list again, where it was scrolled to.
            Action<Guid> changed = id =>
            {
                if (id != _game.Id)
                    return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    double offset = _scroller?.VerticalOffset ?? 0;
                    Fill();
                    _scroller?.ScrollToVerticalOffset(offset);
                }));
            };
            if (_plugin.AchievementStore != null)
            {
                _plugin.AchievementStore.Changed += changed;
                Closed += (s, e) => _plugin.AchievementStore.Changed -= changed;
            }

            Content = Build();
            Tick();
            ShowSaves();
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
            _scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _achievements,
                Focusable = true,
                FocusVisualStyle = null,
            };
            // Chosen (LB, or clicked into): its edge lit, the buttons' ring off (they lose focus).
            _scroller.GotKeyboardFocus += (s, e) => MarkList(true);
            _scroller.LostKeyboardFocus += (s, e) => MarkList(false);
            // The arrow keys go from one achievement to the next, as the D-pad does.
            _scroller.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Up || e.Key == Key.Down)
                {
                    e.Handled = true;
                    Move(e.Key == Key.Down ? 1 : -1);
                }
            };
            _scroller.MouseEnter += (s, e) => { _mouseInList = true; ShowSelection(); };
            _scroller.MouseLeave += (s, e) => { _mouseInList = false; ShowSelection(); };
            achievementsPanel.Children.Add(_scroller);
            _listCard = Card(achievementsPanel, new Thickness(0));
            body.Children.Add(_listCard);

            // The right: the cloud saves at the top, the buttons at the bottom.
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _savesCard = SavesCard();
            right.Children.Add(_savesCard);
            var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetRow(actions, 2);
            right.Children.Add(actions);
            if (_audio != null)
                actions.Children.Add(VolumeRow());
            actions.Children.Add(ActionButton("Resume", Blue, () => ResumeRequested?.Invoke()));
            _quitButton = ActionButton("Quit game", Red, () => QuitRequested?.Invoke());
            actions.Children.Add(_quitButton);
            _actions = actions;
            // Only the buttons of what is in use, as pictures of them, changing live as the
            // controller or the keyboard is used (ControllerGlyphs.Bind).
            string padHow = "{LB} Achievements   {RB} Buttons   {DPad} Move   {A} Select   {B} Close";
            string keyHow = "{Arrows} Move   {Enter} Select   {Esc} Close";
            if (_audio != null)
            {
                padHow += "   {DPadH} Volume";
                keyHow += "   {ArrowsH} Volume";
            }
            var hint = Words("", 13, FontWeights.Normal, Muted);
            hint.LineHeight = 34;
            hint.Margin = new Thickness(0, 12, 0, 0);
            ControllerGlyphs.Bind(hint, padHow, keyHow);
            actions.Children.Add(hint);
            if (_keepsRunning)
            {
                var running = Words("", 12, FontWeights.Normal, Muted);
                running.Margin = new Thickness(0, 4, 0, 0);
                ControllerGlyphs.Bind(running, "The game keeps running and may see these buttons too.", "The game keeps running while this is open.");
                actions.Children.Add(running);
            }
            Grid.SetColumn(right, 2);
            body.Children.Add(right);

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
            if (_inList)
                return true;   // left and right do nothing in the list
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

        /// <summary>
        /// The controller's bumpers: the achievements (the D-pad scrolls them) or the buttons
        /// (the D-pad moves between them, the one chosen before chosen again).
        /// </summary>
        public void UseList(bool list)
        {
            if (list)
            {
                _lastButton = _buttons.FirstOrDefault(b => b.IsKeyboardFocused) ?? _lastButton;
                _scroller?.Focus();
            }
            else
                (_lastButton ?? _buttons.FirstOrDefault(b => b != _volumeButton) ?? _buttons.FirstOrDefault())?.Focus();
        }

        private Button _lastButton;

        private void MarkList(bool chosen)
        {
            _inList = chosen;
            // The first one chosen when the list is first used.
            if (chosen && _selected < 0 && _rows.Count > 0)
                Select(0, true);
            else
                ShowSelection();
            if (_listCard != null)
            {
                _listCard.BorderBrush = chosen ? Blue : Edge;
                _listCard.BorderThickness = new Thickness(chosen ? 2 : 1);
            }
        }

        /// <summary>The controller's D-pad: the next or previous button, or the achievements scrolled.</summary>
        public void Move(int step)
        {
            if (_inList)
            {
                // One achievement to the next; with none (no list), the panel scrolled.
                if (_rows.Count > 0)
                    Select(_selected < 0 ? 0 : _selected + step, true);
                else
                    _scroller?.ScrollToVerticalOffset(Math.Max(0, _scroller.VerticalOffset + step * 58));
                return;
            }
            if (_buttons.Count == 0)
                return;
            int at = _buttons.FindIndex(b => b.IsKeyboardFocused);
            at = at < 0 ? 0 : (at + step + _buttons.Count) % _buttons.Count;
            _buttons[at].Focus();
        }

        /// <summary>The controller's A: the chosen button.</summary>
        public void Press()
        {
            if (_inList)
                return;
            Button chosen = _buttons.FirstOrDefault(b => b.IsKeyboardFocused) ?? _buttons.FirstOrDefault();
            chosen?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        // ---- Streaming (Sunshine) ------------------------------------------------------------

        /// <summary>The game is being streamed: "End stream" offered, above Quit game.</summary>
        public void OfferEndStream()
        {
            if (_endStream != null || _actions == null || _quitButton == null)
                return;
            _endStream = ActionButton("End stream", Purple, () => EndStreamRequested?.Invoke());
            _endStream.ToolTip = "Stops streaming to the other device. The game keeps running here.";
            // In the order shown, for the D-pad.
            _buttons.Remove(_endStream);
            _buttons.Insert(_buttons.IndexOf(_quitButton), _endStream);
            _actions.Children.Insert(_actions.Children.IndexOf(_quitButton), _endStream);
            _quitButton.ToolTip = "Closes the game, then ends the stream.";
        }

        /// <summary>The stream ended (problem null) or could not be: said on the button.</summary>
        public void StreamEnded(string problem)
        {
            if (_endStream == null)
                return;
            var label = (_endStream.Content as Border)?.Child is Border face ? face.Child as TextBlock : null;
            if (problem == null)
            {
                if (label != null)
                    label.Text = "Stream ended";
                _endStream.IsEnabled = false;
                _endStream.Opacity = 0.6;
            }
            else
            {
                if (label != null)
                    label.Text = "End stream (failed)";
                _endStream.ToolTip = problem;
            }
        }

        // ---- Cloud saves ---------------------------------------------------------------------

        private Border SavesCard()
        {
            var icon = new TextBlock
            {
                Text = "\uE753",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 22,
                Foreground = Muted,
                Margin = new Thickness(0, 2, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            _savesHeadline = Words("", 16, FontWeights.SemiBold, Brushes.White);
            _savesDetail = Words("", 12, FontWeights.Normal, Muted);
            var words = new StackPanel();
            words.Children.Add(Words("CLOUD SAVES", 11, FontWeights.SemiBold, Muted));
            words.Children.Add(_savesHeadline);
            words.Children.Add(_savesDetail);
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(words);
            Border card = Card(row, new Thickness(0));
            card.Padding = new Thickness(16, 12, 16, 14);
            card.Visibility = Visibility.Collapsed;   // until known, and only where saves sync
            return card;
        }

        // The game's cloud saves as they stand, looked at off the UI thread (it may read files).
        private void ShowSaves()
        {
            if (_savesLooking || _plugin.SaveSync == null || _savesCard == null)
                return;
            _savesLooking = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                string headline = null, detail = null;
                try
                {
                    _plugin.SaveSync.DescribeStatus(_game, out headline, out detail);
                }
                catch
                {
                    headline = null;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _savesLooking = false;
                    if (string.IsNullOrEmpty(headline))
                    {
                        _savesCard.Visibility = Visibility.Collapsed;
                        return;
                    }
                    _savesCard.Visibility = Visibility.Visible;
                    _savesHeadline.Text = headline;
                    _savesHeadline.Foreground = headline.StartsWith("Sync failed", StringComparison.OrdinalIgnoreCase) ? Red
                        : headline.StartsWith("Syncing", StringComparison.OrdinalIgnoreCase) ? Blue
                        : headline.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase) ? Gold
                        : Brushes.White;
                    _savesDetail.Text = detail ?? "";
                    _savesDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
                }));
            });
        }

        // ---- The pop-out: how rare it is ----------------------------------------------------------

        // Under an achievement, opened while it is the one chosen: the set's spread from rare to
        // common with this one marked (RarityCurve), and in words.
        private FrameworkElement Detail(AchievementItem item, double rarity, List<double> scores)
        {
            double rarerThan = RarityCurve.RarerThan(scores, rarity);
            string share = item.Rarity.HasValue
                ? $"{item.Rarity.Value:0.#}% of players have this"
                : $"{item.Players ?? 0:N0} players have this";
            string place = $"Rarer than {rarerThan:0}% of this game's achievements";
            var panel = new StackPanel { Margin = new Thickness(60, 10, 4, 4) };
            var words = Words(share + "   ·   " + place, 12, FontWeights.SemiBold, rarity <= RareAt ? Gold : Brushes.White);
            words.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(words);
            panel.Children.Add(new RarityCurve(scores, rarity, "Rarest", "Most common"));
            // Opened and closed by growing from its top (ShowSelection).
            panel.LayoutTransform = new ScaleTransform(1, 0);
            panel.Opacity = 0;
            return panel;
        }

        private static void Pop(FrameworkElement detail, bool open)
        {
            var scale = (ScaleTransform)detail.LayoutTransform;
            var time = TimeSpan.FromMilliseconds(180);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(open ? 1 : 0, time) { EasingFunction = ease });
            detail.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(open ? 1 : 0, time) { EasingFunction = ease });
        }

        // ---- The achievement chosen -------------------------------------------------------------

        // The chosen achievement lit (while the list is in use, or under the mouse).
        private void ShowSelection()
        {
            bool lit = _inList || _mouseInList;
            for (int i = 0; i < _rows.Count; i++)
            {
                bool on = lit && i == _selected;
                _rows[i].BorderBrush = on ? Brushes.White : Brushes.Transparent;
                _rows[i].Background = on ? Lighter : Brushes.Transparent;
                // The chosen one's pop-out open, the others closed.
                if (_rowDetails[i] != null && _rowOpen[i] != on)
                {
                    _rowOpen[i] = on;
                    Pop(_rowDetails[i], on);
                }
            }
        }

        private static readonly Brush Lighter = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        private void Select(int index, bool scroll)
        {
            if (_rows.Count == 0)
                return;
            _selected = Math.Max(0, Math.Min(_rows.Count - 1, index));
            ShowSelection();
            if (scroll)
            {
                Border row = _rows[_selected];
                Action show = () =>
                {
                    // With a little of the rows around it in sight.
                    row.BringIntoView(new Rect(0, -24, Math.Max(1, row.ActualWidth), row.ActualHeight + 48));
                    if (row == _rows.FirstOrDefault())
                        _scroller?.ScrollToTop();
                };
                show();
                // Again once its pop-out has opened, so all of it is in sight.
                var again = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                again.Tick += (s, e) =>
                {
                    again.Stop();
                    show();
                };
                again.Start();
            }
        }

        /// <summary>Up or down held on the controller: on through the achievements (only there).</summary>
        public void MoveHeld(int step)
        {
            if (_inList)
                Move(step);
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
            string selectedKey = _selected >= 0 && _selected < _rowKeys.Count ? _rowKeys[_selected] : null;
            _achievements.Children.Clear();
            _rows.Clear();
            _rowKeys.Clear();
            _rowDetails.Clear();
            _rowOpen.Clear();
            _selected = -1;
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

            // Closest to unlocking first (those with progress, furthest along first), then the
            // unlocked, newest first, then the rest of the locked in the game's order. With a
            // heading over each group when some are in progress.
            List<AchievementItem> close = set.Items.Where(i => i.InProgress)
                .OrderByDescending(i => i.ProgressShare ?? 0).ThenBy(i => i.Order).ToList();
            List<AchievementItem> unlocked = set.Items.Where(i => i.Unlocked).OrderByDescending(i => i.When).ToList();
            List<AchievementItem> stillLocked = AchievementViews.AchievementControl.Locked(set.Items).Where(i => !i.InProgress).ToList();
            var headings = new Dictionary<AchievementItem, string>();
            if (close.Count > 0)
            {
                headings[close[0]] = "Almost there";
                if (unlocked.Count > 0)
                    headings[unlocked[0]] = "Unlocked";
                if (stillLocked.Count > 0)
                    headings[stillLocked[0]] = "Locked";
            }
            IEnumerable<AchievementItem> ordered = close.Concat(unlocked).Concat(stillLocked);

            // How rare each is (Rarity): the share of players (Steam's), or for RetroAchievements,
            // its players compared with the set's most earned achievement's.
            int mostPlayers = set.Items.Max(i => i.Players ?? 0);
            Func<AchievementItem, double?> score = i => i.Rarity
                ?? (i.Players.HasValue && mostPlayers > 0 ? 100.0 * i.Players.Value / mostPlayers : (double?)null);
            List<double> scores = set.Items.Select(score).Where(v => v.HasValue).Select(v => v.Value).ToList();
            foreach (AchievementItem item in ordered)
            {
                if (headings.TryGetValue(item, out string heading))
                {
                    var label = Words(heading.ToUpperInvariant(), 11, FontWeights.SemiBold, Muted);
                    label.Margin = new Thickness(10, _rows.Count == 0 ? 0 : 12, 0, 6);
                    _achievements.Children.Add(label);
                }
                double? rarity = score(item);
                var content = new StackPanel();
                content.Children.Add(Row(item, rarity));
                FrameworkElement detail = rarity.HasValue && scores.Count >= 2 ? Detail(item, rarity.Value, scores) : null;
                if (detail != null)
                    content.Children.Add(detail);
                // Each in a frame lit when it is the one chosen.
                var frame = new Border
                {
                    Child = content,
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                };
                int index = _rows.Count;
                frame.MouseEnter += (s, e) => Select(index, false);
                _rows.Add(frame);
                _rowDetails.Add(detail);
                _rowOpen.Add(false);
                _rowKeys.Add((item.Title ?? "") + "|" + item.Order);
                _achievements.Children.Add(frame);
            }
            // The one chosen before, chosen again (the list redrawn on an unlock).
            _selected = selectedKey == null ? -1 : _rowKeys.IndexOf(selectedKey);
            if (_selected < 0 && selectedKey != null && _rows.Count > 0)
                _selected = 0;
            ShowSelection();
        }

        // A rare one: a tenth of players or fewer (or a tenth of the set's most earned).
        private const double RareAt = 10;

        private UIElement Row(AchievementItem item, double? rarity)
        {
            bool rare = rarity.HasValue && rarity.Value <= RareAt;
            var outer = new DockPanel();
            DockPanel row = outer;
            var badge = new Image { Width = 48, Height = 48, VerticalAlignment = VerticalAlignment.Top, Opacity = item.Unlocked ? 1.0 : 0.5 };
            RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.Fant);
            Store.StoreImages.Show(badge, item.Unlocked ? item.BadgeUrl : (item.BadgeLockedUrl ?? item.BadgeUrl), 96);
            // A rare one's badge in a glowing gold edge (bright even while locked).
            var badgeFrame = new Border
            {
                Child = badge,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(rare ? 2 : 0),
                BorderBrush = rare ? Gold : null,
            };
            if (rare)
                badgeFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0xFF, 0xC8, 0x4A),
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = item.Unlocked ? 0.95 : 0.6,
                };
            DockPanel.SetDock(badgeFrame, Dock.Left);
            row.Children.Add(badgeFrame);
            // The words dimmed while locked (the badge's glow is not); less so with progress made.
            var dimmed = new DockPanel { Opacity = item.Unlocked ? 1.0 : item.InProgress ? 0.85 : 0.5 };
            row.Children.Add(dimmed);
            row = dimmed;

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
            var titleLine = Words(secret ? "Hidden achievement" : item.Title ?? "", 15, FontWeights.SemiBold, Brushes.White);
            if (rare)
                titleLine.Inlines.Add(new System.Windows.Documents.Run("   RARE") { FontSize = 11, Foreground = Gold, FontWeight = FontWeights.Bold });
            words.Children.Add(titleLine);
            string detail = secret ? "Keep playing to discover this achievement." : item.Description ?? "";
            if (item.Unlocked && item.When.HasValue)
                detail += (detail.Length > 0 ? "   ·   " : "") + "Unlocked " + item.When.Value.ToLocalTime().ToString("d MMM yyyy");
            // No points (Steam's): how many players have it instead.
            if (item.Points <= 0 && item.Rarity.HasValue)
                detail += (detail.Length > 0 ? "   ·   " : "") + $"{item.Rarity.Value:0.#}% of players";
            if (detail.Length > 0)
                words.Children.Add(Words(detail, 13, FontWeights.Normal, Muted));
            // How far along a locked one is, where the game's emulator keeps it.
            if (item.ProgressShare.HasValue)
                words.Children.Add(AchievementViews.AchievementControl.ProgressLine(item, Blue, Edge, Muted, 12));
            row.Children.Add(words);
            return outer;
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
