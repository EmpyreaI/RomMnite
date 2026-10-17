using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Games
{
    /// <summary>
    /// Which of a game's files Playnite is to start, so Play starts the game without asking.
    /// A disc dumped as tracks (a .cue and its .bin files, a .gdi and its tracks...) is started
    /// from its sheet, never from a track: with the sheet present, the tracks are not offered.
    ///
    ///   an .m3u (the game's discs) when there is one and the emulator takes it
    ///   else the sheet: .cue, then .gdi, .ccd, .mds; several discs' sheets go in an .m3u made
    ///   for them when the emulator takes one, else each is offered (a choice of disc)
    ///   else every file, as before
    /// </summary>
    internal static class LaunchFiles
    {
        private static readonly string[] Sheets = { ".cue", ".gdi", ".ccd", ".mds" };

        private static bool Is(string path, string extension) =>
            string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

        /// <param name="files">The game's files, full paths.</param>
        /// <param name="m3uSupported">Whether the emulator takes .m3u (an .m3u may then be used or made).</param>
        /// <param name="makeM3uIn">Where an .m3u for several discs may be written (null: none is made).</param>
        public static List<string> Pick(IEnumerable<string> files, bool m3uSupported, string makeM3uIn = null, string gameName = null)
        {
            List<string> all = (files ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count <= 1)
                return all;

            string m3u = all.FirstOrDefault(f => Is(f, ".m3u"));
            if (m3u != null && m3uSupported)
                return new List<string> { m3u };

            foreach (string sheet in Sheets)
            {
                List<string> discs = all.Where(f => Is(f, sheet)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (discs.Count == 0)
                    continue;
                if (discs.Count == 1)
                    return discs;
                if (m3uSupported && makeM3uIn != null)
                {
                    try
                    {
                        string name = string.Concat((gameName ?? "discs").Split(Path.GetInvalidFileNameChars()));
                        string path = Path.Combine(makeM3uIn, name + ".m3u");
                        File.WriteAllLines(path, discs.Select(d => MakeRelative(makeM3uIn, d)));
                        return new List<string> { path };
                    }
                    catch
                    {
                        // Not writable: the discs are offered one by one.
                    }
                }
                return discs;
            }

            return all.Where(f => !Is(f, ".m3u")).ToList();
        }

        private static string MakeRelative(string folder, string path)
        {
            string root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        /// <summary>The rank of a file as the one to start: sheets first (lower is better).</summary>
        public static int Rank(string fileName)
        {
            if (Is(fileName, ".m3u"))
                return 0;
            int sheet = Array.FindIndex(Sheets, s => Is(fileName, s));
            return sheet >= 0 ? 1 + sheet : 10;
        }
    }
}
