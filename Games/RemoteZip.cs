using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Games
{
    /// <summary>
    /// A zip on the server read without downloading it: its central directory (the index at its
    /// end, listing every file with its CRC-32, sizes and where its data starts) fetched with HTTP
    /// range requests, so an update downloads only the bytes of the files that changed.
    ///
    /// Pure parsing and planning here (unit-tested); the fetching is GameUpdates'. The layout is
    /// PKWARE's APPNOTE: the end of central directory record (EOCD, 0x06054b50) in the last 64 KB,
    /// the ZIP64 locator (0x07064b50) and record (0x06064b50) before it for archives over 4 GB or
    /// 65535 files, each central directory entry (0x02014b50) with its ZIP64 extra field (0x0001)
    /// where a size or offset is 0xFFFFFFFF, and each file's local header (0x04034b50).
    /// </summary>
    internal static class RemoteZip
    {
        /// <summary>The length of tail to fetch first: the EOCD (22 bytes), a maximal comment, and room for the ZIP64 records before it.</summary>
        public const int TailLength = 22 + 65535 + 20 + 56 + 1024;

        public sealed class Entry
        {
            public string Name;
            public uint Crc;
            public long CompressedSize;
            public long Size;
            public ushort Method;
            public ushort Flags;
            public long LocalHeaderOffset;
            public bool IsDirectory => Name.EndsWith("/", StringComparison.Ordinal) || Name.EndsWith("\\", StringComparison.Ordinal);
            public bool Encrypted => (Flags & 0x0001) != 0;
        }

        /// <summary>Where the central directory is, from the archive's tail.</summary>
        public sealed class CentralDirectory
        {
            public long Offset;
            public long Size;
            public long Entries;
            /// <summary>Set when the ZIP64 record lies before the tail fetched: fetch it (56 bytes) and call <see cref="ReadZip64Record"/>.</summary>
            public long Zip64RecordOffset = -1;
        }

        /// <summary>
        /// The central directory's place, from the last bytes of the archive (<paramref name="tail"/>,
        /// starting at <paramref name="tailStart"/> in an archive <paramref name="total"/> bytes long).
        /// Null when the tail holds no end of central directory record (not a zip).
        /// </summary>
        public static CentralDirectory FindDirectory(byte[] tail, long tailStart, long total)
        {
            for (int at = tail.Length - 22; at >= 0; at--)
            {
                if (U32(tail, at) != 0x06054b50)
                    continue;
                int commentLength = U16(tail, at + 20);
                if (tailStart + at + 22 + commentLength != total)
                    continue;   // a stray signature inside the comment or data
                var dir = new CentralDirectory
                {
                    Entries = U16(tail, at + 10),
                    Size = U32(tail, at + 12),
                    Offset = U32(tail, at + 16),
                };
                bool zip64 = dir.Offset == 0xFFFFFFFF || dir.Size == 0xFFFFFFFF || dir.Entries == 0xFFFF;
                int locator = at - 20;
                if (locator >= 0 && U32(tail, locator) == 0x07064b50)
                {
                    long record = (long)U64(tail, locator + 8);
                    long inTail = record - tailStart;
                    if (inTail >= 0 && inTail + 56 <= tail.Length)
                        ReadZip64Record(tail, (int)inTail, dir);
                    else
                        dir.Zip64RecordOffset = record;
                }
                else if (zip64)
                    return null;   // says ZIP64 but has no locator: not readable here
                return dir;
            }
            return null;
        }

        /// <summary>The ZIP64 end of central directory record (at <paramref name="at"/> in <paramref name="data"/>) into <paramref name="dir"/>.</summary>
        public static void ReadZip64Record(byte[] data, int at, CentralDirectory dir)
        {
            if (U32(data, at) != 0x06064b50)
                throw new InvalidDataException("No ZIP64 end of central directory record where its locator says.");
            dir.Entries = (long)U64(data, at + 32);
            dir.Size = (long)U64(data, at + 40);
            dir.Offset = (long)U64(data, at + 48);
            dir.Zip64RecordOffset = -1;
        }

        private static Encoding s_cp437;

        private static Encoding Cp437
        {
            get
            {
                if (s_cp437 == null)
                {
                    try
                    {
                        s_cp437 = Encoding.GetEncoding(437);
                    }
                    catch
                    {
                        s_cp437 = Encoding.UTF8;
                    }
                }
                return s_cp437;
            }
        }

        /// <summary>The central directory's entries.</summary>
        public static List<Entry> ParseDirectory(byte[] cd)
        {
            var entries = new List<Entry>();
            int at = 0;
            while (at + 46 <= cd.Length && U32(cd, at) == 0x02014b50)
            {
                ushort flags = U16(cd, at + 8);
                int nameLength = U16(cd, at + 28), extraLength = U16(cd, at + 30), commentLength = U16(cd, at + 32);
                if (at + 46 + nameLength + extraLength + commentLength > cd.Length)
                    throw new InvalidDataException("The central directory ends inside an entry.");
                var entry = new Entry
                {
                    Flags = flags,
                    Method = U16(cd, at + 10),
                    Crc = U32(cd, at + 16),
                    CompressedSize = U32(cd, at + 20),
                    Size = U32(cd, at + 24),
                    LocalHeaderOffset = U32(cd, at + 42),
                    Name = ((flags & 0x0800) != 0 ? Encoding.UTF8 : Cp437).GetString(cd, at + 46, nameLength),
                };

                // ZIP64: the sizes and offset that did not fit, in this order, each only if its
                // field above is 0xFFFFFFFF.
                int extra = at + 46 + nameLength, extraEnd = extra + extraLength;
                while (extra + 4 <= extraEnd)
                {
                    ushort id = U16(cd, extra), length = U16(cd, extra + 2);
                    if (id == 0x0001)
                    {
                        int field = extra + 4, fieldEnd = Math.Min(extra + 4 + length, extraEnd);
                        if (entry.Size == 0xFFFFFFFF && field + 8 <= fieldEnd) { entry.Size = (long)U64(cd, field); field += 8; }
                        if (entry.CompressedSize == 0xFFFFFFFF && field + 8 <= fieldEnd) { entry.CompressedSize = (long)U64(cd, field); field += 8; }
                        if (entry.LocalHeaderOffset == 0xFFFFFFFF && field + 8 <= fieldEnd) { entry.LocalHeaderOffset = (long)U64(cd, field); }
                        break;
                    }
                    extra += 4 + length;
                }
                entries.Add(entry);
                at += 46 + nameLength + extraLength + commentLength;
            }
            return entries;
        }

        /// <summary>
        /// Where a file's data starts, from its local header (the first 30 bytes and more of
        /// <paramref name="header"/>, which begins at <paramref name="localHeaderOffset"/>).
        /// </summary>
        public static long DataStart(byte[] header, int at, long localHeaderOffset)
        {
            if (header.Length - at < 30 || U32(header, at) != 0x04034b50)
                throw new InvalidDataException("No local file header where the central directory says.");
            return localHeaderOffset + 30 + U16(header, at + 26) + U16(header, at + 28);
        }

        /// <summary>A stretch of the archive fetched in one request, and the files in it.</summary>
        public sealed class Span
        {
            public long Start;
            public long End;   // exclusive
            public List<Entry> Entries = new List<Entry>();
            public long Length => End - Start;
        }

        /// <summary>
        /// The stretches to fetch for <paramref name="wanted"/>: each file from its local header to
        /// where the next file (of <paramref name="all"/>) or the central directory begins, which
        /// holds its header, data and any data descriptor exactly. Files closer than
        /// <paramref name="gap"/> are fetched together (fewer requests for a few wasted bytes), up
        /// to <paramref name="maxSpan"/> a request.
        /// </summary>
        public static List<Span> Plan(IEnumerable<Entry> wanted, IList<Entry> all, long directoryOffset,
            long gap = 1L << 20, long maxSpan = 256L << 20)
        {
            List<long> starts = all.Select(e => e.LocalHeaderOffset).Distinct().OrderBy(o => o).ToList();
            Func<long, long> endOf = offset =>
            {
                int i = starts.BinarySearch(offset);
                return i >= 0 && i + 1 < starts.Count ? starts[i + 1] : directoryOffset;
            };

            var spans = new List<Span>();
            Span current = null;
            foreach (Entry entry in wanted.OrderBy(e => e.LocalHeaderOffset))
            {
                long start = entry.LocalHeaderOffset, end = endOf(start);
                if (current != null && start - current.End <= gap && end - current.Start <= maxSpan)
                {
                    current.End = Math.Max(current.End, end);
                    current.Entries.Add(entry);
                    continue;
                }
                current = new Span { Start = start, End = end };
                current.Entries.Add(entry);
                spans.Add(current);
            }
            return spans;
        }

        /// <summary>Whether a file's data can be read here: not encrypted, and stored or compressed by a method SharpCompress reads.</summary>
        public static bool Readable(Entry entry) =>
            !entry.Encrypted && (entry.Method == 0 || entry.Method == 8 || entry.Method == 9 || entry.Method == 12 || entry.Method == 14 || entry.Method == 93 || entry.Method == 95);

        // ---- CRC-32 (the zip's own) ----------------------------------------------------------------

        private static readonly uint[] s_table = Table();

        private static uint[] Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        /// <summary>The CRC-32 carried on over more bytes (start with 0).</summary>
        public static uint Crc32(uint crc, byte[] data, int offset, int count)
        {
            crc = ~crc;
            for (int i = offset; i < offset + count; i++)
                crc = s_table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        /// <summary>A file's CRC-32 and length.</summary>
        public static uint Crc32(Stream stream)
        {
            var buffer = new byte[1 << 20];
            uint crc = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                crc = Crc32(crc, buffer, 0, read);
            return crc;
        }

        // ---- Little-endian --------------------------------------------------------------------------

        private static ushort U16(byte[] b, int at) => (ushort)(b[at] | (b[at + 1] << 8));

        private static uint U32(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

        private static ulong U64(byte[] b, int at) => U32(b, at) | ((ulong)U32(b, at + 4) << 32);
    }

    /// <summary>A read-only window onto part of another stream (one zip entry's bytes in a span fetched).</summary>
    internal sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SubStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Max(0, Math.Min(_length, value));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = _length - _position;
            if (left <= 0)
                return 0;
            _inner.Position = _start + _position;
            int read = _inner.Read(buffer, offset, (int)Math.Min(count, left));
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : _length + offset;
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
