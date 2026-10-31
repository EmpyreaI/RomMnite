using System.Linq;
using RomM.Games;
using Xunit;

namespace RomM.Tests
{
    public class InstallFootprintTests
    {
        [Fact]
        public void Cue_names_its_tracks()
        {
            var named = InstallFootprint.Named(".cue", new[]
            {
                "FILE \"Game (Track 1).bin\" BINARY",
                "  TRACK 01 MODE2/2352",
                "    INDEX 01 00:00:00",
                "FILE Track2.bin BINARY",
            }).ToList();
            Assert.Equal(new[] { "Game (Track 1).bin", "Track2.bin" }, named);
        }

        [Fact]
        public void M3u_names_its_discs_not_comments()
        {
            var named = InstallFootprint.Named(".m3u", new[] { "#EXTM3U", "Disc 1.cue", "", "Disc 2.cue" }).ToList();
            Assert.Equal(new[] { "Disc 1.cue", "Disc 2.cue" }, named);
        }

        [Fact]
        public void Gdi_names_its_tracks()
        {
            var named = InstallFootprint.Named(".gdi", new[]
            {
                "3",
                "1 0 4 2352 track01.bin 0",
                "2 600 0 2352 \"track 02.raw\" 0",
                "3 45000 4 2352 track03.bin 0",
            }).ToList();
            Assert.Equal(new[] { "track01.bin", "track 02.raw", "track03.bin" }, named);
        }
    }
}
