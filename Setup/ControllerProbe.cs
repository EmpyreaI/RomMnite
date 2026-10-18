using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RomM.Setup
{
    /// <summary>One controller as an emulator's SDL3 sees it (ControllerProbe).</summary>
    internal sealed class ProbedPad
    {
        /// <summary>SDL's device id, its 16 bytes as 32 hex digits (SDL_GUIDToString's form).</summary>
        public string Guid;
        /// <summary>The name SDL gives it: the gamepad's name when SDL maps it, else the joystick's.</summary>
        public string Name;
        /// <summary>SDL maps it as a gamepad (an Xbox-style layout).</summary>
        public bool IsGamepad;
        public int VendorId, ProductId;
        /// <summary>It has a gyroscope (only asked for when wanted).</summary>
        public bool HasGyro;
        /// <summary>Its mapping: gamepad element ("a", "leftx", "dpup"...) -> the joystick's input ("b1", "a2", "h0.4").</summary>
        public Dictionary<string, string> Mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The id with the two bytes of the name's checksum zeroed, as Eden keeps it (sdl_driver.cpp, GetGUID).</summary>
        public string GuidWithoutCrc => Guid == null || Guid.Length != 32 ? Guid : Guid.Substring(0, 4) + "0000" + Guid.Substring(8);
    }

    /// <summary>
    /// What an emulator's SDL3 will see, asked just before it starts: the emulators that name a
    /// controller by SDL's device id (Cemu, Eden) or by SDL's name for it (Dolphin, RPCS3) cannot
    /// be told it ahead of time from what RomMnite's own controller monitor sees, as they let SDL
    /// talk to PlayStation and Switch controllers directly (its HIDAPI drivers), which gives those
    /// another id and name, while the monitor deliberately does not (ControllerMonitor).
    ///
    /// So, for a moment, SDL3 is started here with the emulator's own SDL settings (the hints
    /// read from each one's source), the connected controllers are listed with their id, name,
    /// mapping and gyroscope, and SDL3 is shut down again; the emulator then starts and finds
    /// them as they were listed. What SDL says to a controller as it is found (a Switch
    /// controller's handshake) it says again a second later in the emulator.
    ///
    /// SDL3 is the official release (github.com/libsdl-org/SDL, zlib licence), SDL3.dll fetched
    /// into Tools\SDL3\&lt;x64|x86&gt; in the plugin's data the first time an emulator that needs
    /// it is set up; until it is there, nothing is asked and the emulators keep the bindings they
    /// could be given without it. SDL3 reads the community controller list Playnite's environment
    /// names (ControllerDatabase), as the emulators do.
    /// </summary>
    internal static class ControllerProbe
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly object s_lock = new object();
        private static string s_toolsFolder;
        private static bool s_loaded;
        private static int s_fetching;
        private static readonly ManualResetEvent s_fetched = new ManualResetEvent(false);

        /// <summary>Where Tools\ is (the plugin's data); set at start.</summary>
        public static void SetToolsFolder(string folder) => s_toolsFolder = folder;

        private static string Arch => Environment.Is64BitProcess ? "x64" : "x86";

        private static string DllPath => s_toolsFolder == null ? null : Path.Combine(s_toolsFolder, "SDL3", Arch, "SDL3.dll");

        /// <summary>Whether SDL3 is here, so the emulators that need it can be given controllers.</summary>
        public static bool Available
        {
            get
            {
                string dll = DllPath;
                return dll != null && File.Exists(dll);
            }
        }

        /// <summary>SDL3 fetched in the background when it is not here yet (once a session).</summary>
        public static void EnsureInBackground()
        {
            if (Available || DllPath == null || Interlocked.Exchange(ref s_fetching, 1) == 1)
                return;
            s_fetched.Reset();
            new Thread(() =>
            {
                try
                {
                    Fetch();
                }
                catch (Exception ex)
                {
                    // Not tried again this session, so no other launch waits for it: next session.
                    s_log.Warn($"[Controllers] Could not download SDL3: {ex.Message}");
                }
                finally
                {
                    s_fetched.Set();
                }
            }) { IsBackground = true, Name = "RomM SDL3 download" }.Start();
        }

        // The newest SDL 3 release's Windows build for this process (SDL3-<version>-win32-<arch>.zip,
        // build-release.py), SDL3.dll from it.
        private static void Fetch()
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("RomMnite");
                string json = client.GetStringAsync("https://api.github.com/repos/libsdl-org/SDL/releases?per_page=30").GetAwaiter().GetResult();
                string url = null;
                foreach (JToken release in JArray.Parse(json))
                {
                    if ((bool?)release["prerelease"] == true || (bool?)release["draft"] == true
                        || !((string)release["tag_name"] ?? "").StartsWith("release-3.", StringComparison.Ordinal))
                        continue;
                    url = release["assets"]?
                        .Select(a => (string)a["browser_download_url"])
                        .FirstOrDefault(u => u != null && u.EndsWith("-win32-" + Arch + ".zip", StringComparison.OrdinalIgnoreCase)
                                             && Path.GetFileName(u).StartsWith("SDL3-", StringComparison.OrdinalIgnoreCase));
                    if (url != null)
                        break;
                }
                if (url == null)
                    throw new InvalidOperationException("no SDL 3 Windows build in its recent releases");

                byte[] zip = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                using (var archive = new ZipArchive(new MemoryStream(zip)))
                {
                    ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "SDL3.dll", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        throw new InvalidOperationException("SDL3.dll not in " + Path.GetFileName(url));
                    string dll = DllPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(dll));
                    string temp = dll + ".new";
                    entry.ExtractToFile(temp, true);
                    if (File.Exists(dll))
                        File.Delete(dll);
                    File.Move(temp, dll);
                }
                s_log.Info($"[Controllers] SDL3 is ready ({Path.GetFileName(url)}), for setting up controllers in Cemu, Eden, Dolphin and RPCS3.");
            }
        }

        /// <summary>
        /// The connected controllers as SDL3 sees them with these hints (an emulator's), in SDL's
        /// order; null when SDL3 is not here (and could not be fetched in time) or could not be asked. <paramref name="gyro"/>: open
        /// each gamepad to see whether it has a gyroscope.
        /// </summary>
        public static List<ProbedPad> Probe(IDictionary<string, string> hints, bool gyro = false)
        {
            if (!Available)
            {
                // The first game in one of these emulators: SDL3 (a few MB) fetched now, waited
                // for a little, so that game already starts with its controller.
                EnsureInBackground();
                s_fetched.WaitOne(TimeSpan.FromSeconds(10));
                if (!Available)
                    return null;
            }
            lock (s_lock)
            {
                List<ProbedPad> result = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        result = Ask(hints, gyro);
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Controllers] SDL3 could not list the controllers: {ex.Message}");
                    }
                }) { IsBackground = true, Name = "RomM SDL3 probe" };
                thread.Start();
                if (!thread.Join(TimeSpan.FromSeconds(8)))
                {
                    s_log.Warn("[Controllers] SDL3 took too long to list the controllers; going on without it.");
                    return null;
                }
                return result;
            }
        }

        private static List<ProbedPad> Ask(IDictionary<string, string> hints, bool gyro)
        {
            if (!s_loaded)
            {
                if (LoadLibrary(DllPath) == IntPtr.Zero)
                    throw new InvalidOperationException("SDL3.dll could not be loaded");
                s_loaded = true;
            }

            SDL_ResetHints();
            SDL_SetHint("SDL_NO_SIGNAL_HANDLERS", "1");
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            foreach (KeyValuePair<string, string> hint in hints ?? new Dictionary<string, string>())
                SDL_SetHint(hint.Key, hint.Value);
            if (!SDL_Init(SDL_INIT_GAMEPAD))
                throw new InvalidOperationException("SDL_Init: " + Utf8(SDL_GetError()));
            try
            {
                // Controllers found by SDL's drivers as they start up, some a moment later: asked
                // until the count holds.
                uint[] ids = new uint[0];
                int steady = 0;
                for (int i = 0; i < 15 && steady < 3; i++)
                {
                    SDL_UpdateJoysticks();
                    uint[] now = Joysticks();
                    steady = now.Length == ids.Length ? steady + 1 : 0;
                    ids = now;
                    Thread.Sleep(100);
                }

                var pads = new List<ProbedPad>();
                foreach (uint id in ids)
                {
                    var pad = new ProbedPad
                    {
                        Guid = GuidString(SDL_GetJoystickGUIDForID(id)),
                        IsGamepad = SDL_IsGamepad(id),
                        VendorId = SDL_GetJoystickVendorForID(id),
                        ProductId = SDL_GetJoystickProductForID(id),
                    };
                    pad.Name = Utf8(pad.IsGamepad ? SDL_GetGamepadNameForID(id) : SDL_GetJoystickNameForID(id))
                               ?? Utf8(SDL_GetJoystickNameForID(id)) ?? "Unknown";
                    if (pad.IsGamepad)
                    {
                        IntPtr mapping = SDL_GetGamepadMappingForID(id);
                        if (mapping != IntPtr.Zero)
                        {
                            ParseMapping(Utf8(mapping), pad.Mapping);
                            SDL_free(mapping);
                        }
                        if (gyro)
                        {
                            IntPtr gamepad = SDL_OpenGamepad(id);
                            if (gamepad != IntPtr.Zero)
                            {
                                pad.HasGyro = SDL_GamepadHasSensor(gamepad, SDL_SENSOR_GYRO);
                                SDL_CloseGamepad(gamepad);
                            }
                        }
                    }
                    pads.Add(pad);
                }
                s_log.Info("[Controllers] SDL3 sees: " + (pads.Count == 0 ? "no controllers"
                    : string.Join("; ", pads.Select(p => $"{p.Name} ({p.Guid}{(p.IsGamepad ? "" : ", not a gamepad")}{(p.HasGyro ? ", gyro" : "")})"))));
                return pads;
            }
            finally
            {
                SDL_Quit();
            }
        }

        private static uint[] Joysticks()
        {
            IntPtr list = SDL_GetJoysticks(out int count);
            if (list == IntPtr.Zero)
                return new uint[0];
            try
            {
                var ids = new uint[Math.Max(0, count)];
                for (int i = 0; i < ids.Length; i++)
                    ids[i] = unchecked((uint)Marshal.ReadInt32(list, i * 4));
                return ids;
            }
            finally
            {
                SDL_free(list);
            }
        }

        // "guid,name,a:b0,b:b1,leftx:a0,dpup:h0.1,...,platform:Windows," -> element -> input. An
        // element written with a +/- (half an axis) is kept under its plain name too, the first
        // one winning.
        internal static void ParseMapping(string mapping, Dictionary<string, string> into)
        {
            if (string.IsNullOrEmpty(mapping))
                return;
            string[] parts = mapping.Split(',');
            for (int i = 2; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon <= 0)
                    continue;
                string key = parts[i].Substring(0, colon).Trim();
                string value = parts[i].Substring(colon + 1).Trim();
                if (key.Length == 0 || value.Length == 0 || key == "platform" || key == "crc" || key == "type" || key == "hint" || key == "sdk>=" || key == "sdk<=")
                    continue;
                string plain = key.TrimStart('+', '-');
                if (!into.ContainsKey(key))
                    into[key] = value;
                if (!into.ContainsKey(plain))
                    into[plain] = value;
            }
        }

        // SDL_GUIDToString: the 16 bytes in order, as lowercase hex.
        private static string GuidString(SdlGuid guid)
        {
            var text = new StringBuilder(32);
            foreach (byte b in BitConverter.GetBytes(guid.Low))
                text.Append(b.ToString("x2"));
            foreach (byte b in BitConverter.GetBytes(guid.High))
                text.Append(b.ToString("x2"));
            return text.ToString();
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

        // ---- SDL3 (SDL_init.h, SDL_hints.h, SDL_joystick.h, SDL_gamepad.h, SDL_sensor.h) --------

        private const uint SDL_INIT_GAMEPAD = 0x00002000u;   // implies JOYSTICK and EVENTS
        private const int SDL_SENSOR_GYRO = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct SdlGuid
        {
            public ulong Low;
            public ulong High;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_ResetHints();

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_SetHint([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_Init(uint flags);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_Quit();

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_UpdateJoysticks();

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetJoysticks(out int count);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_free(IntPtr memory);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern SdlGuid SDL_GetJoystickGUIDForID(uint id);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_IsGamepad(uint id);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetGamepadNameForID(uint id);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetJoystickNameForID(uint id);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetGamepadMappingForID(uint id);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern ushort SDL_GetJoystickVendorForID(uint id);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern ushort SDL_GetJoystickProductForID(uint id);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_OpenGamepad(uint id);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_GamepadHasSensor(IntPtr gamepad, int type);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_CloseGamepad(IntPtr gamepad);
    }
}
