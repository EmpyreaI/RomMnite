using System;
using System.Collections.Generic;
using System.IO;
using RomM.Saves;
using RomM.Saves.Handlers;
using Xunit;

namespace RomM.Tests
{
    public class FolderSetSaveTargetTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "romm-tests-" + Guid.NewGuid().ToString("N"));

        public FolderSetSaveTargetTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private void Write(string folder, string file, string content)
        {
            Directory.CreateDirectory(Path.Combine(_root, folder));
            File.WriteAllText(Path.Combine(_root, folder, file), content);
        }

        [Fact]
        public void Upload_archive_hashes_the_same_as_the_folders()
        {
            Write("ULUS10041", "PARAM.SFO", "sfo");
            Write("ULUS10041DATA00", "DATA.BIN", "data");

            var target = new FolderSetSaveTarget("ppsspp", _root, new[] { "ULUS10041", "ULUS10041DATA00" }, "Game.zip", null);

            Assert.True(target.Exists);
            using (var upload = target.PrepareUpload())
                Assert.Equal(target.ContentHash(), SaveFileHash.ZipHexFile(upload.FilePath));
        }

        [Fact]
        public void Download_replaces_folders_and_reports_their_names()
        {
            Write("ULUS10041", "PARAM.SFO", "new");
            var source = new FolderSetSaveTarget("ppsspp", _root, new[] { "ULUS10041" }, "Game.zip", null);
            byte[] payload;
            using (var upload = source.PrepareUpload())
                payload = File.ReadAllBytes(upload.FilePath);

            Write("ULUS10041", "PARAM.SFO", "old");
            Write("ULUS10041", "STALE.BIN", "stale");

            var learned = new List<string>();
            var target = new FolderSetSaveTarget("ppsspp", _root, new string[0], "Game.zip", names => learned.AddRange(names));
            Assert.False(target.Exists);

            target.ApplyDownload(payload, null);

            Assert.Equal(new[] { "ULUS10041" }, learned);
            Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "ULUS10041", "PARAM.SFO")));
            Assert.False(File.Exists(Path.Combine(_root, "ULUS10041", "STALE.BIN")));
            Assert.True(target.Exists);
        }

        [Fact]
        public void Xenia_syncs_saves_and_headers_but_not_other_title_content()
        {
            Write(Path.Combine("4D5307F1", "00000001", "Hero000"), "save.dat", "save");
            Write(Path.Combine("4D5307F1", "Headers", "00000001"), "Hero000.header", "header");
            Write(Path.Combine("4D5307F1", "00000002", "DLC"), "dlc.bin", "dlc");
            Write(Path.Combine("FFFE07D1", "00010000", "E03000005BE6EC9C"), "profile", "profile");

            var units = XeniaSaveHandler.SaveUnitsWrittenSince(_root, DateTime.UtcNow.AddMinutes(-1));
            Assert.Equal(new[] { "4D5307F1/00000001", "4D5307F1/Headers/00000001" }, units);

            var source = new FolderSetSaveTarget("xenia", _root, units, "Game.zip", null, XeniaSaveHandler.SaveUnitOf);
            byte[] payload;
            using (var upload = source.PrepareUpload())
            {
                Assert.Equal(source.ContentHash(), SaveFileHash.ZipHexFile(upload.FilePath));
                payload = File.ReadAllBytes(upload.FilePath);
            }

            Directory.Delete(Path.Combine(_root, "4D5307F1", "00000001"), true);
            Directory.Delete(Path.Combine(_root, "4D5307F1", "Headers"), true);

            var learned = new List<string>();
            var target = new FolderSetSaveTarget("xenia", _root, new string[0], "Game.zip", names => learned.AddRange(names), XeniaSaveHandler.SaveUnitOf);
            target.ApplyDownload(payload, null);

            Assert.Equal(new[] { "4D5307F1/00000001", "4D5307F1/Headers/00000001" }, learned);
            Assert.Equal("save", File.ReadAllText(Path.Combine(_root, "4D5307F1", "00000001", "Hero000", "save.dat")));
            Assert.Equal("header", File.ReadAllText(Path.Combine(_root, "4D5307F1", "Headers", "00000001", "Hero000.header")));
            // Downloaded content beside the saves is untouched, and never uploaded.
            Assert.Equal("dlc", File.ReadAllText(Path.Combine(_root, "4D5307F1", "00000002", "DLC", "dlc.bin")));
        }

        [Fact]
        public void Pcsx2_uses_the_folder_card_in_slot_one()
        {
            Directory.CreateDirectory(Path.Combine(_root, "inis"));
            Directory.CreateDirectory(Path.Combine(_root, "memcards", "Mcd001_converted.ps2"));
            File.WriteAllText(Path.Combine(_root, "memcards", "Mcd001.ps2"), "a single-file card");

            string Ini(string card) => string.Join("\n", new[]
            {
                "[Folders]", "MemoryCards = memcards",
                "[MemoryCards]", "Slot1_Enable = true", "Slot1_Filename = " + card,
            });

            File.WriteAllText(Path.Combine(_root, "inis", "PCSX2.ini"), Ini("Mcd001_converted.ps2"));
            Assert.Equal(Path.Combine(_root, "memcards", "Mcd001_converted.ps2"), Pcsx2SaveHandler.FindSaveRoot(_root));

            // A single-file card cannot be split per game, so it is not a save root.
            File.WriteAllText(Path.Combine(_root, "inis", "PCSX2.ini"), Ini("Mcd001.ps2"));
            Assert.Null(Pcsx2SaveHandler.FindSaveRoot(_root));
        }

        [Fact]
        public void Pcsx2_system_folder_is_not_a_save()
        {
            Assert.True(Pcsx2SaveHandler.IsSystemFolder("BADATA-SYSTEM"));
            Assert.True(Pcsx2SaveHandler.IsSystemFolder("BEDATA-SYSTEM"));
            Assert.False(Pcsx2SaveHandler.IsSystemFolder("BASLUS-20063MC Save"));
        }

        [Fact]
        public void DuckStation_syncs_the_games_card_file_and_not_a_shared_one()
        {
            const string card = "Tony Hawk's Pro Skater 2 (USA)_1.mcd";
            File.WriteAllText(Path.Combine(_root, card), "card");
            File.WriteAllText(Path.Combine(_root, "shared_card_1.mcd"), "shared");
            File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a card");

            var units = DuckStationSaveHandler.CardsWrittenSince(_root, DateTime.UtcNow.AddMinutes(-1));
            Assert.Equal(new[] { card }, units);

            var source = new FolderSetSaveTarget("duckstation", _root, units, "Game.zip", null, DuckStationSaveHandler.CardOfEntry);
            Assert.True(source.Exists);
            byte[] payload;
            using (var upload = source.PrepareUpload())
            {
                Assert.Equal(source.ContentHash(), SaveFileHash.ZipHexFile(upload.FilePath));
                payload = File.ReadAllBytes(upload.FilePath);
            }

            File.WriteAllText(Path.Combine(_root, card), "older");

            var learned = new List<string>();
            var target = new FolderSetSaveTarget("duckstation", _root, new string[0], "Game.zip", names => learned.AddRange(names), DuckStationSaveHandler.CardOfEntry);
            target.ApplyDownload(payload, null);

            Assert.Equal(new[] { card }, learned);
            Assert.Equal("card", File.ReadAllText(Path.Combine(_root, card)));
            Assert.Equal("shared", File.ReadAllText(Path.Combine(_root, "shared_card_1.mcd")));
        }

        private static byte[] Card(bool withSave)
        {
            var card = new byte[131072];
            card[0] = (byte)'M';
            card[1] = (byte)'C';
            for (int frame = 1; frame <= 15; frame++)
                card[frame * 0x80] = 0xA0; // free
            if (withSave)
                card[0x80] = 0x51; // first block of a save
            return card;
        }

        [Fact]
        public void A_blank_DuckStation_card_counts_as_no_local_save()
        {
            const string card = "Game (USA)_1.mcd";
            string path = Path.Combine(_root, card);

            File.WriteAllBytes(path, Card(withSave: false));
            Assert.True(DuckStationSaveHandler.IsBlankCard(path));
            var blank = new FolderSetSaveTarget("duckstation", _root, new[] { card }, "Game.zip", null,
                DuckStationSaveHandler.CardOfEntry, DuckStationSaveHandler.IsBlankCard);
            Assert.False(blank.Exists);

            File.WriteAllBytes(path, Card(withSave: true));
            Assert.False(DuckStationSaveHandler.IsBlankCard(path));
            Assert.True(blank.Exists);

            // Not recognisable as a card: never treated as blank.
            File.WriteAllText(path, "something else");
            Assert.False(DuckStationSaveHandler.IsBlankCard(path));
        }

        [Fact]
        public void Dolphin_syncs_gci_files_under_their_region_and_card()
        {
            const string save = "USA/Card A/01-GM8E-MetroidPrime A.gci";
            Write(Path.Combine("USA", "Card A"), "01-GM8E-MetroidPrime A.gci", "save");
            Write(Path.Combine("USA", "Card A"), "01-GM8E-MetroidPrime A.gci.deleted", "old");
            File.WriteAllText(Path.Combine(_root, "SRAM.raw"), "sram");

            var units = DolphinSaveHandler.SavesWrittenSince(_root, DateTime.UtcNow.AddMinutes(-1));
            Assert.Equal(new[] { save }, units);

            var source = new FolderSetSaveTarget("dolphin", _root, units, "Game.zip", null, DolphinSaveHandler.SaveOfEntry);
            byte[] payload;
            using (var upload = source.PrepareUpload())
            {
                Assert.Equal(source.ContentHash(), SaveFileHash.ZipHexFile(upload.FilePath));
                payload = File.ReadAllBytes(upload.FilePath);
            }

            Directory.Delete(Path.Combine(_root, "USA"), true);

            var learned = new List<string>();
            var target = new FolderSetSaveTarget("dolphin", _root, new string[0], "Game.zip", names => learned.AddRange(names), DolphinSaveHandler.SaveOfEntry);
            target.ApplyDownload(payload, null);

            Assert.Equal(new[] { save }, learned);
            Assert.Equal("save", File.ReadAllText(Path.Combine(_root, "USA", "Card A", "01-GM8E-MetroidPrime A.gci")));
        }

        [Fact]
        public void Rpcs3_saves_live_under_the_default_user()
        {
            Assert.Equal(
                Path.Combine(_root, "dev_hdd0", "home", "00000001", "savedata"),
                Rpcs3SaveHandler.FindSaveRoot(_root));
        }

        [Fact]
        public void Eboot_launched_games_are_named_after_the_game()
        {
            var request = new SaveTargetRequest
            {
                ContentPath = @"C:\Games\Demon's Souls\PS3_GAME\USRDIR\EBOOT.BIN",
                Game = new Playnite.SDK.Models.Game("Demon's Souls: Remix?"),
            };

            Assert.Equal("Demon's Souls_ Remix_.zip", LearnedFolderSaveHandler.ArchiveName(request));
        }

        [Fact]
        public void Only_folders_written_in_the_session_are_picked_up()
        {
            Write("OLDGAME", "DATA.BIN", "x");
            File.SetLastWriteTimeUtc(Path.Combine(_root, "OLDGAME", "DATA.BIN"), DateTime.UtcNow.AddHours(-1));
            Write("NEWGAME", "DATA.BIN", "x");

            Assert.Equal(new[] { "NEWGAME" }, PpssppSaveHandler.FoldersWrittenSince(_root, DateTime.UtcNow.AddMinutes(-1)));
        }
    }
}
