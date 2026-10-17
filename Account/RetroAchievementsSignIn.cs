using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace RomM.Account
{
    /// <summary>
    /// The signed-in account's RetroAchievements sign-in. RomM keeps only the user's
    /// RetroAchievements name; the emulators need a login token, which RetroAchievements gives
    /// for the password. So the account signs in here once (from the guard on the achievement
    /// sections), as the name set in its RomM profile, and the token is kept for this Windows
    /// user only (DPAPI), one file per account. The password is never kept.
    ///
    /// The token is RetroAchievements' one token for that user, the same any emulator gets on
    /// signing in, so handing it to the emulators signs them in as that user.
    /// </summary>
    public class RetroAchievementsSignIn
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private const string LoginUrl = "https://retroachievements.org/dorequest.php";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RomM.Playnite.RetroAchievements");

        // Its own client: the RomM one carries the RomM sign-in, which is not RetroAchievements' business.
        private static readonly HttpClient s_http = CreateClient();

        private sealed class State
        {
            public string RomMUsername { get; set; }   // the RetroAchievements name in the RomM profile
            public string Username { get; set; }       // the name RetroAchievements signed in
            public string Token { get; set; }          // encrypted for this Windows user
            public DateTime SignedInUtc { get; set; }
        }

        private readonly object _lock = new object();
        private string _path;
        private State _state;

        /// <summary>Raised (on any thread) when the sign-in, or the RomM profile's name, changes.</summary>
        public event Action Changed;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            string version = typeof(RetroAchievementsSignIn).Assembly.GetName().Version?.ToString(3) ?? "0";
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"RomM-Playnite/{version}");
            return client;
        }

        /// <summary>Switches to an account's file (on signing in to RomM).</summary>
        public void UseFile(string path)
        {
            lock (_lock)
            {
                if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                    return;
                _path = path;
                _state = null;
            }
            Changed?.Invoke();
        }

        private State Current
        {
            get
            {
                if (_state != null)
                    return _state;
                try
                {
                    if (_path != null && File.Exists(_path))
                        _state = JsonConvert.DeserializeObject<State>(File.ReadAllText(_path));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[RetroAchievements] Could not read the sign-in: {ex.Message}");
                }
                return _state ?? (_state = new State());
            }
        }

        private void Save()
        {
            try
            {
                if (_path == null)
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[RetroAchievements] Could not keep the sign-in: {ex.Message}");
            }
        }

        /// <summary>The RetroAchievements name in the account's RomM profile; empty when none is set.</summary>
        public string RomMUsername
        {
            get
            {
                lock (_lock)
                    return Current.RomMUsername ?? "";
            }
        }

        /// <summary>
        /// Signed in to RetroAchievements as the name the RomM profile gives. A sign-in as another
        /// name (the profile changed since) does not count.
        /// </summary>
        public bool SignedIn
        {
            get
            {
                lock (_lock)
                {
                    State state = Current;
                    return !string.IsNullOrEmpty(state.Token) && !string.IsNullOrEmpty(state.RomMUsername)
                        && string.Equals(state.Username, state.RomMUsername, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>The name signed in, as RetroAchievements spells it; null when not signed in.</summary>
        public string Username
        {
            get
            {
                lock (_lock)
                    return SignedIn ? Current.Username : null;
            }
        }

        /// <summary>The login token; null when not signed in or it cannot be read here.</summary>
        public string Token()
        {
            lock (_lock)
            {
                if (!SignedIn)
                    return null;
                try
                {
                    byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(Current.Token), Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plain);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[RetroAchievements] Could not read the kept token: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Whether any account on this computer has signed in to RetroAchievements here. Until one
        /// has, the emulators' own sign-ins are left alone (someone may have signed in to one by
        /// hand); from then on each launch signs the emulator in as the account, or out.
        /// </summary>
        public static bool AnyKept(string accountsFolder)
        {
            try
            {
                if (!Directory.Exists(accountsFolder))
                    return false;
                foreach (string file in Directory.GetFiles(accountsFolder, "RetroAchievements.json", SearchOption.AllDirectories))
                {
                    State state = JsonConvert.DeserializeObject<State>(File.ReadAllText(file));
                    if (!string.IsNullOrEmpty(state?.Token))
                        return true;
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[RetroAchievements] Could not look for kept sign-ins: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// The name the RomM profile gives, as just read from the server. <paramref name="notify"/>
        /// raises Changed even when it is the same (someone asked to check again).
        /// </summary>
        public void SetRomMUsername(string name, bool notify = false)
        {
            name = (name ?? "").Trim();
            bool changed;
            lock (_lock)
            {
                changed = !string.Equals(Current.RomMUsername ?? "", name, StringComparison.Ordinal);
                if (changed)
                {
                    Current.RomMUsername = name;
                    Save();
                }
            }
            if (changed || notify)
                Changed?.Invoke();
        }

        /// <summary>
        /// Signs in to RetroAchievements as the RomM profile's name. On a background thread.
        /// False with the reason when RetroAchievements refuses, cannot be reached, or signs in
        /// someone else.
        /// </summary>
        public bool SignIn(string password, out string problem)
        {
            problem = null;
            string name = RomMUsername;
            if (name.Length == 0)
            {
                problem = "Set your RetroAchievements username in your RomM profile first.";
                return false;
            }
            if (string.IsNullOrEmpty(password))
            {
                problem = "Enter your RetroAchievements password.";
                return false;
            }

            JObject answer;
            try
            {
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["r"] = "login2",
                    ["u"] = name,
                    ["p"] = password,
                });
                using (HttpResponseMessage response = s_http.PostAsync(LoginUrl, form).GetAwaiter().GetResult())
                {
                    string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    try
                    {
                        answer = JObject.Parse(text);
                    }
                    catch (Exception)
                    {
                        problem = $"RetroAchievements answered unexpectedly ({(int)response.StatusCode}).";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                problem = "Couldn't reach RetroAchievements: " + (ex.InnerException?.Message ?? ex.Message);
                return false;
            }

            if ((bool?)answer["Success"] != true)
            {
                problem = (string)answer["Error"] ?? "RetroAchievements did not accept the sign-in.";
                return false;
            }
            string user = (string)answer["User"];
            string token = (string)answer["Token"];
            if (string.IsNullOrEmpty(token))
            {
                problem = "RetroAchievements signed in but gave no token.";
                return false;
            }
            if (!string.Equals(user, name, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"RetroAchievements signed in {user}, but your RomM profile says {name}.";
                return false;
            }

            lock (_lock)
            {
                Current.Username = user;
                Current.Token = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
                Current.SignedInUtc = DateTime.UtcNow;
                Save();
            }
            s_log.Info($"[RetroAchievements] Signed in as {user}.");
            Changed?.Invoke();
            return true;
        }

        /// <summary>Forgets the sign-in (the emulators are signed out at their next launch).</summary>
        public void SignOut()
        {
            lock (_lock)
            {
                Current.Username = null;
                Current.Token = null;
                Save();
            }
            Changed?.Invoke();
        }
    }
}
