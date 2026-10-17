using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Models;
using RomM.Games;
using RomM.Models.RomM.Rom;
using Xunit;

namespace RomM.Tests
{
    public class RomMMetadataMapperTests
    {
        private const string Host = "https://romm.example.com";
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Minimal rom with the non-null members BuildBaseMetadata dereferences.
        private static RomMRom NewRom()
        {
            return new RomMRom
            {
                Name = "Advance Wars",
                Summary = "A turn-based strategy game.",
                Metadatum = new metadatum(),
                RomUser = new RomMRomUser(),
                Regions = new List<string>(),
            };
        }

        private static string[] Names(IEnumerable<MetadataProperty> props)
        {
            if (props == null)
                return new string[0];

            return props.OfType<MetadataNameProperty>().Select(p => p.Name).OrderBy(n => n).ToArray();
        }

        private static GameMetadata Map(RomMRom rom, string board = "ESRB")
        {
            return RomMMetadataMapper.BuildBaseMetadata(rom, Host, board);
        }

        [Fact]
        public void Maps_name_and_description()
        {
            var md = Map(NewRom());

            Assert.Equal("Advance Wars", md.Name);
            Assert.Equal("A turn-based strategy game.", md.Description);
        }

        [Fact]
        public void Background_uses_fanart_path_under_resource_mount()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata
            {
                FanartPath = "roms/2/32/fanart/fanart.png",
                FanartUrl = "https://ss.example/fanart.png",
            };

            var md = Map(rom);

            Assert.NotNull(md.BackgroundImage);
            Assert.Equal(Host + "/assets/romm/resources/roms/2/32/fanart/fanart.png", md.BackgroundImage.Path);
        }

        [Fact]
        public void Background_falls_back_to_fanart_url_when_no_path()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata { FanartUrl = "https://ss.example/fanart.png" };

