using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RomM.Account
{
    /// <summary>
    /// Each account its own user in the emulators that have users of their own, so their saves,
    /// trophies and achievements are apart inside the emulator itself, with no swapping:
    ///
    ///   RPCS3         a user is a folder dev_hdd0\home\&lt;8 digits&gt;\ (savedata, trophy, exdata and a
    ///                 localusername file), as its own user manager makes them; the user it starts
    ///                 as is [Users] active_user in GuiConfigs\persistent_settings.dat.
    ///   Xenia Canary  a profile is content\&lt;xuid&gt;\FFFE07D1\00010000\&lt;xuid&gt;\Account (the Xbox
    ///                 account file: RC4, keyed by HMAC-SHA1 with the console's well-known key 0x19,
    ///                 as Xenia's ProfileManager writes it); the profile it signs in is
    ///                 [Profiles] logged_profile_slot_0_xuid in xenia-canary.config.toml.
    ///
    /// Before a game launches, the account's user is found (one already linked to it; one named
    /// as the account; one from before, if the player says it is theirs) or made, and set as the
    /// one the emulator starts with. The emulator itself is never opened for any of this.
    /// </summary>
    internal sealed class EmulatorUsers
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly byte[] XeKey19 = { 0xE1, 0xBC, 0x15, 0x9C, 0x73, 0xB1, 0xEA, 0xE9, 0xAB, 0x31, 0x70, 0xF3, 0xAD, 0x47, 0xEB, 0xF3 };
        private static readonly byte[] XeKey19Devkit = { 0xDA, 0xB6, 0x9A, 0xD9, 0x8E, 0x28, 0x76, 0x4F, 0x97, 0x7E, 0xE2, 0x48, 0x7E, 0x4F, 0x3F, 0x68 };
        private const int AccountInfoSize = 0x17C;
        private const string Dashboard = "FFFE07D1";
        private const string ProfileType = "00010000";

        private sealed class Links
        {
            public string Rpcs3User { get; set; }
            public string XeniaXuid { get; set; }
            public List<string> Declined { get; set; } = new List<string>();   // "rpcs3:00000001", "xenia:E03..."
        }

        private readonly RomM _plugin;
        private readonly string _accountsFolder;
        private string _path;
        private readonly object _lock = new object();

        public EmulatorUsers(RomM plugin, string dataFolder)
        {
            _plugin = plugin;
            _accountsFolder = Path.Combine(dataFolder, "Accounts");
        }

        /// <summary>The signed-in account's links (its own file).</summary>
        public void UseAccount(string accountFolder)
        {
            lock (_lock)
                _path = Path.Combine(accountFolder, "EmulatorUsers.json");
        }

        private Links Load(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    return JsonConvert.DeserializeObject<Links>(File.ReadAllText(path)) ?? new Links();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorUsers] Could not read {path}: {ex.Message}");
            }
            return new Links();
        }

        private void Save(Links links)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, JsonConvert.SerializeObject(links, Formatting.Indented));
        }

        // What the other accounts on this computer are linked to.
        private List<Links> Others()
        {
            var result = new List<Links>();
            try
            {
                if (Directory.Exists(_accountsFolder))
                {
                    foreach (string file in Directory.GetFiles(_accountsFolder, "EmulatorUsers.json", SearchOption.AllDirectories))
                    {
                        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                            result.Add(Load(file));
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorUsers] Could not read the other accounts' links: {ex.Message}");
            }
            return result;
        }

        /// <summary>"rpcs3", "xenia", or null for an emulator without users of its own.</summary>
        public static string KindOf(Emulator emulator, string installDir)
        {
            if (emulator == null)
                return null;
            string id = ((emulator.BuiltInConfigId ?? "") + " " + (emulator.Name ?? "") + " " + (installDir ?? "")).ToLowerInvariant();
            if (id.Contains("rpcs3"))
                return "rpcs3";
            if (id.Contains("xenia"))
                return "xenia";
            return null;
        }

        private string KindOf(Game game, out string installDir)
        {
            installDir = null;
            if (game == null || game.PluginId != _plugin.Id)
                return null;
            Emulator emulator = _plugin.SaveSync.EmulatorFor(game)?.Emulator;
            if (emulator == null)
                return null;
            installDir = Games.PlaynitePath.Resolve(_plugin.Playnite, emulator.InstallDir);
            return KindOf(emulator, installDir);
        }

        /// <summary>Whether the game's emulator keeps users apart itself (its saves are not swapped).</summary>
        public bool Separates(Game game)
        {
            try
            {
                return KindOf(game, out string _) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether the emulator is set to start as this account's user now: its saves and
        /// trophies on disk are the account's.
        /// </summary>
        public bool IsActiveFor(Game game)
        {
            try
            {
                string kind = KindOf(game, out string installDir);
                if (kind == null)
                    return false;
                Links links;
                lock (_lock)
                    links = Load(_path);
                if (kind == "rpcs3")
                    return links.Rpcs3User != null && links.Rpcs3User == Rpcs3ActiveUser(installDir);
                return links.XeniaXuid != null && string.Equals(links.XeniaXuid, XeniaSignedIn(installDir), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Before a game launches: the account's user in its emulator, set as the one it starts
        /// with. False when that could not be done (the game should not start as someone else).
        /// </summary>
        public bool Prepare(Game game, string label, string username)
        {
            string kind;
            string installDir;
            try
            {
                kind = KindOf(game, out installDir);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorUsers] Could not tell {game?.Name}'s emulator: {ex.Message}");
                return true;
            }
            if (kind == null || _path == null)
                return true;
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            {
                s_log.Warn($"[EmulatorUsers] {game.Name}: the emulator's folder is not known; its user is left as it is.");
                return true;
            }

            try
            {
                lock (_lock)
                {
                    Links links = Load(_path);
                    if (kind == "rpcs3")
                        PrepareRpcs3(installDir, links, label, username);
                    else
                        PrepareXenia(installDir, links, label, username);
                    Save(links);
                }
                return true;
            }
            catch (Exception ex)
            {
                s_log.Error(ex, $"[EmulatorUsers] Could not set {game.Name}'s emulator to the account's user.");
                _plugin.Playnite.Dialogs.ShowErrorMessage(
                    $"{game.Name} was not started: its emulator could not be set to your own user, so you would have played " +
                    $"(and saved, and earned trophies) as someone else.\n\n{ex.Message}", "Emulator user");
                return false;
            }
        }

        private bool Ask(string question)
        {
            return _plugin.Playnite.Dialogs.ShowMessage(question, "Whose is this?", System.Windows.MessageBoxButton.YesNo)
                == System.Windows.MessageBoxResult.Yes;
        }

        private static bool SameName(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        // ---- RPCS3 ------------------------------------------------------------------------------

        private static string Rpcs3Home(string installDir) => Path.Combine(installDir, "dev_hdd0", "home");

        private static string Rpcs3Settings(string installDir) => Path.Combine(installDir, "GuiConfigs", "persistent_settings.dat");

        internal static string Rpcs3ActiveUser(string installDir) => Rpcs3ConfiguredUser(installDir) ?? "00000001";

        internal static string Rpcs3ConfiguredUser(string installDir) => Saves.Handlers.EmulatorActiveUser.Rpcs3ConfiguredUser(installDir);

        private void PrepareRpcs3(string installDir, Links links, string label, string username)
        {
            string home = Rpcs3Home(installDir);
            Directory.CreateDirectory(home);
            List<string> users = new DirectoryInfo(home).GetDirectories()
                .Where(d => d.Name.Length == 8 && d.Name.All(char.IsDigit))
                .Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Func<string, string> nameOf = id =>
            {
                try
                {
                    string file = Path.Combine(home, id, "localusername");
                    return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                }
                catch
                {
                    return "";
                }
            };
            var taken = new HashSet<string>(Others().Select(o => o.Rpcs3User).Where(u => u != null));

            string mine = links.Rpcs3User != null && users.Contains(links.Rpcs3User) ? links.Rpcs3User : null;

            // One named as the account.
            if (mine == null)
                mine = users.FirstOrDefault(u => !taken.Contains(u) && (SameName(nameOf(u), label) || SameName(nameOf(u), username)));

            // One from before accounts were kept apart, with saves or trophies in it: the player says.
            if (mine == null)
            {
                foreach (string user in users.Where(u => !taken.Contains(u) && !links.Declined.Contains("rpcs3:" + u)))
                {
                    if (!HasData(Path.Combine(home, user, "savedata")) && !HasData(Path.Combine(home, user, "trophy")))
                        continue;
                    string name = nameOf(user);
                    if (Ask($"RPCS3 on this computer has a user \"{(name.Length > 0 ? name : user)}\" ({user}) with saves or trophies " +
                            "from before each account had its own.\n\nIs it yours? If not, you get a user of your own."))
                    {
                        mine = user;
                        break;
                    }
                    links.Declined.Add("rpcs3:" + user);
                }
            }

            // Otherwise a new one, made as RPCS3's user manager makes them.
            if (mine == null)
            {
                int next = users.Select(u => int.Parse(u)).DefaultIfEmpty(0).Max() + 1;
                mine = next.ToString("00000000");
                string dir = Path.Combine(home, mine);
                Directory.CreateDirectory(Path.Combine(dir, "exdata"));
                Directory.CreateDirectory(Path.Combine(dir, "savedata"));
                Directory.CreateDirectory(Path.Combine(dir, "trophy"));
                File.WriteAllText(Path.Combine(dir, "localusername"), Rpcs3Name(label, username, mine));
                s_log.Info($"[EmulatorUsers] Made RPCS3 user {mine} for {label}.");
            }

            links.Rpcs3User = mine;
            if (SettingsFile.SetIni(Rpcs3Settings(installDir), "Users", new Dictionary<string, string> { ["active_user"] = mine }, create: true, separator: "="))
                s_log.Info($"[EmulatorUsers] RPCS3 starts as user {mine}.");
        }

        // RPCS3's rule for names: 3 to 16 letters, digits, _ or -.
        private static string Rpcs3Name(string label, string username, string id)
        {
            foreach (string candidate in new[] { label, username })
            {
                string name = Regex.Replace(candidate ?? "", "[^A-Za-z0-9_-]", "");
                if (name.Length > 16)
                    name = name.Substring(0, 16);
                if (name.Length >= 3)
                    return name;
            }
            return "User" + id.TrimStart('0');
        }

        private static bool HasData(string folder)
        {
            try
            {
                return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
            }
            catch
            {
                return false;
            }
        }

        // ---- Xenia Canary --------------------------------------------------------------------------

        private static string XeniaRoot(string installDir) => Saves.Handlers.EmulatorActiveUser.XeniaRoot(installDir);

        private static string XeniaConfig(string installDir) => Path.Combine(XeniaRoot(installDir), "xenia-canary.config.toml");

        internal static string XeniaSignedIn(string installDir) => Saves.Handlers.EmulatorActiveUser.XeniaSignedIn(installDir);

        private void PrepareXenia(string installDir, Links links, string label, string username)
        {
            string content = Path.Combine(XeniaRoot(installDir), "content");
            Directory.CreateDirectory(content);

            // Every profile there, with its gamertag (from its account file).
            var profiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryInfo dir in new DirectoryInfo(content).GetDirectories().Where(d => d.Name.Length == 16 && d.Name.All(Uri.IsHexDigit)))
            {
                string account = Path.Combine(dir.FullName, Dashboard, ProfileType, dir.Name, "Account");
                if (File.Exists(account))
                    profiles[dir.Name.ToUpperInvariant()] = ReadGamertag(File.ReadAllBytes(account)) ?? "";
            }
            var taken = new HashSet<string>(Others().Select(o => o.XeniaXuid).Where(x => x != null), StringComparer.OrdinalIgnoreCase);

            string mine = links.XeniaXuid != null && profiles.ContainsKey(links.XeniaXuid) ? links.XeniaXuid : null;

            // One whose gamertag is the account's name.
            if (mine == null)
                mine = profiles.Where(p => !taken.Contains(p.Key) && (SameName(p.Value, label) || SameName(p.Value, username))).Select(p => p.Key).FirstOrDefault();

            // One from before, if the player says it is theirs.
            if (mine == null)
            {
                foreach (KeyValuePair<string, string> profile in profiles.Where(p => !taken.Contains(p.Key) && !links.Declined.Contains("xenia:" + p.Key)))
                {
                    if (Ask($"Xenia on this computer has a profile \"{(profile.Value.Length > 0 ? profile.Value : profile.Key)}\" from before each " +
                            "account had its own.\n\nIs it yours? If not, you get a profile of your own."))
                    {
                        mine = profile.Key;
                        break;
                    }
                    links.Declined.Add("xenia:" + profile.Key);
                }
            }

            // Otherwise a new profile, as Xenia makes them: the folder and its account file.
            if (mine == null)
            {
                string gamertag = Gamertag(label, username);
                ulong xuid;
                var random = new Random();
                do
                {
                    xuid = (0xE03UL << 52) + (ulong)random.Next(0, int.MaxValue);
                }
                while (profiles.ContainsKey(xuid.ToString("X16")));
                mine = xuid.ToString("X16");
                string package = Path.Combine(content, mine, Dashboard, ProfileType, mine);
                Directory.CreateDirectory(package);
                File.WriteAllBytes(Path.Combine(package, "Account"), EncryptAccount(gamertag));
                s_log.Info($"[EmulatorUsers] Made Xenia profile {mine} (\"{gamertag}\") for {label}.");
            }

            links.XeniaXuid = mine;
            if (SettingsFile.SetIni(XeniaConfig(installDir), "Profiles",
                    new Dictionary<string, string> { ["logged_profile_slot_0_xuid"] = "\"" + mine + "\"" }, create: true))
                s_log.Info($"[EmulatorUsers] Xenia signs in profile {mine}.");
        }

        // A gamertag: up to 15 characters, letters, digits and spaces.
        private static string Gamertag(string label, string username)
        {
            foreach (string candidate in new[] { label, username })
            {
                string tag = Regex.Replace(candidate ?? "", "[^A-Za-z0-9 ]", "").Trim();
                if (tag.Length > 15)
                    tag = tag.Substring(0, 15).Trim();
                if (tag.Length > 0)
                    return tag;
            }
            return "Player";
        }

        /// <summary>The gamertag in an Xbox account file, or null when it cannot be read.</summary>
        internal static string ReadGamertag(byte[] file)
        {
            if (file == null || file.Length < 0x10 + 8 + AccountInfoSize)
                return null;
            foreach (byte[] key in new[] { XeKey19, XeKey19Devkit })
            {
                byte[] hash = file.Take(0x10).ToArray();
                byte[] rc4Key = Hmac(key, hash).Take(0x10).ToArray();
                byte[] plain = Rc4(rc4Key, file.Skip(0x10).Take(8 + AccountInfoSize).ToArray());
                if (!Hmac(key, plain).Take(0x10).SequenceEqual(hash))
                    continue;
                // The account info follows an 8-byte confounder; the gamertag is at 8 in it, UTF-16BE.
                var tag = new StringBuilder();
                for (int i = 0; i < 16; i++)
                {
                    int at = 8 + 8 + i * 2;
                    char c = (char)((plain[at] << 8) | plain[at + 1]);
                    if (c == '\0')
                        break;
                    tag.Append(c);
                }
                return tag.ToString();
            }
            return null;
        }

        /// <summary>A new account file holding just a gamertag, as Xenia's CreateAccount writes it.</summary>
        internal static byte[] EncryptAccount(string gamertag)
        {
            byte[] plain = new byte[8 + AccountInfoSize];
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] confounder = new byte[8];
                rng.GetBytes(confounder);
                Array.Copy(confounder, plain, 8);
            }
            byte[] tag = Encoding.BigEndianUnicode.GetBytes(gamertag.Length > 15 ? gamertag.Substring(0, 15) : gamertag);
            Array.Copy(tag, 0, plain, 8 + 8, tag.Length);

            byte[] hash = Hmac(XeKey19, plain).Take(0x10).ToArray();
            byte[] rc4Key = Hmac(XeKey19, hash).Take(0x10).ToArray();
            byte[] encrypted = Rc4(rc4Key, plain);
            return hash.Concat(encrypted).ToArray();
        }

        private static byte[] Hmac(byte[] key, byte[] data)
        {
            using (var hmac = new HMACSHA1(key))
                return hmac.ComputeHash(data);
        }

        private static byte[] Rc4(byte[] key, byte[] data)
        {
            byte[] s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                byte t = s[i]; s[i] = s[j]; s[j] = t;
            }
            byte[] output = new byte[data.Length];
            for (int n = 0, i = 0, j = 0; n < data.Length; n++)
            {
                i = (i + 1) & 0xFF;
                j = (j + s[i]) & 0xFF;
                byte t = s[i]; s[i] = s[j]; s[j] = t;
                output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
            }
            return output;
        }
    }
}
