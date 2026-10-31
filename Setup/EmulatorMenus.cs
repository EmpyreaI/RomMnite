using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Setup
{
    /// <summary>
    /// Emulators with in-game menus of their own, put on whichever controller button the
    /// overlay leaves free (<see cref="For"/>): the Home (Guide) button, Start + Select (Back +
    /// Start on an Xbox pad), or both sticks pressed in (L3 + R3). Written just before a game
    /// starts (EmulatorLaunch.PrepareSettings), and only where the emulator's menu has no
    /// controller binding of the user's own: a binding is taken as RomMnite's when it is exactly
    /// one of the three written here.
    ///
    ///   DuckStation  settings.ini  [Hotkeys] OpenPauseMenu = &lt;Guide&gt;, &lt;Select&gt; &amp; &lt;Start&gt; or
    ///                &lt;LeftStick&gt; &amp; &lt;RightStick&gt;, beside its keyboard key, on the controller its
    ///                Pad1 Start and Select are bound to
    ///   PCSX2        inis\PCSX2.ini, the same
    ///   RetroArch    retroarch.cfg input_menu_toggle_gamepad_combo "0" (none: the controller
    ///                profile's Home binding, RetroArch's own default), "4" (Start + Select) or
    ///                "2" (L3 + R3), where none of the user's own is set; and, while the menu is
    ///                not on Home, the profile's Home binding (input_menu_toggle_btn, which
    ///                RetroArch takes from the profile when retroarch.cfg has none) set to a
    ///                button no controller has, "99"; put back to "nul" when it is on Home again
    ///   PPSSPP       controls.ini [ControlMapping] Pause: Home ("20-3", PPSSPP's own default),
    ///                Select + Start ("20-109:20-108") or L3 + R3 ("20-106:20-107")
    ///
    /// Read from their source: DuckStation and PCSX2 input_manager.cpp / InputManager.cpp (a
    /// binding's parts joined by '&amp;' make a chord) and their SDL/XInput sources' button names
    /// (Back, Guide, Start, LeftStick, RightStick), RetroArch input_defines.h (INPUT_COMBO_L3_R3
    /// is 2, INPUT_COMBO_START_SELECT 4), input_driver.c (a bind of NO_BTN falls back to the
    /// profile's) and xinput_joypad.c (a button number past the pad's reads as not pressed),
    /// PPSSPP KeyMapDefaults.cpp, KeyMap.cpp (combos joined by ':'), InputState.h (XInput device
    /// 20) and KeyCodes.h (HOME 3, THUMBL 106, THUMBR 107, START 108, SELECT 109). Dolphin, Cemu,
    /// Azahar, xemu and Flycast have no menu on the controller; RPCS3's, Xenia's, Vita3K's and
    /// Eden's Home button is the console's own, part of the game.
    /// </summary>
    internal static class EmulatorMenus
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private const string RetroArchNoButton = "99";

        /// <summary>Where the emulators' menus go.</summary>
        public enum Place
        {
            Home,
            StartSelect,
            Sticks,
        }

        /// <summary>
        /// The emulators' menu button, from the overlay's (as chosen: "Guide", "BackStart",
        /// "L3R3" or "None") and whether something else has the Home button now (Steam, the Xbox
        /// Game Bar, Playnite: GameOverlay.GuideTakenBy), which moves the overlay to Start + Select.
        ///
        ///   overlay on Home                      menus on Start + Select
        ///   overlay on Start + Select            menus on Home, or the sticks when Home is taken
        ///   overlay on Home, but Home is taken   overlay on Start + Select, menus on the sticks
        ///   overlay on the sticks                menus on Start + Select
        ///   overlay on the keyboard only         menus on Home, or Start + Select when Home is taken
        /// </summary>
        public static Place For(string overlayButton, bool homeTaken)
        {
            switch (overlayButton ?? "Guide")
            {
                case "L3R3":
                    return Place.StartSelect;
                case "None":
                    return homeTaken ? Place.StartSelect : Place.Home;
                case "BackStart":
                    return homeTaken ? Place.Sticks : Place.Home;
                default:   // "Guide"
                    return homeTaken ? Place.Sticks : Place.StartSelect;
            }
        }

        public static string Words(Place place) =>
            place == Place.Home ? "Home" : place == Place.StartSelect ? "Start + Select" : "L3 + R3";

        /// <param name="id">The emulator's definition id and name, lower case.</param>
        public static void Apply(string id, string installDir, Place place)
        {
            try
            {
                if (id.Contains("duckstation"))
                    DuckStyle("DuckStation", Path.Combine(DuckStationFirstRun.DataRoot(installDir), "settings.ini"), place);
                else if (id.Contains("pcsx2"))
                    DuckStyle("PCSX2", FirstExisting(Path.Combine(installDir, "inis", "PCSX2.ini"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2", "inis", "PCSX2.ini")), place);
                else if (id.Contains("retroarch"))
                    RetroArch(FirstExisting(Path.Combine(installDir, "retroarch.cfg"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch", "retroarch.cfg")), place);
                else if (id.Contains("ppsspp"))
                    Ppsspp(Directory.Exists(Path.Combine(installDir, "memstick"))
                        ? Path.Combine(installDir, "memstick", "PSP", "SYSTEM", "controls.ini")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PPSSPP", "PSP", "SYSTEM", "controls.ini"), place);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorMenus] Could not set the emulator's menu button: {ex.Message}");
            }
        }

        private static string FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

        private static bool IsPad(string binding) =>
            binding.IndexOf("SDL-", StringComparison.OrdinalIgnoreCase) >= 0
            || binding.IndexOf("XInput-", StringComparison.OrdinalIgnoreCase) >= 0
            || binding.IndexOf("DInput-", StringComparison.OrdinalIgnoreCase) >= 0;

        // ---- DuckStation and PCSX2 --------------------------------------------------------------

        private static void DuckStyle(string name, string ini, Place place)
        {
            if (ini == null || !File.Exists(ini))
                return;
            List<string> lines = File.ReadAllLines(ini).ToList();

            // Start and Select as controller 1 has them, so the binding is on the same pad.
            string start = Values(lines, "Pad1", "Start").FirstOrDefault(IsPad) ?? "SDL-0/Start";
            string select = Values(lines, "Pad1", "Select").FirstOrDefault(IsPad) ?? "SDL-0/Back";
            string device = start.Substring(0, start.LastIndexOf('/') + 1);   // "SDL-0/"
            string wanted = place == Place.Home ? device + "Guide"
                : place == Place.StartSelect ? $"{select} & {start}"
                : $"{device}LeftStick & {device}RightStick";

            int section = Header(lines, "Hotkeys");
            var pads = new List<int>();
            if (section >= 0)
            {
                int end = SectionEnd(lines, section);
                for (int i = section + 1; i < end; i++)
                {
                    if (string.Equals(Key(lines[i]), "OpenPauseMenu", StringComparison.OrdinalIgnoreCase) && IsPad(lines[i]))
                        pads.Add(i);
                }
            }
            if (pads.Any(i => !Ours(lines[i])))
                return;   // a controller binding of the user's own
            if (pads.Count == 1 && Same(lines[pads[0]].Substring(lines[pads[0]].IndexOf('=') + 1), wanted))
                return;   // already there

            string line = "OpenPauseMenu = " + wanted;
            if (section < 0)
            {
                lines.Add("");
                lines.Add("[Hotkeys]");
                lines.Add("OpenPauseMenu = Keyboard/Escape");
                lines.Add(line);
            }
            else if (pads.Count > 0)
            {
                lines[pads[0]] = line;
                for (int k = pads.Count - 1; k >= 1; k--)
                    lines.RemoveAt(pads[k]);
            }
            else
            {
                int end = SectionEnd(lines, section);
                int last = -1;
                for (int i = section + 1; i < end; i++)
                {
                    if (string.Equals(Key(lines[i]), "OpenPauseMenu", StringComparison.OrdinalIgnoreCase))
                        last = i;
                }
                int at = last >= 0 ? last + 1 : end;
                // Before the blank lines ending the section.
                while (last < 0 && at > section + 1 && lines[at - 1].Trim().Length == 0)
                    at--;
                lines.Insert(at, line);
            }
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
            s_log.Info($"[EmulatorMenus] {name}: its menu opens with {Words(place)} ({wanted}) ({ini}).");
        }

        // One of the bindings written here: the Home button alone, Select & Start, or both sticks.
        private static bool Ours(string line)
        {
            string[] parts = line.Substring(line.IndexOf('=') + 1).Split('&').Select(p => p.Trim()).ToArray();
            if (!parts.All(IsPad))
                return false;
            Func<string, bool> ends = suffix => parts.Any(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (parts.Length == 1)
                return ends("/Guide");
            if (parts.Length != 2)
                return false;
            return (ends("/Start") && (ends("/Back") || ends("/Select")))
                || (ends("/LeftStick") && ends("/RightStick"));
        }

        private static bool Same(string a, string b)
        {
            Func<string, string> norm = x => string.Join("&", x.Split('&').Select(p => p.Trim().ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal));
            return norm(a) == norm(b);
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

        private static void RetroArch(string cfg, Place place)
        {
            if (cfg == null)
                return;
            List<string> lines = File.ReadAllLines(cfg).ToList();
            var wanted = new Dictionary<string, string>();

            // The combo: none (Home, through the profile), Start + Select or L3 + R3, unless the
            // user chose another.
            string combo = CfgValue(lines, "input_menu_toggle_gamepad_combo");
            bool ours = string.IsNullOrEmpty(combo) || combo == "0" || combo == "2" || combo == "4";
            string target = place == Place.Home ? "0" : place == Place.StartSelect ? "4" : "2";
            if (ours && (combo ?? "0") != target)
                wanted["input_menu_toggle_gamepad_combo"] = target;

            // The profile's Home binding: off while the menu is elsewhere, so the Home button is
            // left to the overlay or Steam; back when the menu is on Home.
            string button = CfgValue(lines, "input_menu_toggle_btn");
            bool unset = string.IsNullOrEmpty(button) || string.Equals(button, "nul", StringComparison.OrdinalIgnoreCase);
            if (ours && place != Place.Home && unset)
                wanted["input_menu_toggle_btn"] = RetroArchNoButton;
            else if (place == Place.Home && button == RetroArchNoButton)
                wanted["input_menu_toggle_btn"] = "nul";

            if (wanted.Count == 0)
                return;
            if (Account.SettingsFile.SetCfg(cfg, wanted))
                s_log.Info($"[EmulatorMenus] RetroArch: its menu on {Words(place)} ({cfg}).");
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

        private static readonly string[] s_ppssppOurs = { "20-3", "20-109:20-108", "20-108:20-109", "20-106:20-107", "20-107:20-106" };

        private static void Ppsspp(string ini, Place place)
        {
            if (!File.Exists(ini))
                return;   // made when PPSSPP first saves its settings; set at the next start
            List<string> lines = File.ReadAllLines(ini).ToList();
            int section = Header(lines, "ControlMapping");
            if (section < 0)
                return;
            string target = place == Place.Home ? "20-3" : place == Place.StartSelect ? "20-109:20-108" : "20-106:20-107";
            int end = SectionEnd(lines, section);
            for (int i = section + 1; i < end; i++)
            {
                if (!string.Equals(Key(lines[i]), "Pause", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = lines[i].Substring(lines[i].IndexOf('=') + 1).Trim();
                var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                int at = parts.FindIndex(p => s_ppssppOurs.Contains(p));
                if (at < 0)
                    return;   // the user's own mapping
                var kept = parts.Where(p => !s_ppssppOurs.Contains(p)).ToList();
                kept.Insert(Math.Min(at, kept.Count), target);
                if (kept.SequenceEqual(parts))
                    return;
                lines[i] = "Pause = " + string.Join(",", kept);
                File.WriteAllLines(ini, lines, new UTF8Encoding(false));
                s_log.Info($"[EmulatorMenus] PPSSPP: its pause menu on {Words(place)} ({ini}).");
                return;
            }
        }
    }
}
