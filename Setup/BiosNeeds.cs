using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Setup
{
    /// <summary>
    /// The BIOS and system files a platform's emulator cannot start a game without, as it looks
    /// for them, so an emulator is not set up for a platform whose files are missing (the user
    /// is told which, to upload them to the RomM server first). Optional files (better
    /// compatibility, a boot animation) are not listed: what is here stops games from starting.
    ///
    /// Some cores look in a folder of their own inside RetroArch's system folder (NeoCD in
    /// neocd\, Neko Project II Kai in np2kai\, PX68k in keropi\, SAME CDi in same_cdi\bios\): their files are put there.
    /// </summary>
    internal static class BiosNeeds
    {
        internal sealed class Need
        {
            /// <summary>What it is, for the user: the file(s) to upload.</summary>
            public string Label;
            /// <summary>File names that satisfy it (any one), case-insensitive.</summary>
            public string[] Names = new string[0];
            /// <summary>Or: any file of one of these sizes.</summary>
            public long[] Sizes = new long[0];
            /// <summary>Or: any file with this extension.</summary>
            public string Extension;
            /// <summary>Or: any file at all (the emulator recognises its BIOS by content, whatever its name).</summary>
            public bool AnyFile;
            /// <summary>The folder inside the emulator's BIOS folder it goes in; null for the BIOS folder itself.</summary>
            public string Folder;
            /// <summary>Installed already on this computer, by the emulator's own doing (firmware).</summary>
            public Func<string, bool> Installed;

            public bool IsMetBy(string name, long size)
            {
                if (string.IsNullOrEmpty(name))
                    return false;
                return AnyFile
                    || Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    || Sizes.Contains(size)
                    || (Extension != null && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static Need File(string label, params string[] names) => new Need { Label = label, Names = names };
        private static Need In(string folder, string label, params string[] names) => new Need { Label = label, Names = names, Folder = folder };

        private static readonly string[] NeoCdBios =
        {
            "neocd_f.rom", "neocd_sf.rom", "neocd_t.rom", "neocd_st.rom", "neocd_z.rom", "neocd_sz.rom",
            "front-sp1.bin", "top-sp1.bin", "neocd.bin", "uni-bioscd.rom",
        };

        // By Playnite platform: the emulator EmulatorSetup picks for it.
        private static readonly Dictionary<string, Need[]> Needs = new Dictionary<string, Need[]>(StringComparer.OrdinalIgnoreCase)
        {
            // DuckStation and PCSX2 tell their BIOS by its content, whatever it is called.
            ["sony_playstation"] = new[] { new Need { AnyFile = true, Label = "a PlayStation BIOS (e.g. scph5501.bin, scph1001.bin or scph7001.bin)" } },
            ["sony_playstation2"] = new[] { new Need { AnyFile = true, Label = "a PlayStation 2 BIOS (e.g. SCPH-70012.bin or scph39001.bin; a U.S. one is used first)" } },
            ["sony_playstation3"] = new[]
            {
                new Need
                {
                    Label = "the PS3 system software, PS3UPDAT.PUP", Names = new[] { "PS3UPDAT.PUP" },
                    Installed = dir => System.IO.File.Exists(Path.Combine(dir, "dev_flash", "vsh", "module", "vsh.self")),
                },
            },
            ["sony_vita"] = new[]
            {
                new Need
                {
                    Label = "the PS Vita system software, PSVUPDAT.PUP (and PSP2UPDAT.PUP, its fonts, if you have it)", Names = new[] { "PSVUPDAT.PUP" },
                    Installed = dir => Directory.Exists(Path.Combine(dir, "portable", "fs", "vs0")),
                },
            },
            // xemu: told apart by size and kind, as EmulatorFirstRun does.
            ["xbox"] = new[]
            {
                new Need { Label = "the Xbox MCPX boot ROM (mcpx_1.0.bin, 512 bytes)", Sizes = new long[] { 512 } },
                new Need { Label = "an Xbox BIOS (e.g. Complex_4627.bin, 256 KB or 1 MB)", Sizes = new long[] { 256 * 1024, 1024 * 1024 } },
                new Need { Label = "an Xbox hard disk image (xbox_hdd.qcow2)", Extension = ".qcow2" },
            },
            ["nintendo_switch"] = new[] { File("the Switch keys, prod.keys", "prod.keys") },
            // Cemu decrypts disc images (.wud, .wux) with the keys in keys.txt, in its data folder
            // (portable\ as set up here, where the platform's files go).
            ["nintendo_wiiu"] = new[] { File("Cemu's keys.txt (the Wii U common key and your games' disc keys)", "keys.txt") },
            // RetroArch's cores, by the names they look for.
            ["sega_saturn"] = new[] { File("a Saturn BIOS: sega_101.bin (Japan) or mpr-17933.bin (U.S./Europe)", "sega_101.bin", "mpr-17933.bin") },
            ["sega_cd"] = new[] { File("a Sega CD BIOS: bios_CD_U.bin, bios_CD_E.bin or bios_CD_J.bin", "bios_CD_U.bin", "bios_CD_E.bin", "bios_CD_J.bin") },
            ["nec_turbografx_cd"] = new[] { File("the CD-ROM System Card, syscard3.pce", "syscard3.pce") },
            ["nec_pcfx"] = new[] { File("the PC-FX BIOS, pcfx.rom", "pcfx.rom") },
            ["3do"] = new[]
            {
                File("a 3DO BIOS (e.g. panafz10.bin)", "panafz10.bin", "panafz10-norsa.bin", "panafz1.bin", "panafz1j.bin", "panafz1j-norsa.bin", "goldstar.bin", "sanyotry.bin"),
            },
            ["snk_neogeo_cd"] = new[]
            {
                In("neocd", "the Neo Geo CD Y-zoom ROM, 000-lo.lo", "000-lo.lo"),
                In("neocd", "a Neo Geo CD BIOS (e.g. neocd_f.rom or top-sp1.bin)", NeoCdBios),
            },
            ["nec_pc98"] = new[]
            {
                In("np2kai", "bios.rom", "bios.rom"),
                In("np2kai", "itf.rom", "itf.rom"),
                In("np2kai", "sound.rom", "sound.rom"),
                In("np2kai", "font.bmp (or FONT.ROM)", "font.bmp", "FONT.ROM"),
            },
            ["sharp_x68000"] = new[]
            {
                In("keropi", "iplrom.dat", "iplrom.dat"),
                In("keropi", "cgrom.dat", "cgrom.dat"),
            },
            ["atari_5200"] = new[] { File("the Atari 5200 BIOS, 5200.rom", "5200.rom") },
            ["atari_lynx"] = new[] { File("the Lynx boot ROM, lynxboot.img", "lynxboot.img") },
            ["atari_st"] = new[] { File("a TOS image, tos.img", "tos.img") },
            ["mattel_intellivision"] = new[] { File("exec.bin", "exec.bin"), File("grom.bin", "grom.bin") },
            ["coleco_vision"] = new[] { File("the ColecoVision BIOS, colecovision.rom", "colecovision.rom") },
            ["nintendo_famicom_disk"] = new[] { File("the Famicom Disk System BIOS, disksys.rom", "disksys.rom") },
            // SAME CDi (MAME's CD-i driver) looks for MAME's sets in system\same_cdi\bios
            // (same_cdi_libretro.info): cdimono1.zip is the one it needs, as a zip.
            ["philips_cdi"] = new[] { In(@"same_cdi\bios", "the CD-i BIOS, cdimono1.zip (MAME's set, zipped as it is)", "cdimono1.zip") },
        };

        // Files that go in a core's own folder without being needed (more of that core's set).
        private static readonly Dictionary<string, string> AlsoIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["2608_bd.wav"] = "np2kai", ["2608_hh.wav"] = "np2kai", ["2608_rim.wav"] = "np2kai",
            ["2608_sd.wav"] = "np2kai", ["2608_tom.wav"] = "np2kai", ["2608_top.wav"] = "np2kai",
            ["cdimono2.zip"] = @"same_cdi\bios", ["cdibios.zip"] = @"same_cdi\bios",
            ["iplrom30.dat"] = "keropi", ["iplromco.dat"] = "keropi", ["iplromxv.dat"] = "keropi",
        };

        public static IReadOnlyList<Need> For(string playnitePlatformId) =>
            playnitePlatformId != null && Needs.TryGetValue(playnitePlatformId, out Need[] needs) ? needs : new Need[0];

        /// <summary>The folder (inside the emulator's BIOS folder) a file goes in, or "" for the BIOS folder itself.</summary>
        public static string FolderFor(string playnitePlatformId, string fileName)
        {
            Need need = For(playnitePlatformId).FirstOrDefault(n => n.Folder != null && n.Names.Any(x => string.Equals(x, fileName, StringComparison.OrdinalIgnoreCase)));
            if (need != null)
                return need.Folder;
            return AlsoIn.TryGetValue(fileName ?? "", out string folder) && For(playnitePlatformId).Any(n => n.Folder == folder) ? folder : "";
        }

        /// <summary>
        /// What is missing: the needs met neither by a file on the server (name and size) nor by
        /// one already in place on this computer.
        /// </summary>
        /// <param name="emulatorDir">The emulator's folder when it is already installed, else null.</param>
        /// <param name="biosFolder">Its BIOS folder, relative to that (null: none).</param>
        public static List<Need> Missing(string playnitePlatformId, IEnumerable<Tuple<string, long>> serverFiles, string emulatorDir, string biosFolder) =>
            Check(playnitePlatformId, serverFiles, emulatorDir, biosFolder).Where(c => !c.Item2).Select(c => c.Item1).ToList();

        /// <summary>
        /// Every need of the platform, in order, with whether it is met (by a file on the server,
        /// or one already in place on this computer) -- for showing what was found and what not.
        /// </summary>
        public static List<Tuple<Need, bool>> Check(string playnitePlatformId, IEnumerable<Tuple<string, long>> serverFiles, string emulatorDir, string biosFolder)
        {
            var files = (serverFiles ?? Enumerable.Empty<Tuple<string, long>>()).ToList();
            var result = new List<Tuple<Need, bool>>();
            foreach (Need need in For(playnitePlatformId))
                result.Add(Tuple.Create(need, IsMet(need, files, emulatorDir, biosFolder)));
            return result;
        }

        private static bool IsMet(Need need, List<Tuple<string, long>> files, string emulatorDir, string biosFolder)
        {
            if (files.Any(f => need.IsMetBy(f.Item1, f.Item2)))
                return true;
            if (emulatorDir == null || !Directory.Exists(emulatorDir))
                return false;
            if (need.Installed != null && need.Installed(emulatorDir))
                return true;
            string local = biosFolder == null ? null : Path.Combine(emulatorDir, biosFolder, need.Folder ?? "");
            return local != null && Directory.Exists(local)
                && Directory.GetFiles(local).Select(p => new FileInfo(p)).Any(f => need.IsMetBy(f.Name, f.Length));
        }
    }
}
