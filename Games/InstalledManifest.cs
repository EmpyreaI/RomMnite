using Newtonsoft.Json;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Games
{
    /// <summary>
    /// What an installed game was installed from, kept so an update can be told and fetched in
    /// part (GameUpdates): the server's files as they were (by id: size, when RomM saw them
    /// change, hashes), and for a zip that was unpacked, every file in it with its CRC-32 and
    /// size (the zip's own index), so the files that changed in a new zip are known without
    /// reading the installed ones.
    ///
    /// One file per game, Installed\&lt;game id&gt;.json in the plugin's data.
    /// </summary>
    public sealed class InstalledManifest
    {
        /// <summary>How the game came: "zip" (one zip, unpacked), "folder" (a folder of files, fetched as one zip and unpacked), "file" (one file, kept as it is).</summary>
        public string Kind { get; set; }
        public int RomId { get; set; }
        public string InstallDir { get; set; }
        /// <summary>The file kept as it is ("file" only): its path.</summary>
        public string FilePath { get; set; }
        /// <summary>The ROM's own folder on the server (full_path), to name a folder's files as installed.</summary>
        public string RomPath { get; set; }
        public List<ServerFile> Files { get; set; } = new List<ServerFile>();
        public List<ZipEntryRecord> Entries { get; set; } = new List<ZipEntryRecord>();
        public DateTime InstalledUtc { get; set; }

        /// <summary>Set when the server's files differ: what changed, in words; null when up to date.</summary>
        public string UpdateAvailable { get; set; }
        /// <summary>The server's files the last notice was about, so the same update is announced once.</summary>
        public string NotifiedFor { get; set; }
        /// <summary>Times played without updating since it was last asked whether to stop checking (GameUpdates.PlayWithoutUpdating).</summary>
        public int Skipped { get; set; }

        public sealed class ServerFile
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string FullPath { get; set; }
            public long Size { get; set; }
            public DateTime? LastModified { get; set; }
            public string Sha1 { get; set; }
            public string Md5 { get; set; }
            public string Crc { get; set; }

            public static ServerFile From(RomMFile file) => new ServerFile
            {
                Id = file.Id ?? 0,
                Name = file.FileName,
                FullPath = file.FullPath,
                Size = file.FileSize ?? 0,
                LastModified = file.LastModified,
                Sha1 = file.Sha1,
                Md5 = file.Md5,
                Crc = file.Crc,
            };

            /// <summary>Whether two records of a file are of the same content, by what both have.</summary>
            public bool SameAs(ServerFile other)
            {
                if (other == null || Size != other.Size)
                    return false;
                if (!string.IsNullOrEmpty(Sha1) && !string.IsNullOrEmpty(other.Sha1))
                    return string.Equals(Sha1, other.Sha1, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(Md5) && !string.IsNullOrEmpty(other.Md5))
                    return string.Equals(Md5, other.Md5, StringComparison.OrdinalIgnoreCase);
                // No hashes (Windows games): when RomM last saw it change on disk. A second
                // apart is still the same time (rounding between the file system and the database).
                if (LastModified.HasValue && other.LastModified.HasValue)
                    return Math.Abs((LastModified.Value - other.LastModified.Value).TotalSeconds) < 2;
                return true;   // the size alone, all there is
            }
        }

        public sealed class ZipEntryRecord
        {
            public string Name { get; set; }
            public uint Crc { get; set; }
            public long Size { get; set; }
        }

        /// <summary>The server's files now, as a key for "the same update": ids, sizes and times.</summary>
        public static string Signature(IEnumerable<ServerFile> files) =>
            string.Join("|", files.OrderBy(f => f.Id).Select(f => $"{f.Id}:{f.Size}:{f.LastModified?.Ticks}:{f.Sha1}"));

        /// <summary>
        /// What differs between the files installed from and the server's now: files added,
        /// changed and gone (by id). Empty when the same.
        /// </summary>
        public static Changes Compare(IList<ServerFile> installed, IList<ServerFile> now)
        {
            var changes = new Changes();
            var before = installed.Where(f => f.Id > 0).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
            var after = now.Where(f => f.Id > 0).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (ServerFile file in after.Values)
            {
                if (!before.TryGetValue(file.Id, out ServerFile old))
                {
                    // The same file under a new id (RomM re-added it): by its name and content.
                    ServerFile renamed = before.Values.FirstOrDefault(b => !after.ContainsKey(b.Id)
                        && string.Equals(b.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase));
                    if (renamed != null && renamed.SameAs(file))
                        continue;
                    changes.Added.Add(file);
                }
                else if (!old.SameAs(file))
                    changes.Changed.Add(file);
            }
            foreach (ServerFile file in before.Values)
            {
                if (!after.ContainsKey(file.Id) && !now.Any(n => string.Equals(n.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase)))
                    changes.Removed.Add(file);
            }
            return changes;
        }

        public sealed class Changes
        {
            public List<ServerFile> Added = new List<ServerFile>();
            public List<ServerFile> Changed = new List<ServerFile>();
            public List<ServerFile> Removed = new List<ServerFile>();
            public bool Any => Added.Count + Changed.Count + Removed.Count > 0;

            public override string ToString()
            {
                var parts = new List<string>();
                if (Changed.Count > 0) parts.Add($"{Changed.Count} changed");
                if (Added.Count > 0) parts.Add($"{Added.Count} new");
                if (Removed.Count > 0) parts.Add($"{Removed.Count} removed");
                return string.Join(", ", parts) + (Changed.Count + Added.Count + Removed.Count == 1 ? " file" : " files");
            }
        }

        /// <summary>
        /// A folder game's file as it lies in the install folder: its path below the ROM's own
        /// folder on the server (the name RomM gives it in the folder's zip).
        /// </summary>
        public string LocalPathOf(ServerFile file)
        {
            string full = (file.FullPath ?? file.Name ?? "").Replace('\\', '/');
            string root = (RomPath ?? "").Replace('\\', '/').TrimEnd('/') + "/";
            string relative = root.Length > 1 && full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : file.Name;
            return relative;
        }

        // ---- On disk --------------------------------------------------------------------------------

        private static string PathFor(string folder, Guid gameId) => Path.Combine(folder, "Installed", gameId + ".json");

        public static InstalledManifest Load(string folder, Guid gameId)
        {
            try
            {
                string path = PathFor(folder, gameId);
                return File.Exists(path) ? JsonConvert.DeserializeObject<InstalledManifest>(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        public void Save(string folder, Guid gameId)
        {
            string path = PathFor(folder, gameId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        public static void Delete(string folder, Guid gameId)
        {
            try
            {
                string path = PathFor(folder, gameId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Kept: harmless, replaced at the next install.
            }
        }
    }
}
