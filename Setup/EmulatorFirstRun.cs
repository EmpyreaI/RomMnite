using Playnite.SDK;
using RomM.Account;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Setup
{
    /// <summary>
    /// First-run screens and prompts of the newer emulators, settled before a game starts (as
    /// Pcsx2FirstRun and DuckStationFirstRun do for theirs). All of them are set up portable
    /// (EmulatorSetup), so their settings are beside them. Keys from each emulator's source:
    ///
    ///   Azahar  user\config\qt-config.ini [UI] confirmClose = false (no "stop emulation?" when closed)
    ///   Cemu    portable\settings.xml made if missing: Cemu shows its Getting Started dialog
    ///           exactly when that file is missing (CemuApp.cpp); check_update off
    ///   xemu    xemu.toml beside it (its portable mode, xemu-settings.cc): no welcome screen or
    ///           update check, straight to the game, and the boot ROM, BIOS, EEPROM and hard
    ///           disk image from the bios folder (where the server's files go), told apart by
    ///           size and extension
    ///   Vita3K  portable\config.yml, once: its initial setup marked done, no welcome screen
    ///           (the one offering to install the firmware: welcome_dialog.cpp), no update check
    ///           (config.h)
    ///
    /// A setting the user changed is left alone: files are only made when missing, except
    /// Azahar's one key and xemu's file paths, which are only filled in while empty.
    /// </summary>
    internal static class EmulatorFirstRun
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        public static void Azahar(string installDir)
        {
            string user = Path.Combine(installDir, "user");
            if (!Directory.Exists(user))
                return;
            string ini = Path.Combine(user, "config", "qt-config.ini");
            // Qt's settings: a value with a "\default" flag beside it; the flag must be false for
            // the value to count.
            if (SettingsFile.SetIni(ini, "UI", new Dictionary<string, string>
                {
                    ["confirmClose\\default"] = "false",
                    ["confirmClose"] = "false",
                }, create: true, separator: "="))
                s_log.Info($"[EmulatorSetup] Azahar: no confirmation when closed ({ini}).");
        }

        public static void Cemu(string installDir)
        {
            string portable = Path.Combine(installDir, "portable");
            if (!Directory.Exists(portable))
                return;
            string settings = Path.Combine(portable, "settings.xml");
            if (File.Exists(settings))
                return;
            File.WriteAllText(settings,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<content>\n    <check_update>false</check_update>\n</content>\n",
                new UTF8Encoding(false));
            s_log.Info($"[EmulatorSetup] Cemu set up without its Getting Started dialog ({settings}).");
        }

        public static void Xemu(string installDir)
        {
            string toml = Path.Combine(installDir, "xemu.toml");
            string bios = Path.Combine(installDir, "bios");
            var files = Directory.Exists(bios) ? Directory.GetFiles(bios).Select(f => new FileInfo(f)).ToList() : new List<FileInfo>();
            // The MCPX boot ROM is 512 bytes, the EEPROM 256, the flash BIOS 256 KB or 1 MB;
            // the hard disk an image of its own kind.
            string bootrom = files.FirstOrDefault(f => f.Length == 512)?.FullName;
            string eeprom = files.FirstOrDefault(f => f.Length == 256)?.FullName;
            string flash = files.FirstOrDefault(f => f.Length == 256 * 1024 || f.Length == 1024 * 1024)?.FullName;
            string hdd = files.FirstOrDefault(f => f.Extension.Equals(".qcow2", StringComparison.OrdinalIgnoreCase))?.FullName;
            eeprom = eeprom ?? Path.Combine(bios, "eeprom.bin");   // xemu makes one when missing

            var lines = File.Exists(toml) ? File.ReadAllLines(toml).ToList() : new List<string>();
            bool changed = !File.Exists(toml);
            if (changed)
            {
                lines.AddRange(new[]
                {
                    "[general]", "show_welcome = false", "skip_boot_anim = true", "",
                    "[general.updates]", "check = false", "",
                    "[display.window]", "fullscreen_on_startup = true", "",
                    "[display.ui]", "show_menubar = false", "",
                    "[sys.files]",
                });
            }
            changed |= TomlFill(lines, "sys.files", "bootrom_path", bootrom);
            changed |= TomlFill(lines, "sys.files", "flashrom_path", flash);
            changed |= TomlFill(lines, "sys.files", "eeprom_path", eeprom);
            changed |= TomlFill(lines, "sys.files", "hdd_path", hdd);
            if (!changed)
                return;
            File.WriteAllLines(toml, lines, new UTF8Encoding(false));
            s_log.Info($"[EmulatorSetup] xemu set up ({toml}): boot ROM {bootrom ?? "(none found)"}, BIOS {flash ?? "(none found)"}, " +
                       $"hard disk {hdd ?? "(none found)"}.");
        }

        public static void Vita3K(string installDir)
        {
            string portable = Path.Combine(installDir, "portable");
            if (!Directory.Exists(portable))
                return;
            // Once per install. Vita3K writes its whole config.yml the first time it runs at all,
            // which during setup is installing the firmware (--firmware), before any game: the
            // file is there by the first launch, so it is edited, not only made when missing.
            string done = Path.Combine(portable, ".romm-first-run");
            if (File.Exists(done))
                return;
            string config = Path.Combine(portable, "config.yml");
            var lines = File.Exists(config) ? File.ReadAllLines(config).ToList() : new List<string> { "---" };
            YamlSet(lines, "initial-setup", "true");
            YamlSet(lines, "show-welcome", "false");             // the welcome screen, which offers the firmware
            YamlSet(lines, "check-for-updates", "false", add: false);   // in some builds only
            YamlSet(lines, "check-for-updates-mode", "0");       // UPDATE_STARTUP_OFF
            File.WriteAllLines(config, lines, new UTF8Encoding(false));
            File.WriteAllText(done, "");
            s_log.Info($"[EmulatorSetup] Vita3K set up without its welcome screen or update check ({config}).");
        }

        // Sets a top-level "key: value" in a YAML file's lines (Vita3K's config is flat).
        private static void YamlSet(List<string> lines, string key, string value, bool add = true)
        {
            int at = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
            if (at >= 0)
                lines[at] = key + ": " + value;
            else if (add)
            {
                int end = lines.FindIndex(l => l.Trim() == "...");
                lines.Insert(end >= 0 ? end : lines.Count, key + ": " + value);
            }
        }

        // Sets key = 'value' in a TOML [section] when the key is missing or empty. Whether it did.
        private static bool TomlFill(List<string> lines, string section, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            string line = key + " = '" + value + "'";   // a literal string: Windows paths as they are
            int start = lines.FindIndex(l => l.Trim() == "[" + section + "]");
            if (start < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                    lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(line);
                return true;
            }
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));
            if (end < 0)
                end = lines.Count;
            for (int i = start + 1; i < end; i++)
            {
                string t = lines[i].Trim();
                int eq = t.IndexOf('=');
                if (eq <= 0 || t.Substring(0, eq).Trim() != key)
                    continue;
                string current = t.Substring(eq + 1).Trim().Trim('\'', '"');
                if (current.Length > 0)
                    return false;
                lines[i] = line;
                return true;
            }
            int at = end;
            while (at - 1 > start && lines[at - 1].Trim().Length == 0)
                at--;
            lines.Insert(at, line);
            return true;
        }
    }
}
