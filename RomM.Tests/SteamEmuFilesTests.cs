using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RomM.Games;
using Xunit;

namespace RomM.Tests
{
    public class SteamEmuFilesTests
    {
        private static Dictionary<string, SteamEmuFiles.Unlock> Parse(string name, string text) =>
            SteamEmuFiles.Parse(name, Encoding.UTF8.GetBytes(text));

        [Fact]
        public void Gbe_and_Goldberg_json()
        {
            var unlocks = Parse("achievements.json",
                "{ \"ACH_ONE\": { \"earned\": true, \"earned_time\": 1700000000 }, \"ACH_TWO\": { \"earned\": false, \"earned_time\": 0 } }");
            Assert.True(unlocks["ACH_ONE"].Achieved);
            Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), unlocks["ACH_ONE"].WhenUtc);
            Assert.False(unlocks["ACH_TWO"].Achieved);
        }

        [Fact]
        public void Codex_ini_with_a_section_per_achievement()
        {
            var unlocks = Parse("achievements.ini",
                "[SteamAchievements]\r\nCount=2\r\n[ACH_ONE]\r\nAchieved=1\r\nUnlockTime=1700000000\r\n[ACH_TWO]\r\nAchieved=0\r\nCurProgress=5\r\nMaxProgress=5\r\n");
            Assert.False(unlocks.ContainsKey("SteamAchievements"));
            Assert.True(unlocks["ACH_ONE"].Achieved);
            Assert.True(unlocks["ACH_TWO"].Achieved);   // done by progress
        }

        [Fact]
        public void Hoodlum_ini_with_flag_and_time_sections()
        {
            var unlocks = Parse("achievements.ini",
                "[Achievements]\nACH_ONE=1\nACH_TWO=0\n[AchievementsUnlockTimes]\nACH_ONE=1700000000\n");
            Assert.True(unlocks["ACH_ONE"].Achieved);
            Assert.NotNull(unlocks["ACH_ONE"].WhenUtc);
            Assert.False(unlocks["ACH_TWO"].Achieved);
        }

        [Fact]
        public void Reloaded_ini_with_little_endian_hex()
        {
            var unlocks = Parse("achievements.ini", "[ACH_ONE]\nState=01000000\nTime=00F15365\n");
            Assert.True(unlocks["ACH_ONE"].Achieved);
            Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), unlocks["ACH_ONE"].WhenUtc);
        }

        [Fact]
        public void Smartsteamemu_binary_by_crc()
        {
            uint crc = SteamEmuFiles.Crc32(Encoding.UTF8.GetBytes("ACH_ONE"));
            var data = new List<byte>();
            data.AddRange(BitConverter.GetBytes(1));
            var entry = new byte[24];
            BitConverter.GetBytes(crc).CopyTo(entry, 0);
            BitConverter.GetBytes(1700000000).CopyTo(entry, 8);
            BitConverter.GetBytes(1).CopyTo(entry, 20);
            data.AddRange(entry);
            var unlocks = SteamEmuFiles.Parse("stats.bin", data.ToArray(), new[] { "ACH_ONE", "ACH_TWO" });
            Assert.True(unlocks["ACH_ONE"].Achieved);
        }

        [Fact]
        public void Crc32_is_the_standard_one()
        {
            Assert.Equal(0xCBF43926u, SteamEmuFiles.Crc32(Encoding.ASCII.GetBytes("123456789")));
        }

        [Fact]
        public void Times_that_are_not_times_are_none()
        {
            Assert.Null(SteamEmuFiles.Time("0"));
            Assert.Null(SteamEmuFiles.Time("1700000"));
            Assert.NotNull(SteamEmuFiles.Time("1700000000000"));   // milliseconds
        }

        [Fact]
        public void The_settings_list_with_texts_by_language()
        {
            var list = SteamEmuFiles.ParseSettingsList(
                "[{ \"name\": \"ACH_ONE\", \"displayName\": { \"english\": \"First\", \"german\": \"Erste\" }, \"description\": \"Do it\", \"hidden\": 1, \"icon\": \"images/a.jpg\", \"icon_gray\": \"images/a_g.jpg\" }]");
            Assert.Equal("First", list[0].Title);
            Assert.True(list[0].Hidden);
            Assert.Equal("images/a_g.jpg", list[0].IconGray);
        }

        [Fact]
        public void Steams_list_in_either_layout()
        {
            var player = SteamEmuFiles.ParseSteamList(
                "{ \"response\": { \"achievements\": [ { \"internal_name\": \"ACH_ONE\", \"localized_name\": \"First\", \"localized_desc\": \"Do it\", \"icon\": \"a.jpg\", \"icon_gray\": \"b.jpg\", \"hidden\": false, \"player_percent_unlocked\": \"12.5\" } ] } }");
            var schema = SteamEmuFiles.ParseSteamList(
                "{ \"game\": { \"availableGameStats\": { \"achievements\": [ { \"name\": \"ACH_ONE\", \"displayName\": \"First\", \"hidden\": 1, \"icongray\": \"b.jpg\" } ] } } }");
            Assert.Equal("First", player[0].Title);
            Assert.Equal("b.jpg", player[0].IconGray);
            Assert.True(schema[0].Hidden);
            Assert.Equal(12.5, player[0].Percent);
            Assert.Null(schema[0].Percent);
        }

        [Fact]
        public void Gbe_local_save_path_is_looked_in_first()
        {
            string dll = Path.Combine(Path.GetTempPath(), "rommnite-steamemu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dll, "steam_settings"));
            try
            {
                File.WriteAllText(Path.Combine(dll, "steam_settings", "configs.user.ini"), "[user::saves]\nlocal_save_path=./saves\n");
                File.WriteAllText(Path.Combine(dll, "steam_settings", "steam_appid.txt"), "480\n");
                Assert.Equal(480, SteamEmuFiles.AppIdIn(dll));
                List<string> folders = SteamEmuFiles.SaveFolders(480, dll);
                Assert.Equal(Path.Combine(Path.GetFullPath(Path.Combine(dll, "./saves")), "480"), folders[0]);
            }
            finally
            {
                Directory.Delete(dll, true);
            }
        }
    }
}
