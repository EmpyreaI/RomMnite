using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using RomM.Games;
using RomM.Downloads;
using RomM.Saves;
using RomM.VersionSelector;
using RomM.Models.RomM.Collection;
using RomM.Models.RomM.Platform;
using RomM.Models.RomM.Rom;
using RomM.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace RomM
{
    public static class HttpClientSingleton
    {
        private static readonly HttpClient httpClient = new HttpClient();

        // For the server's JSON only (the Store's pages): asks for it compressed, which a list of
        // a few hundred games shrinks to a fraction. Not for downloads, whose byte ranges and
        // progress must be of the file itself. The same sign-in as the other.
        private static readonly HttpClient jsonClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        });

        static HttpClientSingleton()
        {
            // .NET Framework lets a desktop app hold only 2 connections to one server at a time.
            // A download holds one for as long as it runs (two downloads, both), so everything
            // else the plugin asks the RomM server for -- the Store's home page makes dozens of
            // requests -- queued behind them and the page never loaded. Set before the first
            // request, so the server's connection pool is made with it.
            if (System.Net.ServicePointManager.DefaultConnectionLimit < 64)
                System.Net.ServicePointManager.DefaultConnectionLimit = 64;
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            jsonClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public static void ConfigureBasicAuth(string username, string password)
        {
            var base64Credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Credentials);
            jsonClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Credentials);
        }
        public static void ConfigureAPIAuth(string apiToken)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            jsonClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        }

        public static void ClearAuth()
        {
            httpClient.DefaultRequestHeaders.Authorization = null;
            jsonClient.DefaultRequestHeaders.Authorization = null;
        }

        public static HttpClient Instance => httpClient;

        /// <summary>For reading the server's JSON, compressed on the way (not for downloads).</summary>
        public static HttpClient Json => jsonClient;
    }

    public static class JsonSerializerSingleton
    {
        public static JsonSerializer Instance { get; } = new JsonSerializer();
    }

    public class RomM : LibraryPlugin, IRomM
    {
        // Games' source (what they came from: the RomM server), and the name themes and other
        // extensions know the plugin's settings and elements by: these stay "RomM".
        private const string s_pluginName = "RomM";

        /// <summary>The extension's own name, as Playnite shows it (it is no longer just RomM's library importer).</summary>
        internal const string DisplayName = "RomMnite";

        internal static readonly string Icon = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"icon.png");
        internal static readonly Guid PluginId = Guid.Parse("9700aa21-447d-41b4-a989-acd38f407d9f");
        internal static readonly MetadataNameProperty SourceName = new MetadataNameProperty(s_pluginName);

        public override Guid Id { get; } = PluginId;
        public override string Name { get; } = DisplayName;
        public override string LibraryIcon { get; } = Icon;

        public ILogger Logger => LogManager.GetLogger();
        public IPlayniteAPI Playnite { get; private set; }
        public SettingsViewModel Settings { get; private set; }

        /// <summary>Where back and forward lead; see the arrows by the STORE tab.</summary>
        public Account.NavigationHistory History { get; } = new Account.NavigationHistory();

        // Notes the library's current place: Home or Downloads, else the selected game.
        private void RecordLibraryPlace()
        {
            if (CloudStatus.LibraryHomeVisible || _selectedGameId == Guid.Empty)
                History.Record(new Account.NavEntry { Store = false, Kind = CloudStatus.LibraryHomeVisible ? CloudStatus.LibraryPage : "Home" });
            else
                History.Record(new Account.NavEntry { Store = false, Kind = "game", GameId = _selectedGameId });
        }     
        public string ROMDataPath { get; private set; }
        public MetadataProperty Source { get; private set; }

        public DownloadQueueController DownloadQueueController { get; private set; }
        internal RomMDownloadsSidebarItem DownloadsSidebar { get; private set; }
        private readonly DownloadQueueViewModel downloadsVm;

        internal SaveSyncService SaveSync { get; private set; }

        /// <summary>Whose save is on disk for each game, and each account's own kept apart.</summary>
        internal SaveAccounts SaveAccounts { get; private set; }
        internal SaveSyncOptOut SaveSyncOff { get; private set; }
        SaveSyncOptOut IRomM.SaveSyncOff => SaveSyncOff;

        /// <summary>Each account's own user in RPCS3 and Xenia (see Account.EmulatorUsers).</summary>
        internal Account.EmulatorUsers EmulatorUsers { get; private set; }
        internal Setup.EmulatorSetup EmulatorSetup { get; private set; }

        /// <summary>RPCS3's trophies and Xenia's achievements, per account (see EmulatorAchievements).</summary>
        public EmulatorAchievements EmulatorAchievements { get; private set; }

        /// <summary>The signed-in account (see Account.AccountLibraries.KeyOf); null when nobody is.</summary>
        public string CurrentAccountKey { get; private set; }

        // Game ids whose next ItemUpdated was caused by the importer itself, so OnItemUpdated must
        // not echo the change back to the RomM server.
        private readonly ConcurrentDictionary<Guid, byte> ignoredGameIds = new ConcurrentDictionary<Guid, byte>();
        public void SuppressSync(Guid gameId) => ignoredGameIds[gameId] = 0;

        // Implementing Client adds ability to open it via special menu in playnite
        public override LibraryClient Client { get; } = new RomMClient();

        // Before anything that needs them is loaded: the extension's own copies of libraries
        // Playnite also ships (see PluginDependencies).
        static RomM()
        {
            PluginDependencies.Register();
            // As early as the plugin loads, before anything connects to the server (see HttpClientSingleton).
            if (System.Net.ServicePointManager.DefaultConnectionLimit < 64)
                System.Net.ServicePointManager.DefaultConnectionLimit = 64;
        }

        public RomM(IPlayniteAPI api) : base(api)
        {
            Playnite = api;
            Properties = new LibraryPluginProperties
            {
                HasSettings = true,
                HasCustomizedGameImport = true,
            };
            ROMDataPath = $"{Playnite.Paths.ExtensionsDataPath}\\{Id}\\Games\\";

            Library = new UserLibrary(
                Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Library"),
                () => Settings?.RomMUser);

            AchievementStore = new AchievementViews.AchievementStore(
                Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Achievements"));
            // Back and forward (the arrows by the STORE tab): showing a library place here; the
            // Store shows its own.
            History.Navigate += entry =>
            {
                try
                {
                    if (entry.Store)
                    {
                        CloudStatus.StoreVisible = true;
                        return;
                    }
                    CloudStatus.StoreVisible = false;
                    try { Playnite.MainView.SwitchToLibraryView(); } catch { }
                    if (entry.Kind == "game")
                    {
                        CloudStatus.LibraryHomeVisible = false;
                        Playnite.MainView.SelectGame(entry.GameId);
                    }
                    else
                    {
                        ShowLibraryPage(entry.Kind);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[History] Could not go back to {entry.Kind}: {ex.Message}");
                }
            };

            // Leaving the Store for the library is a step of its own.
            CloudStatus.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(CloudStatusModel.StoreVisible) && !CloudStatus.StoreVisible)
                    RecordLibraryPlace();
            };

            AchievementStore.Changed += id =>
            {
                if (id == _selectedGameId)
                    ShowAchievementsSummary(id);
            };
            // The selected game's achievement sync, beside its cloud saves' status.
            AchievementSync.Changed += id =>
            {
                if (id == _selectedGameId)
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (id == _selectedGameId)
                            ShowAchievementSync(id);
                    }));
            };
            // Another account's achievements: the selected game's count and bar follow at once
            // (not only at the next change), and its achievements are brought up to date.
            AchievementStore.Reset += () =>
            {
                Guid selected = _selectedGameId;
                ShowAchievementsSummary(selected);
                Game game = selected != Guid.Empty ? Playnite.Database.Games.Get(selected) : null;
                // Afterwards: the switch happens mid sign-in, before the new account's
                // credentials are in place, and the server must be asked as the new account.
                if (game != null)
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() =>
                        {
                            RefreshAchievements(game, force: true);
                            EmulatorAchievements.Refresh(game);
                        }));
            };

            Steam = new SteamDescriptions(
                Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Steam"),
                () => Settings == null || Settings.UseSteamDescriptions);

            News = new SteamNews(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "SteamNews"));
            Locations = new InstallLocations(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "InstallLocations.json"));
            Wikipedia = new WikipediaSummary(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Wikipedia"));
            XeniaCompat = new XeniaCompat(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Xenia"));
            CompatLists = new CompatLists(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Compatibility"));
            CompatWatch = new CompatWatch(this, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Compatibility", "Store"));
            ControllerSupport = new PcControllerSupport(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "ControllerSupport"));
            Wishlist = new Store.Wishlist(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Wishlist.json"));
            HltbData = new HltbStore(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Hltb.json"));
            Accounts = new Account.AccountCache(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Accounts.json"));
            Libraries = new Account.AccountLibraries(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "AccountLibraries.json"));
            // One game on several platforms: one copy shown, the others behind its button's drop-down.
            Versions = new LibraryVersions(this, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()));
            Account.AccountLibraries.HiddenElsewhere = id => Versions.HidThis(id);
            Collections = new GameCollections(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Collections.json"));
            Shelves = new LibraryHome.ShelfLayout(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "LibraryShelves.json"));
            // A new collection gets a shelf on the library's Home page; a deleted one loses its shelves.
            Collections.Created += made => Shelves.AddCollectionShelf(made.Id);
            Collections.Deleted += gone => Shelves.RemoveCollectionShelves(gone);
            CollectionsSync = new CollectionSync(this);
            UserData = new UserDataSync(this);
            // Changes made here go to the server shortly after; the sync's own changes do not.
            Collections.Deleted += gone =>
            {
                if (!CollectionsSync.Applying)
                    CollectionsSync.NoteDeleted(gone);
            };
            Collections.Changed += () =>
            {
                if (!CollectionsSync.Applying)
                    ScheduleSync(TimeSpan.FromSeconds(8));
            };
            _steamIdsPath = Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "SteamIds.json");

            Sessions = new PlaySessions(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "PlaySessions.json"));
            OpenGames = new OpenSessions(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "OpenSessions.json"));

            Videos = new RomMVideoStore(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Videos.json"));

            // Save sync (RetroArch <-> RomM). Reads Settings lazily, so constructing here is safe.
            SaveSync = new SaveSyncService(this);
            SaveSyncOff = new SaveSyncOptOut(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()));
            SaveAccounts = new SaveAccounts(this, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()));
            EmulatorAchievements = new EmulatorAchievements(this, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()));
            ArtCache = new LibraryArtCache(this, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()));
            // The community's controller list: handed to the SDL emulators Playnite starts, and read
            // by the library's Controller section; fetched in the background when missing or old.
            ControllerDatabase.Start(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()));
            // Ludusavi (Windows games' saves): fetched into Tools\ when missing, then those games synced.
            Saves.Handlers.LudusaviSaveHandler.ToolsFolder = Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Tools");
            // SDL3, for giving Cemu, Eden, Dolphin and RPCS3 their controller as they will see it
            // (Setup.ControllerProbe): fetched in the background once one of them is set up.
            Setup.ControllerProbe.SetToolsFolder(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Tools"));
            Saves.Handlers.LudusaviInstaller.Installed += () => SaveSync?.SyncLudusaviGames();
            // Also foreground: a game running or starting (checked at most once a second), and a
            // download in progress.
            DateTime gamesCheckedAt = DateTime.MinValue;
            bool gameRunning = false;
            Foreground.Also(() =>
            {
                if (DateTime.UtcNow - gamesCheckedAt > TimeSpan.FromSeconds(1))
                {
                    gameRunning = Playnite.Database.Games.Any(g => g.IsRunning || g.IsLaunching);
                    gamesCheckedAt = DateTime.UtcNow;
                }
                return gameRunning;
            });
            Foreground.Also(() => DownloadQueueController?.ViewModel?.Items?.ToArray()
                .Any(i => i.Status == DownloadStatus.Downloading || i.Status == DownloadStatus.Extracting) == true);
            // Unlocked while playing: the pop-up over the game, as Steam's.
            EmulatorAchievements.UnlockedLive += (game, item) =>
            {
                Logger.Info($"[Achievements] {game.Name}: unlocked \"{item.Title}\".");
                bool ps3 = game.Platforms?.Any(p => p?.SpecificationId == "sony_playstation3") == true;
                if (ps3)
                {
                    // Trophies have grades, not points (the points kept are the grade's weight).
                    string grade = item.Points >= 180 ? "Platinum" : item.Points >= 90 ? "Gold" : item.Points >= 30 ? "Silver" : "Bronze";
                    Overlay.Toast.Achievement($"{item.Title}  \u00B7  {grade} trophy", item.Description, item.BadgeUrl, 0, null);
                }
                else
                    Overlay.Toast.Achievement(item.Title, item.Description, item.BadgeUrl, item.Points, "G");
            };
            EmulatorUsers = new Account.EmulatorUsers(this, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()));
            EmulatorSetup = new Setup.EmulatorSetup(this);
            // The save on disk is synced only as its owner's (see SaveAccounts).
            SaveSync.MayUseLocalSave = g => SaveAccounts.IsMine(g, CurrentAccountKey);

            // Lets a theme show the selected game's cloud save status with
            // {PluginSettings Plugin=RomM, Path=Text}; see RefreshCloudStatus.
            AddSettingsSupport(new AddSettingsSupportArgs { SourceName = "RomM", SettingsRoot = nameof(CloudStatus) });
            SaveSync.StatusChanged += gameId =>
            {
                if (gameId == _selectedGameId)
                    RefreshCloudStatus();
            };
            // RomM back after being out of reach: the offline notice goes, and what waited
            // (collections, play sessions, ratings) is synced.
            SaveSync.Reconnected += () =>
            {
                try
                {
                    Playnite.Notifications.Remove("romm-offline");
                }
                catch
                {
                    // Not shown.
                }
                ScheduleSync(TimeSpan.FromSeconds(5));
            };

            // Initialise the download queue
            downloadsVm = new DownloadQueueViewModel();

            // Limit to 10 concurrent downloads for the moment
            DownloadQueueController = new DownloadQueueController(Playnite, downloadsVm, maxConcurrent: 10)
            {
                Recent = new RecentDownloads(Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "RecentDownloads.json")),
            };
            // Steam's pop-up when a download finishes: its cover, and a click opens its page.
            DownloadQueueController.Finished += gameId =>
            {
                try
                {
                    Game done = Playnite.Database.Games.Get(gameId);
                    if (done == null)
                        return;
                    string cover = null;
                    if (!string.IsNullOrEmpty(done.CoverImage))
                        cover = done.CoverImage.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? done.CoverImage : Playnite.Database.GetFullFilePath(done.CoverImage);
                    Overlay.Toast.Show("Download complete", (done.Name ?? "The game") + " is ready to play.", cover, "\uE896",
                        () => ShowGameInLibrary(gameId));
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[Downloads] Could not show the finished download: {ex.Message}");
                }
            };

            // Download progress for themes: the selected game's (bound through CloudStatus.Download)
            // and, per library row, the RomM_DownloadStatus element.
            _downloadStatus = new DownloadStatusPresenter(DownloadQueueController, CloudStatus.Download);
            CloudStatus.Download.OpenDownloadsCommand = new RelayCommand(() => ShowLibraryPage("Downloads"));
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "RomM",
                ElementList = new List<string> { "DownloadStatus", "ManualViewer", "GameLogo", "GameVideo", "AccountBadge", "Store", "NavTabs", "MainMenu", "Search", "LibraryHome", "HomeButton", "StorePageButton", "BottomBar", "AchievementsButton", "AchievementsProgressBar", "AchievementsCompactList", "AchievementsCompactUnlocked", "AchievementsCompactLocked", "AchievementsUserStats", "AchievementsList", "AchievementsChart", "HltbProgressBar", "StorePageLink", "LibraryFilters", "PlayButton", "NewsFeed", "ControllerStatus", "ControllerStatusBar", "EmulationStatus" },
            });

            // Initialise the sidebar only in desktop mode
            if (API.Instance.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                DownloadsSidebar = new RomMDownloadsSidebarItem(this);
            }
        }


    #region Helper functions
        public string CombineUrl(string baseUrl, string relativePath) => RomMUrl.Combine(baseUrl, relativePath);

        /// <summary>The ROM sidecar for a game, or null when it is missing or unreadable.</summary>
        internal RomMRomLocal LoadGameData(Game game) =>
            RomMGameData.Load(ROMDataPath, game?.GameId, Logger, game?.Name);

        public EmulatorMapping MappingFor(Game game) =>
            Settings?.MappingById(LoadGameData(game)?.MappingID ?? Guid.Empty);

        public RomMRom FetchRom(string romId)
        {
            string romUrl = CombineUrl(Settings.RomMHost, $"api/roms/{romId}");
            try
            {
                HttpResponseMessage response = HttpClientSingleton.Instance.GetAsync(romUrl).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();

                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonConvert.DeserializeObject<RomMRom>(body);
            }
            catch (HttpRequestException e)
            {
                Logger.Error($"Request exception: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Media the ROM lacks lent from another platform's version of the same game (Games.SameGame),
        /// found by searching the server for its name and the game's name without the edition.
        /// For one game added or refreshed alone; a library update lends within what it downloads.
        /// </summary>
        public void LendFromOtherVersions(RomMRom rom)
        {
            if (rom == null || !SameGame.LacksMedia(rom) || string.IsNullOrWhiteSpace(rom.Name))
                return;
            try
            {
                var found = new List<RomMRom> { rom };
                var terms = new List<string> { SameGame.EditionBase(rom.Name) };
                if (!string.Equals(terms[0], rom.Name, StringComparison.OrdinalIgnoreCase))
                    terms.Add(rom.Name);
                foreach (string term in terms)
                {
                    string url = CombineUrl(Settings.RomMHost, $"api/roms?limit=60&search_term={Uri.EscapeDataString(term)}");
                    HttpResponseMessage response = HttpClientSingleton.Instance.GetAsync(url).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                        continue;
                    var page = Newtonsoft.Json.Linq.JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    foreach (var item in page["items"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                    {
                        RomMRom other = item.ToObject<RomMRom>();
                        if (other != null && found.All(r => r.Id != other.Id))
                            found.Add(other);
                    }
                }
                List<RomMRom> group = SameGame.Group(found.Select(r => SameGame.Of(r)).ToList())
                    .First(g => g.Contains(0)).Select(i => found[i]).Where(r => r.PlatformId != rom.PlatformId).ToList();
                RomMRom donor = SameGame.DonorFor(rom, group);
                if (donor != null && SameGame.Lend(rom, donor))
                    Logger.Info($"[Versions] {rom.Name}: media it lacks taken from its {donor.PlatformDisplayName ?? donor.PlatformName} version.");
            }
            catch (Exception ex)
            {
                Logger.Info($"[Versions] Could not look for other versions of {rom.Name}: {ex.Message}");
            }
        }

        // Playnite url is in the format playnite://romm/<action>/<platform_igdb_id>/<rom_id>
        internal void HandleRommUri(PlayniteUriEventArgs args)
        {
            var action = args.Arguments[0];
            var platformIgdbId = args.Arguments[1];
            var romId = args.Arguments[2];

            Logger.Debug($"Received Playnite URI: {action}/{platformIgdbId}/{romId}");

            RomMRom rom = FetchRom(romId);

            if (rom == null)
            {
                Logger.Warn($"Game {romId} not found in RomM.");
                return;
            }

            foreach (var mapping in SettingsViewModel.Instance.Mappings?.Where(m => m.Enabled))
            {
                if (mapping.RomMPlatform.IgdbId.ToString() == platformIgdbId)
                {
                    var gameName = rom.Name;

                    var game = Playnite.Database.Games.FirstOrDefault(g => g.Source.Name == SourceName.ToString() &&
                                                                           g.Platforms.Any(p => p.Name == mapping.RomMPlatform.PlayniteName) &&
                                                                           g.Name == gameName);

                    if (game == null)
                    {
                        Logger.Warn($"Game {gameName} not found in Playnite database.");
                        return;
                    }

                    PlayniteApi.MainView.SwitchToLibraryView();
                    PlayniteApi.MainView.SelectGame(game.Id);

                    switch (action)
                    {
                        case "view":
                            // We always open the game in the webview
                            return;
                        case "play":
                            PlayniteApi.StartGame(game.Id);
                            break;
                    }
                }
            }
        }

        // New-style overload (used by DownloadQueueController)
        public static Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption, CancellationToken ct)
        {
            return HttpClientSingleton.Instance.GetAsync(url, completionOption, ct);
        }
    #endregion

    #region Playnite functions
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);
            Versions?.Start();
            // Windows signing out or shutting down: no question then (FinishBeforeQuit).
            try
            {
                System.Windows.Application.Current.SessionEnding += (s, e) => _windowsEnding = true;
            }
            catch
            {
            }

            // The Store loads out of sight, but only once the library is there: Playnite's own
            // start-up done and no library update running (they share the server, and the update
            // covers the window until it is done). Then the Store's Home, and the Store's other
            // pages last (StoreView.Preload, PrefetchDirectories).
            try
            {
                DateTime started = DateTime.UtcNow;
                var preload = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    Playnite.MainView.UIDispatcher) { Interval = TimeSpan.FromSeconds(1) };
                preload.Tick += (s, e) =>
                {
                    DateTime now = DateTime.UtcNow;
                    bool settled = now - started >= TimeSpan.FromSeconds(6)
                        && Volatile.Read(ref _importing) == 0
                        && now - _lastImportEndUtc >= TimeSpan.FromSeconds(3);
                    if (!settled)
                        return;
                    preload.Stop();
                    Logger.Info($"[Store] The library is ready ({(now - started).TotalSeconds:0} s after start-up); the store loads in the background.");
                    // Cloud saves first: every installed game's synced (they are foreground, so
                    // the Store's loading waits for them), then watched for while offline.
                    // Sessions the last run left open (Playnite quit or crashed mid-game) first,
                    // so their saves are in the sync that follows.
                    RecoverSessions();
                    SaveSync.StartBackground();
                    CloudStatus.RequestStorePreload();
                    // The library games' artwork not on disk yet, in the background lane behind it.
                    ArtCache?.Start();
                    // SDL3 for the emulators that need it for their controller, once the library is up.
                    try
                    {
                        if (Playnite.Database.Emulators.Any(emulator => new[] { "cemu", "eden", "dolphin", "rpcs3" }
                                .Contains((emulator.BuiltInConfigId ?? "").ToLowerInvariant())))
                            Setup.ControllerProbe.EnsureInBackground();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[Controllers] Could not look through the emulators: {ex.Message}");
                    }
                };
                preload.Start();
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Store] No preload: {ex.Message}");
            }

            if (!Directory.Exists($"{ROMDataPath}"))
                Directory.CreateDirectory($"{ROMDataPath}");

            // Games imported while series came from IGDB's franchises get their real series.
            Task.Run(() => FixSeriesOnce());

            Settings = new SettingsViewModel(this, this);
            CloudStatus.VideoOn = Settings.AutoPlayVideos;

            // Emulators start fullscreen and close with their game (Setup.EmulatorLaunch).
            Setup.EmulatorLaunch.ApplyAll(Playnite);
            EnsureWindowsPlatform();
            AdoptReadyMappings();

            // RomMnite's page as a category of Playnite's own Settings window.
            if (Playnite.ApplicationInfo.Mode == ApplicationMode.Desktop)
                global::RomM.Settings.PlayniteSettingsPage.Install(this);

            // Sunshine: installed games listed for streaming, kept so as they change (when on).
            Sunshine = new Integrations.SunshineSync(Playnite, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()),
                () => Settings?.SunshineSettings());
            Sunshine.SyncInBackground();
            TrimLaunchFiles();

            // The theme shipped with the extension, kept in Playnite's Themes folder (selected the first time).
            Setup.ThemeInstaller.Sync(Playnite, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString()));
            CloudStatus.Achievements.EnableIntegrationCompactUnlocked = Settings.ShowUnlockedAchievements;
            CloudStatus.Achievements.EnableIntegrationCompactLocked = Settings.ShowLockedAchievements;

            if (Settings.UseBasicAuth && !string.IsNullOrEmpty(Settings.RomMUsername) && !string.IsNullOrEmpty(Settings.RomMPassword))
            {
                HttpClientSingleton.ConfigureBasicAuth(Settings.RomMUsername, Settings.RomMPassword);
            }
            else if(SettingsViewModel.ApiTokenPattern.IsMatch(Settings.RomMClientToken))
            {
                HttpClientSingleton.ConfigureAPIAuth(Settings.RomMClientToken);
            }
            
            // Nothing to connect with yet: ask the user to sign in, once Playnite has finished
            // starting so the prompt does not hold up other extensions.
            foreach (var badge in _accountBadges)
                badge.Attach(Settings);

            // The wishlist: asked about shortly after starting, then every half hour.
            StartWishlistChecks();

            // Middle-click scrolling everywhere in the window.
            if (API.Instance.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    Store.MiddleClickScroll.Install(System.Windows.Application.Current.MainWindow);
                    // Which input is in use (controller or keyboard), for the pop-ups' prompts.
                    Overlay.InputActivity.Start();
                    // Smooth mouse-wheel scrolling for every scrolling view, Playnite's own too.
                    Store.SmoothScroll.InstallEverywhere();
                    LibraryHome.LibraryHoverCard.Install(this, System.Windows.Application.Current.MainWindow);
                }));
            }

            // The library opens on Home, as Steam does, once Playnite has settled.
            if (API.Instance.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(ShowLibraryHome));
            }
            // Signing in is required, as in Steam: a remembered account signs in by itself (even
            // with the server out of reach); otherwise the sign-in window, until someone does.
            // Until then the window shows only the RomM logo, not the last account's library.
            if (API.Instance.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(new Action(() =>
                {
                    Account.SignedOutCurtain.Show();

                    // Playnite's own Remove (menu item and Delete key) gives way to ours for RomM games.
                    System.Windows.EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                        new System.Windows.RoutedEventHandler(OnAnyContextMenuOpened));
                    if (System.Windows.Application.Current.MainWindow != null)
                    {
                        System.Windows.Application.Current.MainWindow.PreviewKeyDown += OnMainWindowPreviewKeyDown;
                        System.Windows.Application.Current.MainWindow.PreviewMouseUp += OnMainWindowPreviewMouseUp;
                    }
                }));
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(StartSignIn));
            }

            Playnite.UriHandler.RegisterSource("romm", HandleRommUri);
            Source = SourceName;

            // Portable path fix: expand "{PlayniteDir}" to absolute paths in DB on startup
            if (Playnite.Paths.IsPortable)
            {
                using (PlayniteApi.Database.BufferedUpdate())
                {
                    var games = PlayniteApi.Database.Games.Where(g =>
                        g.PluginId == Id &&
                        g.InstallDirectory != null &&
                        g.InstallDirectory.Contains(ExpandableVariables.PlayniteDirectory));

                    foreach (var game in games)
                    {
                        game.InstallDirectory = PlayniteApi.ExpandGameVariables(game, game.InstallDirectory);

                        if (game.Roms != null && game.Roms.Count > 0)
                        {
                            var roms = game.Roms.Where(r => r.Path.Contains(ExpandableVariables.PlayniteDirectory));
                            foreach (var rom in roms)
                            {
                                rom.Path = PlayniteApi.ExpandGameVariables(game, rom.Path);
                            }
                        }

                        PlayniteApi.Database.Games.Update(game);
                    }
                }
            }

            Playnite.Database.Games.ItemUpdated += OnItemUpdated;

            PlayniteApi.Database.Games.ItemCollectionChanged += (_, argus) =>
            {
                // Their artwork kept on this computer, or let go (Games/LibraryArtCache).
                try
                {
                    ArtCache?.Added(argus.AddedItems);
                    ArtCache?.Removed(argus.RemovedItems);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[ArtCache] {ex.Message}");
                }

                // RomM games added or removed: the account's library on the server follows.
                if (argus.AddedItems.Any(g => g.PluginId == PluginId) || argus.RemovedItems.Any(g => g.PluginId == PluginId))
                    ScheduleSync(TimeSpan.FromSeconds(8));

                // Remove json file if game is removed from playnite
                if (argus.RemovedItems.Count > 0)
                {
                    foreach (var item in argus.RemovedItems)
                    {
                        if (item.PluginId == PluginId)
                        {
                            // Removed from Playnite by whatever means: no longer in the user's
                            // library, so a later update does not bring it back.
                            if (RomMGameId.TryParse(item.GameId, out int removedRomId, out var _))
                            {
                                Library.Remove(removedRomId);
                                CollectionsSync.NoteGameRemoved(removedRomId);
                            }

                            if (RomMGameId.TryParse(item.GameId, out int _, out var sha1))
                            {
                                var romDataFile = RomMGameData.PathFor(ROMDataPath, sha1);
                                if (File.Exists(romDataFile))
                                {
                                    File.Delete(romDataFile);
                                }
                            }
                            else
                            {
                                Logger.Error($"Game {item.Name} id is malformed!");
                            }
                        }
                    }
                }
            };

        }
        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            base.OnApplicationStopped(args);
            // A game left paused under the overlay is let go.
            _overlay?.Stop();
            // A game still running is closed first (Playnite is quitting and cannot be stopped
            // from here, but it waits for this).
            FinishBeforeQuit();

            Playnite.Database.Games.ItemUpdated -= OnItemUpdated;

            // Portable path fix: restore "{PlayniteDir}" tokens before exiting
            if (Playnite.Paths.IsPortable)
            {
                using (PlayniteApi.Database.BufferedUpdate())
                {
                    var games = PlayniteApi.Database.Games.Where(g =>
                        g.PluginId == Id &&
                        g.InstallDirectory != null &&
                        g.InstallDirectory.StartsWith(PlayniteApi.Paths.ApplicationPath));

                    foreach (var game in games)
                    {
                        game.InstallDirectory = game.InstallDirectory.Replace(
                            PlayniteApi.Paths.ApplicationPath,
                            ExpandableVariables.PlayniteDirectory);

                        if (game.Roms != null && game.Roms.Count > 0)
                        {
                            foreach (var rom in game.Roms)
                            {
                                rom.Path = rom.Path.Replace(
                                    PlayniteApi.Paths.ApplicationPath,
                                    ExpandableVariables.PlayniteDirectory);
                            }
                        }

                        PlayniteApi.Database.Games.Update(game);
                    }
                }
            }
        }

        // A library update running (the Store's background loading waits for it), and when the
        // last one ended.
        private int _importing;
        private DateTime _lastImportEndUtc = DateTime.MinValue;

        public override IEnumerable<Game> ImportGames(LibraryImportGamesArgs args)
        {
            Interlocked.Increment(ref _importing);
            Foreground.Begin();
            try
            {
                return ImportGamesNow(args);
            }
            finally
            {
                _lastImportEndUtc = DateTime.UtcNow;
                Interlocked.Decrement(ref _importing);
                Foreground.End();
            }
        }

        private IEnumerable<Game> ImportGamesNow(LibraryImportGamesArgs args)
        {
            if (Playnite.ApplicationInfo.Mode == ApplicationMode.Fullscreen && !Settings.ScanGamesInFullScreen)
            {
                return new List<Game>();
            }

            // Import only needs connectivity + server version, not the profile/avatar.
            if(!Settings.TestConnection(false, false))
            {
                return new List<Game>();
            }

            // The window covered while the library is updated, the logo turning.
            Account.SignedOutCurtain.BeginBusy("Updating your library...");
            try
            {
                return new RomMImportController(this).Import(args);
            }
            finally
            {
                Account.SignedOutCurtain.EndBusy();
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return Settings;
        }
        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SettingsView();
        }

        #region Achievements

        /// <summary>Every game's achievements, as the plugin's own views show them.</summary>
        public AchievementViews.AchievementStore AchievementStore { get; }

        /// <summary>The library games' artwork kept on this computer.</summary>
        internal LibraryArtCache ArtCache { get; private set; }

        /// <summary>How each game's achievements last synced with RomM (per account).</summary>
        internal AchievementSyncStatus AchievementSync { get; } = new AchievementSyncStatus();

        /// <summary>Recent play sessions, for "Last two weeks" on the library's hover cards.</summary>
        public PlaySessions Sessions { get; }

        /// <summary>The games playing now, kept on disk so a session Playnite drops is finished later.</summary>
        internal OpenSessions OpenGames { get; }

        // The closing save sync of the last game stopped (waited for when quitting).
        private Task _endingSync;

        /// <summary>Steam's descriptions, for games RomM has matched on Steam.</summary>
        public SteamDescriptions Steam { get; }

        /// <summary>
        /// Games on the RomM server released within three days of today's date in an earlier
        /// year, for the library's What's New. Uses the server's release-day filter (newer RomM);
        /// each answer is checked against its date, so a server without the filter (which would
        /// send everything) adds nothing. 1 January is left out: games known only by their year
        /// are put there.
        /// </summary>
        public async Task<List<LibraryHome.ServerAnniversary>> ServerAnniversariesAsync()
        {
            var found = new List<LibraryHome.ServerAnniversary>();
            if (Settings == null || string.IsNullOrEmpty(Settings.RomMHost))
                return found;
            string host = Settings.RomMHost.TrimEnd('/');
            DateTime today = DateTime.Now.Date;
            var days = new List<string>();
            for (int offset = -3; offset <= 3; offset++)
            {
                DateTime day = today.AddDays(offset);
                if (!(day.Month == 1 && day.Day == 1))
                    days.Add($"released_days={day.Month}-{day.Day}");
            }
            try
            {
                string url = $"{host}/api/roms?limit=40&{string.Join("&", days)}&released_before_year={today.Year}&order_by=first_release_date&order_dir=asc";
                using (var response = await HttpClientSingleton.Instance.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return found;
                    var json = Newtonsoft.Json.Linq.JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    foreach (var item in json["items"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                    {
                        RomMRom rom = item.ToObject<RomMRom>();
                        long value = rom?.Metadatum?.Release_Date ?? rom?.FirstReleaseDate ?? 0;
                        if (rom == null || value <= 0)
                            continue;
                        long seconds = value > 100000000000 ? value / 1000 : value;
                        DateTime released = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).Date;
                        if (released.Year >= today.Year || (released.Month == 1 && released.Day == 1))
                            continue;
                        int day = Math.Min(released.Day, DateTime.DaysInMonth(today.Year, released.Month));
                        if (Math.Abs((new DateTime(today.Year, released.Month, day) - today).TotalDays) > 3)
                            continue;
                        string fanart = rom.SSMetadata?.FanartPath;
                        string shot = rom.MergedScreenshots?.FirstOrDefault();
                        string art = !string.IsNullOrEmpty(fanart) ? $"{host}/assets/romm/resources/{fanart}"
                            : !string.IsNullOrEmpty(shot) ? host + "/" + shot.TrimStart('/')
                            : !string.IsNullOrEmpty(rom.PathCoverL) ? host + rom.PathCoverL : null;
                        found.Add(new LibraryHome.ServerAnniversary { RomId = rom.Id, Name = rom.Name ?? rom.FileNameNoExt, Released = released, Art = art });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Home] Could not ask the server for anniversaries: {ex.Message}");
            }
            return found;
        }

        /// <summary>Steam news of the library's games, for the What's New shelf.</summary>
        public SteamNews News { get; }

        /// <summary>Folders games can be installed to besides the platforms' own (the Store's install dialog).</summary>
        public InstallLocations Locations { get; }

        List<string> IRomM.InstallRoots => Locations?.All() ?? new List<string>();

        // Games being installed to another install location, until Playnite asks for their install action.
        private readonly ConcurrentDictionary<Guid, string> _installRoots = new ConcurrentDictionary<Guid, string>();

        /// <summary>
        /// Installs a game to an install location: <paramref name="root"/> (one of Locations), or
        /// its platform's own folder when null. On the UI thread.
        /// </summary>
        internal void InstallTo(Guid gameId, string root)
        {
            // Chosen already (empty: the platform's own folder), so Playnite's install does not ask again.
            _installRoots[gameId] = root ?? "";
            try
            {
                Playnite.InstallGame(gameId);
            }
            finally
            {
                _installRoots.TryRemove(gameId, out string _);
            }
        }

        /// <summary>Leaves the Store (and the library's Home page) for a game's page in the library.</summary>
        internal void ShowGameInLibrary(Guid gameId)
        {
            try
            {
                CloudStatus.StoreVisible = false;
                CloudStatus.LibraryHomeVisible = false;
                Playnite.MainView.SwitchToLibraryView();
                Playnite.MainView.SelectGame(gameId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Library] Could not show the game in the library: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether a game downloads as an archive that is unpacked after (it needs room for both
        /// for a moment), from its ROM sidecar; false when that cannot be told.
        /// </summary>
        internal bool UnpacksAfterDownload(Game game, EmulatorMapping mapping)
        {
            try
            {
                var data = LoadGameData(game);
                var revision = data?.ROMVersions?.FirstOrDefault(r => r.IsSelected) ?? data?.ROMVersions?.FirstOrDefault();
                if (revision == null || mapping == null || !mapping.AutoExtract)
                    return false;
                string ext = revision.DownloadAsArchive ? ".zip" : Path.GetExtension(revision.FileName ?? "");
                return new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz" }
                    .Contains(ext ?? "", StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Xenia Canary's compatibility list and game patches, for the library's Emulation section.</summary>
        public XeniaCompat XeniaCompat { get; }

        /// <summary>DuckStation's, PCSX2's, RPCS3's and Vita3K's compatibility lists, for the same section.</summary>
        public CompatLists CompatLists { get; }

        /// <summary>Compatibility for the Store: a game's verdict, and games newly playing well.</summary>
        public CompatWatch CompatWatch { get; }

        /// <summary>Sunshine (game streaming): installed games listed in it, when the setting is on.</summary>
        public Integrations.SunshineSync Sunshine { get; private set; }

        /// <summary>PC games' controller support (Steam, PCGamingWiki), for the library's Controller section.</summary>
        public PcControllerSupport ControllerSupport { get; }

        /// <summary>Games' Wikipedia introductions, for the Store's About when nothing better is at hand.</summary>
        public WikipediaSummary Wikipedia { get; }

        /// <summary>HowLongToBeat times of RomM games, as the server has them.</summary>
        public HltbStore HltbData { get; }

        /// <summary>
        /// Shows the selected game's HowLongToBeat times: what is kept at once, then (when the
        /// kept times are old or missing) the server's answer, if the game is still selected.
        /// Games not from RomM, and RomM games the server has no times for, show none, so the
        /// theme hides the section.
        /// </summary>
        private void ShowHltb(Game game)
        {
            if (game == null || game.PluginId != Id || Settings == null || string.IsNullOrEmpty(Settings.RomMHost)
                || !RomMGameId.TryParse(game.GameId, out int romId, out string _))
            {
                CloudStatus.Hltb.Show(null, 0);
                return;
            }

            Guid gameId = game.Id;
            CloudStatus.Hltb.Show(HltbData.Kept(romId), game.Playtime);
            if (HltbData.Fresh(romId))
                return;

            string host = Settings.RomMHost.TrimEnd('/');
            HltbData.FetchAsync(host, romId).ContinueWith(done =>
            {
                if (done.Status != TaskStatus.RanToCompletion)
                    return;
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    if (_selectedGameId != gameId)
                        return;
                    Game current = Playnite.Database.Games.Get(gameId);
                    CloudStatus.Hltb.Show(done.Result, current?.Playtime ?? 0);
                }));
            });
        }

        /// <summary>The user's own named collections of library games.</summary>
        public GameCollections Collections { get; }

        /// <summary>The shelves of the library's Home page, as arranged.</summary>
        public LibraryHome.ShelfLayout Shelves { get; }

        /// <summary>Keeps the account's library and collections in step with RomM's collections.</summary>
        public CollectionSync CollectionsSync { get; }

        /// <summary>Keeps the account's statuses, scores, notes and play time in step with RomM.</summary>
        public UserDataSync UserData { get; }

        private System.Windows.Threading.DispatcherTimer _syncTimer;
        private bool _syncSignedIn;
        // Syncs the window's cover waits for (one per sign-in since the last sync).
        private int _curtainSyncs;

        /// <summary>
        /// A sync with the server after the delay (changes close together make one sync), then
        /// every half hour. Nothing while nobody is signed in.
        /// </summary>
        public void ScheduleSync(TimeSpan delay)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => ScheduleSync(delay)));
                return;
            }
            if (!_syncSignedIn)
                return;
            if (_syncTimer == null)
            {
                _syncTimer = new System.Windows.Threading.DispatcherTimer();
                _syncTimer.Tick += (s, e) =>
                {
                    _syncTimer.Interval = TimeSpan.FromMinutes(30);
                    if (_syncSignedIn)
                    {
                        int covered = Interlocked.Exchange(ref _curtainSyncs, 0);
                        Task.Run(() =>
                        {
                            try
                            {
                                CollectionsSync.Run();
                                UserData.Run();
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[Sync] {ex.Message}");
                            }
                            finally
                            {
                                for (int i = 0; i < covered; i++)
                                    Account.SignedOutCurtain.EndBusy();
                            }
                        });
                    }
                };
            }
            _syncTimer.Stop();
            _syncTimer.Interval = delay;
            _syncTimer.Start();
        }

        // The signed-in account's own collections, shelves and sync state; the first account
        // takes the ones kept before accounts had their own.
        private void UseAccountFiles(string accountKey)
        {
            string data = Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString());
            string folder = AccountFiles.Folder(data, accountKey);
            Collections.UseFile(Path.Combine(folder, "Collections.json"), Path.Combine(data, "Collections.json"));
            Shelves.UseFile(Path.Combine(folder, "LibraryShelves.json"), Path.Combine(data, "LibraryShelves.json"));
            Sessions.UseFile(Path.Combine(folder, "PlaySessions.json"), Path.Combine(data, "PlaySessions.json"));
            CollectionsSync.UseAccount(accountKey, Path.Combine(folder, "Sync.json"));
            UserData.UseAccount(accountKey, Path.Combine(folder, "UserData.json"));
            RetroAchievements.UseFile(Path.Combine(folder, "RetroAchievements.json"));
            AchievementStore.UseFolder(Path.Combine(folder, "Achievements"), Path.Combine(data, "Achievements"));
            EmulatorAchievements.UseAccount(folder);
            AchievementSync.UseFile(Path.Combine(folder, "AchievementSync.json"));
            EmulatorUsers.UseAccount(folder);
            _selectionSync = null;   // the last account's progress
        }

        #region RetroAchievements

        /// <summary>The signed-in account's RetroAchievements sign-in (per account).</summary>
        public Account.RetroAchievementsSignIn RetroAchievements { get; } = new Account.RetroAchievementsSignIn();

        // The signed-in RomM user's id, for asking RomM to refresh their RetroAchievements progress.
        private int _rommUserId = -1;

        /// <summary>
        /// Reads the RetroAchievements name in the account's RomM profile (and the user's id), in
        /// the background. Offline, the last one read stands.
        /// </summary>
        public void RefreshRetroAchievementsProfile()
        {
            string host = Settings?.RomMHost?.TrimEnd('/');
            string key = CurrentAccountKey;
            if (string.IsNullOrEmpty(host) || key == null)
                return;
            Task.Run(() =>
            {
                try
                {
                    using (var response = HttpClientSingleton.Instance.GetAsync(host + "/api/users/me").GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        var me = Newtonsoft.Json.Linq.JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        if (key != CurrentAccountKey)
                            return;
                        _rommUserId = (int?)me["id"] ?? -1;
                        RetroAchievements.SetRomMUsername((string)me["ra_username"], notify: true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"[RetroAchievements] Could not read the RomM profile: {ex.Message}");
                    if (key == CurrentAccountKey)
                        RetroAchievements.SetRomMUsername(RetroAchievements.RomMUsername, notify: true);
                }
            });
        }

        /// <summary>
        /// Saves a RetroAchievements username to the signed-in user's RomM profile (from the
        /// achievement guard, when the profile has none), then takes it as the account's. In the
        /// background; <paramref name="done"/> gets null, or what went wrong.
        /// </summary>
        public void SetRetroAchievementsUsername(string name, Action<string> done)
        {
            string host = Settings?.RomMHost?.TrimEnd('/');
            string key = CurrentAccountKey;
            name = (name ?? "").Trim();
            if (string.IsNullOrEmpty(host) || key == null || name.Length == 0)
            {
                done?.Invoke("Not signed in to RomM.");
                return;
            }
            Task.Run(() =>
            {
                string problem = null;
                try
                {
                    if (_rommUserId < 0)
                    {
                        using (var response = HttpClientSingleton.Instance.GetAsync(host + "/api/users/me").GetAwaiter().GetResult())
                        {
                            response.EnsureSuccessStatusCode();
                            _rommUserId = (int?)Newtonsoft.Json.Linq.JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())["id"] ?? -1;
                        }
                    }
                    if (_rommUserId < 0)
                        throw new InvalidOperationException("RomM did not say who is signed in.");

                    var form = new MultipartFormDataContent();
                    form.Add(new StringContent(name), "ra_username");
                    using (var response = HttpClientSingleton.Instance.PutAsync($"{host}/api/users/{_rommUserId}", form).GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            throw new InvalidOperationException($"RomM did not save it ({(int)response.StatusCode}). {body}".Trim());
                        }
                        var saved = Newtonsoft.Json.Linq.JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        if (key == CurrentAccountKey)
                            RetroAchievements.SetRomMUsername((string)saved["ra_username"] ?? name, notify: true);
                    }
                    Logger.Info($"[RetroAchievements] Saved the username {name} to the RomM profile.");
                }
                catch (Exception ex)
                {
                    problem = ex is HttpRequestException ? "Couldn't reach the RomM server." : ex.Message;
                    Logger.Warn($"[RetroAchievements] Could not save the username to the RomM profile: {ex.Message}");
                }
                done?.Invoke(problem);
            });
        }

        /// <summary>The RetroAchievements sign-in window (from an achievement section's guard). On the UI thread.</summary>
        public void SignInToRetroAchievements()
        {
            if (!Account.RetroAchievementsWindow.Show(RetroAchievements))
                return;
            // Freshly signed in: RomM's copy of the progress may be old, so it is asked to refresh.
            RefreshServerAchievements(Playnite.MainView.SelectedGames?.FirstOrDefault());
        }

        /// <summary>
        /// Asks RomM to fetch the user's latest RetroAchievements progress (only what is new), then
        /// redraws the game's achievements from it. In the background.
        /// </summary>
        private void RefreshServerAchievements(Game game)
        {
            string host = Settings?.RomMHost?.TrimEnd('/');
            if (string.IsNullOrEmpty(host) || !RetroAchievements.SignedIn)
                return;
            // Syncing from now: RomM fetching from RetroAchievements, then the progress read.
            if (game != null)
                AchievementSync.Begin(game.Id);
            Task.Run(() =>
            {
                string problem = null;
                try
                {
                    if (_rommUserId < 0)
                    {
                        using (var response = HttpClientSingleton.Instance.GetAsync(host + "/api/users/me").GetAwaiter().GetResult())
                        {
                            response.EnsureSuccessStatusCode();
                            _rommUserId = (int?)Newtonsoft.Json.Linq.JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())["id"] ?? -1;
                        }
                    }
                    if (_rommUserId >= 0)
                    {
                        using (var body = new StringContent("{\"incremental\": true}", Encoding.UTF8, "application/json"))
                        using (var response = HttpClientSingleton.Instance.PostAsync($"{host}/api/users/{_rommUserId}/ra/refresh", body).GetAwaiter().GetResult())
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                Logger.Info($"[RetroAchievements] RomM did not refresh the progress ({(int)response.StatusCode}).");
                                problem = $"RomM could not fetch the newest unlocks from RetroAchievements (HTTP {(int)response.StatusCode}); showing what it had.";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"[RetroAchievements] Could not have RomM refresh the progress: {ex.Message}");
                    problem = $"RomM could not fetch the newest unlocks from RetroAchievements ({ex.Message}); showing what it had.";
                }
                if (game != null)
                {
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        // Its own sync begins before this one ends, so it shows as one.
                        RefreshAchievements(game, force: true, upstreamProblem: problem);
                        AchievementSync.Cancel(game.Id);
                    }));
                }
            });
        }

        /// <summary>
        /// Before a game launches: its emulator signed in to RetroAchievements as the account, or
        /// out when it has no sign-in. Left alone until some account here has signed in (see
        /// RetroAchievementsSignIn.AnyKept).
        /// </summary>
        private void PrepareRetroAchievements(Game game)
        {
            try
            {
                SaveEmulatorResolution resolution = SaveSync.EmulatorFor(game);
                if (resolution?.Emulator == null)
                    return;
                string accounts = Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Accounts");
                bool signedIn = RetroAchievements.SignedIn;
                if (!signedIn && !Account.RetroAchievementsSignIn.AnyKept(accounts))
                    return;
                string token = signedIn ? RetroAchievements.Token() : null;
                Account.RetroAchievementsEmulators.Apply(resolution.Emulator,
                    PlaynitePath.Resolve(Playnite, resolution.Emulator.InstallDir),
                    token != null ? RetroAchievements.Username : null, token);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[RetroAchievements] Could not prepare {game.Name}'s emulator: {ex.Message}");
            }
        }

        #endregion

        /// <summary>Games wanted that the server does not have yet.</summary>
        public Store.Wishlist Wishlist { get; }

        private System.Windows.Threading.DispatcherTimer _wishlistTimer;

        private void StartWishlistChecks()
        {
            if (_wishlistTimer != null || System.Windows.Application.Current == null)
                return;
            _wishlistTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _wishlistTimer.Tick += (s, e) =>
            {
                _wishlistTimer.Interval = TimeSpan.FromMinutes(30);
                CheckWishlistNow();
            };
            _wishlistTimer.Start();
        }

        /// <summary>
        /// Asks the server about the wishlist now; each game that has arrived gets one
        /// notification, which opens its Store page when clicked.
        /// </summary>
        public void CheckWishlistNow()
        {
            string host = Settings?.RomMHost?.TrimEnd('/');
            if (string.IsNullOrEmpty(host) || Wishlist.Count == 0)
                return;
            Task.Run(() => Wishlist.CheckAsync(host, entry =>
            {
                int romId = entry.AvailableRomId;
                Playnite.Notifications.Add(new NotificationMessage(
                    "romm-wishlist-" + romId,
                    $"From your wishlist: \"{entry.Name}\" is now on the RomM server.",
                    NotificationType.Info,
                    () => System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() => CloudStatus.OpenStoreGame(romId)))));
            }));
        }

        // Which Steam app each library game is (0: none), as found out; kept on disk.
        private readonly string _steamIdsPath;
        private Dictionary<Guid, int> _steamIds;
        private readonly object _steamIdsLock = new object();
        private static readonly Guid SteamLibraryId = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
        private static readonly System.Text.RegularExpressions.Regex SteamStoreLink =
            new System.Text.RegularExpressions.Regex(@"store\.steampowered\.com/app/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private Dictionary<Guid, int> SteamIds()
        {
            if (_steamIds != null)
                return _steamIds;
            try
            {
                if (File.Exists(_steamIdsPath))
                    _steamIds = JsonConvert.DeserializeObject<Dictionary<Guid, int>>(File.ReadAllText(_steamIdsPath));
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Steam] Could not read the kept Steam ids: {ex.Message}");
            }
            return _steamIds ?? (_steamIds = new Dictionary<Guid, int>());
        }

        internal void NoteSteamId(Guid gameId, int appId)
        {
            lock (_steamIdsLock)
            {
                var ids = SteamIds();
                if (ids.TryGetValue(gameId, out int known) && known == appId)
                    return;
                ids[gameId] = appId;
                try
                {
                    File.WriteAllText(_steamIdsPath, JsonConvert.SerializeObject(ids));
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[Steam] Could not keep the Steam ids: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The Steam app a library game is, if any: Playnite's own Steam games by their id; others
        /// from a Steam store link on the game, from what has been found before, or (RomM games
        /// on PC platforms, the only ones RomM matches on Steam) by asking the server.
        /// </summary>
        public async Task<int?> SteamAppIdAsync(Game game)
        {
            if (game == null)
                return null;
            if (game.PluginId == SteamLibraryId && int.TryParse(game.GameId, out int steamGame))
                return steamGame;
            foreach (var link in game.Links ?? new System.Collections.ObjectModel.ObservableCollection<Link>())
            {
                var match = SteamStoreLink.Match(link?.Url ?? "");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int linked))
                    return linked;
            }
            lock (_steamIdsLock)
            {
                if (SteamIds().TryGetValue(game.Id, out int known))
                    return known > 0 ? known : (int?)null;
            }

            bool pc = game.Platforms?.Any(p => p?.Name != null && (p.Name.IndexOf("PC", StringComparison.OrdinalIgnoreCase) >= 0
                || p.Name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                || p.Name.IndexOf("Linux", StringComparison.OrdinalIgnoreCase) >= 0
                || p.Name.IndexOf("Mac", StringComparison.OrdinalIgnoreCase) >= 0)) ?? false;
            if (game.PluginId != Id || !pc || Settings == null || string.IsNullOrEmpty(Settings.RomMHost)
                || !RomMGameId.TryParse(game.GameId, out int romId, out string _))
                return null;

            try
            {
                using (var response = await HttpClientSingleton.Instance.GetAsync($"{Settings.RomMHost.TrimEnd('/')}/api/roms/{romId}").ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    var rom = JsonConvert.DeserializeObject<RomMRom>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    NoteSteamId(game.Id, rom?.SteamId ?? 0);
                    return rom?.SteamId;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Steam] Could not look up the Steam id of {game.Name}: {ex.Message}");
                return null;
            }
        }

        // Games whose Steam description has been looked into this session.
        private readonly ConcurrentDictionary<Guid, byte> _steamChecked = new ConcurrentDictionary<Guid, byte>();

        /// <summary>
        /// Gives a selected RomM game Steam's description in place of RomM's, once, when the
        /// server has matched it on Steam. Its description then carries a marker, so it is not
        /// done again; a library update writes Steam's text too, from what is kept on disk.
        /// </summary>
        private void ApplySteamDescription(Game game)
        {
            if (game == null || game.PluginId != Id || Settings == null || !Settings.UseSteamDescriptions
                || string.IsNullOrEmpty(Settings.RomMHost)
                || (game.Description ?? "").StartsWith(SteamDescriptions.Marker)
                || !RomMGameId.TryParse(game.GameId, out int romId, out string _)
                || !_steamChecked.TryAdd(game.Id, 0))
                return;

            Guid gameId = game.Id;
            string host = Settings.RomMHost.TrimEnd('/');
            Task.Run(async () =>
            {
                try
                {
                    RomMRom rom;
                    using (var response = await HttpClientSingleton.Instance.GetAsync($"{host}/api/roms/{romId}").ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return;
                        rom = JsonConvert.DeserializeObject<RomMRom>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    }
                    NoteSteamId(gameId, rom?.SteamId ?? 0);
                    if (rom?.SteamId == null)
                        return;

                    SteamText steam = await Steam.GetAsync(rom.SteamId).ConfigureAwait(false);
                    // Nothing to show in Playnite (no words, only videos): RomM's text stays.
                    string steamHtml = steam?.AboutHtmlForPlaynite;
                    if (string.IsNullOrWhiteSpace(steamHtml))
                        return;
                    string description = SteamDescriptions.Marker + RomMMetadataMapper.DescriptionWithBoxArt(host, rom, null, steamHtml);

                    Playnite.MainView.UIDispatcher.Invoke(() =>
                    {
                        var current = Playnite.Database.Games.Get(gameId);
                        if (current == null || (current.Description ?? "").StartsWith(SteamDescriptions.Marker))
                            return;
                        current.Description = description;
                        Playnite.Database.Games.Update(current);
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Steam] Could not use Steam's description for {game.Name}: {ex.Message}");
                }
            });
        }

        private RomMAchievementSync _selectionSync;
        private DateTime _selectionSyncAt = DateTime.MinValue;
        private readonly ConcurrentDictionary<Guid, byte> _refreshingAchievements = new ConcurrentDictionary<Guid, byte>();

        /// <summary>
        /// Brings a selected game's achievements up to date in the background when they are old
        /// or it has been played since: the set from the game's rom on the server, the progress
        /// from the signed-in user. The user's progress is reused for a few minutes, so browsing
        /// the library does not ask the server for it on every click.
        /// </summary>
        /// <summary>
        /// Once: the library's RomM games given their series as they are now worked out
        /// (Games/SeriesNames: IGDB's collections, not its wider franchises, which put Kingdom
        /// Hearts in "Disney", "Donald Duck" and "Goofy"). Only games whose series are still the
        /// franchises they were imported with; a series the user set is left alone. Marked done
        /// in the plugin's data when every game was reached.
        /// </summary>
        private void FixSeriesOnce()
        {
            string marker = Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "SeriesFromCollections.v2.done");
            try
            {
                if (File.Exists(marker) || Settings == null || string.IsNullOrEmpty(Settings.RomMHost))
                    return;
                var games = Playnite.Database.Games.Where(g => g.PluginId == Id && g.SeriesIds?.Count > 0).ToList();
                int changed = 0, failed = 0;
                System.Threading.Tasks.Parallel.ForEach(games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, game =>
                {
                    try
                    {
                        if (!RomMGameId.TryParse(game.GameId, out int romId, out string _))
                            return;
                        RomMRom rom;
                        using (var response = HttpClientSingleton.Instance.GetAsync(CombineUrl(Settings.RomMHost, $"api/roms/{romId}")).GetAwaiter().GetResult())
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                if ((int)response.StatusCode != 404)
                                    Interlocked.Increment(ref failed);
                                return;
                            }
                            rom = Newtonsoft.Json.JsonConvert.DeserializeObject<RomMRom>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        }
                        if (rom?.Metadatum == null)
                            return;
                        var now = new HashSet<string>(game.Series?.Select(s => s.Name) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                        var franchises = new HashSet<string>((rom.Metadatum.Franchises ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()), StringComparer.OrdinalIgnoreCase);
                        var collections = new HashSet<string>((rom.Metadatum.Collections ?? new List<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
                        // As imported before (franchises), or as the first version of this fix
                        // left it (collections, sub-series and all); anything else is the user's.
                        if (!now.SetEquals(franchises) && !(collections.Count > 0 && now.SetEquals(collections)))
                            return;
                        List<string> wanted = SeriesNames.For(rom.Metadatum.Collections, rom.Metadatum.Franchises, rom.Name);
                        if (new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase).SetEquals(now))
                            return;
                        Playnite.MainView.UIDispatcher.Invoke(() =>
                        {
                            Game current = Playnite.Database.Games.Get(game.Id);
                            if (current == null)
                                return;
                            current.SeriesIds = wanted.Select(name => Playnite.Database.Series.Add(name).Id).ToList();
                            Playnite.Database.Games.Update(current);
                        });
                        Interlocked.Increment(ref changed);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Logger.Debug($"[Series] {game.Name}: {ex.Message}");
                    }
                });
                Logger.Info($"[Series] Series worked out again for the library: {changed} of {games.Count} games changed{(failed > 0 ? $", {failed} not reached (tried again next start)" : "")}.");
                if (failed == 0)
                    File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Series] Could not work the library's series out again: {ex.Message}");
            }
        }

        /// <param name="upstreamProblem">RomM could not fetch the newest unlocks from
        /// RetroAchievements first (after a game was played): the sync counts as failed.</param>
        private void RefreshAchievements(Game game, bool force = false, string upstreamProblem = null)
        {
            if (game == null || game.PluginId != Id || Settings == null || string.IsNullOrEmpty(Settings.RomMHost))
                return;

            var known = AchievementStore.Get(game.Id);
            bool stale = known == null
                || DateTime.UtcNow - known.UpdatedUtc > TimeSpan.FromMinutes(15)
                || (game.LastActivity.HasValue && game.LastActivity.Value.ToUniversalTime() > known.UpdatedUtc);
            if (force)
            {
                stale = true;
                _selectionSync = null;
            }
            if (!stale || !RomMGameId.TryParse(game.GameId, out int romId, out string _))
                return;
            if (!_refreshingAchievements.TryAdd(game.Id, 0))
                return;

            AchievementSync.Begin(game.Id);
            Task.Run(() =>
            {
                bool ended = false;
                Foreground.Begin();
                try
                {
                    RomMRom rom;
                    using (var response = HttpClientSingleton.Instance.GetAsync(CombineUrl(Settings.RomMHost, $"api/roms/{romId}")).GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            // Not known yet whether it has achievements at all: a failure only
                            // for a game known to have them.
                            if (AchievementStore.Get(game.Id)?.Total > 0)
                                AchievementSync.Failed(game.Id, "ra", $"RomM answered HTTP {(int)response.StatusCode}.");
                            else
                                AchievementSync.Cancel(game.Id);
                            ended = true;
                            return;
                        }
                        rom = Newtonsoft.Json.JsonConvert.DeserializeObject<RomMRom>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    }
                    if (rom?.RAId == null || rom.RAMetadata?.Achievements == null || rom.RAMetadata.Achievements.Count == 0)
                    {
                        // No RetroAchievements set: nothing of this kind to sync.
                        AchievementSync.Cancel(game.Id);
                        ended = true;
                        return;
                    }

                    if (_selectionSync == null || DateTime.UtcNow - _selectionSyncAt > TimeSpan.FromMinutes(5))
                    {
                        _selectionSync = RomMAchievementSync.Create(Logger, Settings.RomMHost, CancellationToken.None, AchievementStore);
                        _selectionSyncAt = DateTime.UtcNow;
                    }
                    _selectionSync.Apply(game, rom);
                    if (!_selectionSync.ProgressLoaded)
                    {
                        _selectionSync = null;   // asked again next time
                        AchievementSync.Failed(game.Id, "ra", "the account's progress could not be read from RomM.");
                    }
                    else if (upstreamProblem != null)
                        AchievementSync.Failed(game.Id, "ra", upstreamProblem);
                    else
                        AchievementSync.Succeeded(game.Id, "ra");
                    ended = true;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Achievements] Could not refresh the achievements of {game.Name}: {ex.Message}");
                    if (AchievementStore.Get(game.Id)?.Total > 0)
                        AchievementSync.Failed(game.Id, "ra", ex.Message);
                    else
                        AchievementSync.Cancel(game.Id);
                    ended = true;
                }
                finally
                {
                    if (!ended)
                        AchievementSync.Cancel(game.Id);
                    _refreshingAchievements.TryRemove(game.Id, out byte _);
                    Foreground.End();
                }
            });
        }

        #endregion

        #region Library home

        // Selections made while Home is being shown (clearing the selection, Playnite restoring
        // one at start-up) are not the user picking a game, and must not close Home.
        private DateTime _homeGuardUntil = DateTime.MinValue;

        /// <summary>
        /// For a moment, selections are not the user picking a game: Playnite re-selecting after
        /// the filters change must not close Home (nor be taken as a choice).
        /// </summary>
        public void HoldSelection(TimeSpan forHow)
        {
            DateTime until = DateTime.UtcNow + forHow;
            if (until > _homeGuardUntil)
                _homeGuardUntil = until;
            ScheduleSingleResultCheck();
        }

        // Once the selection settles after a filter change: a filter that leaves one game, which
        // Playnite then selects, opens that game (its selection made while selections were not
        // being taken as choices, or not changed at all, so OnGameSelected did not open it).
        private System.Windows.Threading.DispatcherTimer _singleResultTimer;

        private void ScheduleSingleResultCheck()
        {
            try
            {
                if (_singleResultTimer == null)
                {
                    _singleResultTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
                    _singleResultTimer.Tick += (s, e) =>
                    {
                        _singleResultTimer.Stop();
                        if (DateTime.UtcNow < _homeGuardUntil)
                        {
                            ScheduleSingleResultCheck();
                            return;
                        }
                        OpenSingleResult();
                    };
                }
                _singleResultTimer.Stop();
                TimeSpan wait = _homeGuardUntil - DateTime.UtcNow;
                _singleResultTimer.Interval = (wait > TimeSpan.Zero ? wait : TimeSpan.Zero) + TimeSpan.FromMilliseconds(60);
                _singleResultTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Home] Could not schedule the single-result check: {ex.Message}");
            }
        }

        private void OpenSingleResult()
        {
            try
            {
                if (!CloudStatus.LibraryHomeVisible || CloudStatus.StoreVisible)
                    return;
                List<Game> filtered = Playnite.MainView.FilteredGames?.Take(2).ToList();
                List<Game> selected = Playnite.MainView.SelectedGames?.Take(2).ToList();
                if (filtered == null || filtered.Count != 1 || selected == null || selected.Count != 1 || selected[0].Id != filtered[0].Id)
                    return;
                OpenSelectedGame(selected[0].Id);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Home] Could not open the only game left: {ex.Message}");
            }
        }

        // Leaves Home for the selected game's page.
        private void OpenSelectedGame(Guid gameId)
        {
            CloudStatus.LibraryHomeVisible = false;
            _selectedGameId = gameId;
            if (!CloudStatus.StoreVisible)
                History.Record(new Account.NavEntry { Store = false, Kind = "game", GameId = gameId });
            _downloadStatus?.Select(gameId);
        }

        // A click on a game in the list opens it, even the one already selected (selecting it
        // again changes nothing, so Playnite reports no selection and Home would stay).
        private void OnMainWindowPreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != System.Windows.Input.MouseButton.Left || !CloudStatus.LibraryHomeVisible || CloudStatus.StoreVisible)
                    return;
                if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None)
                    return;
                if (!(e.OriginalSource is System.Windows.DependencyObject source) || !InGameListItem(source))
                    return;
                // After the list has handled the click (and selected the game, if it was not).
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        List<Game> selected = Playnite.MainView.SelectedGames?.Take(2).ToList();
                        if (selected != null && selected.Count == 1 && CloudStatus.LibraryHomeVisible && !CloudStatus.StoreVisible)
                            OpenSelectedGame(selected[0].Id);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[Home] Could not open the clicked game: {ex.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Home] Click not handled: {ex.Message}");
            }
        }

        // Whether the element is inside an item of one of Playnite's game lists (PART_ListGames).
        private static bool InGameListItem(System.Windows.DependencyObject element)
        {
            System.Windows.DependencyObject at = element;
            while (at != null)
            {
                if (at is System.Windows.Controls.ItemsControl list && (list as System.Windows.FrameworkElement)?.Name == "PART_ListGames")
                    return System.Windows.Controls.ItemsControl.ContainerFromElement(list, element) != null;
                at = at is System.Windows.Media.Visual || at is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(at)
                    : System.Windows.LogicalTreeHelper.GetParent(at);
            }
            return false;
        }

        /// <summary>Shows the library's Home page (the Home button, and at start-up).</summary>
        public void ShowLibraryHome() => ShowLibraryPage("Home");

        /// <summary>Shows a library page in the Home place: "Home", or "Downloads".</summary>
        public void ShowLibraryPage(string page)
        {
            _homeGuardUntil = DateTime.UtcNow.AddMilliseconds(800);
            CloudStatus.LibraryPage = page;
            CloudStatus.LibraryHomeVisible = true;
            if (!CloudStatus.StoreVisible)
                History.Record(new Account.NavEntry { Store = false, Kind = page });

            // With nothing selected, clicking any game in the list, including the one that
            // was selected before, counts as picking it.
            //
            // Playnite offers no way to select nothing (asked to select an empty set, it does
            // nothing at all), so the game lists themselves are told to drop their selection,
            // which Playnite then follows.
            try
            {
                var lists = Account.PlayniteParts.FindAll(e => e.Name == "PART_ListGames" && e is System.Windows.Controls.Primitives.Selector);
                foreach (var list in lists)
                {
                    if (list is System.Windows.Controls.ListBox box)
                        box.UnselectAll();
                    else
                        ((System.Windows.Controls.Primitives.Selector)list).SelectedIndex = -1;
                }

                if (lists.Count == 0)
                    Logger.Warn("[Home] No game list was found to clear the selection in.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Home] Could not clear the selection: {ex.Message}");
            }
        }

        #endregion

        #region Library

        /// <summary>The signed-in user's own list of library games.</summary>
        public UserLibrary Library { get; }

        /// <summary>
        /// Adds one of the server's games to the library (the Store's button). Runs the import
        /// for that game alone; call it off the UI thread. Null on success, else the reason.
        /// </summary>
        // Games to take out of Playnite once their uninstall finishes (see RemoveFromAccountLibrary),
        // with when that was asked: an uninstall cancelled and done much later does not count.
        private readonly ConcurrentDictionary<Guid, DateTime> _removeAfterUninstall = new ConcurrentDictionary<Guid, DateTime>();

        /// <summary>
        /// Takes RomM games out of the signed-in account's library, after asking. One another
        /// account on this computer has is only hidden for this one (and stays installed, for
        /// them); otherwise an installed game is uninstalled first and then removed from
        /// Playnite. <paramref name="hidden"/> runs when any was only hidden. On the UI thread.
        /// This stands in for Playnite's own Remove, which is hidden for RomM games (it would
        /// take a game away from every account, and leave its files behind).
        /// </summary>
        public void RemoveFromAccountLibrary(IList<Game> games, Action hidden = null)
        {
            List<Game> list = (games ?? new List<Game>()).Where(g => g != null && g.PluginId == Id && InAccountLibrary(g)).ToList();
            if (list.Count == 0)
                return;
            bool one = list.Count == 1;
            if (list.Any(g => g.IsInstalling))
            {
                Playnite.Dialogs.ShowMessage((one ? $"{list[0].Name} is" : "Some of these games are") +
                    " being installed. Cancel the download first, then remove it.", "Remove from library");
                return;
            }

            int shared = list.Count(g => Libraries.OthersHave(g.Id));
            int installed = list.Count(g => g.IsInstalled && !Libraries.OthersHave(g.Id));
            var question = new StringBuilder($"Remove {(one ? list[0].Name : list.Count + " games")} from your library?");
            if (installed > 0)
                question.Append(one ? "\n\nIt will be uninstalled first." : $"\n\n{installed} installed will be uninstalled first.");
            if (shared > 0)
                question.Append(one ? "\n\nAnother account on this computer has it, so it stays installed for them."
                                    : $"\n\n{shared} that another account on this computer has stay installed for them.");
            question.Append(one ? " It stays available in the Store." : " They stay available in the Store.");
            if (Playnite.Dialogs.ShowMessage(question.ToString(), "Remove from library", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
                return;

            bool anyHidden = false;
            var removeNow = new List<Game>();
            foreach (Game game in list)
            {
                if (Libraries.Disown(Playnite, game.Id))
                {
                    anyHidden = true;
                }
                else if (game.IsInstalled)
                {
                    // Removed from Playnite when the uninstall finishes (OnItemUpdated).
                    _removeAfterUninstall[game.Id] = DateTime.UtcNow;
                    Playnite.UninstallGame(game.Id);
                }
                else
                {
                    removeNow.Add(game);
                }
            }
            if (removeNow.Count > 0)
                Playnite.Database.Games.Remove(removeNow);
            if (anyHidden)
            {
                ScheduleSync(TimeSpan.FromSeconds(5));
                hidden?.Invoke();
            }
        }

        public const string RemoveMenuItem = "Remove from my library";

        // Playnite's own Remove item in a game menu that has ours (so: RomM games only) is hidden.
        private static readonly string[] s_nativeRemoveKeys = { "LOCRemoveGame", "LOCRemoveTitle", "LOCRemove" };

        // Playnite's game menu fills itself in its own Opened handler, which runs after this class
        // handler (class handlers come first): the items are looked at once that has run, before
        // the menu is drawn.
        private void OnAnyContextMenuOpened(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is ContextMenu opened)
                opened.Dispatcher.BeginInvoke(new Action(() =>
                {
                    HideNativeRemove(opened);
                    ArrangeManage(opened);
                }), System.Windows.Threading.DispatcherPriority.Send);
        }

        private void HideNativeRemove(ContextMenu menu)
        {
            try
            {
                // Ours may sit at the top or in a plugin's submenu; Playnite's Remove is at the top.
                var all = new List<MenuItem>();
                var pending = new Stack<MenuItem>(menu.Items.OfType<MenuItem>());
                while (pending.Count > 0)
                {
                    MenuItem next = pending.Pop();
                    all.Add(next);
                    foreach (MenuItem child in next.Items.OfType<MenuItem>())
                        pending.Push(child);
                }
                if (!all.Any(i => (i.Header as string) == RemoveMenuItem))
                    return;
                List<MenuItem> items = menu.Items.OfType<MenuItem>().ToList();
                var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { "Remove" };
                foreach (string key in s_nativeRemoveKeys)
                {
                    string text = ResourceProvider.GetString(key);
                    if (!string.IsNullOrEmpty(text) && !text.StartsWith("<!"))
                        names.Add(text.Replace("_", ""));
                }
                foreach (MenuItem item in items)
                {
                    string header = (item.Header as string)?.Replace("_", "").Trim().TrimEnd('.', '\u2026').Trim();
                    if (header == null || !names.Contains(header))
                        continue;
                    item.Visibility = System.Windows.Visibility.Collapsed;
                    // The line Playnite puts before it, when a line (or the end) follows it too.
                    int at = menu.Items.IndexOf(item);
                    object before = at > 0 ? menu.Items[at - 1] : null;
                    object after = at + 1 < menu.Items.Count ? menu.Items[at + 1] : null;
                    if (before is Separator line && (after == null || after is Separator))
                        line.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Library] Could not hide Playnite's Remove item: {ex.Message}");
            }
        }

        // ---- Playnite's game menu: Manage --------------------------------------------------------

        // Playnite's own items that manage a game, by the words its menu shows them with (its
        // translation where it has the text, else English): what goes into Manage, in order.
        private static readonly Tuple<string[], string[]>[] s_manageItems =
        {
            Tuple.Create(new[] { "LOCEditGame", "LOCEdit" }, new[] { "Edit" }),
            Tuple.Create(new[] { "LOCHideGame", "LOCUnHideGame" }, new[] { "Hide", "Unhide", "Hide this game" }),
            Tuple.Create(new[] { "LOCOpenGameLocation", "LOCOpenInstallLocation" }, new[] { "Open Game Location", "Open game location", "Open Install Location", "Browse local files" }),
            Tuple.Create(new[] { "LOCCreateDesktopShortcut" }, new[] { "Create Desktop Shortcut", "Create desktop shortcut" }),
            Tuple.Create(new[] { "LOCUninstallGame", "LOCUninstall" }, new[] { "Uninstall" }),
        };

        private static string Words(object header) => (header as string)?.Replace("_", "").Trim().TrimEnd('.', '\u2026').Trim();

        private const int EditRank = 0;

        // The game's "View in RomM" action (RomMImport), which Playnite lists beside Play: into Links.
        private static readonly string[] s_viewInRomMWords = { "View in RomM" };

        // Playnite's Links submenu and its Open Manual, which goes into Links.
        private static readonly string[] s_linksKeys = { "LOCLinksLabel", "LOCLinks" };
        private static readonly string[] s_linksWords = { "Links" };
        private static readonly string[] s_manualKeys = { "LOCOpenGameManual", "LOCOpenManual", "LOCGameManual" };
        private static readonly string[] s_manualWords = { "Open Manual", "Open manual", "Manual" };

        private static bool Says(MenuItem item, string[] keys, string[] english)
        {
            string words = Words(item?.Header);
            if (string.IsNullOrEmpty(words))
                return false;
            foreach (string key in keys)
            {
                string text = Words(ResourceProvider.GetString(key));
                if (!string.IsNullOrEmpty(text) && !text.StartsWith("<!") && string.Equals(text, words, StringComparison.CurrentCultureIgnoreCase))
                    return true;
            }
            return english.Any(t => string.Equals(t, words, StringComparison.OrdinalIgnoreCase));
        }

        private static int ManageRank(MenuItem item)
        {
            string words = Words(item.Header);
            if (string.IsNullOrEmpty(words))
                return -1;
            for (int i = 0; i < s_manageItems.Length; i++)
            {
                foreach (string key in s_manageItems[i].Item1)
                {
                    string text = Words(ResourceProvider.GetString(key));
                    if (!string.IsNullOrEmpty(text) && !text.StartsWith("<!") && string.Equals(text, words, StringComparison.CurrentCultureIgnoreCase))
                        return i;
                }
                if (s_manageItems[i].Item2.Any(t => string.Equals(t, words, StringComparison.OrdinalIgnoreCase)))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Gathers a game menu's managing items into one "Manage" submenu, as Steam's: Playnite's
        /// Edit, Hide, Open location, Desktop shortcut and Uninstall, and the extensions' own
        /// "Manage" section (RomM's Remove from my library, Reinstall emulator, Uninstall
        /// platform). Only a menu with Playnite's Edit (a game's menu) is touched. Uninstall
        /// asks first (Playnite does not). Playnite builds the menu afresh at each opening,
        /// so nothing here outlives it.
        /// </summary>
        private void ArrangeManage(ContextMenu menu)
        {
            try
            {
                var top = menu.Items.OfType<MenuItem>().ToList();
                var picked = top.Select(i => Tuple.Create(i, ManageRank(i))).Where(t => t.Item2 >= 0).OrderBy(t => t.Item2).ToList();
                if (!picked.Any(t => t.Item2 == EditRank))
                    return;   // not a game's menu

                // The extensions' Manage section, wherever Playnite put it, brought to the top level.
                MenuItem manage = FindSection(menu.Items, LibraryHome.LibraryHomeView.ManageHeader);
                if (manage != null && !menu.Items.Contains(manage))
                {
                    if (manage.Parent is ItemsControl holder)
                        holder.Items.Remove(manage);
                    manage = AtTop(menu, manage, picked[0].Item1);
                }
                else if (manage == null)
                {
                    manage = AtTop(menu, new MenuItem { Header = LibraryHome.LibraryHomeView.ManageHeader }, picked[0].Item1);
                }
                var theirs = manage.Items.Cast<object>().ToList();
                manage.Items.Clear();

                int at = menu.Items.IndexOf(manage);
                foreach (var pick in picked)
                {
                    MenuItem item = pick.Item1;
                    menu.Items.Remove(item);
                    if (item.Visibility == System.Windows.Visibility.Visible)
                        manage.Items.Add(item);
                    if (pick.Item2 == s_manageItems.Length - 1)
                        AskBeforeUninstall(menu, item);
                }
                if (theirs.Count > 0)
                {
                    if (manage.Items.Count > 0)
                        manage.Items.Add(new Separator());
                    foreach (object entry in theirs)
                        manage.Items.Add(entry);
                }
                LibraryHome.LibraryHomeView.SyncFirst(manage);

                // View in RomM at the top of Links and Open Manual at its foot (Links made when the
                // game has no links of its own but has one of these), and Links just below Manage.
                MenuItem view = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Says(i, new string[0], s_viewInRomMWords) && !i.HasItems);
                MenuItem manual = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Says(i, s_manualKeys, s_manualWords) && !i.HasItems);
                MenuItem links = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Says(i, s_linksKeys, s_linksWords) && i.HasItems);
                if ((view != null || manual != null) && links == null)
                    links = new MenuItem { Header = ResourceProvider.GetString("LOCLinksLabel") is string label && label.Length > 0 && !label.StartsWith("<!") ? label : "Links" };
                if (view != null)
                {
                    menu.Items.Remove(view);
                    bool others = links.Items.Count > 0;
                    links.Items.Insert(0, view);
                    if (others)
                        links.Items.Insert(1, new Separator());
                }
                if (manual != null)
                {
                    menu.Items.Remove(manual);
                    if (links.Items.Count > 0)
                        links.Items.Add(new Separator());
                    links.Items.Add(manual);
                }
                if (links != null)
                {
                    if (menu.Items.Contains(links))
                        menu.Items.Remove(links);
                    menu.Items.Insert(menu.Items.IndexOf(manage) + 1, links);
                }
                TidySeparators(menu);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Library] Could not arrange the game menu's Manage section: {ex.Message}");
            }
        }

        private static MenuItem AtTop(ContextMenu menu, MenuItem manage, MenuItem near)
        {
            // Where the first of its items was, so it sits among Playnite's own.
            int index = Math.Max(0, menu.Items.IndexOf(near));
            menu.Items.Insert(index, manage);
            return manage;
        }

        private static MenuItem FindSection(ItemCollection items, string header)
        {
            foreach (MenuItem item in items.OfType<MenuItem>())
            {
                if (string.Equals(Words(item.Header), header, StringComparison.OrdinalIgnoreCase) && item.HasItems)
                    return item;
                MenuItem inner = FindSection(item.Items, header);
                if (inner != null)
                    return inner;
            }
            return null;
        }

        // Lines at either end, or two in a row (once their neighbours moved), are hidden.
        private static void TidySeparators(ContextMenu menu)
        {
            object previous = null;
            foreach (object entry in menu.Items)
            {
                if (entry is Separator line)
                {
                    bool afterLine = previous == null || previous is Separator;
                    line.Visibility = afterLine ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    if (!afterLine)
                        previous = line;
                    continue;
                }
                if (entry is UIElement element && element.Visibility != System.Windows.Visibility.Visible)
                    continue;
                previous = entry;
            }
            if (previous is Separator last)
                last.Visibility = System.Windows.Visibility.Collapsed;
        }

        // Playnite's Uninstall asks first: the click is taken here, the menu closed, and the
        // games chosen in the library uninstalled once confirmed.
        private void AskBeforeUninstall(ContextMenu menu, MenuItem item)
        {
            item.PreviewMouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                menu.IsOpen = false;
                List<Game> games = (Playnite.MainView.SelectedGames ?? Enumerable.Empty<Game>()).ToList();
                Playnite.MainView.UIDispatcher.BeginInvoke(new Action(() => UninstallConfirm.AskAndUninstall(Playnite, games)));
            };
            item.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter && e.Key != System.Windows.Input.Key.Space)
                    return;
                e.Handled = true;
                menu.IsOpen = false;
                List<Game> games = (Playnite.MainView.SelectedGames ?? Enumerable.Empty<Game>()).ToList();
                Playnite.MainView.UIDispatcher.BeginInvoke(new Action(() => UninstallConfirm.AskAndUninstall(Playnite, games)));
            };
        }

        // Delete in the library is Playnite's Remove too: for RomM games, ours instead.
        private void OnMainWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            try
            {
                if (e.Key != System.Windows.Input.Key.Delete || e.Handled
                    || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                    return;
                List<Game> selected = Playnite.MainView.SelectedGames?.ToList() ?? new List<Game>();
                if (selected.Count == 0 || !selected.All(g => g.PluginId == Id && InAccountLibrary(g)))
                    return;
                e.Handled = true;
                RemoveFromAccountLibrary(selected);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Library] Could not handle Delete: {ex.Message}");
            }
        }

        public string AddToLibrary(int romId)
        {
            try
            {
                // Already in Playnite for another account (installed or not): this account
                // only has to take it, which needs no server.
                Game present = Playnite.Database.Games.FirstOrDefault(g =>
                    g.PluginId == Id && RomMGameId.TryParse(g.GameId, out int presentId, out string _) && presentId == romId);
                if (present != null)
                {
                    if (!InAccountLibrary(present))
                    {
                        Guid presentGame = present.Id;
                        Playnite.MainView.UIDispatcher.Invoke(() => Libraries.Own(Playnite, presentGame));
                        Library.Add(romId);
                        ScheduleSync(TimeSpan.FromSeconds(5));
                        DownloadQueueController?.Recent?.MarkAdded(presentGame);
                        SyncSavesOnAdd(presentGame);
                    }
                    return null;
                }

                if (!Settings.TestConnection(false, false))
                    return "Could not connect to the RomM server.";

                // A game the user once removed with "do not import again" is wanted again now.
                foreach (var excluded in Playnite.Database.ImportExclusions.Where(x => x.LibraryId == Id && x.GameId != null && x.GameId.StartsWith(romId + ":")).ToList())
                    Playnite.Database.ImportExclusions.Remove(excluded);

                Library.Add(romId);
                string problem = new RomMImportController(this).ImportSingle(romId, CancellationToken.None);
                if (problem != null)
                    Library.Remove(romId);
                else
                {
                    // Newly added: at the front of Recent Games, as a download is.
                    Game added = Playnite.Database.Games.FirstOrDefault(g =>
                        g.PluginId == Id && RomMGameId.TryParse(g.GameId, out int addedId, out string _) && addedId == romId);
                    if (added != null)
                    {
                        DownloadQueueController?.Recent?.MarkAdded(added.Id);
                        SyncSavesOnAdd(added.Id);
                    }
                }
                return problem;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[Library] Could not add ROM {romId} to the library.");
                return ex.Message;
            }
        }

        /// <summary>
        /// A game just added to the library: its cloud saves brought down (or up) now, when they
        /// can be: cloud saves on for it, its emulator set up, and no other account's save in the
        /// way (SaveSync says why not otherwise, in the log and the Cloud Saves status). In the
        /// background, bounded like the pre-launch sync.
        /// </summary>
        private void SyncSavesOnAdd(Guid gameId)
        {
            Task.Run(() =>
            {
                try
                {
                    Game game = Playnite.Database.Games.Get(gameId);
                    if (game == null || SaveSync == null)
                        return;
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                    {
                        var outcome = SaveSync.Sync(game, deadline.Token);
                        if (outcome.Applicable)
                            Logger.Info($"[SaveSync] {game.Name}: synced on being added to the library.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[SaveSync] Could not sync saves for a game just added: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// A game's emulator, before it is installed or played: see <see cref="SetUpPlatform"/>.
        /// Returns the mapping, or null (the user has been told why). On the UI thread.
        /// </summary>
        internal EmulatorMapping SetUpPlatformFor(Game game)
        {
            RomMPlatform platform = PlatformOf(game);
            if (platform == null)
            {
                Playnite.Dialogs.ShowErrorMessage($"No emulator is set up for {game.Name}'s platform, and which RomM platform it is could not be worked out. " +
                                                  "Choose an emulator for it in the RomMnite settings, then try again.", "RomMnite");
                return null;
            }
            return SetUpPlatform(platform, game.Name);
        }

        /// <summary>
        /// A RomM platform's emulator, made ready without asking: the mapping's emulator when it
        /// is on this computer; else one already unpacked in Playnite\Emulators (hooked up), or
        /// the copy on the RomM server (downloaded); and only when the server has none, or its
        /// copy fails, the user is asked before it is downloaded online. Every game waiting on
        /// the platform then takes the mapping. Returns it, or null (declined, or not possible:
        /// the user has been told). On the UI thread.
        /// </summary>
        internal EmulatorMapping SetUpPlatform(RomMPlatform platform, string forGame = null)
        {
            var dialogs = Playnite.Dialogs;
            EmulatorMapping mapping = MappingForPlatform(platform.Id);
            // Windows games: no emulator to fetch, only Playnite's way to start them.
            if ((mapping == null || EmulatorSetup?.IsPresent(mapping.Emulator) != true) && Setup.EmulatorSetup.IsWindowsPlatform(platform) && EmulatorSetup != null)
                mapping = EmulatorSetup.SetUpWindows(platform);
            if (mapping == null || EmulatorSetup?.IsPresent(mapping.Emulator) != true)
            {
                Setup.EmulatorSetup.Plan plan = EmulatorSetup?.PlanFor(platform.Id);
                if (plan == null)
                {
                    dialogs.ShowMessage(mapping != null
                        ? $"{mapping.Emulator?.Name ?? "The emulator"} for {platform.PlayniteName} is not on this computer, and it is not one this can install by itself. " +
                          $"Install it again, or choose another emulator for {platform.PlayniteName} in the RomMnite settings."
                        : $"Nothing plays {platform.PlayniteName} in Playnite yet, and it is not a platform this can set up by itself. " +
                          $"Choose an emulator for it in the RomMnite settings (emulator mappings).",
                        "RomMnite", MessageBoxButton.OK, MessageBoxImage.Information);
                    return null;
                }

                // Its BIOS files first: without them the emulator would be set up only to fail at
                // the first game. Nothing is installed; the user is told what to upload.
                List<Setup.BiosNeeds.Need> missingBios = EmulatorSetup.MissingBios(plan);
                if (missingBios != null && missingBios.Count > 0)
                {
                    string slug = platform.FsSlug ?? platform.Slug;
                    dialogs.ShowMessage(
                        $"{platform.PlayniteName} needs BIOS files that are not on your RomM server yet, so {plan.EmulatorName} was not set up" +
                        $"{(forGame != null ? " for " + forGame : "")}:\n\n" +
                        string.Join("\n", missingBios.Select(m => "\u2022 " + m.Label)) +
                        $"\n\nUpload them to {platform.PlayniteName}'s firmware in RomM (or put them in the library's bios\\{slug} folder and scan it), then try again.",
                        $"Set up {platform.PlayniteName}", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }

                // Silently: what is on this computer, else the RomM server's copy.
                string problem = EmulatorSetup.Run(plan, fromServer: plan.Downloads);
                if (problem != null && plan.Downloads && problem != "Cancelled.")
                {
                    // Online only when asked.
                    if (dialogs.ShowMessage($"{plan.EmulatorName} could not be set up from your RomM server{(forGame != null ? " for " + forGame : "")}.\n\n{problem}\n\n" +
                                            $"Download it online instead?\n\n{plan.Describe(fromServer: false)}",
                            $"Set up {platform.PlayniteName}", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        return null;
                    problem = EmulatorSetup.Run(plan, fromServer: false);
                }
                if (problem != null)
                {
                    if (problem != "Cancelled.")
                        dialogs.ShowErrorMessage($"{plan.EmulatorName} could not be set up for {platform.PlayniteName}.\n\n{problem}", "RomMnite");
                    return null;
                }
                mapping = MappingForPlatform(platform.Id);
                if (mapping == null)
                    return null;
            }

            AdoptWaitingGames(mapping);
            return mapping;
        }

        /// <summary>
        /// Games installed before LaunchFiles: those offering several files (a disc's .cue and
        /// its .bin tracks) are trimmed to the one to start, so Play no longer asks. Existing
        /// .m3u files are used; none is made here.
        /// </summary>
        private void TrimLaunchFiles()
        {
            try
            {
                foreach (Game game in Playnite.Database.Games.Where(g => g.PluginId == Id && g.IsInstalled && g.Roms != null && g.Roms.Count > 1).ToList())
                {
                    List<string> paths = game.Roms.Select(r => r.Path).ToList();
                    List<string> keep = LaunchFiles.Pick(paths, m3uSupported: true);
                    if (keep.Count == 0 || keep.Count >= paths.Count)
                        continue;
                    game.Roms = new System.Collections.ObjectModel.ObservableCollection<GameRom>(game.Roms.Where(r => keep.Contains(r.Path, StringComparer.OrdinalIgnoreCase)));
                    Playnite.Database.Games.Update(game);
                    Logger.Info($"[Install] {game.Name}: starts from {string.Join(", ", keep.Select(Path.GetFileName))} (was {paths.Count} files to choose from).");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Install] Could not trim the games' launch files: {ex.Message}");
            }
        }

        /// <summary>Whether a RomM platform's games can be played here: mapped, and its emulator on this computer.</summary>
        internal bool PlatformReady(int romMPlatformId)
        {
            EmulatorMapping mapping = MappingForPlatform(romMPlatformId);
            return mapping != null && EmulatorSetup?.IsPresent(mapping.Emulator) == true;
        }

        /// <summary>
        /// The Windows platform mapped from the start (its "emulator" is only Playnite's way to
        /// start a game's own program, so there is nothing to install), and the Windows games
        /// waiting on it given it. On the UI thread.
        /// </summary>
        private void EnsureWindowsPlatform()
        {
            try
            {
                RomMPlatform windows = Settings?.RomMPlatforms?.FirstOrDefault(Setup.EmulatorSetup.IsWindowsPlatform);
                if (windows == null || EmulatorSetup == null || PlatformReady(windows.Id))
                    return;
                EmulatorMapping mapping = EmulatorSetup.SetUpWindows(windows);
                AdoptWaitingGames(mapping);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EmulatorSetup] Could not set up the Windows platform: {ex.Message}");
            }
        }

        /// <summary>
        /// Every mapping whose emulator is on this computer (the user's own included: a platform
        /// they defined themselves) given to the games on its platform still waiting for one, so
        /// their Play works. At start and when the settings are saved. On the UI thread.
        /// </summary>
        internal void AdoptReadyMappings()
        {
            try
            {
                if (Settings?.Mappings == null || EmulatorSetup == null)
                    return;
                List<Game> waiting = Playnite.Database.Games.Where(g => g.PluginId == Id && NeedsEmulator(g)).ToList();
                if (waiting.Count == 0)
                    return;
                foreach (EmulatorMapping mapping in Settings.Mappings.Where(m => m.Enabled && m.IsImportable).ToList())
                {
                    string name = mapping.RomMPlatform?.PlayniteName;
                    if (string.IsNullOrEmpty(name) || !EmulatorSetup.IsPresent(mapping.Emulator))
                        continue;
                    if (waiting.Any(g => g.Platforms?.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == true))
                        AdoptWaitingGames(mapping);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Library] Could not give waiting games their platforms' emulators: {ex.Message}");
            }
        }

        // ---- "Launching..." until the game has started (GameActionButton) ----------------------

        private readonly ConcurrentDictionary<Guid, DateTime> _launching = new ConcurrentDictionary<Guid, DateTime>();

        /// <summary>Play was pressed for the game: it reads "Launching..." until it starts or fails.</summary>
        internal void MarkLaunching(Guid gameId) => _launching[gameId] = DateTime.UtcNow;

        internal void ClearLaunching(Guid gameId) => _launching.TryRemove(gameId, out DateTime _);

        /// <summary>
        /// After Play: when Playnite did not go on to start the game -- its "which file?" or "which
        /// action?" choice was cancelled, which no event reports -- the button is let go at once
        /// rather than after 20 seconds. Checked a moment after Playnite has had the request.
        /// </summary>
        internal void ReleaseIfNotStarted(Guid gameId)
        {
            var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            check.Tick += (s, e) =>
            {
                check.Stop();
                Game game = Playnite.Database.Games.Get(gameId);
                if (game == null || (!game.IsLaunching && !game.IsRunning && !_settingUpFor.ContainsKey(gameId)))
                    ClearLaunching(gameId);
            };
            check.Start();
        }

        // Until Playnite takes over (it marks the game launching itself), or while its emulator
        // is being set up first; a launch that never got going is let go after 20 seconds.
        internal bool IsLaunchPending(Guid gameId) =>
            _settingUpFor.ContainsKey(gameId)
            || (_launching.TryGetValue(gameId, out DateTime since) && DateTime.UtcNow - since < TimeSpan.FromSeconds(20));

        // The in-game overlay (Guide, Back + Start, or Shift+Tab over a running emulated game).
        private Overlay.GameOverlay _overlay;

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            base.OnGameStarted(args);
            if (args?.Game != null)
            {
                ClearLaunching(args.Game.Id);
                OpenGames.Started(args.Game.Id, args.Game.Name, args.StartedProcessId, args.Game.Playtime, CurrentAccountKey);
            }
            // The overlay: RomM's games, and Windows games from any library (GameOverlay decides,
            // by the settings).
            if (args?.Game != null && (args.Game.PluginId == Id || Overlay.GameOverlay.IsPc(args.Game)))
            {
                try
                {
                    _overlay = _overlay ?? new Overlay.GameOverlay(this);
                    _overlay.Start(args.Game, args.StartedProcessId);
                    // Its achievements as up to date as the server has them, for the overlay.
                    RefreshAchievements(args.Game);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Overlay] Could not start: {ex.Message}");
                }
            }
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            base.OnGameStartupCancelled(args);
            // Not when this cancelled it to set its emulator up first: it starts again after.
            if (args?.Game != null && !_settingUpFor.ContainsKey(args.Game.Id))
                ClearLaunching(args.Game.Id);
        }

        private readonly ConcurrentDictionary<Guid, byte> _settingUpFor = new ConcurrentDictionary<Guid, byte>();

        /// <summary>
        /// Whether a RomM game cannot be played here for want of its emulator: added before
        /// anything played its platform, or the emulator its play action (or mapping) names is
        /// not on this computer.
        /// </summary>
        internal bool NeedsEmulator(Game game)
        {
            try
            {
                if (game == null || game.PluginId != Id || EmulatorSetup == null)
                    return false;
                // The emulator its play action names; without one, its mapping's.
                GameAction action = RomMPlayAction.Find(game.GameActions);
                if (action != null)
                    return !EmulatorSetup.IsPresent(Playnite.Database.Emulators.Get(action.EmulatorId));
                EmulatorMapping mapping = MappingFor(game);
                return mapping == null || !EmulatorSetup.IsPresent(mapping.Emulator);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Library] Could not tell whether {game?.Name}'s emulator is here: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Whether an emulator can be set up here for a RomM platform: Windows games (nothing to
        /// install), or a platform the emulator setup knows. Not: the Store's UNSUPPORTED PLATFORM.
        /// </summary>
        internal bool EmulatorAvailable(int romMPlatformId)
        {
            try
            {
                RomMPlatform platform = Settings?.RomMPlatforms?.FirstOrDefault(p => p.Id == romMPlatformId);
                if (platform == null || EmulatorSetup == null)
                    return true;   // not known: Install Emulator says why when pressed
                // A mapping of the user's own counts: its emulator is theirs to have set up.
                return Setup.EmulatorSetup.IsWindowsPlatform(platform) || EmulatorSetup.PlanFor(romMPlatformId) != null
                       || MappingForPlatform(romMPlatformId) != null;
            }
            catch
            {
                return true;
            }
        }

        // Platforms whose emulator setup lacks a file on the server (EmulatorSetup.PlatformsMissingFiles),
        // by id, as last checked: true when lacking, with when.
        private readonly ConcurrentDictionary<int, Tuple<bool, DateTime>> _filesMissing = new ConcurrentDictionary<int, Tuple<bool, DateTime>>();
        private readonly ConcurrentDictionary<int, byte> _checkingFiles = new ConcurrentDictionary<int, byte>();

        /// <summary>The result of a check of these platforms (the Store's, or FilesMissing's own).</summary>
        internal void NoteMissingFiles(IEnumerable<int> checkedIds, ICollection<int> missing)
        {
            DateTime now = DateTime.UtcNow;
            foreach (int id in checkedIds)
                _filesMissing[id] = Tuple.Create(missing.Contains(id), now);
        }

        /// <summary>
        /// Whether a platform's emulator setup lacks a file on the RomM server (the Store's MISSING
        /// EMULATOR), as last checked: null while not known yet. A check older than two minutes is
        /// made again in the background (it asks the server), so asking often is cheap.
        /// </summary>
        internal bool? FilesMissing(int romMPlatformId)
        {
            // Played by a mapping whose emulator is here (the user's own, say): nothing is missing.
            if (PlatformReady(romMPlatformId))
                return false;
            bool known = _filesMissing.TryGetValue(romMPlatformId, out Tuple<bool, DateTime> last);
            if ((!known || DateTime.UtcNow - last.Item2 > TimeSpan.FromMinutes(2))
                && EmulatorSetup != null && _checkingFiles.TryAdd(romMPlatformId, 0))
            {
                Task.Run(() =>
                {
                    try
                    {
                        HashSet<int> missing = EmulatorSetup.PlatformsMissingFiles(new[] { romMPlatformId });
                        if (missing != null)
                            NoteMissingFiles(new[] { romMPlatformId }, missing);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[EmulatorSetup] Could not check platform {romMPlatformId}'s files: {ex.Message}");
                    }
                    finally
                    {
                        _checkingFiles.TryRemove(romMPlatformId, out byte _);
                    }
                });
            }
            return known ? last.Item1 : (bool?)null;
        }

        /// <summary>Where a game stands with its emulator, as the Store's button would say it (Home's big Recent card).</summary>
        internal LibraryHome.HeroEmulator HeroEmulatorOf(Game game)
        {
            if (game == null || game.PluginId != Id || game.IsInstalling || !NeedsEmulator(game))
                return LibraryHome.HeroEmulator.Ready;
            int platformId = PlatformIdOf(game);
            if (platformId > 0 && !EmulatorAvailable(platformId))
                return LibraryHome.HeroEmulator.Unsupported;
            if (platformId > 0 && FilesMissing(platformId) == true)
                return LibraryHome.HeroEmulator.Unavailable;
            return LibraryHome.HeroEmulator.InstallEmulator;
        }

        /// <summary>The RomM platform id a game is on (by its Playnite platform), or 0.</summary>
        internal int PlatformIdOf(Game game) => game == null ? 0 : KnownPlatformOf(game)?.Id ?? 0;

        // Games to download without setting up an emulator (InstallWithoutEmulator), until
        // Playnite asks for their install action.
        private readonly ConcurrentDictionary<Guid, byte> _installWithoutEmulator = new ConcurrentDictionary<Guid, byte>();

        /// <summary>
        /// Downloads a game whether or not anything here plays its platform: no emulator is set up
        /// or asked for. It goes where its platform's games go (where an emulator set up later
        /// finds it) and waits for one (Install Emulator / Emulator Unavailable). On the UI thread.
        /// </summary>
        internal void InstallWithoutEmulator(Guid gameId)
        {
            _installWithoutEmulator[gameId] = 0;
            try
            {
                Playnite.InstallGame(gameId);
            }
            finally
            {
                _installWithoutEmulator.TryRemove(gameId, out byte _);
            }
        }

        internal EmulatorMapping MappingForPlatform(int romMPlatformId) =>
            Settings?.Mappings?.FirstOrDefault(m => m.Enabled && m.IsImportable && m.RomMPlatformId == romMPlatformId);

        // The RomM platform a game was filed under, by its Playnite platform's name only (no server
        // call: for menus); null when that does not tell.
        RomMPlatform IRomM.KnownPlatformOf(Game game) => KnownPlatformOf(game);
        Setup.EmulatorSetup IRomM.EmulatorSetup => EmulatorSetup;

        private RomMPlatform KnownPlatformOf(Game game)
        {
            var names = new HashSet<string>((game.Platforms ?? new List<Platform>()).Select(p => p.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            return names.Count == 0 ? null : Settings?.RomMPlatforms?.FirstOrDefault(p => names.Contains(p.PlayniteName ?? ""));
        }

        /// <summary>
        /// Takes a platform off this computer, after asking: its installed games are uninstalled
        /// (as their own Uninstall does), its emulator mapping is removed, and its emulator too
        /// when no other platform uses it -- its folder to the Recycle Bin when this set it up
        /// (under Playnite\Emulators), else only Playnite's entry. The games stay in the library,
        /// waiting for an emulator again ("Install Emulator"). On the UI thread.
        /// </summary>
        /// <summary>
        /// The platform's emulator downloaded again over itself, after asking (EmulatorSetup.Reinstall):
        /// its settings, saves and BIOS stay. Not while one of its games runs. On the UI thread.
        /// </summary>
        internal void ReinstallEmulator(RomMPlatform platform)
        {
            var dialogs = Playnite.Dialogs;
            try
            {
                Setup.EmulatorSetup.Plan plan = EmulatorSetup?.PlanFor(platform.Id);
                if (plan?.Existing == null || plan.Existing.Id != MappingForPlatform(platform.Id)?.EmulatorId)
                    return;
                Guid emulatorId = plan.Existing.Id;
                bool busy = Playnite.Database.Games.Any(g => (g.IsRunning || g.IsLaunching)
                    && RomMPlayAction.Find(g.GameActions)?.EmulatorId == emulatorId);
                if (busy)
                {
                    dialogs.ShowMessage($"A game is running in {plan.EmulatorName}. Close it, then try again.", "Reinstall emulator",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (dialogs.ShowMessage($"Reinstall {plan.EmulatorName}?\n\nIts program is downloaded again. Your settings, saves and BIOS files are kept.",
                        "Reinstall emulator", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                string problem = EmulatorSetup.Reinstall(plan);
                if (problem != null && problem != "Cancelled.")
                    dialogs.ShowErrorMessage($"{plan.EmulatorName} could not be reinstalled.\n\n{problem}", "Reinstall emulator");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[EmulatorSetup] Reinstalling the emulator for {platform.PlayniteName} failed.");
            }
        }

        internal void UninstallPlatform(RomMPlatform platform)
        {
            var dialogs = Playnite.Dialogs;
            try
            {
                EmulatorMapping mapping = MappingForPlatform(platform.Id);
                if (mapping == null)
                    return;
                Emulator emulator = mapping.Emulator;

                List<Game> games = Playnite.Database.Games
                    .Where(g => g.PluginId == Id && KnownPlatformOf(g)?.Id == platform.Id)
                    .ToList();
                if (games.Any(g => g.IsInstalling || g.IsUninstalling || g.IsLaunching || g.IsRunning))
                {
                    dialogs.ShowMessage($"A {platform.PlayniteName} game is running, installing or uninstalling. Wait for it to finish, then try again.",
                        "Uninstall platform", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                List<Game> installed = games.Where(g => g.IsInstalled).ToList();

                // Other platforms on the same emulator (RetroArch, Dolphin's GameCube and Wii) keep it.
                List<string> sharing = (Settings?.Mappings ?? new System.Collections.ObjectModel.ObservableCollection<EmulatorMapping>())
                    .Where(m => m != mapping && m.Enabled && emulator != null && m.Emulator?.Id == emulator.Id && m.RomMPlatformId != platform.Id)
                    .Select(m => Settings.RomMPlatforms?.FirstOrDefault(p => p.Id == m.RomMPlatformId)?.PlayniteName ?? "another platform")
                    .Distinct()
                    .ToList();
                string emulatorDir = emulator == null ? null : PlaynitePath.Resolve(Playnite, emulator.InstallDir);
                string ours = Path.Combine(Playnite.Paths.ApplicationPath, "Emulators");
                bool deleteFolder = emulator != null && sharing.Count == 0 && !string.IsNullOrEmpty(emulatorDir)
                                    && Directory.Exists(emulatorDir) && RomMInstallPaths.IsInside(ours, emulatorDir);

                var what = new List<string>();
                what.Add(installed.Count == 0
                    ? $"\u2022 No {platform.PlayniteName} games are installed."
                    : $"\u2022 {installed.Count} installed game{(installed.Count == 1 ? " is" : "s are")} uninstalled (their files deleted).");
                if (emulator != null)
                {
                    if (sharing.Count > 0)
                        what.Add($"\u2022 {emulator.Name} stays: {string.Join(", ", sharing)} still use{(sharing.Count == 1 ? "s" : "")} it.");
                    else if (deleteFolder)
                        what.Add($"\u2022 {emulator.Name} is removed, its folder sent to the Recycle Bin (with any saves and settings kept in it).");
                    else
                        what.Add($"\u2022 {emulator.Name} is removed from Playnite (its files, not set up here, are left where they are).");
                }
                what.Add($"\u2022 The games stay in your library; installing one sets {platform.PlayniteName} up again.");
                string note = installed.Count > 0 ? "\n\nSaves already synced to your RomM server stay there." : "";
                if (dialogs.ShowMessage($"Uninstall {platform.PlayniteName} from this computer?\n\n{string.Join("\n", what)}{note}",
                        "Uninstall platform", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                foreach (Game game in installed)
                {
                    try
                    {
                        new RomMUninstallController(game, this, mapping).Uninstall(new UninstallActionArgs());
                        Game current = Playnite.Database.Games.Get(game.Id);
                        if (current != null)
                        {
                            current.IsInstalled = false;
                            current.Roms?.Clear();
                            Playnite.Database.Games.Update(current);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Uninstall platform] Could not uninstall {game.Name}: {ex.Message}");
                    }
                }

                Settings.Mappings.Remove(mapping);
                Settings.Persist();

                if (emulator != null && sharing.Count == 0)
                {
                    if (deleteFolder)
                    {
                        try
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(emulatorDir,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[Uninstall platform] Could not remove {emulatorDir}: {ex.Message}");
                        }
                    }
                    if (Playnite.Database.Emulators.Get(emulator.Id) != null)
                        Playnite.Database.Emulators.Remove(emulator.Id);
                }
                Logger.Info($"[Uninstall platform] {platform.PlayniteName}: {installed.Count} game(s) uninstalled, mapping removed" +
                            (emulator == null ? "." : sharing.Count > 0 ? $"; {emulator.Name} kept for {string.Join(", ", sharing)}." : $"; {emulator.Name} removed."));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[Uninstall platform] {platform?.PlayniteName}");
                dialogs.ShowErrorMessage($"{platform?.PlayniteName} could not be uninstalled.\n\n{ex.Message}", "RomMnite");
            }
        }

        // The RomM platform a game was filed under: by its Playnite platform's name, else the server's word.
        private RomMPlatform PlatformOf(Game game)
        {
            var names = new HashSet<string>((game.Platforms ?? new List<Platform>()).Select(p => p.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            RomMPlatform known = Settings?.RomMPlatforms?.FirstOrDefault(p => names.Contains(p.PlayniteName ?? ""));
            if (known != null)
                return known;
            if (!RomMGameId.TryParse(game.GameId, out int romId, out string _))
                return null;
            RomMRom rom = FetchRom(romId.ToString());
            return rom == null ? null : Settings?.RomMPlatforms?.FirstOrDefault(p => p.Id == rom.PlatformId);
        }

        /// <summary>
        /// Gives every game on the mapping's platform that is waiting for an emulator the mapping:
        /// those filed with no mapping (its play action, unless they have one, and their record),
        /// and those whose play action names an emulator no longer on this computer (pointed at
        /// the mapping's).
        /// </summary>
        internal void AdoptWaitingGames(EmulatorMapping mapping)
        {
            string name = mapping?.RomMPlatform?.PlayniteName;
            if (string.IsNullOrEmpty(name) || mapping.Emulator == null)
                return;
            List<Game> onPlatform = Playnite.Database.Games
                .Where(g => g.PluginId == Id && g.Platforms?.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
                .ToList();
            foreach (Game game in onPlatform)
            {
                try
                {
                    RomMRomLocal record = LoadGameData(game);
                    if (record == null || !RomMGameId.TryParse(game.GameId, out int _, out string sha1))
                        continue;
                    bool unfiled = Settings.MappingById(record.MappingID) == null;
                    GameAction action = RomMPlayAction.Find(game.GameActions);
                    bool stale = action != null && action.EmulatorId != mapping.EmulatorId
                                 && EmulatorSetup?.IsPresent(Playnite.Database.Emulators.Get(action.EmulatorId)) == false;
                    if (!unfiled && !stale)
                        continue;

                    if (action == null || stale)
                    {
                        if (action == null)
                            RomMPlayAction.AddTo(game, mapping.Emulator.Name, mapping.EmulatorId, mapping.EmulatorProfileId);
                        else
                            RomMPlayAction.Apply(action, mapping.Emulator.Name, mapping.EmulatorId, mapping.EmulatorProfileId);
                        record.AppliedEmulatorID = mapping.EmulatorId;
                        record.AppliedEmulatorProfileID = mapping.EmulatorProfileId;
                        SuppressSync(game.Id);
                        Playnite.Database.Games.Update(game);
                    }
                    record.MappingID = mapping.MappingId;
                    RomMGameData.Save(ROMDataPath, sha1, record);
                    Logger.Info($"[Library] {game.Name} now plays in {mapping.Emulator.Name} ({mapping.MappingName}).");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Library] Could not give {game.Name} the {mapping.MappingName} mapping: {ex.Message}");
                }
            }
        }

        #endregion

        #region Account

        // Every account badge a theme has placed; they are created before the settings exist.
        private readonly List<Account.AccountBadgeControl> _accountBadges = new List<Account.AccountBadgeControl>();

        /// <summary>The accounts that have signed in on this computer.</summary>
        public Account.AccountCache Accounts { get; }

        /// <summary>Which RomM games each account has; switched with the account.</summary>
        public Account.AccountLibraries Libraries { get; }

        /// <summary>
        /// Whether a game is in the signed-in account's library: everything Playnite has,
        /// except RomM games hidden because this account does not have them.
        /// </summary>
        /// <summary>Copies of one game on several platforms (Games/LibraryVersions).</summary>
        internal LibraryVersions Versions { get; private set; }

        public bool InAccountLibrary(Game game) => game != null && !Libraries.HiddenForAccount(game.Id);

        private void SignIn() => RequireSignIn(null, null);

        /// <summary>
        /// At start-up: the remembered account signs in by itself and is checked with the server
        /// in the background (out of reach: it stays signed in, offline; refused: the sign-in
        /// window). Without one, the sign-in window. A sign-in kept in the settings from before
        /// accounts were saved becomes a remembered account, so nobody is asked again.
        /// </summary>
        private void StartSignIn()
        {
            try
            {
                if (!Accounts.Exists && Settings != null && !Account.SignInWindow.IsNeeded(Settings))
                {
                    bool key = !Settings.UseBasicAuth;
                    string host = Settings.RomMHost.Trim().TrimEnd('/');
                    string name = key ? "API key" : Settings.RomMUsername;
                    Accounts.Remember(host, name, key, key ? Settings.RomMClientToken : Settings.RomMPassword, true);
                    Accounts.SetDisplayName(host, name, Settings.RomMUser);
                }

                Account.CachedAccount auto = Accounts.AutoSignIn();
                string secret = auto != null ? Accounts.SecretOf(auto) : null;
                if (auto == null || string.IsNullOrEmpty(secret))
                {
                    RequireSignIn(null, null);
                    return;
                }

                ApplySignIn(auto.Host, auto.Username, auto.UsesKey, secret);
                Accounts.Touch(auto, false);
                Task.Run(() =>
                {
                    Account.LoginWindow.Answer answer = Account.LoginWindow.Check(auto.Host,
                        Account.LoginWindow.AuthFor(auto.UsesKey, auto.Username, secret), out string problem, out Account.ServerProfile profile);
                    if (answer == Account.LoginWindow.Answer.Accepted)
                    {
                        Accounts.Touch(auto, true);
                        Accounts.SetProfile(auto.Host, auto.Username, profile);
                        return;
                    }
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (answer == Account.LoginWindow.Answer.Rejected)
                        {
                            RequireSignIn("The server no longer accepts the saved sign-in for " + auto.Label + ". Sign in again.", auto);
                        }
                        else if (answer == Account.LoginWindow.Answer.Unreachable)
                        {
                            Playnite.Notifications.Add("romm-offline",
                                $"Couldn't reach the RomM server: signed in offline as {auto.Label}. The Store, downloads and cloud saves need the server.",
                                NotificationType.Info);
                        }
                        else
                        {
                            Logger.Warn($"[Account] The server could not check the saved sign-in: {problem}");
                        }
                    }));
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Account] Signing in at start-up failed.");
                RequireSignIn(null, null);
            }
        }

        // Puts a sign-in into effect: the settings, and the plugin's connection.
        private void ApplySignIn(string host, string username, bool usesKey, string secret)
        {
            // This account's RomM games in the library, the last account's out of it, and its
            // own collections and shelves.
            string accountKey = Account.AccountLibraries.KeyOf(host, username);
            CurrentAccountKey = accountKey;
            try
            {
                string leaving = Libraries.Switch(Playnite, Id, accountKey);
                if (leaving != null)
                {
                    // The leaving account's favorites, statuses, play time and notes kept, the
                    // arriving account's put on the games.
                    string data = Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString());
                    Account.AccountGameData.Swap(Playnite, Id,
                        Path.Combine(AccountFiles.Folder(data, leaving), "GameData.json"),
                        Path.Combine(AccountFiles.Folder(data, accountKey), "GameData.json"),
                        SuppressSync);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Accounts] Could not switch the library: {ex.Message}");
            }
            try
            {
                UseAccountFiles(accountKey);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Accounts] Could not switch to the account's collections: {ex.Message}");
            }

            bool changed = !string.Equals(Settings.RomMHost?.TrimEnd('/'), host, StringComparison.OrdinalIgnoreCase)
                || Settings.UseBasicAuth == usesKey
                || (usesKey ? Settings.RomMClientToken != secret : (Settings.RomMUsername != username || Settings.RomMPassword != secret));
            Settings.RomMHost = host;
            Settings.UseBasicAuth = !usesKey;
            if (usesKey)
            {
                Settings.RomMClientToken = secret;
                HttpClientSingleton.ConfigureAPIAuth(secret);
            }
            else
            {
                Settings.RomMUsername = username;
                Settings.RomMPassword = secret;
                HttpClientSingleton.ConfigureBasicAuth(username, secret);
            }
            if (changed)
                Settings.Persist();
            foreach (var badge in _accountBadges)
                badge.Refresh();

            // With the server's credentials in place: sync now (offline, it waits for the next).
            // The window stays covered, the logo turning, until that first sync is done, so the
            // account's library is not seen half-switched.
            _syncSignedIn = true;
            Interlocked.Increment(ref _curtainSyncs);
            Account.SignedOutCurtain.BeginBusy("Syncing your library...");
            ScheduleSync(TimeSpan.FromSeconds(1));
            _rommUserId = -1;
            RefreshRetroAchievementsProfile();

            // A new account starts with nothing to go back to: whatever was recorded while
            // signed out (or the start-up's first pages, before the account was known) is not its.
            History.Clear();

            // Someone is signed in: the window shows the library again (once synced).
            Account.SignedOutCurtain.Hide();
        }

        /// <summary>
        /// The sign-in window, until someone signs in; closing it quits Playnite, as Steam's does.
        /// </summary>
        private void RequireSignIn(string message, Account.CachedAccount prefill)
        {
            Account.SignedOutCurtain.Show();
            Account.LoginResult result = Account.LoginWindow.Show(Accounts, message, prefill, Settings?.RomMHost);
            if (result == null)
            {
                Logger.Info("[Account] The sign-in window was closed: quitting.");
                System.Windows.Application.Current?.Shutdown();
                return;
            }

            ApplySignIn(result.Host, result.Username, result.UsesKey, result.Secret);
            if (result.Verified)
            {
                Accounts.Remember(result.Host, result.Username, result.UsesKey, result.Secret, result.Remember);
                Accounts.SetProfile(result.Host, result.Username, result.Profile);
            }
            else if (result.Account != null)
                Accounts.Touch(result.Account, false);

            if (!result.Verified)
            {
                Playnite.Notifications.Add("romm-offline",
                    "Couldn't reach the RomM server: signed in offline. The Store, downloads and cloud saves need the server.",
                    NotificationType.Info);
                return;
            }

            // Fetch the name and picture for the top bar; the saved account takes the name too.
            string host = result.Host;
            string name = result.Username;
            Task.Run(() =>
            {
                try
                {
                    if (Settings.TestConnection(false, true))
                        Settings.Persist();
                    Accounts.SetDisplayName(host, name, Settings.RomMUser);
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        foreach (var badge in _accountBadges)
                            badge.Refresh();
                    }));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Account] Could not fetch the profile after signing in: {ex.Message}");
                }
            });
        }

        private void SignOut()
        {
            var answer = Playnite.Dialogs.ShowMessage(
                "Sign out of RomM on this computer? Your games stay in the library, but downloads, cloud saves and " +
                "library updates stop until someone signs in again.",
                "Sign out", System.Windows.MessageBoxButton.YesNo);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return;

            // Signed out, the account no longer signs in by itself (its tile stays, as in Steam).
            try
            {
                bool key = !Settings.UseBasicAuth;
                Accounts.Forget(Settings.RomMHost?.Trim().TrimEnd('/'), key ? "API key" : Settings.RomMUsername, false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Account] Could not update the saved accounts on signing out: {ex.Message}");
            }

            try
            {
                _syncSignedIn = false;
                _syncTimer?.Stop();
                CurrentAccountKey = null;
                Settings.RomMClientToken = "";
                Settings.RomMUsername = "";
                Settings.RomMPassword = "";
                Settings.RomMUser = "";
                // The device registration belongs to the account that made it.
                Settings.SaveSyncDeviceId = "";
                HttpClientSingleton.ClearAuth();
                Settings.Persist();

                string avatar = Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "avatar.png");
                if (File.Exists(avatar))
                    File.Delete(avatar);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[Account] Signing out did not complete.");
            }

            foreach (var badge in _accountBadges)
                badge.Refresh();

            // Nothing of this account's browsing is left for the next: the library at Home, the
            // Store reset, and back and forward emptied (else the arrows lead into its pages).
            try
            {
                CloudStatus.StoreVisible = false;
                ShowLibraryPage("Home");
                CloudStatus.LeaveAccount();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Account] Could not reset the pages on signing out: {ex.Message}");
            }
            History.Clear();

            // Signing in is required: back to the sign-in window, over an empty window.
            Account.SignedOutCurtain.Show();
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() => RequireSignIn(null, null)));
        }

        #endregion

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            if (DownloadsSidebar != null)
            {
                yield return DownloadsSidebar;
            }
        }
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            List<GameMenuItem> gameMenuItems = new List<GameMenuItem>();

            // The user's collections, for any game (Playnite's own views too): each toggles the
            // chosen games in or out (ticked when they are all in it), then a new one.
            var chosen = args.Games.Select(g => g.Id).ToList();
            foreach (GameCollection collection in Collections.All())
            {
                Guid collectionId = collection.Id;
                bool all = chosen.All(collection.Games.Contains);
                gameMenuItems.Add(new GameMenuItem
                {
                    MenuSection = GameCollections.MenuSection,
                    Description = (all ? "\u2713 " : "") + collection.Name,
                    Action = (_) => Collections.Toggle(collectionId, chosen),
                });
            }
            if (Collections.All().Count > 0)
                gameMenuItems.Add(new GameMenuItem { MenuSection = GameCollections.MenuSection, Description = "-" });
            gameMenuItems.Add(new GameMenuItem
            {
                MenuSection = GameCollections.MenuSection,
                Description = "New collection...",
                Action = (_) => Collections.CreateWithPrompt(Playnite, chosen),
            });

            var game = args.Games.First();
            if (game.PluginId == PluginId && RomMGameId.TryParse(game.GameId, out int _, out var sha1))
            {
                // Out of this account's library: uninstalled first, unless another account has
                // it. With this, Playnite's own Remove is hidden (OnAnyContextMenuOpened).
                if (args.Games.All(g => g.PluginId == PluginId && InAccountLibrary(g)))
                {
                    List<Game> removing = args.Games.ToList();
                    gameMenuItems.Add(new GameMenuItem
                    {
                        Description = RemoveMenuItem,
                        MenuSection = LibraryHome.LibraryHomeView.ManageHeader,
                        Action = (_) => RemoveFromAccountLibrary(removing),
                    });
                }

                // The game's whole platform off this computer: its games and its emulator.
                RomMPlatform menuPlatform = KnownPlatformOf(game);

                // Its emulator downloaded again over itself (settings and saves kept), when it is
                // one the emulator setup knows, it is installed, and it is what the platform plays
                // in (not an emulator of the user's own mapped to it).
                EmulatorMapping menuMapping = menuPlatform != null ? MappingForPlatform(menuPlatform.Id) : null;
                Setup.EmulatorSetup.Plan reinstall = menuMapping != null ? EmulatorSetup?.PlanFor(menuPlatform.Id) : null;
                if (reinstall?.Existing != null && reinstall.Existing.Id == menuMapping.EmulatorId)
                {
                    gameMenuItems.Add(new GameMenuItem
                    {
                        Description = $"Reinstall emulator ({reinstall.EmulatorName})...",
                        MenuSection = LibraryHome.LibraryHomeView.ManageHeader,
                        Action = (_) => ReinstallEmulator(menuPlatform),
                    });
                }
                if (menuPlatform != null && MappingForPlatform(menuPlatform.Id) != null)
                {
                    gameMenuItems.Add(new GameMenuItem
                    {
                        Description = $"Uninstall platform ({menuPlatform.PlayniteName})...",
                        MenuSection = LibraryHome.LibraryHomeView.ManageHeader,
                        Action = (_) => UninstallPlatform(menuPlatform),
                    });
                }

                // Cloud saves, on unless switched off here (ticked when on for all the chosen games).
                List<Game> ours = args.Games.Where(g => g.PluginId == PluginId).ToList();
                bool savesOn = ours.All(SaveSyncOff.IsOn);
                gameMenuItems.Add(new GameMenuItem
                {
                    Description = (savesOn ? "\u2713 " : "") + "Cloud saves",
                    MenuSection = LibraryHome.LibraryHomeView.SyncSection,
                    Action = (_) => SaveSyncOff.Set(ours, !savesOn),
                });
                if (savesOn)
                {
                    gameMenuItems.Add(new GameMenuItem
                    {
                        Description = "Sync saves with RomM",
                        MenuSection = LibraryHome.LibraryHomeView.SyncSection,
                        Action = (_) => SyncSavesWithNotification(ours)
                    });
                }

                if (SaveSyncOff.IsOn(game) && SaveSync.UsesLudusavi(game))
                {
                    gameMenuItems.Add(new GameMenuItem
                    {
                        Description = "Set Ludusavi name...",
                        MenuSection = LibraryHome.LibraryHomeView.SyncSection,
                        Action = (_) => PromptLudusaviTitle(game)
                    });
                }

                gameMenuItems.Add(new GameMenuItem
                {
                    Description = "Refresh achievements",
                    MenuSection = LibraryHome.LibraryHomeView.SyncSection,
                    Action = (_) =>
                    {
                        foreach (var picked in args.Games.Where(x => x.PluginId == PluginId))
                            RefreshAchievements(picked, true);
                    }
                });

                if (Settings.MergeRevisions && game.IsInstalled)
                {
                    var gameData = RomMGameData.LoadBySha1(ROMDataPath, sha1, Logger, out _, game.Name);
                    if (gameData?.ROMVersions?.Count > 1)
                    {
                        gameMenuItems.Add(new GameMenuItem
                        {
                            //MenuSection = "@",
                            Description = "Switch ROM Version!",
                            Action = (gameMenuItem) =>
                            {
                                Playnite.InstallGame(args.Games.First().Id);
                            }
                        });
                    }
                }
            }
            return gameMenuItems;
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                string gameID = args.Game.GameId;
                GameInstallInfo romData = new GameInstallInfo();
                RomMRomLocal gameData = new RomMRomLocal();

                if (gameID == null || !gameID.Contains(':') || gameID.StartsWith("!0"))
                {
                    PlayniteApi.Notifications.Add(new NotificationMessage(PluginId.ToString(), "Old ID detected run update game library before installing!", NotificationType.Error));
                    romData.Id = (int)InstallStatus.Cancelled;
                    yield return new RomMInstallController(args.Game, this, romData);
                    yield break;
                }
                else
                {
                    // Pull game file from RomM data directory
                    if (!RomMGameId.TryParse(gameID, out int _, out string romMSHA1))
                    {
                        Logger.Error($"{args.Game.Name} GameID is malformed!");
                        romData.Id = (int)InstallStatus.Cancelled;
                        yield return new RomMInstallController(args.Game, this, romData);
                        yield break;
                    }

                    gameData = RomMGameData.LoadBySha1(ROMDataPath, romMSHA1, Logger, out _, args.Game.Name);
                    if (gameData?.ROMVersions == null || gameData.ROMVersions.Count == 0)
                    {
                        Logger.Error($"{args.Game.Name} has no usable ROM data file; run update game library before installing!");
                        romData.Id = (int)InstallStatus.Cancelled;
                        yield return new RomMInstallController(args.Game, this, romData);
                        yield break;
                    }

                    // Added before anything played its platform, or its emulator is gone: an
                    // emulator is set up first -- unless it is downloaded without one (the Store's
                    // "without an emulator"): then it goes where its platform's games go, by the
                    // mapping it has, else the one an emulator set up later will have (not saved).
                    EmulatorMapping mapping = Settings.MappingById(gameData.MappingID);
                    if (mapping == null || NeedsEmulator(args.Game))
                    {
                        if (_installWithoutEmulator.ContainsKey(args.Game.Id))
                        {
                            if (mapping == null)
                            {
                                RomMPlatform platform = PlatformOf(args.Game);
                                if (platform != null && EmulatorSetup != null)
                                {
                                    mapping = EmulatorSetup.NewMapping(platform);
                                    mapping.MappingId = Guid.Empty;
                                    mapping.Enabled = false;
                                }
                            }
                            if (mapping == null)
                            {
                                Playnite.Dialogs.ShowErrorMessage($"{args.Game.Name} could not be downloaded: which RomM platform it is on could not be worked out.", "RomMnite");
                                romData.Id = (int)InstallStatus.Cancelled;
                                yield return new RomMInstallController(args.Game, this, romData);
                                yield break;
                            }
                            Logger.Info($"[Install] {args.Game.Name} is downloaded without an emulator, to {mapping.DestinationPath}.");
                        }
                        else
                        {
                            if (SetUpPlatformFor(args.Game) == null)
                            {
                                romData.Id = (int)InstallStatus.Cancelled;
                                yield return new RomMInstallController(args.Game, this, romData);
                                yield break;
                            }
                            gameData = RomMGameData.LoadBySha1(ROMDataPath, romMSHA1, Logger, out _, args.Game.Name) ?? gameData;
                            mapping = Settings.MappingById(gameData.MappingID);
                        }
                    }

                    // Set ROM data to base ROM
                    romData = GameInstallInfo.From(gameData.ROMVersions[0], mapping);

                    // If Siblings are available prompt user with version selection
                    if (Settings.MergeRevisions && gameData.ROMVersions?.Count > 1)
                    {

                        RomMVersionSelector VersionSelectorControl = new RomMVersionSelector(gameData.ROMVersions);
                        var window = Playnite.Dialogs.CreateWindow(new WindowCreationOptions
                        {
                            ShowMinimizeButton = false,
                            ShowMaximizeButton = false,
                            ShowCloseButton = false,
                        });

                        window.Height = 215;
                        window.Width = 600;

                        window.Title = "Select Version to install!";
                        window.ShowInTaskbar = false;
                        window.ResizeMode = ResizeMode.NoResize;
                        window.Owner = API.Instance.Dialogs.GetCurrentAppWindow();
                        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                        window.Content = VersionSelectorControl;

                        window.ShowDialog();

                        if (VersionSelectorControl.Cancelled)
                        {
                            romData.Id = (int)InstallStatus.Cancelled;
                        }
                        else
                        {
                            // Uninstall old ROM before installing new one
                            if (args.Game.IsInstalled)
                            {
                                Playnite.UninstallGame(args.Game.Id);

                                args.Game.IsInstalling = true;
                                Playnite.Database.Games.Update(args.Game);
                            }


                            var selectedrevision = VersionSelectorControl.RomVersions.First(x => x.IsSelected);
                            romData = GameInstallInfo.From(selectedrevision, romData.Mapping);
                            
                            gameData.ROMVersions = VersionSelectorControl.RomVersions.ToList();

                        }
                    }
                    else
                    {
                        gameData.ROMVersions[0].IsSelected = true;
                    }

                    RomMGameData.Save(ROMDataPath, romMSHA1, gameData);
                }

                // Where it goes: chosen in the Store's install dialog already, else asked here with
                // the same dialog (an install from the library, Home, or Playnite's own button).
                string installRoot = null;
                if (romData.Id != (int)InstallStatus.Cancelled && romData.Mapping != null
                    && !_installRoots.TryGetValue(args.Game.Id, out installRoot))
                {
                    if (!global::RomM.Store.InstallDialog.Show(this, args.Game, romData.Mapping, out installRoot))
                        romData.Id = (int)InstallStatus.Cancelled;
                }

                // Installed to another install location: the same layout under that location's
                // folder for the platform.
                if (romData.Id != (int)InstallStatus.Cancelled && romData.Mapping != null && !string.IsNullOrEmpty(installRoot))
                {
                    string platformFolder = Path.GetFileName((romData.Mapping.DestinationPath ?? "").TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(platformFolder))
                    {
                        romData.InstallRoot = Path.Combine(installRoot, platformFolder);
                        Logger.Info($"[Install] {args.Game.Name} goes to {romData.InstallRoot}.");
                    }
                }

                yield return new RomMInstallController(args.Game, this, romData);
            }
        }
        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                EmulatorMapping mapping = MappingFor(args.Game);

                // One way to uninstall, always: two (a null one too, as there was when the game had
                // no mapping) made Playnite ask which, with a blank choice. A game with no mapping
                // (its platform not set up, downloaded without an emulator) is uninstalled from
                // where such a game goes: the folder its platform's mapping would have.
                if (mapping == null && EmulatorSetup != null)
                {
                    RomMPlatform platform = KnownPlatformOf(args.Game);
                    if (platform != null)
                        mapping = EmulatorSetup.NewMapping(platform);
                }
                yield return new RomMUninstallController(args.Game, this, mapping);
            }
        }
        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            base.OnGameInstalled(args);

            if (args.Game.PluginId == PluginId && Settings.NotifyOnInstallComplete)
            {
                Playnite.Notifications.Add(args.Game.GameId, $"Download of \"{args.Game.Name}\" is complete", NotificationType.Info);
            }
        }

        // Playnite blocks the launch until OnGameStarting returns, so the sync runs under a deadline
        // the requests themselves honour: an unreachable RomM aborts the cycle instead of costing
        // the HTTP client's default timeout per request, and nothing is left running into gameplay
        // where it could write over the save the emulator has already loaded.
        private static readonly TimeSpan PreLaunchSyncDeadline = TimeSpan.FromSeconds(20);

        // Pull the newest save down before the emulator launches so the player continues from the
        // latest device. Any failure is swallowed (logged inside Sync) rather than preventing the
        // game from starting.
        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            base.OnGameStarting(args);

            if (args.Game.PluginId != PluginId)
                return;

            var game = args.Game;

            // Its emulator is not on this computer (removed, or never installed here): it is
            // offered for installing instead, and the game starts once it is in place.
            if (NeedsEmulator(game))
            {
                Guid gameId = game.Id;
                MarkLaunching(gameId);
                _settingUpFor[gameId] = 0;
                CancelStartup(args, game);
                Playnite.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    bool ready = false;
                    try
                    {
                        Game waiting = Playnite.Database.Games.Get(gameId);
                        ready = waiting != null && SetUpPlatformFor(waiting) != null;
                    }
                    finally
                    {
                        _settingUpFor.TryRemove(gameId, out byte _);
                    }
                    if (ready)
                        Playnite.StartGame(gameId);
                    else
                        ClearLaunching(gameId);
                }));
                return;
            }

            // Downloaded, but still the archive it came as (its unpacking switched off, cut short
            // or failed): unpacked first, as the download would have done, then started.
            if (PendingArchiveOf(game, out string _, out bool _) != null)
            {
                Guid gameId = game.Id;
                MarkLaunching(gameId);
                CancelStartup(args, game);
                Playnite.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    bool ready = false;
                    try
                    {
                        ready = UnpackForPlay(gameId);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"[Unpack] Could not unpack {game.Name}.");
                    }
                    if (ready)
                        Playnite.StartGame(gameId);
                    else
                        ClearLaunching(gameId);
                }));
                return;
            }

            // The signed-in account's own save on disk first, whoever played last; without it the
            // game does not start (it would play, then sync, someone else's save).
            // In emulators with users of their own (RPCS3, Xenia), the account's own user.
            Account.CachedAccount current = Accounts.All().FirstOrDefault(a => Account.AccountLibraries.KeyOf(a.Host, a.Username) == CurrentAccountKey);
            string label = current?.Label;
            string loginName = current != null && !current.UsesKey ? current.Username : null;
            if (!EmulatorUsers.Prepare(game, label, loginName) || !SaveAccounts.Prepare(game, CurrentAccountKey))
            {
                CancelStartup(args, game);
                return;
            }

            // Fullscreen, closing with the game, no confirmations (Setup.EmulatorLaunch).
            try
            {
                SaveEmulatorResolution launching = SaveSync.EmulatorFor(game);
                if (launching?.Emulator != null)
                {
                    // The emulator's own menu on Start + Select, unless the overlay is on those
                    // buttons itself (Back + Start), which would open both.
                    // The button the overlay opens with this time: Start + Select when Steam, the
                    // Xbox Game Bar or Playnite has the Home button (Overlay.GameOverlay).
                    string overlayButton = Overlay.GameOverlay.EffectiveButton(Settings?.OverlayButton ?? "Guide", Playnite);
                    bool menus = Settings?.EmulatorMenusOnStartSelect != false && overlayButton != "BackStart";
                    Setup.EmulatorLaunch.PrepareSettings(launching.Emulator, PlaynitePath.Resolve(Playnite, launching.Emulator.InstallDir),
                        menus, overlayButton == "Guide", overlayButton == "BackStart",
                        Settings?.EmulatorsAtDisplayResolution != false, Path.Combine(GetPluginUserDataPath(), "EmulatorUpscale.txt"));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EmulatorLaunch] Could not prepare {game.Name}'s emulator: {ex.Message}");
            }

            // Its emulator signed in to RetroAchievements as this account (or out); and the
            // session noted, so the trophies it unlocks are known to be this account's.
            PrepareRetroAchievements(game);
            EmulatorAchievements.SessionStarted(game);

            var deadline = new CancellationTokenSource(PreLaunchSyncDeadline);

            // The deadline cancels the requests themselves, so the task ends on its own; the grace
            // on Wait is only a backstop for a blocking filesystem call, which no token can
            // interrupt. Disposal rides on the task rather than on this scope, so a task that did
            // outlive the wait is not left holding a disposed source.
            var sync = Task.Run(() => SaveSync.Sync(game, deadline.Token));
            sync.ContinueWith(_ => deadline.Dispose());

            if (!sync.Wait(PreLaunchSyncDeadline + TimeSpan.FromSeconds(5)))
            {
                Logger.Warn($"[SaveSync] Pre-launch sync for \"{game.Name}\" did not stop at its deadline; " +
                            "starting the game anyway.");
            }

            // After the saves are in (they may bring Xenia's record of the game): unlocks earned
            // elsewhere put into it, so Xenia keeps their pictures.
            EmulatorAchievements.BeforeLaunch(game);

            // From here on the emulator owns the save: note when the session began and upload new
            // saves as the game writes them.
            SaveSync.BeginSession(game);
        }

        // The selected game's achievement count and bar, or the note that it must be played once.
        private void ShowAchievementsSummary(Guid gameId)
        {
            AchievementViews.GameAchievements data = gameId != Guid.Empty ? AchievementStore.Get(gameId) : null;
            bool firstPlay = false;
            if (gameId != Guid.Empty && (data == null || data.Total == 0))
            {
                Game game = Playnite.Database.Games.Get(gameId);
                firstPlay = game != null && EmulatorAchievements.NeedsFirstPlay(game, out string _);
            }
            CloudStatus.Achievements.Show(data, firstPlay);
            ShowAchievementSync(gameId);
        }

        // The selected game's achievement sync status: shown only for games with achievements
        // (or that must be played once to have them). A sync result alone does not show it: a
        // game without achievements can still have a failed request on record.
        private void ShowAchievementSync(Guid gameId)
        {
            bool applies = gameId != Guid.Empty
                && (CloudStatus.Achievements.HasData || CloudStatus.Achievements.NeedsFirstPlay);
            if (!applies)
            {
                CloudStatus.AchievementSync.Visible = false;
                return;
            }
            AchievementSync.Describe(gameId, out string headline, out string detail);
            CloudStatus.AchievementSync.Text = headline;
            CloudStatus.AchievementSync.Detail = detail;
            CloudStatus.AchievementSync.Visible = true;
        }

        // CancelStartup is looked up rather than named: not every SDK version has it.
        // A game's file as a real path (Playnite's variables and the portable folder resolved).
        private string RealPath(Game game, string path)
        {
            try
            {
                return PlaynitePath.Resolve(Playnite, Playnite.ExpandGameVariables(game, path ?? ""));
            }
            catch
            {
                return path;
            }
        }

        private static bool SameFolder(string a, string b)
        {
            try
            {
                return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                       && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The archive a RomM game was downloaded as and is still waiting to be unpacked from, or
        /// null: one of its files to start is an archive, and the download would have unpacked
        /// it -- always for a Windows game (an archive cannot be started) and for a folder
        /// download, else when its mapping unpacks downloads (AutoExtract). An archive the
        /// mapping keeps (an arcade .zip the emulator opens) is left alone, as is one sharing the
        /// platform's folder with other games (a flat layout), unless it is a Windows game's:
        /// that is unpacked into a folder of its own beside it.
        /// </summary>
        internal string PendingArchiveOf(Game game, out string unpackTo, out bool windows)
        {
            unpackTo = null;
            windows = false;
            if (game == null || game.PluginId != Id || game.Roms == null || game.Roms.Count == 0 || !game.IsInstalled)
                return null;
            string archive = game.Roms.Select(r => RealPath(game, r.Path)).FirstOrDefault(p => PendingUnpack.LooksLikeArchive(p) && File.Exists(p));
            if (archive == null)
                return null;

            EmulatorMapping mapping = MappingFor(game);
            RomMPlatform platform = mapping?.RomMPlatform != null && mapping.RomMPlatform.Id > 0 ? mapping.RomMPlatform : KnownPlatformOf(game);
            windows = Setup.EmulatorSetup.IsWindowsPlatform(platform);
            RomMRomLocal data = LoadGameData(game);
            RomMRevision revision = data?.ROMVersions?.FirstOrDefault(r => r.IsSelected) ?? data?.ROMVersions?.FirstOrDefault();
            bool folderDownload = revision?.DownloadAsArchive == true;
            if (!windows && !folderDownload && mapping?.AutoExtract != true)
                return null;

            // In the platform's own folder (the mapping's, or the same under another install location)?
            string folder = Path.GetDirectoryName(archive);
            string platformFolder = PlaynitePath.Resolve(Playnite, mapping?.DestinationPathResolved);
            string platformName = string.IsNullOrEmpty(platformFolder) ? null : Path.GetFileName(platformFolder.TrimEnd('\\', '/'));
            bool shared = SameFolder(folder, platformFolder)
                          || (platformName != null && (Locations?.All() ?? new List<string>()).Any(root => SameFolder(folder, Path.Combine(root, platformName))));
            if (shared)
            {
                if (!windows && !folderDownload)
                    return null;
                unpackTo = Path.Combine(folder, Path.GetFileNameWithoutExtension(archive));
            }
            else
            {
                unpackTo = folder;
            }
            return archive;
        }

        /// <summary>
        /// Unpacks a game still waiting as an archive (PendingArchiveOf), with Playnite's progress
        /// shown, and points it at what to start in it; the archive is deleted once that is found.
        /// Whether it is ready to start. On the UI thread.
        /// </summary>
        internal bool UnpackForPlay(Guid gameId)
        {
            Game game = Playnite.Database.Games.Get(gameId);
            string archive = PendingArchiveOf(game, out string unpackTo, out bool windows);
            if (archive == null)
                return game != null;

            string problem = null;
            List<string> launch = null;
            Logger.Info($"[Unpack] {game.Name} is still {archive}; unpacking it into {unpackTo} before it starts.");
            Playnite.Dialogs.ActivateGlobalProgress(progress =>
            {
                try
                {
                    progress.ProgressMaxValue = 100;
                    PendingUnpack.Extract(archive, unpackTo, Settings?.Use7z == true ? Settings.PathTo7z : null,
                        done => progress.CurrentProgressValue = done * 100, progress.CancelToken);
                    launch = LaunchFilesIn(game, unpackTo, windows);
                }
                catch (OperationCanceledException)
                {
                    problem = "Cancelled.";
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"[Unpack] Unpacking {game.Name} failed.");
                    problem = ex.Message;
                }
            }, new GlobalProgressOptions($"Unpacking {game.Name}...", true) { IsIndeterminate = false });

            if (problem != null)
            {
                if (problem != "Cancelled.")
                    Playnite.Dialogs.ShowErrorMessage($"{game.Name} was downloaded but not unpacked, and unpacking it now failed.\n\n{problem}", "RomMnite");
                return false;
            }
            if (launch == null || launch.Count == 0)
            {
                // The archive is kept: nothing is lost, and the next try unpacks it again.
                Playnite.Dialogs.ShowErrorMessage($"{game.Name} was unpacked into {unpackTo}, but nothing in it to start was found.\n\n" +
                    "Choose the file to start in the game's Edit window (Installation, ROMs), or install it again.", "RomMnite");
                return false;
            }

            try
            {
                File.Delete(archive);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Unpack] Could not delete {archive} after unpacking it: {ex.Message}");
            }
            game = Playnite.Database.Games.Get(gameId);
            if (game == null)
                return false;
            game.Roms = new System.Collections.ObjectModel.ObservableCollection<GameRom>(launch.Select(f => new GameRom(game.Name, f)));
            // Unpacked into a folder of its own (beside others' in the platform's folder): that
            // folder is the game's, for uninstalling. Otherwise its folder stays what it was.
            if (!SameFolder(unpackTo, Path.GetDirectoryName(archive)))
                game.InstallDirectory = unpackTo;
            Playnite.Database.Games.Update(game);
            Logger.Info($"[Unpack] {game.Name} unpacked; starts from {string.Join(", ", launch.Select(Path.GetFileName))}.");
            return true;
        }

        // What to start of a game just unpacked into a folder, as an install would have found it.
        private List<string> LaunchFilesIn(Game game, string folder, bool windows)
        {
            EmulatorMapping mapping = MappingFor(game);
            List<string> types = RomMInstalledRoms.SupportedTypes(mapping);
            if (windows)
                return PendingUnpack.WindowsLaunchFiles(folder, types);

            RomMRomLocal data = LoadGameData(game);
            RomMRevision revision = data?.ROMVersions?.FirstOrDefault(r => r.IsSelected) ?? data?.ROMVersions?.FirstOrDefault();
            if (!string.IsNullOrEmpty(revision?.PlayableFile) && RomMInstallPaths.IsContained(revision.PlayableFile))
            {
                string primary = Path.Combine(folder, revision.PlayableFile);
                if (File.Exists(primary))
                    return new List<string> { primary };
            }
            bool m3u = types == null || types.Any(t => string.Equals(t, "m3u", StringComparison.OrdinalIgnoreCase));
            List<string> files = RomMInstalledRoms.Detect(null, folder, false, false, null, null, types, m3u);
            return LaunchFiles.Pick(files, m3u, folder, game.Name);
        }

        private void CancelStartup(OnGameStartingEventArgs args, Game game)
        {
            var cancel = args.GetType().GetProperty("CancelStartup");
            if (cancel != null && cancel.CanWrite)
                cancel.SetValue(args, true);
            else
                Logger.Warn($"Could not stop {game.Name} from starting.");
        }

        /// <summary>The selected game's cloud save status, bound to by themes.</summary>
        public CloudStatusModel CloudStatus { get; } = new CloudStatusModel();

        /// <summary>Which games have a video on the RomM server, and where.</summary>
        public RomMVideoStore Videos { get; }

        /// <summary>The address a game's video streams from, or null when it has none.</summary>
        public string VideoUrl(Guid gameId)
        {
            string path = Videos.Get(gameId);
            string host = Settings?.RomMHost;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(host))
                return null;

            return $"{host.TrimEnd('/')}/assets/romm/resources/{path}";
        }

        /// <summary>Passes server video to the media player through an address on this computer.</summary>
        public VideoRelay VideoRelay { get; } = new VideoRelay();

        private Guid _selectedGameId = Guid.Empty;
        private readonly DownloadStatusPresenter _downloadStatus;

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            // Steam's one button on the library's game page (Install Emulator, Install,
            // Downloading / Cancel, Play, Launching...), in place of Playnite's own.
            if (args?.Name == "ControllerStatus")
                return new Games.ControllerStatusControl(this);
            if (args?.Name == "ControllerStatusBar")
                return new Games.ControllerStatusControl(this, compact: true);

            if (args?.Name == "EmulationStatus")
                return new Games.EmulationStatusControl(this);

            if (args?.Name == "NewsFeed")
                return new Games.OverviewFeedControl(this);

            if (args?.Name == "PlayButton")
                return new Store.GameActionButton(this) { MinHeight = 48 };

            if (args?.Name == "LibraryFilters")
                return new LibraryHome.LibraryFiltersControl(Playnite, this);

            if (args?.Name == "StorePageLink")
                return new LibraryHome.StorePageButtonControl(this, true);

            if (args?.Name == "HltbProgressBar")
                return new HltbProgressBar(CloudStatus.Hltb);

            if (args?.Name == "DownloadStatus")
                return new DownloadStatusListControl(DownloadQueueController, g => Versions?.Others(g) ?? new List<Game>());

            if (args?.Name == "GameLogo")
                return new GameLogoControl(Playnite);

            if (args?.Name == "AccountBadge")
            {
                var badge = new Account.AccountBadgeControl(b =>
                {
                    if (b.SignedIn)
                        b.ShowMenu(SignOut);
                    else
                        SignIn();
                });
                _accountBadges.Add(badge);
                if (Settings != null)
                    badge.Attach(Settings);
                return badge;
            }

            if (args?.Name == "NavTabs")
            {
                Action toLibrary = () =>
                {
                    try { Playnite.MainView.SwitchToLibraryView(); } catch { }
                };
                return new Account.NavTabsControl(CloudStatus, History, toLibrary,
                    page =>
                    {
                        CloudStatus.StoreVisible = false;
                        toLibrary();
                        ShowLibraryPage(page);
                    });
            }

            if (args?.Name != null && args.Name.StartsWith("Achievements")
                && Enum.TryParse(args.Name.Substring("Achievements".Length), out AchievementViews.AchievementView kind))
                return new AchievementViews.AchievementControl(this, kind);

            if (args?.Name == "LibraryHome")
                return new LibraryHome.LibraryHomeView(Playnite, CloudStatus, DownloadQueueController, Sessions, AchievementStore, SteamAppIdAsync, News,
                    ServerAnniversariesAsync, romId => CloudStatus.OpenStoreGame(romId), Collections, Shelves,
                    HeroEmulatorOf, game => SetUpPlatformFor(game));

            if (args?.Name == "BottomBar")
            {
                return new Account.BottomBarControl(Playnite, CloudStatus, DownloadQueueController, page =>
                {
                    CloudStatus.StoreVisible = false;
                    try { Playnite.MainView.SwitchToLibraryView(); } catch { }
                    ShowLibraryPage(page);
                });
            }

            if (args?.Name == "StorePageButton")
                return new LibraryHome.StorePageButtonControl(this);

            if (args?.Name == "HomeButton")
                return new LibraryHome.HomeButtonControl(CloudStatus, ShowLibraryHome);

            if (args?.Name == "MainMenu")
                return new Account.MainMenuButtonControl();

            if (args?.Name == "Search")
                return new Account.BarSearchControl(CloudStatus);

            if (args?.Name == "Store")
                return new Store.StoreView(this);

            if (args?.Name == "GameVideo")
                return new GameVideoControl(this);

            if (args?.Name == "ManualViewer")
                return new Manuals.ManualViewerControl(Id, Path.Combine(Playnite.Paths.ExtensionsDataPath, Id.ToString(), "Manuals"));

            return base.GetGameViewControl(args);
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            base.OnGameSelected(args);

            var selected = args?.NewValue;

            // Picking a game leaves the Home page for that game's own page.
            if (selected != null && selected.Count > 0 && DateTime.UtcNow > _homeGuardUntil)
                CloudStatus.LibraryHomeVisible = false;
            else if (selected != null && selected.Count == 1)
                // Selected while a filter change settles: opened then if it is the only game left.
                ScheduleSingleResultCheck();
            _selectedGameId = selected != null && selected.Count == 1 ? selected[0].Id : Guid.Empty;
            // A game picked: its page's art and information load first, background work waits.
            Foreground.Touch(TimeSpan.FromSeconds(4));
            // Away from a copy picked from its button's drop-down: its game goes back to its usual copy.
            Versions?.Selected(_selectedGameId);
            if (_selectedGameId != Guid.Empty && !CloudStatus.StoreVisible && DateTime.UtcNow > _homeGuardUntil)
                History.Record(new Account.NavEntry { Store = false, Kind = "game", GameId = _selectedGameId });
            _downloadStatus?.Select(_selectedGameId);

            // A Manual tab is worth showing only for a RomM game whose manual is a PDF.
            var only = selected != null && selected.Count == 1 ? selected[0] : null;

            try
            {
                CloudStatus.LogoVisible = only != null && File.Exists(GameLogoControl.LogoPath(Playnite, only.Id));
            }
            catch
            {
                CloudStatus.LogoVisible = false;
            }
            CloudStatus.VideoVisible = only != null && VideoUrl(only.Id) != null;

            ShowAchievementsSummary(only?.Id ?? Guid.Empty);
            ShowHltb(only);
            RefreshAchievements(only);
            EmulatorAchievements.Refresh(only);
            ApplySteamDescription(only);

            string manual = only != null && only.PluginId == Id ? only.Manual : null;
            int query = string.IsNullOrEmpty(manual) ? -1 : manual.IndexOf('?');
            CloudStatus.ManualVisible = !string.IsNullOrEmpty(manual)
                && (query >= 0 ? manual.Substring(0, query) : manual).EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

            RefreshCloudStatus();
        }

        // Works out the selected game's cloud save status off the UI thread (it may read a small
        // file) and publishes it on Settings, where a theme can bind to it.
        private void RefreshCloudStatus()
        {
            Guid gameId = _selectedGameId;
            Task.Run(() =>
            {
                try
                {
                    // The sync service reads the plugin's settings, which exist only once Playnite
                    // has started.
                    if (Settings == null)
                        return;

                    string headline = null, detail = null;
                    var game = gameId == Guid.Empty ? null : Playnite.Database.Games.Get(gameId);
                    if (game != null)
                        SaveSync.DescribeStatus(game, out headline, out detail);

                    // The selection may have moved on while this was being worked out.
                    if (gameId != _selectedGameId)
                        return;

                    CloudStatus.Text = headline ?? "";
                    CloudStatus.Detail = detail ?? "";
                    CloudStatus.Visible = headline != null;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[SaveSync] Could not refresh the cloud status: {ex.Message}");
                }
            });
        }

        // Push the save the player just produced back to RomM. Runs in the background so it never
        // delays returning to the library.
        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            base.OnGameStopped(args);
            _overlay?.Stop();
            if (args?.Game == null)
                return;
            ClearLaunching(args.Game.Id);
            OpenGames.Stopped(args.Game.Id);
            if (_finishedByUs.TryRemove(args.Game.Id, out byte _))
                return;   // finished already while quitting (FinishBeforeQuit)
            Task sync = EndSession(args.Game, args.ElapsedSeconds, DateTime.UtcNow);
            if (sync != null)
                _endingSync = sync;
        }

        private volatile bool _windowsEnding;

        // Sessions finished here while quitting, which Playnite then saw end too.
        private readonly ConcurrentDictionary<Guid, byte> _finishedByUs = new ConcurrentDictionary<Guid, byte>();

        /// <summary>
        /// What follows a session: "Last two weeks", and for RomM's games the closing save sync,
        /// achievements and the play session on the server. The save sync's task, or null.
        /// </summary>
        private Task EndSession(Game game, ulong seconds, DateTime endedUtc)
        {
            // Remembered for "Last two weeks" on the library's hover cards, for every game.
            if (seconds > 0)
            {
                Guid playedId = game.Id;
                Task.Run(() => Sessions.Add(playedId, seconds));
            }

            if (game.PluginId != PluginId)
                return null;
            Task sync = Task.Run(() => SaveSync.EndSession(game));

            // What was unlocked: RomM fetches it from RetroAchievements, then it is shown;
            // trophies and Xbox achievements are read from the emulator.
            if (seconds > 0)
                RefreshServerAchievements(game);
            EmulatorAchievements.SessionEnded(game);

            // The session goes to the account's play sessions on the server.
            if (seconds > 0 && RomMGameId.TryParse(game.GameId, out int playedRomId, out string _))
            {
                Task.Run(() =>
                {
                    UserData.QueueSession(playedRomId, endedUtc, seconds);
                    ScheduleSync(TimeSpan.FromSeconds(5));
                });
            }
            return sync;
        }

        /// <summary>
        /// A session Playnite dropped (it quit or crashed with the game running), finished as
        /// OnGameStopped would have: its playtime (only if Playnite did not count it), then
        /// <see cref="EndSession"/> when the same account is signed in. The save sync's task, or null.
        /// </summary>
        private Task FinishLeftOver(OpenSessions.Entry entry, DateTime endedUtc)
        {
            Game game = Playnite.Database.Games.Get(entry.GameId);
            if (game == null)
                return null;
            ulong seconds = endedUtc > entry.StartedUtc ? (ulong)(endedUtc - entry.StartedUtc).TotalSeconds : 0;
            if (seconds > 0 && game.Playtime == entry.PlaytimeAtStart)
            {
                game.Playtime += seconds;
                Playnite.Database.Games.Update(game);
            }
            Logger.Info($"[Sessions] {game.Name}: a session Playnite did not end ({seconds / 60} min) finished now.");
            if (!string.Equals(entry.AccountKey, CurrentAccountKey, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"[Sessions] {game.Name}: it was another account's; only its playtime is counted.");
                return null;
            }
            return EndSession(game, seconds, endedUtc);
        }

        /// <summary>
        /// On start-up: the sessions the last run left open. A game still running (same process)
        /// is finished when it closes; one that has closed, counted to when Playnite last saw it.
        /// </summary>
        private void RecoverSessions()
        {
            try
            {
                if (string.IsNullOrEmpty(CurrentAccountKey))
                    return;   // the next start, once someone is signed in
                foreach (OpenSessions.Entry entry in OpenGames.TakeLeftOver())
                {
                    if (OpenGames.IsOpen(entry.GameId))
                        continue;   // started again since
                    System.Diagnostics.Process process = OpenSessions.StillRunning(entry);
                    if (process == null)
                    {
                        FinishLeftOver(entry, entry.SeenUtc);
                        continue;
                    }
                    Logger.Info($"[Sessions] {entry.Name} is still running from before Playnite restarted; its session ends when it closes.");
                    System.Windows.Threading.Dispatcher ui = Playnite.MainView.UIDispatcher;
                    Task.Run(() =>
                    {
                        using (process)
                            process.WaitForExit();
                        ui.BeginInvoke(new Action(() => FinishLeftOver(entry, DateTime.UtcNow)));
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Sessions] Could not finish the sessions left open: {ex.Message}");
            }
        }

        /// <summary>
        /// Playnite is quitting with a game running (any way it quits: its menu, the tray, the
        /// window's close button, Fullscreen's menu). It cannot be stopped here, but it waits for
        /// this: the game is closed first (the overlay's countdown), then its session ended and
        /// its saves synced before Playnite goes. Playnite does not quit leaving a game running.
        /// One that cannot be closed from here (no process known) is finished on the next start
        /// (RecoverSessions).
        /// </summary>
        private void FinishBeforeQuit()
        {
            try
            {
                List<OpenSessions.Entry> running = OpenGames?.Running();
                if (running == null || running.Count == 0)
                    return;
                if (_windowsEnding)
                {
                    Logger.Info("[Sessions] Windows is ending the session; the running game's session is finished on the next start.");
                    return;
                }
                string names = string.Join(", ", running.Select(r => r.Name));
                Logger.Info($"[Sessions] Playnite is quitting with {names} running: closing it first.");

                Playnite.Dialogs.ActivateGlobalProgress(progress =>
                {
                    foreach (OpenSessions.Entry entry in running)
                    {
                        Game game = Playnite.Database.Games.Get(entry.GameId);
                        // The overlay's (a Windows game's window in front), else the process Playnite
                        // started, if it is still that one. None (a game started by a URL, or by a
                        // launcher that has gone): it cannot be closed from here.
                        int pid = _overlay != null && _overlay.GameId == entry.GameId ? _overlay.QuitProcessId : 0;
                        if (pid <= 0)
                        {
                            using (System.Diagnostics.Process started = OpenSessions.StillRunning(entry))
                                pid = started?.Id ?? 0;
                        }
                        bool gone = pid > 0 && Overlay.GameOverlay.CloseGameAndWait(pid, entry.Name, Overlay.GameOverlay.GraceFor(game),
                            text => progress.Text = $"Closing {entry.Name}... {text}");
                        if (!gone)
                        {
                            Logger.Info($"[Sessions] {entry.Name} could not be closed from here; its session is finished on the next start.");
                            continue;
                        }

                        // Playnite's own ending (its playtime, OnGameStopped), if it sees the game go in time.
                        progress.Text = $"Saving {entry.Name}...";
                        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                        while (OpenGames.IsOpen(entry.GameId) && DateTime.UtcNow < until)
                            Thread.Sleep(200);
                        if (OpenGames.IsOpen(entry.GameId))
                        {
                            OpenGames.Stopped(entry.GameId);
                            _finishedByUs[entry.GameId] = 0;
                            Task sync = FinishLeftOver(entry, DateTime.UtcNow);
                            if (sync != null)
                                _endingSync = sync;
                        }
                    }
                    progress.Text = "Syncing saves...";
                    _endingSync?.Wait(TimeSpan.FromSeconds(60));
                }, new GlobalProgressOptions($"Closing {names}...", false) { IsIndeterminate = true });
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Sessions] Could not close the running game before quitting: {ex.Message}");
            }
        }

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return new RomMMetadataProvider(this);
        }
        #endregion

    #region RomM Status Syncing
        public IList<RomMCollection> FetchFavorites()
        {
            string apiFavoriteUrl = CombineUrl(Settings.RomMHost, "api/collections");
            try
            {
                // Make the request and get the response
                HttpResponseMessage response = HttpClientSingleton.Instance.GetAsync(apiFavoriteUrl).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();

                // Assuming the response is in JSON format
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonConvert.DeserializeObject<List<RomMCollection>>(body);
            }
            catch (HttpRequestException e)
            {
                Logger.Error($"Request exception: {e.Message}");
                return new List<RomMCollection>();
            }
        }
        internal RomMCollection CreateFavorites()
        {
            string apiCollectionUrl = CombineUrl(Settings.RomMHost, "api/collections?is_favorite=true&is_public=false");
            try
            {
                var formData = new MultipartFormDataContent();
                formData.Add(new StringContent("Favorites"), "name");

                HttpResponseMessage postResponse = HttpClientSingleton.Instance.PostAsync(apiCollectionUrl, formData).GetAwaiter().GetResult();
                postResponse.EnsureSuccessStatusCode();

                string body = postResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonConvert.DeserializeObject<RomMCollection>(body);
            }
            catch (HttpRequestException e)
            {
                Logger.Error($"Request exception: {e.Message}");
                return null;
            }
        }
        internal void UpdateFavorites(RomMCollection favoriteCollection, List<int> romIds)
        {
            if (favoriteCollection == null)
            {
                Logger.Error($"Can't update favorites, collection is null");
                return;
            }

            string apiCollectionUrl = CombineUrl(Settings.RomMHost, "api/collections");
            try
            {
                var formData = new MultipartFormDataContent();
                formData.Add(new StringContent(JsonConvert.SerializeObject(romIds)), "rom_ids");
                HttpResponseMessage putResponse = HttpClientSingleton.Instance.PutAsync($"{apiCollectionUrl}/{favoriteCollection.Id}", formData).GetAwaiter().GetResult();
                putResponse.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException e)
            {
                Logger.Error($"Request exception: {e.Message}");
            }
        }

        private void OnItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            Task.Run(async () =>
            {
                foreach (var update in e.UpdatedItems)
                {
                    var oldGame = update.OldData;
                    var newGame = update.NewData;

                    // Ignore non-RomM games
                    if (newGame.PluginId != Id)
                    {
                        continue;
                    }

                    // This is the cancel signal
                    if (oldGame.IsInstalling && !newGame.IsInstalling)
                    {
                        DownloadQueueController?.Cancel(newGame.Id);
                    }

                    // Uninstalled on the way out of the library: now out of Playnite too.
                    if (oldGame.IsInstalled && !newGame.IsInstalled
                        && _removeAfterUninstall.TryRemove(newGame.Id, out DateTime askedUtc)
                        && DateTime.UtcNow - askedUtc < TimeSpan.FromHours(1))
                    {
                        Guid gone = newGame.Id;
                        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                Playnite.Database.Games.Remove(gone);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[Library] Could not remove the uninstalled game: {ex.Message}");
                            }
                        }));
                    }
                
                    // The importer wrote this change itself; don't push it back. Consumed whether or not
                    // sync is on, so a suppression set while it is off cannot swallow a later real edit.
                    if (ignoredGameIds.TryRemove(newGame.Id, out byte _))
                    {
                        continue;
                    }

                    // The user's own data on the game: sent to the server by the next sync, which
                    // merges it with the server's (see UserDataSync and CollectionSync).
                    if (oldGame.Favorite != newGame.Favorite || oldGame.CompletionStatusId != newGame.CompletionStatusId
                        || oldGame.UserScore != newGame.UserScore || !string.Equals(oldGame.Notes ?? "", newGame.Notes ?? "", StringComparison.Ordinal))
                    {
                        ScheduleSync(TimeSpan.FromSeconds(8));
                    }
                }
            });
        }
    #endregion

    #region RomM Save Syncing
        // Manual "Sync saves with RomM" menu action: sync each selected game on a background thread
        // and surface a single summary notification so the user gets feedback.
        // Asks for the name Ludusavi knows this game by, then syncs so the cloud status reflects
        // whether that name worked.
        private void PromptLudusaviTitle(Game game)
        {
            try
            {
                string current = SaveSync.GetLudusaviTitle(game);
                var answer = Playnite.Dialogs.SelectString(
                    "The name Ludusavi knows this game by: its title on PCGamingWiki, or the name of a custom game " +
                    "you added in Ludusavi. Leave it empty to go back to the library name.",
                    $"Ludusavi name for {game.Name}",
                    current ?? game.Name);
                if (answer == null || !answer.Result)
                    return;

                SaveSync.SetLudusaviTitle(game, answer.SelectedString);
                SyncSavesWithNotification(new List<Game> { game });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[SaveSync] Could not set the Ludusavi name for {game?.Name}.");
            }
        }

        private void SyncSavesWithNotification(IList<Game> games)
        {
            if (games == null || games.Count == 0)
            {
                return;
            }

            Task.Run(() =>
            {
                int uploaded = 0, downloaded = 0, conflicts = 0, failed = 0;
                bool anyApplicable = false;
                string lastMessage = null;

                foreach (var game in games)
                {
                    var outcome = SaveSync.Sync(game);

                    // Counted whether or not a save location was resolved: a game that failed before
                    // becoming applicable still failed, and dropping it hides the failure.
                    anyApplicable |= outcome.Applicable;
                    uploaded += outcome.Uploaded;
                    downloaded += outcome.Downloaded;
                    conflicts += outcome.Conflicts;
                    failed += outcome.Failed;

                    if (!string.IsNullOrEmpty(outcome.Message))
                    {
                        lastMessage = outcome.Message;
                    }
                }

                if (!anyApplicable)
                {
                    Playnite.Notifications.Add("RomMPlugin.SaveSync",
                        lastMessage ?? $"Save sync currently supports: {SaveSync.SupportedEmulators}.",
                        NotificationType.Info);
                    return;
                }

                var summary = $"Save sync complete: {downloaded} downloaded, {uploaded} uploaded" +
                              (conflicts > 0 ? $", {conflicts} conflict(s) resolved" : "") +
                              (failed > 0 ? $", {failed} failed" : "") + ".";

                // Without this the reason a sync failed (bad token, unreachable server) never
                // reaches the user -- they only see a count.
                if (failed > 0 && !string.IsNullOrEmpty(lastMessage))
                {
                    summary += $" {lastMessage}";
                }

                Playnite.Notifications.Add("RomMPlugin.SaveSync", summary,
                    failed > 0 ? NotificationType.Error : NotificationType.Info);
            });
        }
    #endregion
    }
}