using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RomM.Saves
{
    /// <summary>
    /// A PlayStation 2 memory card image (PCSX2's Mcd001.ps2), read: its superblock, its FAT and
    /// its directory tree, so the saves on it can be taken out as files. Read-only.
    ///
    /// The layout is the console's own, as mymc (and mymcplus) document it:
    ///
    ///   - pages of page_len bytes (512), each followed in the image by 16 bytes of ECC when the
    ///     image has them (PCSX2's do: 528-byte pages); a cluster is pages_per_cluster pages (2).
    ///   - the superblock in page 0: "Sony PS2 Memory Card Format ", then page_len (0x28),
    ///     pages_per_cluster (0x2A), pages_per_erase_block (0x2C), clusters_per_card (0x30),
    ///     allocatable_cluster_offset (0x34), allocatable_cluster_end (0x38),
    ///     rootdir_cluster (0x3C) and the indirect FAT cluster list (0x50, 32 entries).
    ///   - the FAT, two levels: the indirect list names clusters (absolute) listing FAT clusters
    ///     (absolute), whose entries are the next cluster of a chain, relative to
    ///     allocatable_cluster_offset, with bit 31 set when in use; 0xFFFFFFFF ends a chain.
    ///   - directories as chains of 512-byte entries: mode (0x00; 0x8000 exists, 0x20 a
    ///     directory, 0x10 a file), length (0x04; bytes, or a directory's entry count), created
    ///     (0x08), first cluster (0x10), modified (0x18), name (0x40, 32 bytes). Times are
    ///     Japan's (UTC+9): a byte unused, then seconds, minutes, hours, day, month, year (16 bits).
    ///     A directory's first two entries are "." and "..".
    /// </summary>
    internal sealed class Ps2MemoryCard
    {
        public sealed class Entry
        {
            public string Name;
            public bool IsDirectory;
            public DateTime? ModifiedUtc;
            public byte[] Data;                      // a file's
            public List<Entry> Children = new List<Entry>();   // a directory's
        }

        private const string Magic = "Sony PS2 Memory Card Format ";
        private readonly byte[] _image;
        private readonly int _pageLen;
        private readonly int _pagesPerCluster;
        private readonly int _pagesPerBlock;
        private readonly int _rawPage;
        private readonly int _clusterSize;
        private readonly uint _allocOffset;
        private readonly uint _allocEnd;
        private readonly uint _rootCluster;
        private readonly uint[] _indirect = new uint[32];

        private Ps2MemoryCard(byte[] image)
        {
            _image = image;
            if (image.Length < 0x154 || Encoding.ASCII.GetString(image, 0, Magic.Length) != Magic)
                throw new InvalidDataException("Not a formatted PS2 memory card.");
            _pageLen = U16(0x28);
            _pagesPerCluster = U16(0x2A);
            _pagesPerBlock = U16(0x2C);
            uint clusters = U32(0x30);
            _allocOffset = U32(0x34);
            _allocEnd = U32(0x38);
            _rootCluster = U32(0x3C);
            for (int i = 0; i < 32; i++)
                _indirect[i] = U32(0x50 + 4 * i);
            if (_pageLen <= 0 || _pagesPerCluster <= 0)
                throw new InvalidDataException("The memory card's superblock makes no sense.");
            _clusterSize = _pageLen * _pagesPerCluster;

            // With ECC (528-byte pages) or without, by the image's length.
            long pages = (long)clusters * _pagesPerCluster;
            int spare = (_pageLen + 127) / 128 * 4;
            if (image.Length >= pages * (_pageLen + spare))
                _rawPage = _pageLen + spare;
            else if (image.Length >= pages * _pageLen)
                _rawPage = _pageLen;
            else
                throw new InvalidDataException("The memory card image is shorter than its superblock says.");
        }

        public static Ps2MemoryCard Read(byte[] image) => new Ps2MemoryCard(image);

        private int U16(int at) => _image[at] | (_image[at + 1] << 8);

        private uint U32(int at) => (uint)(_image[at] | (_image[at + 1] << 8) | (_image[at + 2] << 16) | (_image[at + 3] << 24));

        private static uint U32(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

        private byte[] Cluster(long absolute)
        {
            var data = new byte[_clusterSize];
            for (int p = 0; p < _pagesPerCluster; p++)
            {
                long at = (absolute * _pagesPerCluster + p) * _rawPage;
                if (at + _pageLen > _image.Length)
                    throw new InvalidDataException("A cluster lies past the end of the memory card image.");
                Buffer.BlockCopy(_image, (int)at, data, p * _pageLen, _pageLen);
            }
            return data;
        }

        /// <summary>The first erase block's data (no ECC): the superblock as a folder card keeps it (_pcsx2_superblock).</summary>
        public byte[] SuperblockBlock()
        {
            var block = new byte[_pageLen * _pagesPerBlock];
            for (int p = 0; p < _pagesPerBlock; p++)
                Buffer.BlockCopy(_image, p * _rawPage, block, p * _pageLen, _pageLen);
            return block;
        }

        // The FAT entry for a cluster (relative to the allocatable offset).
        private uint Fat(uint cluster)
        {
            if (cluster >= _allocEnd)
                throw new InvalidDataException("A chain leads outside the memory card.");
            int perCluster = _clusterSize / 4;
            uint fatIndex = cluster / (uint)perCluster;
            uint indirect = _indirect[fatIndex / (uint)perCluster];
            uint fatCluster = U32(Cluster(indirect), (int)(fatIndex % (uint)perCluster) * 4);
            return U32(Cluster(fatCluster), (int)(cluster % (uint)perCluster) * 4);
        }

        // A chain's bytes, up to length.
        private byte[] Chain(uint first, long length)
        {
            var output = new MemoryStream();
            uint cluster = first;
            int guard = 0;
            while (output.Length < length)
            {
                if (++guard > 100000)
                    throw new InvalidDataException("A chain on the memory card loops.");
                byte[] data = Cluster(cluster + _allocOffset);
                output.Write(data, 0, (int)Math.Min(data.Length, length - output.Length));
                if (output.Length >= length)
                    break;
                uint next = Fat(cluster);
                if (next == 0xFFFFFFFF || (next & 0x80000000) == 0)
                    break;
                cluster = next & 0x7FFFFFFF;
            }
            return output.ToArray();
        }

        /// <summary>Everything in the root directory, with what is in it.</summary>
        public List<Entry> Root()
        {
            // The root's own entry ("." in its first cluster) says how many entries it has.
            byte[] first = Cluster(_rootCluster + _allocOffset);
            uint count = U32(first, 0x04);
            return ReadDirectory(_rootCluster, count, 0);
        }

        private List<Entry> ReadDirectory(uint cluster, uint count, int depth)
        {
            var entries = new List<Entry>();
            if (depth > 8 || count > 4096)
                return entries;
            byte[] raw = Chain(cluster, count * 512L);
            for (int i = 0; i + 512 <= raw.Length; i += 512)
            {
                int mode = raw[i] | (raw[i + 1] << 8);
                if ((mode & 0x8000) == 0)
                    continue;   // deleted, or never used
                string name = Name(raw, i + 0x40);
                if (name == "." || name == ".." || name.Length == 0)
                    continue;
                uint length = U32(raw, i + 0x04);
                uint start = U32(raw, i + 0x10);
                var entry = new Entry { Name = name, ModifiedUtc = Time(raw, i + 0x18) };
                if ((mode & 0x20) != 0)
                {
                    entry.IsDirectory = true;
                    entry.Children = ReadDirectory(start, length, depth + 1);
                }
                else if ((mode & 0x10) != 0)
                {
                    entry.Data = length == 0 ? new byte[0] : Chain(start, length);
                }
                else
                {
                    continue;
                }
                entries.Add(entry);
            }
            return entries;
        }

        private static string Name(byte[] raw, int at)
        {
            int end = at;
            while (end < at + 32 && raw[end] != 0)
                end++;
            return Encoding.ASCII.GetString(raw, at, end - at);
        }

        private static DateTime? Time(byte[] raw, int at)
        {
            int second = raw[at + 1], minute = raw[at + 2], hour = raw[at + 3], day = raw[at + 4], month = raw[at + 5];
            int year = raw[at + 6] | (raw[at + 7] << 8);
            try
            {
                return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddHours(-9);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes the card out as a PCSX2 folder memory card at <paramref name="folder"/> (which
        /// must not exist): its superblock, and every save as a folder of its files, with their
        /// times. Returns the saves' (top-level folders') names.
        /// </summary>
        public List<string> WriteFolderCard(string folder)
        {
            var saves = new List<string>();
            List<Entry> root = Root();
            System.IO.Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "_pcsx2_superblock"), SuperblockBlock());
            foreach (Entry entry in root)
            {
                Write(folder, entry);
                if (entry.IsDirectory)
                    saves.Add(entry.Name);
            }
            return saves;
        }

        private static void Write(string parent, Entry entry)
        {
            string name = entry.Name;
            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            string path = Path.Combine(parent, name);
            if (entry.IsDirectory)
            {
                System.IO.Directory.CreateDirectory(path);
                foreach (Entry child in entry.Children)
                    Write(path, child);
                if (entry.ModifiedUtc.HasValue)
                    System.IO.Directory.SetLastWriteTimeUtc(path, entry.ModifiedUtc.Value);
            }
            else
            {
                File.WriteAllBytes(path, entry.Data ?? new byte[0]);
                if (entry.ModifiedUtc.HasValue)
                    File.SetLastWriteTimeUtc(path, entry.ModifiedUtc.Value);
            }
        }
    }
}
