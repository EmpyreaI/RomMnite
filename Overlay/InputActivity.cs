using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RomM.Overlay
{
    /// <summary>
    /// Which kind of input was used last, a controller or the keyboard and mouse, so prompts show
    /// only the buttons of the one in use (as Steam's do). Controllers are watched through XInput
    /// (a packet number that moves whenever a pad's state changes; read without taking the pad
    /// from anything); the keyboard and mouse through Windows' own last-input time, which
    /// controllers do not move. Other controllers (Switch, PlayStation...) are noted as used by
    /// the overlay, which reads them while a game runs (HidPads, NotePad).
    ///
    /// What the prompts show (<see cref="ControllerInUse"/>, told as it changes by
    /// <see cref="Changed"/>): a controller's buttons while one is connected and was used (or
    /// plugged in, or connected as the game started) at least as lately as the keyboard and
    /// mouse; the keyboard's otherwise.
    ///
    /// While a game runs, the keyboard and mouse are watched directly (<see cref="NoteKeyboard"/>
    /// from the overlay's keyboard hook, and a mouse hook here on a thread of its own), and only
    /// input from the devices themselves counts: input made by programs (marked injected: Steam
    /// Input turning a controller into keys and mouse movement outside Steam games, its gyro
    /// and trackpads) does not, nor does the mouse moving by a few pixels. Windows' own
    /// last-input time, which counts all of that, is used only when not watching.
    /// </summary>
    internal static class InputActivity
    {
        private static readonly object s_lock = new object();
        private static Thread s_thread;
        private static long s_lastPadTick = long.MinValue;   // Environment.TickCount-based, in ms (64-bit)
        private static volatile bool s_connected;
        private static volatile bool s_controllerShown;

        /// <summary>Whether the prompts show a controller's buttons changed (on this class's thread).</summary>
        public static event Action Changed;

        private const long OwnInputMs = 500;
        private static long s_lastKeyTick = long.MinValue;   // real keyboard or mouse use, while watched
        private static volatile bool s_watching;

        public static void Start()
        {
            lock (s_lock)
            {
                if (s_thread != null)
                    return;
                s_thread = new Thread(Run) { IsBackground = true, Name = "RomM input activity" };
                s_thread.Start();
            }
        }

        /// <summary>Whether the prompts show a controller's buttons (else the keyboard's).</summary>
        public static bool ControllerInUse => Decide();

        private static bool Decide()
        {
            if (!s_connected && !ConnectedNow())
                return false;
            long pad = Interlocked.Read(ref s_lastPadTick);
            if (pad == long.MinValue)
                return false;
            if (s_watching)
            {
                long key = Interlocked.Read(ref s_lastKeyTick);
                return key == long.MinValue || key <= pad;
            }
            long keyboard = LastKeyboardMouseTick();
            return keyboard == long.MinValue || keyboard <= pad + OwnInputMs;
        }

        /// <summary>A key pressed on the keyboard itself (not one a program made).</summary>
        public static void NoteKeyboard()
        {
            Interlocked.Exchange(ref s_lastKeyTick, Now());
            Recheck();
        }

        // ---- The mouse, watched while a game runs -------------------------------------------------

        private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInfo
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hook, LowLevelMouseProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string module);

        private const int WH_MOUSE_LL = 14;
        private const uint WM_QUIT = 0x0012, WM_MOUSEMOVE = 0x0200;
        private const uint LLMHF_INJECTED = 0x01;
        private const int MoveThreshold = 12;   // pixels: a nudge of the desk is not use

        private static LowLevelMouseProc s_mouseProc;   // kept alive while hooked
        private static uint s_mouseThread;
        private static int s_anchorX = int.MinValue, s_anchorY;

        /// <summary>A game started: the keyboard (the overlay's hook tells) and the mouse watched.</summary>
        public static void Watch()
        {
            lock (s_lock)
            {
                if (s_watching)
                    return;
                Interlocked.Exchange(ref s_lastKeyTick, long.MinValue);
                s_anchorX = int.MinValue;
                s_watching = true;
                var thread = new Thread(MouseLoop) { IsBackground = true, Name = "RomM input mouse" };
                thread.Start();
            }
        }

        /// <summary>The game stopped: Windows' last-input time again.</summary>
        public static void StopWatching()
        {
            lock (s_lock)
            {
                if (!s_watching)
                    return;
                s_watching = false;
                if (s_mouseThread != 0)
                    PostThreadMessage(s_mouseThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }

        // The hook on a thread of its own with its own message loop, so the mouse is never held
        // up behind Playnite's window (a low-level hook runs on the thread that set it).
        private static void MouseLoop()
        {
            IntPtr hook = IntPtr.Zero;
            uint me = GetCurrentThreadId();
            try
            {
                s_mouseThread = me;
                s_mouseProc = OnMouse;
                hook = SetWindowsHookEx(WH_MOUSE_LL, s_mouseProc, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero)
                    return;
                while (s_watching && GetMessage(out Msg message, IntPtr.Zero, 0, 0) > 0)
                {
                }
            }
            catch
            {
                // Not watched: the keyboard alone tells.
            }
            finally
            {
                if (hook != IntPtr.Zero)
                    UnhookWindowsHookEx(hook);
                // Not one started since for the next game.
                if (s_mouseThread == me)
                    s_mouseThread = 0;
            }
        }

        private static IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            try
            {
                // No longer watching (the quit message missed): this thread ends its loop.
                if (!s_watching)
                    PostThreadMessage(GetCurrentThreadId(), WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                else if (code >= 0)
                {
                    var info = (MouseInfo)Marshal.PtrToStructure(data, typeof(MouseInfo));
                    if ((info.Flags & LLMHF_INJECTED) == 0)
                    {
                        if ((uint)message != WM_MOUSEMOVE)
                            NoteKeyboard();   // a click or the wheel
                        else if (s_anchorX == int.MinValue)
                        {
                            s_anchorX = info.X;
                            s_anchorY = info.Y;
                        }
                        else if (Math.Abs(info.X - s_anchorX) + Math.Abs(info.Y - s_anchorY) > MoveThreshold)
                        {
                            s_anchorX = info.X;
                            s_anchorY = info.Y;
                            NoteKeyboard();
                        }
                    }
                }
            }
            catch
            {
                // Passed on as it is.
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        /// <summary>A controller was used now (a press the overlay read: XInput's, or HidPads' Switch, PlayStation...).</summary>
        public static void NotePad()
        {
            Interlocked.Exchange(ref s_lastPadTick, Now());
            Recheck();
        }

        /// <summary>
        /// A game started: a controller connected now counts as the one to show, as if just
        /// used (the game was most likely started with the mouse).
        /// </summary>
        public static void GameStarted()
        {
            s_connected = ConnectedNow();
            if (s_connected)
                Interlocked.Exchange(ref s_lastPadTick, Now());
            Recheck();
        }

        private static bool ConnectedNow()
        {
            try
            {
                return Games.ControllerMonitor.XInputConnected() || HidPads.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        // Tells the prompts when what they show changes.
        private static void Recheck()
        {
            bool shown = Decide();
            if (shown == s_controllerShown)
                return;
            s_controllerShown = shown;
            try
            {
                Changed?.Invoke();
            }
            catch
            {
                // A prompt gone.
            }
        }

        // ---- The controller ------------------------------------------------------------------

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

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint index, out XInputState state);

        private static void Run()
        {
            var packets = new uint?[4];
            while (true)
            {
                try
                {
                    bool any = false;
                    for (uint i = 0; i < 4; i++)
                    {
                        if (XInputGetState(i, out XInputState state) != 0)
                        {
                            packets[i] = null;
                            continue;
                        }
                        any = true;
                        // The first reading is only where it starts; a change after it is use.
                        // Stick drift moves the packet number too, so only presses and real
                        // stick and trigger movement count.
                        if (packets[i].HasValue && packets[i].Value != state.PacketNumber && Meaningful(state))
                            Interlocked.Exchange(ref s_lastPadTick, Now());
                        packets[i] = state.PacketNumber;
                    }
                    // Plugged in: shown at once, as if used; all unplugged: the keyboard's.
                    bool connected = any || HidPads.Count > 0;
                    if (connected && !s_connected)
                        Interlocked.Exchange(ref s_lastPadTick, Now());
                    s_connected = connected;
                }
                catch (DllNotFoundException)
                {
                    s_connected = HidPads.Count > 0;
                }
                catch
                {
                    // Next time.
                }
                Recheck();
                Thread.Sleep(100);
            }
        }

        private static bool Meaningful(XInputState state)
        {
            const short Stick = 12000;
            return state.Buttons != 0 || state.LeftTrigger > 40 || state.RightTrigger > 40
                || Math.Abs((int)state.ThumbLX) > Stick || Math.Abs((int)state.ThumbLY) > Stick
                || Math.Abs((int)state.ThumbRX) > Stick || Math.Abs((int)state.ThumbRY) > Stick;
        }

        // ---- The keyboard and mouse -------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        private static long Now() => (long)GetTickCount64();

        // When the keyboard or mouse was last used, on Now()'s clock. GetLastInputInfo gives the
        // low 32 bits of the tick count; it is taken as within the last 49 days.
        private static long LastKeyboardMouseTick()
        {
            try
            {
                var info = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
                if (!GetLastInputInfo(ref info))
                    return long.MinValue;
                long now = Now();
                uint ago = unchecked((uint)now - info.Time);
                return now - ago;
            }
            catch
            {
                return long.MinValue;
            }
        }
    }
}
