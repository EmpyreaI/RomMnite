using System.Collections.Generic;

namespace RomM.Setup
{
    /// <summary>
    /// A platform's graphics, as set on one of its games' pages (Games/EmulationStatusControl)
    /// and kept for every game of the platform. Null for each one left as recommended
    /// (GraphicsOptions.Recommended), so a better recommendation later reaches it too.
    /// </summary>
    public sealed class PlatformGraphics
    {
        /// <summary>"Original", "2x", "3x", "4x" or "Screen"; null: recommended.</summary>
        public string Resolution { get; set; }
        public bool? Widescreen { get; set; }
        public bool? Stretch { get; set; }
        public bool? VSync { get; set; }

        internal bool IsEmpty => Resolution == null && !Widescreen.HasValue && !Stretch.HasValue && !VSync.HasValue;
    }

    /// <summary>
    /// What each emulator's graphics can be set to here (EmulatorGraphics writes them), and the
    /// most it is recommended to have: its games drawn at the screen's own resolution, with VSync,
    /// not stretched, and widescreen where that only applies fixes made for each game (PCSX2's
    /// widescreen patches) rather than a hack across every game (DuckStation's, Dolphin's,
    /// Flycast's, which glitch in some).
    /// </summary>
    internal static class GraphicsOptions
    {
        internal sealed class Support
        {
            public bool Widescreen;
            public bool Stretch;
            /// <summary>What widescreen does here, said under it.</summary>
            public string WidescreenNote;
            public bool WidescreenRecommended;
        }

        /// <param name="id">The emulator's built-in id and name, lower case (as EmulatorLaunch has it).</param>
        /// <returns>Null when RomMnite sets no graphics for it (RetroArch, Cemu, Eden ...).</returns>
        public static Support For(string id)
        {
            id = id ?? "";
            if (id.Contains("pcsx2"))
                return new Support { Widescreen = true, Stretch = true, WidescreenRecommended = true, WidescreenNote = "Widescreen patches made for each game, where there is one." };
            if (id.Contains("duckstation"))
                return new Support { Widescreen = true, Stretch = true, WidescreenNote = "Shows more of the scene. Some games glitch." };
            if (id.Contains("dolphin"))
                return new Support { Widescreen = true, Stretch = true, WidescreenNote = "Shows more of the scene. Some games glitch." };
            if (id.Contains("flycast"))
                return new Support { Widescreen = true, Stretch = true, WidescreenNote = "Shows more of the scene. Some games glitch." };
            if (id.Contains("rpcs3") || id.Contains("xenia") || id.Contains("vita3k") || id.Contains("xemu"))
                return new Support { Stretch = true };
            if (id.Contains("azahar") || id.Contains("ppsspp"))
                return new Support();
            return null;
        }

        public static GraphicsChoice Recommended(Support support) => new GraphicsChoice
        {
            Resolution = "Screen",
            Widescreen = support != null && support.Widescreen && support.WidescreenRecommended,
            Stretch = false,
            VSync = true,
        };

        /// <summary>The platform's graphics for its emulator: what was set, the rest recommended.</summary>
        public static GraphicsChoice Resolve(PlatformGraphics saved, string id)
        {
            Support support = For(id);
            GraphicsChoice choice = Recommended(support);
            if (saved == null || support == null)
                return choice;
            if (saved.Resolution != null)
                choice.Resolution = saved.Resolution;
            if (saved.Widescreen.HasValue && support.Widescreen)
                choice.Widescreen = saved.Widescreen.Value;
            if (saved.Stretch.HasValue && support.Stretch)
                choice.Stretch = saved.Stretch.Value;
            if (saved.VSync.HasValue)
                choice.VSync = saved.VSync.Value;
            return choice;
        }

        internal static readonly KeyValuePair<string, string>[] Resolutions =
        {
            new KeyValuePair<string, string>("Screen", "Match my screen"),
            new KeyValuePair<string, string>("4x", "4x"),
            new KeyValuePair<string, string>("3x", "3x"),
            new KeyValuePair<string, string>("2x", "2x"),
            new KeyValuePair<string, string>("Original", "Original"),
        };
    }
}
