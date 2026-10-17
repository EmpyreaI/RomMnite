using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using RomM.Models.RomM.Platform;
using RomM.Settings;
using SharpCompress.Archives;
using SharpCompress.Readers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;

namespace RomM.Setup
{
    /// <summary>
    /// Sets up an emulator for a RomM platform nothing plays yet, so its games can be added from
    /// the Store: downloads the emulator from its project's own releases, unpacks it portable
    /// under Playnite\Emulators\&lt;name&gt;, registers it as Playnite's own "Import emulators" would
    /// (its built-in definition and the platform's profile), maps the RomM platform to it, and
    /// puts the platform's BIOS files from the RomM server where the emulator looks for them.
    /// An emulator already in Playnite for the platform is used as it is (only the profile and
    /// the mapping are added). Playnite's own "Download emulators" only lists websites.
    ///
    /// Which emulator: DuckStation (PS1), PCSX2 (PS2), PPSSPP (PSP), Dolphin (GameCube, Wii),
    /// RPCS3 (PS3), Xenia Canary (Xbox 360), Flycast (Dreamcast), Ruffle (Flash / browser),
    /// Azahar (3DS), Cemu (Wii U), xemu (Xbox), Vita3K (PS Vita), Eden (Switch; from the RomM
    /// server only), RetroArch with a core for the rest. RetroArch's cores, too, come from the
    /// RomM server when put there (emulator-libretro-&lt;core&gt;...zip), else from libretro's buildbot.
    /// </summary>
    internal sealed class EmulatorSetup
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly HttpClient s_web = CreateClient();

        // How to get and prepare each emulator. Download file names follow each project's
        // releases as they are; when a project renames them, its pattern here is what changes.
        internal sealed class Recipe
        {
            public string DefinitionId;      // Playnite's built-in emulator definition
            public string Folder;            // under Playnite\Emulators
            public string Source;            // for the confirmation and the log
            public Func<string> Url;         // the archive to download
            public string[] PortableFiles = new string[0];
            public string[] PortableFolders = new string[0];
            public string BiosFolder;        // relative to the executable's folder; null: no BIOS needed
            public string Profile;           // the definition's profile for today's builds (else whichever matches)
            public bool Firmware;            // RPCS3, Vita3K: the console's firmware (.pup) is installed by the emulator itself
        }

