using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using RomM.Games;
using Xunit;

namespace RomM.Tests
{
    public class RemoteZipTests
    {
        private static byte[] Zip(Dictionary<string, byte[]> files, string comment = null)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    int i = 0;
                    foreach (var file in files)
                    {
                        var entry = zip.CreateEntry(file.Key, i++ % 2 == 0 ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                        using (var stream = entry.Open())
                            stream.Write(file.Value, 0, file.Value.Length);
                    }
                }
                return memory.ToArray();
            }
        }

        private static Dictionary<string, byte[]> Files(int count)
        {
            var random = new Random(7);
            var files = new Dictionary<string, byte[]>();
            for (int i = 0; i < count; i++)
            {
                var data = new byte[random.Next(0, 20000)];
                random.NextBytes(data);
                // Half compressible.
                for (int j = 0; j < data.Length / 2; j++)
                    data[j] = (byte)'x';
                files[$"Game/Data/file{i}.bin"] = data;
            }
            return files;
        }

        [Fact]
        public void The_index_is_found_and_read_from_the_tail()
        {
            Dictionary<string, byte[]> files = Files(25);
            byte[] zip = Zip(files);
            int tailLength = Math.Min(zip.Length, RemoteZip.TailLength);
            byte[] tail = zip.Skip(zip.Length - tailLength).ToArray();
            RemoteZip.CentralDirectory dir = RemoteZip.FindDirectory(tail, zip.Length - tailLength, zip.Length);
            Assert.NotNull(dir);
            Assert.Equal(25, dir.Entries);
            List<RemoteZip.Entry> entries = RemoteZip.ParseDirectory(zip.Skip((int)dir.Offset).Take((int)dir.Size).ToArray());
            Assert.Equal(files.Keys.OrderBy(k => k), entries.Select(e => e.Name).OrderBy(k => k));
            foreach (RemoteZip.Entry entry in entries)
            {
                byte[] data = files[entry.Name];
                Assert.Equal(data.Length, entry.Size);
                Assert.Equal(RemoteZip.Crc32(0, data, 0, data.Length), entry.Crc);
            }
        }

        [Fact]
        public void Only_the_wanted_files_bytes_are_fetched_and_they_unpack()
        {
            Dictionary<string, byte[]> files = Files(40);
            byte[] zip = Zip(files);
            byte[] tail = zip.Skip(Math.Max(0, zip.Length - RemoteZip.TailLength)).ToArray();
            RemoteZip.CentralDirectory dir = RemoteZip.FindDirectory(tail, zip.Length - tail.Length, zip.Length);
            List<RemoteZip.Entry> entries = RemoteZip.ParseDirectory(zip.Skip((int)dir.Offset).Take((int)dir.Size).ToArray());
            List<RemoteZip.Entry> wanted = entries.Where((e, i) => i % 7 == 3).ToList();

            List<RemoteZip.Span> spans = RemoteZip.Plan(wanted, entries, dir.Offset, gap: 0);
            Assert.Equal(wanted.Count, spans.Sum(s => s.Entries.Count));
            Assert.True(spans.Sum(s => s.Length) < zip.Length / 2);
            foreach (RemoteZip.Span span in spans)
            {
                byte[] fetched = zip.Skip((int)span.Start).Take((int)span.Length).ToArray();
                foreach (RemoteZip.Entry entry in span.Entries)
                {
                    int at = (int)(entry.LocalHeaderOffset - span.Start);
                    long dataAt = RemoteZip.DataStart(fetched, at, entry.LocalHeaderOffset) - span.Start;
                    byte[] raw = fetched.Skip((int)dataAt).Take((int)entry.CompressedSize).ToArray();
                    byte[] data;
                    if (entry.Method == 0)
                        data = raw;
                    else
                    {
                        using (var inflate = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress))
                        using (var output = new MemoryStream())
                        {
                            inflate.CopyTo(output);
                            data = output.ToArray();
                        }
                    }
                    Assert.Equal(files[entry.Name], data);
                }
            }
        }

        [Fact]
        public void Not_a_zip_has_no_index()
        {
            var junk = Encoding.ASCII.GetBytes(new string('z', 5000));
            Assert.Null(RemoteZip.FindDirectory(junk, 0, junk.Length));
        }
    }

    public class InstalledManifestTests
    {
        private static InstalledManifest.ServerFile F(int id, long size, int minute = 0, string path = null) => new InstalledManifest.ServerFile
        {
            Id = id,
            Size = size,
            FullPath = path ?? $"roms/psx/Game/disc{id}.bin",
            LastModified = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc),
        };

        [Fact]
        public void The_same_files_are_no_update()
        {
            Assert.False(InstalledManifest.Compare(new[] { F(1, 10), F(2, 20) }, new[] { F(1, 10), F(2, 20) }).Any);
        }

        [Fact]
        public void A_file_changed_added_and_removed()
        {
            InstalledManifest.Changes changes = InstalledManifest.Compare(new[] { F(1, 10), F(2, 20), F(3, 30) }, new[] { F(1, 10, 5), F(2, 20), F(4, 40) });
            Assert.Equal(new[] { 1 }, changes.Changed.Select(f => f.Id));
            Assert.Equal(new[] { 4 }, changes.Added.Select(f => f.Id));
            Assert.Equal(new[] { 3 }, changes.Removed.Select(f => f.Id));
        }

        [Fact]
        public void The_same_file_under_a_new_id_is_no_change()
        {
            Assert.False(InstalledManifest.Compare(new[] { F(1, 10, 0, "roms/win/Game.zip") }, new[] { F(9, 10, 0, "roms/win/Game.zip") }).Any);
        }

        [Fact]
        public void A_replaced_zip_of_the_same_name_is_an_update()
        {
            Assert.True(InstalledManifest.Compare(new[] { F(1, 10, 0, "roms/win/Game.zip") }, new[] { F(1, 12, 3, "roms/win/Game.zip") }).Any);
        }

        [Fact]
        public void A_folder_files_local_path_is_below_the_roms_folder()
        {
            var manifest = new InstalledManifest { RomPath = "roms/psx/Game" };
            Assert.Equal("Disc 1/track.bin", manifest.LocalPathOf(new InstalledManifest.ServerFile { FullPath = "roms/psx/Game/Disc 1/track.bin", Name = "track.bin" }));
        }
    }
}
