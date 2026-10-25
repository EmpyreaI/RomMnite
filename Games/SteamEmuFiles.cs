using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RomM.Games
{
    /// <summary>
    /// The files Steam emulators keep a game's achievements in, read (never written). A Windows
    /// game whose steam_api.dll was replaced by one keeps its unlocks in a file of its own instead
    /// of on Steam; EmulatorAchievements makes them the account's as it does RPCS3's trophies.
    ///
    /// The emulators and their files, as the ones in use write them (Goldberg's successor
    /// gbe_fork read from its source; the older ones as Achievement Watcher, xan105, reads them):
    ///
    ///   gbe_fork      %APPDATA%\GSE Saves\&lt;appid&gt;\achievements.json, or where its
    ///                 steam_settings\configs.user.ini puts saves ([user::saves]
    ///                 local_save_path, relative to the dll; saves_folder_name)
    ///   Goldberg      %APPDATA%\Goldberg SteamEmu Saves\&lt;appid&gt;\achievements.json, or beside the
    ///                 dll when steam_settings\local_save.txt names a folder
    ///   EMPRESS       %APPDATA%\EMPRESS and Public Documents\EMPRESS: &lt;appid&gt;\remote\&lt;appid&gt;\achievements.json
    ///   CODEX, RUNE   Public Documents\Steam\CODEX (RUNE), %APPDATA%\Steam\CODEX (RUNE): &lt;appid&gt;\achievements.ini
    ///   SKIDROW       %LOCALAPPDATA%\SKIDROW, Documents\SkidRow: &lt;appid&gt;\...
    ///   SmartSteamEmu %APPDATA%\SmartSteamEmu\&lt;appid&gt;\stats.bin
    ///   Reloaded, 3DM %PROGRAMDATA%\Steam\&lt;name&gt;\&lt;appid&gt;\stats\achievements.ini
    ///   CreamAPI      %APPDATA%\CreamAPI\&lt;appid&gt;\stats\CreamAPI.Achievements.cfg
    ///
    /// The JSON ones: { "ACH_NAME": { "earned": true, "earned_time": 1700000000 } }. The INI ones:
    /// a section per achievement (Achieved / UnlockTime and the like), or the older layouts kept
    /// below. SmartSteamEmu's is binary, by a CRC-32 of each achievement's name.
    ///
    /// The list itself (names, descriptions, icons): gbe_fork and Goldberg keep it in the game's
    /// steam_settings\achievements.json; otherwise Steam's (EmulatorAchievements.SteamEmu).
    /// </summary>
    internal static class SteamEmuFiles
    {
        /// <summary>An achievement as the file has it.</summary>
        internal sealed class Unlock
        {
            public bool Achieved;
            public DateTime? WhenUtc;
        }

        /// <summary>An achievement of the list.</summary>
        internal sealed class Entry
        {
            public string Id;
            public string Title;
            public string Description;
            public bool Hidden;
            public string Icon;       // as the source names it: a path relative to steam_settings, or Steam's file name
            public string IconGray;
            public int Order;
            public double? Percent;   // Steam's: the share of players who have it
        }

        // The file names an emulator's folder for a game may hold, in the order they are tried.
        internal static readonly string[] FileNames =
        {
            "achievements.json", "achievements.ini", "achiev.ini", "stats.ini", "Achievements.ini",
            Path.Combine("stats", "achievements.ini"), Path.Combine("stats", "CreamAPI.Achievements.cfg"), "stats.bin",
        };

        // Sections that are not achievements.
        private static readonly HashSet<string> s_notAchievements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SteamAchievements", "Steam64", "Steam", "Settings",
        };

        // ---- Unlocks ----------------------------------------------------------------------------

        /// <summary>
        /// The achievements in an emulator's file, by name (as unlocked or not, with the time when
        /// the file has it). <paramref name="names"/>: the list's names, which SmartSteamEmu's
        /// binary file needs (it keeps only a CRC of each).
        /// </summary>
        public static Dictionary<string, Unlock> Parse(string fileName, byte[] data, IEnumerable<string> names = null)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (ext == ".bin")
                return ParseSse(data, names ?? Enumerable.Empty<string>());
            string text = Decode(data);
            return ext == ".json" ? ParseJson(text) : ParseIni(text);
        }

        private static string Decode(byte[] data)
        {
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            return Encoding.UTF8.GetString(data);
        }

        public static Dictionary<string, Unlock> ParseJson(string text)
        {
            var result = new Dictionary<string, Unlock>(StringComparer.OrdinalIgnoreCase);
            JToken root = JToken.Parse(text);
            if (root is JObject wrapped && wrapped["ACHIEVE_DATA"] is JObject inner)
                root = inner;
            if (root is JArray array)
            {
                // A list of { "name": ..., "earned": ... } (some builds).
                foreach (JObject item in array.OfType<JObject>())
                {
                    string name = (string)(item["name"] ?? item["apiname"] ?? item["id"]);
                    if (!string.IsNullOrEmpty(name))
                        result[name] = FromFields(item.Properties().ToDictionary(p => p.Name, p => Scalar(p.Value), StringComparer.OrdinalIgnoreCase));
                }
                return result;
            }
            if (!(root is JObject obj))
                return result;
            foreach (JProperty property in obj.Properties())
            {
                if (s_notAchievements.Contains(property.Name))
                    continue;
                if (property.Value is JObject fields)
                    result[property.Name] = FromFields(fields.Properties().ToDictionary(p => p.Name, p => Scalar(p.Value), StringComparer.OrdinalIgnoreCase));
                else
                    result[property.Name] = new Unlock { Achieved = Truthy(Scalar(property.Value)) };
            }
            return result;
        }

        private static string Scalar(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Boolean)
                return (bool)token ? "1" : "0";
            return token.Type == JTokenType.Object || token.Type == JTokenType.Array ? null : Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);
        }

        public static Dictionary<string, Unlock> ParseIni(string text)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            Dictionary<string, string> current = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;
                if (line[0] == '[' && line.EndsWith("]", StringComparison.Ordinal))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    if (!sections.TryGetValue(name, out current))
                    {
                        sections[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        order.Add(name);
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || current == null)
                    continue;
                current[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Trim('"');
            }

            var result = new Dictionary<string, Unlock>(StringComparer.OrdinalIgnoreCase);

            // [Achievements] NAME=1 with [AchievementsUnlockTimes] NAME=time (hoodlum, DARKSiDERS).
            if (sections.TryGetValue("Achievements", out var flags) && sections.TryGetValue("AchievementsUnlockTimes", out var times))
            {
                foreach (KeyValuePair<string, string> flag in flags)
                {
                    times.TryGetValue(flag.Key, out string time);
                    result[flag.Key] = new Unlock { Achieved = Truthy(flag.Value), WhenUtc = Time(time) };
                }
                return result;
            }

            // [State] NAME=0101 with [Time] NAME=hex (3DM, Reloaded).
            if (sections.TryGetValue("State", out var states) && sections.TryGetValue("Time", out var stamps))
            {
                foreach (KeyValuePair<string, string> state in states)
                {
                    stamps.TryGetValue(state.Key, out string stamp);
                    result[state.Key] = new Unlock
                    {
                        Achieved = state.Value == "0101" || state.Value == "1",
                        WhenUtc = Time(HexLe(stamp)?.ToString(CultureInfo.InvariantCulture) ?? stamp),
                    };
                }
                return result;
            }

            // A section per achievement.
            foreach (string name in order)
            {
                if (s_notAchievements.Contains(name))
                    continue;
                Dictionary<string, string> fields = sections[name];
                if (fields.Count == 0)
                    continue;
                // RLD!: the numbers as little-endian hex (State=01000000), told by its State.
                bool hex = fields.TryGetValue("State", out string state) && HexLe(state) != null;
                var plain = hex
                    ? fields.ToDictionary(f => f.Key, f => HexLe(f.Value)?.ToString(CultureInfo.InvariantCulture) ?? f.Value, StringComparer.OrdinalIgnoreCase)
                    : fields;
                result[name] = FromFields(plain);
            }
            return result;
        }

        // From one achievement's fields, whatever the emulator calls them.
        private static Unlock FromFields(IDictionary<string, string> fields)
        {
            string Get(params string[] keys)
            {
                foreach (string key in keys)
                {
                    if (fields.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value))
                        return value;
                }
                return null;
            }

            bool achieved = Truthy(Get("earned", "Achieved", "State", "HaveAchieved", "Unlocked"));
            if (!achieved)
            {
                // Done by progress (CODEX for some games).
                long.TryParse(Get("CurProgress", "progress"), out long have);
                long.TryParse(Get("MaxProgress", "max_progress"), out long need);
                achieved = need > 0 && have >= need;
            }
            return new Unlock
            {
                Achieved = achieved,
                WhenUtc = achieved ? Time(Get("earned_time", "UnlockTime", "unlocktime", "unlock_time", "HaveAchievedTime", "HaveHaveAchievedTime", "Time")) : null,
            };
        }

        private static bool Truthy(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            value = value.Trim();
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // Unix seconds (or milliseconds); none for a time that cannot be one.
        internal static DateTime? Time(string value)
        {
            if (string.IsNullOrEmpty(value) || !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return null;
            if (number > 1e12)
                number /= 1000;
            if (number < 1e9 || number > 4.2e9)
                return null;   // not a time (CreamAPI's cut-short ones, zeros)
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(number);
        }

        // "01000000" (eight hex digits, little-endian) as its number; null for anything else.
        private static uint? HexLe(string value)
        {
            if (value == null || value.Length != 8 || !value.All(Uri.IsHexDigit))
                return null;
            byte[] bytes = Enumerable.Range(0, 4).Select(i => Convert.ToByte(value.Substring(i * 2, 2), 16)).ToArray();
            return BitConverter.ToUInt32(bytes, 0);
        }

        // SmartSteamEmu's stats.bin: a count, then 24-byte entries (a CRC-32 of the name, the
        // unlock time at 8, the value at 20: 0 or 1 for an achievement).
        private static Dictionary<string, Unlock> ParseSse(byte[] data, IEnumerable<string> names)
        {
            var result = new Dictionary<string, Unlock>(StringComparer.OrdinalIgnoreCase);
            if (data.Length < 4)
                return result;
            int count = BitConverter.ToInt32(data, 0);
            var byCrc = new Dictionary<uint, string>();
            foreach (string name in names)
                byCrc[Crc32(Encoding.UTF8.GetBytes(name))] = name;
            for (int i = 0; i < count && 4 + (i + 1) * 24 <= data.Length; i++)
            {
                int at = 4 + i * 24;
                uint crc = BitConverter.ToUInt32(data, at);
                int value = BitConverter.ToInt32(data, at + 20);
                if (value > 1 || !byCrc.TryGetValue(crc, out string name))
                    continue;   // a statistic, or a name not in the list
                result[name] = new Unlock
                {
                    Achieved = value == 1,
                    WhenUtc = value == 1 ? Time(BitConverter.ToInt32(data, at + 8).ToString(CultureInfo.InvariantCulture)) : null,
                };
            }
            return result;
        }

        internal static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }

        // ---- The list ---------------------------------------------------------------------------

        /// <summary>
        /// gbe_fork's and Goldberg's list (steam_settings\achievements.json): [{ "name", "displayName",
        /// "description", "hidden", "icon", "icon_gray" }], the texts either plain or by language.
        /// </summary>
        public static List<Entry> ParseSettingsList(string text, string language = "english")
        {
            var list = new List<Entry>();
            if (!(JToken.Parse(text) is JArray array))
                return list;
            int order = 0;
            foreach (JObject item in array.OfType<JObject>())
            {
                string id = (string)item["name"];
                if (string.IsNullOrEmpty(id))
                    continue;
                list.Add(new Entry
                {
                    Id = id,
                    Title = Localized(item["displayName"], language) ?? id,
                    Description = Localized(item["description"], language) ?? "",
                    Hidden = Truthy(Scalar(item["hidden"])),
                    Icon = (string)item["icon"],
                    IconGray = (string)(item["icon_gray"] ?? item["icongray"]),
                    Order = order++,
                });
            }
            return list;
        }

        private static string Localized(JToken token, string language)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.String)
                return (string)token;
            if (token is JObject byLanguage)
            {
                return (string)byLanguage[language]
                    ?? (string)byLanguage["english"]
                    ?? byLanguage.Properties().Select(p => (string)p.Value).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            }
            return null;
        }

        /// <summary>
        /// Steam's list (IPlayerService/GetGameAchievements): { "response": { "achievements": [
        /// { "internal_name", "localized_name", "localized_desc", "icon", "icon_gray", "hidden" } ] } }.
        /// Also Steam's GetSchemaForGame layout ({ "game": { "availableGameStats": { "achievements":
        /// [{ "name", "displayName", "description", "icon", "icongray", "hidden" }] } } }).
        /// </summary>
        public static List<Entry> ParseSteamList(string text)
        {
            var list = new List<Entry>();
            JToken root = JToken.Parse(text);
            JArray items = root.SelectToken("response.achievements") as JArray
                ?? root.SelectToken("game.availableGameStats.achievements") as JArray
                ?? new JArray();
            int order = 0;
            foreach (JObject item in items.OfType<JObject>())
            {
                string id = (string)(item["internal_name"] ?? item["name"]);
                if (string.IsNullOrEmpty(id))
                    continue;
                list.Add(new Entry
                {
                    Id = id,
                    Title = (string)(item["localized_name"] ?? item["displayName"]) ?? id,
                    Description = (string)(item["localized_desc"] ?? item["description"]) ?? "",
                    Hidden = Truthy(Scalar(item["hidden"])),
                    Icon = (string)item["icon"],
                    IconGray = (string)(item["icon_gray"] ?? item["icongray"]),
                    Order = order++,
                    Percent = double.TryParse(Scalar(item["player_percent_unlocked"] ?? item["percent"]), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
                        ? percent : (double?)null,
                });
            }
            return list;
        }

        // ---- The game's folder --------------------------------------------------------------------

        private static readonly Regex s_appIdLine = new Regex(@"^\s*app_?id\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>
        /// The game's Steam app id as the emulator's own files in its folder name it (steam_appid.txt,
        /// steam_settings\steam_appid.txt, the emulators' .ini files), or 0.
        /// </summary>
        public static int AppIdIn(string dllFolder)
        {
            foreach (string file in new[]
            {
                Path.Combine(dllFolder, "steam_settings", "steam_appid.txt"),
                Path.Combine(dllFolder, "steam_appid.txt"),
            })
            {
                if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out int id) && id > 0)
                    return id;
            }
            foreach (string name in new[] { "steam_emu.ini", "cream_api.ini", "SmartSteamEmu.ini", "ColdClientLoader.ini", "SKIDROW.ini", "CPY.ini", "ds.ini" })
            {
                string file = Path.Combine(dllFolder, name);
                if (!File.Exists(file))
                    continue;
                Match match = s_appIdLine.Match(File.ReadAllText(file));
                if (match.Success && int.TryParse(match.Groups[1].Value, out int id) && id > 0)
                    return id;
            }
            return 0;
        }

        /// <summary>
        /// The folders in a game's install folder holding a steam_api(64).dll (or a steam_settings
        /// folder), nearest the top first; a few levels down at most.
        /// </summary>
        public static List<string> DllFolders(string installDir, int depth = 5)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return found;
            var level = new List<string> { installDir };
            for (int d = 0; d <= depth && level.Count > 0 && found.Count < 8; d++)
            {
                var next = new List<string>();
                foreach (string folder in level)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(folder, "steam_api64.dll")) || File.Exists(Path.Combine(folder, "steam_api.dll"))
                            || Directory.Exists(Path.Combine(folder, "steam_settings")))
                            found.Add(folder);
                        next.AddRange(Directory.GetDirectories(folder));
                    }
                    catch
                    {
                        // Not readable: passed over.
                    }
                }
                level = next.Count > 4000 ? next.Take(4000).ToList() : next;
            }
            return found;
        }

        /// <summary>
        /// Where the emulators would keep this game's achievements: every folder they use, by the
        /// game's app id (gbe_fork's and Goldberg's per-game settings in <paramref name="dllFolder"/>
        /// first). Folders, not files: each may hold one of <see cref="FileNames"/>.
        /// </summary>
        public static List<string> SaveFolders(int appId, string dllFolder, Func<Environment.SpecialFolder, string> special = null)
        {
            special = special ?? Environment.GetFolderPath;
            string id = appId.ToString(CultureInfo.InvariantCulture);
            string appData = special(Environment.SpecialFolder.ApplicationData);
            string localAppData = special(Environment.SpecialFolder.LocalApplicationData);
            string programData = special(Environment.SpecialFolder.CommonApplicationData);
            string publicDocs = special(Environment.SpecialFolder.CommonDocuments);
            string documents = special(Environment.SpecialFolder.MyDocuments);
            var folders = new List<string>();

            if (!string.IsNullOrEmpty(dllFolder))
            {
                string settings = Path.Combine(dllFolder, "steam_settings");
                // gbe_fork: [user::saves] in configs.user.ini.
                string config = Path.Combine(settings, "configs.user.ini");
                if (File.Exists(config))
                {
                    string text = File.ReadAllText(config);
                    string local = IniValue(text, "user::saves", "local_save_path");
                    if (!string.IsNullOrEmpty(local))
                        folders.Add(Path.Combine(Path.GetFullPath(Path.Combine(dllFolder, local)), id));
                    string name = IniValue(text, "user::saves", "saves_folder_name");
                    if (!string.IsNullOrEmpty(name))
                        folders.Add(Path.Combine(appData, name, id));
                }
                // Goldberg: local_save.txt names a folder beside the dll.
                string localSave = Path.Combine(settings, "local_save.txt");
                if (File.Exists(localSave))
                {
                    string name = File.ReadAllText(localSave).Trim();
                    folders.Add(Path.Combine(dllFolder, name.Length > 0 ? name : "SAVE", id));
                }
            }

            folders.Add(Path.Combine(appData, "GSE Saves", id));
            folders.Add(Path.Combine(appData, "Goldberg SteamEmu Saves", id));
            folders.Add(Path.Combine(appData, "EMPRESS", id, "remote", id));
            folders.Add(Path.Combine(publicDocs, "EMPRESS", id, "remote", id));
            foreach (string group in new[] { "CODEX", "RUNE" })
            {
                folders.Add(Path.Combine(publicDocs, "Steam", group, id));
                folders.Add(Path.Combine(appData, "Steam", group, id));
            }
            folders.Add(Path.Combine(localAppData, "SKIDROW", id));
            folders.Add(Path.Combine(documents, "SkidRow", id));
            folders.Add(Path.Combine(appData, "SmartSteamEmu", id));
            folders.Add(Path.Combine(appData, "CreamAPI", id));
            try
            {
                string steam = Path.Combine(programData, "Steam");
                if (Directory.Exists(steam))
                    folders.AddRange(Directory.GetDirectories(steam).Select(d => Path.Combine(d, id)));
            }
            catch
            {
                // Not readable.
            }
            return folders;
        }

        private static string IniValue(string text, string section, string key)
        {
            string current = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(current, section, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Split('#', ';')[0].Trim().Trim('"');
            }
            return null;
        }

        /// <summary>The achievements file in one of the folders: the most recently written, or null.</summary>
        public static string NewestFile(IEnumerable<string> folders)
        {
            string best = null;
            DateTime newest = DateTime.MinValue;
            foreach (string folder in folders)
            {
                foreach (string name in FileNames)
                {
                    string file = Path.Combine(folder, name);
                    try
                    {
                        if (!File.Exists(file))
                            continue;
                        DateTime when = File.GetLastWriteTimeUtc(file);
                        if (when > newest)
                        {
                            newest = when;
                            best = file;
                        }
                        break;   // one file per folder: the first of the names
                    }
                    catch
                    {
                        // Not readable.
                    }
                }
            }
            return best;
        }

        /// <summary>A name safe as a file name (achievement names are letters, digits and _ almost always).</summary>
        public static string SafeName(string id)
        {
            var text = new StringBuilder();
            foreach (char c in id ?? "")
                text.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return text.ToString();
        }
    }
}
