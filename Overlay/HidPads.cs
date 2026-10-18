using Microsoft.Win32.SafeHandles;
using Playnite.SDK;
using RomM.Games;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace RomM.Overlay
{
    /// <summary>
    /// The controllers XInput does not see (Switch, PlayStation and other DirectInput pads),
    /// read for the overlay without taking them from the game: each one's HID input is opened
    /// for reading, shared, the way Windows lets any number of programs read a device's input
    /// reports at once (each handle gets its own copy). Nothing is sent to the controller, so
    /// its mode, lights and rumble stay the emulator's.
    ///
    /// Read by report layout, as SDL3's own drivers read them (SDL_hidapi_switch.c,
    /// SDL_hidapi_ps4.c, SDL_hidapi_ps5.c), since an emulator that talks to the controller
    /// directly switches it to a report its HID description does not cover:
    ///
    ///   Switch Pro / Joy-Con / Switch-mode pads (Nintendo's ids)
    ///       0x30, 0x31, 0x21 (full): byte 3 right buttons (Y 01, X 02, B 04, A 08, R 40),
    ///         byte 4 (Minus 01, Plus 02, R-stick 04, L-stick 08, Home 10),
    ///         byte 5 left buttons (down 01, up 02, right 04, left 08, L 40)
    ///       0x3F (simple): byte 1 (B 01, A 02, Y 04, X 08, L 10, R 20),
    ///         byte 2 (Minus 01, Plus 02, L-stick 04, R-stick 08, Home 10), byte 3 the D-pad hat
    ///   DualShock 4 (Sony, not a DualSense)
    ///       0x01 (USB): bytes 5-7; 0x11-0x18 (Bluetooth): bytes 7-9
    ///   DualSense / DualSense Edge
    ///       0x01 from Bluetooth (the simple report): bytes 5-7 as a DualShock 4's;
    ///       0x01 from USB: bytes 8-10; 0x31 (Bluetooth): bytes 9-11
    ///     PlayStation buttons byte: hat in the low nibble (0 up, clockwise, 8 none),
    ///     Square 10, Cross 20, Circle 40, Triangle 80; next byte Share 10, Options 20, L3 40,
    ///     R3 80; next PS 01.
    ///
    /// Any other gamepad: its HID buttons and hat, by the mapping SDL has for it (the monitor's
    /// SDL, ControllerMonitor), SDL's button n being HID button usage n + 1 as DirectInput
    /// orders them. Xbox controllers (Microsoft's ids) are left to XInput.
    ///
    /// <see cref="Buttons"/> is every such controller's buttons together, as XInput's bits.
    /// </summary>
    internal static class HidPads
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, Reader> s_readers = new Dictionary<string, Reader>(StringComparer.OrdinalIgnoreCase);
        private static Thread s_scanner;
        private static readonly HashSet<string> s_told = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool s_running;

        // XInput's bits (XINPUT_GAMEPAD).
        private const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, StartButton = 0x0010, Back = 0x0020,
            L3 = 0x0040, R3 = 0x0080, Guide = 0x0400, A = 0x1000, B = 0x2000;

        private const int Microsoft = 0x045E, Sony = 0x054C, Nintendo = 0x057E;

        /// <summary>The buttons held on all of them, as XInput's bits (Guide, Back, Start, the D-pad, A, B...).</summary>
        public static ushort Buttons
        {
            get
            {
                ushort all = 0;
                lock (s_lock)
                {
                    foreach (Reader reader in s_readers.Values)
                        all |= reader.State;
                }
                return all;
            }
        }

        /// <summary>How many such controllers are being read now.</summary>
        public static int Count
        {
            get
            {
                lock (s_lock)
                    return s_readers.Count;
            }
        }

        /// <summary>Starts reading (a game started); controllers connected later are found as they come.</summary>
        public static void Begin()
        {
            lock (s_lock)
            {
                if (s_running)
                    return;
                s_running = true;
                ControllerMonitor.Listen(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                s_scanner = new Thread(Scan) { IsBackground = true, Name = "RomM overlay HID" };
                s_scanner.Start();
            }
        }

        /// <summary>Stops reading (the game stopped).</summary>
        public static void End()
        {
            List<Reader> readers;
            lock (s_lock)
            {
                if (!s_running)
                    return;
                s_running = false;
                ControllerMonitor.StopListening();
                readers = s_readers.Values.ToList();
                s_readers.Clear();
            }
            foreach (Reader reader in readers)
                reader.Close();
        }

        // Looks for controllers every few seconds, and starts a reader for each new one.
        private static void Scan()
        {
            while (s_running)
            {
                try
                {
                    foreach (string path in HidPaths())
                    {
                        lock (s_lock)
                        {
                            if (!s_running || s_readers.ContainsKey(path))
                                continue;
                        }
                        Reader reader = Reader.Open(path);
                        if (reader == null)
                            continue;
                        lock (s_lock)
                        {
                            if (!s_running)
                            {
                                reader.Close();
                                return;
                            }
                            s_readers[path] = reader;
                        }
                        reader.Start(() =>
                        {
                            lock (s_lock)
                                s_readers.Remove(path);
                        });
                    }
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Overlay] Could not look for controllers: {ex.Message}");
                }
                for (int i = 0; i < 30 && s_running; i++)
                    Thread.Sleep(100);
            }
        }

        // ---- One controller ------------------------------------------------------------------

        private enum Layout { Switch, DualShock4, DualSense, Generic }

        private sealed class Reader
        {
            private SafeFileHandle _handle;
            private IntPtr _preparsed;
            private Layout _layout;
            private int _length;
            private Dictionary<string, string> _mapping;
            private int _hatMin;
            private string _name;
            public volatile ushort State;

            public static Reader Open(string path)
            {
                SafeFileHandle handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                    return null;   // gone, or opened by something that does not share it
                var reader = new Reader { _handle = handle };
                try
                {
                    var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                    if (!HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out reader._preparsed))
                    {
                        reader.Close();
                        return null;
                    }
                    if (HidP_GetCaps(reader._preparsed, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS
                        || caps.UsagePage != 0x01 || (caps.Usage != 0x04 && caps.Usage != 0x05) || caps.InputReportByteLength == 0)
                    {
                        reader.Close();
                        return null;   // not a joystick or gamepad
                    }
                    int vendor = attributes.VendorID, product = attributes.ProductID;
                    reader._length = caps.InputReportByteLength;
                    if (vendor == Microsoft)
                    {
                        reader.Close();
                        return null;   // an Xbox controller: XInput's
                    }
                    if (vendor == Nintendo)
                        reader._layout = Layout.Switch;
                    else if (vendor == Sony && (product == 0x0CE6 || product == 0x0DF2))
                        reader._layout = Layout.DualSense;
                    else if (vendor == Sony)
                        reader._layout = Layout.DualShock4;
                    else
                    {
                        // Another gamepad: only with SDL's mapping for it, which says which of its
                        // buttons is which.
                        ConnectedPad known = ControllerMonitor.Current.FirstOrDefault(p =>
                            p.VendorId == vendor && p.ProductId == product && !string.IsNullOrEmpty(p.Mapping));
                        if (known == null)
                        {
                            // Tried again at the next look (SDL's view of it may not be in yet);
                            // said once.
                            lock (s_told)
                            {
                                if (s_told.Add(path))
                                    s_log.Info($"[Overlay] A controller ({vendor:x4}:{product:x4}) has no button layout SDL or the community list knows, so its buttons cannot be read for the overlay.");
                            }
                            reader.Close();
                            return null;
                        }
                        reader._layout = Layout.Generic;
                        reader._mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        Setup.ControllerProbe.ParseMapping(known.Mapping, reader._mapping);
                        reader._hatMin = HatMinimum(reader._preparsed);
                    }
                    reader._name = $"{reader._layout} {vendor:x4}:{product:x4}";
                    return reader;
                }
                catch
                {
                    reader.Close();
                    return null;
                }
            }

            public void Start(Action gone)
            {
                s_log.Info($"[Overlay] Reading a controller XInput does not see ({_name}).");
                new Thread(() =>
                {
                    var report = new byte[_length];
                    try
                    {
                        while (s_running)
                        {
                            if (!ReadFile(_handle, report, (uint)report.Length, out uint read, IntPtr.Zero) || read == 0)
                                break;   // unplugged, or closed
                            State = Decode(report, (int)read);
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Overlay] {_name}: {ex.Message}");
                    }
                    finally
                    {
                        State = 0;
                        Close();
                        gone();
                    }
                }) { IsBackground = true, Name = "RomM overlay " + _name }.Start();
            }

            public void Close()
            {
                try
                {
                    if (_handle != null && !_handle.IsInvalid && !_handle.IsClosed)
                    {
                        CancelIoEx(_handle, IntPtr.Zero);
                        _handle.Dispose();
                    }
                }
                catch
                {
                }
                IntPtr preparsed = Interlocked.Exchange(ref _preparsed, IntPtr.Zero);
                if (preparsed != IntPtr.Zero)
                    HidD_FreePreparsedData(preparsed);
            }

            private ushort Decode(byte[] r, int length)
            {
                if (length < 1)
                    return 0;
                switch (_layout)
                {
                    case Layout.Switch:
                        return DecodeSwitch(r, length);
                    case Layout.DualShock4:
                        if (r[0] == 0x01 && length > 7)
                            return PlayStation(r[5], r[6], r[7]);
                        if (r[0] >= 0x11 && r[0] <= 0x18 && length > 9)
                            return PlayStation(r[7], r[8], r[9]);
                        return State;
                    case Layout.DualSense:
                        // Report 0x01 is the simple one over Bluetooth (the device's reports are
                        // longer than USB's 64 bytes there), the full one over USB.
                        if (r[0] == 0x01 && _length != 64 && length > 7)
                            return PlayStation(r[5], r[6], r[7]);
                        if (r[0] == 0x01 && length > 10)
                            return PlayStation(r[8], r[9], r[10]);
                        if (r[0] == 0x31 && length > 11)
                            return PlayStation(r[9], r[10], r[11]);
                        return State;
                    default:
                        return DecodeGeneric(r, length);
                }
            }

            private ushort DecodeSwitch(byte[] r, int length)
            {
                ushort s = 0;
                if ((r[0] == 0x30 || r[0] == 0x31 || r[0] == 0x21) && length > 5)
                {
                    byte right = r[3], shared = r[4], left = r[5];
                    if ((right & 0x04) != 0) s |= A;        // south (B on the controller)
                    if ((right & 0x08) != 0) s |= B;        // east (A)
                    if ((shared & 0x01) != 0) s |= Back;    // Minus
                    if ((shared & 0x02) != 0) s |= StartButton;   // Plus
                    if ((shared & 0x04) != 0) s |= R3;
                    if ((shared & 0x08) != 0) s |= L3;
                    if ((shared & 0x10) != 0) s |= Guide;   // Home
                    if ((left & 0x01) != 0) s |= Down;
                    if ((left & 0x02) != 0) s |= Up;
                    if ((left & 0x04) != 0) s |= Right;
                    if ((left & 0x08) != 0) s |= Left;
                    return s;
                }
                if (r[0] == 0x3F && length > 3)
                {
                    byte face = r[1], middle = r[2];
                    if ((face & 0x01) != 0) s |= A;
                    if ((face & 0x02) != 0) s |= B;
                    if ((middle & 0x01) != 0) s |= Back;
                    if ((middle & 0x02) != 0) s |= StartButton;
                    if ((middle & 0x04) != 0) s |= L3;
                    if ((middle & 0x08) != 0) s |= R3;
                    if ((middle & 0x10) != 0) s |= Guide;
                    s |= Hat(r[3]);
                    return s;
                }
                return State;   // another report (a reply): the buttons as they were
            }

            private static ushort PlayStation(byte face, byte middle, byte system)
            {
                ushort s = Hat(face & 0x0F);
                if ((face & 0x20) != 0) s |= A;        // Cross
                if ((face & 0x40) != 0) s |= B;        // Circle
                if ((middle & 0x10) != 0) s |= Back;   // Share / Create
                if ((middle & 0x20) != 0) s |= StartButton;  // Options
                if ((middle & 0x40) != 0) s |= L3;
                if ((middle & 0x80) != 0) s |= R3;
                if ((system & 0x01) != 0) s |= Guide;  // PS
                return s;
            }

            // A hat's 0-7 (up, then clockwise; anything else is none) as D-pad bits.
            private static ushort Hat(int value)
            {
                switch (value)
                {
                    case 0: return Up;
                    case 1: return (ushort)(Up | Right);
                    case 2: return Right;
                    case 3: return (ushort)(Right | Down);
                    case 4: return Down;
                    case 5: return (ushort)(Down | Left);
                    case 6: return Left;
                    case 7: return (ushort)(Left | Up);
                    default: return 0;
                }
            }

            private ushort DecodeGeneric(byte[] r, int length)
            {
                // The buttons pressed (usage page 9), and the hat (generic desktop 0x39).
                var pressed = new HashSet<int>();
                uint count = 64;
                var usages = new ushort[count];
                if (HidP_GetUsages(0, 0x09, 0, usages, ref count, _preparsed, r, (uint)length) == HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < count; i++)
                        pressed.Add(usages[i]);
                }
                int hatBits = 0;
                if (HidP_GetUsageValue(0, 0x01, 0, 0x39, out uint hat, _preparsed, r, (uint)length) == HIDP_STATUS_SUCCESS)
                {
                    ushort dpad = Hat((int)hat - _hatMin);
                    hatBits = ((dpad & Up) != 0 ? 1 : 0) | ((dpad & Right) != 0 ? 2 : 0) | ((dpad & Down) != 0 ? 4 : 0) | ((dpad & Left) != 0 ? 8 : 0);
                }

                ushort s = 0;
                if (Held("guide", pressed, hatBits)) s |= Guide;
                if (Held("back", pressed, hatBits)) s |= Back;
                if (Held("start", pressed, hatBits)) s |= StartButton;
                if (Held("leftstick", pressed, hatBits)) s |= L3;
                if (Held("rightstick", pressed, hatBits)) s |= R3;
                if (Held("a", pressed, hatBits)) s |= A;
                if (Held("b", pressed, hatBits)) s |= B;
                if (Held("dpup", pressed, hatBits)) s |= Up;
                if (Held("dpdown", pressed, hatBits)) s |= Down;
                if (Held("dpleft", pressed, hatBits)) s |= Left;
                if (Held("dpright", pressed, hatBits)) s |= Right;
                return s;
            }

            // Whether the gamepad element SDL's mapping names is held: its button ("b11", HID
            // usage 12) or hat direction ("h0.4"). Axes are not read.
            private bool Held(string element, HashSet<int> pressed, int hatBits)
            {
                if (_mapping == null || !_mapping.TryGetValue(element, out string input) || input.Length < 2)
                    return false;
                if (input[0] == 'b' && int.TryParse(input.Substring(1), out int button))
                    return pressed.Contains(button + 1);
                if (input[0] == 'h')
                {
                    int dot = input.IndexOf('.');
                    if (dot > 0 && int.TryParse(input.Substring(dot + 1), out int mask))
                        return (hatBits & mask) != 0;
                }
                return false;
            }

            // The hat's logical minimum (most report 0-7, some 1-8), from its value caps.
            private static int HatMinimum(IntPtr preparsed)
            {
                try
                {
                    ushort count = 1;
                    var caps = new byte[72 * 4];
                    if (HidP_GetSpecificValueCaps(0, 0x01, 0, 0x39, caps, ref count, preparsed) == HIDP_STATUS_SUCCESS && count > 0)
                        return BitConverter.ToInt32(caps, 40);   // HIDP_VALUE_CAPS.LogicalMin
                }
                catch
                {
                }
                return 0;
            }
        }

        // ---- Finding them --------------------------------------------------------------------

        private static List<string> HidPaths()
        {
            var paths = new List<string>();
            HidD_GetHidGuid(out Guid hid);
            IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1))
                return paths;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref data); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                    if (needed == 0)
                        continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is 8 in a 64-bit process, 6 in a 32-bit one.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out needed, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(detail + 4));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return paths;
        }

        // ---- Windows ---------------------------------------------------------------------------

        private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [In, Out] ushort[] usages,
            ref uint usageLength, IntPtr preparsed, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
            out uint value, IntPtr preparsed, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        private static extern int HidP_GetSpecificValueCaps(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
            [In, Out] byte[] valueCaps, ref ushort valueCapsLength, IntPtr preparsed);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr info);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
