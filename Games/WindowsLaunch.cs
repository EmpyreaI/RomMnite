using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Games
{
    /// <summary>
    /// Which of a Windows game's programs starts it. A .com or a .bat in its folder is a choice
    /// made when it was packed and wins outright (a .com is a renamed .exe, which Windows runs
    /// the same). Otherwise every .exe that is not an installer, an uninstaller, a redistributable
    /// or a helper is weighed:
    ///
    ///   +40  what Steam starts it with (the Ludusavi manifest's launch entry) ...
    ///   +15  ... when that is a launcher (Peace Walker's launcher\launcher.exe, Cyberpunk's
    ///        redprelauncher.exe): a way in, not the game
    ///   +30  beside steam_api.dll / steam_api64.dll (a Steam game's own program sits there)
    ///   +20  its product name or description (from the file itself) is the game's name
    ///   +15  its file name is the game's name
    ///   -15  its name says launcher (launcher, prelauncher, launch)
    ///    +6  64-bit, on 64-bit Windows
    ///   +10  in proportion to its size, the largest the most
    ///    -4  for each folder down from the game's own
    ///
    /// The best is sure when no other comes within 20 and it is not a launcher; otherwise the
    /// player is asked once, at the first Play (WindowsLaunchPicker).
    /// </summary>
    internal static class WindowsLaunch
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        internal const int SureGap = 20;

        // Not the game: installers, redistributables, crash reporters, updaters, settings tools.
        internal static readonly string[] NotTheGame =
        {
            "unins", "setup", "install", "vcredist", "vc_redist", "dxsetup", "dxwebsetup", "directx", "dotnet", "ndp",
            "oalinst", "physx", "crash", "report", "helper", "launcherpatcher", "updater", "update", "config", "settings",
            "ue4prereq", "ueprereq", "easyanticheat_setup", "be_service", "cefsharp", "quicksfv", "dowser",
        };

        // Not the game wherever in the name (seen in real games' folders: Cyberpunk's
        // REDEngineErrorReporter, Unity's UnityCrashHandler64, Need for Speed's safemode_inst and
        // shell_inst, Cyberpunk's redMod).
        internal static readonly string[] NotTheGameAnywhere =
        {
            "reporter", "errorreport", "crashreport", "crashhandler", "crashpad", "safemode", "_inst", "redmod", "uninst",
        };

        private static readonly string[] NotTheGameFolders = { "_CommonRedist", "Redist", "DirectX", "__Installer", "Support", "_Redist", "vcredist" };

        internal sealed class Candidate
        {
            public string Path;
            public string Relative;
            public int Score;
            public long Size;
            public bool? Is64;           // null: not a program file (a .bat)
            public string Product;
            public bool Launcher;        // its name says launcher
            public bool SteamLaunches;   // the manifest's launch entry
            public List<string> Reasons = new List<string>();
            public override string ToString() => $"{Relative} ({Score}: {string.Join(", ", Reasons)})";
        }

        internal sealed class Ranking
        {
            public List<Candidate> Candidates = new List<Candidate>();
            /// <summary>A .com or .bat: chosen when packed.</summary>
            public string Chosen;
            public bool Sure;
            public Candidate Best => Candidates.FirstOrDefault();
            public string BestPath => Chosen ?? Best?.Path;
        }

        /// <param name="folder">The game's own folder.</param>
        /// <param name="title">The game's name (as the library has it).</param>
        /// <param name="chosenTypes">The extensions a choice is marked with (the Windows profile's: com, bat).</param>
        /// <param name="manifestTitle">The game's name in the Ludusavi manifest, when set by hand; else the title.</param>
        /// <param name="comAsPrograms">.com files weighed as programs too, not taken as chosen (the check of the ranking against the choices made by hand).</param>
        public static Ranking Rank(string folder, string title, IList<string> chosenTypes, string manifestTitle = null, bool comAsPrograms = false)
        {
            var ranking = new Ranking();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return ranking;

            List<string> files;
            try
            {
                files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Launch] Could not look through {folder}: {ex.Message}");
                return ranking;
            }

            // Chosen when packed: a .com or a .bat, the one nearest the top.
            var kinds = (chosenTypes != null && chosenTypes.Count > 0 ? chosenTypes : new List<string> { "com", "bat" })
                .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t.TrimStart('.'), "exe", StringComparison.OrdinalIgnoreCase))
                .Select(t => "." + t.TrimStart('.')).ToList();
            ranking.Chosen = files
                .Where(f => kinds.Any(k => string.Equals(Path.GetExtension(f), k, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => Depth(folder, f))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (ranking.Chosen != null)
            {
                ranking.Sure = true;
                return ranking;
            }

            List<string> exes = files.Where(f => (string.Equals(Path.GetExtension(f), ".exe", StringComparison.OrdinalIgnoreCase)
                                                  || (comAsPrograms && string.Equals(Path.GetExtension(f), ".com", StringComparison.OrdinalIgnoreCase)))
                                                 && !Excluded(folder, f)).ToList();
            if (exes.Count == 0)
                return ranking;

            HashSet<string> steamLaunches = ManifestLaunches(folder, manifestTitle ?? title);
            string wanted = Plain(title);
            string folderName = Plain(Path.GetFileName(folder.TrimEnd('\\', '/')));
            long largest = 1;
            foreach (string exe in exes)
            {
                var c = new Candidate { Path = exe, Relative = exe.Substring(folder.TrimEnd('\\', '/').Length).TrimStart('\\', '/') };
                try
                {
                    c.Size = new FileInfo(exe).Length;
                }
                catch
                {
                }
                largest = Math.Max(largest, c.Size);
                ranking.Candidates.Add(c);
            }

            foreach (Candidate c in ranking.Candidates)
            {
                string name = Path.GetFileNameWithoutExtension(c.Path);
                string plainName = Plain(name);
                c.Launcher = name.IndexOf("launch", StringComparison.OrdinalIgnoreCase) >= 0;
                c.SteamLaunches = steamLaunches.Contains(c.Path);
                if (c.SteamLaunches)
                    Add(c, c.Launcher ? 15 : 40, c.Launcher ? "Steam starts it (a launcher)" : "Steam starts it");
                else if (c.Launcher)
                    Add(c, -15, "a launcher");

                string dir = Path.GetDirectoryName(c.Path) ?? "";
                if (File.Exists(Path.Combine(dir, "steam_api64.dll")) || File.Exists(Path.Combine(dir, "steam_api.dll")))
                    Add(c, 30, "beside steam_api");

                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(c.Path);
                    c.Product = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim()
                        : !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim() : null;
                    if (Names(wanted, Plain(info.ProductName)) || Names(wanted, Plain(info.FileDescription)))
                        Add(c, 20, "named for the game inside");
                }
                catch
                {
                }

                if (Names(wanted, plainName) || (folderName.Length > 0 && Names(folderName, plainName)))
                    Add(c, 15, "file named for the game");

                c.Is64 = Is64Bit(c.Path);
                if (c.Is64 == true && Environment.Is64BitOperatingSystem)
                    Add(c, 6, "64-bit");

                int bySize = (int)Math.Round(10.0 * c.Size / largest);
                if (bySize > 0)
                    Add(c, bySize, "size");

                int depth = Depth(folder, c.Path);
                if (depth > 0)
                    Add(c, -4 * depth, depth + " folder" + (depth == 1 ? "" : "s") + " down");
            }

            ranking.Candidates = ranking.Candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => Depth(folder, c.Path))
                .ThenByDescending(c => c.Size)
                .ToList();
            Candidate best = ranking.Candidates[0];
            Candidate next = ranking.Candidates.Count > 1 ? ranking.Candidates[1] : null;
            ranking.Sure = next == null || (best.Score - next.Score >= SureGap && !best.Launcher);
            return ranking;
        }

        private static void Add(Candidate c, int points, string why)
        {
            c.Score += points;
            c.Reasons.Add((points >= 0 ? "+" : "") + points + " " + why);
        }

        private static bool Excluded(string folder, string file)
        {
            string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (NotTheGame.Any(n => name.StartsWith(n) || name.Contains("_" + n) || name.Contains("-" + n) || name.Contains(" " + n)))
                return true;
            if (NotTheGameAnywhere.Any(n => name.Contains(n)) || name.EndsWith("inst"))
                return true;
            string relative = file.Substring(Math.Min(file.Length, folder.TrimEnd('\\', '/').Length));
            return relative.Split('\\', '/').Any(part => NotTheGameFolders.Any(n => part.Equals(n, StringComparison.OrdinalIgnoreCase)));
        }

        internal static int Depth(string root, string file) =>
            file.Substring(Math.Min(file.Length, root.TrimEnd('\\', '/').Length)).Count(c => c == '\\' || c == '/') - 1;

        // Letters and digits alone, lower case ("METAL GEAR SOLID: PEACE WALKER" -> "metalgearsolidpeacewalker").
        internal static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var plain = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                    plain.Append(char.ToLowerInvariant(ch));
            }
            return plain.ToString();
        }

        // One names the other: equal, or one holds the other (at least 4 letters of it).
        internal static bool Names(string a, string b)
        {
            if (a.Length < 4 || b.Length < 4)
                return a.Length > 0 && a == b;
            return a == b || a.Contains(b) || b.Contains(a);
        }

        // A PE file built for x64 (its machine: 0x8664) or ARM64 (0xAA64); null when not a PE file.
        internal static bool? Is64Bit(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
                        return null;
                    stream.Position = 0x3C;
                    int pe = reader.ReadInt32();
                    if (pe <= 0 || pe + 6 > stream.Length)
                        return null;
                    stream.Position = pe;
                    if (reader.ReadUInt32() != 0x00004550)
                        return null;
                    ushort machine = reader.ReadUInt16();
                    return machine == 0x8664 || machine == 0xAA64;
                }
            }
            catch
            {
                return null;
            }
        }

        // The programs Steam starts the game with, by the Ludusavi manifest, found in its folder
        // (the folder itself or one a level or two below, where the manifest's <base> is).
        private static HashSet<string> ManifestLaunches(string folder, string title)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string manifest = Saves.LudusaviManifest.PathFor(Saves.Handlers.LudusaviSaveHandler.FindLudusavi());
                Saves.LudusaviManifest.Entry entry = manifest == null ? null : Saves.LudusaviManifest.For(manifest, title);
                if (entry == null || entry.LaunchPaths.Count == 0)
                    return found;
                var bases = new List<string> { folder };
                foreach (string sub in Directory.GetDirectories(folder))
                {
                    bases.Add(sub);
                    bases.AddRange(Directory.GetDirectories(sub));
                }
                foreach (string launch in entry.LaunchPaths)
                {
                    string relative = launch.Replace('/', Path.DirectorySeparatorChar);
                    if (relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || relative.Contains("<"))
                        continue;
                    foreach (string root in bases)
                    {
                        string full = Path.Combine(root, relative);
                        if (File.Exists(full))
                        {
                            found.Add(Path.GetFullPath(full));
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Launch] The Ludusavi manifest could not be read for {title}: {ex.Message}");
            }
            return found;
        }
    }
}
