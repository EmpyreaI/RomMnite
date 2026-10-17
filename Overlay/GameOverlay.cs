using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using System;
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
    /// Controllers are read through XInput only (Xbox pads, and others in XInput mode), which
    /// reads them without taking them from the emulator: SDL's DirectInput path would open them
    /// exclusively. Other controllers open it with the keyboard. Windows' Xbox Game Bar also
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
        private volatile bool _running;
        private OverlayWindow _window;
        private bool _suspended;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private LowLevelKeyboardProc _hookProc;   // kept alive while hooked
        private volatile bool _guideOpens = true;  // false while Playnite takes the Guide button
        private volatile string _button = "Guide";  // OverlayButton: Guide, BackStart, L3R3, None
        private bool _isPc;
        private static readonly Guid SteamLibrary = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        public GameOverlay(RomM plugin)
        {
            _plugin = plugin;
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
                return;
            // An emulator is always Playnite's process; a Windows game may be started through a
            // launcher, so it is found by its folder instead.
            if (!pc && processId <= 0)
                return;
            _game = game;
            _isPc = pc;
            _processId = processId;
            _startedUtc = DateTime.UtcNow;
            _button = _plugin.Settings?.OverlayButton ?? "Guide";
            _guideOpens = _button == "Guide" && !PlayniteTakesGuide();
            if (_button == "Guide" && !_guideOpens)
                s_log.Info("[Overlay] Playnite's own Guide button setting is on, so the Home button is left to it; the overlay opens with Shift+Tab.");
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
                // A Windows game in exclusive fullscreen: it can't be opened, so that, not how.
                if (_isPc && GameInFront() && InExclusiveFullscreen())
                {
                    ShowFullscreenNote();
                    return;
                }
                // Only the buttons of what is in use: the controller's, or the keyboard's.
                bool pad = InputActivity.ControllerInUse && PadOpens() != null;
                Toast.Show("Overlay ready",
                    pad ? PadOpens() + " to open the overlay." : "Press Shift+Tab to open the overlay.",
                    glyph: "\uE7FC",
                    screenOf: GameInFront() ? GetForegroundWindow() : IntPtr.Zero);
            };
            hint.Start();
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
            UnhookKeyboard();
            CloseWindow();
            Resume();
            _game = null;
            _processId = 0;
            _frontProcessId = 0;
            _isPc = false;
        }

        // How the controller opens it, in words, or null when it does not.
        private string PadOpens()
        {
            switch (_button)
            {
                case "Guide": return _guideOpens ? "Press the Home button" : null;
                case "BackStart": return "Hold Back + Start";
                case "L3R3": return "Press both sticks in (L3 + R3)";
                default: return null;
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
            // Taking the focus from a Windows game in exclusive fullscreen would minimise it:
            // the pop-up, which does not take the focus, says why nothing opens.
            if (_isPc && GameInFront() && InExclusiveFullscreen())
            {
                s_log.Info($"[Overlay] {_game.Name} is in exclusive fullscreen; the overlay stays closed.");
                ShowFullscreenNote();
                return;
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
                _window = new OverlayWindow(_plugin, game, _startedUtc, Bounds(gameWindow), keepsRunning: _isPc);
                _window.ResumeRequested += CloseWindow;
                _window.QuitRequested += Quit;
                _window.Closed += (s, e) =>
                {
                    _window = null;
                    Resume();
                    // Back to the game.
                    if (gameWindow != IntPtr.Zero)
                        SetForegroundWindow(gameWindow);
                };
                _window.Show();
                _window.Activate();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Overlay] Could not open: {ex.Message}");
                _window = null;
                Resume();
            }
        }

        private void ShowFullscreenNote()
        {
            Toast.Show("Overlay unavailable",
                "The overlay can't be opened while this game is in fullscreen. Set it to borderless or windowed in its display settings to use the overlay.",
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

        private void Quit()
        {
            int pid = _processId;
            CloseWindow();
            Resume();
            if (_isPc)
            {
                QuitPc(_frontProcessId > 0 ? _frontProcessId : pid, _game?.Name);
                return;
            }
            if (pid <= 0)
                return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        // The window in front is the game's (the emulator's, or a child's).
                        process.CloseMainWindow();
                        if (!process.WaitForExit(5000))
                        {
                            s_log.Info($"[Overlay] {process.ProcessName} did not close; ending it.");
                            process.Kill();
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // Gone already.
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Overlay] Could not quit the game: {ex.Message}");
                }
            });
        }

        // A Windows game: asked to close, never ended by force (it may be saving).
        private void QuitPc(int pid, string name)
        {
            if (pid <= 0)
            {
                Toast.Show("Can't quit from here", $"Quit {name ?? "the game"} from its own menu.", glyph: "\uE7BA");
                return;
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(10000))
                        {
                            s_log.Info($"[Overlay] {process.ProcessName} did not close when asked; left running.");
                            Ui(() => Toast.Show("Still running", $"{name ?? "The game"} didn't close when asked. Quit from its own menu.", glyph: "\uE7BA"));
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // Gone already.
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Overlay] Could not quit the game: {ex.Message}");
                }
            });
        }

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
        private const ushort PadStart = 0x0010, PadBack = 0x0020, PadL3 = 0x0040, PadR3 = 0x0080, PadGuide = 0x0400, PadA = 0x1000, PadB = 0x2000;

        private void Poll()
        {
            bool haveEx = true;
            var previous = new ushort[4];
            DateTime comboSince = DateTime.MaxValue;
            bool comboFired = false;
            while (_running)
            {
                try
                {
                    ushort all = 0;
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
                        ushort pressed = (ushort)(now & ~previous[i]);
                        previous[i] = now;
                        all |= now;
                        if (pressed != 0)
                            OnPressed(pressed);
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

        private void OnPressed(ushort pressed)
        {
            if ((pressed & PadGuide) != 0 && _guideOpens)
            {
                Ui(Toggle);
                return;
            }
            if (_window == null)
                return;
            // In the overlay: the D-pad moves between its buttons, A presses, B closes.
            if ((pressed & DPadUp) != 0) Ui(() => _window?.Move(-1));
            if ((pressed & DPadDown) != 0) Ui(() => _window?.Move(1));
            if ((pressed & DPadLeft) != 0) Ui(() => _window?.Move(-1));
            if ((pressed & DPadRight) != 0) Ui(() => _window?.Move(1));
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
            if (_processId > 0 && pid == (uint)_processId)
                return true;
            if (string.IsNullOrEmpty(_processFolder) || pid == (uint)Process.GetCurrentProcess().Id)
                return false;
            try
            {
                {
                    string path = ProcessPath((int)pid);
                    if (string.IsNullOrEmpty(path))
                        return false;
                    string folder = Path.GetDirectoryName(path) ?? "";
                    string root = _processFolder.TrimEnd('\\', '/');
                    return string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
                           || folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
                }
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
