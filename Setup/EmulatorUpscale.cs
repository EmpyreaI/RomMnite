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
    /// <summary>
    /// Emulators render at the display's own resolution instead of the console's, set just
    /// before a game starts (EmulatorLaunch.PrepareSettings). "The display" is the monitor
    /// Playnite is on, in its real pixels (its current display mode, not scaled by Windows'
    /// DPI setting). Where an emulator can scale to its window by itself, that is used, so it
    /// follows the display on its own; elsewhere the smallest scale that reaches the display's
    /// height is worked out (H is that height):
    ///
    ///   DuckStation  settings.ini [GPU] ResolutionScale = 0          automatic, by window size (settings.cpp)
    ///   Dolphin      GFX.ini [Settings] InternalResolution = 0       EFB_SCALE_AUTO_INTEGRAL
    ///   Azahar       qt-config.ini [Renderer] resolution_factor = 0  the window's scaling ratio (renderer_base.cpp)
    ///   PCSX2        PCSX2.ini [EmuCore/GS] upscale_multiplier       ceil(H / 480), up to 8
    ///   RPCS3        config.yml Video: Resolution Scale              percent of 720p: ceil(100 H / 720), 100-800
    ///   Xenia        xenia-canary.config.toml [GPU] draw_resolution_scale_x/_y   ceil(H / 720), up to 3
    ///                (Xenia lowers it itself to what the graphics card supports)
    ///   Vita3K       config.yml resolution-multiplier                H / 544, up in quarter steps, up to 8
    ///   Flycast      emu.cfg [config] rend.Resolution                its own steps (half multiples of 480) from H up
    ///   xemu         xemu.toml [display.quality] surface_scale       ceil(H / 480), up to 6
    ///
    /// PPSSPP already defaults to automatic (InternalResolution = 0 on Windows). RetroArch
    /// (each core has its own option), Cemu (graphics packs per game) and Eden are left as
    /// they are.
    ///
    /// Only a setting still at the emulator's default, or at what was set here before, is
    /// changed: a scale the user picked in the emulator stays. What was set is noted
    /// (EmulatorUpscale.txt in the plugin's data), so a later display change is followed, and
    /// with the setting off, those values go back to the emulator's default.
    /// </summary>
    internal static class EmulatorUpscale
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        /// <param name="id">The emulator's built-in id and name, lower case.</param>
        /// <param name="record">Where what was set is noted; null to change only defaults.</param>
        /// <param name="on">Off: what was set here goes back to the emulator's default.</param>
        public static void Apply(string id, string installDir, string record, bool on)
        {
            int height = on ? NativeHeight() : 0;
            if (on && height <= 0)
                return;
            var notes = Load(record);
            bool changed = false;
            foreach (Setting setting in For(id, installDir, height))
                changed |= Decide(setting, notes, on);
            if (changed)
                Save(record, notes);
        }

        // ---- Each emulator's settings ------------------------------------------------------------

        private sealed class Setting
        {
            public string Emulator;
            public string File;
            public string Name;
            public string Default;
            public string Wanted;
            public Func<string> Read;          // null when not set (the default)
            public Func<string, bool> Write;   // whether the file changed
        }

        private static IEnumerable<Setting> For(string id, string installDir, int h)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (id.Contains("duckstation"))
            {
                string ini = Path.Combine(DuckStationFirstRun.DataRoot(installDir), "settings.ini");
                if (File.Exists(ini))
                    yield return Ini("DuckStation", ini, "GPU", "ResolutionScale", "1", "0");
            }
            else if (id.Contains("dolphin"))
            {
                bool portable = File.Exists(Path.Combine(installDir, "portable.txt")) || Directory.Exists(Path.Combine(installDir, "User"));
                string user = portable ? Path.Combine(installDir, "User") : Path.Combine(documents, "Dolphin Emulator");
                if (portable || Directory.Exists(user))
                    yield return Ini("Dolphin", Path.Combine(user, "Config", "GFX.ini"), "Settings", "InternalResolution", "1", "0", create: true);
            }
            else if (id.Contains("azahar"))
            {
                string user = Path.Combine(installDir, "user");
                if (Directory.Exists(user))
                    yield return QtIni("Azahar", Path.Combine(user, "config", "qt-config.ini"), "Renderer", "resolution_factor", "1", "0");
            }
            else if (id.Contains("pcsx2"))
            {
                string ini = new[] { Path.Combine(installDir, "inis", "PCSX2.ini"), Path.Combine(documents, "PCSX2", "inis", "PCSX2.ini") }.FirstOrDefault(File.Exists);
                if (ini != null)
                    yield return Ini("PCSX2", ini, "EmuCore/GS", "upscale_multiplier", "1", Number(Clamp(Up(h, 480), 1, 8)));
            }
            else if (id.Contains("rpcs3"))
            {
                // Beside it, or in portable\; on Windows in a config\ folder inside that.
                string root = Directory.Exists(Path.Combine(installDir, "portable")) ? Path.Combine(installDir, "portable") : installDir;
                string yml = new[] { Path.Combine(root, "config", "config.yml"), Path.Combine(root, "config.yml") }.FirstOrDefault(File.Exists);
                if (yml != null)
                    yield return Yaml("RPCS3", yml, "Video", "Resolution Scale", "100", Number(Clamp(Up(h * 100, 720), 100, 800)));
            }
            else if (id.Contains("xenia"))
            {
                string toml = Path.Combine(Saves.Handlers.EmulatorActiveUser.XeniaRoot(installDir), "xenia-canary.config.toml");
                if (File.Exists(toml))
                {
                    string scale = Number(Clamp(Up(h, 720), 1, 3));
                    yield return Toml("Xenia", toml, "GPU", "draw_resolution_scale_x", "1", scale, add: false);
                    yield return Toml("Xenia", toml, "GPU", "draw_resolution_scale_y", "1", scale, add: false);
                }
            }
            else if (id.Contains("vita3k"))
            {
                string yml = Path.Combine(installDir, "portable", "config.yml");
                if (File.Exists(yml))
                {
                    double quarters = Math.Min(32, Math.Max(4, Up(h * 4, 544)));
                    yield return Yaml("Vita3K", yml, null, "resolution-multiplier", "1", Number(quarters / 4));
                }
            }
            else if (id.Contains("flycast"))
            {
                string cfg = new[] { Path.Combine(installDir, "emu.cfg"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "flycast", "emu.cfg") }.FirstOrDefault(File.Exists);
                if (cfg != null)
                {
                    // Its own choices (settings_video.cpp): 480 times these.
                    double[] steps = { 1, 1.5, 2, 2.5, 3, 4, 4.5, 5, 6, 7, 8, 9 };
                    double step = steps.FirstOrDefault(s => s * 480 >= h);
                    if (step == 0)
                        step = steps[steps.Length - 1];
                    yield return Ini("Flycast", cfg, "config", "rend.Resolution", "480", Number(step * 480));
                }
            }
            else if (id.Contains("xemu"))
            {
                string toml = Path.Combine(installDir, "xemu.toml");
                if (File.Exists(toml))
                    yield return Toml("xemu", toml, "display.quality", "surface_scale", "1", Number(Clamp(Up(h, 480), 1, 6)), add: true);
            }
        }

        private static int Up(int value, int by) => (value + by - 1) / by;

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        // ---- Whether to change it ---------------------------------------------------------------

        private static bool Decide(Setting s, Dictionary<string, string> notes, bool on)
        {
            string note = s.File + "|" + s.Name;
            notes.TryGetValue(note, out string set);
            string current;
            try
            {
                current = s.Read();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Upscale] {s.Emulator}: could not read {s.File}: {ex.Message}");
                return false;
            }
            bool ours = current == null || Same(current, s.Default) || (set != null && Same(current, set));
            if (!ours)
            {
                // The user's own choice: kept, and not ours to follow any more.
                return notes.Remove(note);
            }
            string wanted = on ? s.Wanted : s.Default;
            if (!on && set == null)
                return false;   // never set here: nothing to put back
            try
            {
                if (current == null ? !Same(wanted, s.Default) : !Same(current, wanted))
                {
                    if (s.Write(wanted))
                        s_log.Info(on
                            ? $"[Upscale] {s.Emulator}: {s.Name} = {wanted} for the display ({s.File})."
                            : $"[Upscale] {s.Emulator}: {s.Name} back to its default, {wanted} ({s.File}).");
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Upscale] {s.Emulator}: could not set {s.Name} in {s.File}: {ex.Message}");
                return false;
            }
            if (on)
            {
                if (set == wanted)
                    return false;
                notes[note] = wanted;
                return true;
            }
            notes.Remove(note);
            return true;
        }

        private static bool Same(string a, string b)
        {
            if (a == null || b == null)
                return a == b;
            if (double.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                return Math.Abs(x - y) < 0.001;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> Load(string record)
        {
            var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (record != null && File.Exists(record))
                {
                    foreach (string line in File.ReadAllLines(record))
                    {
                        int tab = line.LastIndexOf('\t');
                        if (tab > 0)
                            notes[line.Substring(0, tab)] = line.Substring(tab + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Upscale] {ex.Message}");
            }
            return notes;
        }

        private static void Save(string record, Dictionary<string, string> notes)
        {
            if (record == null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(record));
                File.WriteAllLines(record, notes.Select(n => n.Key + "\t" + n.Value), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Upscale] {ex.Message}");
            }
        }

        // ---- File kinds ---------------------------------------------------------------------------

        private static Setting Ini(string emulator, string path, string section, string key, string def, string wanted, bool create = false) => new Setting
        {
            Emulator = emulator, File = path, Name = key, Default = def, Wanted = wanted,
            Read = () => IniValue(path, section, key),
            Write = value => SettingsFile.SetIni(path, section, new Dictionary<string, string> { [key] = value }, create: create),
        };

        // Qt's settings: a value counts only with its "\default" flag false beside it.
        private static Setting QtIni(string emulator, string path, string section, string key, string def, string wanted) => new Setting
        {
            Emulator = emulator, File = path, Name = key, Default = def, Wanted = wanted,
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
        private static Setting Yaml(string emulator, string path, string parent, string key, string def, string wanted) => new Setting
        {
            Emulator = emulator, File = path, Name = key, Default = def, Wanted = wanted,
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
        private static Setting Toml(string emulator, string path, string section, string key, string def, string wanted, bool add) => new Setting
        {
            Emulator = emulator, File = path, Name = key, Default = def, Wanted = wanted,
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
