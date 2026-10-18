using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>
    /// The community's list of controller layouts, SDL_GameControllerDB
    /// (github.com/mdqinc/SDL_GameControllerDB, zlib licence): gamecontrollerdb.txt, one line
    /// per controller, "GUID,name,a:b0,b:b1,...,platform:Windows,". It knows thousands of
    /// controllers SDL's own built-in list does not (generic USB pads, arcade sticks, adapters).
    ///
    /// Two uses:
    ///
    ///   The emulators: SDL (2.0.10 and later, and SDL3) loads the file named by the
    ///   SDL_GAMECONTROLLERCONFIG_FILE variable when its controller support starts, and a
    ///   variable set in the environment comes before the program's own hints (SDL_hints.c).
    ///   Programs inherit Playnite's environment, so every SDL emulator Playnite starts knows
    ///   these controllers too, with nothing written in their folders.
    ///
    ///   The library's Controller section: which connected controllers are in the list
    ///   (<see cref="NameFor"/>), so it can say a controller SDL's own list does not know will
    ///   still work in an SDL emulator. It is not handed to the monitor's own SDL, so the monitor
    ///   still tells apart what SDL knows by itself (what an emulator that does not use SDL is
    ///   likely to know too) from what only this list adds.
    ///
    /// Kept in Controllers\gamecontrollerdb.txt in the plugin's data, fetched when missing and
    /// again after two weeks, in the background (Foreground).
    /// </summary>
    public static class ControllerDatabase
    {
        private const string Url = "https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt";
        private const string Variable = "SDL_GAMECONTROLLERCONFIG_FILE";
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);
        private static readonly ILogger s_log = LogManager.GetLogger();

        private static readonly object s_lock = new object();
        private static Dictionary<string, string> s_names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> s_lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string s_file;
        private static int s_started;

        /// <summary>Raised (off the UI thread) when the list has been read anew.</summary>
        public static event Action Changed;

        /// <summary>
        /// Reads the list kept on disk, if any, and hands it to the emulators; then fetches a newer
        /// one in the background when it is missing or old. Once.
        /// </summary>
        public static void Start(string dataFolder)
        {
            if (string.IsNullOrEmpty(dataFolder) || Interlocked.Exchange(ref s_started, 1) == 1)
                return;
            s_file = Path.Combine(dataFolder, "Controllers", "gamecontrollerdb.txt");
            Task.Run(async () =>
            {
                try
                {
                    if (File.Exists(s_file))
                        Use();
                    if (File.Exists(s_file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(s_file) < KeepFor)
                        return;
                    await Foreground.WaitQuietAsync().ConfigureAwait(false);
                    if (await FetchAsync().ConfigureAwait(false))
                        Use();
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Controllers] Could not update the controller list: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// The list's name for a connected controller, from its SDL GUID (32 hex digits), or
        /// null when it is not in the list (Windows lines only).
        /// </summary>
        public static string NameFor(string guid)
        {
            if (string.IsNullOrEmpty(guid) || guid.Length != 32)
                return null;
            lock (s_lock)
            {
                if (s_names.Count == 0)
                    return null;
                // As SDL matches: the GUID as it is, then without what newer SDL adds to it (a
                // CRC of the name, the device's version, the driver's mark), which the list's
                // lines mostly leave as zeros.
                if (s_names.TryGetValue(guid, out string name) || s_names.TryGetValue(Plain(guid), out name))
                    return name;
                return null;
            }
        }

        /// <summary>
        /// The list's whole line for a connected controller (its mapping: "guid,name,a:b1,
        /// guide:b12,..."), by its SDL GUID as <see cref="NameFor"/> finds it; null when it is not
        /// listed. For reading its buttons directly (Overlay.HidPads), as SDL would map them.
        /// </summary>
        public static string MappingFor(string guid)
        {
            if (string.IsNullOrEmpty(guid) || guid.Length != 32)
                return null;
            lock (s_lock)
            {
                if (s_lines.TryGetValue(guid, out string line) || s_lines.TryGetValue(Plain(guid), out line))
                    return line;
                return null;
            }
        }

        // The GUID without its CRC (hex 4-7), version (24-27) and driver mark (28-31).
        private static string Plain(string guid) =>
            guid.Substring(0, 4) + "0000" + guid.Substring(8, 16) + "00000000";

        // Reads the file's Windows lines and names it to the emulators started from now on.
        private static void Use()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(s_file))
            {
                if (line.Length < 34 || line[0] == '#' || line[32] != ',' || line.IndexOf("platform:Windows", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                string guid = line.Substring(0, 32);
                int end = line.IndexOf(',', 33);
                if (end < 0)
                    continue;
                string name = line.Substring(33, end - 33).Trim();
                if (name.Length == 0)
                    continue;
                if (!names.ContainsKey(guid))
                {
                    names[guid] = name;
                    lines[guid] = line.Trim();
                }
                string plain = Plain(guid);
                if (!names.ContainsKey(plain))
                {
                    names[plain] = name;
                    lines[plain] = line.Trim();
                }
            }
            if (names.Count == 0)
                return;
            lock (s_lock)
            {
                s_names = names;
                s_lines = lines;
            }

            // For the emulators Playnite starts (they inherit its environment). Not the user's
            // own setting, should they have one.
            string own = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrEmpty(own) || string.Equals(own, s_file, StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable(Variable, s_file);
            else
                s_log.Info($"[Controllers] {Variable} is already set ({own}); left as it is.");

            s_log.Info($"[Controllers] The controller list knows {names.Count / 2} Windows controllers or more ({s_file}).");
            Changed?.Invoke();
        }

        // Downloads the list; true when a usable one replaced the one kept.
        private static async Task<bool> FetchAsync()
        {
            string temp = s_file + ".new";
            try
            {
                string text;
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("RomMnite");
                    text = await client.GetStringAsync(Url).ConfigureAwait(false);
                }
                // A page of something else (an error, a moved file) is not taken for the list.
                int windows = 0;
                foreach (string line in text.Split('\n'))
                {
                    if (line.Length > 33 && line[32] == ',' && line.IndexOf("platform:Windows", StringComparison.OrdinalIgnoreCase) >= 0)
                        windows++;
                }
                if (windows < 100)
                {
                    s_log.Warn($"[Controllers] The controller list downloaded has {windows} Windows lines; not used.");
                    return false;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(s_file));
                File.WriteAllText(temp, text);
                if (File.Exists(s_file))
                    File.Delete(s_file);
                File.Move(temp, s_file);
                return true;
            }
            catch (Exception ex)
            {
                s_log.Info($"[Controllers] Could not download the controller list: {ex.Message}");
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch
                {
                }
                return false;
            }
        }
    }
}
