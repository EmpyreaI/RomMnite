using Playnite.SDK;
using RomM.Games;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RomM.Setup
{
    /// <summary>
    /// Controller 1 for the emulators that tie their bindings to one named device (so SDL's
    /// "any controller" cannot be set for them ahead of time), bound just before a game starts
    /// to the controller connected then, by the names those emulators give devices that do not
    /// change from one computer to the next:
    ///
    ///   Dolphin (GameCube)  User\Config\GCPadNew.ini [GCPad1], Device = XInput/&lt;slot&gt;/Gamepad
    ///                       (its XInput backend names a device by its XInput slot: XInput.cpp,
    ///                       GetPreferredId), its buttons, sticks, triggers and D-pad on the Xbox
    ///                       pad's; or, with no Xbox pad, an official PlayStation 4 or 5 or Switch
    ///                       Pro controller on its SDL backend, SDL/0/&lt;name&gt;, by the names SDL3's
    ///                       own drivers give those (SDL_hidapi_ps4.c, _ps5.c, _switch.c; Dolphin
    ///                       leaves those drivers on) and SDLGamepad.h's input names
    ///   Dolphin (Wii)       User\Config\WiimoteNew.ini [Wiimote1], an emulated Wii Remote with a
    ///                       Nunchuk on the same device: A, B (right trigger), 1, 2, -, +, the
    ///                       D-pad, pointing on the right stick, shaking on the right shoulder; the
    ///                       Nunchuk's stick on the left stick, C and Z on the left shoulder and
    ///                       trigger (WiimoteEmu.cpp, Nunchuk.cpp, Attachments.cpp). Home is left
    ///                       off: the Home button opens RomMnite's overlay
    ///   Cemu                portable\controllerProfiles\controller0.xml, a Wii U GamePad on the
    ///                       XInput controller in its slot (Cemu's XInput uuid is the slot:
    ///                       XInputController.cpp) with the mapping Cemu's own "default" button
    ///                       gives an XInput pad (VPADController::set_default_mapping)
    ///   RPCS3               config\input_configs\global\Default.yml, player 1 on its XInput,
    ///                       DualShock 4 or DualSense handler ("XInput Pad #&lt;slot + 1&gt;":
    ///                       xinput_pad_handler.cpp, GetDeviceNumber; "DS4 Pad #1",
    ///                       "DualSense Pad #1"), the handler's own default bindings
    ///   Azahar (3DS)        user\config\qt-config.ini [Controls], profile 1 on any controller SDL
    ///                       knows (its "all controllers" mapping, maptype:all, which needs no
    ///                       device id: sdl_impl.cpp), the buttons where Azahar's own automatic
    ///                       mapping puts them (xinput_to_3ds_mapping); Home left off (the overlay's)
    ///
    /// Not here: Cemu and RPCS3 with controllers other than Xbox (and PlayStation, for RPCS3),
    /// and Eden. Cemu and Eden name an SDL device by an id SDL makes differently when it talks to
    /// a PlayStation or Switch controller directly (as they have it do), and RPCS3's SDL handler
    /// by a name, so nothing written ahead of time would be sure to find the controller.
    ///
    /// Only an Xbox (XInput) controller gives such a name in Dolphin and Cemu; RPCS3 also has one
    /// for PlayStation 4 and 5 controllers. A file is only written when the emulator has none
    /// (it then uses the keyboard) or when it is still the one written here before (kept beside
    /// it as &lt;file&gt;.romm), so it follows whichever controller is connected; one the user
    /// changed is theirs. Nothing is written when no such controller is connected.
    /// </summary>
    internal static class EmulatorControllers
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        public static void Dolphin(string installDir)
        {
            string user = Path.Combine(installDir, "User");
            if (!File.Exists(Path.Combine(installDir, "portable.txt")) && !Directory.Exists(user))
                return;   // not portable: its settings are elsewhere, and its own
            if (!Writable(Path.Combine(user, "Config", "GCPadNew.ini")) && !Writable(Path.Combine(user, "Config", "WiimoteNew.ini")))
                return;   // both the user's: nothing to look for
            DolphinDevice device = FindDolphinDevice();
            if (device == null)
                return;
            string config = Path.Combine(user, "Config");

            var gc = new List<string>
            {
                "[GCPad1]",
                "Device = " + device.Name,
                "Buttons/A = " + device.South,
                "Buttons/B = " + device.East,
                "Buttons/X = " + device.West,
                "Buttons/Y = " + device.North,
                "Buttons/Z = `Shoulder R`",
                "Buttons/Start = Start",
                "Main Stick/Up = " + device.LeftUp,
                "Main Stick/Down = " + device.LeftDown,
                "Main Stick/Left = `Left X-`",
                "Main Stick/Right = `Left X+`",
                "C-Stick/Up = " + device.RightUp,
                "C-Stick/Down = " + device.RightDown,
                "C-Stick/Left = `Right X-`",
                "C-Stick/Right = `Right X+`",
                "Triggers/L = `Trigger L`",
                "Triggers/R = `Trigger R`",
                "Triggers/L-Analog = `Trigger L`",
                "Triggers/R-Analog = `Trigger R`",
                "D-Pad/Up = `Pad N`",
                "D-Pad/Down = `Pad S`",
                "D-Pad/Left = `Pad W`",
                "D-Pad/Right = `Pad E`",
            };
            Write("Dolphin", Path.Combine(config, "GCPadNew.ini"), string.Join("\r\n", gc) + "\r\n");

            // A Wii Remote held upright with a Nunchuk, the way most Wii games are played.
            var wii = new List<string>
            {
                "[Wiimote1]",
                "Device = " + device.Name,
                "Buttons/A = " + device.South,
                "Buttons/B = `Trigger R`",
                "Buttons/1 = " + device.West,
                "Buttons/2 = " + device.North,
                "Buttons/- = Back",
                "Buttons/+ = Start",
                "D-Pad/Up = `Pad N`",
                "D-Pad/Down = `Pad S`",
                "D-Pad/Left = `Pad W`",
                "D-Pad/Right = `Pad E`",
                "IR/Up = " + device.RightUp,
                "IR/Down = " + device.RightDown,
                "IR/Left = `Right X-`",
                "IR/Right = `Right X+`",
                "Shake/X = `Shoulder R`",
                "Shake/Y = `Shoulder R`",
                "Shake/Z = `Shoulder R`",
                "Extension = Nunchuk",
                "Nunchuk/Buttons/C = `Shoulder L`",
                "Nunchuk/Buttons/Z = `Trigger L`",
                "Nunchuk/Stick/Up = " + device.LeftUp,
                "Nunchuk/Stick/Down = " + device.LeftDown,
                "Nunchuk/Stick/Left = `Left X-`",
                "Nunchuk/Stick/Right = `Left X+`",
                "Nunchuk/Shake/X = `Thumb L`",
                "Nunchuk/Shake/Y = `Thumb L`",
                "Nunchuk/Shake/Z = `Thumb L`",
            };
            Write("Dolphin", Path.Combine(config, "WiimoteNew.ini"), string.Join("\r\n", wii) + "\r\n");
        }

        /// <summary>A device as Dolphin names it, and the names of its inputs that differ by backend.</summary>
        private sealed class DolphinDevice
        {
            public string Name;
            public string South, East, West, North;            // face buttons by position
            public string LeftUp, LeftDown, RightUp, RightDown; // XInput's Y axis points up, SDL's down
        }

        // The Xbox controller in its XInput slot, else an official PlayStation or Switch Pro
        // controller on SDL; null when neither is connected.
        private static DolphinDevice FindDolphinDevice()
        {
            int slot = ControllerMonitor.FirstXInputSlot();
            if (slot >= 0)
            {
                return new DolphinDevice
                {
                    Name = $"XInput/{slot}/Gamepad",
                    South = "`Button A`", East = "`Button B`", West = "`Button X`", North = "`Button Y`",
                    LeftUp = "`Left Y+`", LeftDown = "`Left Y-`", RightUp = "`Right Y+`", RightDown = "`Right Y-`",
                };
            }
            // Any controller on Dolphin's SDL, by the name and number Dolphin gives it: SDL's name,
            // and the first number free among devices of that name (ControllerInterface::AddDevice).
            string device = null;
            List<ProbedPad> probed = ControllerProbe.Probe(DolphinHints);
            ProbedPad pad = probed?.FirstOrDefault(p => p.IsGamepad);
            if (pad != null)
                device = $"SDL/{probed.Take(probed.IndexOf(pad)).Count(p => p.Name == pad.Name)}/{pad.Name}";
            else if (probed == null)
            {
                // SDL3 not here yet: the controllers whose name SDL sets itself.
                string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string name = ControllerMonitor.Snapshot(folder, TimeSpan.FromSeconds(2))
                    .Select(DolphinSdlName).FirstOrDefault(n => n != null);
                if (name != null)
                    device = "SDL/0/" + name;
            }
            if (device == null)
                return null;
            return new DolphinDevice
            {
                Name = device,
                South = "`Button S`", East = "`Button E`", West = "`Button W`", North = "`Button N`",
                LeftUp = "`Left Y-`", LeftDown = "`Left Y+`", RightUp = "`Right Y-`", RightDown = "`Right Y+`",
            };
        }

        // Dolphin's SDL settings as it starts with its own defaults (SDL.cpp, InputBackend).
        private static readonly Dictionary<string, string> DolphinHints = new Dictionary<string, string>
        {
            ["SDL_JOYSTICK_ENHANCED_REPORTS"] = "1",
            ["SDL_JOYSTICK_WGI"] = "0",
            ["SDL_JOYSTICK_HIDAPI_PS5_PLAYER_LED"] = "0",
            ["SDL_JOYSTICK_DIRECTINPUT"] = "1",
            ["SDL_JOYSTICK_HIDAPI_COMBINE_JOY_CONS"] = "1",
            ["SDL_JOYSTICK_HIDAPI_VERTICAL_JOY_CONS"] = "0",
            ["SDL_JOYSTICK_HIDAPI_GAMECUBE"] = "1",
        };

        private const int Sony = 0x054C, Nintendo = 0x057E;

        /// <summary>
        /// The name Dolphin's SDL gives a controller, for those whose name SDL3 sets itself:
        /// official PlayStation 4 and 5 controllers and the Switch Pro Controller. Null for others
        /// (their names come from the device or a mapping, and may not be the ones seen here).
        /// </summary>
        internal static string DolphinSdlName(ConnectedPad pad)
        {
            if (pad == null || pad.XInput)
                return null;
            if (pad.VendorId == Sony && pad.IsPs4)
                return "PS4 Controller";
            if (pad.VendorId == Sony && pad.IsPs5)
                return pad.ProductId == 0x0DF2 ? "DualSense Edge Wireless Controller" : "DualSense Wireless Controller";
            if (pad.VendorId == Nintendo && pad.ProductId == 0x2009)
                return "Nintendo Switch Pro Controller";
            return null;
        }

        // Cemu's ids: VPADController::ButtonId (mapping) and Buttons2 (button), in order.
        private const int A = 1, B = 2, X = 3, Y = 4, L = 5, R = 6, ZL = 7, ZR = 8, Plus = 9, Minus = 10,
            Up = 11, Down = 12, Left = 13, Right = 14, StickL = 15, StickR = 16,
            StickLUp = 17, StickLDown = 18, StickLLeft = 19, StickLRight = 20,
            StickRUp = 21, StickRDown = 22, StickRLeft = 23, StickRRight = 24;
        private const int AxisXP = 38, AxisYP = 39, RotationXP = 40, RotationYP = 41, TriggerXP = 42, TriggerYP = 43,
            AxisXN = 44, AxisYN = 45, RotationXN = 46, RotationYN = 47;

        // VPADController::set_default_mapping, case InputAPI::XInput (buttons by XInput bit).
        private static readonly int[,] CemuXInput =
        {
            { A, 13 }, { B, 12 }, { X, 15 }, { Y, 14 },
            { L, 8 }, { R, 9 }, { ZL, TriggerXP }, { ZR, TriggerYP },
            { Plus, 4 }, { Minus, 5 },
            { Up, 0 }, { Down, 1 }, { Left, 2 }, { Right, 3 },
            { StickL, 6 }, { StickR, 7 },
            { StickLUp, AxisYP }, { StickLDown, AxisYN }, { StickLLeft, AxisXN }, { StickLRight, AxisXP },
            { StickRUp, RotationYP }, { StickRDown, RotationYN }, { StickRLeft, RotationXN }, { StickRRight, RotationXP },
        };

        // VPADController::set_default_mapping, case InputAPI::SDLController: by SDL gamepad
        // button (SDL_GamepadButton: SOUTH 0, EAST 1, WEST 2, NORTH 3, BACK 4, START 6,
        // LEFT_STICK 7, RIGHT_STICK 8, shoulders 9 and 10, D-pad 11-14), A on the right face
        // button; a Switch Pro Controller (by its id) with A and B, X and Y the other way round.
        private static readonly int[,] CemuSdl =
        {
            { A, 1 }, { B, 0 }, { X, 3 }, { Y, 2 },
            { L, 9 }, { R, 10 }, { ZL, TriggerXP }, { ZR, TriggerYP },
            { Plus, 6 }, { Minus, 4 },
            { Up, 11 }, { Down, 12 }, { Left, 13 }, { Right, 14 },
            { StickL, 7 }, { StickR, 8 },
            { StickLUp, AxisYN }, { StickLDown, AxisYP }, { StickLLeft, AxisXN }, { StickLRight, AxisXP },
            { StickRUp, RotationYN }, { StickRDown, RotationYP }, { StickRLeft, RotationXN }, { StickRRight, RotationXP },
        };

        // SDLController::kSwitchProController.
        private const string CemuSwitchPro = "030000007e0500000920000000006800";

        // Cemu's SDL settings (SDLControllerProvider::InitSDL).
        private static readonly Dictionary<string, string> CemuHints = new Dictionary<string, string>
        {
            ["SDL_JOYSTICK_ENHANCED_REPORTS"] = "1",
            ["SDL_JOYSTICK_HIDAPI_PS4"] = "1",
            ["SDL_JOYSTICK_HIDAPI_PS5"] = "1",
            ["SDL_JOYSTICK_HIDAPI_GAMECUBE"] = "1",
            ["SDL_JOYSTICK_HIDAPI_SWITCH"] = "1",
            ["SDL_JOYSTICK_HIDAPI_SWITCH2"] = "1",
            ["SDL_JOYSTICK_HIDAPI_JOY_CONS"] = "1",
            ["SDL_JOYSTICK_HIDAPI_STADIA"] = "1",
            ["SDL_JOYSTICK_HIDAPI_STEAM"] = "1",
            ["SDL_JOYSTICK_HIDAPI_LUNA"] = "1",
        };

        public static void Cemu(string installDir)
        {
            string portable = Path.Combine(installDir, "portable");
            if (!Directory.Exists(portable))
                return;
            string xml = Path.Combine(portable, "controllerProfiles", "controller0.xml");
            if (!Writable(xml))
                return;

            string api, uuid, name;
            bool motion = false;
            int[,] mapping;
            int slot = ControllerMonitor.FirstXInputSlot();
            if (slot >= 0)
            {
                api = "XInput";
                uuid = slot.ToString();
                name = "Controller 1";
                mapping = CemuXInput;
            }
            else
            {
                // Any other controller on Cemu's SDL, named as Cemu names it: the how-manieth
                // gamepad with this id, and the id (ControllerFactory, SDLController).
                List<ProbedPad> pads = ControllerProbe.Probe(CemuHints, gyro: true)?.Where(p => p.IsGamepad).ToList();
                ProbedPad pad = pads?.FirstOrDefault();
                if (pad == null)
                    return;
                int index = pads.Take(pads.IndexOf(pad)).Count(p => p.Guid == pad.Guid);
                api = "SDLController";
                uuid = $"{index}_{pad.Guid}";
                name = pad.Name;
                motion = pad.HasGyro;
                mapping = pad.Guid == CemuSwitchPro ? CemuSwitchProSdl() : CemuSdl;
            }

            var text = new StringBuilder();
            text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            text.Append("<emulated_controller>\n");
            text.Append("\t<type>Wii U GamePad</type>\n");
            text.Append("\t<controller>\n");
            text.Append($"\t\t<api>{api}</api>\n");
            text.Append($"\t\t<uuid>{uuid}</uuid>\n");
            text.Append($"\t\t<display_name>{System.Security.SecurityElement.Escape(name)}</display_name>\n");
            if (motion)
                text.Append("\t\t<motion>true</motion>\n");
            text.Append("\t\t<mappings>\n");
            for (int i = 0; i < mapping.GetLength(0); i++)
                text.Append($"\t\t\t<entry>\n\t\t\t\t<mapping>{mapping[i, 0]}</mapping>\n\t\t\t\t<button>{mapping[i, 1]}</button>\n\t\t\t</entry>\n");
            text.Append("\t\t</mappings>\n");
            text.Append("\t</controller>\n");
            text.Append("</emulated_controller>\n");
            Write("Cemu", xml, text.ToString());
        }

        private static int[,] CemuSwitchProSdl()
        {
            var mapping = (int[,])CemuSdl.Clone();
            for (int i = 0; i < mapping.GetLength(0); i++)
            {
                if (mapping[i, 0] == A) mapping[i, 1] = 0;
                else if (mapping[i, 0] == B) mapping[i, 1] = 1;
                else if (mapping[i, 0] == X) mapping[i, 1] = 2;
                else if (mapping[i, 0] == Y) mapping[i, 1] = 3;
            }
            return mapping;
        }

        public static void Rpcs3(string installDir)
        {
            string handler, device;
            int slot = ControllerMonitor.FirstXInputSlot();
            if (slot >= 0)
            {
                handler = "XInput";
                device = $"XInput Pad #{slot + 1}";
            }
            else
            {
                string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                List<ConnectedPad> pads = ControllerMonitor.Snapshot(folder, TimeSpan.FromSeconds(2));
                if (pads.Any(p => p.IsPs5))
                {
                    handler = "DualSense";
                    device = "DualSense Pad #1";
                }
                else if (pads.Any(p => p.IsPs4))
                {
                    handler = "DualShock 4";
                    device = "DS4 Pad #1";
                }
                else
                {
                    // Any other controller on RPCS3's SDL handler, named as it names one: SDL's
                    // gamepad name and how-manieth with that name (sdl_pad_handler.cpp).
                    if (!Writable(Rpcs3Yml(installDir)))
                        return;
                    List<ProbedPad> probed = ControllerProbe.Probe(Rpcs3Hints)?.Where(p => p.IsGamepad).ToList();
                    ProbedPad pad = probed?.FirstOrDefault();
                    if (pad == null)
                        return;
                    handler = "SDL";
                    device = $"{pad.Name} {1 + probed.Take(probed.IndexOf(pad)).Count(p => p.Name == pad.Name)}";
                }
            }
            Write("RPCS3", Rpcs3Yml(installDir), $"Player 1 Input:\n  Handler: {handler}\n  Device: {device}\n");
        }

        // RPCS3's settings: beside it, or in portable\ when that folder is there; on Windows in a
        // config\ folder inside that (fs::get_config_dir(true)).
        private static string Rpcs3Yml(string installDir)
        {
            string root = Directory.Exists(Path.Combine(installDir, "portable")) ? Path.Combine(installDir, "portable") : installDir;
            return Path.Combine(root, "config", "input_configs", "global", "Default.yml");
        }

        // RPCS3's SDL settings (sdl_instance.cpp).
        private static readonly Dictionary<string, string> Rpcs3Hints = new Dictionary<string, string>
        {
            ["SDL_JOYSTICK_HIDAPI_PS3_SIXAXIS_DRIVER"] = "1",
            ["SDL_JOYSTICK_HIDAPI_PS3"] = "1",
            ["SDL_JOYSTICK_HIDAPI_LG4FF"] = "0",
        };

        /// <summary>How many connected controllers SDL emulators map as gamepads (for controller 2).</summary>
        internal static int SdlGamepads()
        {
            string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return ControllerMonitor.Snapshot(folder, TimeSpan.FromSeconds(2)).Count(p => p.KnownToSdl);
        }

        // Azahar's 3DS buttons (Settings::NativeButton::mapping) on SDL game controller buttons,
        // as its own automatic mapping puts them for an Xbox-style pad (xinput_to_3ds_mapping):
        // by position, A on the right face button. SDL_GameControllerButton: A 0, B 1, X 2,
        // Y 3, BACK 4, START 6, LEFTSHOULDER 9, RIGHTSHOULDER 10, DPAD_UP 11 .. DPAD_RIGHT 14.
        private static readonly Dictionary<string, int> AzaharButtons = new Dictionary<string, int>
        {
            ["button_a"] = 1, ["button_b"] = 0, ["button_x"] = 3, ["button_y"] = 2,
            ["button_up"] = 11, ["button_down"] = 12, ["button_left"] = 13, ["button_right"] = 14,
            ["button_l"] = 9, ["button_r"] = 10, ["button_start"] = 6, ["button_select"] = 4,
        };

        /// <summary>
        /// Azahar's first input profile on any controller SDL knows, when one is connected and the
        /// profile is still the keyboard Azahar starts with (or what was written here before).
        /// </summary>
        public static void Azahar(string installDir)
        {
            string user = Path.Combine(installDir, "user");
            if (!Directory.Exists(user))
                return;   // not portable: its settings are elsewhere, and its own
            string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!ControllerMonitor.Snapshot(folder, TimeSpan.FromSeconds(2)).Any(p => p.KnownToSdl))
                return;

            string ini = Path.Combine(user, "config", "qt-config.ini");
            string mine = ini + ".romm-controls";
            const string prefix = @"profiles\1\";
            try
            {
                // Another profile chosen, or profile 1 bound by the user: theirs.
                string profile = QtValue(ini, "Controls", "profile");
                if (!string.IsNullOrEmpty(profile) && profile != "0")
                    return;
                var keys = AzaharButtons.Keys.Concat(new[] { "button_zl", "button_zr", "circle_pad", "c_stick" }).Select(k => prefix + k);
                // Qt: a value without its "\default" flag counts (QtConfig::ReadSetting).
                if (!StillDefaultOrMine(ini, keys, mine, prefix + "button_a", missingFlagIsDefault: false))
                    return;

                var values = new Dictionary<string, string>
                {
                    [prefix + "input_maptype\\default"] = "false",
                    [prefix + "input_maptype"] = "0",   // InputMappingType::AllControllers
                };
                if (QtValue(ini, "Controls", @"profiles\size") == null)
                    values[@"profiles\size"] = "1";
                foreach (KeyValuePair<string, int> button in AzaharButtons)
                    AzaharSet(values, prefix + button.Key, $"api:controller,button:{button.Value},engine:sdl,maptype:all");
                // ZL and ZR: the triggers, as axes (SDL_CONTROLLER_AXIS_TRIGGERLEFT 4, RIGHT 5).
                AzaharSet(values, prefix + "button_zl", "api:controller,axis:4,direction:+,engine:sdl,maptype:all,threshold:0.5");
                AzaharSet(values, prefix + "button_zr", "api:controller,axis:5,direction:+,engine:sdl,maptype:all,threshold:0.5");
                AzaharSet(values, prefix + "circle_pad", "api:controller,axis_x:0,axis_y:1,engine:sdl,maptype:all");
                AzaharSet(values, prefix + "c_stick", "api:controller,axis_x:2,axis_y:3,engine:sdl,maptype:all");

                if (Account.SettingsFile.SetIni(ini, "Controls", values, create: true, separator: "="))
                {
                    Remember(mine, values);
                    s_log.Info($"[EmulatorSetup] Azahar: controller 1 set to any connected controller ({ini}).");
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] Azahar: could not set controller 1: {ex.Message}");
            }
        }

        // A Qt setting with its "\default" flag off (else Azahar uses its own default), quoted as
        // Qt writes a value with commas (unquoted, Qt would read a list).
        private static void AzaharSet(Dictionary<string, string> values, string key, string value)
        {
            values[key + "\\default"] = "false";
            values[key] = "\"" + value + "\"";
        }

        /// <summary>
        /// Whether bindings in an ini file's [Controls] may be written here: each of these keys
        /// is either still the emulator's default (missing, its "\default" flag on, or a keyboard
        /// key, as the emulators' defaults are) or exactly
        /// what was written here last (the record beside the file: Remember). One changed by the
        /// user, any of them, and none are touched. A record from before every key was kept (just
        /// the value of <paramref name="legacyKey"/>) stands for that one key.
        /// </summary>
        private static bool StillDefaultOrMine(string ini, IEnumerable<string> keys, string mine, string legacyKey, bool missingFlagIsDefault)
        {
            var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(mine))
            {
                string[] lines = File.ReadAllLines(mine);
                if (lines.Any(l => l.IndexOf('\t') >= 0))
                {
                    foreach (string line in lines)
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0)
                            recorded[line.Substring(0, tab)] = line.Substring(tab + 1);
                    }
                }
                else if (lines.Length > 0)
                    recorded[legacyKey] = File.ReadAllText(mine);
            }

            foreach (string key in keys)
            {
                string value = QtValue(ini, "Controls", key);
                string flag = QtValue(ini, "Controls", key + "\\default");
                if (recorded.TryGetValue(key, out string written) && value == written)
                    continue;   // as written here
                // A keyboard binding is not a controller someone chose: the emulator's own
                // defaults are keyboard keys, and Eden writes them back as set (its motion
                // default, "engine:keyboard,code:56,toggle:0", with its flag off).
                bool isDefault = value == null
                    || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
                    || (missingFlagIsDefault && flag == null)
                    || value.IndexOf("engine:keyboard", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isDefault)
                    return false;
            }
            return true;
        }

        // What was written, every key and value, for StillDefaultOrMine to know it again.
        private static void Remember(string mine, Dictionary<string, string> values)
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> value in values)
            {
                if (value.Value != null)
                    text.Append(value.Key).Append('\t').Append(value.Value).Append("\r\n");
            }
            File.WriteAllText(mine, text.ToString(), new UTF8Encoding(false));
        }

        // A key's value in a Qt ini file's section ("key=value"), or null.
        private static string QtValue(string path, string section, string key)
        {
            if (!File.Exists(path))
                return null;
            bool inSection = false;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inSection = string.Equals(line, "[" + section + "]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection)
                    continue;
                int equals = line.IndexOf('=');
                if (equals > 0 && string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(equals + 1).Trim();
            }
            return null;
        }

        // ---- Eden ------------------------------------------------------------------------------

        // Eden's Switch buttons (settings_input.cpp, NativeButton::mapping) on SDL gamepad
        // elements, as its own default mapping has them (SDLDriver::GetDefaultButtonBinding:
        // by position, A on the right face button; SL/SR on the shoulders for a whole
        // controller). Home is left off: the Home button opens RomMnite's overlay.
        private static readonly string[,] EdenButtons =
        {
            { "button_a", "b" }, { "button_b", "a" }, { "button_x", "y" }, { "button_y", "x" },
            { "button_lstick", "leftstick" }, { "button_rstick", "rightstick" },
            { "button_l", "leftshoulder" }, { "button_r", "rightshoulder" },
            { "button_zl", "lefttrigger" }, { "button_zr", "righttrigger" },
            { "button_plus", "start" }, { "button_minus", "back" },
            { "button_dleft", "dpleft" }, { "button_dup", "dpup" }, { "button_dright", "dpright" }, { "button_ddown", "dpdown" },
            { "button_slleft", "leftshoulder" }, { "button_srleft", "rightshoulder" },
            { "button_slright", "leftshoulder" }, { "button_srright", "rightshoulder" },
            { "button_screenshot", "misc1" },
        };

        /// <summary>
        /// Eden's player 1 on the first gamepad its SDL sees, as Eden's own "default" mapping
        /// binds one (sdl_driver.cpp, GetButtonMappingForDevice, GetAnalogMappingForDevice,
        /// GetMotionMappingForDevice), when player 1 is still the keyboard Eden starts with (or
        /// what was written here before). Eden names the controller by SDL's id with the name's
        /// checksum zeroed, and its inputs by the joystick's own numbers, which SDL3 is asked for
        /// with Eden's settings (ControllerProbe).
        /// </summary>
        public static void Eden(string installDir)
        {
            string config = Path.Combine(installDir, "user", "config");
            if (!Directory.Exists(Path.Combine(installDir, "user")))
                return;   // not portable: its settings are elsewhere, and its own
            string ini = Path.Combine(config, "qt-config.ini");
            string mine = ini + ".romm-controls";
            try
            {
                // Player 1 bound by the user (any of it): theirs. A string setting whose
                // "\default" flag is missing or true is Eden's default (Config::ReadStringSetting).
                var keys = Enumerable.Range(0, EdenButtons.GetLength(0)).Select(i => EdenButtons[i, 0])
                    .Concat(new[] { "lstick", "rstick", "motionleft", "motionright" })
                    .Select(k => "player_0_" + k);
                if (!StillDefaultOrMine(ini, keys, mine, "player_0_button_a", missingFlagIsDefault: true))
                {
                    s_log.Info($"[EmulatorSetup] Eden: player 1 left as it is: its bindings were changed outside RomMnite ({ini}).");
                    return;
                }

                List<ProbedPad> pads = ControllerProbe.Probe(EdenHints(ini), gyro: true);
                ProbedPad pad = pads?.FirstOrDefault(p => p.IsGamepad);
                if (pad == null)
                {
                    s_log.Info(pads == null
                        ? "[EmulatorSetup] Eden: player 1 not set: SDL3 is not available yet."
                        : "[EmulatorSetup] Eden: player 1 not set: SDL3 sees no gamepad.");
                    return;
                }
                // Its port: the how-manieth controller with the same id (SDLDriver::InitJoystick).
                int port = pads.Take(pads.IndexOf(pad)).Count(p => p.GuidWithoutCrc == pad.GuidWithoutCrc);
                string guid = pad.GuidWithoutCrc;

                var values = new Dictionary<string, string>();
                for (int i = 0; i < EdenButtons.GetLength(0); i++)
                {
                    string param = EdenButtonParam(pad, EdenButtons[i, 1], guid, port);
                    if (param != null)
                        EdenSet(values, "player_0_" + EdenButtons[i, 0], param);
                }
                string lstick = EdenStickParam(pad, "leftx", "lefty", guid, port);
                string rstick = EdenStickParam(pad, "rightx", "righty", guid, port);
                if (lstick != null)
                    EdenSet(values, "player_0_lstick", lstick);
                if (rstick != null)
                    EdenSet(values, "player_0_rstick", rstick);
                if (pad.HasGyro)
                {
                    // One controller: both motion inputs on its gyroscope (GetMotionMappingForDevice).
                    string motion = $"engine:sdl,guid:{guid},motion:0,port:{port}";
                    EdenSet(values, "player_0_motionleft", motion);
                    EdenSet(values, "player_0_motionright", motion);
                }
                if (!values.ContainsKey("player_0_button_a"))
                    return;   // nothing usable in its mapping

                // What a controller used before has and this one does not (a PlayStation pad's
                // gyroscope, a button this one lacks): back to Eden's default, so nothing is left
                // bound to a controller no longer there.
                foreach (string key in keys)
                {
                    if (values.ContainsKey(key))
                        continue;
                    values[key + "\\default"] = "true";
                    values[key] = null;   // removed (SetIni)
                }

                if (Account.SettingsFile.SetIni(ini, "Controls", values, create: true, separator: "="))
                {
                    Remember(mine, values);
                    s_log.Info($"[EmulatorSetup] Eden: controller 1 set to {pad.Name}, id {guid}, port {port} ({ini}).");
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] Eden: could not set controller 1: {ex.Message}");
            }
        }

        // Eden's SDL settings (SDLDriver::SDLDriver), from its own options where it has them
        // (settings.h: enable_raw_input off, disable_wgi_xinput off, enable_joycon_driver on,
        // enable_procon_driver off, unless set).
        private static Dictionary<string, string> EdenHints(string ini)
        {
            bool rawInput = EdenOption(ini, "enable_raw_input", false);
            bool noWgi = EdenOption(ini, "disable_wgi_xinput", false);
            bool joyconDriver = EdenOption(ini, "enable_joycon_driver", true);
            bool proconDriver = EdenOption(ini, "enable_procon_driver", false);
            var hints = new Dictionary<string, string>
            {
                ["SDL_JOYSTICK_RAWINPUT"] = rawInput ? "1" : "0",
                ["SDL_JOYSTICK_HIDAPI_STEAM"] = "1",
                ["SDL_JOYSTICK_ENHANCED_REPORTS"] = "1",
                ["SDL_JOYSTICK_HIDAPI_JOY_CONS"] = joyconDriver ? "0" : "1",
                ["SDL_JOYSTICK_HIDAPI_SWITCH"] = proconDriver ? "0" : "1",
                ["SDL_JOYSTICK_HIDAPI_XBOX"] = "0",
            };
            if (!joyconDriver)
            {
                hints["SDL_JOYSTICK_HIDAPI_COMBINE_JOY_CONS"] = "0";
                hints["SDL_JOYSTICK_HIDAPI_VERTICAL_JOY_CONS"] = "1";
            }
            if (noWgi)
            {
                hints["SDL_JOYSTICK_RAWINPUT_CORRELATE_XINPUT"] = "0";
                hints["SDL_JOYSTICK_WGI"] = "0";
            }
            return hints;
        }

        // A boolean of Eden's [Controls] (Config::ReadBooleanSetting: the "\default" flag, else
        // the value, else the default).
        private static bool EdenOption(string ini, string key, bool fallback)
        {
            if (string.Equals(QtValue(ini, "Controls", key + @"\default"), "true", StringComparison.OrdinalIgnoreCase))
                return fallback;
            string value = QtValue(ini, "Controls", key);
            if (value == null)
                return fallback;
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        // A button as Eden binds one (BuildParamPackageForBinding): the joystick's button, hat
        // direction or axis that SDL's mapping puts behind the gamepad element; null when none.
        private static string EdenButtonParam(ProbedPad pad, string element, string guid, int port)
        {
            if (!pad.Mapping.TryGetValue(element, out string input))
                return null;
            input = input.TrimStart('+', '-').TrimEnd('~');
            if (input.Length < 2)
                return null;
            int number;
            switch (input[0])
            {
                case 'b':
                    return int.TryParse(input.Substring(1), out number) ? $"button:{number},engine:sdl,guid:{guid},port:{port}" : null;
                case 'a':
                    // An axis as a button: past half way, either way round as Eden writes it.
                    return int.TryParse(input.Substring(1), out number)
                        ? $"axis:{number},engine:sdl,guid:{guid},invert:+,port:{port},threshold:0.5" : null;
                case 'h':
                    string[] hat = input.Substring(1).Split('.');
                    if (hat.Length != 2 || !int.TryParse(hat[0], out number) || !int.TryParse(hat[1], out int mask))
                        return null;
                    string direction = mask == 1 ? "up" : mask == 2 ? "right" : mask == 4 ? "down" : mask == 8 ? "left" : null;
                    return direction == null ? null : $"direction:{direction},engine:sdl,guid:{guid},hat:{number},port:{port}";
                default:
                    return null;
            }
        }

        // A stick as Eden binds one (BuildParamPackageForAnalog), with no drift to take off (the
        // offsets Eden measures as the stick rests when it is mapped by hand).
        private static string EdenStickParam(ProbedPad pad, string x, string y, string guid, int port)
        {
            if (!pad.Mapping.TryGetValue(x, out string xi) || !pad.Mapping.TryGetValue(y, out string yi))
                return null;
            xi = xi.TrimStart('+', '-').TrimEnd('~');
            yi = yi.TrimStart('+', '-').TrimEnd('~');
            if (xi.Length < 2 || yi.Length < 2 || xi[0] != 'a' || yi[0] != 'a'
                || !int.TryParse(xi.Substring(1), out int ax) || !int.TryParse(yi.Substring(1), out int ay))
                return null;
            return $"axis_x:{ax},axis_y:{ay},engine:sdl,guid:{guid},invert_x:+,invert_y:+,offset_x:0.000000,offset_y:0.000000,port:{port}";
        }

        // A string setting with its "\default" flag off, quoted as Eden writes a value with commas
        // (Config::AdjustOutputString).
        private static void EdenSet(Dictionary<string, string> values, string key, string value)
        {
            values[key + "\\default"] = "false";
            values[key] = "\"" + value + "\"";
        }

        // Whether Write would write the file: missing, or still what was written here last.
        private static bool Writable(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return true;
                string mine = path + ".romm";
                return File.Exists(mine) && File.ReadAllText(mine) == File.ReadAllText(path);
            }
            catch
            {
                return false;
            }
        }

        // Writes the file when it is missing or still what was written here last (the copy kept
        // beside it says what that was).
        private static void Write(string emulator, string path, string content)
        {
            try
            {
                string mine = path + ".romm";
                if (File.Exists(path))
                {
                    string current = File.ReadAllText(path);
                    if (current == content)
                        return;
                    if (!File.Exists(mine) || File.ReadAllText(mine) != current)
                        return;   // the user's own
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(path, content, utf8);
                File.WriteAllText(mine, content, utf8);
                s_log.Info($"[EmulatorSetup] {emulator}: controller 1 set to the connected controller ({path}).");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[EmulatorSetup] {emulator}: could not set controller 1: {ex.Message}");
            }
        }
    }
}
