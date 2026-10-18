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
    /// </summary>
    internal static class InputActivity
    {
        private static readonly object s_lock = new object();
        private static Thread s_thread;
        private static long s_lastPadTick = long.MinValue;   // Environment.TickCount-based, in ms (64-bit)

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

        /// <summary>Whether a controller was used more recently than the keyboard and mouse.</summary>
        public static bool ControllerInUse
        {
            get
            {
                long pad = Interlocked.Read(ref s_lastPadTick);
                if (pad == long.MinValue)
                    return false;
                long keyboard = LastKeyboardMouseTick();
                return keyboard == long.MinValue || pad > keyboard;
            }
        }

        /// <summary>A controller read another way (HidPads: Switch, PlayStation...) was used now.</summary>
        public static void NotePad() => Interlocked.Exchange(ref s_lastPadTick, Now());

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
                    for (uint i = 0; i < 4; i++)
                    {
                        if (XInputGetState(i, out XInputState state) != 0)
                        {
                            packets[i] = null;
                            continue;
                        }
                        // The first reading is only where it starts; a change after it is use.
                        // Stick drift moves the packet number too, so only presses and real
                        // stick and trigger movement count.
                        if (packets[i].HasValue && packets[i].Value != state.PacketNumber && Meaningful(state))
                            Interlocked.Exchange(ref s_lastPadTick, Now());
                        packets[i] = state.PacketNumber;
                    }
                }
                catch (DllNotFoundException)
                {
                    return;   // no XInput: the keyboard is what is in use
                }
                catch
                {
                    // Next time.
                }
                Thread.Sleep(120);
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
