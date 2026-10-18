using Playnite.SDK;
using RomM.Account;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Setup
{
    /// <summary>
    /// PCSX2's first-run wizard, done for it: PCSX2 writes its default settings with no window
    /// (pcsx2-qt -testconfig: it loads, or makes and saves, its settings and exits), and those
    /// are then completed as the wizard would:
    ///
    ///   [UI] SetupWizardIncomplete = false     the wizard is not shown
    ///   [Filenames] BIOS = &lt;file&gt;             a U.S. BIOS from the bios folder (by its ROMVER)
    ///   [InputSources] SDL = true, XInput = false
    ///   [Pad1] Type = DualShock2, every button and axis on SDL-0, as PCSX2's own automatic
    ///                                          mapping for an SDL game controller binds them
    ///
    /// SDL, not XInput: SDL reads any controller it knows the layout of (Xbox, PlayStation,
    /// Switch Pro, most others) as an Xbox-style pad, XInput only Xbox ones. XInput is turned off
    /// so an Xbox pad is not seen twice. Builds on SDL3 (SDL3.dll beside the program) name the
    /// face buttons by place (FaceSouth...), older ones by Xbox label (A...).
    ///
    /// No game folders are added. A PCSX2 whose wizard was already finished is left as it is,
    /// except that the XInput bindings an earlier version of this wrote are moved to SDL.
    /// Keys from PCSX2's source (QtHost.cpp, BiosTools.cpp, SDLInputSource.cpp, PadDualshock2.cpp).
    /// </summary>
    internal static class Pcsx2FirstRun
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        // DualShock 2 input -> SDL-0 element, as SDLInputSource::GetGenericBindingMapping has them
        // (s_sdl_generic_binding_button_mapping / _axis_mapping); face buttons by SDL3's names or
        // by SDL2's (which SDL3 builds still read, moving them to the new names).
        private static Dictionary<string, string> PadBindings(bool sdl3)
        {
            string south = sdl3 ? "FaceSouth" : "A", east = sdl3 ? "FaceEast" : "B";
            string west = sdl3 ? "FaceWest" : "X", north = sdl3 ? "FaceNorth" : "Y";
            return new Dictionary<string, string>
            {
                ["Up"] = "SDL-0/DPadUp",
                ["Right"] = "SDL-0/DPadRight",
                ["Down"] = "SDL-0/DPadDown",
                ["Left"] = "SDL-0/DPadLeft",
                ["Triangle"] = "SDL-0/" + north,
                ["Circle"] = "SDL-0/" + east,
                ["Cross"] = "SDL-0/" + south,
                ["Square"] = "SDL-0/" + west,
                ["Select"] = "SDL-0/Back",
                ["Start"] = "SDL-0/Start",
                ["L1"] = "SDL-0/LeftShoulder",
                ["L2"] = "SDL-0/+LeftTrigger",
                ["R1"] = "SDL-0/RightShoulder",
                ["R2"] = "SDL-0/+RightTrigger",
                ["L3"] = "SDL-0/LeftStick",
                ["R3"] = "SDL-0/RightStick",
                ["LUp"] = "SDL-0/-LeftY",
                ["LRight"] = "SDL-0/+LeftX",
                ["LDown"] = "SDL-0/+LeftY",
                ["LLeft"] = "SDL-0/-LeftX",
                ["RUp"] = "SDL-0/-RightY",
                ["RRight"] = "SDL-0/+RightX",
                ["RDown"] = "SDL-0/+RightY",
                ["RLeft"] = "SDL-0/-RightX",
                ["LargeMotor"] = "SDL-0/LargeMotor",
                ["SmallMotor"] = "SDL-0/SmallMotor",
            };
        }

        // What an earlier version of this wrote (XInput-0), recognised to be moved to SDL.
        private static readonly Dictionary<string, string> OldXInputBindings = new Dictionary<string, string>
        {
            ["Up"] = "XInput-0/DPadUp",
            ["Right"] = "XInput-0/DPadRight",
            ["Down"] = "XInput-0/DPadDown",
            ["Left"] = "XInput-0/DPadLeft",
            ["Triangle"] = "XInput-0/Y",
            ["Circle"] = "XInput-0/B",
            ["Cross"] = "XInput-0/A",
            ["Square"] = "XInput-0/X",
            ["Select"] = "XInput-0/Back",
            ["Start"] = "XInput-0/Start",
            ["L1"] = "XInput-0/LeftShoulder",
            ["L2"] = "XInput-0/+LeftTrigger",
            ["R1"] = "XInput-0/RightShoulder",
            ["R2"] = "XInput-0/+RightTrigger",
            ["L3"] = "XInput-0/LeftStick",
            ["R3"] = "XInput-0/RightStick",
            ["LUp"] = "XInput-0/-LeftY",
            ["LRight"] = "XInput-0/+LeftX",
            ["LDown"] = "XInput-0/+LeftY",
            ["LLeft"] = "XInput-0/-LeftX",
            ["RUp"] = "XInput-0/-RightY",
            ["RRight"] = "XInput-0/+RightX",
            ["RDown"] = "XInput-0/+RightY",
            ["RLeft"] = "XInput-0/-RightX",
            ["LargeMotor"] = "XInput-0/LargeMotor",
            ["SmallMotor"] = "XInput-0/SmallMotor",
        };

        /// <summary>Settles a PCSX2 whose first-run wizard has not been through. Before it starts.</summary>
        public static void Prepare(string installDir)
        {
            try
            {
                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                    return;
                bool portable = File.Exists(Path.Combine(installDir, "portable.txt")) || File.Exists(Path.Combine(installDir, "portable.ini"));
                string dataRoot = portable ? installDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2");
                string ini = Path.Combine(dataRoot, "inis", "PCSX2.ini");

                if (!File.Exists(ini))
                    WriteDefaults(installDir, ini);
                if (!File.Exists(ini))
                    return;
                bool sdl3 = File.Exists(Path.Combine(installDir, "SDL3.dll"));
                // The Guide button opens the in-game overlay: no longer PCSX2's Analog button, as
                // an earlier version of this bound it.
                string analog = Value(ini, "Pad1", "Analog");
                if (analog == "SDL-0/Guide" || analog == "XInput-0/Guide")
                    SettingsFile.SetIni(ini, "Pad1", new Dictionary<string, string> { ["Analog"] = "" });
                // Finished by the user (or by this before): theirs, but for the XInput bindings an
                // earlier version of this wrote, moved to SDL (when still exactly those).
                if (!string.Equals(Value(ini, "UI", "SetupWizardIncomplete"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    if (OldXInputBindings.All(b => string.Equals(Value(ini, "Pad1", b.Key), b.Value, StringComparison.Ordinal)))
                    {
                        SettingsFile.SetIni(ini, "InputSources", new Dictionary<string, string> { ["SDL"] = "true", ["XInput"] = "false" });
                        SettingsFile.SetIni(ini, "Pad1", PadBindings(sdl3));
                        s_log.Info($"[EmulatorSetup] PCSX2: controller 1 moved from XInput to SDL ({ini}).");
                    }
                    return;
                }

                string bios = Value(ini, "Filenames", "BIOS");
                string biosDir = Path.Combine(dataRoot, "bios");
                if (string.IsNullOrEmpty(bios) || !File.Exists(Path.Combine(biosDir, bios)))
                    bios = ChooseBios(biosDir);

                SettingsFile.SetIni(ini, "InputSources", new Dictionary<string, string> { ["SDL"] = "true", ["XInput"] = "false" });
                var pad = new Dictionary<string, string>(PadBindings(sdl3)) { ["Type"] = "DualShock2" };
                SettingsFile.SetIni(ini, "Pad1", pad);
                if (bios != null)
                    SettingsFile.SetIni(ini, "Filenames", new Dictionary<string, string> { ["BIOS"] = bios }, create: true);
                SettingsFile.SetIni(ini, "UI", new Dictionary<string, string> { ["SetupWizardIncomplete"] = "false" });
                s_log.Info($"[EmulatorSetup] PCSX2 set up without its wizard: BIOS {bios ?? "(none found)"}, controller 1 on SDL ({ini}).");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] Could not set PCSX2 up: {ex.Message}");
            }
        }

        // pcsx2-qt -testconfig: settings made and saved, then it exits; no window.
        private static void WriteDefaults(string installDir, string ini)
        {
            string exe = Directory.GetFiles(installDir, "pcsx2*.exe")
                .OrderBy(f => Path.GetFileName(f).IndexOf("qt", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .FirstOrDefault();
            if (exe == null)
                return;
            using (Process process = Process.Start(new ProcessStartInfo(exe, "-testconfig")
            {
                WorkingDirectory = installDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                if (process != null && !process.WaitForExit(30000))
                {
                    try { process.Kill(); } catch { }
                }
            }
            s_log.Info(File.Exists(ini) ? $"[EmulatorSetup] PCSX2 wrote its default settings ({ini})." : "[EmulatorSetup] PCSX2 did not write its settings.");
        }

        /// <summary>The BIOS to use: a U.S. one when there is one, else any PCSX2 would take; null when none.</summary>
        internal static string ChooseBios(string biosDir)
        {
            if (!Directory.Exists(biosDir))
                return null;
            var found = new List<Tuple<string, char>>();
            foreach (string file in Directory.GetFiles(biosDir))
            {
                char region = Region(file);
                if (region != '\0')
                    found.Add(Tuple.Create(Path.GetFileName(file), region));
            }
            return (found.FirstOrDefault(f => f.Item2 == 'A') ?? found.FirstOrDefault())?.Item1;
        }

        /// <summary>
        /// A PS2 BIOS's region letter (ROMVER's fifth character: J Japan, A USA, E Europe, H Asia,
        /// C China...), or '\0' when the file is not one. Read as PCSX2 reads it: the ROM directory
        /// starts with a 16-byte "RESET" entry on a 16-byte boundary; each entry is a 10-byte name, a
        /// 2-byte extinfo size and a 4-byte file size, files laid end to end, each padded to 16.
        /// </summary>
        internal static char Region(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length < 1024 * 1024 || info.Length > 64 * 1024 * 1024)
                    return '\0';
                byte[] data = File.ReadAllBytes(path);
                int dir = -1;
                for (int i = 0; i + 16 <= Math.Min(data.Length, 512 * 1024 * 16); i += 16)
                {
                    if (Name(data, i) == "RESET")
                    {
                        dir = i;
                        break;
                    }
                }
                if (dir < 0)
                    return '\0';

                long offset = 0;
                for (int at = dir; at + 16 <= data.Length; at += 16)
                {
                    string name = Name(data, at);
                    if (name.Length == 0)
                        break;
                    uint size = BitConverter.ToUInt32(data, at + 12);
                    if (name == "ROMVER")
                        return offset + 5 <= data.Length ? (char)data[offset + 4] : '\0';
                    offset += size % 0x10 == 0 ? size : (size + 0x10) & 0xFFFFFFF0;
                }
            }
            catch
            {
                // Not readable: not one.
            }
            return '\0';
        }

        private static string Name(byte[] data, int at)
        {
            int length = 0;
            while (length < 10 && data[at + length] != 0)
                length++;
            return length == 10 ? "" : Encoding.ASCII.GetString(data, at, length);
        }

        /// <summary>
        /// Controller 2 on the second gamepad SDL knows (SDL-1), when two are connected and
        /// [Pad2] is still "None" with nothing bound, as PCSX2 starts (Pad::GetDefaultPadType).
        /// </summary>
        internal static void SecondPlayer(string installDir, int gamepads)
        {
            if (gamepads < 2 || string.IsNullOrEmpty(installDir))
                return;
            try
            {
                bool portable = File.Exists(Path.Combine(installDir, "portable.txt")) || File.Exists(Path.Combine(installDir, "portable.ini"));
                string dataRoot = portable ? installDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2");
                string ini = Path.Combine(dataRoot, "inis", "PCSX2.ini");
                if (!File.Exists(ini))
                    return;
                string type = Value(ini, "Pad2", "Type");
                if (type != null && !string.Equals(type, "None", StringComparison.OrdinalIgnoreCase))
                    return;
                bool sdl3 = File.Exists(Path.Combine(installDir, "SDL3.dll"));
                Dictionary<string, string> bindings = PadBindings(sdl3);
                if (bindings.Keys.Any(key => !string.IsNullOrEmpty(Value(ini, "Pad2", key))))
                    return;   // something bound there: the user's
                var values = new Dictionary<string, string> { ["Type"] = "DualShock2" };
                foreach (KeyValuePair<string, string> binding in bindings)
                    values[binding.Key] = binding.Value.Replace("SDL-0/", "SDL-1/");
                if (SettingsFile.SetIni(ini, "Pad2", values, create: false))
                    s_log.Info($"[EmulatorSetup] PCSX2: controller 2 on the second gamepad ({ini}).");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] PCSX2: could not set controller 2: {ex.Message}");
            }
        }

        // A value from an ini file ("key = value" under [section]), or null.
        private static string Value(string path, string section, string key)
        {
            string current = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    current = line.Substring(1, line.Length - 2);
                else if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
                {
                    int equals = line.IndexOf('=');
                    if (equals > 0 && string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(equals + 1).Trim();
                }
            }
            return null;
        }
    }
}
