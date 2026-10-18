using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Media;

namespace RomM.Games
{
    /// <summary>
    /// The custom element RomM_ControllerStatus: Steam's "Controller" panel for a game's library
    /// page. For emulated games it says what follows; for PC games (any library's) how well the
    /// game itself takes a controller, as Steam and PCGamingWiki record it (PcControllerSupport),
    /// with whether one is connected.
    /// Says, as it changes, whether a controller is connected and whether the game's emulator
    /// will know it (ControllerMonitor):
    ///
    ///   Supports your &lt;controller&gt;         one is connected that the game's emulator takes
    ///                                       with no setting up (see Ready): for most, any
    ///                                       controller SDL knows the layout of (they read
    ///                                       controllers through SDL; Xenia Canary too: its "any"
    ///                                       input skips XInput for SDL)
    ///   &lt;controller&gt; needs setting up once   known, but the emulator ties its controls to a
    ///                                       named device (Azahar, Eden, Dolphin's Wii...)
    ///   &lt;controller&gt; may not work            connected, but its layout is not known
    ///   No controller connected
    ///
    /// Drawn as the page's Details panel: a title, then a card. Or (RomM_ControllerStatusBar,
    /// compact) as Steam's line in the game's bar: the icon and the headline, the card opening
    /// under it when clicked.
    /// </summary>
    public class ControllerStatusControl : PluginUserControl
    {
        private static readonly Guid SteamLibrary = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
        private const string PadGlyph = "";
        private static readonly Brush Warning = Frozen(Color.FromRgb(0xE5, 0xB1, 0x43));
        private static readonly Brush Good = Frozen(Color.FromRgb(0x66, 0xC0, 0xF4));

        private readonly RomM _plugin;
        private readonly TextBlock _icon;
        private readonly TextBlock _headline;
        private readonly TextBlock _detail;
        private readonly Border _tipRow;
        private readonly TextBlock _tipHeader;
        private readonly StackPanel _tipList;
        private readonly TextBlock _source;
        private Game _game;
        private bool _listening;
        private PadInfo _pcInfo;          // the PC game's support, once known
        private bool _pcAsked;

        private readonly bool _compact;
        private readonly TextBlock _barIcon;
        private readonly TextBlock _barText;

