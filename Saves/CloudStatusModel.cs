using System.Collections.Generic;

namespace RomM.Saves
{
    /// <summary>
    /// The selected game's cloud save status, for themes. A theme binds to it with
    /// {PluginSettings Plugin=RomM, Path=Text} (and Visible, Detail).
    ///
    /// Kept apart from the plugin's settings object on purpose: that one is only created once
    /// Playnite has started, by which time a theme may already have bound to a null and would
    /// never see it appear. This object exists from the moment the plugin is constructed.
    /// </summary>
    public class CloudStatusModel : ObservableObject
    {
        /// <summary>
        /// The selected game's download progress. It lives here only because a plugin can offer
        /// themes a single object to bind to; a theme reaches it as Path=Download.Headline etc.
        /// </summary>
        public Downloads.DownloadStatusModel Download { get; } = new Downloads.DownloadStatusModel();

        private bool _logoVisible;

        /// <summary>Whether the selected game has a logo image, for a theme's logo area.</summary>
        public bool LogoVisible
        {
            get => _logoVisible;
            set => SetValue(ref _logoVisible, value);
        }

        private bool _newsFeedVisible;

        /// <summary>
        /// Whether the selected game's page shows its Steam news (RomM_NewsFeed) in place of its
        /// description: a PC game on Steam with announcements.
        /// </summary>
        public bool NewsFeedVisible
        {
            get => _newsFeedVisible;
            set => SetValue(ref _newsFeedVisible, value);
        }

        private bool _videoVisible;

        /// <summary>Whether the RomM server holds a video for the selected game.</summary>
        public bool VideoVisible
        {
            get => _videoVisible;
            set => SetValue(ref _videoVisible, value);
        }

        /// <summary>
        /// The selected game's achievements, for a theme: {PluginSettings Plugin=RomM,
        /// Path=Achievements.HasData} (and Unlocked, Total, Percent, Points, PointsTotal).
        /// </summary>
        public AchievementViews.AchievementsSummary Achievements { get; } = new AchievementViews.AchievementsSummary();

        /// <summary>
        /// The selected game's HowLongToBeat times from RomM, for themes: {PluginSettings
        /// Plugin=RomM, Path=Hltb.HasData} (and MainStoryFormat, MainExtraFormat,
        /// CompletionistFormat, PlayedFormat, Percent).
        /// </summary>
        public global::RomM.Games.HltbSummary Hltb { get; } = new global::RomM.Games.HltbSummary();

        /// <summary>
        /// The selected game's achievement sync with RomM, for themes: {PluginSettings
        /// Plugin=RomM, Path=AchievementSync.Text} (and Visible, Detail).
        /// </summary>
        public global::RomM.Games.AchievementSyncSummary AchievementSync { get; } = new global::RomM.Games.AchievementSyncSummary();

        private bool _libraryHomeVisible;

        /// <summary>Whether the library's Home page is showing in place of a game's page.</summary>
        public bool LibraryHomeVisible
        {
            get => _libraryHomeVisible;
            set
            {
                SetValue(ref _libraryHomeVisible, value);
                OnPropertyChanged(nameof(LibraryDownloadsVisible));
            }
        }

        private string _libraryPage = "Home";

        /// <summary>Which page shows in the library's Home place: "Home" or "Downloads".</summary>
        public string LibraryPage
        {
            get => _libraryPage;
            set
            {
                SetValue(ref _libraryPage, value ?? "Home");
                OnPropertyChanged(nameof(LibraryDownloadsVisible));
            }
        }

        /// <summary>
        /// Whether the library's Downloads page is showing, for themes: the game list is hidden
        /// beside it ({PluginSettings Plugin=RomM, Path=LibraryDownloadsVisible}).
        /// </summary>
        public bool LibraryDownloadsVisible => _libraryHomeVisible && _libraryPage == "Downloads";

        /// <summary>Raised to show one game's Store page (from the library), with its RomM id.</summary>
        public event System.Action<int> StoreGameRequested;

        /// <summary>Shows the Store at a game's page.</summary>
        public void OpenStoreGame(int romId)
        {
            // The request first, so the Store knows, as it appears, that it is going to a
            // game's page and not back to where it was.
            StoreGameRequested?.Invoke(romId);
            StoreVisible = true;
        }

