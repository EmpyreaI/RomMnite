using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Setup
{
    /// <summary>
    /// DuckStation's first-run wizard, done for it. DuckStation shows the wizard while
    /// settings.ini has [Main] SetupWizardIncomplete = true, or has neither that key nor
    /// SettingsVersion (QtHost::InitializeConfig). With no settings.ini at all it writes its
    /// defaults (Core::InitializeBaseSettingsLayer) and so flags the wizard; this writes the file
    /// first instead, with those same defaults where they are only written there (input
    /// sources, hotkeys, controller 1), and:
    ///
    ///   [Main] SetupWizardIncomplete = false
    ///   [Pad1] Type = AnalogController; every input on the first controller (SDL-0, as
    ///          DuckStation's own automatic mapping binds a gamepad) and, as its defaults have
    ///          them, on the keyboard too
    ///
    /// The BIOS is left to DuckStation: with no image named it picks one for the game's region
    /// from its bios folder, where the setup puts them. No game folders are added. A
    /// DuckStation whose wizard was already finished is left as it is. Keys and names from
    /// DuckStation's source (qthost.cpp, core.cpp, settings.cpp, hotkeys.cpp, input_manager.cpp,
    /// sdl_input_source.cpp, analog_controller.cpp).
    /// </summary>
    internal static class DuckStationFirstRun
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        // AnalogController input -> bindings (SDL's generic mapping first, then the keyboard's).
        private static readonly string[][] PadBindings =
        {
            new[] { "Up", "SDL-0/DPadUp", "Keyboard/UpArrow" },
            new[] { "Right", "SDL-0/DPadRight", "Keyboard/RightArrow" },
            new[] { "Down", "SDL-0/DPadDown", "Keyboard/DownArrow" },
            new[] { "Left", "SDL-0/DPadLeft", "Keyboard/LeftArrow" },
            new[] { "Triangle", "SDL-0/Y", "Keyboard/I" },
            new[] { "Circle", "SDL-0/B", "Keyboard/L" },
            new[] { "Cross", "SDL-0/A", "Keyboard/K" },
            new[] { "Square", "SDL-0/X", "Keyboard/J" },
            new[] { "Select", "SDL-0/Back", "Keyboard/Backspace" },
            new[] { "Start", "SDL-0/Start", "Keyboard/Enter" },
            new[] { "L1", "SDL-0/LeftShoulder", "Keyboard/Q" },
            new[] { "R1", "SDL-0/RightShoulder", "Keyboard/E" },
            new[] { "L2", "SDL-0/+LeftTrigger", "Keyboard/1" },
            new[] { "R2", "SDL-0/+RightTrigger", "Keyboard/3" },
            new[] { "L3", "SDL-0/LeftStick", "Keyboard/2" },
            new[] { "R3", "SDL-0/RightStick", "Keyboard/4" },
            new[] { "LLeft", "SDL-0/-LeftX", "Keyboard/A" },
            new[] { "LRight", "SDL-0/+LeftX", "Keyboard/D" },
            new[] { "LDown", "SDL-0/+LeftY", "Keyboard/S" },
            new[] { "LUp", "SDL-0/-LeftY", "Keyboard/W" },
            new[] { "RLeft", "SDL-0/-RightX", "Keyboard/F" },
            new[] { "RRight", "SDL-0/+RightX", "Keyboard/H" },
            new[] { "RDown", "SDL-0/+RightY", "Keyboard/G" },
            new[] { "RUp", "SDL-0/-RightY", "Keyboard/T" },
            new[] { "LargeMotor", "SDL-0/LargeMotor" },
            new[] { "SmallMotor", "SDL-0/SmallMotor" },
        };

        // Settings::SetDefaultHotkeyConfig.
        private static readonly string[][] Hotkeys =
        {
            new[] { "FastForward", "Keyboard/Tab" },
            new[] { "TogglePause", "Keyboard/Space" },
            new[] { "Screenshot", "Keyboard/F10" },
            new[] { "ToggleFullscreen", "Keyboard/F11" },
            new[] { "OpenPauseMenu", "Keyboard/Escape" },
            new[] { "LoadSelectedSaveState", "Keyboard/F1" },
            new[] { "SaveSelectedSaveState", "Keyboard/F2" },
            new[] { "SelectPreviousSaveStateSlot", "Keyboard/F3" },
            new[] { "SelectNextSaveStateSlot", "Keyboard/F4" },
        };

        /// <summary>
        /// Where DuckStation keeps its settings (Core::SetCriticalFolders): beside it when
        /// portable.txt or settings.ini is there, else Documents\DuckStation when that folder
        /// exists (older installs), else %LOCALAPPDATA%\DuckStation.
        /// </summary>
        internal static string DataRoot(string installDir)
        {
            if (File.Exists(Path.Combine(installDir, "portable.txt")) || File.Exists(Path.Combine(installDir, "settings.ini")))
                return installDir;
            string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DuckStation");
            if (Directory.Exists(documents))
                return documents;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuckStation");
        }

        /// <summary>Settles a DuckStation whose first-run wizard has not been through. Before it starts.</summary>
        public static void Prepare(string installDir)
        {
            try
            {
                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                    return;
                string ini = Path.Combine(DataRoot(installDir), "settings.ini");

                if (!File.Exists(ini))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ini));
                    File.WriteAllText(ini, NewSettings(), new UTF8Encoding(false));
                    s_log.Info($"[EmulatorSetup] DuckStation set up without its wizard: controller 1 on the first gamepad and the keyboard ({ini}).");
                    return;
                }

                List<string> lines = File.ReadAllLines(ini).ToList();
                // The Guide button opens the in-game overlay: no longer DuckStation's Analog
                // button, as an earlier version of this bound it.
                if (lines.RemoveAll(l => l.Trim() == "Analog = SDL-0/Guide") > 0)
                {
                    File.WriteAllLines(ini, lines, new UTF8Encoding(false));
                    s_log.Info($"[EmulatorSetup] DuckStation: the Guide button left for the overlay ({ini}).");
                }
                string flag = Value(lines, "Main", "SetupWizardIncomplete");
                bool incomplete = string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
                                  || (flag == null && Value(lines, "Main", "SettingsVersion") == null);
                if (!incomplete)
                    return;

                // Its defaults are there (it ran once, the wizard left): controller 1 given the
                // gamepad too, unless something already binds it to one.
                bool gamepad = SectionLines(lines, "Pad1").Any(l => l.Contains("SDL-") || l.Contains("XInput-"));
                if (!gamepad)
                {
                    RemoveSection(lines, "Pad1");
                    AppendSection(lines, "Pad1", PadSection());
                }
                SetValue(lines, "Main", "SetupWizardIncomplete", "false");
                File.WriteAllLines(ini, lines, new UTF8Encoding(false));
                s_log.Info($"[EmulatorSetup] DuckStation's unfinished wizard marked done{(gamepad ? "" : ", controller 1 on the first gamepad and the keyboard")} ({ini}).");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] Could not set DuckStation up: {ex.Message}");
            }
        }

        private static string NewSettings()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Main]");
            sb.AppendLine("SetupWizardIncomplete = false");
            sb.AppendLine();
            sb.AppendLine("[BIOS]");
            sb.AppendLine("SearchDirectory = bios");
            sb.AppendLine();
            // InputManager::SetDefaultSourceConfig.
            sb.AppendLine("[InputSources]");
            sb.AppendLine("SDL = true");
            sb.AppendLine("SDLControllerEnhancedMode = false");
            sb.AppendLine("SDLPS5PlayerLED = false");
            sb.AppendLine("XInput = false");
            sb.AppendLine("RawInput = false");
            sb.AppendLine();
            sb.AppendLine("[Pad1]");
            foreach (string line in PadSection())
                sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("[Pad2]");
            sb.AppendLine("Type = None");
            sb.AppendLine();
            sb.AppendLine("[Hotkeys]");
            foreach (string[] hotkey in Hotkeys)
                sb.AppendLine(hotkey[0] + " = " + hotkey[1]);
            return sb.ToString();
        }

        // A binding list is the key repeated, one value each (INISettingsInterface string lists).
        private static List<string> PadSection()
        {
            var lines = new List<string> { "Type = AnalogController" };
            foreach (string[] binding in PadBindings)
            {
                for (int i = 1; i < binding.Length; i++)
                    lines.Add(binding[0] + " = " + binding[i]);
            }
            return lines;
        }

        private static int SectionStart(List<string> lines, string section) =>
            lines.FindIndex(l => string.Equals(l.Trim(), "[" + section + "]", StringComparison.OrdinalIgnoreCase));

        private static int SectionEnd(List<string> lines, int start)
        {
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));
            return end < 0 ? lines.Count : end;
        }

        private static IEnumerable<string> SectionLines(List<string> lines, string section)
        {
            int start = SectionStart(lines, section);
            if (start < 0)
                return Enumerable.Empty<string>();
            return lines.Skip(start + 1).Take(SectionEnd(lines, start) - start - 1);
        }

        private static void RemoveSection(List<string> lines, string section)
        {
            int start = SectionStart(lines, section);
            if (start < 0)
                return;
            lines.RemoveRange(start, SectionEnd(lines, start) - start);
        }

        private static void AppendSection(List<string> lines, string section, IEnumerable<string> body)
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                lines.Add("");
            lines.Add("[" + section + "]");
            lines.AddRange(body);
            lines.Add("");
        }

        private static string KeyOf(string line)
        {
            string trimmed = line.Trim();
            int equals = trimmed.IndexOf('=');
            return trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#' || equals <= 0 ? null : trimmed.Substring(0, equals).Trim();
        }

        private static string Value(List<string> lines, string section, string key)
        {
            foreach (string line in SectionLines(lines, section))
            {
                if (string.Equals(KeyOf(line), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(line.IndexOf('=') + 1).Trim();
            }
            return null;
        }

        private static void SetValue(List<string> lines, string section, string key, string value)
        {
            int start = SectionStart(lines, section);
            if (start < 0)
            {
                lines.Insert(0, "[" + section + "]");
                lines.Insert(1, key + " = " + value);
                lines.Insert(2, "");
                return;
            }
            int end = SectionEnd(lines, start);
            for (int i = start + 1; i < end; i++)
            {
                if (string.Equals(KeyOf(lines[i]), key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + " = " + value;
                    return;
                }
            }
            lines.Insert(start + 1, key + " = " + value);
        }
    }
}
