using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Models.RomM;
using RomM.Models.RomM.Achievements;
using RomM.Models.RomM.Rom;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;

namespace RomM.Games
{
    // Shows RomM's RetroAchievements data in the SuccessStory extension, with RomM as the only
    // source: the achievement list comes from each rom, the earned state from the signed-in RomM
    // user. SuccessStory is used purely as the viewer, so it needs no RetroAchievements account of
    // its own (its RetroAchievements option should be switched off so it never fetches by itself).
    //
    // SuccessStory has no API for other extensions, so its live database object is driven through
    // reflection. Everything here is best-effort: if SuccessStory is missing or its internals have
    // changed, this logs once and the import carries on without achievements.
    internal class RomMAchievementSync
    {
        // true: badge images come from the RomM server. false: from RetroAchievements' public image
        // host, which always has them (RomM only has the ones it downloaded during a scan).
        private const bool UseRomMBadges = false;

        private readonly ILogger _logger;
        private readonly string _host;
        private readonly Dictionary<int, RomMRAGameProgression> _progressByRAId = new Dictionary<int, RomMRAGameProgression>();

        // SuccessStory internals, resolved once.
        private object _database;
        private MethodInfo _getOnlyCache;
        private MethodInfo _getDefault;
        private MethodInfo _addOrUpdate;
        private Type _achievementType;
        private bool _available;          // SuccessStory found and usable
        private bool _progressLoaded;     // the user's progress was read from RomM

        // Where the plugin's own achievement views read from; written whatever SuccessStory does.
        private AchievementViews.AchievementStore _store;

        public int Updated { get; private set; }

        /// <summary>Whether the account's progress was read from RomM (else nothing is applied).</summary>
        public bool ProgressLoaded => _progressLoaded;

        private RomMAchievementSync(ILogger logger, string host)
        {
            _logger = logger;
            _host = host;
        }

        /// <summary>
        /// Finds SuccessStory and loads the user's progress from RomM. Never throws; when anything
        /// is missing the returned object simply does nothing.
        /// </summary>
        public static RomMAchievementSync Create(ILogger logger, string host, CancellationToken cancelToken, AchievementViews.AchievementStore store = null)
        {
            var sync = new RomMAchievementSync(logger, host) { _store = store };
            try
            {
                // SuccessStory is optional now: the plugin shows achievements itself, and only
                // keeps SuccessStory filled in when it happens to be installed.
                sync._available = sync.BindSuccessStory();
                if (!sync._available && store == null)
                    return sync;

                sync.LoadProgress(cancelToken);
                sync._progressLoaded = true;
            }
            catch (Exception ex)
            {
                logger.Warn($"[Achievements] Disabled for this import: {ex.Message}");
            }
            return sync;
        }

        private bool BindSuccessStory()
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "SuccessStory", StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
            {
                _logger.Info("[Achievements] SuccessStory is not installed; achievements are shown by this plugin alone.");
                return false;
            }

            Type pluginType = assembly.GetType("SuccessStory.SuccessStory");
            _achievementType = assembly.GetType("SuccessStory.Models.Achievement");
            _database = pluginType?
                .GetProperty("PluginDatabase", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?
                .GetValue(null);
            if (_database == null || _achievementType == null)
            {
                _logger.Warn("[Achievements] SuccessStory found but its database is not reachable; skipping achievements.");
                return false;
            }

            Type dbType = _database.GetType();
            if (dbType.GetProperty("IsLoaded")?.GetValue(_database) as bool? == false)
            {
                _logger.Warn("[Achievements] SuccessStory has not finished loading; skipping achievements this time.");
                return false;
            }

            _getOnlyCache = dbType.GetMethod("GetOnlyCache", new[] { typeof(Guid) });
            _getDefault = dbType.GetMethod("GetDefault", new[] { typeof(Game) });
            _addOrUpdate = dbType.GetMethods().FirstOrDefault(m => m.Name == "AddOrUpdate" && m.GetParameters().Length == 1);
            if (_getOnlyCache == null || _getDefault == null || _addOrUpdate == null)
            {
                _logger.Warn("[Achievements] This SuccessStory version is not supported; skipping achievements.");
                return false;
            }
            return true;
        }

