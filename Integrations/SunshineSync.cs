using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RomM.Integrations
{
    /// <summary>
    /// Sunshine (LizardByte's game streaming host): the installed games listed in it as
    /// applications, so Moonlight shows them, each started through Playnite
    /// ("Playnite.DesktopApp.exe --start &lt;game id&gt;": Playnite's own start, with its cloud
    /// saves, emulator setup and play time). Added when a game is installed, removed when it is
    /// uninstalled, hidden or removed; Sunshine's other applications are never touched.
    ///
    /// Through Sunshine's own web API (https://localhost:47990/api/apps, with the web UI's admin
    /// name and password), so Sunshine takes the change at once with no restart. Ours are known by
    /// their command naming a Playnite game. Sunshine answers on this computer only, over HTTPS with
    /// a certificate of its own making: accepted for this computer's own address only.
    /// </summary>
    public sealed class SunshineSync
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Regex OurCommand = new Regex(@"--start\s+""?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})""?", RegexOptions.IgnoreCase);

        private readonly IPlayniteAPI _api;
        private readonly Func<SunshineSettings> _settings;
        private readonly string _coversFolder;
        private readonly SemaphoreSlim _one = new SemaphoreSlim(1);
        private readonly System.Windows.Threading.DispatcherTimer _soon;

        public SunshineSync(IPlayniteAPI api, string dataFolder, Func<SunshineSettings> settings)
        {
            _api = api;
            _settings = settings;
            _coversFolder = Path.Combine(dataFolder, "SunshineCovers");
            // Changes come in bursts (a library update, a platform uninstalled): one sync after.
            _soon = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _soon.Tick += (s, e) =>
            {
                _soon.Stop();
                SyncInBackground();
            };
            _api.Database.Games.ItemUpdated += (s, e) =>
            {
                if (e.UpdatedItems.Any(u => u.OldData.IsInstalled != u.NewData.IsInstalled || u.OldData.Hidden != u.NewData.Hidden
                                            || u.OldData.Name != u.NewData.Name || u.OldData.CoverImage != u.NewData.CoverImage))
                    SyncSoon();
            };
            _api.Database.Games.ItemCollectionChanged += (s, e) => SyncSoon();
        }

        /// <summary>A sync a few seconds from now (on the UI thread).</summary>
        public void SyncSoon()
        {
            if (_settings()?.Enabled != true)
                return;
            _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
            {
                _soon.Stop();
                _soon.Start();
            }));
        }

        public void SyncInBackground()
        {
            if (_settings()?.Enabled != true)
                return;
            Task.Run(async () =>
            {
                string problem = await SyncAsync().ConfigureAwait(false);
                if (problem != null)
                    s_log.Warn($"[Sunshine] {problem}");
            });
        }

        /// <summary>
        /// Brings Sunshine's list in line with the installed games. Null when done, else what went
        /// wrong. With <paramref name="removeAll"/>, takes all of ours out instead.
        /// </summary>
        public async Task<string> SyncAsync(bool removeAll = false, SunshineSettings use = null)
        {
            SunshineSettings settings = use ?? _settings();
            if (settings == null || (!settings.Enabled && !removeAll))
                return null;
            await _one.WaitAsync().ConfigureAwait(false);
            try
            {
                using (HttpClient client = Client(settings, out string problem))
                {
                    if (client == null)
                        return problem;

                    // What should be there: every installed game Playnite would show.
                    var wanted = removeAll
                        ? new Dictionary<Guid, Game>()
                        : _api.Database.Games.Where(g => g.IsInstalled && !g.Hidden).ToDictionary(g => g.Id);

                    // Ours that should go, from the end so the indexes before them hold.
                    List<SunshineApp> apps = await Apps(client).ConfigureAwait(false);
                    if (apps == null)
                        return "Sunshine could not be reached, or did not accept the name and password.";
                    foreach (SunshineApp app in apps.Where(a => a.GameId != null && (!wanted.ContainsKey(a.GameId.Value)
                                 || apps.Count(o => o.GameId == a.GameId) > 1 && apps.First(o => o.GameId == a.GameId) != a))
                             .OrderByDescending(a => a.Index).ToList())
                    {
                        using (var response = await client.DeleteAsync($"api/apps/{app.Index}").ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                                return $"Sunshine would not remove {app.Name} ({(int)response.StatusCode}).";
                        }
                        s_log.Info($"[Sunshine] Removed {app.Name}.");
                    }

                    // Ours to add, or to bring up to date (name, cover); the list asked again after
                    // each change, as Sunshine re-sorts it by name (the indexes move).
                    apps = await Apps(client).ConfigureAwait(false) ?? apps;
                    bool moved = false;
                    foreach (Game game in wanted.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (moved)
                        {
                            apps = await Apps(client).ConfigureAwait(false) ?? apps;
                            moved = false;
                        }
                        SunshineApp existing = apps.FirstOrDefault(a => a.GameId == game.Id);
                        string cover = Cover(game);
                        if (existing != null && existing.Name == game.Name && string.Equals(existing.Image ?? "", cover ?? "", StringComparison.OrdinalIgnoreCase))
                            continue;
                        JObject body = Describe(game, existing?.Index ?? -1, cover);
                        using (var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"))
                        using (var response = await client.PostAsync("api/apps", content).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                                return $"Sunshine would not take {game.Name} ({(int)response.StatusCode}).";
                        }
                        moved = true;
                        s_log.Info($"[Sunshine] {(existing == null ? "Added" : "Updated")} {game.Name}.");
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                return "Sunshine could not be updated: " + ex.Message;
            }
            finally
            {
                _one.Release();
            }
        }

        /// <summary>
        /// Takes every game this put in Sunshine back out (the integration turned off), with the
        /// settings it had while on. Sunshine's other applications stay.
        /// </summary>
        public async Task RemoveAllAsync(SunshineSettings settings)
        {
            string problem = await SyncAsync(true, settings).ConfigureAwait(false);
            if (problem != null)
                s_log.Warn($"[Sunshine] Could not take the games back out: {problem}");
        }

        /// <summary>Whether Sunshine answers with these settings: null when it does, else why not.</summary>
        public async Task<string> TestAsync(SunshineSettings settings)
        {
            try
            {
                using (HttpClient client = Client(settings, out string problem))
                {
                    if (client == null)
                        return problem;
                    using (var response = await client.GetAsync("api/apps").ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                            return "Sunshine did not accept the name and password (those of its web UI).";
                        if (!response.IsSuccessStatusCode)
                            return $"Sunshine answered {(int)response.StatusCode}.";
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                return "Sunshine could not be reached: " + (ex.InnerException?.Message ?? ex.Message) + " Is it running?";
            }
        }

        // ---- Sunshine's side ------------------------------------------------------------------

        private sealed class SunshineApp
        {
            public int Index;
            public string Name;
            public string Image;
            public Guid? GameId;
        }

        private static async Task<List<SunshineApp>> Apps(HttpClient client)
        {
            using (var response = await client.GetAsync("api/apps").ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    return null;
                JObject root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var list = new List<SunshineApp>();
                int index = 0;
                foreach (JToken app in root["apps"] as JArray ?? new JArray())
                {
                    string cmd = app.Value<string>("cmd") ?? "";
                    Match ours = cmd.IndexOf("Playnite.DesktopApp.exe", StringComparison.OrdinalIgnoreCase) >= 0 ? OurCommand.Match(cmd) : Match.Empty;
                    list.Add(new SunshineApp
                    {
                        Index = index++,
                        Name = app.Value<string>("name"),
                        Image = app.Value<string>("image-path"),
                        GameId = ours.Success && Guid.TryParse(ours.Groups[1].Value, out Guid id) ? id : (Guid?)null,
                    });
                }
                return list;
            }
        }

        // Sunshine's description of one game: started through Playnite, which hands it to the
        // Playnite already running and returns at once (so Sunshine is told not to wait on it:
        // the stream ends from Moonlight).
        private JObject Describe(Game game, int index, string cover)
        {
            string playnite = Path.Combine(_api.Paths.ApplicationPath, "Playnite.DesktopApp.exe");
            var body = new JObject
            {
                ["name"] = game.Name ?? "Game",
                ["index"] = index,
                ["cmd"] = $"\"{playnite}\" --start {game.Id} --hidesplashscreen",
                ["output"] = "",
                ["exclude-global-prep-cmd"] = false,
                ["elevated"] = false,
                ["auto-detach"] = true,
                ["wait-all"] = true,
                ["exit-timeout"] = 5,
                ["prep-cmd"] = new JArray(),
                ["detached"] = new JArray(),
            };
            if (cover != null)
                body["image-path"] = cover;
            return body;
        }

        // The game's cover as a PNG (what Sunshine takes), made from Playnite's once and kept;
        // null when the game has none.
        private string Cover(Game game)
        {
            try
            {
                if (string.IsNullOrEmpty(game.CoverImage))
                    return null;
                string source = _api.Database.GetFullFilePath(game.CoverImage);
                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                    return null;
                Directory.CreateDirectory(_coversFolder);
                string target = Path.Combine(_coversFolder, game.Id + ".png");
                if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source))
                    return target;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(source);
                bitmap.DecodePixelWidth = 600;
                bitmap.EndInit();
                bitmap.Freeze();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(target))
                    encoder.Save(file);
                return target;
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Sunshine] No cover for {game.Name}: {ex.Message}");
                return null;
            }
        }

        private static HttpClient Client(SunshineSettings settings, out string problem)
        {
            problem = null;
            if (!Uri.TryCreate((settings.Url ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out Uri address))
            {
                problem = "Sunshine's address is not a web address.";
                return null;
            }
            if (!address.IsLoopback)
            {
                problem = "Sunshine has to be on this computer (its address localhost) to stream its games.";
                return null;
            }
            if (string.IsNullOrEmpty(settings.User))
            {
                problem = "Sunshine's name and password are not set.";
                return null;
            }
            // Sunshine's own certificate, which nothing else vouches for: taken from this
            // computer's own address only (checked above), never elsewhere.
            var handler = new WebRequestHandler
            {
                ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    errors == SslPolicyErrors.None || (sender as HttpWebRequest)?.RequestUri?.IsLoopback == true,
            };
            var client = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.User}:{settings.Password}")));
            return client;
        }

        // ---- The password, kept for this Windows user only -----------------------------------

        public static string Protect(string password)
        {
            if (string.IsNullOrEmpty(password))
                return "";
            byte[] sealed_ = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(sealed_);
        }

        public static string Unprotect(string protectedPassword)
        {
            try
            {
                if (string.IsNullOrEmpty(protectedPassword))
                    return "";
                byte[] open = ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(open);
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>What SunshineSync works with, read from the settings when it runs.</summary>
    public sealed class SunshineSettings
    {
        public bool Enabled;
        public string Url;
        public string User;
        public string Password;
    }
}
