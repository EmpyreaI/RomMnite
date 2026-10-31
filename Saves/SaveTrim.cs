using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RomM.Saves
{
    /// <summary>
    /// A save's archive made small enough for the server's upload limit by leaving out its
    /// oldest save slots: for games that keep every save ever made (Cyberpunk 2077's hundreds of
    /// megabytes), so the newest saves still reach RomM. The same for every game, with nothing
    /// known about any one of them:
    ///
    ///   - Slots are told by their shape. Three or more sibling folders whose files are mostly
    ///     named alike (each Cyberpunk save: sav.dat, metadata.9.json, screenshot.png) are a
    ///     group of folder slots; three or more sibling files with one extension (outside such a
    ///     folder) are a group of file slots. A group counts only when it is a good part of the
    ///     archive (a tenth, or 10 MB), so a few small settings files are never taken for slots.
    ///   - A slot's age is its newest file's. The oldest go first, across every group, until the
    ///     archive fits; the newest of each group always stays. Everything that is not a slot
    ///     (settings, profiles, an index of saves) stays.
    ///   - Only the copy for the server is trimmed: the saves on this computer are never touched.
    ///   - Ludusavi's backup lists its files (mapping.yaml); the files left out are taken off the
    ///     list too, so restoring it elsewhere does not look for them.
    ///
    /// When even the newest slots do not fit (one save bigger than the limit), there is no
    /// trimming that helps: <see cref="Trim"/> says so.
    /// </summary>
    internal static class SaveTrim
    {
        /// <summary>One file of the archive, as far as trimming needs it.</summary>
        internal sealed class Item
        {
            public string Name;          // '/'-separated
            public long Size;            // as stored (compressed)
            public DateTime Modified;
        }

        /// <summary>A save slot: its entries, its age and its size.</summary>
        internal sealed class Slot
        {
            public string Group;         // the folder holding the group's slots, and their kind
            public string Path;          // the slot's folder, or file
            public List<Item> Items = new List<Item>();
            public DateTime Newest => Items.Max(i => i.Modified);
            public long Size => Items.Sum(i => i.Size);
        }

        private const long SignificantGroup = 10L << 20;

        /// <summary>The upload limit RomM states when it turns a save away (HTTP 413), or null.</summary>
        public static long? LimitFrom(string answer)
        {
            Match match = Regex.Match(answer ?? "", @"maximum allowed size of (\d+) bytes", RegexOptions.IgnoreCase);
            return match.Success && long.TryParse(match.Groups[1].Value, out long limit) && limit > 0 ? limit : (long?)null;
        }

        /// <summary>The slots in an archive's files, grouped as described above.</summary>
        internal static List<Slot> FindSlots(IList<Item> items)
        {
            var slots = new List<Slot>();
            long total = items.Sum(i => i.Size);
            long significant = Math.Min(SignificantGroup, Math.Max(1, total / 10));

            // Every folder, and what is directly in it.
            var folders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // folder -> child folders
            foreach (Item item in items)
            {
                string[] parts = item.Name.Split('/');
                string parent = "";
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string folder = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                    if (!folders.TryGetValue(parent, out var children))
                        folders[parent] = children = new List<string>();
                    if (!children.Contains(folder, StringComparer.OrdinalIgnoreCase))
                        children.Add(folder);
                    parent = folder;
                }
            }

            // Folder slots: sibling folders mostly holding the same file names.
            var taken = new List<string>();   // slot folders: nothing inside them is looked at again
            foreach (var pair in folders.OrderBy(p => p.Key.Length))
            {
                if (Inside(pair.Key, taken) || pair.Value.Count < 3)
                    continue;
                var contents = pair.Value.ToDictionary(child => child, child => items
                    .Where(i => i.Name.StartsWith(child + "/", StringComparison.OrdinalIgnoreCase))
                    .ToList(), StringComparer.OrdinalIgnoreCase);
                var names = contents.ToDictionary(c => c.Key, c => new HashSet<string>(
                    c.Value.Select(i => i.Name.Substring(c.Key.Length + 1)), StringComparer.OrdinalIgnoreCase));
                // The names most of them have.
                var typical = new HashSet<string>(names.Values.SelectMany(n => n)
                    .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() * 2 >= pair.Value.Count)
                    .Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
                if (typical.Count == 0)
                    continue;
                var alike = names.Where(n => n.Value.Count > 0 && n.Value.Count(typical.Contains) * 2 >= n.Value.Count).Select(n => n.Key).ToList();
                if (alike.Count < 3)
                    continue;
                var group = alike.Select(folder => new Slot { Group = pair.Key + "|folders", Path = folder, Items = contents[folder] }).ToList();
                if (group.Sum(s => s.Size) < significant)
                    continue;
                slots.AddRange(group);
                taken.AddRange(alike);
            }

            // File slots: three or more files with one extension in a folder that is not a slot.
            foreach (var byFolder in items.Where(i => !Inside(i.Name, taken))
                .GroupBy(i => Parent(i.Name), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var byExtension in byFolder.GroupBy(i => Path.GetExtension(i.Name).ToLowerInvariant()))
                {
                    if (byExtension.Key.Length == 0 || byExtension.Count() < 3)
                        continue;
                    if (byExtension.Key == ".yaml" && byExtension.Any(i => i.Name.EndsWith("/mapping.yaml", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (byExtension.Sum(i => i.Size) < significant)
                        continue;
                    slots.AddRange(byExtension.Select(i => new Slot
                    {
                        Group = byFolder.Key + "|*" + byExtension.Key,
                        Path = i.Name,
                        Items = new List<Item> { i },
                    }));
                }
            }
            return slots;
        }

        /// <summary>
        /// The slots to leave out so the archive's stored size comes within <paramref name="budget"/>:
        /// the oldest first, never a group's newest. Null when that is not enough.
        /// </summary>
        internal static List<Slot> Choose(IList<Item> items, List<Slot> slots, long budget)
        {
            long size = items.Sum(i => i.Size + Overhead(i));
            var dropped = new List<Slot>();
            if (size <= budget)
                return dropped;
            var newest = new HashSet<Slot>(slots.GroupBy(s => s.Group).Select(g => g.OrderByDescending(s => s.Newest).First()));
            foreach (Slot slot in slots.Where(s => !newest.Contains(s)).OrderBy(s => s.Newest))
            {
                dropped.Add(slot);
                size -= slot.Items.Sum(i => i.Size + Overhead(i));
                if (size <= budget)
                    return dropped;
            }
            return null;
        }

        // A zip entry's own headers: local and central, each with the name.
        private static long Overhead(Item item) => 30 + 46 + 2 * Encoding.UTF8.GetByteCount(item.Name) + 64;

        private static bool Inside(string path, List<string> folders) =>
            folders.Any(f => path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));

        private static string Parent(string name)
        {
            int slash = name.LastIndexOf('/');
            return slash < 0 ? "" : name.Substring(0, slash);
        }

        /// <summary>
        /// A copy of the archive at <paramref name="zipPath"/> without its oldest slots, at most
        /// <paramref name="maxBytes"/> long; null when it cannot be made that small (and why, in
        /// <paramref name="problem"/>). <paramref name="summary"/> says what was left out.
        /// </summary>
        public static string Trim(string zipPath, long maxBytes, out string summary, out string problem)
        {
            summary = null;
            problem = null;
            List<Item> items;
            using (var archive = ZipArchive.Open(zipPath))
            {
                items = archive.Entries.Where(e => !e.IsDirectory).Select(e => new Item
                {
                    Name = e.Key.Replace('\\', '/'),
                    Size = e.CompressedSize,
                    Modified = e.LastModifiedTime ?? DateTime.MinValue,
                }).ToList();
            }
            List<Slot> slots = FindSlots(items);
            if (slots.Count == 0)
            {
                problem = "it holds no group of save slots to leave the oldest out of";
                return null;
            }

            // A little room for the form around the file, and for the estimate.
            long budget = maxBytes - (64L << 10) - maxBytes / 50;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                List<Slot> drop = Choose(items, slots, budget);
                if (drop == null)
                {
                    problem = "even its newest saves are bigger than RomM takes";
                    return null;
                }
                string trimmed = Write(zipPath, drop);
                long length = new FileInfo(trimmed).Length;
                if (length <= maxBytes - (64L << 10))
                {
                    summary = $"left out its {drop.Count} oldest save slot(s) " +
                              $"(the oldest kept from {slots.Except(drop).Min(s => s.Newest):d}), {length / (1024 * 1024)} MB of {new FileInfo(zipPath).Length / (1024 * 1024)} MB sent";
                    return trimmed;
                }
                File.Delete(trimmed);
                budget -= length - (maxBytes - (64L << 10)) + (1L << 20);   // the estimate was short: aim lower
            }
            problem = "it could not be brought under the limit";
            return null;
        }

        // The archive again without the dropped slots' files, and with them off Ludusavi's lists.
        private static string Write(string zipPath, List<Slot> drop)
        {
            var left = new HashSet<string>(drop.SelectMany(s => s.Items).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            string trimmed = Path.Combine(Path.GetTempPath(), "romm-save-trimmed-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var archive = ZipArchive.Open(zipPath))
            using (var output = File.Create(trimmed))
            using (var writer = WriterFactory.Open(output, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate)))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.IsDirectory)
                        continue;
                    string name = entry.Key.Replace('\\', '/');
                    if (left.Contains(name))
                        continue;
                    if (name.EndsWith("mapping.yaml", StringComparison.OrdinalIgnoreCase) && (name == "mapping.yaml" || name.EndsWith("/mapping.yaml", StringComparison.OrdinalIgnoreCase)))
                    {
                        string text;
                        using (var reader = new StreamReader(entry.OpenEntryStream(), Encoding.UTF8))
                            text = reader.ReadToEnd();
                        string top = name.Length > "mapping.yaml".Length ? name.Substring(0, name.Length - "mapping.yaml".Length) : "";
                        byte[] bytes = Encoding.UTF8.GetBytes(WithoutFiles(text, top, left));
                        using (var data = new MemoryStream(bytes))
                            writer.Write(name, data, entry.LastModifiedTime);
                        continue;
                    }
                    using (var data = entry.OpenEntryStream())
                        writer.Write(name, data, entry.LastModifiedTime);
                }
            }
            return trimmed;
        }

        /// <summary>
        /// Ludusavi's mapping.yaml without the files left out. Its backup keeps a file at
        /// "&lt;game&gt;/drive-C/Users/..." and lists it as "C:/Users/...": each listed file whose
        /// archive name (under <paramref name="top"/>) is in <paramref name="left"/> goes, with
        /// the lines under it.
        /// </summary>
        internal static string WithoutFiles(string mapping, string top, ICollection<string> left)
        {
            var lines = mapping.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>();
            int skipDeeperThan = -1;
            foreach (string line in lines)
            {
                int indent = line.Length - line.TrimStart(' ').Length;
                if (skipDeeperThan >= 0)
                {
                    if (line.Trim().Length > 0 && indent > skipDeeperThan)
                        continue;
                    skipDeeperThan = -1;
                }
                string trimmed = line.Trim();
                if (trimmed.EndsWith(":", StringComparison.Ordinal) && trimmed.Length > 3)
                {
                    string key = trimmed.Substring(0, trimmed.Length - 1).Trim().Trim('"').Replace("\\\\", "\\").Replace('\\', '/');
                    // "C:/Users/..." is "drive-C/Users/..." in the backup.
                    if (key.Length > 2 && key[1] == ':' && char.IsLetter(key[0]))
                    {
                        string inArchive = top + "drive-" + char.ToUpperInvariant(key[0]) + key.Substring(2);
                        if (left.Contains(inArchive))
                        {
                            skipDeeperThan = indent;
                            continue;
                        }
                    }
                }
                kept.Add(line);
            }
            return string.Join("\n", kept);
        }
    }
}