            Assert.Equal("https://ss.example/fanart.png", Map(rom).BackgroundImage.Path);
        }

        [Fact]
        public void Background_is_null_without_fanart()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata { MiximagePath = "roms/2/32/miximage/miximage.png" };

            Assert.Null(Map(rom).BackgroundImage);
            Assert.Null(Map(NewRom()).BackgroundImage);
        }

        [Fact]
        public void Logo_url_prefers_romm_hosted_path()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata
            {
                LogoPath = "roms/2/32/logo/logo.png",
                LogoUrl = "https://ss.example/wheel.png",
            };

            var url = RomMMetadataMapper.LogoUrl(Host, rom, out bool servedByRomM);

            Assert.Equal(Host + "/assets/romm/resources/roms/2/32/logo/logo.png", url);
            Assert.True(servedByRomM);
        }

        [Fact]
        public void Logo_url_falls_back_to_external_url()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata { LogoUrl = "https://ss.example/wheel.png" };

            var url = RomMMetadataMapper.LogoUrl(Host, rom, out bool servedByRomM);

            Assert.Equal("https://ss.example/wheel.png", url);
            Assert.False(servedByRomM);
        }

        [Fact]
        public void Logo_url_is_null_without_logo()
        {
            Assert.Null(RomMMetadataMapper.LogoUrl(Host, NewRom(), out bool _));

            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata { FanartUrl = "https://ss.example/fanart.png" };
            Assert.Null(RomMMetadataMapper.LogoUrl(Host, rom, out bool _));
        }

        [Fact]
        public void Description_appends_box_front_and_back_side_by_side()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata
            {
                Box2dPath = "roms/2/32/box2d/box2d.png",
                Box2dBackUrl = "https://ss.example/back.png?a=1&b=2",
            };

            Assert.Equal(
                "A turn-based strategy game.<br><br>"
                + "<img src=\"" + Host + "/assets/romm/resources/roms/2/32/box2d/box2d.png\" style=\"width:49%\">"
                + "<img src=\"https://ss.example/back.png?a=1&amp;b=2\" style=\"width:49%\">",
                Map(rom).Description);
        }

        [Fact]
        public void Description_is_only_box_art_without_summary()
        {
            var rom = NewRom();
            rom.Summary = null;
            rom.SSMetadata = new RomMSSMetadata { Box2dBackPath = "roms/2/32/box2d_back/box2d_back.png" };

            Assert.Equal(
                "<img src=\"" + Host + "/assets/romm/resources/roms/2/32/box2d_back/box2d_back.png\" style=\"width:49%\">",
                Map(rom).Description);
        }

        [Fact]
        public void Description_leaves_out_box_art_that_does_not_load()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata
            {
                Box2dPath = "roms/2/32/box2d/box2d.png",
                Box2dBackUrl = "https://ss.example/back.png",
            };

            var md = RomMMetadataMapper.BuildBaseMetadata(rom, Host, "ESRB", url => url.StartsWith(Host));

            Assert.Equal(
                "A turn-based strategy game.<br><br>"
                + "<img src=\"" + Host + "/assets/romm/resources/roms/2/32/box2d/box2d.png\" style=\"width:49%\">",
                md.Description);

            var none = RomMMetadataMapper.BuildBaseMetadata(rom, Host, "ESRB", url => false);
            Assert.Equal("A turn-based strategy game.", none.Description);
        }

        [Fact]
        public void Manual_is_a_details_link_not_part_of_the_description()
        {
            var rom = NewRom();
            rom.HasManual = true;
            rom.ManualPath = "roms/2/32/manual/32.pdf";

            var md = Map(rom);

            Assert.Equal("A turn-based strategy game.", md.Description);
            var manual = Assert.Single(md.Links, l => l.Name == "Manual");
            Assert.Equal(Host + "/assets/romm/resources/roms/2/32/manual/32.pdf", manual.Url);
        }

        [Fact]
        public void No_manual_link_without_a_manual()
        {
            Assert.DoesNotContain(Map(NewRom()).Links, l => l.Name == "Manual");
        }

        [Fact]
        public void Description_is_untouched_without_box_art()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata { FanartUrl = "https://ss.example/fanart.png" };

            Assert.Equal("A turn-based strategy game.", Map(rom).Description);
        }

        [Fact]
        public void Background_falls_back_to_first_stored_screenshot()
        {
            var rom = NewRom();
            rom.MergedScreenshots = new List<string>
            {
                "/assets/romm/resources/roms/2/32/screenshots/0.jpg",
                "/assets/romm/resources/roms/2/32/screenshots/1.jpg",
            };
            rom.SSMetadata = new RomMSSMetadata { ScreenshotUrl = "https://ss.example/ss.png" };

            Assert.Equal(Host + "/assets/romm/resources/roms/2/32/screenshots/0.jpg", Map(rom).BackgroundImage.Path);
        }

        [Fact]
        public void Background_falls_back_to_external_screenshot_url()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata { ScreenshotUrl = "https://ss.example/ss.png" };

            Assert.Equal("https://ss.example/ss.png", Map(rom).BackgroundImage.Path);
        }

        [Fact]
        public void Background_prefers_fanart_over_screenshots()
        {
            var rom = NewRom();
            rom.MergedScreenshots = new List<string> { "/assets/romm/resources/roms/2/32/screenshots/0.jpg" };
            rom.SSMetadata = new RomMSSMetadata { FanartUrl = "https://ss.example/fanart.png" };

            Assert.Equal("https://ss.example/fanart.png", Map(rom).BackgroundImage.Path);
        }

        [Fact]
        public void Icon_prefers_logo_over_cover()
        {
            var rom = NewRom();
            rom.PathCoverL = "/assets/romm/resources/roms/2/32/cover/big.png?ts=1";
            rom.SSMetadata = new RomMSSMetadata { LogoPath = "roms/2/32/logo/logo.png" };

            Assert.Equal(Host + "/assets/romm/resources/roms/2/32/logo/logo.png", Map(rom).Icon.Path);
        }

        [Fact]
        public void Icon_falls_back_to_cover_without_logo()
        {
            var rom = NewRom();
            rom.PathCoverL = "/assets/romm/resources/roms/2/32/cover/big.png?ts=1";
            rom.SSMetadata = new RomMSSMetadata { MiximagePath = "roms/2/32/miximage/miximage.png" };

            Assert.Equal(Host + "/assets/romm/resources/roms/2/32/cover/big.png?ts=1", Map(rom).Icon.Path);

            RomMMetadataMapper.IconUrl(Host, rom, out bool servedByRomM);
            Assert.True(servedByRomM);
        }

        [Fact]
        public void Icon_never_uses_the_miximage()
        {
            var rom = NewRom();
            rom.SSMetadata = new RomMSSMetadata
            {
                MiximagePath = "roms/2/32/miximage/miximage.png",
                MiximageUrl = "https://ss.example/mix.png",
            };

            Assert.Null(Map(rom).Icon);
        }

        [Fact]
        public void Icon_is_null_without_logo_or_cover()
        {
            Assert.Null(Map(NewRom()).Icon);
        }

        [Fact]
        public void Cover_uses_host_plus_path_without_resource_mount()
        {
            var rom = NewRom();
            rom.PathCoverL = "/assets/romm/resources/roms/2/32/cover/big.png";

            var md = Map(rom);

            Assert.NotNull(md.CoverImage);
            Assert.Equal(Host + "/assets/romm/resources/roms/2/32/cover/big.png", md.CoverImage.Path);
        }

        [Fact]
        public void Cover_is_null_without_path()
        {
            Assert.Null(Map(NewRom()).CoverImage);
        }

        [Fact]
        public void Maps_collection_fields_and_skips_empty_strings()
        {
            var rom = NewRom();
            rom.Metadatum.Genres = new List<string> { "Strategy", "", "Tactics" };
            rom.Metadatum.Franchises = new List<string> { "Nintendo Wars" };
            rom.Metadatum.Gamemodes = new List<string> { "Single player" };
            rom.Metadatum.Collections = new List<string> { "Advance Wars" };
            rom.Regions = new List<string> { "USA", "" };

            var md = Map(rom);

            Assert.Equal(new[] { "Strategy", "Tactics" }, Names(md.Genres));
            Assert.Equal(new[] { "Advance Wars" }, Names(md.Series));
            Assert.Equal(new[] { "Single player" }, Names(md.Features));
            Assert.Equal(new[] { "Advance Wars" }, Names(md.Categories));
            Assert.Equal(new[] { "USA" }, Names(md.Regions));
        }

        [Fact]
        public void Series_are_collections_not_wider_franchises()
        {
            var rom = NewRom();
            rom.Name = "Kingdom Hearts II";
            rom.Metadatum.Franchises = new List<string> { "Disney", "Donald Duck", "Final Fantasy" };
            rom.Metadatum.Collections = new List<string> { "Kingdom Hearts" };

            Assert.Equal(new[] { "Kingdom Hearts" }, Names(Map(rom).Series));
        }

        [Fact]
        public void Sub_series_fold_into_their_series_but_not_inside_a_word()
        {
            var rom = NewRom();
            rom.Name = "The Legend of Zelda: Four Swords Adventures";
            rom.Metadatum.Collections = new List<string> { "The Legend of Zelda", "The Legend of Zelda: Four Swords", "WarioWare", "Wario" };

            Assert.Equal(new[] { "The Legend of Zelda", "Wario", "WarioWare" }, Names(Map(rom).Series));
        }

        [Fact]
        public void Without_collections_only_franchises_the_title_names_are_series()
        {
            var rom = NewRom();
            rom.Name = "Kingdom Hearts 358/2 Days";
            rom.Metadatum.Collections = new List<string>();
            rom.Metadatum.Franchises = new List<string> { "Disney", "Goofy", "Kingdom Hearts", "Final Fantasy" };

            Assert.Equal(new[] { "Kingdom Hearts" }, Names(Map(rom).Series));
        }

        [Fact]
        public void Series_fall_back_to_franchises_without_collections()
        {
            var rom = NewRom();
            rom.Metadatum.Franchises = new List<string> { "Advance Wars" };
            rom.Metadatum.Collections = new List<string>();

            Assert.Equal(new[] { "Advance Wars" }, Names(Map(rom).Series));
        }

        [Fact]
        public void Age_ratings_keep_only_preferred_board()
        {
            var rom = NewRom();
            rom.Metadatum.Age_Ratings = new List<string> { "ESRB:Everyone", "PEGI:3" };

            var md = Map(rom, board: "ESRB");

            Assert.Equal(new[] { "ESRB:Everyone" }, Names(md.AgeRatings));
        }

        [Fact]
        public void Age_ratings_null_when_none_present()
        {
            Assert.Null(Map(NewRom()).AgeRatings);
        }

        [Fact]
        public void Builds_external_links_for_present_ids()
        {
            var rom = NewRom();
            rom.SSId = 190935;
            rom.HasheousId = 42;
            rom.RAId = 7;
            rom.HLTBId = 99;

            var md = Map(rom);

            Assert.Equal(4, md.Links.Count);
            Assert.Contains(md.Links, l => l.Name == "Screenscraper" && l.Url.Contains("gameid=190935"));
            Assert.Contains(md.Links, l => l.Name == "Hasheous" && l.Url.Contains("id=42"));
            Assert.Contains(md.Links, l => l.Name == "RetroAchievements" && l.Url.EndsWith("/game/7"));
            Assert.Contains(md.Links, l => l.Name == "HowLongToBeat" && l.Url.EndsWith("/game/99"));
        }

        [Fact]
        public void No_links_when_no_ids()
        {
            Assert.Empty(Map(NewRom()).Links);
        }

        [Fact]
        public void Maps_scores_and_last_activity()
        {
            var rom = NewRom();
            rom.Metadatum.Average_Rating = 7.5f;
            rom.RomUser.Rating = 8;
            rom.RomUser.LastPlayed = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            var md = Map(rom);

            Assert.Equal(7, md.CommunityScore);  // 1-10 float truncated to int
            Assert.Equal(80, md.UserScore);      // 1-10 rating scaled to Playnite's 1-100
            Assert.Equal(rom.RomUser.LastPlayed, md.LastActivity);
        }

        [Fact]
        public void Maps_release_date_from_epoch_milliseconds()
        {
            var rom = NewRom();
            long ms = 1_700_000_000_000L;
            rom.Metadatum.Release_Date = ms;

            var md = Map(rom);

            // Recompute identically (incl. ToLocalTime) so the assertion is timezone-stable.
            var expected = new ReleaseDate(UnixEpoch.AddMilliseconds(ms).ToLocalTime());
            Assert.True(md.ReleaseDate.HasValue);
            Assert.Equal(expected, md.ReleaseDate.Value);
        }
    }
}
