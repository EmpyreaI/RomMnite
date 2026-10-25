using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RomM.Overlay
{
    /// <summary>
    /// A Windows game running in an ordinary window (title bar, borders) made borderless and
    /// sized to its screen, as borderless-windowed tools do: only the window's style and place
    /// change, nothing in the game. Then the overlay can show over it, which it cannot over a
    /// game in exclusive fullscreen. (Settings: "Make windowed Windows games borderless".)
    ///
    /// Looked for every two seconds while the game runs: its own processes' visible top-level
    /// windows with a caption or sizing frame, large enough to be the game's (not a launcher's
    /// or a dialog).
    ///
    /// Two ways, the second for games that put their frame back (Unreal Engine 3 games set their
    /// window's style again whenever it changes size, Fable Anniversary among them):
    ///   1. the frame taken off and the window sized to its screen;
    ///   2. the frame left on, and the window placed so its inside covers the screen exactly,
    ///      the frame just outside it (the style is then the game's own, so it has nothing to
    ///      put back). On a computer with more than one screen the frame's edge can show on
    ///      the one next to it.
    /// A window whose inside covers its screen already is left alone. A few tries at most, so
    /// the game and this never fight over it; a game that resists both ways (it sizes its own
    /// window, as Fable Anniversary does) is remembered (BorderlessResisted.json in the plugin's
    /// data) and not tried on its next launches, so its window jumps about only the first time.
    /// Tried again after <see cref="RetryAfter"/>, in case an update changed it.
    /// </summary>
    internal sealed class BorderlessWindows
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private const int MaxTimes = 6;
        private const int StyleTries = 2;   // then the frame is left on, outside the screen
        private const int MinWidth = 640, MinHeight = 480;

        private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(90);
        private static readonly object s_fileLock = new object();

        private readonly Func<int, bool> _isGame;
        private readonly string _gameName;
        private readonly Guid _gameId;
        private readonly string _file;
        private bool _noted;
        private readonly Dictionary<IntPtr, int> _done = new Dictionary<IntPtr, int>();
        private Timer _timer;
        private int _busy;

        /// <param name="dataFolder">The plugin's data folder, where the games that resisted are kept.</param>
        public BorderlessWindows(Func<int, bool> isGameProcess, Guid gameId, string gameName, string dataFolder)
        {
            _isGame = isGameProcess;
            _gameId = gameId;
            _gameName = gameName;
            _file = string.IsNullOrEmpty(dataFolder) ? null : Path.Combine(dataFolder, "BorderlessResisted.json");
        }

        /// <summary>Whether the game resisted being made borderless before (and is not due to be tried again).</summary>
        public static bool Resisted(Guid gameId, string dataFolder)
        {
            if (string.IsNullOrEmpty(dataFolder))
                return false;
            Dictionary<Guid, DateTime> resisted = Load(Path.Combine(dataFolder, "BorderlessResisted.json"));
            return resisted.TryGetValue(gameId, out DateTime when) && DateTime.UtcNow - when < RetryAfter;
        }

        private static Dictionary<Guid, DateTime> Load(string file)
        {
            lock (s_fileLock)
            {
                try
                {
                    if (File.Exists(file))
                        return JsonConvert.DeserializeObject<Dictionary<Guid, DateTime>>(File.ReadAllText(file)) ?? new Dictionary<Guid, DateTime>();
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Overlay] Could not read {file}: {ex.Message}");
                }
                return new Dictionary<Guid, DateTime>();
            }
        }

        // The game resisted: noted, once, so its next launches are left alone.
        private void NoteResisted()
        {
            if (_noted || _file == null)
                return;
            _noted = true;
            lock (s_fileLock)
            {
                try
                {
                    Dictionary<Guid, DateTime> resisted = Load(_file);
                    resisted[_gameId] = DateTime.UtcNow;
                    File.WriteAllText(_file, JsonConvert.SerializeObject(resisted, Formatting.Indented));
                    s_log.Info($"[Overlay] {_gameName}: not made borderless on its next launches (it sizes its own window).");
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Overlay] Could not note that {_gameName} resists being made borderless: {ex.Message}");
                }
            }
        }

        public void Start()
        {
            s_log.Info($"[Overlay] {_gameName}: windowed windows of the game made borderless while it runs.");
            _timer = new Timer(_ => Look(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void Look()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                return;   // the last look not done yet
            try
            {
                var windows = new List<IntPtr>();
                EnumWindows((window, _) =>
                {
                    if (IsWindowVisible(window))
                        windows.Add(window);
                    return true;
                }, IntPtr.Zero);
                foreach (IntPtr window in windows)
                {
                    GetWindowThreadProcessId(window, out uint pid);
                    if (!IsGame((int)pid))
                        continue;
                    if (Covers(window))
                        continue;   // fills its screen (either way)
                    string skip = Skip(window, out string what);
                    if (skip != null)
                    {
                        Tell(window, $"{what} left as it is: {skip}.");
                        continue;
                    }
                    _done.TryGetValue(window, out int times);
                    if (times >= MaxTimes)
                    {
                        Tell(window, $"{what} keeps putting its frame back; left as it is after {MaxTimes} tries.");
                        NoteResisted();
                        continue;
                    }
                    try
                    {
                        MakeBorderless(window, what, keepFrame: times >= StyleTries);
                        _done[window] = times + 1;
                    }
                    catch (Exception ex)
                    {
                        Tell(window, $"{what} could not be made borderless: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Info($"[Overlay] Borderless: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        // Whether a process is the game's, asked once each.
        private readonly Dictionary<int, bool> _games = new Dictionary<int, bool>();

        private bool IsGame(int pid)
        {
            if (!_games.TryGetValue(pid, out bool game))
                _games[pid] = game = _isGame(pid);
            return game;
        }

        // What the log has said of each window, so it says it once.
        private readonly Dictionary<IntPtr, string> _told = new Dictionary<IntPtr, string>();

        private void Tell(IntPtr window, string line)
        {
            if (_told.TryGetValue(window, out string said) && said == line)
                return;
            _told[window] = line;
            s_log.Info($"[Overlay] {_gameName}: {line}");
        }

        // Why a game's window is not one to make borderless (null: it is): not an ordinary
        // top-level window with a frame (a title bar or sizing border), or too small for the
        // game's (a launcher's, a dialog). What it is, for the log.
        private static string Skip(IntPtr window, out string what)
        {
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            long style = GetWindowLong(window, GWL_STYLE).ToInt64() & 0xFFFFFFFFL;
            GetClientRect(window, out Rect client);
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            what = $"window \"{name}\" ({width}x{height}, style {style:X8})";

            if (IsIconic(window))
                return "minimised";
            IntPtr owner = GetWindow(window, GW_OWNER);
            if (owner != IntPtr.Zero && IsWindowVisible(owner))
                return "owned by another window (a dialog's or tool's)";
            if ((style & WS_CHILD) != 0)
                return "a child window";
            if ((style & (WS_CAPTION | WS_THICKFRAME)) == 0)
                return "no frame (borderless or fullscreen already)";
            if (name.ToString() == "#32770")
                return "a dialog";
            if (width < MinWidth || height < MinHeight)
                return $"smaller than {MinWidth}x{MinHeight}";
            return null;
        }

        // Whether the window's inside (its client area) is exactly its screen.
        private static bool Covers(IntPtr window)
        {
            if (!ScreenOf(window, out Rect screen) || !GetClientRect(window, out Rect client))
                return false;
            var origin = new Point();
            if (!ClientToScreen(window, ref origin))
                return false;
            return origin.X == screen.Left && origin.Y == screen.Top
                && client.Right - client.Left == screen.Right - screen.Left
                && client.Bottom - client.Top == screen.Bottom - screen.Top;
        }

        private static bool ScreenOf(IntPtr window, out Rect screen)
        {
            screen = default(Rect);
            IntPtr monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(monitor, ref info))
                return false;
            screen = info.Monitor;
            return true;
        }

        private void MakeBorderless(IntPtr window, string what, bool keepFrame)
        {
            if (!ScreenOf(window, out Rect screen))
                return;
            long style = GetWindowLong(window, GWL_STYLE).ToInt64() & 0xFFFFFFFFL;
            long exStyle = GetWindowLong(window, GWL_EXSTYLE).ToInt64() & 0xFFFFFFFFL;
            if (keepFrame)
            {
                // The window as big as its frame needs for its inside to be the screen.
                Rect outer = screen;
                if (!AdjustWindowRectEx(ref outer, unchecked((uint)style), false, unchecked((uint)exStyle)))
                    return;
                SetWindowPos(window, IntPtr.Zero, outer.Left, outer.Top, outer.Right - outer.Left, outer.Bottom - outer.Top,
                    SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
                s_log.Info($"[Overlay] {_gameName}: {what} puts its frame back; placed to fill its screen ({screen.Right - screen.Left}x{screen.Bottom - screen.Top}) with the frame outside it.");
                return;
            }
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_MAXIMIZE);
            style |= WS_POPUP;
            exStyle &= ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE);
            SetWindowLong(window, GWL_STYLE, style);
            SetWindowLong(window, GWL_EXSTYLE, exStyle);
            SetWindowPos(window, IntPtr.Zero, screen.Left, screen.Top, screen.Right - screen.Left, screen.Bottom - screen.Top,
                SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            s_log.Info($"[Overlay] {_gameName}: {what} made borderless on its screen ({screen.Right - screen.Left}x{screen.Bottom - screen.Top}).");
        }

        // ---- Windows --------------------------------------------------------------------------

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const uint GW_OWNER = 4;
        private const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
            WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_MAXIMIZE = 0x01000000,
            WS_POPUP = 0x80000000L, WS_CHILD = 0x40000000;
        private const long WS_EX_DLGMODALFRAME = 0x0001, WS_EX_WINDOWEDGE = 0x0100, WS_EX_CLIENTEDGE = 0x0200, WS_EX_STATICEDGE = 0x20000;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref Point point);

        [DllImport("user32.dll")]
        private static extern bool AdjustWindowRectEx(ref Rect rect, uint style, bool menu, uint exStyle);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        // 32- and 64-bit Playnite alike (GetWindowLongPtr exists only in the 64-bit user32).
        private static IntPtr GetWindowLong(IntPtr window, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

        // The style as a number: its top bit (WS_POPUP) set is too large for a 32-bit IntPtr, which
        // would throw ("Arithmetic operation resulted in an overflow") in 32-bit Playnite.
        private static void SetWindowLong(IntPtr window, int index, long value)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(window, index, new IntPtr(value));
            else
                SetWindowLong32(window, index, unchecked((int)value));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr window, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
    }
}
