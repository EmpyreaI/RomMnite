using System.Collections.Generic;
using System.Linq;
using RomM.Games;
using RomM.Models.RomM.Rom;
using Xunit;

namespace RomM.Tests
{
    public class SameGameTests
    {
        private static SameGame.Facts F(string name, int platform, int igdb = 0, int ss = 0, params int[] linked) =>
            new SameGame.Facts { Name = name, PlatformId = platform, Igdb = igdb, Ss = ss, Linked = linked.ToList() };

        private static bool Together(List<List<int>> groups, int a, int b) => groups.Any(g => g.Contains(a) && g.Contains(b));

        [Theory]
        [InlineData("Metal Gear Solid: Peace Walker - Master Collection Version", "Metal Gear Solid: Peace Walker")]
        [InlineData("Okami HD", "Okami")]
        [InlineData("Shadow of the Colossus (HD Remaster)", "Shadow of the Colossus")]
        [InlineData("Age of Empires II: Definitive Edition", "Age of Empires II")]
        [InlineData("Disco Elysium - The Final Cut", "Disco Elysium")]
        [InlineData("Final Fantasy VII Remake", "Final Fantasy VII Remake")]
        [InlineData("Halo: The Master Chief Collection", "Halo: The Master Chief Collection")]
        [InlineData("HD", "HD")]
        [InlineData("Cyberpunk 2077 (Collector's Edition)", "Cyberpunk 2077")]
        public void Edition_words_are_taken_off_the_end(string name, string game)
        {
            Assert.Equal(game, SameGame.EditionBase(name));
        }

        [Fact]
        public void An_edition_is_its_game_even_under_another_igdb_id()
        {
            var groups = SameGame.Group(new List<SameGame.Facts>
            {
                F("Metal Gear Solid: Peace Walker", 1, igdb: 100),
                F("Metal Gear Solid: Peace Walker - Master Collection Version", 2, igdb: 200),
            });
            Assert.True(Together(groups, 0, 1));
        }

        [Fact]
        public void The_same_plain_name_under_two_igdb_ids_stays_apart()
        {
            var groups = SameGame.Group(new List<SameGame.Facts> { F("Doom", 1, igdb: 1), F("Doom", 2, igdb: 2) });
            Assert.False(Together(groups, 0, 1));
        }

        [Fact]
        public void Igdb_ports_join_either_way()
        {
            var groups = SameGame.Group(new List<SameGame.Facts> { F("Original", 1, 10, 0, 20), F("Different Name", 2, igdb: 20) });
            Assert.True(Together(groups, 0, 1));
        }

        [Fact]
        public void A_screenscraper_id_joins_across_platforms_only()
        {
            var across = SameGame.Group(new List<SameGame.Facts> { F("A", 1, igdb: 1, ss: 5), F("B", 2, igdb: 2, ss: 5) });
            var same = SameGame.Group(new List<SameGame.Facts> { F("A", 1, igdb: 1, ss: 5), F("B", 1, igdb: 2, ss: 5) });
            Assert.True(Together(across, 0, 1));
            Assert.False(Together(same, 0, 1));
        }

        [Fact]
        public void Media_is_lent_only_where_missing()
        {
            var windows = new RomMRom
            {
                Id = 2, PlatformId = 2, Name = "Peace Walker - Master Collection Version",
                SSMetadata = new RomMSSMetadata { LogoPath = "own-logo.png" },
                MergedScreenshots = new List<string> { "igdb-shot.jpg" },
            };
            var psp = new RomMRom
            {
                Id = 1, PlatformId = 1, Name = "Peace Walker",
                SSMetadata = new RomMSSMetadata { FanartPath = "fanart.jpg", LogoPath = "psp-logo.png", VideoPath = "video.mp4" },
                MergedScreenshots = new List<string> { "psp-shot.jpg" },
            };
            Assert.Equal(1, SameGame.LendAcross(new List<RomMRom> { psp, windows }));
            Assert.Equal("fanart.jpg", windows.SSMetadata.FanartPath);
            Assert.Equal("video.mp4", windows.SSMetadata.VideoPath);
            Assert.Equal("own-logo.png", windows.SSMetadata.LogoPath);
            Assert.Equal(new[] { "igdb-shot.jpg" }, windows.MergedScreenshots);
            Assert.Equal("psp-logo.png", psp.SSMetadata.LogoPath);
        }
    }
}
