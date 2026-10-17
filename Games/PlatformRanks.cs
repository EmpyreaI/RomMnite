using System;
using System.Collections.Generic;

namespace RomM.Games
{
    /// <summary>
    /// Which platform's version of a game is the better one to offer: later and stronger home
    /// consoles over the handhelds of their time (PlayStation 2 over PSP), the original Xbox
    /// below PlayStation 2 and GameCube (its emulator, xemu, is the weakest of the three). By
    /// RomM's platform name (the Store) or Playnite's specification id (the library); 40 for
    /// a platform not listed.
    /// </summary>
    internal static class PlatformRanks
    {
        public const int Unknown = 40;

        public static readonly Dictionary<string, int> BySlug = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["win"] = 100, ["pc"] = 100, ["windows"] = 100,
            ["ps3"] = 92, ["xbox360"] = 91, ["wiiu"] = 89, ["switch"] = 88,
            ["wii"] = 82, ["ngc"] = 80, ["ps2"] = 79, ["xbox"] = 74,
            ["psvita"] = 72, ["dc"] = 70, ["3ds"] = 64, ["new-nintendo-3ds"] = 65,
            ["psp"] = 62, ["n64"] = 58, ["psx"] = 57, ["saturn"] = 56, ["segacd"] = 50,
            ["nds"] = 52, ["sega32"] = 47, ["snes"] = 46, ["genesis"] = 45, ["tg16"] = 44,
            ["turbografx-cd"] = 44, ["neogeoaes"] = 48, ["arcade"] = 49, ["gba"] = 42,
            ["nes"] = 36, ["sms"] = 35, ["gbc"] = 32, ["gamegear"] = 31, ["gb"] = 30,
            ["atari7800"] = 22, ["atari2600"] = 20, ["lynx"] = 21,
        };

        public static readonly Dictionary<string, int> BySpec = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["pc_windows"] = 100,
            ["sony_playstation3"] = 92, ["xbox360"] = 91, ["nintendo_wiiu"] = 89, ["nintendo_switch"] = 88,
            ["nintendo_wii"] = 82, ["nintendo_gamecube"] = 80, ["sony_playstation2"] = 79, ["xbox"] = 74,
            ["sony_vita"] = 72, ["sega_dreamcast"] = 70, ["nintendo_3ds"] = 64, ["sony_psp"] = 62,
            ["nintendo_64"] = 58, ["sony_playstation"] = 57, ["sega_saturn"] = 56, ["nintendo_ds"] = 52,
            ["sega_cd"] = 50, ["arcade"] = 49, ["sega_32x"] = 47, ["nintendo_super_nes"] = 46,
            ["sega_genesis"] = 45, ["nec_turbografx_16"] = 44, ["nec_turbografx_cd"] = 44,
            ["nintendo_gameboyadvance"] = 42, ["nintendo_nes"] = 36, ["sega_mastersystem"] = 35,
            ["nintendo_gameboycolor"] = 32, ["sega_gamegear"] = 31, ["nintendo_gameboy"] = 30,
            ["atari_7800"] = 22, ["atari_2600"] = 20, ["atari_lynx"] = 21,
        };

        // Playnite platforms made by name (as the importer makes them) have no specification id:
        // the name, letters and digits only, read as one ("PlayStation Portable", "Sony PSP").
        private static readonly Dictionary<string, string> NameToSpec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pc"] = "pc_windows", ["windows"] = "pc_windows", ["pcwindows"] = "pc_windows", ["microsoftwindows"] = "pc_windows",
            ["playstation"] = "sony_playstation", ["ps1"] = "sony_playstation", ["psx"] = "sony_playstation", ["psone"] = "sony_playstation",
            ["playstation2"] = "sony_playstation2", ["ps2"] = "sony_playstation2",
            ["playstation3"] = "sony_playstation3", ["ps3"] = "sony_playstation3",
            ["playstationportable"] = "sony_psp", ["psp"] = "sony_psp",
            ["playstationvita"] = "sony_vita", ["psvita"] = "sony_vita", ["vita"] = "sony_vita",
            ["xbox"] = "xbox", ["xbox360"] = "xbox360",
            ["gamecube"] = "nintendo_gamecube", ["wii"] = "nintendo_wii", ["wiiu"] = "nintendo_wiiu", ["switch"] = "nintendo_switch",
            ["nintendo64"] = "nintendo_64", ["n64"] = "nintendo_64",
            ["ds"] = "nintendo_ds", ["nintendods"] = "nintendo_ds", ["3ds"] = "nintendo_3ds", ["nintendo3ds"] = "nintendo_3ds", ["new3ds"] = "nintendo_3ds",
            ["gameboy"] = "nintendo_gameboy", ["gameboycolor"] = "nintendo_gameboycolor", ["gameboyadvance"] = "nintendo_gameboyadvance", ["gba"] = "nintendo_gameboyadvance",
            ["nes"] = "nintendo_nes", ["nintendoentertainmentsystem"] = "nintendo_nes", ["famicom"] = "nintendo_nes",
            ["snes"] = "nintendo_super_nes", ["supernintendo"] = "nintendo_super_nes", ["supernintendoentertainmentsystem"] = "nintendo_super_nes", ["superfamicom"] = "nintendo_super_nes",
            ["genesis"] = "sega_genesis", ["megadrive"] = "sega_genesis", ["genesismegadrive"] = "sega_genesis",
            ["saturn"] = "sega_saturn", ["dreamcast"] = "sega_dreamcast", ["mastersystem"] = "sega_mastersystem",
            ["gamegear"] = "sega_gamegear", ["segacd"] = "sega_cd", ["megacd"] = "sega_cd", ["32x"] = "sega_32x",
            ["turbografx16"] = "nec_turbografx_16", ["pcengine"] = "nec_turbografx_16", ["arcade"] = "arcade",
        };

        private static readonly string[] Makers = { "sony", "nintendo", "microsoft", "sega", "nec", "snk", "atari" };

        /// <summary>A Playnite platform's specification id, or the one its name stands for, or "".</summary>
        public static string SpecOf(string specificationId, string name)
        {
            if (!string.IsNullOrEmpty(specificationId))
                return specificationId;
            var key = new System.Text.StringBuilder();
            foreach (char c in name ?? "")
            {
                if (char.IsLetterOrDigit(c))
                    key.Append(char.ToLowerInvariant(c));
            }
            string k = key.ToString();
            if (NameToSpec.TryGetValue(k, out string spec))
                return spec;
            foreach (string maker in Makers)
            {
                if (k.StartsWith(maker) && NameToSpec.TryGetValue(k.Substring(maker.Length), out spec))
                    return spec;
            }
            return "";
        }

        public static int OfSlug(string slug) => !string.IsNullOrEmpty(slug) && BySlug.TryGetValue(slug, out int r) ? r : Unknown;

        public static int OfSpec(string spec) => !string.IsNullOrEmpty(spec) && BySpec.TryGetValue(spec, out int r) ? r : Unknown;
    }
}