        private void LoadProgress(CancellationToken cancelToken)
        {
            RomMUser user = GetUser(cancelToken);
            if (user == null)
                return;

            if (string.IsNullOrEmpty(user.RAUsername))
            {
                _logger.Info("[Achievements] No RetroAchievements username on this RomM account; achievements will show as locked.");
            }
            else
            {
                // Ask RomM to top up its copy of the progress first. Incremental, so it only
                // re-reads games whose totals changed. A failure just means slightly older data.
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken))
                    using (var body = new StringContent("{\"incremental\":true}", Encoding.UTF8, "application/json"))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(90));
                        using (var response = HttpClientSingleton.Instance.PostAsync($"{_host}/api/users/{user.Id}/ra/refresh", body, cts.Token).GetAwaiter().GetResult())
                        {
                            if (response.IsSuccessStatusCode)
                                user = GetUser(cancelToken) ?? user;
                            else
                                _logger.Warn($"[Achievements] RomM progress refresh returned HTTP {(int)response.StatusCode}; using its stored progress.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[Achievements] RomM progress refresh failed ({ex.Message}); using its stored progress.");
                }
            }

            foreach (var game in user.RAProgression?.Results ?? new List<RomMRAGameProgression>())
            {
                if (game?.RAId != null)
                    _progressByRAId[game.RAId.Value] = game;
            }
            _logger.Info($"[Achievements] RomM has progress for {_progressByRAId.Count} games.");
        }

        private RomMUser GetUser(CancellationToken cancelToken)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using (var response = HttpClientSingleton.Instance.GetAsync($"{_host}/api/users/me", cts.Token).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    return JsonConvert.DeserializeObject<RomMUser>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                }
            }
        }

        /// <summary>
        /// Gives SuccessStory this game's achievements, unless what it already holds matches.
        /// </summary>
        public void Apply(Game game, RomMRom rom)
        {
            if (game == null || rom?.RAId == null || !_progressLoaded)
                return;

            var achievements = rom.RAMetadata?.Achievements;
            if (achievements == null || achievements.Count == 0)
                return;

            var earned = new Dictionary<string, RomMRAEarned>(StringComparer.OrdinalIgnoreCase);
            if (_progressByRAId.TryGetValue(rom.RAId.Value, out var progress) && progress.Earned != null)
            {
                foreach (var e in progress.Earned.Where(e => !string.IsNullOrEmpty(e?.BadgeId)))
                    earned[e.BadgeId] = e;
            }

            SaveOwn(game, rom, achievements, earned);
            if (!_available)
                return;

            try
            {
                int unlocked = achievements.Count(a => !string.IsNullOrEmpty(a.BadgeId) && earned.ContainsKey(a.BadgeId));

                // Skip when SuccessStory already shows the same set and the same unlock count, and
                // leave alone any game the user told SuccessStory to ignore.
                object cached = _getOnlyCache.Invoke(_database, new object[] { game.Id });
                if (cached != null)
                {
                    if (Read<bool>(cached, "IsIgnored"))
                        return;
                    if (Read<int>(cached, "RAgameID") == rom.RAId.Value
                        && Read<int>(cached, "Total") == achievements.Count
                        && Read<int>(cached, "Unlocked") == unlocked)
                        return;
                }

                object item = _getDefault.Invoke(_database, new object[] { game });
                if (item == null)
                    return;

                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_achievementType));
                foreach (var a in achievements)
                {
                    object ach = Activator.CreateInstance(_achievementType);
                    Write(ach, "Name", a.Title);
                    Write(ach, "ApiName", a.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                    Write(ach, "Description", a.Description);
                    Write(ach, "UrlUnlocked", BadgeUrl(a.BadgePath, a.BadgeUrl));
                    Write(ach, "UrlLocked", BadgeUrl(a.BadgePathLock, a.BadgeUrlLock));
                    Write(ach, "GamerScore", (float)(a.Points ?? 0));

                    if (!string.IsNullOrEmpty(a.BadgeId) && earned.TryGetValue(a.BadgeId, out var e))
                    {
                        Write(ach, "DateUnlocked", ParseUtc(e.Date));
                        Write(ach, "DateUnlockedRaHardCore", ParseUtc(e.DateHardcore));
                    }
                    list.Add(ach);
                }

                Write(item, "Items", list);
                Write(item, "RAgameID", rom.RAId.Value);

                PropertyInfo sourceLink = item.GetType().GetProperty("SourcesLink");
                if (sourceLink != null)
                {
                    object link = Activator.CreateInstance(sourceLink.PropertyType);
                    Write(link, "Name", "RetroAchievements");
                    Write(link, "GameName", rom.Name);
                    Write(link, "Url", $"https://retroachievements.org/game/{rom.RAId.Value}");
                    sourceLink.SetValue(item, link);
                }

                item.GetType().GetMethod("SetRaretyIndicator", Type.EmptyTypes)?.Invoke(item, null);

                // SuccessStory's database feeds its on-screen lists, so write on the UI thread.
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    dispatcher.Invoke(() => _addOrUpdate.Invoke(_database, new[] { item }));
                else
                    _addOrUpdate.Invoke(_database, new[] { item });

                Updated++;
            }
            catch (Exception ex)
            {
                Exception cause = (ex as TargetInvocationException)?.InnerException ?? ex;
                _logger.Warn($"[Achievements] {rom.Name}: not updated: {cause.Message}");
            }
        }

        // The plugin's own copy, for its achievement views.
        private void SaveOwn(Game game, RomMRom rom, List<RomMRAAchievement> achievements, Dictionary<string, RomMRAEarned> earned)
        {
            if (_store == null)
                return;
            try
            {
                var data = new AchievementViews.GameAchievements { RAId = rom.RAId.Value, UpdatedUtc = DateTime.UtcNow };
                int order = 0;
                foreach (var a in achievements)
                {
                    var item = new AchievementViews.AchievementItem
                    {
                        Title = a.Title,
                        Description = a.Description,
                        Points = a.Points ?? 0,
                        BadgeUrl = OwnBadge(a.BadgePath, a.BadgeUrl),
                        BadgeLockedUrl = OwnBadge(a.BadgePathLock, a.BadgeUrlLock),
                        Order = order++,
                    };
                    if (!string.IsNullOrEmpty(a.BadgeId) && earned.TryGetValue(a.BadgeId, out var e))
                    {
                        item.UnlockedUtc = ParseUtc(e.Date);
                        item.UnlockedHardcoreUtc = ParseUtc(e.DateHardcore);
                    }
                    data.Items.Add(item);
                }
                _store.Save(game.Id, data);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Achievements] {rom.Name}: could not keep its achievements: {ex.Message}");
            }
        }

        // The server's copy of a badge when it has one, else RetroAchievements' own.
        private string OwnBadge(string romMPath, string externalUrl)
        {
            if (!string.IsNullOrEmpty(romMPath))
                return romMPath.StartsWith("/") ? $"{_host}{romMPath}" : $"{_host}/{romMPath}";
            return externalUrl;
        }

        private string BadgeUrl(string romMPath, string externalUrl)
        {
            if (UseRomMBadges && !string.IsNullOrEmpty(romMPath))
                return $"{_host}{romMPath}";
            return externalUrl;
        }

        // RetroAchievements reports times in UTC without saying so.
        private static DateTime? ParseUtc(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static T Read<T>(object target, string property)
        {
            object value = target.GetType().GetProperty(property)?.GetValue(target);
            return value is T typed ? typed : default(T);
        }

        private static void Write(object target, string property, object value)
        {
            PropertyInfo info = target.GetType().GetProperty(property);
            if (info != null && info.CanWrite)
                info.SetValue(target, value);
        }
    }
}
