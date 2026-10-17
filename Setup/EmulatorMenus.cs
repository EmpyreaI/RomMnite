using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Setup
{
    /// <summary>
    /// Emulators with in-game menus of their own, opened with Start + Select (Back + Start on an
    /// Xbox pad) rather than the Home (Guide) button, which is the overlay's. Written just before
    /// a game starts (EmulatorLaunch.PrepareSettings), only where the emulator has no controller
    /// binding for its menu of the user's own:
    ///
    ///   DuckStation  settings.ini  [Hotkeys] OpenPauseMenu = &lt;Select&gt; &amp; &lt;Start&gt;, added beside its
    ///                keyboard key, on the controller its Pad1 Start and Select are bound to
    ///   PCSX2        inis\PCSX2.ini, the same
    ///   RetroArch    retroarch.cfg input_menu_toggle_gamepad_combo = "4" (Start + Select) where
    ///                none is set; and, while the overlay has the Home button, its controller
    ///                profile's Home binding for the menu (input_menu_toggle_btn, which RetroArch
    ///                takes from the profile when retroarch.cfg has none) set to a button no
    ///                controller has, "99"; put back to "nul" when the overlay no longer has Home
    ///   PPSSPP       controls.ini [ControlMapping] Pause: the Xbox pad's Home ("20-3", PPSSPP's
    ///                own default) swapped for Select + Start ("20-109:20-108")
    ///
    /// Read from their source: DuckStation and PCSX2 input_manager.cpp / InputManager.cpp (a
    /// binding's parts joined by '&amp;' make a chord), RetroArch input_defines.h (INPUT_COMBO_START_SELECT
    /// is 4), input_driver.c (a bind of NO_BTN falls back to the profile's) and xinput_joypad.c (a
    /// button number past the pad's reads as not pressed), PPSSPP KeyMapDefaults.cpp, KeyMap.cpp
    /// (combos joined by ':'), InputState.h (XInput device 20) and KeyCodes.h (HOME 3, START 108,
    /// SELECT 109). Dolphin, Cemu, Azahar, xemu and Flycast have no menu on the controller; RPCS3's,
    /// Xenia's, Vita3K's and Eden's Home button is the console's own, part of the game.
    /// </summary>
    internal static class EmulatorMenus
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private const string RetroArchNoButton = "99";

        /// <param name="id">The emulator's definition id and name, lower case.</param>
        /// <param name="overlayHasHome">Whether the overlay opens on the Home button.</param>
        public static void Apply(string id, string installDir, bool overlayHasHome)
        {
            try
            {
                if (id.Contains("duckstation"))
                    DuckStyle("DuckStation", Path.Combine(DuckStationFirstRun.DataRoot(installDir), "settings.ini"));
                else if (id.Contains("pcsx2"))
                    DuckStyle("PCSX2", FirstExisting(Path.Combine(installDir, "inis", "PCSX2.ini"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2", "inis", "PCSX2.ini")));
                else if (id.Contains("retroarch"))
                    RetroArch(FirstExisting(Path.Combine(installDir, "retroarch.cfg"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch", "retroarch.cfg")), overlayHasHome);
                else if (id.Contains("ppsspp"))
                    Ppsspp(Directory.Exists(Path.Combine(installDir, "memstick"))
                        ? Path.Combine(installDir, "memstick", "PSP", "SYSTEM", "controls.ini")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PPSSPP", "PSP", "SYSTEM", "controls.ini"));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorMenus] Could not set the emulator's menu buttons: {ex.Message}");
            }
        }

        private static string FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

        private static bool IsPad(string binding) =>
            binding.IndexOf("SDL-", StringComparison.OrdinalIgnoreCase) >= 0
            || binding.IndexOf("XInput-", StringComparison.OrdinalIgnoreCase) >= 0
            || binding.IndexOf("DInput-", StringComparison.OrdinalIgnoreCase) >= 0;

        // ---- DuckStation and PCSX2 --------------------------------------------------------------

        private static void DuckStyle(string name, string ini)
        {
            if (ini == null || !File.Exists(ini))
                return;
            List<string> lines = File.ReadAllLines(ini).ToList();
            if (Values(lines, "Hotkeys", "OpenPauseMenu").Any(IsPad))
                return;   // a controller already opens it: the user's

            // Start and Select as controller 1 has them, so the chord is on the same pad.
            string start = Values(lines, "Pad1", "Start").FirstOrDefault(IsPad) ?? "SDL-0/Start";
            string select = Values(lines, "Pad1", "Select").FirstOrDefault(IsPad) ?? "SDL-0/Back";
            string chord = $"OpenPauseMenu = {select} & {start}";

            int section = Header(lines, "Hotkeys");
            if (section < 0)
            {
                lines.Add("");
                lines.Add("[Hotkeys]");
                lines.Add("OpenPauseMenu = Keyboard/Escape");
                lines.Add(chord);
            }
            else
            {
                int end = SectionEnd(lines, section);
                int last = -1;
                for (int i = section + 1; i < end; i++)
                {
                    if (Key(lines[i]) == "OpenPauseMenu")
                        last = i;
                }
                int at = last >= 0 ? last + 1 : end;
                // Before the blank lines ending the section.
                while (last < 0 && at > section + 1 && lines[at - 1].Trim().Length == 0)
                    at--;
                lines.Insert(at, chord);
            }
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
            s_log.Info($"[EmulatorMenus] {name}: its menu opens with Start + Select ({select} & {start}), leaving Home to the overlay ({ini}).");
        }

        private static int Header(List<string> lines, string section) =>
            lines.FindIndex(l => string.Equals(l.Trim(), "[" + section + "]", StringComparison.OrdinalIgnoreCase));

        private static int SectionEnd(List<string> lines, int start)
        {
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));
            return end < 0 ? lines.Count : end;
        }

        private static string Key(string line)
        {
            int eq = line.IndexOf('=');
            return eq < 0 ? null : line.Substring(0, eq).Trim();
        }

        private static IEnumerable<string> Values(List<string> lines, string section, string key)
        {
            int start = Header(lines, section);
            if (start < 0)
                yield break;
            int end = SectionEnd(lines, start);
            for (int i = start + 1; i < end; i++)
            {
                if (string.Equals(Key(lines[i]), key, StringComparison.OrdinalIgnoreCase))
                    yield return lines[i].Substring(lines[i].IndexOf('=') + 1).Trim();
            }
        }

        // ---- RetroArch ---------------------------------------------------------------------------

        private static void RetroArch(string cfg, bool overlayHasHome)
        {
            if (cfg == null)
                return;
            List<string> lines = File.ReadAllLines(cfg).ToList();
            var wanted = new Dictionary<string, string>();
            string combo = CfgValue(lines, "input_menu_toggle_gamepad_combo");
            if (string.IsNullOrEmpty(combo) || combo == "0")
                wanted["input_menu_toggle_gamepad_combo"] = "4";
            string button = CfgValue(lines, "input_menu_toggle_btn");
            bool unset = string.IsNullOrEmpty(button) || string.Equals(button, "nul", StringComparison.OrdinalIgnoreCase);
            if (overlayHasHome && unset)
                wanted["input_menu_toggle_btn"] = RetroArchNoButton;
            else if (!overlayHasHome && button == RetroArchNoButton)
                wanted["input_menu_toggle_btn"] = "nul";
            if (wanted.Count == 0)
                return;
            if (Account.SettingsFile.SetCfg(cfg, wanted))
                s_log.Info($"[EmulatorMenus] RetroArch: its menu on Start + Select{(overlayHasHome ? ", Home left to the overlay" : "")} ({cfg}).");
        }

        private static string CfgValue(List<string> lines, string key)
        {
            foreach (string line in lines)
            {
                if (string.Equals(Key(line), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(line.IndexOf('=') + 1).Trim().Trim('"');
            }
            return null;
        }

        // ---- PPSSPP ------------------------------------------------------------------------------

        private static void Ppsspp(string ini)
        {
            if (!File.Exists(ini))
                return;   // made when PPSSPP first saves its settings; set at the next start
            List<string> lines = File.ReadAllLines(ini).ToList();
            int section = Header(lines, "ControlMapping");
            if (section < 0)
                return;
            int end = SectionEnd(lines, section);
            for (int i = section + 1; i < end; i++)
            {
                if (!string.Equals(Key(lines[i]), "Pause", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = lines[i].Substring(lines[i].IndexOf('=') + 1).Trim();
                var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                int home = parts.IndexOf("20-3");
                if (home < 0)
                    return;   // the user's own mapping
                parts.RemoveAt(home);
                if (!parts.Contains("20-109:20-108") && !parts.Contains("20-108:20-109"))
                    parts.Insert(home, "20-109:20-108");
                lines[i] = "Pause = " + string.Join(",", parts);
                File.WriteAllLines(ini, lines, new UTF8Encoding(false));
                s_log.Info($"[EmulatorMenus] PPSSPP: its pause menu on Select + Start, not Home ({ini}).");
                return;
            }
        }
    }
}
