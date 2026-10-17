using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using RomM.Models.RomM.Collection;
using RomM.Models.RomM.Platform;
using RomM.Models.RomM.Rom;
using RomM.Settings;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace RomM.Games
{
    class RomMImportController
    {
        private readonly RomM _plugin;
        public ILogger Logger => LogManager.GetLogger();

        public RomMImportController(RomM plugin)
        {
            _plugin = plugin;
        }

        public List<Game> Import(LibraryImportGamesArgs args)
        {
            // Only block servers we can positively identify as older than 4.9. Dev/non-numeric
            // versions and pre-release suffixes are treated as compatible (see RomMServerVersion).
            if (!RomMServerVersion.SupportsImport(_plugin.Settings.ServerVersion))
            {
                _plugin.Playnite.Notifications.Add(_plugin.Id.ToString(), "RomM Server 4.9 or later required to import ROMs!", NotificationType.Error);
                return new List<Game>();
            }

            IList<RomMPlatform> apiPlatforms = FetchPlatforms();
            List<Game> games = new List<Game>();
            IEnumerable<EmulatorMapping> enabledMappings = SettingsViewModel.Instance.Mappings?.Where(m => m.Enabled);
            string url = BuildROMUrl();

            // No mappings is a fine state: the games in the account's library still come in, each
            // waiting for an emulator (ImportWaiting), which installing it offers to set up.
            if (enabledMappings == null || !enabledMappings.Any())
            {
                Logger.Info("[Import Controller] No emulator mappings: only the account's library is brought in, waiting for emulators.");
                enabledMappings = Enumerable.Empty<EmulatorMapping>();
            }

            IList<RomMCollection> favoritCollections = _plugin.FetchFavorites();
            var favorites = favoritCollections.FirstOrDefault(c => c.IsFavorite)?.RomIds ?? new List<int>();

            // RetroAchievements from RomM, displayed through SuccessStory when it is installed.
            var achievements = RomMAchievementSync.Create(Logger, _plugin.Settings.RomMHost, args.CancelToken, _plugin.AchievementStore);

            // Pull ROM data for each enabled mapping and add the games to playnite
            foreach (var mapping in enabledMappings)
            {
                if (args.CancelToken.IsCancellationRequested)
                    break;

                // A mapping with no emulator/profile is genuinely unconfigured — skip quietly.
                if (!mapping.HasEmulatorProfile)
                {
                    Logger.Warn($"[Import Controller] Emulator {mapping.MappingId} is misconfigured, skipping.");
                    continue;
                }

                // No RomM platform selected. This is the common state right after upgrading from a
                // pre-0.6 version, where the old platform mapping is dropped and no RomM platform id
                // is carried over. Give the user an actionable message instead of a cryptic
                // "-1 not found". The <= 0 check covers both the unset RomMPlatformId (-1) and the
                // empty default RomMPlatform (Id 0).
                if (!mapping.HasRomMPlatform)
                {
                    var name = !string.IsNullOrWhiteSpace(mapping.MappingName)
                        ? mapping.MappingName
                        : (mapping.Emulator?.Name ?? "a mapping");
                    // Stable per-mapping id so repeated imports dedupe instead of stacking.
                    _plugin.Playnite.Notifications.Add(
                        $"{_plugin.Id}-platform-unset-{mapping.MappingId}",
                        $"The RomM platform for \"{name}\" is not set. This can happen after upgrading the plugin. " +
                        "Open RomM settings, select the emulator mapping, choose its RomM platform, then run Update Game Library again.",
                        NotificationType.Error);
                    continue;
                }

                RomMPlatform apiPlatform = apiPlatforms.FirstOrDefault(p => p.Id == mapping.RomMPlatformId);
                if (apiPlatform == null)
                {
                    _plugin.Playnite.Notifications.Add(_plugin.Id.ToString(), $"Platform {mapping.RomMPlatform.PlayniteName} with ID {mapping.RomMPlatformId} not found in RomM, skipping.", NotificationType.Error);
                    continue;
                }

                // Pull data from server
                // Could be made async, but when testing (4.7.0) found a performance degradation
                var romMROMs = DownloadROMData(args, url, apiPlatform);
                if(romMROMs == null)
                {
                    Logger.Warn($"[Import Controller] Failed to get ROMs for {apiPlatform.Name}.");
                    continue;
                }
                Logger.Info($"[Import Controller] Finished parsing response for {apiPlatform.Name}.");

                // Import games for the current mapping.
                // ProcessData mutates the Playnite database (ImportGame/Update/Remove), which is not
                // thread-safe, so each mapping is processed sequentially to avoid races/DependencySource
                // errors. The expensive network fetch above already ran synchronously.
                RomMImport newImport = new RomMImport(_plugin, args, mapping, romMROMs, favorites, achievements);
                games.AddRange(newImport.ProcessData());
            }

            if (!args.CancelToken.IsCancellationRequested)
                ImportWaiting(favoritCollections, args.CancelToken);

            if (achievements.Updated > 0)
                Logger.Info($"[Achievements] Updated {achievements.Updated} games in SuccessStory.");

            return games;
        }

        /// <summary>
        /// The games in the account's library (its "Playnite Library" collection on the server)
        /// that are not in Playnite yet and that no mapping brought in -- above all those on
        /// platforms nothing plays here yet, which come in waiting for an emulator. Games deleted
        /// here since the last sync are left out; the sync takes them off the server's list.
        /// </summary>
        private void ImportWaiting(IList<RomMCollection> collections, System.Threading.CancellationToken cancel)
        {
            try
            {
                int me = -1;
                using (HttpResponseMessage response = HttpClientSingleton.Instance.GetAsync(_plugin.CombineUrl(_plugin.Settings.RomMHost, "api/users/me")).GetAwaiter().GetResult())
                {
                    if (response.IsSuccessStatusCode)
                        me = (int?)JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())["id"] ?? -1;
                }

                var wanted = new HashSet<int>((collections ?? new List<RomMCollection>())
                    .Where(c => !c.IsFavorite && (c.UserId == null || c.UserId == me)
                                && string.Equals(c.Name, CollectionSync.LibraryCollectionName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(c => c.RomIds ?? new List<int>()));
                if (wanted.Count == 0)
                    return;

                var here = new HashSet<int>();
                foreach (Game game in _plugin.Playnite.Database.Games.Where(g => g.PluginId == _plugin.Id))
                {
                    if (RomMGameId.TryParse(game.GameId, out int id, out string _))
                        here.Add(id);
                }
                wanted.ExceptWith(here);
                wanted.ExceptWith(_plugin.CollectionsSync?.RemovedSinceSync() ?? new HashSet<int>());

                var added = new List<int>();
                foreach (int romId in wanted)
                {
                    if (cancel.IsCancellationRequested)
                        break;
                    string problem = ImportSingle(romId, cancel);
                    if (problem == null)
                        added.Add(romId);
                    else
                        Logger.Info($"[Import Controller] Library game {romId} not brought in: {problem}");
                }
                if (added.Count > 0)
                {
                    _plugin.Library.AddMany(added);
                    Logger.Info($"[Import Controller] Brought in {added.Count} game(s) from the account's library that no mapping covered.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Import Controller] Could not bring in the account's library: {ex.Message}");
            }
        }

        /// <summary>
        /// Brings one game into Playnite: the Store's "Add to Library". The same import as a
        /// library update, given a single ROM. Returns null on success, otherwise a sentence
        /// saying why it could not be added.
        /// </summary>
        public string ImportSingle(int romId, System.Threading.CancellationToken cancel)
        {
            if (!RomMServerVersion.SupportsImport(_plugin.Settings.ServerVersion))
                return "RomM server 4.9 or later is required.";

            RomMRom rom;
            try
            {
                string url = _plugin.CombineUrl(_plugin.Settings.RomMHost, $"api/roms/{romId}");
                using (HttpResponseMessage response = HttpClientSingleton.Instance.GetAsync(url).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return $"The server did not return the game ({(int)response.StatusCode}).";
                    rom = JsonConvert.DeserializeObject<RomMRom>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                }
            }
            catch (Exception e)
            {
                Logger.Error($"[Import Controller] Could not fetch ROM {romId}: {e.Message}");
                return "Could not reach the RomM server.";
            }

            if (rom == null)
                return "The server returned nothing for this game.";

            EmulatorMapping mapping = SettingsViewModel.Instance.Mappings?
                .FirstOrDefault(m => m.Enabled && m.IsImportable && m.RomMPlatformId == rom.PlatformId);
            if (mapping == null)
            {
                // Nothing plays the platform yet: the game is still added, waiting for an emulator,
                // which installing it offers to set up (RomM.SetUpPlatformFor). The mapping it is
                // filed with is never saved; its id (empty) matches no real one.
                RomMPlatform platform = _plugin.Settings.RomMPlatforms?.FirstOrDefault(p => p.Id == rom.PlatformId)
                    ?? FetchPlatforms().FirstOrDefault(p => p.Id == rom.PlatformId);
                if (platform == null || _plugin.EmulatorSetup == null)
                    return "Its platform could not be read from the RomM server.";
                mapping = _plugin.EmulatorSetup.NewMapping(platform);
                mapping.MappingId = Guid.Empty;
                mapping.Enabled = false;
                Logger.Info($"[Import Controller] {rom.Name} is added without an emulator: nothing plays {platform.PlayniteName} yet.");
            }

            var favorites = _plugin.FetchFavorites().FirstOrDefault(c => c.IsFavorite)?.RomIds ?? new List<int>();
            var achievements = RomMAchievementSync.Create(Logger, _plugin.Settings.RomMHost, cancel, _plugin.AchievementStore);
            // Playnite alone can set the cancel token on these; left unset it is one that never
            // cancels, which suits a single game.
            var args = new LibraryImportGamesArgs();

            new RomMImport(_plugin, args, mapping, new List<RomMRom> { rom }, favorites, achievements, partial: true).ProcessData();

            bool present = _plugin.Playnite.Database.Games.Any(g =>
                g.PluginId == _plugin.Id && RomMGameId.TryParse(g.GameId, out int id, out string _) && id == romId);
            return present
                ? null
                : "It was not added. It may be an alternate version merged under another entry, or on the import exclusion list.";
        }

        private static async Task<HttpResponseMessage> GetAsyncWithParams(string baseUrl, NameValueCollection queryParams)
        {
            var uriBuilder = new UriBuilder(baseUrl);
            var query = HttpUtility.ParseQueryString(uriBuilder.Query);

            foreach (string key in queryParams)
            {
                query[key] = queryParams[key];
            }

            uriBuilder.Query = query.ToString();

            return await HttpClientSingleton.Instance.GetAsync(uriBuilder.Uri);
        }

        private string BuildROMUrl()
        {
            string baseUrl = _plugin.CombineUrl(_plugin.Settings.RomMHost, "api/roms");
            return RomMRomQuery.Build(baseUrl, _plugin.Settings.SkipMissingFiles, _plugin.Settings.ExcludeGenres);
        }
        private IList<RomMPlatform> FetchPlatforms()
        {
            string apiPlatformsUrl = _plugin.CombineUrl(_plugin.Settings.RomMHost, "api/platforms");
            try
            {
                HttpResponseMessage response = HttpClientSingleton.Instance.GetAsync(apiPlatformsUrl).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();

                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonConvert.DeserializeObject<List<RomMPlatform>>(body);
            }
            catch (HttpRequestException e)
            {
                Logger.Error($"[Import Controller] Request exception: {e.Message}");
                return new List<RomMPlatform>();
            }
        }
        
        private List<RomMRom> DownloadROMData(LibraryImportGamesArgs args, string url, RomMPlatform platform)
        {
            Logger.Info($"[Import Controller] Starting to fetch games for {platform.Name}.");

            const int pageSize = 50;
            int offset = 0;
            bool hasMoreData = true;
            var romData = new List<RomMRom>();

            // Download data from RomM server
            while (hasMoreData)
            {
                if (args.CancelToken.IsCancellationRequested)
                    break;

                NameValueCollection queryParams = new NameValueCollection
                    {
                        { "platform_ids", platform.Id.ToString() },
                        { "genres_logic", "none" },
                        { "order_by", "name" },
                        { "order_dir", "asc" },
                        { "with_files", "true" },
                        { "with_siblings", "true" },
                        { "limit", pageSize.ToString() },
                        { "offset", offset.ToString() },
                    };

                try
                {
                    HttpResponseMessage response = GetAsyncWithParams(url, queryParams).GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();

                    Logger.Info($"[Import Controller] Parsing response for {platform.Name} batch {offset / pageSize + 1}.");

                    Stream body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    List<RomMRom> roms;
                    using (StreamReader reader = new StreamReader(body))
                    {
                        var jsonResponse = JObject.Parse(reader.ReadToEnd());
                        roms = jsonResponse["items"].ToObject<List<RomMRom>>();
                    }

                    Logger.Info($"[Import Controller] Parsed {roms.Count} roms for batch {offset / pageSize + 1}.");
                    romData.AddRange(roms);

                    if (roms.Count < pageSize)
                    {
                        Logger.Info($"[Import Controller] Received less than {pageSize} roms for {platform.Name}, assuming no more games.");
                        hasMoreData = false;
                        break;
                    }

                    offset += pageSize;
                }
                catch (HttpRequestException e)
                {
                    Logger.Error($"[Import Controller] Request exception: {e.Message}");
                    hasMoreData = false;
                }
            }

            return romData;
        }
    }

}
