using Playnite.SDK;
using RomM.Account;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RomM.Setup
{
    /// <summary>The graphics choices in RomMnite's settings, applied to every emulator that has them.</summary>
    internal sealed class GraphicsChoice
    {
        /// <summary>"Original", "2x", "3x", "4x" or "Screen" (the display's resolution).</summary>
        public string Resolution = "Screen";
        public bool Widescreen;
        public bool Stretch;
        public bool VSync = true;

        /// <summary>The scale chosen: 1 for the console's own, 2 to 4, or 0 for the display's.</summary>
        public int Scale => Resolution == "Original" ? 1 : Resolution == "2x" ? 2 : Resolution == "3x" ? 3 : Resolution == "4x" ? 4 : 0;
    }

    /// <summary>
    /// RomMnite's graphics choices (resolution, widescreen, stretching, VSync) written into each
    /// emulator's own settings just before a game starts (EmulatorLaunch.PrepareSettings),
    /// every time: RomMnite's choice wins over one made in the emulator. An emulator's per-game
    /// settings, where it has them, still win over both.
    ///
    /// Resolution "Screen" is the display's own: the monitor Playnite is on, in its real
    /// pixels. Where an emulator scales to its window by itself, that is used; elsewhere the
    /// smallest scale reaching the display's height (H) is worked out.
    ///
    ///   DuckStation  settings.ini [GPU] ResolutionScale (0 automatic), PGXPEnable (on whenever
    ///                upscaled: without it, 3D wobbles and warps at higher resolutions),
    ///                WidescreenHack; [Display] AspectRatio ("Auto (Game Native)", "Stretch To
    ///                Fill", "16:9" with widescreen, as its own widescreen toggle does), VSync
    ///   PCSX2        PCSX2.ini [EmuCore/GS] upscale_multiplier (ceil(H / 480), up to 8),
    ///                AspectRatio ("Auto 4:3/3:2", which turns 16:9 by itself where a widescreen
    ///                patch applies, or "Stretch"), VsyncEnable; [EmuCore] EnableWideScreenPatches
    ///   Dolphin      GFX.ini [Settings] InternalResolution (0 EFB_SCALE_AUTO_INTEGRAL),
    ///                wideScreenHack, AspectRatio (0 Auto, 1 ForceWide, 3 Stretch); [Hardware] VSync
    ///   RPCS3        config.yml Video: Resolution Scale (percent of 720p), Stretch To Display
    ///                Area, VSync Mode ("Disabled", "Full"). PS3 games are widescreen already.
    ///   Xenia        xenia-canary.config.toml [GPU] draw_resolution_scale_x/_y (ceil(H / 720),
    ///                Xenia lowers it to what the graphics card supports), vsync; [Display]
    ///                present_letterbox (off stretches). Xbox 360 games are widescreen already.
    ///   Vita3K       config.yml resolution-multiplier, stretch_the_display_area, v-sync
    ///   Flycast      emu.cfg [config] rend.Resolution (half multiples of 480), rend.WideScreen
    ///                and rend.WidescreenGameHacks, rend.ScreenStretching (percent: 133 stretches
    ///                4:3 to 16:9), rend.vsync
    ///   xemu         xemu.toml [display.quality] surface_scale (ceil(H / 480), up to 6),
    ///                [display.ui] fit ("scale", "stretch"), [display.window] vsync
    ///   Azahar       qt-config.ini [Renderer] resolution_factor (0 the window's), use_vsync
    ///   PPSSPP       ppsspp.ini [Graphics] InternalResolution (0 automatic), VerticalSync.
    ///                The PSP's screen is widescreen already.
    ///
    /// Keys and values read from their source (DuckStation settings.cpp, PCSX2 Pcsx2Config.cpp
    /// and Patch.cpp, Dolphin GraphicsSettings.cpp and VideoConfig.h, RPCS3 system_config.h,
    /// Xenia texture_cache.cc, gpu_flags.cc and presenter.cc, Vita3K config.h, Flycast
    /// option.cpp, xemu config_spec.yml, Azahar settings.h, PPSSPP Config.cpp). RetroArch (each
    /// core has its own options), Cemu (graphics packs per game) and Eden are left as they are.
    /// </summary>
    internal static class EmulatorGraphics
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        /// <param name="id">The emulator's built-in id and name, lower case.</param>
        public static void Apply(string id, string installDir, GraphicsChoice choice)
        {
            if (choice == null)
                return;
            int height = choice.Scale == 0 ? NativeHeight() : 0;
            foreach (Setting setting in For(id, installDir, choice, height))
                Write(setting);
        }

        // ---- Each emulator's settings ------------------------------------------------------------

        private sealed class Setting
        {
            public string Emulator;
            public string File;
            public string Name;
            public string Wanted;
            public Func<string> Read;          // null when not set
            public Func<string, bool> Write;   // whether the file changed
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static string TitleBool(bool value) => value ? "True" : "False";

        private static IEnumerable<Setting> For(string id, string installDir, GraphicsChoice g, int h)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            int n = g.Scale;
            bool knownHeight = h > 0;   // for "Screen" where the emulator needs the number
            if (id.Contains("duckstation"))
            {
                string ini = Path.Combine(DuckStationFirstRun.DataRoot(installDir), "settings.ini");
                if (File.Exists(ini))
                {
                    yield return Ini("DuckStation", ini, "GPU", "ResolutionScale", Number(n));
                    yield return Ini("DuckStation", ini, "GPU", "PGXPEnable", Bool(n != 1));
                    yield return Ini("DuckStation", ini, "GPU", "WidescreenHack", Bool(g.Widescreen));
                    yield return Ini("DuckStation", ini, "Display", "AspectRatio", g.Widescreen ? "16:9" : g.Stretch ? "Stretch To Fill" : "Auto (Game Native)");
                    yield return Ini("DuckStation", ini, "Display", "VSync", Bool(g.VSync));
                }
            }
            else if (id.Contains("dolphin"))
            {
                bool portable = File.Exists(Path.Combine(installDir, "portable.txt")) || Directory.Exists(Path.Combine(installDir, "User"));
                string user = portable ? Path.Combine(installDir, "User") : Path.Combine(documents, "Dolphin Emulator");
                if (portable || Directory.Exists(user))
                {
                    string gfx = Path.Combine(user, "Config", "GFX.ini");
                    yield return Ini("Dolphin", gfx, "Settings", "InternalResolution", Number(n), create: true);
                    yield return Ini("Dolphin", gfx, "Settings", "wideScreenHack", TitleBool(g.Widescreen), create: true);
                    yield return Ini("Dolphin", gfx, "Settings", "AspectRatio", g.Widescreen ? "1" : g.Stretch ? "3" : "0", create: true);
                    yield return Ini("Dolphin", gfx, "Hardware", "VSync", TitleBool(g.VSync), create: true);
                }
            }
            else if (id.Contains("azahar"))
            {
                string user = Path.Combine(installDir, "user");
                if (Directory.Exists(user))
                {
                    string ini = Path.Combine(user, "config", "qt-config.ini");
                    yield return QtIni("Azahar", ini, "Renderer", "resolution_factor", "1", Number(n));
                    yield return QtIni("Azahar", ini, "Renderer", "use_vsync", "true", Bool(g.VSync));
                }
            }
            else if (id.Contains("pcsx2"))
            {
                string ini = new[] { Path.Combine(installDir, "inis", "PCSX2.ini"), Path.Combine(documents, "PCSX2", "inis", "PCSX2.ini") }.FirstOrDefault(File.Exists);
                if (ini != null)
                {
                    if (n != 0 || knownHeight)
                        yield return Ini("PCSX2", ini, "EmuCore/GS", "upscale_multiplier", Number(n != 0 ? n : Clamp(Up(h, 480), 1, 8)));
                    yield return Ini("PCSX2", ini, "EmuCore", "EnableWideScreenPatches", Bool(g.Widescreen));
                    yield return Ini("PCSX2", ini, "EmuCore/GS", "AspectRatio", g.Stretch ? "Stretch" : "Auto 4:3/3:2");
                    yield return Ini("PCSX2", ini, "EmuCore/GS", "VsyncEnable", Bool(g.VSync));
                }
            }
            else if (id.Contains("rpcs3"))
            {
                // Beside it, or in portable\; on Windows in a config\ folder inside that.
                string root = Directory.Exists(Path.Combine(installDir, "portable")) ? Path.Combine(installDir, "portable") : installDir;
                string yml = new[] { Path.Combine(root, "config", "config.yml"), Path.Combine(root, "config.yml") }.FirstOrDefault(File.Exists);
                if (yml != null)
                {
                    if (n != 0 || knownHeight)
                        yield return Yaml("RPCS3", yml, "Video", "Resolution Scale", Number(n != 0 ? n * 100 : Clamp(Up(h * 100, 720), 100, 800)));
                    yield return Yaml("RPCS3", yml, "Video", "Stretch To Display Area", Bool(g.Stretch));
                    yield return Yaml("RPCS3", yml, "Video", "VSync Mode", g.VSync ? "Full" : "Disabled");
                }
            }
            else if (id.Contains("xenia"))
            {
                string toml = Path.Combine(Saves.Handlers.EmulatorActiveUser.XeniaRoot(installDir), "xenia-canary.config.toml");
                if (File.Exists(toml))
                {
                    if (n != 0 || knownHeight)
                    {
                        string scale = Number(n != 0 ? n : Clamp(Up(h, 720), 1, 3));
                        yield return Toml("Xenia", toml, "GPU", "draw_resolution_scale_x", scale, add: false);
                        yield return Toml("Xenia", toml, "GPU", "draw_resolution_scale_y", scale, add: false);
                    }
                    yield return Toml("Xenia", toml, "GPU", "vsync", Bool(g.VSync), add: false);
                    yield return Toml("Xenia", toml, "Display", "present_letterbox", Bool(!g.Stretch), add: false);
                }
            }
            else if (id.Contains("vita3k"))
            {
                string yml = Path.Combine(installDir, "portable", "config.yml");
                if (File.Exists(yml))
                {
                    if (n != 0 || knownHeight)
                    {
                        double multiplier = n != 0 ? n : Math.Min(32, Math.Max(4, Up(h * 4, 544))) / 4.0;
                        yield return Yaml("Vita3K", yml, null, "resolution-multiplier", Number(multiplier));
                    }
                    yield return Yaml("Vita3K", yml, null, "stretch_the_display_area", Bool(g.Stretch));
                    yield return Yaml("Vita3K", yml, null, "v-sync", Bool(g.VSync));
                }
            }
            else if (id.Contains("flycast"))
            {
                string cfg = new[] { Path.Combine(installDir, "emu.cfg"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "flycast", "emu.cfg") }.FirstOrDefault(File.Exists);
                if (cfg != null)
                {
                    if (n != 0 || knownHeight)
                    {
                        // Its own choices (settings_video.cpp): 480 times these.
                        double[] steps = { 1, 1.5, 2, 2.5, 3, 4, 4.5, 5, 6, 7, 8, 9 };
                        double step = n != 0 ? n : steps.FirstOrDefault(s => s * 480 >= h);
                        if (step == 0)
                            step = steps[steps.Length - 1];
                        yield return Ini("Flycast", cfg, "config", "rend.Resolution", Number(step * 480));
                    }
                    string yesNo = g.Widescreen ? "yes" : "no";
                    yield return Ini("Flycast", cfg, "config", "rend.WideScreen", yesNo);
                    yield return Ini("Flycast", cfg, "config", "rend.WidescreenGameHacks", yesNo);
                    yield return Ini("Flycast", cfg, "config", "rend.ScreenStretching", g.Stretch && !g.Widescreen ? "133" : "100");
                    yield return Ini("Flycast", cfg, "config", "rend.vsync", g.VSync ? "yes" : "no");
                }
            }
            else if (id.Contains("xemu"))
            {
                string toml = Path.Combine(installDir, "xemu.toml");
                if (File.Exists(toml))
                {
                    if (n != 0 || knownHeight)
                        yield return Toml("xemu", toml, "display.quality", "surface_scale", Number(n != 0 ? n : Clamp(Up(h, 480), 1, 6)), add: true);
                    yield return Toml("xemu", toml, "display.ui", "fit", g.Stretch ? "'stretch'" : "'scale'", add: true);
                    yield return Toml("xemu", toml, "display.window", "vsync", Bool(g.VSync), add: true);
                }
            }
            else if (id.Contains("ppsspp"))
            {
                string ini = Directory.Exists(Path.Combine(installDir, "memstick"))
                    ? Path.Combine(installDir, "memstick", "PSP", "SYSTEM", "ppsspp.ini")
                    : Path.Combine(documents, "PPSSPP", "PSP", "SYSTEM", "ppsspp.ini");
                if (File.Exists(ini))
                {
                    yield return Ini("PPSSPP", ini, "Graphics", "InternalResolution", Number(n));
                    yield return Ini("PPSSPP", ini, "Graphics", "VerticalSync", TitleBool(g.VSync));
                }
            }
        }

        private static int Up(int value, int by) => (value + by - 1) / by;

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        // ---- Writing --------------------------------------------------------------------------

        private static void Write(Setting s)
        {
            try
            {
                string current = s.Read();
                if (current != null && Same(current, s.Wanted))
                    return;
                if (s.Write(s.Wanted))
                    s_log.Info($"[Graphics] {s.Emulator}: {s.Name} = {s.Wanted} ({s.File}).");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Graphics] {s.Emulator}: could not set {s.Name} in {s.File}: {ex.Message}");
            }
        }

        private static bool Same(string a, string b)
        {
            a = a.Trim().Trim('"', '\'');
            b = b.Trim().Trim('"', '\'');
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                return Math.Abs(x - y) < 0.001;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // ---- File kinds ---------------------------------------------------------------------------

        private static Setting Ini(string emulator, string path, string section, string key, string wanted, bool create = false) => new Setting
        {
            Emulator = emulator, File = path, Name = key, Wanted = wanted,
            Read = () => IniValue(path, section, key),
            Write = value => SettingsFile.SetIni(path, section, new Dictionary<string, string> { [key] = value }, create: create),
        };

        // Qt's settings: a value counts only with its "\default" flag false beside it.
        private static Setting QtIni(string emulator, string path, string section, string key, string def, string wanted) => new Setting
        {
            Emulator = emulator, File = path, Name = key, Wanted = wanted,
            Read = () => string.Equals(IniValue(path, section, key + "\\default"), "false", StringComparison.OrdinalIgnoreCase)
                ? IniValue(path, section, key)
                : null,
            Write = value => SettingsFile.SetIni(path, section, new Dictionary<string, string>
            {
                [key + "\\default"] = value == def ? "true" : "false",
                [key] = value,
            }, create: true, separator: "="),
        };

        private static string IniValue(string path, string section, string key)
        {
            if (!File.Exists(path))
                return null;
            bool inside = false;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inside = string.Equals(line, "[" + section + "]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (inside && eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim().Trim('"');
            }
            return null;
        }

        // YAML: a top-level "key: value", or one indented under a top-level "parent:".
        private static Setting Yaml(string emulator, string path, string parent, string key, string wanted) => new Setting
        {
            Emulator = emulator, File = path, Name = key, Wanted = wanted,
            Read = () =>
            {
                List<string> lines = File.ReadAllLines(path).ToList();
                int at = YamlLine(lines, parent, key);
                return at < 0 ? null : lines[at].Substring(lines[at].IndexOf(':') + 1).Trim().Trim('"', '\'');
            },
            Write = value =>
            {
                List<string> lines = File.ReadAllLines(path).ToList();
                int at = YamlLine(lines, parent, key);
                if (at >= 0)
                {
                    string indent = lines[at].Substring(0, lines[at].Length - lines[at].TrimStart().Length);
                    string wantedLine = indent + key + ": " + value;
                    if (lines[at] == wantedLine)
                        return false;
                    lines[at] = wantedLine;
                }
                else if (parent == null)
                {
                    int end = lines.FindIndex(l => l.Trim() == "...");
                    lines.Insert(end >= 0 ? end : lines.Count, key + ": " + value);
                }
                else
                {
                    int head = lines.FindIndex(l => l.TrimEnd() == parent + ":");
                    if (head < 0)
                        return false;   // the emulator writes its whole file: not one of its kind
                    lines.Insert(head + 1, "  " + key + ": " + value);
                }
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
                return true;
            },
        };

        private static int YamlLine(List<string> lines, string parent, string key)
        {
            int from = 0, to = lines.Count;
            if (parent != null)
            {
                int head = lines.FindIndex(l => l.TrimEnd() == parent + ":");
                if (head < 0)
                    return -1;
                from = head + 1;
                int next = lines.FindIndex(from, l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith("#", StringComparison.Ordinal));
                to = next < 0 ? lines.Count : next;
            }
            for (int i = from; i < to; i++)
            {
                string line = lines[i];
                bool top = line.Length > 0 && !char.IsWhiteSpace(line[0]);
                if ((parent == null) != top)
                    continue;
                if (line.TrimStart().StartsWith(key + ":", StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        // TOML: "key = value" in a [section], a comment after it kept.
        private static Setting Toml(string emulator, string path, string section, string key, string wanted, bool add) => new Setting
        {
            Emulator = emulator, File = path, Name = key, Wanted = wanted,
            Read = () =>
            {
                List<string> lines = File.ReadAllLines(path).ToList();
                int at = TomlLine(lines, section, key, out _);
                if (at < 0)
                    return null;
                string value = lines[at].Substring(lines[at].IndexOf('=') + 1);
                int hash = value.IndexOf('#');
                return (hash >= 0 ? value.Substring(0, hash) : value).Trim();
            },
            Write = value =>
            {
                List<string> lines = File.ReadAllLines(path).ToList();
                int at = TomlLine(lines, section, key, out int head);
                if (at >= 0)
                {
                    string line = lines[at];
                    int eq = line.IndexOf('=');
                    int hash = line.IndexOf('#', eq);
                    string after = hash >= 0 ? line.Substring(hash) : "";
                    string current = (hash >= 0 ? line.Substring(eq + 1, hash - eq - 1) : line.Substring(eq + 1)).Trim();
                    if (current == value)
                        return false;
                    string start = line.Substring(0, eq + 1) + " " + value;
                    // The comment where it was, when it still fits.
                    lines[at] = after.Length == 0 ? start : start.PadRight(Math.Max(start.Length + 1, hash)) + after;
                }
                else if (!add)
                {
                    return false;
                }
                else if (head >= 0)
                {
                    lines.Insert(head + 1, key + " = " + value);
                }
                else
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                        lines.Add("");
                    lines.Add("[" + section + "]");
                    lines.Add(key + " = " + value);
                }
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
                return true;
            },
        };

        private static int TomlLine(List<string> lines, string section, string key, out int head)
        {
            head = lines.FindIndex(l => l.Trim() == "[" + section + "]");
            if (head < 0)
                return -1;
            for (int i = head + 1; i < lines.Count; i++)
            {
                string line = lines[i].TrimStart();
                if (line.StartsWith("[", StringComparison.Ordinal))
                    break;
                int eq = line.IndexOf('=');
                if (eq > 0 && line.Substring(0, eq).Trim() == key)
                    return i;
            }
            return -1;
        }

        // ---- The display --------------------------------------------------------------------------

        /// <summary>
        /// The height in real pixels of the monitor Playnite is on (the window in front as a game
        /// starts), or the main monitor; the shorter side for a monitor turned upright. 0 if not
        /// known.
        /// </summary>
        internal static int NativeHeight()
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTOPRIMARY);
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                    return 0;
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                if (EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref mode) && mode.dmPelsHeight > 0)
                    return Math.Min(mode.dmPelsWidth, mode.dmPelsHeight);
                // Without the mode: the monitor's area, which Windows may have scaled for DPI.
                int w = info.rcMonitor.Right - info.rcMonitor.Left, hgt = info.rcMonitor.Bottom - info.rcMonitor.Top;
                return Math.Max(0, Math.Min(w, hgt));
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Upscale] Could not read the display's resolution: {ex.Message}");
                return 0;
            }
        }

        private const int MONITOR_DEFAULTTOPRIMARY = 1;
        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE mode);
    }
}
