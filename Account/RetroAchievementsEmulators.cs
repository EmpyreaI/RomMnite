using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RomM.Account
{
    /// <summary>
    /// Signs the emulator a game launches with in to RetroAchievements as the signed-in account
    /// (or out, when it has no RetroAchievements sign-in), by writing the emulator's own settings
    /// just before it starts, while it is closed. Each emulator keeps its sign-in its own way;
    /// the keys here were checked against each one's source:
    ///
    ///   RetroArch    retroarch.cfg               cheevos_enable, cheevos_username, cheevos_token
    ///   PCSX2        inis\PCSX2.ini [Achievements] Enabled, Username, LoginTimestamp, Token
    ///                (newer versions move Token into inis\secrets.ini by themselves)
    ///   DuckStation  settings.ini [Cheevos]      Enabled, Username, Token (encrypted), LoginTimestamp
    ///   PPSSPP       PSP\SYSTEM\ppsspp.ini [Achievements] AchievementsEnable, AchievementsUserName;
    ///                the token alone in PSP\SYSTEM\ppsspp_retroachievements.dat
    ///   Dolphin      User\Config\RetroAchievements.ini [Achievements] Enabled, Username, ApiToken
    ///   Flycast      emu.cfg [achievements]      Enabled, UserName, Token
    ///
    /// Nothing else in those files is touched, and a file only changes when a value differs.
    /// An emulator whose settings file is not found (never run yet) is left alone.
    /// </summary>
    internal static class RetroAchievementsEmulators
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        public static void Apply(Emulator emulator, string installDir, string username, string token)
        {
            if (emulator == null)
                return;
            string id = ((emulator.BuiltInConfigId ?? "") + " " + (emulator.Name ?? "") + " " + (installDir ?? "")).ToLowerInvariant();
            bool on = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(token);
            try
            {
                if (id.Contains("retroarch"))
                    RetroArch(installDir, on, username, token);
                else if (id.Contains("pcsx2"))
                    Pcsx2(installDir, on, username, token);
                else if (id.Contains("duckstation"))
                    DuckStation(installDir, on, username, token);
                else if (id.Contains("ppsspp"))
                    Ppsspp(installDir, on, username, token);
                else if (id.Contains("dolphin"))
                    Dolphin(installDir, on, username, token);
                else if (id.Contains("flycast"))
                    Flycast(installDir, on, username, token);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[RetroAchievements] Could not sign {emulator.Name} {(on ? "in" : "out")}: {ex.Message}");
            }
        }

        private static string Unix() => ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString();

        private static string FirstExisting(params string[] paths) => paths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

        private static string Documents(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);

        private static string Roaming(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);

        private static string In(string installDir, params string[] parts) =>
            string.IsNullOrEmpty(installDir) ? null : Path.Combine(new[] { installDir }.Concat(parts).ToArray());

        private static void Report(string emulator, string file, bool changed, bool on, string username)
        {
            if (changed)
                s_log.Info($"[RetroAchievements] {emulator}: {(on ? "signed in as " + username : "signed out")} ({file}).");
        }

        private static void Missing(string emulator) =>
            s_log.Info($"[RetroAchievements] {emulator}: its settings file was not found (run it once first); left as it is.");

        // ---- The emulators ---------------------------------------------------------------------

        private static void RetroArch(string installDir, bool on, string username, string token)
        {
            string cfg = FirstExisting(In(installDir, "retroarch.cfg"), Path.Combine(Roaming("RetroArch"), "retroarch.cfg"));
            if (cfg == null)
            {
                Missing("RetroArch");
                return;
            }
            bool changed = SettingsFile.SetCfg(cfg, new Dictionary<string, string>
            {
                ["cheevos_enable"] = on ? "true" : "false",
                ["cheevos_username"] = on ? username : "",
                ["cheevos_token"] = on ? token : "",
                ["cheevos_password"] = "",
            });
            Report("RetroArch", cfg, changed, on, username);
        }

        private static void Pcsx2(string installDir, bool on, string username, string token)
        {
            string ini = FirstExisting(In(installDir, "inis", "PCSX2.ini"), Path.Combine(Documents("PCSX2"), "inis", "PCSX2.ini"));
            if (ini == null)
            {
                Missing("PCSX2");
                return;
            }
            // The token goes in PCSX2.ini: older versions read it there, newer ones move it into
            // secrets.ini (over what was there) on starting.
            bool changed = SettingsFile.SetIni(ini, "Achievements", new Dictionary<string, string>
            {
                ["Enabled"] = on ? "true" : "false",
                ["Username"] = on ? username : null,
                ["Token"] = on ? token : null,
                ["LoginTimestamp"] = on ? Unix() : null,
            }, keepIfOnlyStamp: "LoginTimestamp");
            string secrets = Path.Combine(Path.GetDirectoryName(ini), "secrets.ini");
            if (File.Exists(secrets))
                changed |= SettingsFile.SetIni(secrets, "Achievements", new Dictionary<string, string> { ["Token"] = on ? token : null });
            Report("PCSX2", ini, changed, on, username);
        }

        private static void DuckStation(string installDir, bool on, string username, string token)
        {
            bool portable = !string.IsNullOrEmpty(installDir)
                && (File.Exists(Path.Combine(installDir, "portable.txt")) || File.Exists(Path.Combine(installDir, "settings.ini")));
            string ini = portable ? Path.Combine(installDir, "settings.ini") : Path.Combine(Documents("DuckStation"), "settings.ini");
            if (!File.Exists(ini))
            {
                Missing("DuckStation");
                return;
            }
            bool changed = SettingsFile.SetIni(ini, "Cheevos", new Dictionary<string, string>
            {
                ["Enabled"] = on ? "true" : "false",
                ["Username"] = on ? username : null,
                ["Token"] = on ? DuckStationToken(token, username, portable) : null,
                ["LoginTimestamp"] = on ? Unix() : null,
            }, keepIfOnlyStamp: "LoginTimestamp");
            Report("DuckStation", ini, changed, on, username);
        }

        private static void Ppsspp(string installDir, bool on, string username, string token)
        {
            string system = Directory.Exists(In(installDir, "memstick") ?? "")
                ? Path.Combine(installDir, "memstick", "PSP", "SYSTEM")
                : Path.Combine(Documents("PPSSPP"), "PSP", "SYSTEM");
            string ini = Path.Combine(system, "ppsspp.ini");
            if (!File.Exists(ini))
            {
                Missing("PPSSPP");
                return;
            }
            bool changed = SettingsFile.SetIni(ini, "Achievements", new Dictionary<string, string>
            {
                ["AchievementsEnable"] = on ? "True" : "False",
                ["AchievementsUserName"] = on ? username : "",
            });

            // PPSSPP reads the token from a file of its own, holding nothing else.
            string secret = Path.Combine(system, "ppsspp_retroachievements.dat");
            string current = File.Exists(secret) ? File.ReadAllText(secret) : null;
            if (on && current != token)
            {
                File.WriteAllText(secret, token);
                changed = true;
            }
            else if (!on && current != null)
            {
                File.Delete(secret);
                changed = true;
            }
            Report("PPSSPP", ini, changed, on, username);
        }

        private static void Dolphin(string installDir, bool on, string username, string token)
        {
            string user = null;
            if (!string.IsNullOrEmpty(installDir)
                && (File.Exists(Path.Combine(installDir, "portable.txt")) || Directory.Exists(Path.Combine(installDir, "User"))))
                user = Path.Combine(installDir, "User");
            else if (Directory.Exists(Documents("Dolphin Emulator")))
                user = Documents("Dolphin Emulator");
            else if (Directory.Exists(Roaming("Dolphin Emulator")))
                user = Roaming("Dolphin Emulator");
            if (user == null || !Directory.Exists(Path.Combine(user, "Config")))
            {
                Missing("Dolphin");
                return;
            }
            // A Dolphin new to achievements has no RetroAchievements.ini yet; it is made.
            string ini = Path.Combine(user, "Config", "RetroAchievements.ini");
            bool changed = SettingsFile.SetIni(ini, "Achievements", new Dictionary<string, string>
            {
                ["Enabled"] = on ? "True" : "False",
                ["Username"] = on ? username : "",
                ["ApiToken"] = on ? token : "",
            }, create: true);
            Report("Dolphin", ini, changed, on, username);
        }

        private static void Flycast(string installDir, bool on, string username, string token)
        {
            string cfg = FirstExisting(In(installDir, "emu.cfg"), Path.Combine(Roaming("flycast"), "emu.cfg"));
            if (cfg == null)
            {
                Missing("Flycast");
                return;
            }
            bool changed = SettingsFile.SetIni(cfg, "achievements", new Dictionary<string, string>
            {
                ["Enabled"] = on ? "yes" : "no",
                ["UserName"] = on ? username : "",
                ["Token"] = on ? token : "",
            });
            Report("Flycast", cfg, changed, on, username);
        }

        // ---- DuckStation's token ----------------------------------------------------------------

        /// <summary>
        /// The token as DuckStation keeps it (Achievements::EncryptLoginToken): AES-128-CBC with
        /// zero padding, base64. Key and IV are the two halves of a SHA-256 of this computer's
        /// MachineGuid (with its terminating NUL, as DuckStation reads it; left out when DuckStation
        /// is portable) followed by the username, hashed 100 more times. Got wrong, DuckStation
        /// only fails to read it and asks to sign in.
        /// </summary>
        internal static string DuckStationToken(string token, string username, bool portable)
        {
            var input = new List<byte>();
            if (!portable)
            {
                string guid = MachineGuid();
                if (!string.IsNullOrEmpty(guid))
                {
                    input.AddRange(Encoding.ASCII.GetBytes(guid));
                    input.Add(0);
                }
            }
            input.AddRange(Encoding.UTF8.GetBytes(username));

            byte[] key;
            using (var sha = SHA256.Create())
            {
                key = sha.ComputeHash(input.ToArray());
                for (int i = 0; i < 100; i++)
                    key = sha.ComputeHash(key);
            }

            byte[] plain = Encoding.UTF8.GetBytes(token);
            int padded = (plain.Length + 15) / 16 * 16;
            Array.Resize(ref plain, padded);

            using (var aes = Aes.Create())
            {
                aes.KeySize = 128;
                aes.Key = key.Take(16).ToArray();
                aes.IV = key.Skip(16).Take(16).ToArray();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    return Convert.ToBase64String(encryptor.TransformFinalBlock(plain, 0, plain.Length));
            }
        }

        private static string MachineGuid()
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    return key?.GetValue("MachineGuid") as string;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[RetroAchievements] Could not read the machine id for DuckStation: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Changes a few values in an emulator's settings file, leaving the rest as it was.</summary>
    internal static class SettingsFile
    {
        private sealed class Text
        {
            public List<string> Lines;
            public string NewLine;
            public bool Bom;
            public bool TrailingNewLine;
        }

        private static Text Read(string path)
        {
            byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : new byte[0];
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string content = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            string newLine = content.Contains("\r\n") ? "\r\n" : content.Contains("\n") ? "\n" : "\r\n";
            var lines = content.Length == 0 ? new List<string>() : content.Replace("\r\n", "\n").Split('\n').ToList();
            bool trailing = lines.Count > 0 && lines[lines.Count - 1].Length == 0;
            if (trailing)
                lines.RemoveAt(lines.Count - 1);
            return new Text { Lines = lines, NewLine = newLine, Bom = bom, TrailingNewLine = trailing || lines.Count == 0 };
        }

        private static void Write(string path, Text text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string content = string.Join(text.NewLine, text.Lines) + (text.TrailingNewLine ? text.NewLine : "");
            File.WriteAllText(path, content, new UTF8Encoding(text.Bom));
        }

        private static string KeyOf(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#' || trimmed[0] == '[')
                return null;
            int equals = trimmed.IndexOf('=');
            return equals <= 0 ? null : trimmed.Substring(0, equals).Trim();
        }

        /// <summary>
        /// Sets keys in an ini section ("key = value"); a null value removes the key. The section
        /// is added when missing. <paramref name="keepIfOnlyStamp"/> names a key (a timestamp) not
        /// rewritten when nothing else changed, so the file is not touched on every launch.
        /// Whether the file changed.
        /// </summary>
        public static bool SetIni(string path, string section, IDictionary<string, string> values, bool create = false, string keepIfOnlyStamp = null, string separator = " = ")
        {
            if (!File.Exists(path) && !create)
                return false;
            Text text = Read(path);
            List<string> lines = text.Lines;

            int start = lines.FindIndex(l => string.Equals(l.Trim(), "[" + section + "]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                if (values.Values.All(v => v == null))
                    return false;
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                    lines.Add("");
                lines.Add("[" + section + "]");
                start = lines.Count - 1;
            }
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));
            if (end < 0)
                end = lines.Count;

            // What would change, ignoring the timestamp.
            var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            var present = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = start + 1; i < end; i++)
            {
                string key = KeyOf(lines[i]);
                if (key != null && pending.ContainsKey(key) && !present.ContainsKey(key))
                    present[key] = i;
            }
            bool differs = pending.Any(p =>
                !string.Equals(p.Key, keepIfOnlyStamp, StringComparison.OrdinalIgnoreCase)
                && (present.TryGetValue(p.Key, out int line)
                    ? p.Value == null || ValueOf(lines[line]) != p.Value
                    : p.Value != null));
            if (!differs)
                return false;

            var removals = new List<int>();
            foreach (KeyValuePair<string, string> kv in pending)
            {
                if (present.TryGetValue(kv.Key, out int at))
                {
                    if (kv.Value == null)
                        removals.Add(at);
                    else
                        lines[at] = kv.Key + separator + kv.Value;
                }
            }
            // New keys go after the section's last setting.
            int insertAt = end;
            while (insertAt - 1 > start && lines[insertAt - 1].Trim().Length == 0)
                insertAt--;
            foreach (KeyValuePair<string, string> kv in pending.Where(p => p.Value != null && !present.ContainsKey(p.Key)))
                lines.Insert(insertAt++, kv.Key + separator + kv.Value);
            foreach (int at in removals.OrderByDescending(i => i))
                lines.RemoveAt(at);

            Write(path, text);
            return true;
        }

        private static string ValueOf(string line)
        {
            int equals = line.IndexOf('=');
            return equals < 0 ? null : line.Substring(equals + 1).Trim();
        }

        /// <summary>Sets keys in a RetroArch-style config (key = "value", no sections). Whether it changed.</summary>
        public static bool SetCfg(string path, IDictionary<string, string> values)
        {
            Text text = Read(path);
            List<string> lines = text.Lines;
            var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            for (int i = 0; i < lines.Count && pending.Count > 0; i++)
            {
                string key = KeyOf(lines[i]);
                if (key == null || !pending.TryGetValue(key, out string value))
                    continue;
                string wanted = key + " = \"" + value + "\"";
                if (lines[i] != wanted)
                {
                    lines[i] = wanted;
                    changed = true;
                }
                pending.Remove(key);
            }
            foreach (KeyValuePair<string, string> kv in pending)
            {
                lines.Add(kv.Key + " = \"" + kv.Value + "\"");
                changed = true;
            }
            if (changed)
                Write(path, text);
            return changed;
        }
    }
}
