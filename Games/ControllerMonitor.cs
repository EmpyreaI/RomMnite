using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RomM.Games
{
    /// <summary>One controller connected to this computer, as the emulators will see it.</summary>
    public sealed class ConnectedPad
    {
        public string Name { get; set; }
        /// <summary>SDL knows its layout (a "game controller"), so an emulator set to SDL maps it as an Xbox pad.</summary>
        public bool Known { get; set; }
        /// <summary>An XInput device (an Xbox controller, or one acting as one).</summary>
        public bool XInput { get; set; }
        /// <summary>Its USB vendor and product ids (0 when SDL does not know them).</summary>
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        /// <summary>SDL's mapping for it ("guid,name,a:b1,guide:b12,..."), when SDL knows its layout; for reading it directly (Overlay.HidPads).</summary>
        public string Mapping { get; set; }
        /// <summary>SDL's controller type: 4 PS4, 7 PS5, 5 Switch Pro... (0 unknown).</summary>
        public int Type { get; set; }
        /// <summary>
        /// Not known to SDL by itself, but in the community's controller list
        /// (<see cref="ControllerDatabase"/>), which the SDL emulators Playnite starts are given:
        /// mapped as an Xbox pad in those, not necessarily in the others.
        /// </summary>
        public bool InDatabase { get; set; }

        /// <summary>Mapped as an Xbox pad in the SDL emulators started from Playnite.</summary>
        public bool KnownToSdl => Known || InDatabase;

        public bool IsPs4 => Type == 4;
        public bool IsPs5 => Type == 7;

        public override bool Equals(object obj) =>
            obj is ConnectedPad other && other.Name == Name && other.Known == Known && other.XInput == XInput && other.Type == Type
            && other.InDatabase == InDatabase && other.VendorId == VendorId && other.ProductId == ProductId;

        public override int GetHashCode() => (Name ?? "").GetHashCode() ^ (Known ? 1 : 0) ^ (XInput ? 2 : 0) ^ (InDatabase ? 4 : 0);
    }

    /// <summary>
    /// Which controllers are connected, and whether the emulators will know them, for the
    /// library's Controller section. Asked of SDL2 (shipped with the plugin, Native\SDL2), the
    /// same library almost every emulator here reads controllers through: a controller SDL
    /// recognises as a "game controller" gets an Xbox-style layout in them all. XInput devices
    /// are counted apart (Dolphin, Cemu and RPCS3 are bound to them by their XInput slot).
    ///
    /// Only looked at, never opened, so nothing changes for the emulators: SDL's HIDAPI and raw
    /// input drivers are turned off here (HIDAPI opens some controllers and talks to them as
    /// they are found: a Switch Pro controller is sent its start-up handshake), leaving XInput
    /// and DirectInput, which only list devices. Device changes are watched on SDL's own
    /// thread; this asks every second and a half while anything listens, and raises Changed
    /// when the answer differs. Without SDL (the file missing, or it fails to load) only XInput
    /// controllers are seen.
    ///
    /// A controller SDL does not know by itself is looked up in the community's controller list
    /// (ControllerDatabase), which this SDL is not given (so the two stay told apart) but the
    /// emulators are.
    /// </summary>
    public static class ControllerMonitor
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly object s_lock = new object();
        private static List<ConnectedPad> s_pads = new List<ConnectedPad>();
        private static Thread s_thread;
        private static int s_listeners;
        private static string s_folder;
        private static bool s_sdl;
        private static readonly ManualResetEvent s_looked = new ManualResetEvent(false);

        /// <summary>
        /// The controllers connected now, for setting an emulator up just before it starts: the
        /// monitor started if it was not (its first look waited for, up to <paramref name="wait"/>).
        /// </summary>
        public static List<ConnectedPad> Snapshot(string pluginFolder, TimeSpan wait)
        {
            Listen(pluginFolder);
            try
            {
                s_looked.WaitOne(wait);
                return Current;
            }
            finally
            {
                StopListening();
            }
        }

        /// <summary>Whether an XInput controller (an Xbox pad, or one in XInput mode) is connected, asked of XInput directly.</summary>
        public static bool XInputConnected()
        {
            try
            {
                return XInputCount() > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The XInput slot (0-3) of the first Xbox controller connected, or -1: Dolphin, Cemu and
        /// RPCS3 name an XInput device by its slot, which is not always the first (a controller
        /// reconnected, or a second one in before it).
        /// </summary>
        public static int FirstXInputSlot()
        {
            for (int slot = 0; slot < 4; slot++)
            {
                try
                {
                    if (XInputConnected(slot))
                        return slot;
                }
                catch
                {
                    return -1;
                }
            }
            return -1;
        }

        /// <summary>The controllers seen last (on any thread).</summary>
        public static List<ConnectedPad> Current
        {
            get
            {
                lock (s_lock)
                    return s_pads.ToList();
            }
        }

        /// <summary>Whether SDL could be used (else only XInput controllers are seen).</summary>
        public static bool UsingSdl => s_sdl;

        /// <summary>Raised on the monitor's thread when the controllers seen change.</summary>
        public static event Action Changed;

        /// <param name="pluginFolder">The plugin's folder, holding Native\SDL2.</param>
        public static void Listen(string pluginFolder)
        {
            lock (s_lock)
            {
                s_folder = s_folder ?? pluginFolder;
                s_listeners++;
                if (s_thread != null)
                    return;
                s_thread = new Thread(Run) { IsBackground = true, Name = "RomM controller monitor" };
                s_thread.Start();
            }
        }

        public static void StopListening()
        {
            lock (s_lock)
                s_listeners = Math.Max(0, s_listeners - 1);
        }

        private static void Run()
        {
            s_sdl = TryStartSdl();
            while (true)
            {
                int listeners;
                lock (s_lock)
                    listeners = s_listeners;
                if (listeners > 0)
                {
                    try
                    {
                        List<ConnectedPad> now = Look();
                        bool changed;
                        lock (s_lock)
                        {
                            changed = !now.SequenceEqual(s_pads);
                            if (changed)
                                s_pads = now;
                        }
                        s_looked.Set();
                        if (changed)
                            Changed?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Controllers] Could not look at the controllers: {ex.Message}");
                    }
                }
                Thread.Sleep(1500);
            }
        }

        private static List<ConnectedPad> Look()
        {
            var pads = new List<ConnectedPad>();
            if (s_sdl)
            {
                SDL_JoystickUpdate();
                int count = SDL_NumJoysticks();
                for (int i = 0; i < count; i++)
                {
                    bool known = SDL_IsGameController(i) != 0;
                    string name = Utf8(known ? SDL_GameControllerNameForIndex(i) : SDL_JoystickNameForIndex(i)) ?? "Controller";
                    // SDL_CONTROLLER_TYPE_XBOX360 / XBOXONE: what SDL's XInput driver calls the
                    // pads it reads (third-party pads in XInput mode included).
                    int type = known ? SDL_GameControllerTypeForIndex(i) : 0;
                    // Unknown to SDL by itself: perhaps in the community's list, under its name there.
                    bool listed = false;
                    string listedMapping = null;
                    if (!known)
                    {
                        string guid = GuidOf(i);
                        listedMapping = ControllerDatabase.MappingFor(guid);
                        string listedName = ControllerDatabase.NameFor(guid);
                        if (listedName != null)
                        {
                            listed = true;
                            name = listedName;
                        }
                    }
                    pads.Add(new ConnectedPad
                    {
                        Name = name, Known = known, XInput = type == 1 || type == 2, Type = type, InDatabase = listed,
                        VendorId = SDL_JoystickGetDeviceVendor(i), ProductId = SDL_JoystickGetDeviceProduct(i),
                        // SDL's own mapping, else the community list's line for it.
                        Mapping = known ? Utf8Owned(SDL_GameControllerMappingForDeviceIndex(i)) : listedMapping,
                    });
                }
            }
            else
            {
                int xinputCount = XInputCount();
                for (int i = 0; i < xinputCount; i++)
                    pads.Add(new ConnectedPad { Name = "Xbox controller", Known = true, XInput = true, Type = 1 });
            }
            return pads;
        }

        // ---- SDL ---------------------------------------------------------------------------

        private const uint SDL_INIT_GAMECONTROLLER = 0x00002000u;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_SetHintWithPriority([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value, int priority);

        private const int SDL_HINT_OVERRIDE = 2;

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_Init(uint flags);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_JoystickUpdate();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_NumJoysticks();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_IsGameController(int index);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerNameForIndex(int index);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_JoystickNameForIndex(int index);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GameControllerTypeForIndex(int index);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerMappingForDeviceIndex(int index);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_free(IntPtr memory);

        // A string SDL hands over to be freed (SDL_GameControllerMappingForDeviceIndex).
        private static string Utf8Owned(IntPtr text)
        {
            if (text == IntPtr.Zero)
                return null;
            try
            {
                return Utf8(text);
            }
            finally
            {
                SDL_free(text);
            }
        }

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern ushort SDL_JoystickGetDeviceVendor(int index);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern ushort SDL_JoystickGetDeviceProduct(int index);

        // SDL_JoystickGUID: 16 bytes, returned by value.
        [StructLayout(LayoutKind.Sequential)]
        private struct SdlGuid
        {
            public ulong Low;
            public ulong High;
        }

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern SdlGuid SDL_JoystickGetDeviceGUID(int index);

        // A device's GUID as the controller list writes it: its 16 bytes in order, as hex.
        private static string GuidOf(int index)
        {
            try
            {
                SdlGuid guid = SDL_JoystickGetDeviceGUID(index);
                var text = new StringBuilder(32);
                foreach (byte b in BitConverter.GetBytes(guid.Low))
                    text.Append(b.ToString("x2"));
                foreach (byte b in BitConverter.GetBytes(guid.High))
                    text.Append(b.ToString("x2"));
                return text.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static bool TryStartSdl()
        {
            try
            {
                // The build that fits this process, loaded by its full path, so the calls above
                // find it already loaded (whatever other SDL2.dll may be on the path).
                string dll = Path.Combine(s_folder ?? "", "Native", "SDL2", Environment.Is64BitProcess ? "64bit" : "32bit", "SDL2.dll");
                if (!File.Exists(dll) || LoadLibrary(dll) == IntPtr.Zero)
                {
                    s_log.Info($"[Controllers] SDL2 could not be loaded ({dll}); only XInput controllers are seen.");
                    return false;
                }
                SDL_SetHint("SDL_JOYSTICK_HIDAPI", "0");
                SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
                SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
                // Not the community's controller list, which Playnite's environment names for the
                // emulators (ControllerDatabase): SDL's own knowledge is told apart from the
                // list's. Only an override comes before the environment.
                SDL_SetHintWithPriority("SDL_GAMECONTROLLERCONFIG_FILE", "", SDL_HINT_OVERRIDE);
                if (SDL_Init(SDL_INIT_GAMECONTROLLER) != 0)
                {
                    s_log.Info($"[Controllers] SDL could not start: {Utf8(SDL_GetError())}; only XInput controllers are seen.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                s_log.Info($"[Controllers] SDL2 is not usable here ({ex.Message}); only XInput controllers are seen.");
                return false;
            }
        }

        private static string Utf8(IntPtr text)
        {
            if (text == IntPtr.Zero)
                return null;
            int length = 0;
            while (Marshal.ReadByte(text, length) != 0)
                length++;
            var bytes = new byte[length];
            Marshal.Copy(text, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        // ---- XInput ------------------------------------------------------------------------

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
        private static extern uint XInputGetState14(uint index, out XInputState state);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint index, out XInputState state);

        private static bool s_xinput14 = true;

        // Whether an XInput slot has a controller.
        private static bool XInputConnected(int slot)
        {
            try
            {
                return (s_xinput14 ? XInputGetState14((uint)slot, out XInputState _) : XInputGetState910((uint)slot, out XInputState _)) == 0;
            }
            catch (DllNotFoundException) when (s_xinput14)
            {
                s_xinput14 = false;   // Windows 7: the older XInput
                return XInputGetState910((uint)slot, out XInputState _) == 0;
            }
        }

        // How many of the four XInput slots have a controller (0 when XInput is missing).
        private static int XInputCount()
        {
            int count = 0;
            for (uint i = 0; i < 4; i++)
            {
                try
                {
                    uint result = s_xinput14 ? XInputGetState14(i, out XInputState _) : XInputGetState910(i, out XInputState _);
                    if (result == 0)
                        count++;
                }
                catch (DllNotFoundException) when (s_xinput14)
                {
                    s_xinput14 = false;   // Windows 7: the older XInput
                    i--;
                }
                catch
                {
                    return count;
                }
            }
            return count;
        }
    }
}
