using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Models.RomM.Rom;
using RomM.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>
    /// The server's artwork of the games in the library, kept on this computer, so the pages
    /// that show it do so at once, also after Playnite restarts and without the server: each
    /// game's cover, fan art and logo, the artwork the library uses. Not its screenshots or
    /// videos (the Store's, fetched as they are looked at). Kept in ArtCache\ in the plugin's
    /// data, one file per picture (StoreImages.ArtFolder); which pictures are a game's in
    /// ArtCache.v2.json.
    ///
    /// Games added to the library are fetched as they come, and the library's games without
    /// their artwork once the library is up (after the library and the Store's Home: last of
    /// all, one picture at a time, in StoreImages' background lane, and only while the user is
    /// not doing anything else: see Foreground). A game that leaves the
    /// library takes its pictures with it, unless another library game shows the same one.
    /// </summary>
    internal sealed class LibraryArtCache
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly RomM _plugin;
        private readonly string _index;
        private readonly object _lock = new object();
        private Dictionary<int, List<string>> _byRom = new Dictionary<int, List<string>>();   // rom id -> its picture addresses
        private readonly Queue<int> _queue = new Queue<int>();
        private readonly HashSet<int> _queued = new HashSet<int>();
        private bool _started;
        private int _working;

        public LibraryArtCache(RomM plugin, string dataFolder)
        {
            _plugin = plugin;
            _index = Path.Combine(dataFolder, "ArtCache.v2.json");
            StoreImages.ArtFolder = Path.Combine(dataFolder, "ArtCache");
            // The first version kept screenshots too: it is let go whole, and the library's
            // artwork fetched again.
            try
            {
                string first = Path.Combine(dataFolder, "ArtCache.json");
                if (File.Exists(first))
                {
                    if (Directory.Exists(StoreImages.ArtFolder))
                        Directory.Delete(StoreImages.ArtFolder, true);
                    File.Delete(first);
                    s_log.Info("[ArtCache] Kept screenshots removed; only covers, fan art and logos are kept now.");
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[ArtCache] Could not remove the first kept artwork: {ex.Message}");
            }
            try
            {
                if (File.Exists(_index))
                    _byRom = JsonConvert.DeserializeObject<Dictionary<int, List<string>>>(File.ReadAllText(_index)) ?? new Dictionary<int, List<string>>();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[ArtCache] Could not read the list of kept artwork: {ex.Message}");
            }
            Publish();
        }

        private string Host => (_plugin.Settings?.RomMHost ?? "").TrimEnd('/');

        /// <summary>
        /// The library is up: its games whose artwork is not on disk yet are fetched, after
        /// anything already asked for. Once.
        /// </summary>
        public void Start()
        {
            if (_started)
                return;
            _started = true;
            try
            {
                foreach (Game game in _plugin.Playnite.Database.Games.Where(g => g.PluginId == _plugin.Id).ToList())
                {
                    if (!RomMGameId.TryParse(game.GameId, out int romId, out string _))
                        continue;
                    List<string> known;
                    lock (_lock)
                        _byRom.TryGetValue(romId, out known);
                    // Listed and all on disk: nothing to do.
                    if (known != null && known.All(StoreImages.IsKept))
                        continue;
                    Enqueue(romId);
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[ArtCache] Could not look through the library: {ex.Message}");
            }
            Pump();
        }

        /// <summary>Games added to the library: their artwork fetched (once the library is up).</summary>
        public void Added(IEnumerable<Game> games)
        {
            foreach (Game game in games ?? Enumerable.Empty<Game>())
            {
                if (game?.PluginId == _plugin.Id && RomMGameId.TryParse(game.GameId, out int romId, out string _))
                    Enqueue(romId);
            }
            if (_started)
                Pump();
        }

        /// <summary>Games removed from the library: their pictures go, unless another game shows them.</summary>
        public void Removed(IEnumerable<Game> games)
        {
            var gone = new List<string>();
            lock (_lock)
            {
                foreach (Game game in games ?? Enumerable.Empty<Game>())
                {
                    if (game?.PluginId != _plugin.Id || !RomMGameId.TryParse(game.GameId, out int romId, out string _))
                        continue;
                    // Another copy of the same rom still here (another account's): kept.
                    bool stillHere = _plugin.Playnite.Database.Games.Any(g => g.Id != game.Id && g.PluginId == _plugin.Id
                        && RomMGameId.TryParse(g.GameId, out int other, out string _) && other == romId);
                    if (stillHere || !_byRom.TryGetValue(romId, out List<string> urls))
                        continue;
                    _byRom.Remove(romId);
                    var inUse = new HashSet<string>(_byRom.Values.SelectMany(u => u), StringComparer.OrdinalIgnoreCase);
                    gone.AddRange(urls.Where(u => !inUse.Contains(u)));
                }
            }
            if (gone.Count == 0)
                return;
            Task.Run(() =>
            {
                foreach (string url in gone)
                    StoreImages.Forget(url);
            });
            Save();
            Publish();
        }

        private void Enqueue(int romId)
        {
            lock (_lock)
            {
                if (_queued.Add(romId))
                    _queue.Enqueue(romId);
            }
        }

        // One game at a time, one picture at a time, in the background.
        private void Pump()
        {
            if (Interlocked.Exchange(ref _working, 1) == 1)
                return;
            Task.Run(async () =>
            {
                int games = 0, pictures = 0;
                try
                {
                    while (true)
                    {
                        int romId;
                        lock (_lock)
                        {
                            if (_queue.Count == 0)
                                break;
                            romId = _queue.Dequeue();
                            _queued.Remove(romId);
                        }
                        await Foreground.WaitQuietAsync().ConfigureAwait(false);
                        List<string> urls = Urls(romId);
                        if (urls == null)
                            continue;   // not reached: tried again next start
                        lock (_lock)
                            _byRom[romId] = urls;
                        Save();
                        Publish();
                        foreach (string url in urls)
                        {
                            if (!StoreImages.IsKept(url) && await StoreImages.KeepAsync(url).ConfigureAwait(false))
                                pictures++;
                        }
                        games++;
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[ArtCache] {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _working, 0);
                    if (games > 0)
                        s_log.Info($"[ArtCache] Artwork of {games} library games kept on this computer ({pictures} pictures downloaded).");
                }
                // Asked for meanwhile.
                bool more;
                lock (_lock)
                    more = _queue.Count > 0;
                if (more)
                    Pump();
            });
        }

        // The game's artwork, as the Store's and the library's pages ask for it: null when the
        // server was not reached.
        private List<string> Urls(int romId)
        {
            string host = Host;
            if (host.Length == 0)
                return null;
            try
            {
                RomMRom rom;
                using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/roms/{romId}").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    rom = JsonConvert.DeserializeObject<RomMRom>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                }
                if (rom == null)
                    return null;
                // The cover, the fan art and (below) the logo: the library's artwork.
                var paths = new List<string> { rom.PathCoverL, rom.PathCoverS };
                if (!string.IsNullOrEmpty(rom.SSMetadata?.FanartPath))
                    paths.Add("/assets/romm/resources/" + rom.SSMetadata.FanartPath);
                var urls = paths.Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => host + (p.StartsWith("/") ? p : "/" + p))
                    .ToList();
                string logo = RomMMetadataMapper.LogoUrl(host, rom, out bool _);
                if (!string.IsNullOrEmpty(logo))
                    urls.Add(logo);
                return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                s_log.Debug($"[ArtCache] Could not read rom {romId}: {ex.Message}");
                return null;
            }
        }

        private void Save()
        {
            try
            {
                string json;
                lock (_lock)
                    json = JsonConvert.SerializeObject(_byRom);
                File.WriteAllText(_index, json);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[ArtCache] Could not keep the list of kept artwork: {ex.Message}");
            }
        }

        // The addresses StoreImages keeps on disk when it downloads them.
        private void Publish()
        {
            List<string> all;
            lock (_lock)
                all = _byRom.Values.SelectMany(u => u).ToList();
            StoreImages.SetArtUrls(all);
        }
    }
}
