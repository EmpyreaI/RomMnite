using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Account;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Setup
{
    /// <summary>
    /// Every emulator starts its game fullscreen and closes when the game is quit, with nothing
    /// of its own (game list, "are you sure?") left in between. Two parts:
    ///
    /// Arguments, on the emulator's Playnite profile (its arguments overridden with the
    /// definition's own plus these), set when Playnite starts and when an emulator is set up,
    /// since Playnite reads them before any game starts:
    ///
    ///   RPCS3     --no-gui --fullscreen      no game list; closes with the game
    ///   PCSX2     -batch                     (the Qt profiles) closes when the game is shut down
    ///   Xenia     --fullscreen=true
    ///   Dolphin   -C Dolphin.Display.Fullscreen=True -C Dolphin.Interface.ConfirmStop=False
    ///             (it already runs --batch: closes when stopped)
    ///   RetroArch -f
    ///   Flycast   -config window:fullscreen=yes (emu.cfg has it too once it exists)
    ///   Azahar    -f;  Vita3K -F;  Cemu (-f), xemu (-full-screen), Eden (-f) already do.
    ///   Ruffle    --fullscreen --no-gui --open-url-mode deny, settings and saves in its folder,
    ///             the gamepad's D-pad, A and Start as keys (closes when its window does)
    ///   DuckStation (-batch -fullscreen) and PPSSPP (--fullscreen --pause-menu-exit) already do.
    ///
    /// A profile whose arguments the user overrode is left as it is.
    ///
    /// Settings, written just before the game starts, in files the emulator has made:
    ///
    ///   DuckStation / PCSX2  first-run wizard done for them (DuckStationFirstRun, Pcsx2FirstRun),
    ///              controller 1 on SDL (any controller SDL knows)
    ///              and controller 2 on the second gamepad when two are connected
    ///   Dolphin, Cemu, RPCS3, Azahar, Eden  controller 1 on the connected controller
    ///              (EmulatorControllers; the SDL ones as SDL3 sees them: ControllerProbe)
    ///   Azahar, Cemu, xemu, Vita3K  first-run screens and prompts (EmulatorFirstRun)
    ///   PCSX2      inis\PCSX2.ini [UI] ConfirmShutdown = false
    ///   DuckStation settings.ini [Main] ConfirmPowerOff = false
    ///   RPCS3      GuiConfigs\CurrentSettings.ini [main_window] confirmationBoxExitGame=false
    ///   Flycast    emu.cfg [window] fullscreen = yes
    ///   RetroArch  retroarch.cfg confirm_quit, quit_on_close_content (quit when the game is
    ///              closed), video_fullscreen
    ///
    /// The flags and keys were read from each emulator's source, except RetroArch's
    /// quit_on_close_content, named after its "Quit on Close Content" setting.
    /// </summary>
    internal static class EmulatorLaunch
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        /// <summary>The arguments a built-in profile should start with, or null to leave the definition's.</summary>
        internal static string WantedArguments(string definitionId, string arguments)
        {
            string args = arguments ?? "";
            switch ((definitionId ?? "").ToLowerInvariant())
            {
                case "rpcs3":
                    return args.Contains("--no-gui") ? null : ("--no-gui --fullscreen " + args).Trim();
                case "pcsx2":
                    // The Qt profiles ("-fullscreen -slowboot -- {ImagePath}"); the old ones run --nogui.
                    return args.Contains("-slowboot") && !args.Contains("-batch") ? "-batch " + args : null;
                case "flycast":
                    // Its settings file is only written once it has run, so the setting is also
                    // given for the run itself (cl.cpp: -config section:key=value, not saved).
                    return args.Contains("window:fullscreen") ? null : ("-config window:fullscreen=yes " + args).Trim();
                case "xenia":
                    return args.Contains("--fullscreen") ? null : ("--fullscreen=true " + args).Trim();
                case "dolphin":
                    return args.Contains("Display.Fullscreen") ? null
                        : (args + " -C Dolphin.Display.Fullscreen=True -C Dolphin.Interface.ConfirmStop=False").Trim();
                case "azahar":
                    // The game's path must stay last (citra_qt.cpp).
                    return args.Contains("-f") || args.Contains("--fullscreen") ? null : ("-f " + args).Trim();
                case "vita3k":
                    return args.Contains("-F") || args.Contains("--fullscreen") ? null : ("-F " + args).Trim();
                case "ruffle":
                    // Fullscreen with no menu bar; links a game opens (sponsors' sites) not followed;
                    // its settings and Flash saves beside it, not in AppData; the gamepad's D-pad as
                    // the arrow keys, A as Space, Start as Enter (Flash games read the keyboard).
                    return args.Contains("--fullscreen") ? null
                        : ("--fullscreen --no-gui --open-url-mode deny " +
                           "--config \"{EmulatorDir}\\config\" --save-directory \"{EmulatorDir}\\saves\" " +
                           "-B d-pad-up=up -B d-pad-down=down -B d-pad-left=left -B d-pad-right=right " +
                           "-B south=space -B start=return " + args).Trim();
                case "retroarch":
                    return args.StartsWith("-f ", StringComparison.Ordinal) || args.Contains(" -f ") ? null : ("-f " + args).Trim();
                default:
                    return null;
            }
        }

        /// <summary>
        /// Sets an emulator's built-in profiles to start fullscreen and close with the game.
        /// Whether anything changed (the caller saves the emulator).
        /// </summary>
        internal static bool ApplyArguments(IPlayniteAPI api, Emulator emulator)
        {
            if (emulator?.BuiltinProfiles == null || string.IsNullOrEmpty(emulator.BuiltInConfigId))
                return false;
            EmulatorDefinition definition = api.Emulation.GetEmulator(emulator.BuiltInConfigId);
            if (definition?.Profiles == null)
                return false;

            bool changed = false;
            foreach (BuiltInEmulatorProfile profile in emulator.BuiltinProfiles)
            {
                EmulatorDefinitionProfile own = definition.Profiles.FirstOrDefault(p => p.Name == profile.BuiltInProfileName);
                string wanted = WantedArguments(definition.Id, own?.StartupArguments);
                if (wanted == null || (profile.OverrideDefaultArgs && profile.CustomArguments == wanted))
                    continue;
                if (profile.OverrideDefaultArgs && !string.IsNullOrWhiteSpace(profile.CustomArguments))
                {
                    // The user's own arguments stay.
                    if (WantedArguments(definition.Id, profile.CustomArguments) != null)
                        s_log.Info($"[EmulatorLaunch] {emulator.Name} ({profile.Name}) has arguments of its own; left as they are: {profile.CustomArguments}");
                    continue;
                }
                profile.OverrideDefaultArgs = true;
                profile.CustomArguments = wanted;
                changed = true;
                s_log.Info($"[EmulatorLaunch] {emulator.Name} ({profile.Name}) starts with: {wanted}");
            }
            return changed;
        }

        /// <summary>Every emulator in Playnite, on the UI thread.</summary>
        internal static void ApplyAll(IPlayniteAPI api)
        {
            try
            {
                foreach (Emulator emulator in api.Database.Emulators.ToList())
                {
                    if (ApplyArguments(api, emulator))
                        api.Database.Emulators.Update(emulator);
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorLaunch] Could not set the emulators' arguments: {ex.Message}");
            }
        }

        /// <summary>The emulator's own settings, just before a game starts in it.</summary>
        /// <param name="menusOnStartSelect">Emulators' own menus moved to Start + Select (EmulatorMenus).</param>
        /// <param name="overlayHasHome">Whether the overlay opens on the Home button.</param>
        /// <param name="overlayOnStartSelect">Whether the overlay opens on Start + Select: the emulators' menus are taken off it.</param>
        /// <param name="upscale">Rendering at the display's resolution (EmulatorUpscale); off puts back what it set.</param>
        /// <param name="upscaleRecord">Where EmulatorUpscale notes what it set.</param>
        internal static void PrepareSettings(Emulator emulator, string installDir, bool menusOnStartSelect = false, bool overlayHasHome = false,
            bool overlayOnStartSelect = false, bool upscale = true, string upscaleRecord = null)
        {
            if (emulator == null || string.IsNullOrEmpty(installDir))
                return;
            string id = ((emulator.BuiltInConfigId ?? "") + " " + (emulator.Name ?? "")).ToLowerInvariant();
            try
            {
                if (overlayOnStartSelect)
                    EmulatorMenus.Release(id, installDir);
                else if (menusOnStartSelect)
                    EmulatorMenus.Apply(id, installDir, overlayHasHome);

                if (id.Contains("pcsx2"))
                {
                    // Its first-run wizard done for it (BIOS, XInput controller), if not yet.
                    Pcsx2FirstRun.Prepare(installDir);
                    Pcsx2FirstRun.SecondPlayer(installDir, EmulatorControllers.SdlGamepads());
                    string ini = Existing(Path.Combine(installDir, "inis", "PCSX2.ini"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2", "inis", "PCSX2.ini"));
                    Set("PCSX2", ini, SettingsFile.SetIni(ini, "UI", new Dictionary<string, string> { ["ConfirmShutdown"] = "false" }));
                }
                else if (id.Contains("duckstation"))
                {
                    // Its first-run wizard done for it (controller 1 on the gamepad), if not yet.
                    DuckStationFirstRun.Prepare(installDir);
                    DuckStationFirstRun.SecondPlayer(installDir, EmulatorControllers.SdlGamepads());
                    string ini = Existing(Path.Combine(DuckStationFirstRun.DataRoot(installDir), "settings.ini"));
                    Set("DuckStation", ini, SettingsFile.SetIni(ini, "Main", new Dictionary<string, string> { ["ConfirmPowerOff"] = "false" }));
                }
                else if (id.Contains("rpcs3"))
                {
                    // Controller 1 on the controller connected now (EmulatorControllers).
                    EmulatorControllers.Rpcs3(installDir);
                    string ini = Existing(Path.Combine(installDir, "GuiConfigs", "CurrentSettings.ini"));
                    Set("RPCS3", ini, SettingsFile.SetIni(ini, "main_window", new Dictionary<string, string> { ["confirmationBoxExitGame"] = "false" }, separator: "="));
                }
                else if (id.Contains("dolphin"))
                {
                    // No "this is an NKit image" warning (Dolphin's MAIN_SKIP_NKIT_WARNING).
                    // Portable (portable.txt, as set up here): its User folder, made now if it has not
                    // run yet, so even the first game starts without the warning.
                    bool portable = File.Exists(Path.Combine(installDir, "portable.txt")) || Directory.Exists(Path.Combine(installDir, "User"));
                    string user = portable
                        ? Path.Combine(installDir, "User")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Dolphin Emulator");
                    string ini = Path.Combine(user, "Config", "Dolphin.ini");
                    if (portable || Directory.Exists(user))
                        Set("Dolphin", ini, SettingsFile.SetIni(ini, "Interface", new Dictionary<string, string> { ["SkipNKitWarning"] = "True" }, create: true));
                    EmulatorControllers.Dolphin(installDir);
                }
                else if (id.Contains("azahar"))
                {
                    EmulatorFirstRun.Azahar(installDir);
                    EmulatorControllers.Azahar(installDir);
                }
                else if (id.Contains("cemu"))
                {
                    EmulatorFirstRun.Cemu(installDir);
                    EmulatorControllers.Cemu(installDir);
                }
                else if (id.Contains("xemu"))
                    EmulatorFirstRun.Xemu(installDir);
                else if (id.Contains("eden"))
                    EmulatorControllers.Eden(installDir);
                else if (id.Contains("vita3k"))
                    EmulatorFirstRun.Vita3K(installDir);
                else if (id.Contains("flycast"))
                {
                    string cfg = Existing(Path.Combine(installDir, "emu.cfg"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "flycast", "emu.cfg"));
                    Set("Flycast", cfg, SettingsFile.SetIni(cfg, "window", new Dictionary<string, string> { ["fullscreen"] = "yes" }));
                }
                else if (id.Contains("retroarch"))
                {
                    string cfg = Existing(Path.Combine(installDir, "retroarch.cfg"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch", "retroarch.cfg"));
                    if (cfg != null)
                        Set("RetroArch", cfg, SettingsFile.SetCfg(cfg, new Dictionary<string, string>
                        {
                            ["video_fullscreen"] = "true",
                            ["confirm_quit"] = "false",
                            ["quit_on_close_content"] = "1",
                        }));
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorLaunch] Could not set {emulator.Name}'s settings: {ex.Message}");
            }
            // After the first-run steps above, which may make the files it edits.
            try
            {
                EmulatorUpscale.Apply(id, installDir, upscaleRecord, upscale);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorLaunch] Could not set {emulator.Name}'s resolution: {ex.Message}");
            }
        }

        private static string Existing(params string[] paths) => paths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

        private static void Set(string emulator, string file, bool changed)
        {
            if (file != null && changed)
                s_log.Info($"[EmulatorLaunch] {emulator}: set to start fullscreen and close with the game ({file}).");
        }
    }
}