        public ControllerStatusControl(RomM plugin, bool compact = false)
        {
            _compact = compact;
            _plugin = plugin;
            Focusable = false;

            var title = new TextBlock { Text = "Controller" };
            title.SetResourceReference(StyleProperty, "DetailViewSectionTitleTextBlockStyle");

            _icon = new TextBlock
            {
                Text = PadGlyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 30,
                Margin = new Thickness(0, 2, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            _headline = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            _headline.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
            _headline.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            _detail.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");

            // Where a PC game's support comes from, with its PCGamingWiki page when it has one.
            _source = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11, Visibility = Visibility.Collapsed };
            _source.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(_headline);
            words.Children.Add(_detail);
            words.Children.Add(_source);
            var row = new DockPanel();
            DockPanel.SetDock(_icon, Dock.Left);
            row.Children.Add(_icon);
            row.Children.Add(words);

            // A better controller for the platform, when there is one and it is not the one connected.
            var tipIcon = new TextBlock
            {
                Text = "\uE82F",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                Margin = new Thickness(7, 1, 21, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            tipIcon.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
            _tipHeader = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            _tipHeader.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
            _tipHeader.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _tipList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var tipWords = new StackPanel();
            tipWords.Children.Add(_tipHeader);
            tipWords.Children.Add(_tipList);
            var tipLine = new DockPanel();
            DockPanel.SetDock(tipIcon, Dock.Left);
            tipLine.Children.Add(tipIcon);
            tipLine.Children.Add(tipWords);
            _tipRow = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), Child = tipLine, Visibility = Visibility.Collapsed };
            _tipRow.SetResourceReference(Border.BorderBrushProperty, "PanelSeparatorBrush");
            var stack = new StackPanel();
            stack.Children.Add(row);
            stack.Children.Add(_tipRow);

            var card = new Border { Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 12, 0, 0), Child = stack };
            card.SetResourceReference(StyleProperty, "DetailViewCardBorder");
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");

            if (compact)
            {
                // In the bar: the icon and the headline; the full card in a popup below.
                _barIcon = new TextBlock
                {
                    Text = PadGlyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 20,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _barText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 };
                _barText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(_barIcon);
                line.Children.Add(_barText);
                var bar = new Border { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(6, 4, 6, 4), Child = line, VerticalAlignment = VerticalAlignment.Center };

                card.Margin = new Thickness(0);
                card.Width = 380;
                var sheet = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Child = card,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.6 },
                    Margin = new Thickness(0, 0, 12, 12),
                };
                var popup = new System.Windows.Controls.Primitives.Popup
                {
                    PlacementTarget = bar,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    StaysOpen = false,
                    AllowsTransparency = true,
                    Child = sheet,
                    VerticalOffset = 6,
                };
                bar.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    popup.IsOpen = !popup.IsOpen;
                };
                bar.MouseEnter += (s, e) => bar.Opacity = 0.85;
                bar.MouseLeave += (s, e) => bar.Opacity = 1;
                var host = new Grid();
                host.Children.Add(bar);
                host.Children.Add(popup);
                Content = host;
            }
            else
            {
                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };
                panel.Children.Add(title);
                panel.Children.Add(card);
                Content = panel;
            }
            Visibility = Visibility.Collapsed;

            Loaded += (s, e) => StartListening();
            Unloaded += (s, e) => StopListening();
        }

        private void StartListening()
        {
            if (_listening)
                return;
            _listening = true;
            ControllerMonitor.Changed += OnChanged;
            ControllerMonitor.Listen(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            Paint();
        }

        private void StopListening()
        {
            if (!_listening)
                return;
            _listening = false;
            ControllerMonitor.Changed -= OnChanged;
            ControllerMonitor.StopListening();
        }

        private void OnChanged() => Dispatcher.BeginInvoke(new Action(Paint));

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _game = newContext;
            _emulated = Emulated(newContext, out _emulatorId);
            _pcInfo = null;
            _pcAsked = false;
            Paint();
        }

        // Whether the game is one an emulator here plays: a RomM game whose RomM platform an
        // emulator can be set up for. Asked once per game (it reads the platform's setup).
        private bool _emulated;
        // The emulator that plays it ("duckstation", "retroarch"...), when known.
        private string _emulatorId;

        private bool Emulated(Game game, out string emulatorId)
        {
            emulatorId = null;
            try
            {
                if (game == null || game.PluginId != _plugin.Id)
                    return false;
                int platformId = _plugin.PlatformIdOf(game);
                if (platformId <= 0)
                    return false;
                var plan = _plugin.EmulatorSetup?.PlanFor(platformId);
                emulatorId = plan?.Definition?.Id;
                return plan != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The emulators here that read controllers through SDL's game controller support, so
        /// also know the controllers in the community's list (ControllerDatabase, handed to them
        /// through the environment). Xenia Canary is one: its default input ("any") skips XInput
        /// for SDL (xenia_main.cc). PPSSPP (XInput and DirectInput on Windows) and RetroArch (its
        /// own controller profiles) are not counted: for them a controller only SDL's list knows
        /// is not said to work.
        /// </summary>
        private static readonly HashSet<string> SdlEmulators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "duckstation", "pcsx2", "flycast", "xemu", "vita3k", "azahar", "cemu", "eden", "xenia",
        };

        private bool UsesSdl => _emulatorId != null && SdlEmulators.Contains(_emulatorId);

        // A Windows (PC) game. Not "PC Engine", "PC-FX" or "PC-98", which are emulated.
        private static bool IsPc(Game game) =>
            game.Platforms?.Any(p => p != null && (string.Equals(p.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase)
                || (p.Name ?? "").IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals((p.Name ?? "").Trim(), "PC", StringComparison.OrdinalIgnoreCase)
                || (p.Name ?? "").StartsWith("PC (", StringComparison.OrdinalIgnoreCase))) == true;

        private void Paint()
        {
            PaintCard();
            if (_compact)
            {
                _barText.Text = _headline.Text;
                _barIcon.Foreground = _icon.Foreground;
            }
        }

        private void PaintCard()
        {
            Game game = _game;
            if (game != null && game.Platforms != null && IsPc(game))
            {
                Visibility = Visibility.Visible;
                PaintPc(game);
                return;
            }
            _source.Visibility = Visibility.Collapsed;
            // No card where no emulator plays the game (a platform not supported, Android say):
            // nothing would read the controller.
            if (game == null || game.Platforms == null || game.Platforms.Count == 0 || !_emulated)
            {
                Visibility = Visibility.Collapsed;
                return;
            }
            Visibility = Visibility.Visible;

            var pads = ControllerMonitor.Current;
            string spec = game.Platforms.Select(p => p?.SpecificationId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? "";

            Better better = BetterFor(spec);
            bool hasBest = better != null && pads.Any(better.Has);
            ShowBetter(better != null && !hasBest ? better : null);

            bool sdl = UsesSdl;
            Func<ConnectedPad, bool> ready = Ready(spec, sdl);
            ConnectedPad good = pads.FirstOrDefault(ready);
            ConnectedPad known = pads.FirstOrDefault(p => sdl ? p.KnownToSdl : p.Known);
            if (good != null)
            {
                _icon.Foreground = Good;
                bool best = better == null || hasBest;
                _headline.Text = best ? $"Your {Friendly(good.Name)} works with this game" : $"Your {Friendly(good.Name)} probably works with this game";
                // Known from the community's list, not SDL's own: said, as that is less certain.
                _detail.Text = good.Known ? "" : "Its button layout comes from the community controller list (SDL_GameControllerDB).";
            }
            else if (known != null)
            {
                // Known to SDL, but this emulator ties its controls to one named device that
                // cannot be set for it ahead of time (EmulatorControllers).
                _icon.Foreground = Warning;
                _headline.Text = $"Your {Friendly(known.Name)} needs setting up once";
                _detail.Text = $"Choose it in {EmulatorOf(spec)}'s controller settings.";
            }
            else if (pads.Count > 0)
            {
                _icon.Foreground = Warning;
                _headline.Text = $"Your {Friendly(pads[0].Name)} may not work";
                _detail.Text = "Its buttons may be mixed up or missing.";
            }
            else
            {
                _icon.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
                _headline.Text = "No controller connected";
                _detail.Text = "";
            }
            _detail.Visibility = _detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- PC games ---------------------------------------------------------------------------

        private void PaintPc(Game game)
        {
            ShowBetter(null);
            if (!_pcAsked)
            {
                _pcAsked = true;
                AskPc(game);
            }

            var pads = ControllerMonitor.Current;
            ConnectedPad pad = pads.FirstOrDefault(p => p.KnownToSdl) ?? pads.FirstOrDefault();
            string yours = pad != null ? $"Your {Friendly(pad.Name)}" : null;
            PadSupport support = _pcInfo?.Support ?? PadSupport.Unknown;
            // A game reading Xbox controllers only sees a PlayStation or Switch one as nothing.
            bool unseen = pad != null && !pad.XInput && _pcInfo?.XInputOnly == true
                          && (support == PadSupport.Full || support == PadSupport.Partial);

            string detail;
            switch (support)
            {
                case PadSupport.Full:
                    detail = "Full controller support";
                    break;
                case PadSupport.Partial:
                    detail = "Partial controller support: some of it needs the keyboard and mouse";
                    break;
                case PadSupport.NeedsFix:
                    detail = "Controllers work only with a fix (see PCGamingWiki)";
                    break;
                case PadSupport.None:
                    detail = "No controller support: play with the keyboard and mouse";
                    break;
                default:
                    detail = _pcInfo == null ? "" : "Whether it supports controllers isn't known";
                    break;
            }

            if (support == PadSupport.None || support == PadSupport.NeedsFix)
            {
                _icon.Foreground = Warning;
                _headline.Text = support == PadSupport.None ? "This game doesn't support controllers" : "This game needs a fix for controllers";
            }
            else if (unseen && game.PluginId == SteamLibrary)
            {
                // Started through Steam, whose Steam Input presents it as an Xbox controller
                // (unless switched off in Steam's controller settings).
                _icon.Foreground = Good;
                _headline.Text = $"{yours} probably works with this game";
                detail = "The game reads Xbox controllers only; Steam Input presents yours as one.";
            }
            else if (unseen)
            {
                _icon.Foreground = Warning;
                _headline.Text = $"{yours} may not work with this game";
                detail = "It reads Xbox controllers only. A tool like DS4Windows can make yours look like one.";
            }
            else if (pad != null)
            {
                if (support == PadSupport.Full)
                    _icon.Foreground = Good;
                else if (support == PadSupport.Partial)
                    _icon.Foreground = Warning;
                else
                    _icon.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                _headline.Text = support == PadSupport.Full ? $"{yours} works with this game"
                    : support == PadSupport.Partial ? $"{yours} works with parts of this game"
                    : $"{yours} is connected";
            }
            else
            {
                _icon.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
                _headline.Text = "No controller connected";
            }
            _detail.Text = detail;
            _detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            PaintSource();
        }

        // "From Steam and PCGamingWiki", the wiki's name a link to the game's page.
        private void PaintSource()
        {
            _source.Inlines.Clear();
            PadInfo info = _pcInfo;
            bool steam = !string.IsNullOrEmpty(info?.Steam);
            bool wiki = info?.WikiUrl != null && info.WikiController != null;
            if (!steam && !wiki)
            {
                if (info?.WikiUrl != null)
                {
                    _source.Inlines.Add("See ");
                    _source.Inlines.Add(WikiLink(info.WikiUrl));
                    _source.Visibility = Visibility.Visible;
                }
                else
                {
                    _source.Visibility = Visibility.Collapsed;
                }
                return;
            }
            _source.Inlines.Add("From ");
            if (steam)
                _source.Inlines.Add("Steam");
            if (steam && wiki)
                _source.Inlines.Add(" and ");
            if (wiki)
                _source.Inlines.Add(WikiLink(info.WikiUrl));
            _source.Visibility = Visibility.Visible;
        }

        private static Hyperlink WikiLink(string url)
        {
            var link = new Hyperlink(new Run("PCGamingWiki")) { NavigateUri = new Uri(url) };
            link.SetResourceReference(TextElement.ForegroundProperty, "GlyphBrush");
            link.RequestNavigate += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch
                {
                    // No browser to open it with.
                }
                e.Handled = true;
            };
            return link;
        }

        // The PC game's support: what is kept, then (when old or missing) asked for, and the card
        // drawn again if the game is still the one shown.
        private async void AskPc(Game game)
        {
            try
            {
                if (_plugin.ControllerSupport == null)
                    return;
                int? steamId = await _plugin.SteamAppIdAsync(game);
                if (_game?.Id != game.Id)
                    return;
                PadInfo kept = _plugin.ControllerSupport.Cached(steamId, game.Name);
                if (kept != null)
                {
                    _pcInfo = kept;
                    Paint();
                }
                PadInfo info = await _plugin.ControllerSupport.GetAsync(steamId, game.Name);
                if (_game?.Id != game.Id || info == null)
                    return;
                _pcInfo = info;
                Paint();
            }
            catch (Exception ex)
            {
                Playnite.SDK.LogManager.GetLogger().Debug($"[Controllers] Could not show {game?.Name}'s controller support: {ex.Message}");
            }
        }

        // The better options under the line, or nothing.
        private void ShowBetter(Better better)
        {
            _tipList.Children.Clear();
            if (better == null)
            {
                _tipRow.Visibility = Visibility.Collapsed;
                return;
            }
            _tipHeader.Text = better.Also ? "For the best experience, also use:" : "For the best experience, use one of these instead:";
            foreach (string option in better.Options)
            {
                var item = new TextBlock { Text = "\u2022  " + option, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                item.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
                item.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
                _tipList.Children.Add(item);
            }
            if (!string.IsNullOrEmpty(better.Note))
            {
                var note = new TextBlock { Text = better.Note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontStyle = FontStyles.Italic };
                note.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
                note.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
                _tipList.Children.Add(note);
            }
            _tipRow.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Which controllers a platform's emulator takes with no setting up, as EmulatorControllers
        /// binds them: any SDL knows for those that read controllers through SDL (set so here, or
        /// by default) and for Azahar ("all controllers"). Dolphin (GameCube, and Wii as a Wii
        /// Remote with a Nunchuk), Cemu, RPCS3 and Eden are given an Xbox pad by its slot, and any
        /// other controller SDL knows as their SDL3 sees it (ControllerProbe) once SDL3 is here;
        /// before that, Dolphin the controllers whose name SDL sets itself and RPCS3 PlayStation 4
        /// and 5 controllers, and Eden none.
        /// </summary>
        private static Func<ConnectedPad, bool> Ready(string spec, bool sdl)
        {
            bool probe = global::RomM.Setup.ControllerProbe.Available;
            switch (spec)
            {
                case "nintendo_gamecube":
                case "nintendo_wii":
                    return p => p.XInput || global::RomM.Setup.EmulatorControllers.DolphinSdlName(p) != null || (probe && p.KnownToSdl);
                case "nintendo_wiiu":
                    return p => p.XInput || (probe && p.KnownToSdl);
                case "sony_playstation3":
                    return p => p.XInput || p.IsPs4 || p.IsPs5 || (probe && p.KnownToSdl);
                case "nintendo_3ds":
                    return p => p.KnownToSdl;
                case "nintendo_switch":
                    return p => probe && p.KnownToSdl;
                default:
                    // An SDL emulator also knows the controllers in the community's list.
                    return sdl ? (Func<ConnectedPad, bool>)(p => p.KnownToSdl) : p => p.Known;
            }
        }

        /// <summary>Controllers (or a mouse) a platform is better with, and how to tell one is connected.</summary>
        private sealed class Better
        {
            public string[] Options;
            public Func<ConnectedPad, bool> Has = p => false;
            /// <summary>Alongside the controller (a mouse), not instead of it.</summary>
            public bool Also;
            public string Note;
        }

        /// <summary>
        /// What a platform is better with than an ordinary pad, when the emulator here can use
        /// what it adds (from their source): Vita3K takes a PlayStation 4 or 5 controller's
        /// touchpad as the Vita's touch screen, Cemu and Eden the motion sensors of PlayStation
        /// 4/5 and Switch Pro controllers (SDL), Dolphin real Wii Remotes and GameCube
        /// controllers on a USB adapter, RPCS3 a DualShock 3's pressure-sensitive buttons. DS
        /// and 3DS touch screens follow the mouse (RetroArch passes no controller touchpad to its
        /// cores). Null when there is nothing better.
        /// </summary>
        private static Better BetterFor(string spec)
        {
            Func<ConnectedPad, bool> motion = p => p.IsPs4 || p.IsPs5 || p.Type == 5 || p.Type == 13;
            switch (spec)
            {
                case "nintendo_ds":
                case "nintendo_3ds":
                    return new Better { Options = new[] { "A mouse, for the touch screen" }, Also = true };
                case "sony_vita":
                    return new Better
                    {
                        Options = new[] { "PlayStation 5 controller (touchpad)", "PlayStation 4 controller (touchpad)" },
                        Has = p => p.IsPs4 || p.IsPs5,
                    };
                case "nintendo_wiiu":
                    return new Better
                    {
                        Options = new[] { "PlayStation 5 controller (motion)", "PlayStation 4 controller (motion)", "Switch Pro controller (motion)" },
                        Has = motion,
                        Note = "Set it up once in Cemu.",
                    };
                case "nintendo_switch":
                    return new Better
                    {
                        Options = new[] { "Switch Pro controller", "PlayStation 5 controller (motion)", "PlayStation 4 controller (motion)" },
                        Has = motion,
                    };
                case "nintendo_wii":
                    return new Better
                    {
                        Options = new[] { "Wii Remote, over Bluetooth", "PlayStation 5, PlayStation 4 or Switch Pro controller (motion, for pointing)" },
                        Note = "Set it up once in Dolphin.",
                    };
                case "nintendo_gamecube":
                    return new Better { Options = new[] { "GameCube controller, with a USB adapter" }, Note = "Set it up once in Dolphin." };
                case "sony_playstation3":
                    return new Better { Options = new[] { "DualShock 3 (pressure-sensitive buttons)" }, Has = p => p.Type == 3 };
                default:
                    return null;
            }
        }

        private static string EmulatorOf(string spec)
        {
            switch (spec)
            {
                case "nintendo_gamecube":
                case "nintendo_wii":
                    return "Dolphin";
                case "nintendo_wiiu":
                    return "Cemu";
                case "sony_playstation3":
                    return "RPCS3";
                case "nintendo_3ds":
                    return "Azahar";
                case "nintendo_switch":
                    return "Eden";
                default:
                    return "The emulator";
            }
        }

        // "Xbox 360 Controller" stays; a generic name is said plainly.
        private static string Friendly(string name) =>
            string.IsNullOrWhiteSpace(name) ? "controller" : name.Trim();

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
