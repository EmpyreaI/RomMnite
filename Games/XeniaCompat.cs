using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using SharpCompress.Archives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>How an Xbox 360 game runs in Xenia Canary, as its compatibility list has it.</summary>
    public sealed class XeniaReport
    {
        public string TitleId;          // 8 hex digits, upper case
        public string Title;            // as the list names it
        public string State;            // Playable, Gameplay, Loads, Unplayable, Unknown
        public List<string> Labels = new List<string>();
        public string Url;              // the game's report
        public List<XeniaPatch> Patches = new List<XeniaPatch>();
    }

    /// <summary>One of the community's patches for a game (xenia-canary/game-patches).</summary>
    public sealed class XeniaPatch
    {
        public string Name;
        public string Description;
        public string Author;
        /// <summary>Works around a problem, by its name or description (else an enhancement: 60 FPS, widescreen...).</summary>
        public bool IsFix;
        /// <summary>The patch files (one per version of the game) holding it.</summary>
        public List<string> Files = new List<string>();
    }

    /// <summary>A setting a game needs in Xenia, from its report's labels.</summary>
    public sealed class XeniaSetting
    {
        public string Section;
        public string Key;
        public string Value;            // as TOML writes it
        public string Why;
    }

    /// <summary>
    /// Xenia Canary's game compatibility list and the community's game patches, for the library's
    /// Emulation section (EmulationStatusControl):
    ///
    ///   the list: xenia-canary/game-compatibility's reports, published by that repository as
    ///   one file (its "game-compatibility" release, rebuilt every 8 hours): title id, name, state
    ///   (Playable / Gameplay / Loads / Unplayable) and labels (graphics, audio, crash problems;
    ///   settings a game requires);
    ///   the patches: xenia-canary/game-patches, one TOML file per game version
    ///   ("&lt;title id&gt; - &lt;name&gt;.patch.toml"), every patch off until switched on.
    ///
    /// Both are fetched in the background and kept (the list a day, the patches a week). A game is
    /// found by its name, as either source spells it (roman numerals and editions aside): "Fable
    /// II" is the patches' name and the list's "Fable 2", one title id, 4D5307F1.
    ///
    /// What is done with them, in Xenia's own terms (its portable folder: Playnite\Emulators\Xenia
    /// Canary): a game's settings go in config\&lt;title id&gt;.config.toml, which Xenia reads over
    /// its own settings for that game only; its patch files go in patches\, Xenia applying those
    /// switched on to the version of the game whose executable they name.
    /// </summary>
    public sealed class XeniaCompat
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private const string ListUrl = "https://github.com/xenia-canary/game-compatibility/releases/download/game-compatibility/compatibility_data.json";
        private const string PatchesUrl = "https://codeload.github.com/xenia-canary/game-patches/zip/refs/heads/main";
        private static readonly HttpClient s_http = CreateClient();

        private readonly string _folder;
        private readonly SemaphoreSlim _loading = new SemaphoreSlim(1);
        private List<JObject> _list;
        private Dictionary<string, List<string>> _patchFiles;   // title id -> patch file paths (kept here)
        private Dictionary<string, string> _idByName;           // normalized name -> title id

        public XeniaCompat(string folder)
        {
            _folder = folder;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RomMnite");
            return client;
        }

        private string ListFile => Path.Combine(_folder, "compatibility_data.json");
        private string PatchesFolder => Path.Combine(_folder, "patches");
        private string PatchesStamp => Path.Combine(_folder, "patches.fetched");

        /// <summary>The game's report and patches, or null when the list has no such game. Off the UI thread.</summary>
        public async Task<XeniaReport> FindAsync(string gameName)
        {
            await Load().ConfigureAwait(false);
            string id = TitleIdFor(gameName);
            if (id == null)
                return null;
            JObject entry = _list?.FirstOrDefault(e => string.Equals((string)e["id"], id, StringComparison.OrdinalIgnoreCase));
            var report = new XeniaReport
            {
                TitleId = id,
                Title = (string)entry?["title"] ?? gameName,
                State = (string)entry?["state"] ?? "Unknown",
                Url = (string)entry?["url"],
                Labels = (entry?["labels"]?["others"] as JArray)?.Select(l => (string)l).Where(l => l != null).ToList() ?? new List<string>(),
            };
            if (_patchFiles != null && _patchFiles.TryGetValue(id, out List<string> files))
                report.Patches = ReadPatches(files);
            return report;
        }

        // ---- Fetching ----------------------------------------------------------------------------

        private async Task Load()
        {
            await _loading.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_folder);
                if (Older(ListFile, TimeSpan.FromDays(1)))
                    await Fetch(ListUrl, ListFile).ConfigureAwait(false);
                if (Older(PatchesStamp, TimeSpan.FromDays(7)))
                    await FetchPatches().ConfigureAwait(false);

                if (_list == null || _idByName == null)
                {
                    _list = File.Exists(ListFile)
                        ? (JArray.Parse(File.ReadAllText(ListFile)).OfType<JObject>().ToList())
                        : new List<JObject>();
                    _patchFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    _idByName = new Dictionary<string, string>();
                    foreach (JObject entry in _list)
                        Remember((string)entry["title"], (string)entry["id"]);
                    if (Directory.Exists(PatchesFolder))
                    {
                        foreach (string file in Directory.GetFiles(PatchesFolder, "*.patch.toml"))
                        {
                            Match named = Regex.Match(Path.GetFileName(file), @"^([0-9A-Fa-f]{8})\s*-\s*(.+?)(\s*\([^)]*\))*\.patch\.toml$");
                            if (!named.Success)
                                continue;
                            string id = named.Groups[1].Value.ToUpperInvariant();
                            if (!_patchFiles.TryGetValue(id, out List<string> list))
                                _patchFiles[id] = list = new List<string>();
                            list.Add(file);
                            Remember(named.Groups[2].Value, id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Info($"[Xenia] Could not load the compatibility list or patches: {ex.Message}");
                _list = _list ?? new List<JObject>();
                _idByName = _idByName ?? new Dictionary<string, string>();
            }
            finally
            {
                _loading.Release();
            }
        }

        private static bool Older(string file, TimeSpan age) => !File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > age;

        private static async Task Fetch(string url, string target)
        {
            try
            {
                byte[] bytes = await s_http.GetByteArrayAsync(url).ConfigureAwait(false);
                File.WriteAllBytes(target + ".new", bytes);
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(target + ".new", target);
                s_log.Info($"[Xenia] Fetched {Path.GetFileName(target)}.");
            }
            catch (Exception ex)
            {
                s_log.Info($"[Xenia] Could not fetch {url}: {ex.Message}");
            }
        }

        // The patches repository as one archive: its patches\ files kept, nothing else.
        private async Task FetchPatches()
        {
            string archive = Path.Combine(_folder, "game-patches.zip");
            await Fetch(PatchesUrl, archive).ConfigureAwait(false);
            if (!File.Exists(archive))
                return;
            try
            {
                string fresh = PatchesFolder + ".new";
                if (Directory.Exists(fresh))
                    Directory.Delete(fresh, true);
                Directory.CreateDirectory(fresh);
                using (var zip = ArchiveFactory.Open(archive))
                {
                    foreach (var entry in zip.Entries.Where(e => !e.IsDirectory))
                    {
                        string key = entry.Key.Replace('\\', '/');
                        string name = Path.GetFileName(key);
                        if (!key.Contains("/patches/") || !name.EndsWith(".patch.toml", StringComparison.OrdinalIgnoreCase)
                            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                            continue;
                        using (var source = entry.OpenEntryStream())
                        using (var file = File.Create(Path.Combine(fresh, name)))
                            source.CopyTo(file);
                    }
                }
                if (Directory.Exists(PatchesFolder))
                    Directory.Delete(PatchesFolder, true);
                Directory.Move(fresh, PatchesFolder);
                File.WriteAllText(PatchesStamp, DateTime.UtcNow.ToString("o"));
                _list = null;   // read again, with the new names
            }
            catch (Exception ex)
            {
                s_log.Info($"[Xenia] Could not unpack the game patches: {ex.Message}");
            }
            finally
            {
                try { File.Delete(archive); } catch { }
            }
        }

        // ---- Names -------------------------------------------------------------------------------

        private void Remember(string name, string id)
        {
            string key = Normalize(name);
            if (key.Length > 0 && !string.IsNullOrEmpty(id) && !_idByName.ContainsKey(key))
                _idByName[key] = id.ToUpperInvariant();
        }

        private string TitleIdFor(string gameName)
        {
            if (_idByName == null)
                return null;
            foreach (string candidate in new[] { Normalize(gameName), Normalize(WithoutEdition(gameName)) })
            {
                if (candidate.Length > 0 && _idByName.TryGetValue(candidate, out string id))
                    return id;
            }
            return null;
        }

        private static readonly Dictionary<string, string> Roman = new Dictionary<string, string>
        {
            ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10",
        };

        /// <summary>A name for matching: lower case, words only, roman numerals as numbers, no "the" or bracketed tags.</summary>
        internal static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";
            string text = Regex.Replace(name.ToLowerInvariant(), @"[\(\[][^\)\]]*[\)\]]", " ");
            text = text.Replace("&", " and ");
            text = Regex.Replace(text, @"[^a-z0-9]+", " ");
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => Roman.TryGetValue(w, out string number) ? number : w)
                .ToList();
            if (words.Count > 1 && words[0] == "the")
                words.RemoveAt(0);
            return string.Join(" ", words);
        }

        private static string WithoutEdition(string name) =>
            Regex.Replace(name ?? "", @"\b(game of the year|goty|platinum hits|platinum|classics|ultimate|complete|limited|collector'?s|special|deluxe)( edition)?\b", "", RegexOptions.IgnoreCase);

        // ---- Patches -----------------------------------------------------------------------------

        private static readonly Regex FixWords = new Regex(@"\b(fix|fixes|workaround|crash|hang|freeze|softlock|soft-lock|broken|bug|glitch|black screen|missing|corrupt)", RegexOptions.IgnoreCase);

        private static List<XeniaPatch> ReadPatches(List<string> files)
        {
            var patches = new List<XeniaPatch>();
            foreach (string file in files)
            {
                foreach (var block in Blocks(File.ReadAllText(file)))
                {
                    string name = Field(block, "name");
                    if (string.IsNullOrEmpty(name))
                        continue;
                    XeniaPatch patch = patches.FirstOrDefault(p => p.Name == name);
                    if (patch == null)
                    {
                        string desc = Field(block, "desc");
                        patch = new XeniaPatch
                        {
                            Name = name,
                            Description = desc,
                            Author = Field(block, "author"),
                            IsFix = FixWords.IsMatch(name) || FixWords.IsMatch(desc ?? ""),
                        };
                        patches.Add(patch);
                    }
                    patch.Files.Add(Path.GetFileName(file));
                }
            }
            return patches.OrderBy(p => p.IsFix ? 0 : 1).ToList();
        }

        // Each [[patch]] block of a patch file, as text.
        private static IEnumerable<string> Blocks(string toml)
        {
            string[] parts = Regex.Split(toml, @"^[ \t]*\[\[patch\]\][ \t]*(?=\r?$)", RegexOptions.Multiline);
            return parts.Skip(1);
        }

        private static string Field(string block, string key)
        {
            Match match = Regex.Match(block, @"^\s*" + key + @"\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline);
            return match.Success ? Regex.Unescape(match.Groups[1].Value) : null;
        }

        /// <summary>Whether a patch is switched on in Xenia's own patches folder (any of its files).</summary>
        public bool IsEnabled(string xeniaFolder, XeniaPatch patch)
        {
            foreach (string name in patch.Files)
            {
                string installed = Path.Combine(xeniaFolder, "patches", name);
                if (!File.Exists(installed))
                    continue;
                foreach (string block in Blocks(File.ReadAllText(installed)))
                {
                    if (Field(block, "name") == patch.Name && Regex.IsMatch(block, @"^\s*is_enabled\s*=\s*true", RegexOptions.Multiline))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Switches a patch on or off for the game in Xenia: its files put in Xenia's patches folder
        /// when not there yet (one per version of the game; Xenia uses the one for the version
        /// played), and the patch's is_enabled set in each. Null when done, else what went wrong.
        /// </summary>
        public string SetEnabled(string xeniaFolder, XeniaPatch patch, bool on)
        {
            try
            {
                string target = Path.Combine(xeniaFolder, "patches");
                Directory.CreateDirectory(target);
                foreach (string name in patch.Files)
                {
                    string installed = Path.Combine(target, name);
                    if (!File.Exists(installed))
                    {
                        string source = Path.Combine(PatchesFolder, name);
                        if (!File.Exists(source))
                            continue;
                        File.Copy(source, installed);
                    }
                    string text = File.ReadAllText(installed);
                    File.WriteAllText(installed, WithEnabled(text, patch.Name, on), new UTF8Encoding(false));
                }
                s_log.Info($"[Xenia] Patch \"{patch.Name}\" {(on ? "on" : "off")} ({string.Join(", ", patch.Files)}).");
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // The file with one patch's is_enabled set (added to its block when missing).
        internal static string WithEnabled(string toml, string patchName, bool on)
        {
            var matches = Regex.Matches(toml, @"^[ \t]*\[\[patch\]\][ \t]*(?=\r?$)", RegexOptions.Multiline).Cast<Match>().ToList();
            var builder = new StringBuilder(toml);
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                int start = matches[i].Index + matches[i].Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : toml.Length;
                string block = toml.Substring(start, end - start);
                if (Field(block, "name") != patchName)
                    continue;
                string value = on ? "true" : "false";
                string changed = Regex.IsMatch(block, @"^\s*is_enabled\s*=", RegexOptions.Multiline)
                    ? Regex.Replace(block, @"^(\s*is_enabled\s*=\s*)(true|false)", "${1}" + value, RegexOptions.Multiline)
                    : Regex.Replace(block, @"^(\s*name\s*=\s*""(?:[^""\\]|\\.)*""[^\r\n]*)", "$1" + "\n    is_enabled = " + value, RegexOptions.Multiline);
                builder.Remove(start, end - start).Insert(start, changed);
            }
            return builder.ToString();
        }

        // ---- Settings ----------------------------------------------------------------------------

        /// <summary>The settings a report's labels say the game needs (cvar names from Xenia Canary's source).</summary>
        public static List<XeniaSetting> Needed(XeniaReport report)
        {
            var needed = new List<XeniaSetting>();
            foreach (string label in report?.Labels ?? new List<string>())
            {
                switch (label)
                {
                    case "requires_render_target_rov_fsi":
                        needed.Add(new XeniaSetting { Section = "GPU", Key = "render_target_path_d3d12", Value = "\"rov\"", Why = "Accurate render targets (Direct3D 12)" });
                        needed.Add(new XeniaSetting { Section = "GPU", Key = "render_target_path_vulkan", Value = "\"fsi\"", Why = "Accurate render targets (Vulkan)" });
                        break;
                    case "requires_clear_memory_page_state_true":
                        needed.Add(new XeniaSetting { Section = "GPU", Key = "clear_memory_page_state", Value = "true", Why = "GPU-written memory refreshed (missing models)" });
                        break;
                    case "requires_protect_zero_false":
                        needed.Add(new XeniaSetting { Section = "Memory", Key = "protect_zero", Value = "false", Why = "Zero page left unprotected" });
                        break;
                    case "requires_scribble_heap":
                        needed.Add(new XeniaSetting { Section = "Memory", Key = "scribble_heap", Value = "true", Why = "Heap memory filled on allocation" });
                        break;
                    case "requires_mount_cache":
                        needed.Add(new XeniaSetting { Section = "Storage", Key = "mount_cache", Value = "true", Why = "Cache partition mounted" });
                        break;
                    case "vsync-off-speedup":
                        needed.Add(new XeniaSetting { Section = "GPU", Key = "vsync", Value = "false", Why = "VSync off (runs faster)" });
                        break;
                }
            }
            return needed;
        }

        private static string GameConfig(string xeniaFolder, string titleId) => Path.Combine(xeniaFolder, "config", titleId.ToUpperInvariant() + ".config.toml");

        /// <summary>Whether the game's own settings file already has all of these, as they are.</summary>
        public static bool Applied(string xeniaFolder, XeniaReport report, List<XeniaSetting> settings)
        {
            string file = GameConfig(xeniaFolder, report.TitleId);
            if (!File.Exists(file) || settings.Count == 0)
                return false;
            string text = File.ReadAllText(file);
            return settings.All(s => Regex.IsMatch(text, @"^\s*" + Regex.Escape(s.Key) + @"\s*=\s*" + Regex.Escape(s.Value) + @"\s*(#.*)?$", RegexOptions.Multiline));
        }

        /// <summary>
        /// Writes the settings into the game's own settings file (config\&lt;title id&gt;.config.toml),
        /// keeping anything else already in it. Null when done, else what went wrong.
        /// </summary>
        public static string Apply(string xeniaFolder, XeniaReport report, List<XeniaSetting> settings)
        {
            try
            {
                string file = GameConfig(xeniaFolder, report.TitleId);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string text = File.Exists(file) ? File.ReadAllText(file) : $"# {report.Title}: settings for this game only, set by RomMnite from Xenia's compatibility list.\n";
                foreach (var group in settings.GroupBy(s => s.Section))
                {
                    foreach (XeniaSetting setting in group)
                        text = Set(text, group.Key, setting.Key, setting.Value);
                }
                File.WriteAllText(file, text, new UTF8Encoding(false));
                s_log.Info($"[Xenia] {report.Title}: {string.Join(", ", settings.Select(s => s.Key + " = " + s.Value))} in {file}.");
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // One key set in a [section] of a small TOML file: replaced where it is (its comment
        // kept), else added under the section's header, else the section added at the end.
        internal static string Set(string toml, string section, string key, string value)
        {
            string nl = "\n";
            var sections = Regex.Matches(toml, @"^[ \t]*\[([^\[\]\r\n]+)\][ \t]*(?=\r?$)", RegexOptions.Multiline).Cast<Match>().ToList();
            Match head = sections.FirstOrDefault(m => string.Equals(m.Groups[1].Value.Trim(), section, StringComparison.OrdinalIgnoreCase));
            if (head == null)
                return toml.TrimEnd() + nl + nl + "[" + section + "]" + nl + key + " = " + value + nl;
            int start = head.Index + head.Length;
            Match next = sections.FirstOrDefault(m => m.Index > head.Index);
            int end = next?.Index ?? toml.Length;
            string body = toml.Substring(start, end - start);
            var line = new Regex(@"^([ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*)[^\r\n#]*?([ \t]*(#[^\r\n]*)?)(?=\r?$)", RegexOptions.Multiline);
            body = line.IsMatch(body)
                ? line.Replace(body, m => m.Groups[1].Value + value + m.Groups[2].Value, 1)
                : nl + key + " = " + value + body;
            return toml.Substring(0, start) + body + toml.Substring(end);
        }
    }
}
