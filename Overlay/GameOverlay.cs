using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace RomM.Overlay
{
    /// <summary>
    /// The in-game overlay, for emulated games and Windows games: opened over the running game
    /// with the controller button chosen in the settings (OverlayButton: the Home / Guide button
    /// by default, or Back + Start or L3 + R3 held for half a second, or none), or Shift+Tab
    /// while the game's window is in front. It shows the time, how long this session and the
    /// game have been played, the game's achievements, and Resume / Quit game (most emulators
    /// have no easy way out with a controller).
    ///
    /// Emulated games: while it is open the emulator is paused (its process suspended), so the
    /// buttons pressed in the overlay do nothing in the game, and resumed when it closes. Quit
    /// closes the emulator as its own close button would (so it saves its settings), and ends it
    /// if it has not gone within five seconds.
    ///
    /// Windows games (OverlayForPcGames; not Steam's, which have Steam's overlay on the same
    /// button): the game keeps running, never suspended (online games, anti-cheat and audio do
    /// not survive it), and still sees the controller while the overlay is open, since XInput
    /// cannot be taken from it; most games pause or ignore input once their window is not in
    /// front. It shows over games in a window or borderless. A game in exclusive fullscreen
    /// would be minimised by Windows as the overlay takes the focus, so for one the overlay does
    /// not open: a pop-up says so instead (<see cref="InExclusiveFullscreen"/>). Quit asks the game to close as its close button would,
    /// and never ends it by force (it may be saving): if it is still there after ten seconds, the
    /// player is told to quit from its own menu. The game's window is the one in front when the
    /// game is running from its install folder, or Playnite's process for it.
    ///
    /// Either way, the game's own exit is then seen by Playnite as usual (play time, cloud saves).
    ///
    /// Controllers are read without taking them from the game: Xbox pads (and others in XInput
    /// mode) through XInput, Switch, PlayStation and other DirectInput pads through their HID
    /// input, shared (HidPads). SDL's DirectInput path would open them exclusively. Windows' Xbox Game Bar also
    /// opens on the Guide button unless that is turned off in Windows' Game Bar settings.
    ///
    /// Playnite can take the Guide button too ("Guide button switches to Fullscreen mode", in its
    /// Input settings). While that is on, the overlay leaves Guide to Playnite and opens on
    /// Back + Start only, so one press does not do both (checked at each game's start).
    /// </summary>
    internal sealed class GameOverlay
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly RomM _plugin;
        private Game _game;
        private int _processId;
        private string _processFolder;
        private DateTime _startedUtc;
        private Thread _poller;
        private BorderlessWindows _borderless;
        // A Windows game Windows calls exclusive fullscreen: found to really be (opening the
        // overlay minimised it), or found not to be (the overlay showed over it). Unknown: tried.
        private bool? _trulyExclusive;
        private volatile bool _running;
        private OverlayWindow _window;
        // Streaming, and the stream can be ended without closing Playnite (End stream offered):
        // Quit game ends it too, once the game has closed.
        private volatile bool _streamEndable;
        private bool _suspended;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private LowLevelKeyboardProc _hookProc;   // kept alive while hooked
        private volatile bool _guideOpens = true;  // false while Playnite takes the Guide button
        private volatile string _button = "Guide";  // OverlayButton: Guide, BackStart, L3R3, None
        private string _guideTakenBy;                // what has the Home button instead (Steam...), or null
        private bool _isPc;
        private static readonly Guid SteamLibrary = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        // The running game's own volume and mute (the overlay's Game volume).
        private readonly GameAudio _audio;

        public GameOverlay(RomM plugin)
        {
            _plugin = plugin;
            _audio = new GameAudio(plugin.GetPluginUserDataPath());
            InputActivity.Start();
        }

        /// <summary>The game has started (on the UI thread).</summary>
        public void Start(Game game, int processId)
        {
            Stop();
            if (game == null)
                return;
            bool pc = IsPc(game);
            if (pc && (_plugin.Settings?.OverlayForPcGames == false || game.PluginId == SteamLibrary))
            {
                s_log.Info($"[Overlay] Not over {game.Name}: " + (game.PluginId == SteamLibrary ? "a Steam game (Steam's own overlay)." : "the overlay is off for Windows games."));
                return;
            }
            // An emulator is always Playnite's process; a Windows game may be started through a
            // launcher, so it is found by its folder instead.
            if (!pc && processId <= 0)
                return;
            _game = game;
            _isPc = pc;
            _processId = processId;
            _startedUtc = DateTime.UtcNow;
            string chosen = _plugin.Settings?.OverlayButton ?? "Guide";
            _guideTakenBy = chosen == "Guide" ? GuideTakenBy(_plugin.Playnite) : null;
            _button = _guideTakenBy != null ? "BackStart" : chosen;
            _guideOpens = _button == "Guide";
            if (_guideTakenBy != null)
                s_log.Info($"[Overlay] {_guideTakenBy} takes the Home button, so the overlay opens with Start + Select (held) instead.");
            _processFolder = null;
            if (pc && !string.IsNullOrEmpty(game.InstallDirectory))
            {
                try
                {
                    string folder = PlaynitePath.Resolve(_plugin.Playnite, _plugin.Playnite.ExpandGameVariables(game, game.InstallDirectory));
                    if (Directory.Exists(folder))
                        _processFolder = folder;
                }
                catch
                {
                    // By Playnite's process only.
                }
            }
            if (_processFolder == null && processId > 0)
            {
                try
                {
                    _processFolder = Path.GetDirectoryName(ProcessPath(processId));
                }
                catch
                {
                    _processFolder = null;
                }
            }

            _running = true;
            // Switch, PlayStation and other controllers XInput does not see (HidPads).
            HidPads.Begin();
            // A controller connected now: its buttons in the prompts (until the keyboard is used);
            // the keyboard and mouse themselves watched meanwhile.
            InputActivity.Watch();
            InputActivity.GameStarted();
            // Its volume as last set for it (else full), on its sound as it starts.
            _audio.Start(game.Id, IsGameProcess);
            // A Windows game in an ordinary window made borderless, when chosen in the settings.
            // Not one that resisted it before (it sizes its own window): left alone.
            if (pc && _plugin.Settings?.BorderlessWindowedGames == true)
            {
                string data = _plugin.GetPluginUserDataPath();
                if (BorderlessWindows.Resisted(game.Id, data))
                    s_log.Info($"[Overlay] {game.Name}: not made borderless (it resisted before; tried again in a few months).");
                else
                {
                    _borderless = new BorderlessWindows(IsGameProcess, game.Id, game.Name, data);
                    _borderless.Start();
                }
            }
            _poller = new Thread(Poll) { IsBackground = true, Name = "RomM overlay" };
            _poller.Start();
            HookKeyboard();
            s_log.Info($"[Overlay] Ready for {game.Name} ({(pc ? "Windows game" : "emulated")}, process {processId}, folder {_processFolder ?? "unknown"}).");

            // Steam's pop-up: how to open it, once the game's window is up.
            var hint = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            Guid hinted = game.Id;
            hint.Tick += (s, e) =>
            {
                hint.Stop();
                if (_game == null || _game.Id != hinted)
                    return;
                // A Windows game known to be in true exclusive fullscreen: it can't be opened, so
                // that, not how. (One Windows only calls exclusive is tried at the first press.)
                if (_isPc && _trulyExclusive == true && GameInFront() && InExclusiveFullscreen())
                {
                    ShowFullscreenNote();
                    return;
                }
                // The controller's way while one is what is in use (connected as the game started,
                // or plugged in or pressed since), the keyboard's otherwise, changing live as
                // that does (InputActivity, ControllerGlyphs.Bind). The buttons are pictures.
                string padOpens = PadOpens();
                const string keys = "Press {Shift} + {Tab} to open the overlay.";
                string message = padOpens == null ? keys : $"{padOpens} to open the overlay.";
                if (padOpens != null && _guideTakenBy != null)
                    message += $" ({_guideTakenBy} has the Home button.)";
                Toast.Show("Overlay ready", message,
                    glyph: "\uE7FC",
                    screenOf: GameInFront() ? GetForegroundWindow() : IntPtr.Zero,
                    keyboardMessage: keys);
            };
            hint.Start();
        }

        /// <summary>
        /// What else answers the Home (Guide) button, so the overlay would open along with it:
        /// Playnite's own Guide setting, Steam (running: its Guide button opens Steam), or the
        /// Xbox Game Bar (installed, and its "Open Xbox Game Bar using this button on a
        /// controller" on: HKCU\Software\Microsoft\GameBar, UseNexusForGameBarEnabled, on unless
        /// set to 0). Null when nothing does. Then the overlay opens with Start + Select instead.
        /// </summary>
        internal static string GuideTakenBy(IPlayniteAPI api)
        {
            if (PlayniteTakesGuide(api))
                return "Playnite";
            try
            {
                Process[] steam = Process.GetProcessesByName("steam");
                foreach (Process p in steam)
                    p.Dispose();
                if (steam.Length > 0)
                    return "Steam";
            }
            catch
            {
            }
            if (GameBarTakesGuide())
                return "The Xbox Game Bar";
            return null;
        }

        /// <summary>The button that opens the overlay: the setting's, or Start + Select when the Home button is taken.</summary>
        internal static string EffectiveButton(string chosen, IPlayniteAPI api) =>
            (chosen ?? "Guide") == "Guide" && GuideTakenBy(api) != null ? "BackStart" : (chosen ?? "Guide");

        /// <summary>Where the emulators' own menus go this time, from the overlay's button (Setup.EmulatorMenus.For).</summary>
        internal static Setup.EmulatorMenus.Place EmulatorMenuButton(string chosen, IPlayniteAPI api) =>
            Setup.EmulatorMenus.For(chosen, GuideTakenBy(api) != null);

        private static bool GameBarTakesGuide()
        {
            try
            {
                // Installed (its app package registered for this user).
                using (var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (packages != null && !packages.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.XboxGamingOverlay", StringComparison.OrdinalIgnoreCase)))
                        return false;
                }
                using (var gameBar = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar"))
                {
                    object value = gameBar?.GetValue("UseNexusForGameBarEnabled");
                    return !(value is int on) || on != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether Playnite itself acts on the Guide button: its "Guide button switches to
        /// Fullscreen mode" (Desktop mode) or Fullscreen mode's own Guide setting, read from the
        /// mode's settings file. The setting's stored name is not part of Playnite's SDK, so any
        /// setting naming the Guide button with a button / focus / fullscreen word that is on
        /// counts; when the file cannot be read, Guide is left to the overlay.
        /// </summary>
        private bool PlayniteTakesGuide() => PlayniteTakesGuide(_plugin.Playnite);

        internal static bool PlayniteTakesGuide(IPlayniteAPI api)
        {
            try
            {
                bool fullscreen = api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
                string file = Path.Combine(api.Paths.ConfigurationPath, fullscreen ? "fullscreenConfig.json" : "config.json");
                if (!File.Exists(file))
                    return false;
                var root = Newtonsoft.Json.Linq.JToken.Parse(File.ReadAllText(file));
                return root.SelectTokens("$..*").OfType<Newtonsoft.Json.Linq.JValue>().Any(v =>
                {
                    if (v.Type != Newtonsoft.Json.Linq.JTokenType.Boolean || !(bool)v.Value)
                        return false;
                    string name = (v.Parent as Newtonsoft.Json.Linq.JProperty)?.Name ?? "";
                    return name.IndexOf("Guide", StringComparison.OrdinalIgnoreCase) >= 0
                           && (name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0
                               || name.IndexOf("Focus", StringComparison.OrdinalIgnoreCase) >= 0
                               || name.IndexOf("Fullscreen", StringComparison.OrdinalIgnoreCase) >= 0);
                });
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Overlay] Could not read Playnite's Guide button setting: {ex.Message}");
                return false;
            }
        }

        /// <summary>The game has stopped, or Playnite is closing (on the UI thread).</summary>
        public void Stop()
        {
            _running = false;
            _borderless?.Stop();
            _borderless = null;
            _trulyExclusive = null;
            HidPads.End();
            _audio.Stop();
            UnhookKeyboard();
            InputActivity.StopWatching();
            CloseWindow();
            Resume();
            _game = null;
            _processId = 0;
            _frontProcessId = 0;
            _isPc = false;
        }

        // ---- Streaming (Sunshine) ------------------------------------------------------------

        // "End stream" in the overlay while Sunshine is streaming, unless Sunshine itself started
        // the game (or Playnite): its "close app" would end those too. Looked at off the UI thread.
        private void OfferEndStreamIfStreaming(OverlayWindow window)
        {
            var sunshine = _plugin.Sunshine;
            if (sunshine == null || _plugin.Settings?.SunshineEnabled != true)
                return;
            int game = _processId;
            System.Threading.Tasks.Task.Run(async () =>
            {
                bool? streaming = await sunshine.StreamingAsync().ConfigureAwait(false);
                if (streaming != true)
                    return;
                string startedBy = SunshineAncestor(game) ?? SunshineAncestor(Process.GetCurrentProcess().Id);
                if (startedBy != null)
                {
                    s_log.Info($"[Sunshine] Streaming, but \"End stream\" is not offered: {startedBy} started the game, and ending the stream would close it.");
                    return;
                }
                Ui(() =>
                {
                    if (_window == window)
                    {
                        _streamEndable = true;
                        window.OfferEndStream();
                    }
                });
            });
        }

        private void EndStream()
        {
            OverlayWindow window = _window;
            var sunshine = _plugin.Sunshine;
            if (window == null || sunshine == null)
                return;
            System.Threading.Tasks.Task.Run(async () =>
            {
                string problem = await sunshine.EndStreamAsync().ConfigureAwait(false);
                if (problem != null)
                    s_log.Warn($"[Sunshine] Could not end the stream: {problem}");
                if (problem == null)
                    _streamEndable = false;
                Ui(() => window.StreamEnded(problem));
            });
        }

        // After Quit game: the stream ended too, when it still runs. Not when the game would not
        // close (the player is still at it), nor when Sunshine started the game or Playnite
        // (never offered then: its "close app" would close Playnite as well, and a stream of the
        // game Sunshine started ends with it by itself).
        private void EndStreamAfterQuit(string name)
        {
            var sunshine = _plugin.Sunshine;
            if (sunshine == null)
                return;
            try
            {
                bool? streaming = sunshine.StreamingAsync().GetAwaiter().GetResult();
                if (streaming != true)
                    return;
                string problem = sunshine.EndStreamAsync().GetAwaiter().GetResult();
                if (problem != null)
                {
                    s_log.Warn($"[Sunshine] {name}: quit, but the stream could not be ended: {problem}");
                    Toast.Show("Stream still running", "End it from Moonlight or Sunshine.", glyph: "\uE7BA");
                }
                else
                {
                    s_log.Info($"[Sunshine] {name}: quit, and the stream ended.");
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sunshine] Could not end the stream after quitting: {ex.Message}");
            }
        }

        // Sunshine as a parent of the process (up to a few levels), by name; null when not.
        private static string SunshineAncestor(int processId)
        {
            if (processId <= 0)
                return null;
            Dictionary<int, KeyValuePair<int, string>> parents = ProcessParents();
            int at = processId;
            for (int depth = 0; depth < 12 && parents.TryGetValue(at, out var entry); depth++)
            {
                if (entry.Value.IndexOf("sunshine", StringComparison.OrdinalIgnoreCase) >= 0)
                    return entry.Value;
                at = entry.Key;
            }
            return null;
        }

        // Every process: its parent's id and its own name.
        private static Dictionary<int, KeyValuePair<int, string>> ProcessParents()
        {
            var all = new Dictionary<int, KeyValuePair<int, string>>();
            IntPtr snapshot = CreateToolhelp32Snapshot(0x00000002, 0);   // TH32CS_SNAPPROCESS
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                return all;
            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
                if (!Process32First(snapshot, ref entry))
                    return all;
                do
                {
                    all[(int)entry.ProcessId] = new KeyValuePair<int, string>((int)entry.ParentProcessId, entry.ExeFile ?? "");
                }
                while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            // Its own entry: the parent of the process (keyed by the child) is looked up next.
            var byChild = new Dictionary<int, KeyValuePair<int, string>>();
            foreach (var pair in all)
            {
                int parent = pair.Value.Key;
                string parentName = all.TryGetValue(parent, out var p) ? p.Value : "";
                byChild[pair.Key] = new KeyValuePair<int, string>(parent, parentName);
            }
            return byChild;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        // How the controller opens it, in words, or null when it does not.
        private string PadOpens()
        {
            switch (_button)
            {
                case "Guide": return _guideOpens ? "Press {Guide}" : null;
                case "BackStart": return "Hold {Back} + {Start}";
                case "L3R3": return "Press {L3} + {R3}";
                default: return null;
            }
        }

        // A controller connected: an Xbox pad (XInput), or one read directly (HidPads).
        private static bool ControllerConnected()
        {
            try
            {
                return ControllerMonitor.XInputConnected() || HidPads.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsPc(Game game) =>
            game.Platforms?.Any(p => p != null && (string.Equals(p.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase)
                || (p.Name ?? "").IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                || (p.Name ?? "").StartsWith("PC", StringComparison.OrdinalIgnoreCase))) == true;

        // ---- Opening and closing -------------------------------------------------------------

        private void Toggle()
        {
            if (_window != null)
                CloseWindow();
            else
                Open();
        }

        private void Open()
        {
            if (_game == null || _window != null)
                return;
            // A Windows game Windows calls exclusive fullscreen. Many "fullscreen" games on
            // Windows 10 and 11 are not truly (Windows runs them borderless underneath, and a
            // window shows over them), so the first press tries: if the game is minimised by it,
            // it is truly exclusive (put back, and said so from then on); if not, the overlay
            // simply works. Known to be truly exclusive: the pop-up, which takes no focus, at once.
            bool probing = false;
            if (_isPc && GameInFront() && InExclusiveFullscreen())
            {
                if (_trulyExclusive == true)
                {
                    s_log.Info($"[Overlay] {_game.Name} is in exclusive fullscreen; the overlay stays closed.");
                    ShowFullscreenNote();
                    return;
                }
                probing = _trulyExclusive == null;
            }
            try
            {
                Game game = _plugin.Playnite.Database.Games.Get(_game.Id) ?? _game;
                IntPtr gameWindow = GetForegroundWindow();
                if (GameInFront())
                {
                    GetWindowThreadProcessId(gameWindow, out uint front);
                    _frontProcessId = (int)front;
                }
                if (!_isPc)
                    Suspend();
                _window = new OverlayWindow(_plugin, game, _startedUtc, Bounds(gameWindow), keepsRunning: _isPc, audio: _audio);
                _window.ResumeRequested += CloseWindow;
                _window.QuitRequested += Quit;
                _window.EndStreamRequested += EndStream;
                _streamEndable = false;   // looked at again for this opening
                OfferEndStreamIfStreaming(_window);
                _window.Closed += (s, e) =>
                {
                    _window = null;
                    Resume();
                    // Back to the game (put back first, if taking the focus minimised it).
                    if (gameWindow != IntPtr.Zero)
                    {
                        if (IsIconic(gameWindow))
                            ShowWindow(gameWindow, SW_RESTORE);
                        SetForegroundWindow(gameWindow);
                    }
                };
                _window.Show();
                _window.Activate();
                if (probing)
                    CheckExclusive(gameWindow);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Overlay] Could not open: {ex.Message}");
                _window = null;
                Resume();
            }
        }

        // Shortly after the overlay opened over a game Windows calls exclusive fullscreen: was the
        // game minimised by it (truly exclusive: closed again, the game put back, and the pop-up)?
        private void CheckExclusive(IntPtr gameWindow)
        {
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            wait.Tick += (s, e) =>
            {
                wait.Stop();
                if (_game == null)
                    return;
                bool minimised = gameWindow != IntPtr.Zero && IsIconic(gameWindow);
                _trulyExclusive = minimised;
                s_log.Info(minimised
                    ? $"[Overlay] {_game.Name} is in true exclusive fullscreen (opening the overlay minimised it); it stays closed over it."
                    : $"[Overlay] {_game.Name} is fullscreen but not exclusive; the overlay shows over it.");
                if (!minimised)
                    return;
                CloseWindow();   // puts the game back
                ShowFullscreenNote();
            };
            wait.Start();
        }

        // Short: a pop-up. Where to go, by what the settings already have.
        private void ShowFullscreenNote()
        {
            bool helper = _plugin.Settings?.BorderlessWindowedGames == true;
            Toast.Show("Overlay unavailable",
                helper
                    ? "Set this game to windowed in its display settings to use the overlay."
                    : "Set this game to windowed, then turn on Settings > RomMnite > Make windowed Windows games borderless.",
                glyph: "\uE7BA",
                screenOf: GetForegroundWindow());
        }

        // Windows' own word for it (the same check that keeps notifications away from games):
        // a Direct3D program in exclusive fullscreen, as the one in front. Borderless and
        // windowed games are not (QUNS_BUSY at most), so they keep the overlay.
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        internal static bool InExclusiveFullscreen()
        {
            try
            {
                return SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;
            }
            catch
            {
                return false;   // unknown: the overlay opens as before
            }
        }

        private void CloseWindow()
        {
            OverlayWindow window = _window;
            if (window == null)
                return;
            try
            {
                window.Close();
            }
            catch
            {
                // Closed already.
            }
        }

        // Quit: the emulator asked to close as its own close button would, then ended if it is
        // still there five seconds later.
        private int _frontProcessId;   // the process whose window was the game's when the overlay opened

        // How long a game has to close when asked before it is ended: an emulator saves its
        // settings at once; a Windows game may be writing its save as it exits.
        private static readonly TimeSpan EmulatorGrace = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PcGrace = TimeSpan.FromSeconds(10);

        /// <summary>The game's process Exit closes: a Windows game's window in front, else the one started.</summary>
        internal int QuitProcessId => _isPc && _frontProcessId > 0 ? _frontProcessId : _processId;

        /// <summary>The game the overlay is over, if any.</summary>
        internal Guid? GameId => _game?.Id;

        /// <summary>How long a game has to close before it is ended: emulators 5 s, Windows games 10 s.</summary>
        internal static TimeSpan GraceFor(Game game) => game != null && IsPc(game) ? PcGrace : EmulatorGrace;

        private void Quit()
        {
            int pid = QuitProcessId;
            string name = _game?.Name;
            TimeSpan grace = _isPc ? PcGrace : EmulatorGrace;
            bool endStream = _streamEndable;
            CloseWindow();
            Resume();
            if (pid <= 0)
            {
                Toast.Show("Can't quit from here", $"Quit {name ?? "the game"} from its own menu.", glyph: "\uE7BA");
                return;
            }
            if (!endStream)
            {
                CloseGame(pid, name, grace);
                return;
            }
            // Streaming: the game closed first (so it saves), then the stream.
            System.Threading.Tasks.Task.Run(() =>
            {
                if (CloseGameAndWait(pid, name, grace))
                    EndStreamAfterQuit(name);
            });
        }

        /// <summary>
        /// Asks the game to close as its own close button would (so it saves), with a pop-up
        /// counting down the seconds it has; ended if it is still there when they are up.
        /// </summary>
        private static void CloseGame(int pid, string name, TimeSpan grace) =>
            System.Threading.Tasks.Task.Run(() => CloseGameAndWait(pid, name, grace));

        /// <summary>
        /// <see cref="CloseGame"/>, returning once the game is gone (or could not be closed):
        /// true when it is gone. <paramref name="status"/> hears the countdown too.
        /// </summary>
        internal static bool CloseGameAndWait(int pid, string name, TimeSpan grace, Action<string> status = null)
        {
            Toast.Live note = null;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    IntPtr window = IntPtr.Zero;
                    try
                    {
                        window = process.MainWindowHandle;
                    }
                    catch
                    {
                    }
                    string title = $"Closing {name ?? "the game"}...";
                    note = Toast.ShowLive(title, Countdown((int)grace.TotalSeconds), glyph: "\uE711", screenOf: window);
                    status?.Invoke(Countdown((int)grace.TotalSeconds));
                    process.CloseMainWindow();
                    DateTime until = DateTime.UtcNow + grace;
                    while (!process.WaitForExit(250))
                    {
                        int left = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
                        if (left <= 0)
                        {
                            s_log.Info($"[Overlay] {process.ProcessName} did not close within {grace.TotalSeconds:0} s; ending it.");
                            process.Kill();
                            note.Close();
                            return process.WaitForExit(3000);
                        }
                        note.Update(Countdown(left));
                        status?.Invoke(Countdown(left));
                    }
                    note.Close();
                    return true;
                }
            }
            catch (ArgumentException)
            {
                note?.Close();   // gone already
                return true;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Overlay] Could not quit the game: {ex.Message}");
                note?.Update("Couldn't close it. Quit from its own menu.");
                note?.Close(TimeSpan.FromSeconds(4));
                return false;
            }
        }

        private static string Countdown(int seconds) =>
            $"Force quitting in {seconds} second{(seconds == 1 ? "" : "s")} if it doesn't close.";

        // ---- Pausing ---------------------------------------------------------------------------

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr process);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr process);

        private void Suspend()
        {
            if (_suspended || _processId <= 0)
                return;
            try
            {
                using (Process process = Process.GetProcessById(_processId))
                {
                    if (NtSuspendProcess(process.Handle) == 0)
                        _suspended = true;
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Overlay] Could not pause the game: {ex.Message}");
            }
        }

        private void Resume()
        {
            if (!_suspended)
                return;
            _suspended = false;
            try
            {
                using (Process process = Process.GetProcessById(_processId))
                    NtResumeProcess(process.Handle);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Overlay] Could not resume the game: {ex.Message}");
            }
        }

        // ---- The controller --------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX;
            public short ThumbLY;
            public short ThumbRX;
            public short ThumbRY;
        }

        // XInputGetStateEx (ordinal 100): XInputGetState with the Guide button too.
        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx(uint index, out XInputState state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint index, out XInputState state);

        private const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
        private const ushort PadStart = 0x0010, PadBack = 0x0020, PadL3 = 0x0040, PadR3 = 0x0080, PadLB = 0x0100, PadRB = 0x0200,
            PadGuide = 0x0400, PadA = 0x1000, PadB = 0x2000;

        private void Poll()
        {
            bool haveEx = true;
            var previous = new ushort[5];
            DateTime comboSince = DateTime.MaxValue;
            bool comboFired = false;
            DateTime heldSince = DateTime.MaxValue, lastRepeat = DateTime.MinValue;
            while (_running)
            {
                try
                {
                    ushort all = 0, pressedAll = 0;
                    ushort pressedXInput = 0;
                    for (uint i = 0; i < 4; i++)
                    {
                        XInputState state;
                        uint result;
                        if (haveEx)
                        {
                            try
                            {
                                result = XInputGetStateEx(i, out state);
                            }
                            catch (EntryPointNotFoundException)
                            {
                                haveEx = false;
                                result = XInputGetState(i, out state);
                            }
                        }
                        else
                        {
                            result = XInputGetState(i, out state);
                        }
                        ushort now = result == 0 ? state.Buttons : (ushort)0;
                        pressedXInput |= (ushort)(now & ~previous[i]);
                        pressedAll |= (ushort)(now & ~previous[i]);
                        previous[i] = now;
                        all |= now;
                    }
                    // The controllers XInput does not see, as one more.
                    ushort hid = HidPads.Buttons;
                    if (hid != previous[4] && hid != 0)
                        InputActivity.NotePad();
                    ushort pressedHid = (ushort)(hid & ~previous[4]);
                    pressedAll |= pressedHid;
                    previous[4] = hid;
                    all |= hid;
                    // Whose buttons to show: the controller pressed last. One seen both ways
                    // (Steam Input's virtual Xbox pad and the controller itself) is the controller.
                    if (pressedXInput != 0)
                    {
                        // XInput's own presses (the Guide button too, which its packet number
                        // does not show): the controller is what is in use.
                        InputActivity.NotePad();
                        ControllerGlyphs.Used(ControllerGlyphs.Family.Xbox);
                    }
                    if (pressedHid != 0)
                        ControllerGlyphs.Used(HidPads.LastPressedFamily ?? ControllerGlyphs.Family.Xbox);
                    // One press, once: a controller seen both ways (Steam Input's virtual pad
                    // and the controller itself) is still one.
                    if (pressedAll != 0)
                        OnPressed(pressedAll);

                    // Up or down held in the overlay's achievements: on to the next, again and again.
                    int held = (all & DPadUp) != 0 ? -1 : (all & DPadDown) != 0 ? 1 : 0;
                    if (held == 0 || _window == null)
                        heldSince = DateTime.MaxValue;
                    else if ((pressedAll & (DPadUp | DPadDown)) != 0)
                    {
                        heldSince = DateTime.UtcNow;
                        lastRepeat = DateTime.UtcNow;
                    }
                    else if (heldSince != DateTime.MaxValue && DateTime.UtcNow - heldSince > TimeSpan.FromMilliseconds(400)
                             && DateTime.UtcNow - lastRepeat > TimeSpan.FromMilliseconds(90))
                    {
                        lastRepeat = DateTime.UtcNow;
                        int step = held;
                        Ui(() => _window?.MoveHeld(step));
                    }

                    // The chosen pair held together for half a second.
                    ushort pair = _button == "BackStart" ? (ushort)(PadBack | PadStart)
                                : _button == "L3R3" ? (ushort)(PadL3 | PadR3)
                                : (ushort)0;
                    if (pair != 0 && (all & pair) == pair)
                    {
                        if (comboSince == DateTime.MaxValue)
                            comboSince = DateTime.UtcNow;
                        else if (!comboFired && DateTime.UtcNow - comboSince > TimeSpan.FromMilliseconds(500))
                        {
                            comboFired = true;
                            Ui(Toggle);
                        }
                    }
                    else
                    {
                        comboSince = DateTime.MaxValue;
                        comboFired = false;
                    }
                }
                catch (DllNotFoundException)
                {
                    s_log.Info("[Overlay] XInput is not available; the overlay opens with Shift+Tab only.");
                    return;
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Overlay] {ex.Message}");
                }
                Thread.Sleep(30);
            }
        }

        private DateTime _lastGuideToggle = DateTime.MinValue;

        private void OnPressed(ushort pressed)
        {
            if ((pressed & PadGuide) != 0 && _guideOpens)
            {
                // The same press reaching here twice (one controller read two ways, a moment
                // apart) opens it once.
                if (DateTime.UtcNow - _lastGuideToggle < TimeSpan.FromMilliseconds(300))
                    return;
                _lastGuideToggle = DateTime.UtcNow;
                Ui(Toggle);
                return;
            }
            if (_window == null)
                return;
            // In the overlay: the bumpers choose the achievements (LB) or the buttons (RB); the
            // D-pad scrolls the one or moves between the other, A presses, B closes.
            if ((pressed & PadLB) != 0) Ui(() => _window?.UseList(true));
            if ((pressed & PadRB) != 0) Ui(() => _window?.UseList(false));
            if ((pressed & DPadUp) != 0) Ui(() => _window?.Move(-1));
            if ((pressed & DPadDown) != 0) Ui(() => _window?.Move(1));
            // Left and right turn the game's volume when it is chosen, else move like up and down.
            if ((pressed & DPadLeft) != 0) Ui(() => { if (_window != null && !_window.Adjust(-1)) _window.Move(-1); });
            if ((pressed & DPadRight) != 0) Ui(() => { if (_window != null && !_window.Adjust(1)) _window.Move(1); });
            if ((pressed & PadA) != 0) Ui(() => _window?.Press());
            if ((pressed & PadB) != 0) Ui(CloseWindow);
        }

        private void Ui(Action action)
        {
            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(action);
            }
            catch
            {
                // Playnite is closing.
            }
        }

        // ---- The keyboard: Shift+Tab while the game is in front -------------------------------

        private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInfo
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string module);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        private const int VK_TAB = 0x09, VK_SHIFT = 0x10;
        private const uint LLKHF_INJECTED = 0x10;

        private void HookKeyboard()
        {
            if (_keyboardHook != IntPtr.Zero)
                return;
            _hookProc = OnKey;
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            if (_keyboardHook == IntPtr.Zero)
                s_log.Debug("[Overlay] Could not watch the keyboard; the overlay opens from the controller only.");
        }

        private void UnhookKeyboard()
        {
            if (_keyboardHook == IntPtr.Zero)
                return;
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        // Shift+Tab taken (not passed on) only while the game's window or the overlay is in front.
        private IntPtr OnKey(int code, IntPtr message, IntPtr data)
        {
            try
            {
                if (code >= 0 && ((int)message == WM_KEYDOWN || (int)message == WM_SYSKEYDOWN))
                {
                    var info = (KeyboardInfo)Marshal.PtrToStructure(data, typeof(KeyboardInfo));
                    // A key on the keyboard itself (not one a program made, as Steam Input does
                    // from a controller): the keyboard's buttons in the prompts.
                    if ((info.Flags & LLKHF_INJECTED) == 0)
                        InputActivity.NoteKeyboard();
                    if (info.VirtualKey == VK_TAB && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 && (_window != null || GameInFront()))
                    {
                        Ui(Toggle);
                        return (IntPtr)1;
                    }
                }
            }
            catch
            {
                // Passed on.
            }
            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        // A process's program, whatever its bitness (Process.MainModule cannot read a 64-bit
        // process from a 32-bit one), or null.
        private static string ProcessPath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var name = new System.Text.StringBuilder(1024);
                int size = name.Capacity;
                return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        // Whether the window in front is the game's: Playnite's process for it, or a program from
        // its folder (an emulator may start the game in a process of its own; a Windows game's
        // may be anywhere under its install folder, bin\x64 and the like).
        private bool GameInFront()
        {
            IntPtr front = GetForegroundWindow();
            if (front == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(front, out uint pid);
            return IsGameProcess((int)pid);
        }

        // Whether a process is the game's: Playnite's process for it, or a program from its folder.
        private bool IsGameProcess(int pid)
        {
            if (_processId > 0 && pid == _processId)
                return true;
            string gameFolder = _processFolder;
            if (string.IsNullOrEmpty(gameFolder) || pid <= 0 || pid == Process.GetCurrentProcess().Id)
                return false;
            try
            {
                string path = ProcessPath(pid);
                if (string.IsNullOrEmpty(path))
                    return false;
                string folder = Path.GetDirectoryName(path) ?? "";
                string root = gameFolder.TrimEnd('\\', '/');
                return string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
                       || folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ---- Where to show it --------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        // The screen the game is on, in device pixels (the window converts them).
        private static Int32Rect Bounds(IntPtr gameWindow)
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(gameWindow, 2 /* MONITOR_DEFAULTTONEAREST */);
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    return new Int32Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
            }
            catch
            {
                // The main screen.
            }
            return Int32Rect.Empty;
        }
    }
}
