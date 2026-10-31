using System;
using System.IO;
using System.Linq;
using RomM.Saves;
using Xunit;

namespace RomM.Tests
{
    public class LudusaviManifestTests
    {
        // As Ludusavi's manifest has them (a quoted title, tags before when, a Steam-only path).
        private static readonly string[] Manifest =
        {
            "Metal Gear Solid - Master Collection Version:",
            "  files:",
            "    \"<base>/other\":",
            "      tags:",
            "        - save",
            "\"Metal Gear Solid: Peace Walker - Master Collection Version\":",
            "  cloud:",
            "    steam: true",
            "  files:",
            "    \"<base>/mgspw_savedata_win/<storeUserId>\":",
            "      tags:",
            "        - config",
            "        - save",
            "      when:",
            "        - os: windows",
            "    \"<base>/linux_only\":",
            "      when:",
            "        - os: linux",
            "    \"<root>/userdata/<storeUserId>/2492660/remote\":",
            "      when:",
            "        - os: windows",
            "          store: steam",
            "  installDir:",
            "    MGS_PW: {}",
            "  steam:",
            "    id: 2492660",
            "Metal Gear Survive:",
            "  files:",
            "    \"<base>/survive\": {}",
        };

        [Fact]
        public void Reads_the_games_base_paths_for_Windows_and_its_install_folder()
        {
            LudusaviManifest.Entry entry = LudusaviManifest.Read(Manifest, "Metal Gear Solid: Peace Walker - Master Collection Version");
            Assert.Equal(new[] { "mgspw_savedata_win/<storeUserId>" }, entry.BasePaths);
            Assert.Equal(new[] { "MGS_PW" }, entry.InstallDirs);
        }

        [Fact]
        public void Reads_what_Steam_starts_it_with_on_Windows_only()
        {
            // As the manifest has Cyberpunk 2077's (a Mac app and a Windows launcher).
            string[] manifest =
            {
                "Cyberpunk 2077:",
                "  files:",
                "    \"<base>/save\": {}",
                "  launch:",
                "    \"<base>/Cyberpunk2077.app\":",
                "      - when:",
                "          - os: mac",
                "            store: steam",
                "    \"<base>/redprelauncher.exe\":",
                "      - when:",
                "          - os: windows",
                "            store: steam",
                "    \"<base>/bin/x64/Cyberpunk2077.exe\":",
                "      - arguments: \"--launcher-skip\"",
                "  steam:",
                "    id: 1091500",
            };
            LudusaviManifest.Entry entry = LudusaviManifest.Read(manifest, "Cyberpunk 2077");
            Assert.Equal(new[] { "redprelauncher.exe", "bin/x64/Cyberpunk2077.exe" }, entry.LaunchPaths);
            Assert.Equal(new[] { "save" }, entry.BasePaths);
        }

        [Fact]
        public void An_unknown_title_has_no_entry()
        {
            Assert.Null(LudusaviManifest.Read(Manifest, "Metal Gear Solid: Peace Walker"));
        }

        [Fact]
        public void Finds_the_files_with_any_user_id_and_the_base_folder_holding_them()
        {
            string root = Path.Combine(Path.GetTempPath(), "romm-manifest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string saves = Path.Combine(root, "mgspw_savedata_win", "76561197960287930");
                Directory.CreateDirectory(saves);
                File.WriteAllText(Path.Combine(saves, "SAVE.dat"), "x");
                Directory.CreateDirectory(Path.Combine(root, "mgspw"));

                LudusaviManifest.Entry entry = LudusaviManifest.Read(Manifest, "Metal Gear Solid: Peace Walker - Master Collection Version");
                string baseFolder = LudusaviManifest.BaseFolder(root, entry);
                Assert.Equal(root, baseFolder);
                var files = LudusaviManifest.Files(baseFolder, entry);
                Assert.Equal(new[] { Path.Combine("mgspw_savedata_win", "76561197960287930", "SAVE.dat") }, files);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
