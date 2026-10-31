using System;
using System.Collections.Generic;
using System.Linq;
using RomM.Saves;
using Xunit;

namespace RomM.Tests
{
    public class SaveTrimTests
    {
        private const long MB = 1L << 20;

        // Cyberpunk 2077's saves as Ludusavi backs them up: a folder per save, alike inside.
        private static List<SaveTrim.Item> Cyberpunk(int saves)
        {
            const string root = "Cyberpunk 2077/drive-C/Users/Ryan/Saved Games/CD Projekt Red/Cyberpunk 2077/";
            var items = new List<SaveTrim.Item>
            {
                new SaveTrim.Item { Name = "Cyberpunk 2077/mapping.yaml", Size = 4096, Modified = new DateTime(2026, 10, 31) },
                new SaveTrim.Item { Name = root + "UserSettings.json", Size = 8192, Modified = new DateTime(2020, 1, 1) },
            };
            for (int i = 0; i < saves; i++)
            {
                DateTime when = new DateTime(2026, 1, 1).AddDays(i);
                items.Add(new SaveTrim.Item { Name = root + $"ManualSave-{i}/sav.dat", Size = 6 * MB, Modified = when });
                items.Add(new SaveTrim.Item { Name = root + $"ManualSave-{i}/metadata.9.json", Size = 2048, Modified = when });
                items.Add(new SaveTrim.Item { Name = root + $"ManualSave-{i}/screenshot.png", Size = 300 * 1024, Modified = when });
            }
            return items;
        }

        [Fact]
        public void Folders_alike_inside_are_slots_and_settings_are_not()
        {
            var slots = SaveTrim.FindSlots(Cyberpunk(10));
            Assert.Equal(10, slots.Count);
            Assert.All(slots, s => Assert.Contains("/ManualSave-", s.Path));
            Assert.DoesNotContain(slots.SelectMany(s => s.Items), i => i.Name.EndsWith("UserSettings.json") || i.Name.EndsWith("mapping.yaml"));
        }

        [Fact]
        public void The_oldest_go_first_and_the_newest_always_stays()
        {
            var items = Cyberpunk(10);
            var slots = SaveTrim.FindSlots(items);
            var dropped = SaveTrim.Choose(items, slots, 40 * MB);
            Assert.NotNull(dropped);
            Assert.Equal(new[] { "ManualSave-0", "ManualSave-1", "ManualSave-2", "ManualSave-3" },
                dropped.Select(s => s.Path.Substring(s.Path.LastIndexOf('/') + 1)).ToArray());

            // Room for less than one save: the newest is never left out, so it can't be done.
            Assert.Null(SaveTrim.Choose(items, slots, 3 * MB));
        }

        [Fact]
        public void A_few_small_files_are_never_taken_for_slots()
        {
            var items = new List<SaveTrim.Item>
            {
                new SaveTrim.Item { Name = "Game/save.dat", Size = 600 * MB, Modified = DateTime.Now },
                new SaveTrim.Item { Name = "Game/a.ini", Size = 1024, Modified = DateTime.Now },
                new SaveTrim.Item { Name = "Game/b.ini", Size = 1024, Modified = DateTime.Now },
                new SaveTrim.Item { Name = "Game/c.ini", Size = 1024, Modified = DateTime.Now },
            };
            Assert.Empty(SaveTrim.FindSlots(items));
        }

        [Fact]
        public void Files_left_out_come_off_Ludusavis_list()
        {
            string mapping = string.Join("\n",
                "name: Cyberpunk 2077",
                "drives:",
                "  drive-C: \"C:\"",
                "backups:",
                "  - name: \".\"",
                "    files:",
                "      \"C:/Users/Ryan/Saved Games/CD Projekt Red/Cyberpunk 2077/ManualSave-0/sav.dat\":",
                "        hash: aaa",
                "        size: 6",
                "      \"C:/Users/Ryan/Saved Games/CD Projekt Red/Cyberpunk 2077/ManualSave-1/sav.dat\":",
                "        hash: bbb",
                "        size: 6",
                "    registry:",
                "      hash: ~");
            var left = new HashSet<string> { "Cyberpunk 2077/drive-C/Users/Ryan/Saved Games/CD Projekt Red/Cyberpunk 2077/ManualSave-0/sav.dat" };
            string result = SaveTrim.WithoutFiles(mapping, "Cyberpunk 2077/", left);
            Assert.DoesNotContain("ManualSave-0", result);
            Assert.DoesNotContain("aaa", result);
            Assert.Contains("ManualSave-1/sav.dat", result);
            Assert.Contains("bbb", result);
            Assert.Contains("registry:", result);
        }

        [Fact]
        public void The_servers_limit_is_read_from_its_refusal()
        {
            Assert.Equal(536870912L, SaveTrim.LimitFrom("{\"detail\":\"Request body exceeds the maximum allowed size of 536870912 bytes\"}"));
            Assert.Null(SaveTrim.LimitFrom("Too large"));
        }
    }
}
