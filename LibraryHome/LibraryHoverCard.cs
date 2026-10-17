using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using RomM.Models.RomM.Rom;
using RomM.Store;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace RomM.LibraryHome
{
    /// <summary>
    /// Steam's library hover card: resting the pointer on a game in the library shows a card
    /// beside it with the game's name, its screenshots fading one into the next, and the time
    /// played (lately and in total).
    ///
    /// It appears on the Home page's covers (see <see cref="Attach"/>) and on the covers of
    /// Playnite's grid view, which it finds by itself. Not on the list down the left side of
    /// the details view, where Steam has none either.
    /// </summary>
    public sealed class LibraryHoverCard
    {
        private const double CardWidth = 300;
        private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(450);

        private static readonly ILogger s_log = LogManager.GetLogger();
        private static LibraryHoverCard s_instance;

        // Screenshots of RomM games, by rom id, fetched once per run.
        private static readonly Dictionary<int, List<string>> s_shots = new Dictionary<int, List<string>>();
        private static readonly Dictionary<Type, PropertyInfo> s_gameProperty = new Dictionary<Type, PropertyInfo>();

        private readonly RomM _plugin;
        private readonly DispatcherTimer _delay = new DispatcherTimer { Interval = OpenDelay };

        private FrameworkElement _target;
        private Game _game;
        private Popup _popup;
        private FadeSlides _slides;

        private LibraryHoverCard(RomM plugin)
        {
            _plugin = plugin;
            _delay.Tick += (s, e) =>
            {
                _delay.Stop();
                if (_target != null && _target.IsMouseOver && _target.IsVisible && _game != null)
                    Open();
            };
        }

        /// <summary>
        /// Starts the cards. Called once the main window exists: from then on every cover in
        /// Playnite's grid view shows one.
        /// </summary>
        public static void Install(RomM plugin, Window window)
        {
            if (s_instance != null || plugin == null)
                return;
            s_instance = new LibraryHoverCard(plugin);

            EventManager.RegisterClassHandler(typeof(ListBoxItem), UIElement.MouseEnterEvent, new MouseEventHandler(OnItemEnter));
            EventManager.RegisterClassHandler(typeof(ListBoxItem), UIElement.MouseLeaveEvent, new MouseEventHandler(OnItemLeave));
            EventManager.RegisterClassHandler(typeof(ListBoxItem), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((s, e) => s_instance.Close()));
            // Also when the wheel was already taken (smooth scrolling handles it before the item sees it).
            EventManager.RegisterClassHandler(typeof(ListBoxItem), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((s, e) => s_instance.Close()), true);

            if (window != null)
            {
                window.Deactivated += (s, e) => s_instance.Close();
                window.LocationChanged += (s, e) => s_instance.Close();
            }
        }

        /// <summary>Gives <paramref name="element"/> (a cover on the Home page) the card of <paramref name="game"/>.</summary>
        public static void Attach(FrameworkElement element, Game game)
        {
            if (element == null || game == null)
                return;
            element.MouseEnter += (s, e) => s_instance?.Hover(element, game);
            element.MouseLeave += (s, e) => s_instance?.Leave(element);
            element.PreviewMouseDown += (s, e) => s_instance?.Close();
            element.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((s, e) => s_instance?.Close()), true);
        }

        // ---- Playnite's grid view -------------------------------------------------------------

        private static void OnItemEnter(object sender, MouseEventArgs e)
        {
            try
            {
                if (!(sender is ListBoxItem item) || s_instance == null || !InGridView(item))
                    return;
                Game game = GameOf(item.DataContext);
                if (game != null)
                    s_instance.Hover(item, game);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Hover] {ex.Message}");
            }
        }

        private static void OnItemLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement item)
                s_instance?.Leave(item);
        }

        // Only covers in the grid view: not the details view's list, and not the plugin's or
        // Playnite's other lists (menus, settings, filters).
        private static bool InGridView(DependencyObject item)
        {
            DependencyObject node = item;
            for (int depth = 0; node != null && depth < 40; depth++)
            {
                string name = node.GetType().Name;
                if (name == "LibraryGridView")
                    return true;
                if (name == "LibraryDetailsView" || name == "LibraryListView" || node is Window)
                    return false;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        // A list item's game: the item itself, or the "Game" of Playnite's wrapper around it.
        private static Game GameOf(object data)
        {
            if (data == null)
                return null;
            if (data is Game game)
                return game;

            Type type = data.GetType();
            if (!s_gameProperty.TryGetValue(type, out PropertyInfo property))
            {
                property = type.GetProperty("Game", BindingFlags.Public | BindingFlags.Instance);
                if (property != null && !typeof(Game).IsAssignableFrom(property.PropertyType))
                    property = null;
                s_gameProperty[type] = property;
            }
            return property?.GetValue(data) as Game;
        }

        // ---- Showing --------------------------------------------------------------------------

        private void Hover(FrameworkElement target, Game game)
        {
            Close();
            _target = target;
            _game = game;
            _delay.Stop();
            _delay.Start();
        }

        private void Leave(FrameworkElement target)
        {
            if (ReferenceEquals(_target, target))
            {
                _delay.Stop();
                _target = null;
                Close();
            }
        }

        private void Close()
        {
            _delay.Stop();
            _slides?.Stop();
            _slides = null;
            if (_popup != null)
            {
                _popup.IsOpen = false;
                _popup = null;
            }
        }

        private static TextBlock Text(string text, double size, bool bold, double opacity = 1)
        {
            return new TextBlock
            {
                Text = text ?? "",
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brushes.White,
                Opacity = opacity,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        // "13 min", "15.1 hrs".
        private static string PlayTime(ulong seconds)
        {
            if (seconds < 3600)
                return $"{Math.Max(1, seconds / 60)} min";
            return $"{seconds / 3600.0:0.0} hrs";
        }

        private void Open()
        {
            Game game = _game;
            try
            {
                var body = new StackPanel();

                var title = Text(game.Name, 15, true);
                title.Margin = new Thickness(12, 10, 12, 9);
                body.Children.Add(title);

                // The screenshots, one fading into the next.
                _slides = new FadeSlides(Math.Round(CardWidth * 9 / 16), 600);
                body.Children.Add(_slides.Stage);

                var facts = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
                facts.Children.Add(Text("TIME PLAYED", 11, false, 0.6));
                ulong lately = _plugin.Sessions?.PlayedWithin(game.Id, 14) ?? 0;
                if (game.Playtime == 0 && lately == 0)
                {
                    facts.Children.Add(Text("You haven't played this yet", 13, false, 0.85));
                }
                else
                {
                    if (lately > 0)
                        facts.Children.Add(Text("Last two weeks: " + PlayTime(lately), 13, false, 0.85));
                    facts.Children.Add(Text("Total: " + PlayTime(Math.Max(game.Playtime, lately)), 13, false, 0.85));
                }

                var achievements = _plugin.AchievementStore?.Get(game.Id);
                if (achievements != null && achievements.Total > 0)
                {
                    var head = Text("ACHIEVEMENTS", 11, false, 0.6);
                    head.Margin = new Thickness(0, 10, 0, 0);
                    facts.Children.Add(head);
                    facts.Children.Add(Text($"{achievements.Unlocked} / {achievements.Total}", 13, false, 0.85));
                    var bar = new Grid { Height = 4, Margin = new Thickness(0, 4, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) };
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(achievements.Unlocked, GridUnitType.Star) });
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, achievements.Total - achievements.Unlocked), GridUnitType.Star) });
                    bar.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)) });
                    facts.Children.Add(bar);
                }
                body.Children.Add(facts);

                var card = new Border
                {
                    Width = CardWidth,
                    Background = new LinearGradientBrush(Color.FromRgb(0x4A, 0x4F, 0x5C), Color.FromRgb(0x2B, 0x2F, 0x38), 90),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    IsHitTestVisible = false,
                    Child = body,
                    Margin = new Thickness(0, 0, 14, 14),
                    Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.6, Color = Colors.Black },
                };

                // Beside the cover, to the right, or the left where the right has no room.
                _popup = new Popup
                {
                    PlacementTarget = _target,
                    Placement = PlacementMode.Right,
                    HorizontalOffset = 6,
                    AllowsTransparency = true,
                    StaysOpen = true,
                    IsHitTestVisible = false,
                    Child = card,
                };
                _popup.IsOpen = true;

                // Until the screenshots arrive, the game's background (or cover) stands in.
                _slides.ShowNow(LocalImage(game.BackgroundImage) ?? LocalImage(game.CoverImage));
                LoadScreenshots(game);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Hover] Could not show the card for {game?.Name}: {ex.Message}");
            }
        }

        private string LocalImage(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : _plugin.Playnite.Database.GetFullFilePath(path);
            }
            catch
            {
                return null;
            }
        }

        // A RomM game's screenshots come from the server (cached for the session); any other
        // game just keeps its background.
        private void LoadScreenshots(Game game)
        {
            if (game.PluginId != _plugin.Id || !RomMGameId.TryParse(game.GameId, out int romId, out string _))
                return;

            List<string> known;
            lock (s_shots)
                s_shots.TryGetValue(romId, out known);
            if (known != null)
            {
                Begin(game, known);
                return;
            }

            string host = _plugin.Settings?.RomMHost;
            if (string.IsNullOrEmpty(host))
                return;
            host = host.TrimEnd('/');

            Task.Run(() =>
            {
                var shots = new List<string>();
                try
                {
                    using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/roms/{romId}").GetAwaiter().GetResult())
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            var rom = JsonConvert.DeserializeObject<RomMRom>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                            foreach (string path in rom?.MergedScreenshots ?? new List<string>())
                            {
                                if (!string.IsNullOrEmpty(path))
                                    shots.Add(path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : host + "/" + path.TrimStart('/'));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Hover] Could not fetch the screenshots of {game.Name}: {ex.Message}");
                    return;
                }

                lock (s_shots)
                    s_shots[romId] = shots;
                _plugin.Playnite.MainView.UIDispatcher.BeginInvoke(new Action(() => Begin(game, shots)));
            });
        }

        // Starts the slideshow, if the card is still the one for this game.
        private void Begin(Game game, List<string> shots)
        {
            if (_popup == null || !_popup.IsOpen || _slides == null || _game == null || _game.Id != game.Id || shots.Count == 0)
                return;
            _slides.Play(shots, false);
        }
    }
}
