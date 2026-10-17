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
    ///   Dolphin (GameCube)  User\Config\GCPadNew.ini [GCPad1], Device = XInput/0/Gamepad, its
    ///                       buttons, sticks, triggers and D-pad on the Xbox pad's (Dolphin's
    ///                       XInput backend names: XInput.cpp)
    ///   Cemu                portable\controllerProfiles\controller0.xml, a Wii U GamePad on
    ///                       XInput controller 0 with the mapping Cemu's own "default" button
    ///                       gives an XInput pad (VPADController::set_default_mapping)
    ///   RPCS3               config\input_configs\global\Default.yml, player 1 on its XInput,
    ///                       DualShock 4 or DualSense handler ("XInput Pad #1", "DS4 Pad #1",
    ///                       "DualSense Pad #1"), the handler's own default bindings
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
            if (!ControllerMonitor.XInputConnected())
                return;
            string user = Path.Combine(installDir, "User");
            if (!File.Exists(Path.Combine(installDir, "portable.txt")) && !Directory.Exists(user))
                return;   // not portable: its settings are elsewhere, and its own
            string ini = Path.Combine(user, "Config", "GCPadNew.ini");
            var lines = new List<string>
            {
                "[GCPad1]",
                "Device = XInput/0/Gamepad",
                "Buttons/A = `Button A`",
                "Buttons/B = `Button B`",
                "Buttons/X = `Button X`",
                "Buttons/Y = `Button Y`",
                "Buttons/Z = `Shoulder R`",
                "Buttons/Start = Start",
                "Main Stick/Up = `Left Y+`",
                "Main Stick/Down = `Left Y-`",
                "Main Stick/Left = `Left X-`",
                "Main Stick/Right = `Left X+`",
                "C-Stick/Up = `Right Y+`",
                "C-Stick/Down = `Right Y-`",
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
            Write("Dolphin", ini, string.Join("\r\n", lines) + "\r\n");
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

        public static void Cemu(string installDir)
        {
            if (!ControllerMonitor.XInputConnected())
                return;
            string portable = Path.Combine(installDir, "portable");
            if (!Directory.Exists(portable))
                return;
            string xml = Path.Combine(portable, "controllerProfiles", "controller0.xml");
            var text = new StringBuilder();
            text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            text.Append("<emulated_controller>\n");
            text.Append("\t<type>Wii U GamePad</type>\n");
            text.Append("\t<controller>\n");
            text.Append("\t\t<api>XInput</api>\n");
            text.Append("\t\t<uuid>0</uuid>\n");
            text.Append("\t\t<display_name>Controller 1</display_name>\n");
            text.Append("\t\t<mappings>\n");
            for (int i = 0; i < CemuXInput.GetLength(0); i++)
                text.Append($"\t\t\t<entry>\n\t\t\t\t<mapping>{CemuXInput[i, 0]}</mapping>\n\t\t\t\t<button>{CemuXInput[i, 1]}</button>\n\t\t\t</entry>\n");
            text.Append("\t\t</mappings>\n");
            text.Append("\t</controller>\n");
            text.Append("</emulated_controller>\n");
            Write("Cemu", xml, text.ToString());
        }

        public static void Rpcs3(string installDir)
        {
            string handler, device;
            if (ControllerMonitor.XInputConnected())
            {
                handler = "XInput";
                device = "XInput Pad #1";
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
                    return;
                }
            }
            // RPCS3's settings: beside it, or in portable\ when that folder is there; on Windows
            // in a config\ folder inside that (fs::get_config_dir(true)).
            string root = Directory.Exists(Path.Combine(installDir, "portable")) ? Path.Combine(installDir, "portable") : installDir;
            string yml = Path.Combine(root, "config", "input_configs", "global", "Default.yml");
            Write("RPCS3", yml, $"Player 1 Input:\n  Handler: {handler}\n  Device: {device}\n");
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