        /// <summary>
        /// Raised once the library is up: the Store starts loading out of sight, so it is ready,
        /// or nearly, by the time it is opened. <see cref="StorePreloadWanted"/> tells a Store
        /// made after it was raised.
        /// </summary>
        public event System.Action StorePreloadRequested;

        public bool StorePreloadWanted { get; private set; }

        public void RequestStorePreload()
        {
            StorePreloadWanted = true;
            StorePreloadRequested?.Invoke();
        }

        /// <summary>Raised to send the Store to its Home page (the STORE tab's menu).</summary>
        public event System.Action StoreHomeRequested;

        public void OpenStoreHome()
        {
            // The request first, so the Store knows, as it appears, that it is going Home.
            StoreHomeRequested?.Invoke();
            PressStoreTab();
        }

        /// <summary>Raised to send the Store to its Platforms, Genres or Series page (the STORE tab's menu).</summary>
        public event System.Action<string> StoreDirectoryRequested;

        public void OpenStoreDirectory(string which)
        {
            // The request first, as for Home, so the Store knows where it is going as it appears.
            StoreDirectoryRequested?.Invoke(which);
            PressStoreTab();
        }

        /// <summary>
        /// Raised when the account signs out: the Store lets go of where it was (its last page,
        /// a game's page, a search), so the next account starts at its Home.
        /// </summary>
        public event System.Action AccountLeft;

        public void LeaveAccount() => AccountLeft?.Invoke();

        private bool _storeVisible;

        /// <summary>Whether the Store is showing in place of the library.</summary>
        public bool StoreVisible
        {
            get => _storeVisible;
            set => SetValue(ref _storeVisible, value);
        }

        /// <summary>Raised when the bar's search box has something for the Store to look up.</summary>
        public event System.Action<string> StoreSearchRequested;

        public void SearchStore(string text) => StoreSearchRequested?.Invoke(text ?? "");

        /// <summary>Raised whenever the STORE tab is pressed, also when the Store is already showing.</summary>
        public event System.Action StoreTabPressed;

        /// <summary>The STORE tab: shows the Store, or returns it to browsing if it is showing.</summary>
        public void PressStoreTab()
        {
            StoreVisible = true;
            StoreTabPressed?.Invoke();
        }

        private Playnite.SDK.RelayCommand _openStoreCommand;
        private Playnite.SDK.RelayCommand _openLibraryCommand;

        /// <summary>For a theme's STORE tab: Command="{PluginSettings Plugin=RomM, Path=OpenStoreCommand}".</summary>
        public Playnite.SDK.RelayCommand OpenStoreCommand =>
            _openStoreCommand ?? (_openStoreCommand = new Playnite.SDK.RelayCommand(PressStoreTab));

        /// <summary>For a theme's LIBRARY tab.</summary>
        public Playnite.SDK.RelayCommand OpenLibraryCommand =>
            _openLibraryCommand ?? (_openLibraryCommand = new Playnite.SDK.RelayCommand(() => StoreVisible = false));

        private bool _videoOn;

        /// <summary>
        /// Whether videos are switched on, for a theme's video on/off switch to bind to (both
        /// ways). Starts from the "Play game videos automatically" setting; flipping the switch
        /// lasts until Playnite closes and does not change the setting.
        /// </summary>
        public bool VideoOn
        {
            get => _videoOn;
            set => SetValue(ref _videoOn, value);
        }

        private bool _manualVisible;

        /// <summary>
        /// Whether the selected game has a manual the built-in viewer can show; for the
        /// visibility of a theme's Manual tab.
        /// </summary>
        public bool ManualVisible
        {
            get => _manualVisible;
            set => SetValue(ref _manualVisible, value);
        }

        private bool _visible;
        private string _text = "";
        private string _detail = "";

        /// <summary>Whether cloud saves apply to the selected game at all.</summary>
        public bool Visible
        {
            get => _visible;
            set => SetValue(ref _visible, value);
        }

        /// <summary>Short status: "Up to date", "Syncing...", "Sync failed", "Not synced yet".</summary>
        public string Text
        {
            get => _text;
            set => SetValue(ref _text, value);
        }

        /// <summary>A sentence with the time of the last sync or the reason it failed.</summary>
        public string Detail
        {
            get => _detail;
            set => SetValue(ref _detail, value);
        }
    }
}