        private static readonly Dictionary<string, Recipe> Recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase)
        {
            ["duckstation"] = new Recipe
            {
                DefinitionId = "duckstation", Folder = "DuckStation", Source = "github.com/stenzek/duckstation",
                Url = () => GitHubAsset("stenzek/duckstation", @"windows.*x64.*\.(zip|7z)$", @"symbols|debug|arm64|installer|sse2|pdb", true),
                PortableFiles = new[] { "portable.txt" }, BiosFolder = "bios",
            },
            ["pcsx2"] = new Recipe
            {
                DefinitionId = "pcsx2", Folder = "PCSX2", Source = "github.com/PCSX2/pcsx2",
                Url = () => GitHubAsset("PCSX2/pcsx2", @"windows.*x64.*\.(7z|zip)$", @"symbols|debug|arm64|installer|pdb", false),
                PortableFiles = new[] { "portable.ini", "portable.txt" }, BiosFolder = "bios", Profile = "Default QT",
            },
            ["ppsspp"] = new Recipe
            {
                DefinitionId = "ppsspp", Folder = "PPSSPP", Source = "ppsspp.org",
                Url = PpssppUrl, Profile = "64bit",
                PortableFolders = new[] { "memstick" },
            },
            ["dolphin"] = new Recipe
            {
                DefinitionId = "dolphin", Folder = "Dolphin", Source = "dolphin-emu.org",
                Url = DolphinUrl,
                PortableFiles = new[] { "portable.txt" },
            },
            ["rpcs3"] = new Recipe
            {
                DefinitionId = "rpcs3", Folder = "RPCS3", Source = "github.com/RPCS3/rpcs3-binaries-win",
                Url = () => GitHubAsset("RPCS3/rpcs3-binaries-win", @"win64.*\.7z$", @"symbols|debug|pdb", true),
                Firmware = true,
            },
            ["xenia"] = new Recipe
            {
                DefinitionId = "xenia", Folder = "Xenia Canary", Source = "github.com/xenia-canary",
                // Builds were published in xenia-canary-releases, and are now in the main repository
                // (that one's last build there is February 2026's): the newer of the two.
                Url = () => GitHubAsset(new[] { "xenia-canary/xenia-canary", "xenia-canary/xenia-canary-releases" },
                                        @"windows.*\.(zip|7z)$", @"netplay|symbols|pdb|linux", true),
                PortableFiles = new[] { "portable.txt" }, Profile = "Canary",
            },
            ["flycast"] = new Recipe
            {
                DefinitionId = "flycast", Folder = "Flycast", Source = "github.com/flyinghead/flycast",
                Url = () => GitHubAsset("flyinghead/flycast", @"win.*64.*\.(zip|7z)$", @"arm|uwp|libretro|symbols|pdb", false),
                BiosFolder = "data",
            },
            ["ruffle"] = new Recipe
            {
                // Flash, as RomM plays its "browser" and "flash" platforms in the browser. The
                // Windows build is ruffle.exe alone (release.yml: ruffle-<version|nightly-date>-windows-x86_64.zip);
                // its settings and saves are kept beside it by its arguments (EmulatorLaunch).
                DefinitionId = "ruffle", Folder = "Ruffle", Source = "github.com/ruffle-rs/ruffle",
                Url = () => GitHubAsset("ruffle-rs/ruffle", @"windows-x86_64\.zip$", @"arm64|msi|selfhosted|extension", true),
            },
            ["azahar"] = new Recipe
            {
                // azahar-windows-msvc-<tag>.zip (.ci/pack.sh); portable with a "user" folder beside it.
                DefinitionId = "azahar", Folder = "Azahar", Source = "github.com/azahar-emu/azahar",
                Url = () => GitHubAsset("azahar-emu/azahar", @"windows-msvc.*\.zip$", @"mxe|installer|pdb|debug", false),
                PortableFolders = new[] { "user" },
            },
            ["cemu"] = new Recipe
            {
                // cemu-<version>-windows-x64.zip (deploy_release.yml); portable with a "portable"
                // folder, where it also reads keys.txt (the platform's files from the server go there).
                DefinitionId = "cemu", Folder = "Cemu", Source = "github.com/cemu-project/Cemu",
                Url = () => GitHubAsset("cemu-project/Cemu", @"windows-x64\.zip$", @"pdb|debug", true),
                PortableFolders = new[] { "portable" }, BiosFolder = "portable",
            },
            ["xemu"] = new Recipe
            {
                // xemu-win-x86_64-release.zip (release.yml); portable with xemu.toml beside it, which
                // names the BIOS, boot ROM and hard disk image from the server (EmulatorLaunch).
                DefinitionId = "xemu", Folder = "xemu", Source = "github.com/xemu-project/xemu",
                Url = () => GitHubAsset("xemu-project/xemu", @"win-x86_64-release\.zip$|windows-x86_64\.zip$", @"pdb|aarch64|arm64", false),
                BiosFolder = "bios",
            },
            ["vita3k"] = new Recipe
            {
                // The "continuous" release's windows-latest.zip (.ci/release-collect.sh); portable with
                // a "portable" folder. The firmware (.pup from the server) is installed by
                // Vita3K --firmware, which installs it and exits.
                DefinitionId = "vita3k", Folder = "Vita3K", Source = "github.com/Vita3K/Vita3K",
                Url = () => GitHubAsset("Vita3K/Vita3K", @"^windows-latest\.zip$", @"arm64", true),
                PortableFolders = new[] { "portable" }, Firmware = true,
            },
            ["eden"] = new Recipe
            {
                // Eden publishes no download this can find by itself: put a copy on the RomM server
                // (emulator-eden....zip). Portable with a "user" folder; prod.keys and title.keys
                // from the server go in user\keys.
                DefinitionId = "eden", Folder = "Eden", Source = "eden-emu.dev (put a copy on your RomM server)",
                Url = null,
                PortableFolders = new[] { "user" }, BiosFolder = @"user\keys",
            },
            ["bigpemu"] = new Recipe
            {
                // Jaguar and Jaguar CD (RetroArch's Virtual Jaguar plays no CDs). Its builds are on
                // its author's site, linked from the download page.
                DefinitionId = "bigpemu", Folder = "BigPEmu", Source = "richwhitehouse.com/jaguar",
                Url = BigPEmuUrl,
            },
            ["retroarch"] = new Recipe
            {
                DefinitionId = "retroarch", Folder = "RetroArch", Source = "buildbot.libretro.com",
                Url = RetroArchUrl,
                BiosFolder = "system",
            },
        };

        // The emulator for each of Playnite's platforms; the rest go to RetroArch when it has a core.
        private static readonly Dictionary<string, string> EmulatorFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sony_playstation"] = "duckstation",
            ["sony_playstation2"] = "pcsx2",
            ["sony_psp"] = "ppsspp",
            ["nintendo_gamecube"] = "dolphin",
            ["nintendo_wii"] = "dolphin",
            ["sony_playstation3"] = "rpcs3",
            ["xbox360"] = "xenia",
            ["sega_dreamcast"] = "flycast",
            ["adobe_flash"] = "ruffle",
            ["nintendo_3ds"] = "azahar",
            ["nintendo_wiiu"] = "cemu",
            ["xbox"] = "xemu",
            ["sony_vita"] = "vita3k",
            ["nintendo_switch"] = "eden",
            ["atari_jaguar"] = "bigpemu",
        };

        // Platforms whose games are played as the archives they come in (arcade sets, DOSBox
        // Pure's zipped DOS games): their mappings do not extract them.
        private static readonly HashSet<string> KeepArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "arcade", "pc_dos", "snk_neogeo_aes",
        };

        // RetroArch's core per platform, where there is a clear first choice (by core file name);
        // otherwise the first core Playnite's RetroArch definition lists for the platform.
        private static readonly Dictionary<string, string> PreferredCore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nintendo_nes"] = "mesen_libretro",
            ["nintendo_famicom_disk"] = "mesen_libretro",
            ["nintendo_super_nes"] = "snes9x_libretro",
            ["nintendo_64"] = "mupen64plus_next_libretro",
            ["nintendo_gameboy"] = "gambatte_libretro",
            ["nintendo_gameboycolor"] = "gambatte_libretro",
            ["nintendo_gameboyadvance"] = "mgba_libretro",
            ["nintendo_ds"] = "melondsds_libretro",
            ["nintendo_virtualboy"] = "mednafen_vb_libretro",
            ["sega_genesis"] = "genesis_plus_gx_libretro",
            ["sega_mastersystem"] = "genesis_plus_gx_libretro",
            ["sega_gamegear"] = "genesis_plus_gx_libretro",
            ["sega_cd"] = "genesis_plus_gx_libretro",
            ["sega_sg1000"] = "genesis_plus_gx_libretro",
            ["sega_32x"] = "picodrive_libretro",
            ["sega_saturn"] = "mednafen_saturn_libretro",
            ["nec_turbografx_16"] = "mednafen_pce_fast_libretro",
            ["nec_turbografx_cd"] = "mednafen_pce_fast_libretro",
            ["nec_supergrafx"] = "mednafen_supergrafx_libretro",
            ["3do"] = "opera_libretro",
            ["commodore_64"] = "vice_x64sc_libretro",
            ["commodore_amiga"] = "puae_libretro",
            ["pc_dos"] = "dosbox_pure_libretro",
            ["nec_pc98"] = "np2kai_libretro",
            ["atari_2600"] = "stella_libretro",
            ["atari_7800"] = "prosystem_libretro",
            ["atari_lynx"] = "handy_libretro",
            ["bandai_wonderswan"] = "mednafen_wswan_libretro",
            ["bandai_wonderswan_color"] = "mednafen_wswan_libretro",
            ["snk_neogeopocket"] = "mednafen_ngp_libretro",
            ["snk_neogeopocket_color"] = "mednafen_ngp_libretro",
            ["arcade"] = "fbneo_libretro",
        };

        // RomM's own platform names, for platforms whose IGDB id does not settle it.
        private static readonly Dictionary<string, string> SlugToPlatform = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["psx"] = "sony_playstation", ["ps2"] = "sony_playstation2", ["ps3"] = "sony_playstation3", ["psp"] = "sony_psp",
            ["ngc"] = "nintendo_gamecube", ["wii"] = "nintendo_wii", ["xbox360"] = "xbox360", ["dc"] = "sega_dreamcast",
            ["nes"] = "nintendo_nes", ["fds"] = "nintendo_famicom_disk", ["snes"] = "nintendo_super_nes", ["n64"] = "nintendo_64",
            ["gb"] = "nintendo_gameboy", ["gbc"] = "nintendo_gameboycolor", ["gba"] = "nintendo_gameboyadvance", ["nds"] = "nintendo_ds",
            ["virtualboy"] = "nintendo_virtualboy", ["genesis"] = "sega_genesis", ["sms"] = "sega_mastersystem", ["gamegear"] = "sega_gamegear",
            ["segacd"] = "sega_cd", ["sega32"] = "sega_32x", ["sg1000"] = "sega_sg1000", ["saturn"] = "sega_saturn",
            ["tg16"] = "nec_turbografx_16", ["turbografx-cd"] = "nec_turbografx_cd", ["supergrafx"] = "nec_supergrafx",
            ["atari2600"] = "atari_2600", ["atari7800"] = "atari_7800", ["lynx"] = "atari_lynx",
            ["wonderswan"] = "bandai_wonderswan", ["wonderswan-color"] = "bandai_wonderswan_color",
            ["neo-geo-pocket"] = "snk_neogeopocket", ["neo-geo-pocket-color"] = "snk_neogeopocket_color", ["arcade"] = "arcade",
            // RomM's browser games (IGDB's "Web browser", 82, which Playnite has no platform for) and
            // Flash: RomM plays both with Ruffle, Playnite's "Adobe Flash".
            ["browser"] = "adobe_flash", ["flash"] = "adobe_flash",
            // Playnite gives Vectrex Intellivision's IGDB id (67): both settled by name.
            ["intellivision"] = "mattel_intellivision", ["vectrex"] = "vectrex",
            // Platforms Playnite has under no IGDB id, or RomM keeps apart from Playnite's.
            ["new-nintendo-3ds"] = "nintendo_3ds", ["arduboy"] = "arduboy", ["supervision"] = "watara_supervision",
            ["ti-83"] = "ti_83", ["neogeomvs"] = "arcade", ["cps1"] = "arcade", ["cps2"] = "arcade", ["cps3"] = "arcade",
            // Neo Geo AES games come as arcade sets (FinalBurn Neo), not Geolith's .neo files.
            ["neogeoaes"] = "arcade",
            // Playnite has one Jaguar platform; BigPEmu plays the CD games too.
            ["atari-jaguar-cd"] = "atari_jaguar", ["jaguar"] = "atari_jaguar",
            ["philips-cd-i"] = "philips_cdi", ["cdi"] = "philips_cdi",
            ["xbox"] = "xbox", ["3ds"] = "nintendo_3ds", ["wiiu"] = "nintendo_wiiu", ["psvita"] = "sony_vita", ["switch"] = "nintendo_switch",
        };

        /// <summary>
        /// RetroArch cores for platforms Playnite's RetroArch definition has no profile for: set
        /// up as a profile of the RetroArch emulator's own (custom), running the core. Philips
        /// CD-i: SAME CDi, MAME's CD-i driver as a core; its file types from libretro's core info
        /// (same_cdi_libretro.info: chd, iso, cue).
        /// </summary>
        internal sealed class ExtraCore
        {
            public string Name;
            public string Core;
            public string[] Extensions;
        }

        private static readonly Dictionary<string, ExtraCore> ExtraCores = new Dictionary<string, ExtraCore>(StringComparer.OrdinalIgnoreCase)
        {
            ["philips_cdi"] = new ExtraCore { Name = "SAME CDi", Core = "same_cdi_libretro", Extensions = new[] { "chd", "cue", "iso" } },
        };

        /// <summary>What setting up a platform will do, worked out before anything is downloaded.</summary>
        public sealed class Plan
        {
            public RomMPlatform RomMPlatform;
            public EmulatedPlatform Platform;
            public EmulatorDefinition Definition;
            public EmulatorDefinitionProfile Profile;
            public Emulator Existing;        // already in Playnite: nothing to download
            public Emulator Reinstall;       // in Playnite, but no longer on disk: downloaded again for it
            internal Recipe Recipe;
            internal List<EmulatorDefinitionProfile> Candidates;   // the definition's profiles for the platform
            public string Core;              // RetroArch: the core's file name (without .dll)
            internal ExtraCore Extra;        // RetroArch: a core Playnite has no profile for (a custom profile)

            public string EmulatorName => Definition?.Name ?? "";
            /// <summary>Whether setting it up downloads the emulator (else it is already installed).</summary>
            public bool Downloads => Existing == null;

            /// <summary>What setting it up does, downloading from the RomM server or from the project's site.</summary>
            public string Describe(bool fromServer = false)
            {
                if (Existing != null)
                    return $"{EmulatorName} is already set up in Playnite; its {Profile.Name} profile is used for {RomMPlatform.PlayniteName}.";
                string from = fromServer ? "your RomM server" : Recipe.Source;
                string bios = Recipe.BiosFolder != null || Recipe.Firmware ? ", with the platform's BIOS files from your RomM server." : ".";
                return Reinstall != null
                    ? $"{Reinstall.Name} is no longer installed on this computer. It is downloaded again from {from} " +
                      $"into Playnite\\Emulators\\{Recipe.Folder}, and Playnite's {Reinstall.Name} is pointed at it{bios}"
                    : $"{EmulatorName}{(Core != null ? " with its " + Profile.Name + " core" : "")} is downloaded from {from} " +
                      $"into Playnite\\Emulators\\{Recipe.Folder}, set up for {RomMPlatform.PlayniteName}{bios}";
            }
        }

        private readonly RomM _plugin;

        public EmulatorSetup(RomM plugin)
        {
            _plugin = plugin;
        }

        private IPlayniteAPI Api => _plugin.Playnite;

        private static HttpClient CreateClient()
        {
            // GitHub and the emulator sites only take TLS 1.2 or later.
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RomM-Playnite");
            return client;
        }

        // ---- Planning ------------------------------------------------------------------------

        /// <summary>How a RomM platform would be set up, or null when this cannot set it up.</summary>
        public Plan PlanFor(int romMPlatformId)
        {
            try
            {
                RomMPlatform platform = _plugin.Settings?.RomMPlatforms?.FirstOrDefault(p => p.Id == romMPlatformId);
                if (platform == null)
                    return null;
                EmulatedPlatform playnite = PlaynitePlatform(platform);
                if (playnite == null)
                    return null;

                // The platform's mapping says which emulator, when there is one this can install.
                Emulator mapped = _plugin.Settings?.Mappings?
                    .FirstOrDefault(m => m.Enabled && m.IsImportable && m.RomMPlatformId == romMPlatformId)?.Emulator;
                string key = mapped?.BuiltInConfigId != null && Recipes.ContainsKey(mapped.BuiltInConfigId) ? mapped.BuiltInConfigId
                    : EmulatorFor.TryGetValue(playnite.Id, out string chosen) ? chosen : "retroarch";
                Recipe recipe = Recipes[key];
                EmulatorDefinition definition = Api.Emulation.GetEmulator(recipe.DefinitionId);
                if (definition == null)
                    return null;
                List<EmulatorDefinitionProfile> profiles = definition.Profiles?.Where(p => p.Platforms?.Contains(playnite.Id) == true).ToList()
                    ?? new List<EmulatorDefinitionProfile>();
                ExtraCore extra = null;
                if (profiles.Count == 0 && key == "retroarch" && ExtraCores.TryGetValue(playnite.Id, out extra))
                {
                    // A profile of the definition's making, for the plan alone: RetroArch's program
                    // (as its other profiles name it) running the core. Registered as a custom profile.
                    profiles.Add(new EmulatorDefinitionProfile
                    {
                        Name = extra.Name,
                        Platforms = new List<string> { playnite.Id },
                        ImageExtensions = extra.Extensions.ToList(),
                        ProfileFiles = new List<string> { "cores\\" + extra.Core + ".dll" },
                        StartupExecutable = definition.Profiles?.FirstOrDefault(p => !string.IsNullOrEmpty(p.StartupExecutable))?.StartupExecutable ?? @"^retroarch\.exe$",
                        StartupArguments = $"-L \".\\cores\\{extra.Core}.dll\" \"{{ImagePath}}\"",
                    });
                }
                if (profiles.Count == 0)
                    return null;

                var plan = new Plan { RomMPlatform = platform, Platform = playnite, Definition = definition, Recipe = recipe, Candidates = profiles, Extra = extra };
                if (extra != null)
                {
                    plan.Profile = profiles[0];
                    plan.Core = extra.Core;
                }
                else if (key == "retroarch")
                {
                    PreferredCore.TryGetValue(playnite.Id, out string core);
                    plan.Profile = profiles.FirstOrDefault(p => core != null && (p.ProfileFiles ?? new List<string>()).Any(f => f.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0))
                        ?? profiles.First();
                    string file = (plan.Profile.ProfileFiles ?? new List<string>()).FirstOrDefault(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                    plan.Core = file == null ? null : Path.GetFileNameWithoutExtension(file);
                }

                // An emulator of this kind already in Playnite (and on disk) is used as it is; the
                // mapping's own first. One in Playnite but gone from disk is installed again.
                var ofKind = Api.Database.Emulators
                    .Where(e => string.Equals(e.BuiltInConfigId, definition.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => mapped != null && e.Id == mapped.Id ? 0 : 1)
                    .ToList();
                plan.Existing = ofKind.FirstOrDefault(IsPresent);
                if (plan.Existing == null)
                    plan.Reinstall = ofKind.FirstOrDefault();
                if (plan.Profile == null)
                {
                    string dir = plan.Existing != null ? PlaynitePath.Resolve(Api, plan.Existing.InstallDir) : null;
                    // Installed: the profile whose program is there. To download: today's builds' profile
                    // (checked again once unpacked, see RunCore).
                    plan.Profile = (dir != null ? profiles.FirstOrDefault(p => HasExecutable(dir, p.StartupExecutable)) : null)
                        ?? profiles.FirstOrDefault(p => string.Equals(p.Name, recipe.Profile, StringComparison.OrdinalIgnoreCase))
                        ?? profiles.First();
                }
                return plan;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] Could not work out how to set up platform {romMPlatformId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Playnite's platform for a RomM one: by RomM's name for it when listed here (some IGDB ids
        /// Playnite has wrong or not at all), else by IGDB id. Null when Playnite has none.
        /// </summary>
        internal EmulatedPlatform PlaynitePlatform(RomMPlatform platform)
        {
            if (platform == null)
                return null;
            foreach (string slug in new[] { platform.Slug, platform.FsSlug })
            {
                if (slug != null && SlugToPlatform.TryGetValue(slug, out string specId))
                {
                    EmulatedPlatform named = Api.Emulation.Platforms.FirstOrDefault(p => p.Id == specId);
                    if (named != null)
                        return named;
                }
            }
            return Api.Emulation.Platforms.FirstOrDefault(p => (platform.IgdbId ?? 0) > 0 && p.IgdbId == platform.IgdbId.Value);
        }

        /// <summary>
        /// Whether an emulator is on this computer: its folder, holding the program of one of its
        /// profiles (a folder alone is not enough: Playnite's own Emulation\Emulators\&lt;name&gt;
        /// folders exist whether an emulator is in them or not). An emulator with no built-in
        /// definition is taken at its folder.
        /// </summary>
        internal bool IsPresent(Emulator emulator)
        {
            if (emulator == null)
                return false;
            string dir = PlaynitePath.Resolve(Api, emulator.InstallDir);
            // No folder, but profiles of its own that name what they run ("Windows": the game's
            // own program): nothing to be missing.
            if (string.IsNullOrEmpty(dir) && string.IsNullOrEmpty(emulator.BuiltInConfigId) && emulator.CustomProfiles?.Count > 0)
                return true;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;
            EmulatorDefinition definition = string.IsNullOrEmpty(emulator.BuiltInConfigId) ? null : Api.Emulation.GetEmulator(emulator.BuiltInConfigId);
            if (definition?.Profiles == null)
                return true;
            var used = new HashSet<string>((emulator.BuiltinProfiles ?? new ObservableCollection<BuiltInEmulatorProfile>()).Select(p => p.BuiltInProfileName ?? ""));
            return definition.Profiles
                .Where(p => used.Count == 0 || used.Contains(p.Name ?? ""))
                .Any(p => HasExecutable(dir, p.StartupExecutable));
        }

        // ---- Windows games ----------------------------------------------------------------------

        /// <summary>RomM's Windows platform (IGDB's "PC (Microsoft Windows)", 6).</summary>
        internal static bool IsWindowsPlatform(RomMPlatform platform) =>
            platform != null && ((platform.IgdbId ?? 0) == 6
                || new[] { "win", "windows", "pc", "pc-windows" }.Contains((platform.Slug ?? "").ToLowerInvariant()));

        private const string WindowsExecutable = "{ImagePath}";

        /// <summary>
        /// Windows games need no emulator, only a way for Playnite to start them: an emulator
        /// "Windows" whose profile runs the game's own program, which these games are packed
        /// with as their one .com (a renamed .exe, which Windows runs the same) or a .bat:
        ///
        ///   Executable {ImagePath}, Working directory {ImageDir} (the game's own folder: games
        ///   load their files from where they start), Platform PC (Windows), File types com, bat
        ///
        /// An emulator already in Playnite with such a profile is used as it is. Then the RomM
        /// platform is mapped to it (games extracted, each in a folder of its own). Nothing is
        /// downloaded, so nothing is asked. On the UI thread.
        /// </summary>
        internal EmulatorMapping SetUpWindows(RomMPlatform platform)
        {
            Platform pc = Api.Database.Platforms.FirstOrDefault(p => string.Equals(p.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase));
            if (pc == null)
            {
                pc = new Platform("PC (Windows)") { SpecificationId = "pc_windows" };
                Api.Database.Platforms.Add(pc);
            }

            Emulator emulator = Api.Database.Emulators.FirstOrDefault(e =>
                e.CustomProfiles?.Any(p => string.Equals(p.Executable?.Trim(), WindowsExecutable, StringComparison.OrdinalIgnoreCase)) == true);
            CustomEmulatorProfile profile;
            if (emulator == null)
            {
                profile = new CustomEmulatorProfile
                {
                    Name = "Windows",
                    Platforms = new List<Guid> { pc.Id },
                    Executable = WindowsExecutable,
                    Arguments = "",
                    WorkingDirectory = ExpandableVariables.ImageDir,
                    ImageExtensions = new List<string> { "com", "bat" },
                };
                emulator = new Emulator("Windows")
                {
                    InstallDir = "",
                    CustomProfiles = new ObservableCollection<CustomEmulatorProfile> { profile },
                };
                Api.Database.Emulators.Add(emulator);
                s_log.Info("[EmulatorSetup] Added the Windows emulator: runs a game's own .com or .bat, in the game's folder.");
            }
            else
            {
                profile = emulator.CustomProfiles.First(p => string.Equals(p.Executable?.Trim(), WindowsExecutable, StringComparison.OrdinalIgnoreCase));
            }

            SettingsViewModel settings = _plugin.Settings;
            EmulatorMapping mapping = settings.Mappings.FirstOrDefault(m => m.Enabled && m.RomMPlatformId == platform.Id);
            bool isNew = mapping == null;
            if (isNew)
            {
                mapping = NewMapping(platform);
                mapping.AutoExtract = true;     // Windows games come as an archive of their folder
                mapping.InstallFlat = false;
            }
            mapping.Emulator = emulator;
            mapping.EmulatorProfileId = profile.Id;
            mapping.RomMPlatform = platform;
            if (isNew)
                settings.Mappings.Add(mapping);
            settings.Persist();
            s_log.Info($"[EmulatorSetup] Mapped {platform.PlayniteName} to {emulator.Name} ({profile.Name}); games go to {mapping.DestinationPath}.");
            return mapping;
        }

        private static bool HasExecutable(string dir, string pattern)
        {
            try
            {
                var regex = new Regex(pattern ?? "^$", RegexOptions.IgnoreCase);
                return Directory.Exists(dir) && Directory.GetFiles(dir).Any(f => regex.IsMatch(Path.GetFileName(f)));
            }
            catch
            {
                return false;
            }
        }

        // ---- Doing it ----------------------------------------------------------------------

        /// <summary>
        /// Sets the platform up, showing Playnite's progress dialog. Null when done, else what
        /// went wrong. On the UI thread.
        /// </summary>
        public string Run(Plan plan, bool fromServer)
        {
            string problem = null;
            Api.Dialogs.ActivateGlobalProgress(progress =>
            {
                try
                {
                    RunCore(plan, progress, fromServer);
                }
                catch (OperationCanceledException)
                {
                    problem = "Cancelled.";
                }
                catch (Exception ex)
                {
                    s_log.Error(ex, $"[EmulatorSetup] Setting up {plan.EmulatorName} for {plan.RomMPlatform.PlayniteName} failed.");
                    problem = ex.Message;
                }
            }, new GlobalProgressOptions($"Setting up {plan.EmulatorName} for {plan.RomMPlatform.PlayniteName}...", true) { IsIndeterminate = false });
            return problem;
        }

        // Reinstall leaves these as they are when the emulator already has them.
        private static readonly HashSet<string> SettingsExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ini", ".cfg", ".yml", ".yaml", ".json", ".xml", ".toml", ".txt",
        };

        /// <summary>
        /// Downloads the platform's emulator again over the one installed (the plan's Existing):
        /// unpacked apart, then its files copied over the installed ones, so its settings, saves,
        /// BIOS and anything else it keeps beside itself stay. From the RomM server when it has a
        /// copy, else the project's site. A RetroArch platform's core is fetched again too. Null
        /// when done, else what went wrong. On the UI thread (shows Playnite's progress).
        /// </summary>
        public string Reinstall(Plan plan)
        {
            if (plan?.Existing == null)
                return "The emulator is not installed.";
            string dir = PlaynitePath.Resolve(Api, plan.Existing.InstallDir);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return "The emulator's folder could not be found.";
            string problem = null;
            Api.Dialogs.ActivateGlobalProgress(progress =>
            {
                string archive = null;
                string unpacked = Path.Combine(Path.GetTempPath(), "romm-reinstall-" + Guid.NewGuid().ToString("N"));
                try
                {
                    string staged = ServerCopy(plan);
                    if (staged != null)
                        archive = Download(staged, progress, $"Downloading {plan.EmulatorName} from your RomM server...", HttpClientSingleton.Instance);
                    else
                    {
                        progress.Text = $"Finding the latest {plan.EmulatorName}...";
                        string url = plan.Recipe.Url?.Invoke();
                        if (url == null)
                            throw new InvalidOperationException($"No copy of {plan.EmulatorName} was found on your RomM server or at {plan.Recipe.Source}.");
                        archive = Download(url, progress, $"Downloading {plan.EmulatorName}...");
                    }
                    progress.Text = $"Unpacking {plan.EmulatorName}...";
                    Extract(archive, unpacked, progress.CancelToken);

                    // The download's own folder holding the program (archives often have one).
                    string source = new[] { plan.Profile }.Concat(plan.Candidates ?? new List<EmulatorDefinitionProfile>())
                        .Where(p => !string.IsNullOrEmpty(p?.StartupExecutable))
                        .Select(p => FindExecutableFolder(unpacked, p.StartupExecutable))
                        .FirstOrDefault(f => f != null)
                        ?? throw new InvalidOperationException($"{plan.EmulatorName} was downloaded, but its program is not in it.");

                    progress.Text = $"Replacing {plan.EmulatorName}'s files...";
                    foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    {
                        progress.CancelToken.ThrowIfCancellationRequested();
                        string target = Path.Combine(dir, file.Substring(source.Length).TrimStart('\\', '/'));
                        // A settings file the download ships with is kept as the user has it.
                        if (File.Exists(target) && SettingsExtensions.Contains(Path.GetExtension(target)))
                            continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(file, target, true);
                    }

                    string core = plan.Core ?? plan.Extra?.Core;
                    if (core != null)
                    {
                        TryDelete(Path.Combine(dir, "cores", core + ".dll"));
                        InstallCore(plan, dir, core, progress);
                    }
                    s_log.Info($"[EmulatorSetup] {plan.EmulatorName} reinstalled over {dir}.");
                }
                catch (OperationCanceledException)
                {
                    problem = "Cancelled.";
                }
                catch (Exception ex)
                {
                    s_log.Error(ex, $"[EmulatorSetup] Reinstalling {plan.EmulatorName} failed.");
                    problem = ex.Message;
                }
                finally
                {
                    if (archive != null)
                        TryDelete(archive);
                    try
                    {
                        if (Directory.Exists(unpacked))
                            Directory.Delete(unpacked, true);
                    }
                    catch
                    {
                        // Left in the temp folder.
                    }
                }
            }, new GlobalProgressOptions($"Reinstalling {plan.EmulatorName}...", true) { IsIndeterminate = false });
            return problem;
        }

        // fromServer: the emulator comes from the RomM server (ServerCopy), else the project's site.
        private void RunCore(Plan plan, GlobalProgressActionArgs progress, bool fromServer)
        {
            CancellationToken ct = progress.CancelToken;
            string dir;
            Emulator emulator = plan.Existing;
            if (emulator != null)
            {
                dir = PlaynitePath.Resolve(Api, emulator.InstallDir);
            }
            else
            {
                string target = Path.Combine(Api.Paths.ApplicationPath, "Emulators", plan.Recipe.Folder);
                // Already unpacked there (Playnite reinstalled, or its entry removed): hooked up as it is.
                bool unpacked = Directory.Exists(target)
                    && new[] { plan.Profile }.Concat(plan.Candidates ?? new List<EmulatorDefinitionProfile>())
                        .Any(p => FindExecutableFolder(target, p.StartupExecutable) != null);
                if (unpacked)
                    s_log.Info($"[EmulatorSetup] {plan.EmulatorName} is already in {target}; it is used as it is.");
                else
                {
                string archive;
                if (fromServer)
                {
                    progress.Text = $"Looking for {plan.EmulatorName} on your RomM server...";
                    string staged = ServerCopy(plan)
                        ?? throw new InvalidOperationException($"Your RomM server has no copy of {plan.EmulatorName}: a firmware file named " +
                                                               $"\"emulator-{plan.Recipe.DefinitionId}...\" (.zip or .7z), e.g. emulator-{plan.Recipe.DefinitionId}-v1.7z.");
                    s_log.Info($"[EmulatorSetup] Downloading {plan.EmulatorName} from your RomM server ({staged}).");
                    archive = Download(staged, progress, $"Downloading {plan.EmulatorName} from your RomM server...", HttpClientSingleton.Instance);
                }
                else
                {
                    progress.Text = $"Finding the latest {plan.EmulatorName}...";
                    if (plan.Recipe.Url == null)
                        throw new InvalidOperationException($"{plan.EmulatorName} can only be set up from your RomM server: download it from " +
                                                            $"{plan.Recipe.Source} and upload it to the platform's firmware as \"emulator-{plan.Recipe.DefinitionId}-v1.zip\".");
                    string url = plan.Recipe.Url();
                    if (url == null)
                        throw new InvalidOperationException($"Could not find a Windows download of {plan.EmulatorName} at {plan.Recipe.Source}.");
                    s_log.Info($"[EmulatorSetup] Downloading {plan.EmulatorName} from {url}.");
                    archive = Download(url, progress, $"Downloading {plan.EmulatorName}...");
                }
                try
                {
                    progress.Text = $"Unpacking {plan.EmulatorName}...";
                    Extract(archive, target, ct);
                }
                finally
                {
                    TryDelete(archive);
                }
                }

                // Where its executable is (archives often hold a folder of their own). The planned
                // profile first; when the build's program is named otherwise, the profile that fits it.
                dir = FindExecutableFolder(target, plan.Profile.StartupExecutable);
                if (dir == null)
                {
                    foreach (EmulatorDefinitionProfile candidate in plan.Candidates ?? new List<EmulatorDefinitionProfile>())
                    {
                        dir = FindExecutableFolder(target, candidate.StartupExecutable);
                        if (dir != null)
                        {
                            s_log.Info($"[EmulatorSetup] {plan.EmulatorName}'s download fits its {candidate.Name} profile rather than {plan.Profile.Name}.");
                            plan.Profile = candidate;
                            break;
                        }
                    }
                }
                if (dir == null)
                    throw new InvalidOperationException($"{plan.EmulatorName} was unpacked, but its program ({plan.Profile.StartupExecutable}) is not in it.");
                foreach (string file in plan.Recipe.PortableFiles)
                {
                    if (!File.Exists(Path.Combine(dir, file)))
                        File.WriteAllText(Path.Combine(dir, file), "");
                }
                foreach (string folder in plan.Recipe.PortableFolders)
                    Directory.CreateDirectory(Path.Combine(dir, folder));
            }

            ct.ThrowIfCancellationRequested();
            if (plan.Core != null)
                InstallCore(plan, dir, plan.Core, progress);

            ct.ThrowIfCancellationRequested();
            InstallBios(plan, dir, progress);

            // PCSX2 would open its first-run wizard: its settings are made here instead.
            if (string.Equals(plan.Recipe.DefinitionId, "pcsx2", StringComparison.OrdinalIgnoreCase))
            {
                progress.Text = "Setting up PCSX2...";
                Pcsx2FirstRun.Prepare(dir);
            }

            progress.Text = "Adding it to Playnite...";
            string profileId = null;
            bool downloaded = plan.Existing == null;
            Api.MainView.UIDispatcher.Invoke(() =>
            {
                emulator = Register(plan, plan.Existing ?? plan.Reinstall, downloaded ? dir : null, out profileId);
                AddMapping(plan, emulator, profileId);
            });
            s_log.Info($"[EmulatorSetup] {plan.EmulatorName} is set up for {plan.RomMPlatform.PlayniteName} in {dir}.");
        }

        // newDir: where the emulator was just unpacked (null: it was already installed).
        private Emulator Register(Plan plan, Emulator emulator, string newDir, out string profileId)
        {
            bool isNew = emulator == null;
            string installDir = newDir;
            if (newDir != null && Api.Paths.IsPortable && newDir.StartsWith(Api.Paths.ApplicationPath, StringComparison.OrdinalIgnoreCase))
                installDir = ExpandableVariables.PlayniteDirectory + newDir.Substring(Api.Paths.ApplicationPath.TrimEnd('\\').Length);
            if (isNew)
            {
                emulator = new Emulator(plan.Definition.Name)
                {
                    BuiltInConfigId = plan.Definition.Id,
                    InstallDir = installDir,
                    BuiltinProfiles = new ObservableCollection<BuiltInEmulatorProfile>(),
                };
            }
            else if (installDir != null)
            {
                s_log.Info($"[EmulatorSetup] {emulator.Name} moves from {emulator.InstallDir} to {installDir}.");
                emulator.InstallDir = installDir;
            }
            if (emulator.BuiltinProfiles == null)
                emulator.BuiltinProfiles = new ObservableCollection<BuiltInEmulatorProfile>();

            if (plan.Extra != null)
            {
                profileId = AddCoreProfile(emulator, plan, newDir ?? PlaynitePath.Resolve(Api, emulator.InstallDir));
                EmulatorLaunch.ApplyArguments(Api, emulator);
                if (isNew)
                    Api.Database.Emulators.Add(emulator);
                else
                    Api.Database.Emulators.Update(emulator);
                return emulator;
            }

            BuiltInEmulatorProfile profile = emulator.BuiltinProfiles.FirstOrDefault(p => p.BuiltInProfileName == plan.Profile.Name);
            if (profile == null)
            {
                profile = new BuiltInEmulatorProfile { Name = plan.Profile.Name, BuiltInProfileName = plan.Profile.Name };
                emulator.BuiltinProfiles.Add(profile);
            }
            profileId = profile.Id;

            // Fullscreen, and closing with the game (see EmulatorLaunch).
            EmulatorLaunch.ApplyArguments(Api, emulator);

            if (isNew)
                Api.Database.Emulators.Add(emulator);
            else
                Api.Database.Emulators.Update(emulator);
            return emulator;
        }

        /// <summary>
        /// A custom profile on RetroArch for a core Playnite's definition has no profile for
        /// (ExtraCores), or the one already there (by name). Started as the built-in profiles are
        /// (fullscreen: -f), from RetroArch's folder, the game's path last:
        ///
        ///   {EmulatorDir}\retroarch.exe  -f -L "{EmulatorDir}\cores\same_cdi_libretro.dll" "{ImagePath}"
        ///
        /// Its id.
        /// </summary>
        private string AddCoreProfile(Emulator emulator, Plan plan, string dir)
        {
            if (emulator.CustomProfiles == null)
                emulator.CustomProfiles = new ObservableCollection<CustomEmulatorProfile>();
            CustomEmulatorProfile profile = emulator.CustomProfiles.FirstOrDefault(p => string.Equals(p.Name, plan.Extra.Name, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
                return profile.Id;

            // The program as this build names it (retroarch.exe).
            string exe = "retroarch.exe";
            try
            {
                var regex = new Regex(plan.Profile.StartupExecutable ?? "^$", RegexOptions.IgnoreCase);
                string found = dir != null && Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).FirstOrDefault(regex.IsMatch) : null;
                exe = found ?? exe;
            }
            catch
            {
                // The usual name.
            }

            Guid platformId = PlatformIdFor(plan.Platform);
            profile = new CustomEmulatorProfile
            {
                Name = plan.Extra.Name,
                Executable = ExpandableVariables.EmulatorDirectory + "\\" + exe,
                Arguments = $"-f -L \"{ExpandableVariables.EmulatorDirectory}\\cores\\{plan.Extra.Core}.dll\" \"{ExpandableVariables.ImagePath}\"",
                WorkingDirectory = ExpandableVariables.EmulatorDirectory,
                ImageExtensions = plan.Extra.Extensions.ToList(),
                Platforms = platformId == Guid.Empty ? new List<Guid>() : new List<Guid> { platformId },
            };
            emulator.CustomProfiles.Add(profile);
            s_log.Info($"[EmulatorSetup] Added the {profile.Name} profile to {emulator.Name}: {profile.Executable} {profile.Arguments}");
            return profile.Id;
        }

        // Playnite's library platform for an emulated one (made when missing, as for Windows games).
        private Guid PlatformIdFor(EmulatedPlatform platform)
        {
            if (platform == null)
                return Guid.Empty;
            Platform own = Api.Database.Platforms.FirstOrDefault(p => string.Equals(p.SpecificationId, platform.Id, StringComparison.OrdinalIgnoreCase));
            if (own == null)
            {
                own = new Platform(platform.Name) { SpecificationId = platform.Id };
                Api.Database.Platforms.Add(own);
            }
            return own.Id;
        }

        /// <summary>
        /// A mapping for a platform, without an emulator yet: games go beside the other platforms'
        /// (the same parent folder, in a folder named as RomM names the platform's), else under
        /// Playnite\Roms, and extract and lay out as the first mapping does. A game added before
        /// its platform has an emulator is filed with one of these (see RomMImportController), so
        /// its paths are the ones the mapping set up later gives it.
        /// </summary>
        internal EmulatorMapping NewMapping(RomMPlatform platform)
        {
            SettingsViewModel settings = _plugin.Settings;
            List<EmulatorMapping> existing = (settings.Mappings ?? new ObservableCollection<EmulatorMapping>())
                .Where(m => !string.IsNullOrWhiteSpace(m.DestinationPath)).ToList();

            string parent = existing
                .Select(m => Path.GetDirectoryName(m.DestinationPath.TrimEnd('\\', '/')))
                .Where(p => !string.IsNullOrEmpty(p))
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? Path.Combine(Api.Paths.ApplicationPath, "Roms");
            string folder = platform.FsSlug ?? platform.Slug ?? platform.Id.ToString();
            EmulatorMapping model = existing.FirstOrDefault();

            bool keepArchives = KeepArchives.Contains(PlaynitePlatform(platform)?.Id ?? "");
            var mapping = new EmulatorMapping(settings.RomMPlatforms)
            {
                Enabled = true,
                AutoExtract = !keepArchives && (model?.AutoExtract ?? true),
                InstallFlat = model?.InstallFlat ?? false,
                DestinationPath = Path.Combine(parent, folder),
            };
            mapping.RomMPlatform = platform;
            return mapping;
        }

        private void AddMapping(Plan plan, Emulator emulator, string profileId)
        {
            SettingsViewModel settings = _plugin.Settings;
            // The platform's own mapping, when it has one (its emulator was missing): pointed at
            // this one, keeping its folder and options.
            EmulatorMapping mapping = settings.Mappings.FirstOrDefault(m => m.Enabled && m.RomMPlatformId == plan.RomMPlatform.Id);
            bool isNew = mapping == null;
            if (isNew)
                mapping = NewMapping(plan.RomMPlatform);
            // The emulator first: setting it clears the platform, which is set again after.
            mapping.Emulator = emulator;
            mapping.EmulatorProfileId = profileId;
            mapping.RomMPlatform = plan.RomMPlatform;
            if (isNew)
                settings.Mappings.Add(mapping);
            settings.Persist();
            s_log.Info($"[EmulatorSetup] Mapped {plan.RomMPlatform.PlayniteName} to {emulator.Name} ({plan.Profile.Name}); games go to {mapping.DestinationPath}.");
        }

        // ---- RetroArch cores --------------------------------------------------------------------

        // From the RomM server when it has the core (emulator-libretro-<core>...zip, as
        // Get-RommEmulators.ps1 names them), else from libretro's buildbot.
        private void InstallCore(Plan plan, string dir, string core, GlobalProgressActionArgs progress)
        {
            string cores = Path.Combine(dir, "cores");
            if (File.Exists(Path.Combine(cores, core + ".dll")))
                return;
            string name = core.Replace("_libretro", "");
            string staged = ServerCopy(plan, "emulator-libretro-" + core);
            string archive;
            if (staged != null)
            {
                s_log.Info($"[EmulatorSetup] Downloading the RetroArch core {core} from your RomM server ({staged}).");
                archive = Download(staged, progress, $"Downloading the {name} core from your RomM server...", HttpClientSingleton.Instance);
            }
            else
            {
                string url = $"https://buildbot.libretro.com/nightly/windows/x86_64/latest/{core}.dll.zip";
                s_log.Info($"[EmulatorSetup] Downloading the RetroArch core {core} from {url}.");
                archive = Download(url, progress, $"Downloading the {name} core...");
            }
            try
            {
                Extract(archive, cores, progress.CancelToken);
            }
            finally
            {
                TryDelete(archive);
            }
        }

        // ---- BIOS from the RomM server ------------------------------------------------------------

        private void InstallBios(Plan plan, string dir, GlobalProgressActionArgs progress)
        {
            if (plan.Recipe.BiosFolder == null && !plan.Recipe.Firmware)
                return;
            string host = _plugin.Settings?.RomMHost?.TrimEnd('/');
            progress.Text = "Looking for BIOS files on your RomM server...";
            JArray files;
            using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/firmware?platform_id={plan.RomMPlatform.Id}").GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    s_log.Info($"[EmulatorSetup] The server's firmware list could not be read ({(int)response.StatusCode}).");
                    return;
                }
                files = JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JArray ?? new JArray();
            }
            if (files.Count == 0)
            {
                s_log.Info($"[EmulatorSetup] The server has no BIOS files for {plan.RomMPlatform.PlayniteName}.");
                return;
            }

            foreach (JToken file in files)
            {
                progress.CancelToken.ThrowIfCancellationRequested();
                int id = (int?)file["id"] ?? 0;
                string name = Path.GetFileName((string)file["file_name"] ?? "");
                if (id <= 0 || name.Length == 0)
                    continue;

                // An emulator put on the server for setting up (ServerCopy) is not a BIOS file.
                if (IsServerEmulator(name))
                    continue;
                bool firmware = plan.Recipe.Firmware && name.EndsWith(".pup", StringComparison.OrdinalIgnoreCase);
                // Eden: the Switch's firmware is its system NCAs, in the emulated NAND's
                // system\Contents\registered (where its own "Install Firmware" puts them): loose
                // .nca files go there, and a .zip of them is unpacked there.
                bool eden = string.Equals(plan.Recipe.DefinitionId, "eden", StringComparison.OrdinalIgnoreCase);
                string registered = Path.Combine(dir, "user", "nand", "system", "Contents", "registered");
                bool switchNca = eden && name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase);
                bool switchFirmwareZip = eden && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                if (switchFirmwareZip && Directory.Exists(registered) && Directory.EnumerateFiles(registered, "*.nca").Any())
                    continue;   // a firmware is in place already
                if (!firmware && !switchFirmwareZip && plan.Recipe.BiosFolder == null)
                    continue;
                // Some cores look in a folder of their own inside the system folder (BiosNeeds).
                string destination = firmware || switchFirmwareZip
                    ? Path.Combine(Path.GetTempPath(), "romm-" + Guid.NewGuid().ToString("N") + "-" + name)
                    : switchNca
                        ? Path.Combine(registered, name)
                        : Path.Combine(dir, plan.Recipe.BiosFolder, BiosNeeds.FolderFor(plan.Platform?.Id, name), name);
                if (!firmware && !switchFirmwareZip && File.Exists(destination))
                    continue;

                progress.Text = $"Downloading {name} from your RomM server...";
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/firmware/{id}/content/{Uri.EscapeDataString(name)}", HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var output = File.Create(destination))
                        input.CopyTo(output);
                }
                s_log.Info($"[EmulatorSetup] {name} from the server {(firmware || switchFirmwareZip ? "downloaded" : "put in " + Path.GetDirectoryName(destination))}.");

                if (switchFirmwareZip)
                {
                    try
                    {
                        progress.Text = "Installing the Switch firmware...";
                        int count = InstallSwitchFirmware(destination, registered, progress.CancelToken);
                        s_log.Info($"[EmulatorSetup] Switch firmware: {count} NCA file(s) from {name} put in {registered}.");
                    }
                    finally
                    {
                        TryDelete(destination);
                    }
                }

                if (firmware)
                {
                    try
                    {
                        if (string.Equals(plan.Recipe.DefinitionId, "vita3k", StringComparison.OrdinalIgnoreCase))
                            InstallVitaFirmware(dir, destination, progress);
                        else
                            InstallPs3Firmware(dir, destination, progress);
                    }
                    finally
                    {
                        TryDelete(destination);
                    }
                }
            }
        }

        /// <summary>
        /// Of these RomM platforms, the ones an emulator could be set up for but not yet: a BIOS
        /// file it cannot do without is missing (see <see cref="MissingBios"/>), or the emulator
        /// itself can only come from the RomM server and is not there (Eden). One look at the
        /// server's firmware list for all of them. Platforms this cannot set up at all are not
        /// in it. Null when the list could not be read. Off the UI thread.
        /// </summary>
        public HashSet<int> PlatformsMissingFiles(IEnumerable<int> romMPlatformIds)
        {
            string host = _plugin.Settings?.RomMHost?.TrimEnd('/');
            if (string.IsNullOrEmpty(host))
                return null;
            JArray firmware;
            try
            {
                using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/firmware").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    firmware = JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JArray ?? new JArray();
                }
            }
            catch (Exception ex)
            {
                s_log.Info($"[EmulatorSetup] Could not read the server's firmware: {ex.Message}");
                return null;
            }

            var files = firmware
                .Select(f => new { Platform = (int?)f["platform_id"] ?? 0, Name = Path.GetFileName((string)f["file_name"] ?? ""), Size = (long?)f["file_size_bytes"] ?? -1L })
                .Where(f => f.Name.Length > 0)
                .ToList();
            var missing = new HashSet<int>();
            foreach (int id in romMPlatformIds.Distinct())
            {
                try
                {
                    Plan plan = PlanFor(id);
                    if (plan?.Platform == null)
                        continue;
                    var own = files.Where(f => f.Platform == id && !IsServerEmulator(f.Name)).Select(f => Tuple.Create(f.Name, f.Size)).ToList();
                    string installed = plan.Existing != null ? PlaynitePath.Resolve(Api, plan.Existing.InstallDir) : null;
                    bool lacking = BiosNeeds.Missing(plan.Platform.Id, own, installed, plan.Recipe.BiosFolder).Count > 0;
                    if (!lacking && plan.Downloads && plan.Recipe.Url == null)
                    {
                        string unpacked = Path.Combine(Api.Paths.ApplicationPath, "Emulators", plan.Recipe.Folder);
                        bool here = Directory.Exists(unpacked) && FindExecutableFolder(unpacked, plan.Profile.StartupExecutable) != null;
                        lacking = !here && !files.Any(f => IsServerEmulator(f.Name) && NamedFor(f.Name, ServerNames(plan)));
                    }
                    if (lacking)
                        missing.Add(id);
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[EmulatorSetup] Could not check platform {id}'s files: {ex.Message}");
                }
            }
            return missing;
        }

        /// <summary>One thing setting a platform up needs, and whether it was found.</summary>
        public sealed class Requirement
        {
            public string Label;
            public bool Found;
        }

        /// <summary>
        /// Everything setting a platform's emulator up needs from the RomM server (or already on
        /// this computer), each with whether it was found: the files BiosNeeds lists, and the
        /// emulator itself when it can only come from the server (Eden). The same reckoning as
        /// PlatformsMissingFiles, item by item. Null when the platform cannot be set up or the
        /// server's list could not be read. Off the UI thread.
        /// </summary>
        public List<Requirement> Requirements(int romMPlatformId)
        {
            string host = _plugin.Settings?.RomMHost?.TrimEnd('/');
            Plan plan = PlanFor(romMPlatformId);
            if (plan?.Platform == null || string.IsNullOrEmpty(host))
                return null;
            try
            {
                JArray files;
                using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/firmware?platform_id={romMPlatformId}").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    files = JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JArray ?? new JArray();
                }
                var all = files
                    .Select(f => Tuple.Create(Path.GetFileName((string)f["file_name"] ?? ""), (long?)f["file_size_bytes"] ?? -1L))
                    .Where(f => f.Item1.Length > 0)
                    .ToList();
                var own = all.Where(f => !IsServerEmulator(f.Item1)).ToList();
                string installed = plan.Existing != null ? PlaynitePath.Resolve(Api, plan.Existing.InstallDir) : null;

                var result = new List<Requirement>();
                if (plan.Downloads && plan.Recipe.Url == null)
                {
                    string unpacked = Path.Combine(Api.Paths.ApplicationPath, "Emulators", plan.Recipe.Folder);
                    bool here = Directory.Exists(unpacked) && FindExecutableFolder(unpacked, plan.Profile.StartupExecutable) != null;
                    result.Add(new Requirement
                    {
                        Label = $"{plan.EmulatorName} itself, as emulator-{plan.Recipe.DefinitionId}-v1.zip (or .7z)",
                        Found = here || all.Any(f => IsServerEmulator(f.Item1) && NamedFor(f.Item1, ServerNames(plan))),
                    });
                }
                foreach (Tuple<BiosNeeds.Need, bool> check in BiosNeeds.Check(plan.Platform.Id, own, installed, plan.Recipe.BiosFolder))
                    result.Add(new Requirement { Label = check.Item1.Label, Found = check.Item2 });
                return result;
            }
            catch (Exception ex)
            {
                s_log.Info($"[EmulatorSetup] Could not check {plan.RomMPlatform.PlayniteName}'s files on the server: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The BIOS files the platform's emulator cannot do without that are neither on the RomM
        /// server nor already on this computer (see <see cref="BiosNeeds"/>): empty when nothing is
        /// missing, null when the server's list could not be read (then it is not held against
        /// the setup, which reports its own trouble with the server).
        /// </summary>
        public List<BiosNeeds.Need> MissingBios(Plan plan)
        {
            if (plan?.Platform == null || BiosNeeds.For(plan.Platform.Id).Count == 0)
                return new List<BiosNeeds.Need>();
            string host = _plugin.Settings?.RomMHost?.TrimEnd('/');
            if (string.IsNullOrEmpty(host))
                return null;
            try
            {
                JArray files;
                using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/firmware?platform_id={plan.RomMPlatform.Id}").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    files = JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JArray ?? new JArray();
                }
                var onServer = files
                    .Select(f => Tuple.Create(Path.GetFileName((string)f["file_name"] ?? ""), (long?)f["file_size_bytes"] ?? -1L))
                    .Where(f => f.Item1.Length > 0 && !IsServerEmulator(f.Item1))
                    .ToList();
                string installed = plan.Existing != null ? PlaynitePath.Resolve(Api, plan.Existing.InstallDir) : null;
                List<BiosNeeds.Need> missing = BiosNeeds.Missing(plan.Platform.Id, onServer, installed, plan.Recipe.BiosFolder);
                if (missing.Count > 0)
                    s_log.Info($"[EmulatorSetup] {plan.RomMPlatform.PlayniteName} is missing BIOS files: {string.Join("; ", missing.Select(m => m.Label))}.");
                return missing;
            }
            catch (Exception ex)
            {
                s_log.Info($"[EmulatorSetup] Could not check {plan.RomMPlatform.PlayniteName}'s BIOS files on the server: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The .nca files of a Switch firmware archive, wherever they are inside it, into Eden's
        /// system\Contents\registered, flat. Anything else in the archive is left out.
        /// </summary>
        private static int InstallSwitchFirmware(string archivePath, string registered, CancellationToken ct)
        {
            Directory.CreateDirectory(registered);
            int count = 0;
            using (var archive = ArchiveFactory.Open(archivePath))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    string fileName = Path.GetFileName((entry.Key ?? "").Replace('/', '\\'));
                    if (!fileName.EndsWith(".nca", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string target = RomMInstallPaths.ResolveWithin(registered, fileName);
                    using (var input = entry.OpenEntryStream())
                    using (var output = File.Create(target))
                        input.CopyTo(output);
                    count++;
                }
            }
            if (count == 0)
                throw new InvalidOperationException("The Switch firmware archive on your RomM server holds no .nca files.");
            return count;
        }

        /// <summary>
        /// Vita3K installs the Vita's firmware itself: Vita3K --firmware &lt;file.pup&gt; installs it
        /// (into portable\fs) and exits, with no window (main.cpp). Both of the Vita's files are
        /// taken this way, the system (PSVUPDAT.PUP) and the fonts (PSP2UPDAT.PUP).
        /// </summary>
        private static void InstallVitaFirmware(string dir, string pup, GlobalProgressActionArgs progress)
        {
            string exe = Directory.GetFiles(dir, "Vita3K*.exe").FirstOrDefault();
            if (exe == null)
                return;
            progress.Text = "Vita3K is installing the PS Vita firmware...";
            using (Process vita3k = Process.Start(new ProcessStartInfo(exe, $"--firmware \"{pup}\"") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true }))
            {
                DateTime until = DateTime.UtcNow.AddMinutes(10);
                while (!vita3k.HasExited && DateTime.UtcNow < until)
                {
                    progress.CancelToken.ThrowIfCancellationRequested();
                    Thread.Sleep(500);
                }
                if (!vita3k.HasExited)
                    vita3k.Kill();
            }
            s_log.Info($"[EmulatorSetup] Vita3K installed {Path.GetFileName(pup)}.");
        }

        /// <summary>
        /// RPCS3 installs the PS3's firmware itself (--installfw); it is run once for it and closed
        /// once the firmware's files are in place.
        /// </summary>
        private static void InstallPs3Firmware(string dir, string pup, GlobalProgressActionArgs progress)
        {
            string exe = Directory.GetFiles(dir, "rpcs3*.exe").FirstOrDefault();
            string installed = Path.Combine(dir, "dev_flash", "vsh", "module", "vsh.self");
            if (exe == null || File.Exists(installed))
                return;
            progress.Text = "RPCS3 is installing the PS3 firmware (this takes a minute)...";
            using (Process rpcs3 = Process.Start(new ProcessStartInfo(exe, $"--installfw \"{pup}\"") { WorkingDirectory = dir, UseShellExecute = false }))
            {
                DateTime until = DateTime.UtcNow.AddMinutes(10);
                while (!rpcs3.HasExited && !File.Exists(installed) && DateTime.UtcNow < until)
                {
                    progress.CancelToken.ThrowIfCancellationRequested();
                    Thread.Sleep(1000);
                }
                // A little longer for the last files, then RPCS3 is closed.
                Thread.Sleep(5000);
                if (!rpcs3.HasExited)
                {
                    rpcs3.CloseMainWindow();
                    if (!rpcs3.WaitForExit(10000))
                        rpcs3.Kill();
                }
            }
            s_log.Info(File.Exists(installed) ? "[EmulatorSetup] The PS3 firmware is installed." : "[EmulatorSetup] RPCS3 did not finish installing the PS3 firmware.");
        }

        // ---- Download sources ---------------------------------------------------------------------

        /// <summary>
        /// The newest GitHub release's download whose name matches (prereleases too when allowed,
        /// and when there is nothing else), or null.
        /// </summary>
        private static string GitHubAsset(string repo, string include, string exclude, bool allowPrerelease) =>
            GitHubAsset(new[] { repo }, include, exclude, allowPrerelease);

        /// <summary>
        /// The download whose name matches from the newest release (by when it was published) of
        /// any of the repositories -- prereleases too when allowed, and when there is nothing
        /// else -- or null.
        /// </summary>
        private static string GitHubAsset(string[] repos, string include, string exclude, bool allowPrerelease)
        {
            var wanted = new Regex(include, RegexOptions.IgnoreCase);
            var unwanted = new Regex(exclude, RegexOptions.IgnoreCase);
            var found = new List<Tuple<DateTime, bool, string, string>>();   // published, prerelease, url, repo
            foreach (string repo in repos)
            {
                try
                {
                    string json = s_web.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=15").GetAwaiter().GetResult();
                    foreach (JToken release in JArray.Parse(json).Where(r => (bool?)r["draft"] != true))
                    {
                        JToken asset = (release["assets"] as JArray ?? new JArray())
                            .FirstOrDefault(a => wanted.IsMatch((string)a["name"] ?? "") && !unwanted.IsMatch((string)a["name"] ?? ""));
                        if (asset == null)
                            continue;
                        DateTime published = (DateTime?)release["published_at"] ?? (DateTime?)release["created_at"] ?? DateTime.MinValue;
                        found.Add(Tuple.Create(published, (bool?)release["prerelease"] == true, (string)asset["browser_download_url"], repo));
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[EmulatorSetup] Could not read {repo}'s releases: {ex.Message}");
                }
            }
            var pick = found.Where(f => allowPrerelease || !f.Item2).OrderByDescending(f => f.Item1).FirstOrDefault()
                       ?? found.OrderByDescending(f => f.Item1).FirstOrDefault();
            if (pick != null)
                s_log.Info($"[EmulatorSetup] Newest build in {pick.Item4}: published {pick.Item1:yyyy-MM-dd}.");
            return pick?.Item3;
        }

        /// <summary>
        /// Ludusavi's newest Windows build unpacked into <paramref name="folder"/>: its exe's path,
        /// or null when no release has one. Throws when it cannot be downloaded.
        /// </summary>
        internal static string InstallLudusavi(string folder)
        {
            string url = GitHubAsset("mtkennerly/ludusavi", @"win64\.zip$", "^$", false);
            if (url == null)
                return null;
            string archive = Path.Combine(Path.GetTempPath(), "romm-ludusavi-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                File.WriteAllBytes(archive, s_web.GetByteArrayAsync(url).GetAwaiter().GetResult());
                Extract(archive, folder, CancellationToken.None);
            }
            finally
            {
                TryDelete(archive);
            }
            string exe = Directory.GetFiles(folder, "ludusavi.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe != null && !string.Equals(Path.GetDirectoryName(exe), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                // Unpacked in a folder of its own: moved up, where it is looked for.
                string top = Path.Combine(folder, "ludusavi.exe");
                if (File.Exists(top))
                    File.Delete(top);
                File.Move(exe, top);
                exe = top;
            }
            return exe;
        }

        private static string LatestTag(string repo)
        {
            try
            {
                string json = s_web.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest").GetAwaiter().GetResult();
                return (string)JObject.Parse(json)["tag_name"];
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] Could not read {repo}'s latest release: {ex.Message}");
                return null;
            }
        }

        // PPSSPP's builds are on its site, by version: v1.19.3 -> files/1_19_3/ppsspp_win.zip.
        private static string PpssppUrl()
        {
            string tag = LatestTag("hrydgard/ppsspp");
            Match version = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!version.Success)
                return null;
            string folder = string.Join("_", version.Groups.Cast<Group>().Skip(1).Where(g => g.Success).Select(g => g.Value));
            return $"https://www.ppsspp.org/files/{folder}/ppsspp_win.zip";
        }

        // Dolphin's release builds, as linked from its download page.
        private static string DolphinUrl()
        {
            try
            {
                string page = s_web.GetStringAsync("https://dolphin-emu.org/download/").GetAwaiter().GetResult();
                // Any of its download hosts (dl., dl-mirror., ...), a release before a dev build.
                MatchCollection links = Regex.Matches(page, @"https?://[\w.-]*dolphin-emu\.org/[^""'\s<>]*?dolphin-[\w.-]+?-x64\.7z");
                string release = links.Cast<Match>().Select(m => m.Value).FirstOrDefault(u => u.Contains("/releases/"));
                string any = links.Cast<Match>().Select(m => m.Value).FirstOrDefault();
                if (release == null && any == null)
                    s_log.Warn($"[EmulatorSetup] Dolphin's download page ({page.Length} characters) has no Windows x64 .7z link this recognises.");
                return release ?? any;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] Could not read Dolphin's download page: {ex.Message}");
                return null;
            }
        }

        // BigPEmu's newest Windows build, as linked from its download page (builds/BigPEmu_v1xx.zip).
        private static string BigPEmuUrl()
        {
            try
            {
                const string page = "https://www.richwhitehouse.com/jaguar/index.php?content=download";
                string html = s_web.GetStringAsync(page).GetAwaiter().GetResult();
                Match link = Regex.Match(html, @"[^""'\s<>]*BigPEmu_v?[\w.]+?\.zip", RegexOptions.IgnoreCase);
                if (!link.Success)
                {
                    s_log.Warn("[EmulatorSetup] BigPEmu's download page has no build link this recognises.");
                    return null;
                }
                return new Uri(new Uri(page), link.Value).ToString();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] Could not read BigPEmu's download page: {ex.Message}");
                return null;
            }
        }

        // RetroArch's stable build for the latest release: buildbot.libretro.com/stable/<version>/...
        private static string RetroArchUrl()
        {
            string tag = LatestTag("libretro/RetroArch");
            Match version = Regex.Match(tag ?? "", @"\d+\.\d+\.\d+");
            return version.Success ? $"https://buildbot.libretro.com/stable/{version.Value}/windows/x86_64/RetroArch.7z" : null;
        }

        // ---- Files ---------------------------------------------------------------------------------

        // ---- Emulators kept on the RomM server ------------------------------------------------

        // The standard name is "emulator-<emulator>-v<version>.zip|7z" (emulator-dolphin-v1.7z),
        // matched without regard to case. Only "emulator-<emulator>" is needed: anything may
        // follow it after "-", "_", "." or a space (emulator-dolphin.7z, emulator-dolphin-2512.7z,
        // emulator-xenia-canary-v3.zip), but not more of a name (emulator-dolphinx.zip). The
        // name's own parts may be joined by "_" in place of "-" (emulator_xenia_canary_v3.zip).
        private static bool NamedFor(string fileName, string prefix)
        {
            if (fileName == null || prefix == null || fileName.Length < prefix.Length)
                return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                char a = char.ToLowerInvariant(fileName[i]), b = char.ToLowerInvariant(prefix[i]);
                if (a == b || (Joiner(a) && Joiner(b)))
                    continue;
                return false;
            }
            char next = fileName.Length > prefix.Length ? fileName[prefix.Length] : '.';
            return next == '-' || next == '_' || next == '.' || next == ' ';
        }

        private static bool Joiner(char c) => c == '-' || c == '_';

        private static bool NamedFor(string fileName, IEnumerable<string> prefixes) => prefixes.Any(p => NamedFor(fileName, p));

        // The names an emulator goes by on the server: Playnite's id for it (emulator-xenia),
        // and its folder's name (emulator-xenia-canary, emulator-bigpemu).
        private static List<string> ServerNames(Plan plan)
        {
            var names = new List<string> { "emulator-" + plan.Recipe.DefinitionId };
            string folder = Regex.Replace((plan.Recipe.Folder ?? "").Trim().ToLowerInvariant(), @"\s+", "-");
            if (folder.Length > 0)
                names.Add("emulator-" + folder);
            // Longest first, so "emulator-xenia-canary-v2" reads its version after the whole name.
            return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToList();
        }

        /// <summary>
        /// The version in what follows the emulator's name: "v" and numbers (-v1, _v2.1, -V3.0.4),
        /// else bare numbers (-2512, -0.21.0); null when there is none. Compared part by part,
        /// so v1.10 is newer than v1.9.
        /// </summary>
        internal static int[] VersionOf(string fileName, string prefix)
        {
            if (fileName == null || prefix == null || fileName.Length < prefix.Length)
                return null;
            string rest = fileName.Substring(prefix.Length);
            rest = Regex.Replace(rest, @"\.(zip|7z)$", "", RegexOptions.IgnoreCase);
            Match match = Regex.Match(rest, @"(?:^|[-_. ])v(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(rest, @"(?:^|[-_. ])(\d+(?:\.\d+)*)");
            if (!match.Success)
                return null;
            try
            {
                return match.Groups[1].Value.Split('.').Select(part => int.Parse(part.Length > 9 ? part.Substring(0, 9) : part)).ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static int CompareVersions(int[] a, int[] b)
        {
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y)
                    return x.CompareTo(y);
            }
            return 0;
        }

        private static readonly Random s_random = new Random();

        // When a file was put on the server (or last changed there), or null when not said.
        private static DateTime? UploadedAt(JToken file)
        {
            foreach (string field in new[] { "updated_at", "created_at" })
            {
                try
                {
                    JToken value = file[field];
                    if (value == null || value.Type == JTokenType.Null)
                        continue;
                    if (value.Type == JTokenType.Date)
                        return value.Value<DateTime>().ToUniversalTime();
                    if (DateTime.TryParse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed))
                        return parsed;
                }
                catch
                {
                    // Not a date: the other field, or none.
                }
            }
            return null;
        }

        /// <summary>
        /// Of the server's copies of one emulator, the one to download: the highest version where
        /// any has one (the newest upload among equal versions); else the newest upload; else, with
        /// nothing to tell them apart, any one of them.
        /// </summary>
        private static JToken Choose(List<JToken> candidates, IList<string> prefixes)
        {
            if (candidates.Count <= 1)
                return candidates.FirstOrDefault();
            var read = candidates.Select(f =>
            {
                string name = (string)f["file_name"] ?? "";
                string prefix = prefixes.FirstOrDefault(p => NamedFor(name, p));
                return new { File = f, Version = VersionOf(name, prefix), At = UploadedAt(f) };
            }).ToList();

            var versioned = read.Where(r => r.Version != null).ToList();
            if (versioned.Count > 0)
            {
                versioned.Sort((x, y) =>
                {
                    int byVersion = CompareVersions(y.Version, x.Version);
                    return byVersion != 0 ? byVersion : Nullable.Compare(y.At, x.At);
                });
                return versioned[0].File;
            }
            var dated = read.Where(r => r.At != null).OrderByDescending(r => r.At).ToList();
            if (dated.Count > 0)
                return dated[0].File;
            lock (s_random)
                return read[s_random.Next(read.Count)].File;
        }

        private static bool IsServerEmulator(string fileName) =>
            (fileName.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("emulator_", StringComparison.OrdinalIgnoreCase))
            && (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The emulator as put on the RomM server, or null: a firmware file named
        /// "emulator-&lt;emulator&gt;...zip|7z" (emulator-dolphin.7z, emulator-xenia-canary.zip...),
        /// uploaded to the platform being set up or, failing that, to any other. Of several, see
        /// Choose: the highest version (emulator-dolphin-v2.7z over -v1), else the newest upload.
        /// </summary>
        private string ServerCopy(Plan plan, string prefix = null)
        {
            string host = _plugin.Settings?.RomMHost?.TrimEnd('/');
            if (string.IsNullOrEmpty(host))
                return null;
            List<string> prefixes = prefix != null ? new List<string> { prefix } : ServerNames(plan);
            foreach (string query in new[] { $"?platform_id={plan.RomMPlatform.Id}", "" })
            {
                try
                {
                    using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/firmware{query}").GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode)
                            continue;
                        var files = JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JArray ?? new JArray();
                        JToken pick = Choose(files
                            .Where(f => IsServerEmulator((string)f["file_name"] ?? "")
                                        && NamedFor((string)f["file_name"], prefixes))
                            .ToList(), prefixes);
                        if (pick != null)
                            s_log.Info($"[EmulatorSetup] Server copy of {plan.EmulatorName}: {(string)pick["file_name"]}.");
                        if (pick != null)
                            return $"{host}/api/firmware/{(int)pick["id"]}/content/{Uri.EscapeDataString((string)pick["file_name"])}";
                    }
                }
                catch (Exception ex)
                {
                    s_log.Info($"[EmulatorSetup] Could not look for {plan.EmulatorName} on the server: {ex.Message}");
                }
            }
            return null;
        }

        private static string Download(string url, GlobalProgressActionArgs progress, string text, HttpClient client = null)
        {
            string file = Path.Combine(Path.GetTempPath(), "romm-setup-" + Guid.NewGuid().ToString("N") + Path.GetExtension(new Uri(url).AbsolutePath));
            using (var response = (client ?? s_web).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, progress.CancelToken).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                progress.ProgressMaxValue = total > 0 ? total : 1;
                progress.CurrentProgressValue = 0;
                progress.Text = text;
                using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var output = File.Create(file))
                {
                    byte[] buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        progress.CancelToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        done += read;
                        if (total > 0)
                            progress.CurrentProgressValue = done;
                    }
                }
            }
            return file;
        }

        // Unpacks a .zip or .7z, refusing entries that would land outside the folder.
        private static void Extract(string archivePath, string destination, CancellationToken ct)
        {
            Directory.CreateDirectory(destination);
            using (var archive = ArchiveFactory.Open(archivePath))
            using (IReader reader = archive.ExtractAllEntries())
            {
                while (reader.MoveToNextEntry())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.Entry.IsDirectory)
                        continue;
                    string target = RomMInstallPaths.ResolveWithin(destination, reader.Entry.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var output = File.Create(target))
                        reader.WriteEntryTo(output);
                }
            }
        }

        private static string FindExecutableFolder(string root, string pattern)
        {
            var regex = new Regex(pattern ?? "^$", RegexOptions.IgnoreCase);
            return Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories)
                .Where(f => regex.IsMatch(Path.GetFileName(f)))
                .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                .Select(Path.GetDirectoryName)
                .FirstOrDefault();
        }

        private static void TryDelete(string file)
        {
            try
            {
                if (file != null && File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
                // A leftover temporary file is not worth failing over.
            }
        }
    }
}
