using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using RomM.Downloads;
using RomM.Games;
using RomM.Models.RomM.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RomM.Store
{
    /// <summary>
    /// Steam's one button for a game, in the order a game gets there:
    ///
    ///   Install Emulator  (nothing here plays its platform, or its emulator is gone)
    ///   Emulator Unsupported (as above, and the emulator setup has no emulator for its platform)
    ///   Emulator Unavailable (as above, and a file its setup needs is not on the RomM server)
    ///   Add to Library    (Store only: not in this account's library yet)
    ///   Install           (Store: View in Library, which goes to the game's page there)
    ///   Downloading 42%   (pointed at: Cancel, and a Pause button slides out to its right)
    ///   Update            (blue: the game changed on the server, Games/GameUpdates; its drop-down
    ///                     has "Play without updating")
    ///   Play
    ///   Launching...      (from the click until the game has started)
    ///   Running
    ///
    /// On the Store's game page (for a RomM game, in the library or not) and, as the custom
    /// element RomM_PlayButton, on the library's game page (for any game: games of other
    /// libraries go Install, Play as Playnite's own button would). It follows the game as it
    /// changes: checked twice a second, the emulator every few seconds (it reads files).
    ///
    /// In the library, a game with copies on other platforms (Games/LibraryVersions) has a
    /// drop-down at the button's right end, split off by a thin line: one button with a part.
    /// It lists the other copies with what each can do now (Play, Install, its download);
    /// picking one swaps the library over to that copy, whose own button then plays or
    /// installs it. Its own platform's icon is then drawn left of the words, so which copy the
    /// button plays is plain (PlatformIcons).
    ///
    /// Play turns into "Launching..." at once: the button is drawn so before Playnite is asked to
    /// start the game, whose start (the cloud saves brought down first, an archive unpacked)
    /// can hold the window a moment.
    /// </summary>
    public class GameActionButton : PluginUserControl
    {
        private enum Kind { None, InstallEmulator, EmulatorUnsupported, EmulatorUnavailable, AddToLibrary, Install, Downloading, Uninstalling, Update, Play, Launching, Running }

        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Color Green = Color.FromRgb(0x5C, 0x7E, 0x10);
        private static readonly Color GreenHover = Color.FromRgb(0x77, 0xA3, 0x16);
        private static readonly Color Blue = Color.FromRgb(0x1A, 0x6E, 0xC4);
        private static readonly Color BlueHover = Color.FromRgb(0x2A, 0x85, 0xE0);
        private static readonly Color Grey = Color.FromRgb(0x3D, 0x44, 0x50);
        private static readonly Color GreyHover = Color.FromRgb(0x52, 0x5B, 0x69);
        private static readonly Color Red = Color.FromRgb(0xA8, 0x32, 0x32);

        private readonly RomM _plugin;
        private readonly Border _box;
        private readonly TextBlock _text;
        // The platform's icon: in greys, lightened toward white (PlatformIcons.Grey), so it sits on
        // the button like its words and keeps its detail; GreyPlatformIcons off draws it in its
        // own colours.
        private const bool GreyPlatformIcons = true;
        private const double PlatformIconSize = 26;
        private readonly Image _platformIcon;
        private readonly DispatcherTimer _timer;

        // Pause / Resume, beside the button while a download is pointed at: slides out of its
        // right edge (its holder's width animated), and back in when the pointer leaves.
        private const double PauseWidth = 48;
        private readonly Border _pauseHolder;
        private readonly Border _pause;
        private readonly TextBlock _pauseGlyph;
        private bool _over;          // the pointer is on the button or the Pause beside it
        private bool _pauseHover;
        private bool _pauseShown;

        // The other platforms' copies (library only): the drop-down at the right end.
        private const double DropWidth = 34;
        private readonly Border _drop;
        private readonly TextBlock _dropGlyph;
        private bool _dropHover;
        private int _others;

        // The Store's game (null in the library), and what the Store does to add it.
        private readonly int _romId;
        private readonly int _platformId;
        private readonly Action _addToLibrary;
        private readonly Action _kindChanged;

        private Guid _gameId;
        private Kind _kind = Kind.None;
        private bool _hover;
        private bool _busy;
        private bool _needsEmulator;
        private bool _noEmulator;      // needed, and none can be set up for the platform
        private bool _filesMissing;    // needed, and its setup lacks a file on the server
        private bool _updateWaiting;   // installed, and changed on the server since (GameUpdates)
        private DateTime _emulatorCheckedAt = DateTime.MinValue;

        /// <summary>In the library: follows the selected game (GameContextChanged).</summary>
        public GameActionButton(RomM plugin) : this(plugin, 0, 0, null, null)
        {
        }

        /// <summary>
        /// On the Store's page for a RomM game. <paramref name="kindChanged"/> runs when the
        /// button moves on to another step, for the page to redraw what goes with it.
        /// </summary>
        public GameActionButton(RomM plugin, int romId, int platformId, Action addToLibrary, Action kindChanged)
        {
            _plugin = plugin;
            _romId = romId;
            _platformId = platformId;
            _addToLibrary = addToLibrary;
            _kindChanged = kindChanged;

            _text = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Taller than the words, without making the button taller (its margins give the room back).
            var iconMargin = new Thickness(0, -5, 10, -5);
            _platformIcon = new Image
            {
                Width = PlatformIconSize,
                Height = PlatformIconSize,
                Margin = iconMargin,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            RenderOptions.SetBitmapScalingMode(_platformIcon, BitmapScalingMode.HighQuality);
            var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            label.Children.Add(_platformIcon);
            label.Children.Add(_text);
            _box = new Border
            {
                MinWidth = 150,
                MinHeight = 34,
                Padding = new Thickness(18, 8, 18, 8),
                CornerRadius = new CornerRadius(2),
                // A long label ("Emulator Unsupported") is made smaller to fit the button
                // rather than cut short; a short one keeps its size.
                Child = new Viewbox { Child = label, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly },
            };
            _box.MouseEnter += (s, e) => { _hover = true; Paint(); };
            _box.MouseLeave += (s, e) => { _hover = false; Paint(); };
            _box.MouseLeftButtonUp += (s, e) => { e.Handled = true; Click(); };

            _pauseGlyph = new TextBlock
            {
                Text = "\uE769",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // The button's own right end, split off by a thin line: one button that grows a part.
            _pause = new Border
            {
                Width = PauseWidth,
                CornerRadius = new CornerRadius(0, 2, 2, 0),
                BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
                Background = new SolidColorBrush(Grey),
                Cursor = Cursors.Hand,
                Child = _pauseGlyph,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _pause.MouseEnter += (s, e) => { _pauseHover = true; Paint(); };
            _pause.MouseLeave += (s, e) => { _pauseHover = false; Paint(); };
            _pause.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                try
                {
                    Game game = _gameId == Guid.Empty ? null : _plugin.Playnite.Database.Games.Get(_gameId);
                    if (game != null)
                        _plugin.DownloadQueueController?.TogglePause(game.Id);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[GameActionButton] Pause failed: {ex.Message}");
                }
                Paint();
            };
            _pauseHolder = new Border { Width = 0, ClipToBounds = true, Child = _pause };

            _dropGlyph = new TextBlock
            {
                Text = "\uE70D",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _drop = new Border
            {
                Width = DropWidth,
                CornerRadius = new CornerRadius(0, 2, 2, 0),
                BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
                Cursor = Cursors.Hand,
                Child = _dropGlyph,
                Visibility = Visibility.Collapsed,
                ToolTip = "Other platforms",
            };
            _drop.MouseEnter += (s, e) => { _dropHover = true; Paint(); };
            _drop.MouseLeave += (s, e) => { _dropHover = false; Paint(); };
            _drop.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                OpenVersions();
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
            row.Children.Add(_box);
            row.Children.Add(_pauseHolder);
            row.Children.Add(_drop);
            row.MouseEnter += (s, e) => { _over = true; Paint(); };
            row.MouseLeave += (s, e) => { _over = false; _pauseHover = false; Paint(); };
            Content = row;
            Focusable = false;

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (s, e) => Refresh(false);
            Loaded += (s, e) => { _timer.Start(); Refresh(true); };
            Unloaded += (s, e) => _timer.Stop();

            if (_romId > 0)
            {
                Game present = LibraryGame();
                _gameId = present?.Id ?? Guid.Empty;
                Refresh(true);
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            if (_romId > 0)
                return;
            _gameId = newContext?.Id ?? Guid.Empty;
            _hover = false;
            Refresh(true);
        }

        // The Store's game in this account's library, or null.
        private Game LibraryGame()
        {
            Game game = _plugin.Playnite.Database.Games.FirstOrDefault(g =>
                g.PluginId == _plugin.Id && RomMGameId.TryParse(g.GameId, out int id, out string _) && id == _romId);
            return game != null && _plugin.InAccountLibrary(game) ? game : null;
        }

        private Kind Work(Game game)
        {
            if (game == null)
            {
                if (_romId <= 0)
                    return Kind.None;
                return _needsEmulator ? EmulatorKind() : Kind.AddToLibrary;
            }
            // Installing, or its update downloading (which the game itself does not show).
            if (game.IsInstalling || (game.IsInstalled && _plugin.DownloadQueueController?.Find(game.Id) != null))
                return Kind.Downloading;
            if (game.IsUninstalling)
                return Kind.Uninstalling;
            if (game.IsRunning)
                return Kind.Running;
            if (game.IsLaunching || _plugin.IsLaunchPending(game.Id))
                return Kind.Launching;
            if (_needsEmulator)
                return EmulatorKind();
            if (game.IsInstalled && _updateWaiting)
                return Kind.Update;
            return game.IsInstalled ? Kind.Play : Kind.Install;
        }

        private Kind EmulatorKind() => _noEmulator ? Kind.EmulatorUnsupported : _filesMissing ? Kind.EmulatorUnavailable : Kind.InstallEmulator;

        /// <param name="full">Also whether its emulator is here (files are read).</param>
        private void Refresh(bool full)
        {
            try
            {
                Game game;
                if (_romId > 0)
                {
                    game = LibraryGame();
                    _gameId = game?.Id ?? Guid.Empty;
                }
                else
                {
                    game = _gameId == Guid.Empty ? null : _plugin.Playnite.Database.Games.Get(_gameId);
                }

                if (full || DateTime.UtcNow - _emulatorCheckedAt > TimeSpan.FromSeconds(4))
                {
                    _emulatorCheckedAt = DateTime.UtcNow;
                    _needsEmulator = game != null
                        ? game.PluginId == _plugin.Id && _plugin.NeedsEmulator(game)
                        : _romId > 0 && !_plugin.PlatformReady(_platformId);
                    int platformId = _romId > 0 ? _platformId : game != null ? _plugin.PlatformIdOf(game) : 0;
                    _noEmulator = _needsEmulator && platformId > 0 && !_plugin.EmulatorAvailable(platformId);
                    _filesMissing = _needsEmulator && !_noEmulator && platformId > 0 && _plugin.FilesMissing(platformId) == true;
                    // An update waiting (read from its record, so not twice a second).
                    _updateWaiting = game != null && game.PluginId == _plugin.Id && _plugin.Updates?.Waiting(game) != null;
                }

                // The other platforms' copies, in the library (the Store lists them on its page).
                _others = _romId <= 0 && game != null && _plugin.Versions != null ? _plugin.Versions.Others(game).Count : 0;

                Kind kind = Work(game);
                // The Store redraws its page when the game comes into the library or its platform
                // gets an emulator (what goes with the button changes); not for each later step.
                bool moved = kind != _kind && _kind != Kind.None
                             && (IsBeforeLibrary(_kind) || IsBeforeLibrary(kind));
                _kind = kind;
                Visibility = kind == Kind.None ? Visibility.Collapsed : Visibility.Visible;
                Paint(game);
                if (moved)
                    _kindChanged?.Invoke();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[GameActionButton] {ex.Message}");
            }
        }

        private static bool IsBeforeLibrary(Kind kind) => kind == Kind.InstallEmulator || kind == Kind.EmulatorUnsupported || kind == Kind.EmulatorUnavailable || kind == Kind.AddToLibrary;

        private void Paint() => Paint(_gameId == Guid.Empty ? null : _plugin.Playnite.Database.Games.Get(_gameId));

        private void Paint(Game game)
        {
            string text;
            Color color;
            bool clickable = !_busy;
            bool canPause = false;
            switch (_kind)
            {
                case Kind.InstallEmulator:
                    text = _busy ? "Installing Emulator..." : "Install Emulator";
                    color = _hover && !_busy ? BlueHover : Blue;
                    break;
                case Kind.EmulatorUnsupported:
                    text = "Emulator Unsupported";
                    color = Grey;
                    clickable = false;
                    break;
                case Kind.EmulatorUnavailable:
                    text = "Emulator Unavailable";
                    color = Grey;
                    clickable = false;
                    break;
                case Kind.AddToLibrary:
                    text = _busy ? "Adding..." : "Add to Library";
                    color = _hover && !_busy ? GreenHover : Green;
                    break;
                case Kind.Install:
                    // The Store sends the game to the library, where it is installed from.
                    text = _romId > 0 ? "View in Library" : "Install";
                    color = _hover ? BlueHover : Blue;
                    break;
                case Kind.Downloading:
                    DownloadQueueItem item = game == null ? null : _plugin.DownloadQueueController?.Find(game.Id);
                    clickable = item != null;
                    canPause = item != null && item.CanPause;
                    if (canPause)
                    {
                        _pauseGlyph.Text = item.PauseRequested ? "\uE768" : "\uE769";
                        _pause.ToolTip = item.PauseButtonText;
                        _pause.Background = new SolidColorBrush(_pauseHover ? GreyHover : Grey);
                    }
                    if (_hover && item != null)
                    {
                        text = "Cancel";
                        color = Red;
                    }
                    else
                    {
                        text = DownloadText(item);
                        color = Grey;
                    }
                    break;
                case Kind.Uninstalling:
                    text = "Uninstalling...";
                    color = Grey;
                    clickable = false;
                    break;
                case Kind.Launching:
                    text = "Launching...";
                    color = Grey;
                    clickable = false;
                    break;
                case Kind.Running:
                    text = "Running";
                    color = Grey;
                    clickable = false;
                    break;
                case Kind.Update:
                    text = "Update";
                    color = _hover ? BlueHover : Blue;
                    break;
                case Kind.Play:
                    text = "Play";
                    color = _hover ? GreenHover : Green;
                    break;
                default:
                    text = "";
                    color = Grey;
                    clickable = false;
                    break;
            }
            _text.Text = text;
            // Which platform's copy this is, where there are others.
            ImageSource icon = null;
            if (_others > 0 && game != null && (_kind == Kind.Play || _kind == Kind.Install || _kind == Kind.Update || _kind == Kind.Launching || _kind == Kind.Running))
            {
                Guid shown = game.Id;
                icon = PlatformIcons.For(_plugin, game, () =>
                {
                    if (_gameId == shown)
                        Paint();
                });
            }
            if (GreyPlatformIcons && icon != null)
                icon = PlatformIcons.Grey(icon);
            _platformIcon.Source = icon;
            _platformIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
            _box.Background = new SolidColorBrush(color);
            _box.Cursor = clickable ? Cursors.Hand : Cursors.Arrow;
            SlidePause(_over && canPause);

            // The drop-down: the button's own colour (a shade lighter pointed at), joined to it.
            // Its other copies, and with an update waiting, playing without it.
            bool drop = _others > 0 || _kind == Kind.Update;
            _drop.ToolTip = _kind == Kind.Update ? "More options" : "Other platforms";
            _drop.Visibility = drop ? Visibility.Visible : Visibility.Collapsed;
            if (drop)
            {
                Color dropColor = color;
                if (_dropHover)
                    dropColor = color == Green ? GreenHover : color == Blue ? BlueHover : GreyHover;
                _drop.Background = new SolidColorBrush(dropColor);
            }
            if (!_pauseShown)
                _box.CornerRadius = drop ? new CornerRadius(2, 0, 0, 2) : new CornerRadius(2);
            // Pause between the button and the drop-down: square on both sides then.
            _pause.CornerRadius = drop ? new CornerRadius(0) : new CornerRadius(0, 2, 2, 0);
        }

        private void SlidePause(bool show)
        {
            if (show == _pauseShown)
                return;
            _pauseShown = show;
            if (show)
            {
                // Square where the part joins, and the button keeps its width (it grows, rather
                // than giving up room to the part).
                _box.CornerRadius = new CornerRadius(2, 0, 0, 2);
                _box.MinWidth = Math.Max(150, _box.ActualWidth);
            }
            var slide = new DoubleAnimation(show ? PauseWidth : 0, TimeSpan.FromMilliseconds(show ? 160 : 120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            if (!show)
            {
                slide.Completed += (s, e) =>
                {
                    if (_pauseShown)
                        return;
                    _box.CornerRadius = _others > 0 || _kind == Kind.Update ? new CornerRadius(2, 0, 0, 2) : new CornerRadius(2);
                    _box.MinWidth = 150;
                };
            }
            _pauseHolder.BeginAnimation(WidthProperty, slide);
        }

        private static readonly Thickness EntryMargin = new Thickness(6, 6, 18, 6);

        // An entry of the drop-down as tall as what is in it (the theme's entries are one line,
        // 28 pixels, which cut two lines off), lit when pointed at as the theme's are.
        private static ControlTemplate s_entryTemplate;

        private static ControlTemplate EntryTemplate()
        {
            if (s_entryTemplate != null)
                return s_entryTemplate;
            var border = new FrameworkElementFactory(typeof(Border), "Lit");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, false);
            border.AppendChild(content);
            var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = border };
            var lit = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            lit.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ControlHoverBackgroundBrush"), "Lit"));
            template.Triggers.Add(lit);
            s_entryTemplate = template;
            return template;
        }

        private static MenuItem Entry(UIElement header)
        {
            var entry = new MenuItem { Header = header, Template = EntryTemplate() };
            entry.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return entry;
        }

        // The other copies, each with what it can do now.
        private void OpenVersions()
        {
            Game game = _gameId == Guid.Empty ? null : _plugin.Playnite.Database.Games.Get(_gameId);
            if (game == null)
                return;
            // Centred under the arrow, a little below it, with room around its entries.
            var menu = new ContextMenu
            {
                PlacementTarget = _drop,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
                Padding = new Thickness(4, 6, 4, 6),
                CustomPopupPlacementCallback = (popup, target, offset) => new[]
                {
                    new System.Windows.Controls.Primitives.CustomPopupPlacement(
                        new Point((target.Width - popup.Width) / 2, target.Height + 6),
                        System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
                },
            };
            if (_kind == Kind.Update)
            {
                var words = new StackPanel { Margin = EntryMargin };
                words.Children.Add(new TextBlock { Text = "Play without updating", FontWeight = FontWeights.SemiBold });
                words.Children.Add(new TextBlock { Text = _plugin.Updates?.Waiting(game) ?? "The update waits until next time", FontSize = 11, Opacity = 0.7 });
                MenuItem play = Entry(words);
                play.Click += (s, e) => PlayWithoutUpdating(game);
                menu.Items.Add(play);
                if (_others > 0)
                    menu.Items.Add(new Separator());
            }
            foreach (Game other in _plugin.Versions?.Others(game) ?? new List<Game>())
            {
                string platform = other.Platforms?.FirstOrDefault()?.Name ?? "Other platform";
                string action;
                DownloadQueueItem item = _plugin.DownloadQueueController?.Find(other.Id);
                if (other.IsRunning)
                {
                    action = "Running";
                }
                else if (other.IsInstalling || item != null)
                {
                    action = DownloadText(item);
                }
                else if (other.IsUninstalling)
                {
                    action = "Uninstalling...";
                }
                else
                    action = other.IsInstalled ? "Installed" : "Not installed";

                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(new TextBlock { Text = platform, FontWeight = FontWeights.SemiBold });
                words.Children.Add(new TextBlock { Text = action, FontSize = 11, Opacity = 0.7 });
                // The platform's icon (in its colours here) before its name.
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = EntryMargin };
                ImageSource icon = PlatformIcons.For(_plugin, other, null);
                if (icon != null)
                {
                    var picture = new Image { Source = icon, Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                    RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                    line.Children.Add(picture);
                }
                line.Children.Add(words);
                // Any copy can be gone to, whatever it is doing.
                MenuItem entry = Entry(line);
                Game target = other;
                entry.Click += (s, e) =>
                {
                    try
                    {
                        _plugin.Versions.ShowCopy(game, target);
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[GameActionButton] Could not go to {platform}'s copy: {ex.Message}");
                    }
                };
                menu.Items.Add(entry);
            }
            if (menu.Items.Count > 0)
                menu.IsOpen = true;
        }

        // The game started as it is, its update left waiting (the second time, asked whether to
        // stop checking it for updates: GameUpdates.PlayWithoutUpdating).
        private void PlayWithoutUpdating(Game game)
        {
            try
            {
                _plugin.Updates?.PlayWithoutUpdating(game);
                Start(game.Id, game.Name);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[GameActionButton] Could not start {game.Name} without updating: {ex.Message}");
                Refresh(true);
            }
        }

        // "Launching..." drawn first, then the game started once the window has shown it (a
        // lower priority than drawing): Playnite's start runs on this thread and can hold it.
        private void Start(Guid gameId, string name)
        {
            _plugin.MarkLaunching(gameId);
            Refresh(false);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    _plugin.Playnite.StartGame(gameId);
                    // A cancelled choice of file or action: back to Play.
                    _plugin.ReleaseIfNotStarted(gameId);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[GameActionButton] Could not start {name ?? gameId.ToString()}: {ex.Message}");
                    _plugin.ClearLaunching(gameId);
                    Refresh(true);
                }
            }));
        }

        private static string DownloadText(DownloadQueueItem item)
        {
            if (item == null)
                return "Installing...";
            switch (item.Status)
            {
                case DownloadStatus.Queued:
                    return "Queued";
                case DownloadStatus.Extracting:
                    return "Extracting...";
                case DownloadStatus.Paused:
                    return item.Percent.HasValue ? $"Paused {item.Percent.Value:0}%" : "Paused";
                default:
                    return item.Percent.HasValue ? $"Downloading {item.Percent.Value:0}%" : "Downloading...";
            }
        }

        private void Click()
        {
            if (_busy)
                return;
            try
            {
                Game game = _gameId == Guid.Empty ? null : _plugin.Playnite.Database.Games.Get(_gameId);
                switch (_kind)
                {
                    case Kind.InstallEmulator:
                        _busy = true;
                        Paint(game);
                        try
                        {
                            if (game != null)
                                _plugin.SetUpPlatformFor(game);
                            else
                            {
                                RomMPlatform platform = _plugin.Settings?.RomMPlatforms?.FirstOrDefault(p => p.Id == _platformId);
                                if (platform != null)
                                    _plugin.SetUpPlatform(platform);
                            }
                        }
                        finally
                        {
                            _busy = false;
                        }
                        Refresh(true);
                        break;
                    case Kind.AddToLibrary:
                        _busy = true;
                        Paint(game);
                        // "Adding..." until the Store redraws the page with the game added (or not).
                        _addToLibrary?.Invoke();
                        break;
                    case Kind.Install:
                        if (game != null && _romId > 0)
                        {
                            // The Store: to the game's page in the library, where it is installed.
                            _plugin.ShowGameInLibrary(game.Id);
                        }
                        else if (game != null)
                            _plugin.Playnite.InstallGame(game.Id);
                        Refresh(false);
                        break;
                    case Kind.Downloading:
                        if (game != null && _plugin.DownloadQueueController?.Find(game.Id) != null)
                            _plugin.DownloadQueueController.Cancel(game.Id);
                        Refresh(false);
                        break;
                    case Kind.Update:
                        if (game != null)
                        {
                            _plugin.Updates?.Start(game, verify: false);
                            _updateWaiting = false;   // downloading now; read again with the next full look
                        }
                        Refresh(true);
                        break;
                    case Kind.Play:
                        if (game != null)
                            Start(game.Id, null);
                        break;
                }
            }
            catch (Exception ex)
            {
                _busy = false;
                s_log.Warn($"[GameActionButton] {_kind} failed: {ex.Message}");
                Refresh(true);
            }
        }
    }
}
