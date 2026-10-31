using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RomM.Saves
{
    /// <summary>
    /// The saves Ludusavi's manifest puts inside a game's own folder ("&lt;base&gt;/..."), found here
    /// rather than by Ludusavi. Ludusavi only knows a game's folder when it sits in a store's
    /// library it knows (a Steam library's steamapps\common\&lt;installDir&gt;, say); a game RomMnite
    /// installed is in RomMnite's own folder, so for those Ludusavi finds nothing, though tens of
    /// thousands of the manifest's entries are such saves (Metal Gear Solid: Peace Walker -
    /// Master Collection Version keeps its in &lt;base&gt;/mgspw_savedata_win/&lt;storeUserId&gt;).
    ///
    /// The manifest is the one Ludusavi downloads and keeps up to date beside its settings
    /// (%APPDATA%\ludusavi\manifest.yaml, or beside ludusavi.exe when that is portable). Only the
    /// game's own entry is read, line by line: a top-level key (the title, quoted when it needs
    /// to be), its "files:" map, and each path's "when" conditions (a path only for Linux or Mac
    /// is left out). Placeholders other than &lt;base&gt; (&lt;storeUserId&gt; and the like) and
    /// the manifest's own * match anything.
    /// </summary>
    internal static class LudusaviManifest
    {
        /// <summary>The manifest beside the Ludusavi that runs, or null when there is none yet.</summary>
        public static string PathFor(string ludusaviExe)
        {
            var candidates = new List<string>();
            string beside = string.IsNullOrEmpty(ludusaviExe) ? null : Path.GetDirectoryName(ludusaviExe);
            if (beside != null && File.Exists(Path.Combine(beside, "ludusavi.portable")))
                candidates.Add(Path.Combine(beside, "manifest.yaml"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ludusavi", "manifest.yaml"));
            return candidates.FirstOrDefault(File.Exists);
        }

        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, KeyValuePair<DateTime, Entry>> s_cache =
            new Dictionary<string, KeyValuePair<DateTime, Entry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A game's install-folder saves, as the manifest has them.</summary>
        public sealed class Entry
        {
            /// <summary>Each path below &lt;base&gt; ('/'-separated, placeholders kept).</summary>
            public List<string> BasePaths = new List<string>();
            /// <summary>The folder names a store installs it under (installDir).</summary>
            public List<string> InstallDirs = new List<string>();
            /// <summary>
            /// What a store starts it with, for Windows ("launch:", '/'-separated below &lt;base&gt;):
            /// Steam's launch options, so at times a launcher rather than the game itself.
            /// </summary>
            public List<string> LaunchPaths = new List<string>();
        }

        /// <summary>The game's entry, or null when the manifest has no such title. Kept while the file is unchanged.</summary>
        public static Entry For(string manifest, string title)
        {
            if (string.IsNullOrEmpty(manifest) || string.IsNullOrEmpty(title) || !File.Exists(manifest))
                return null;
            DateTime stamp = File.GetLastWriteTimeUtc(manifest);
            string key = manifest + "|" + title;
            lock (s_lock)
            {
                if (s_cache.TryGetValue(key, out var kept) && kept.Key == stamp)
                    return kept.Value;
            }
            // The title as written, else in any case (the library has "METAL GEAR SOLID: Peace
            // Walker - ...", the manifest an entry in each case, which may differ: the exact one first).
            Entry entry = Read(File.ReadLines(manifest), title) ?? Read(File.ReadLines(manifest), title, ignoreCase: true);
            lock (s_lock)
                s_cache[key] = new KeyValuePair<DateTime, Entry>(stamp, entry);
            return entry;
        }

        /// <summary>The entry from the manifest's lines (separate for testing).</summary>
        internal static Entry Read(IEnumerable<string> lines, string title, bool ignoreCase = false)
        {
            Entry entry = null;
            string section = null;       // the entry's second-level key: files, installDir ...
            string path = null;          // the files: path being read
            List<string> pathLines = null;
            bool launch = false;         // the path being read is a launch: one
            Action endPath = () =>
            {
                if (entry != null && path != null && path.StartsWith("<base>/", StringComparison.Ordinal))
                {
                    if (launch)
                    {
                        // Its conditions as "- os: windows" items: none naming a system, or one naming Windows.
                        bool names = pathLines.Any(l => l.Contains("os: "));
                        if (!names || pathLines.Any(l => l.Contains("os: windows")))
                            entry.LaunchPaths.Add(path.Substring("<base>/".Length));
                    }
                    else if (ForWindows(pathLines))
                    {
                        entry.BasePaths.Add(path.Substring("<base>/".Length));
                    }
                }
                path = null;
                pathLines = null;
            };

            foreach (string line in lines)
            {
                if (line.Length == 0)
                    continue;
                int indent = line.Length - line.TrimStart(' ').Length;
                if (indent == 0)
                {
                    if (entry != null)
                        break;   // the next game
                    if (string.Equals(Key(line), title, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        entry = new Entry();
                    continue;
                }
                if (entry == null)
                    continue;
                if (indent == 2)
                {
                    endPath();
                    section = Key(line);
                    continue;
                }
                if (indent == 4)
                {
                    endPath();
                    if (section == "files" || section == "launch")
                    {
                        path = Key(line);
                        pathLines = new List<string>();
                        launch = section == "launch";
                    }
                    else if (section == "installDir")
                    {
                        string name = Key(line);
                        if (!string.IsNullOrEmpty(name))
                            entry.InstallDirs.Add(name);
                    }
                    continue;
                }
                pathLines?.Add(line.Trim());
            }
            endPath();
            return entry;
        }

        // A path's conditions allow Windows: none, or one of its "when" items for Windows or for
        // no particular system.
        private static bool ForWindows(List<string> conditions)
        {
            if (conditions == null)
                return true;
            int at = conditions.IndexOf("when:");
            if (at < 0)
                return true;
            // Items begin "- "; each may name an os and a store.
            var items = new List<List<string>>();
            for (int i = at + 1; i < conditions.Count; i++)
            {
                string c = conditions[i];
                if (c.StartsWith("- ", StringComparison.Ordinal))
                {
                    items.Add(new List<string>());
                    c = c.Substring(2).Trim();
                }
                else if (!c.Contains(":") || c.EndsWith(":", StringComparison.Ordinal) && !c.StartsWith("os", StringComparison.Ordinal) && !c.StartsWith("store", StringComparison.Ordinal))
                {
                    break;   // the next key (tags:)
                }
                if (items.Count > 0)
                    items[items.Count - 1].Add(c);
            }
            if (items.Count == 0)
                return true;
            return items.Any(item => !item.Any(c => c.StartsWith("os:", StringComparison.Ordinal)) || item.Contains("os: windows"));
        }

        // A YAML map key: "Title": or Title: (a quoted key may hold \" and \\).
        private static string Key(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("\"", StringComparison.Ordinal))
            {
                var key = new System.Text.StringBuilder();
                for (int i = 1; i < t.Length; i++)
                {
                    if (t[i] == '\\' && i + 1 < t.Length)
                    {
                        key.Append(t[++i]);
                        continue;
                    }
                    if (t[i] == '"')
                        return key.ToString();
                    key.Append(t[i]);
                }
                return null;
            }
            int colon = t.IndexOf(':');
            return colon < 0 ? null : t.Substring(0, colon).Trim();
        }

        // ---- On disk -----------------------------------------------------------------------------

        /// <summary>
        /// Which folder is the game's &lt;base&gt;: of the install folder and the folders a little below
        /// it, the one where a save path's first fixed part is already there, else one named as a
        /// store installs it, else the install folder.
        /// </summary>
        public static string BaseFolder(string installDir, Entry entry)
        {
            if (entry == null || string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return null;
            var candidates = new List<string> { installDir };
            try
            {
                foreach (string sub in Directory.GetDirectories(installDir))
                {
                    candidates.Add(sub);
                    candidates.AddRange(Directory.GetDirectories(sub));
                }
            }
            catch
            {
                // Not readable: the install folder alone.
            }
            var firsts = entry.BasePaths.Select(p => p.Split('/')[0]).Where(f => f.Length > 0 && !IsPattern(f)).Distinct().ToList();
            string withSaves = candidates.FirstOrDefault(c => firsts.Any(f => Directory.Exists(Path.Combine(c, f)) || File.Exists(Path.Combine(c, f))));
            if (withSaves != null)
                return withSaves;
            string named = candidates.FirstOrDefault(c => entry.InstallDirs.Any(d => string.Equals(Path.GetFileName(c), d, StringComparison.OrdinalIgnoreCase)));
            return named ?? installDir;
        }

        /// <summary>Every file the entry's paths match under <paramref name="baseFolder"/>, as paths relative to it.</summary>
        public static List<string> Files(string baseFolder, Entry entry)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entry == null || string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder))
                return found.ToList();
            foreach (string pattern in entry.BasePaths)
            {
                try
                {
                    foreach (string match in Match(baseFolder, pattern.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries), 0))
                    {
                        if (File.Exists(match))
                            found.Add(match);
                        else if (Directory.Exists(match))
                            foreach (string file in Directory.EnumerateFiles(match, "*", SearchOption.AllDirectories))
                                found.Add(file);
                    }
                }
                catch
                {
                    // Not readable: passed over.
                }
            }
            string root = baseFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return found.Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Substring(root.Length))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsPattern(string segment) => segment.IndexOfAny(new[] { '*', '?', '<', '[' }) >= 0;

        private static IEnumerable<string> Match(string folder, string[] segments, int at)
        {
            if (at == segments.Length)
            {
                yield return folder;
                yield break;
            }
            string segment = segments[at];
            if (segment == "**")
            {
                // Any depth: this folder and every one below it.
                foreach (string deeper in new[] { folder }.Concat(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)))
                    foreach (string match in Match(deeper, segments, at + 1))
                        yield return match;
                yield break;
            }
            if (!IsPattern(segment))
            {
                string next = Path.Combine(folder, segment);
                if (Directory.Exists(next) || File.Exists(next))
                    foreach (string match in Match(next, segments, at + 1))
                        yield return match;
                yield break;
            }
            var regex = new Regex("^" + Regex.Escape(segment)
                .Replace(@"\*", ".*").Replace(@"\?", ".").Replace(@"\[", "[").Replace(@"\]", "]") + "$", RegexOptions.IgnoreCase);
            // A placeholder (<storeUserId> ...) matches any one name.
            regex = new Regex(Regex.Replace(regex.ToString(), "<[^>]*>", ".*"), RegexOptions.IgnoreCase);
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (!regex.IsMatch(Path.GetFileName(entry)))
                    continue;
                if (at + 1 < segments.Length && !Directory.Exists(entry))
                    continue;
                foreach (string match in Match(entry, segments, at + 1))
                    yield return match;
            }
        }
    }
}
