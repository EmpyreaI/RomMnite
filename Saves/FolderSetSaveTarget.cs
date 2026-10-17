using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves
{
    /// <summary>
    /// A save made of one or more whole folders, or single files, under a shared root --
    /// PPSSPP's SAVEDATA\&lt;title id&gt;... folders, RPCS3's savedata\&lt;title id&gt;..., Xenia's
    /// &lt;title id&gt;\00000001, DuckStation's memcards\&lt;game&gt;_1.mcd. Each is a "unit", named by
    /// its path relative to the root. The units travel as one zip whose entry names are those
    /// same relative paths, so an archive unpacks under any root -- another PC, another profile.
    /// </summary>
    internal sealed class FolderSetSaveTarget : SaveTarget
    {
        private readonly string _root;
        private readonly List<string> _units;
        private readonly Action<IEnumerable<string>> _onUnitsLearned;
        private readonly Func<string, string> _unitOfEntry;
        private readonly Func<string, bool> _isBlankFile;

        /// <param name="root">The directory the units are relative to.</param>
        /// <param name="units">Relative paths of the folders known to belong to this game; may be empty.</param>
        /// <param name="fileName">The name the archive is filed under on the server.</param>
        /// <param name="onUnitsLearned">Told which units a downloaded save contained.</param>
        /// <param name="isBlankFile">
        /// For single-file units: whether the file at this path holds no save yet, such as a
        /// freshly formatted memory card. A blank file counts as no local save at all, so it is
        /// never uploaded and a save on the server replaces it.
        /// </param>
        /// <param name="unitOfEntry">
        /// Given an archive entry name ('/'-separated), the unit it belongs to, or null to skip
        /// the entry. Defaults to the entry's first folder, which fits saves that are single
        /// folders directly under the root.
        /// </param>
        public FolderSetSaveTarget(string emulatorTag, string root, IEnumerable<string> units, string fileName,
            Action<IEnumerable<string>> onUnitsLearned, Func<string, string> unitOfEntry = null,
            Func<string, bool> isBlankFile = null)
        {
            EmulatorTag = emulatorTag;
            FileName = fileName;
            _root = root;
            _units = (units ?? Enumerable.Empty<string>()).Select(Normalize).ToList();
            _onUnitsLearned = onUnitsLearned;
            _unitOfEntry = unitOfEntry ?? FirstFolder;
            _isBlankFile = isBlankFile;
        }

        public override string EmulatorTag { get; }

        public override string Slot => "autosave";

        public override string FileName { get; }

        public override string WatchDirectory => _root;

        public override string Describe()
        {
            string known = _units.Count == 0
                ? "none learned yet"
                : string.Join(", ", _units.Select(u => Directory.Exists(UnitPath(u)) || File.Exists(UnitPath(u)) ? u : u + " (missing)"));
            return $"save root \"{_root}\" ({(Directory.Exists(_root) ? "exists" : "does not exist")}), folders for this game: {known}";
        }

        private static string Normalize(string unit) => (unit ?? string.Empty).Replace('\\', '/').Trim('/');

        private static string FirstFolder(string entryName)
        {
            int slash = entryName.IndexOf('/');
            return slash <= 0 ? null : entryName.Substring(0, slash);
        }

        private string UnitPath(string unit) => Path.Combine(_root, unit.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Every file in the save, keyed by its archive entry name.</summary>
        private List<KeyValuePair<string, FileInfo>> Entries()
        {
            var entries = new List<KeyValuePair<string, FileInfo>>();
            foreach (string unit in _units)
            {
                // A unit that is a single file is its own one entry.
                var single = new FileInfo(UnitPath(unit));
                if (single.Exists)
                {
                    if (_isBlankFile == null || !_isBlankFile(single.FullName))
                        entries.Add(new KeyValuePair<string, FileInfo>(unit, single));
                    continue;
                }

                var directory = new DirectoryInfo(UnitPath(unit));
                if (!directory.Exists)
                    continue;

                foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                {
                    string relative = file.FullName.Substring(directory.FullName.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');
                    entries.Add(new KeyValuePair<string, FileInfo>(unit + "/" + relative, file));
                }
            }
            return entries;
        }

        public override bool Exists => Entries().Count > 0;

        public override DateTime UpdatedAtUtc => Entries().Max(e => e.Value.LastWriteTimeUtc);

        public override long SizeBytes => Entries().Sum(e => e.Value.Length);

        public override string ContentHash()
        {
            return SaveFileHash.EntriesAsZipHex(
                Entries().Select(e => new KeyValuePair<string, string>(e.Key, e.Value.FullName)));
        }

        public override PreparedUpload PrepareUpload()
        {
            string zipPath = Path.Combine(Path.GetTempPath(), "romm-save-" + Guid.NewGuid().ToString("N") + ".zip");

            using (var output = File.Create(zipPath))
            using (var writer = WriterFactory.Open(output, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate)))
            {
                foreach (var entry in Entries())
                {
                    // Shared read: the emulator may still hold the file open while the game runs.
                    using (var input = new FileStream(entry.Value.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        writer.Write(entry.Key, input, entry.Value.LastWriteTime);
                }
            }

            return new PreparedUpload(zipPath, FileName, isTemporary: true);
        }

        public override void Clear()
        {
            // Only the units known to be this game's; a unit not learned yet cannot be told apart
            // from another game's and stays.
            foreach (string unit in _units)
            {
                string path = UnitPath(unit);
                if (File.Exists(path))
                    File.Delete(path);
                else if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
        }

        public override void ApplyDownload(byte[] payload, DateTime? serverUpdatedAtUtc)
        {
            // Unpack beside the real folders first and swap each unit in only once the whole
            // archive is out, so a bad download never leaves a half-written save behind. Only the
            // units themselves are replaced: anything else under the same parent -- another
            // game's saves, downloaded content -- is left exactly as it is.
            string staging = Path.Combine(_root, ".romm-incoming-" + Guid.NewGuid().ToString("N"));
            var units = new List<string>();

            try
            {
                Directory.CreateDirectory(staging);
                string stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

                using (var stream = new MemoryStream(payload))
                using (var archive = ZipArchive.Open(stream))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                            continue;

                        string key = entry.Key.Replace('\\', '/').TrimStart('/');
                        string unit = _unitOfEntry(key);
                        if (string.IsNullOrEmpty(unit))
                            continue; // Not part of a save this layout knows how to place.

                        unit = Normalize(unit);
                        // The entry is either the unit itself (a single-file save) or inside it.
                        if (!key.Equals(unit, StringComparison.OrdinalIgnoreCase)
                            && !key.StartsWith(unit + "/", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string destination = Path.GetFullPath(Path.Combine(staging, key.Replace('/', Path.DirectorySeparatorChar)));
                        if (!destination.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Save archive entry escapes the save folder: {entry.Key}");

                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (var input = entry.OpenEntryStream())
                        using (var output = File.Create(destination))
                            input.CopyTo(output);

                        if (serverUpdatedAtUtc.HasValue)
                            File.SetLastWriteTimeUtc(destination, serverUpdatedAtUtc.Value.ToUniversalTime());

                        if (!units.Contains(unit, StringComparer.OrdinalIgnoreCase))
                            units.Add(unit);
                    }
                }

                if (units.Count == 0)
                    throw new InvalidDataException("The downloaded save holds no save folders.");

                foreach (string unit in units)
                {
                    string live = UnitPath(unit);
                    string retired = live + ".romm-old";
                    string incoming = Path.Combine(staging, unit.Replace('/', Path.DirectorySeparatorChar));

                    Directory.CreateDirectory(Path.GetDirectoryName(live));

                    if (File.Exists(incoming))
                    {
                        if (File.Exists(retired))
                            File.Delete(retired);
                        if (File.Exists(live))
                            File.Move(live, retired);

                        File.Move(incoming, live);

                        if (File.Exists(retired))
                            File.Delete(retired);
                        continue;
                    }

                    if (Directory.Exists(retired))
                        Directory.Delete(retired, true);
                    if (Directory.Exists(live))
                        Directory.Move(live, retired);

                    Directory.Move(incoming, live);

                    if (Directory.Exists(retired))
                        Directory.Delete(retired, true);
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(staging))
                        Directory.Delete(staging, true);
                }
                catch
                {
                    // A leftover staging folder is not worth failing a sync over.
                }
            }

            foreach (string unit in units)
            {
                if (!_units.Contains(unit, StringComparer.OrdinalIgnoreCase))
                    _units.Add(unit);
            }
            _onUnitsLearned?.Invoke(units);
        }
    }
}
