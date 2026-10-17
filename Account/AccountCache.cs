using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RomM.Account
{
    /// <summary>What the server says of a signed-in user.</summary>
    public class ServerProfile
    {
        public string DisplayName;
        public byte[] Avatar;
    }

    /// <summary>An account that has signed in on this computer.</summary>
    public class CachedAccount
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Host { get; set; }
        public string Username { get; set; }        // the name typed (or "API key")
        public string DisplayName { get; set; }     // the server's name for the user, once known
        public bool UsesKey { get; set; }           // an API key rather than a username and password
        public string Secret { get; set; }          // the password or key, encrypted for this Windows user
        public bool Remember { get; set; }          // sign in by itself at start-up
        public DateTime LastSignInUtc { get; set; }
        public DateTime VerifiedUtc { get; set; }   // last time the server accepted it
        public string AvatarFile { get; set; }      // the user's RomM profile picture, kept here

        [JsonIgnore]
        public string Label => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : Username;

        [JsonIgnore]
        public string ServerLabel
        {
            get
            {
                try { return new Uri(Host).Host; }
                catch { return Host; }
            }
        }
    }

    /// <summary>
    /// The accounts that have signed in on this computer, as Steam keeps them: a tile each on the
    /// sign-in window, and the one last used with "Remember me" signs in by itself at start-up,
    /// with or without the server (offline). An account is only added after the server has
    /// accepted it. Passwords and keys are encrypted with Windows' data protection for the
    /// current Windows user (DPAPI), so the file is of no use copied elsewhere.
    /// </summary>
    public class AccountCache
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RomM.Playnite.Accounts");

        private readonly string _path;
        private readonly object _lock = new object();
        private List<CachedAccount> _items;

        public AccountCache(string path)
        {
            _path = path;
        }

        private List<CachedAccount> Items
        {
            get
            {
                if (_items != null)
                    return _items;
                try
                {
                    if (File.Exists(_path))
                        _items = JsonConvert.DeserializeObject<List<CachedAccount>>(File.ReadAllText(_path));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Accounts] Could not read the saved accounts: {ex.Message}");
                }
                return _items ?? (_items = new List<CachedAccount>());
            }
        }

        public bool Exists => File.Exists(_path);

        /// <summary>Every account, the most recently used first.</summary>
        public List<CachedAccount> All()
        {
            lock (_lock)
                return Items.OrderByDescending(a => a.LastSignInUtc).ToList();
        }

        /// <summary>The account to sign in by itself at start-up, if any.</summary>
        public CachedAccount AutoSignIn()
        {
            lock (_lock)
                return Items.Where(a => a.Remember && !string.IsNullOrEmpty(a.Secret)).OrderByDescending(a => a.LastSignInUtc).FirstOrDefault();
        }

        public CachedAccount Find(string host, string username)
        {
            lock (_lock)
                return Items.FirstOrDefault(a => Same(a, host, username));
        }

        private static bool Same(CachedAccount a, string host, string username)
        {
            return string.Equals((a.Host ?? "").TrimEnd('/'), (host ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Username ?? "", username ?? "", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Adds or updates an account the server has just accepted.</summary>
        public CachedAccount Remember(string host, string username, bool usesKey, string secret, bool remember)
        {
            lock (_lock)
            {
                CachedAccount account = Items.FirstOrDefault(a => Same(a, host, username));
                if (account == null)
                {
                    account = new CachedAccount { Host = host, Username = username };
                    Items.Add(account);
                }
                account.UsesKey = usesKey;
                // Without "Remember me" the password is not kept: the tile asks for it again.
                account.Secret = remember ? Protect(secret) : null;
                account.Remember = remember;
                account.LastSignInUtc = DateTime.UtcNow;
                account.VerifiedUtc = DateTime.UtcNow;
                // Only one account signs in by itself.
                if (remember)
                {
                    foreach (CachedAccount other in Items.Where(a => a != account))
                        other.Remember = false;
                }
                Save();
                return account;
            }
        }

        public void Touch(CachedAccount account, bool verified)
        {
            lock (_lock)
            {
                CachedAccount kept = Items.FirstOrDefault(a => a.Id == account.Id);
                if (kept == null)
                    return;
                kept.LastSignInUtc = DateTime.UtcNow;
                if (verified)
                    kept.VerifiedUtc = DateTime.UtcNow;
                Save();
            }
        }

        /// <summary>
        /// Keeps what the server says of the user: their name and their profile picture (saved
        /// beside the accounts file, so the tile shows it even offline).
        /// </summary>
        public void SetProfile(string host, string username, ServerProfile profile)
        {
            if (profile == null)
                return;
            lock (_lock)
            {
                CachedAccount kept = Items.FirstOrDefault(a => Same(a, host, username));
                if (kept == null)
                    return;
                if (!string.IsNullOrWhiteSpace(profile.DisplayName))
                    kept.DisplayName = profile.DisplayName;
                if (profile.Avatar != null && profile.Avatar.Length > 0)
                {
                    try
                    {
                        string folder = Path.Combine(Path.GetDirectoryName(_path), "Avatars");
                        Directory.CreateDirectory(folder);
                        string file = Path.Combine(folder, kept.Id + ".png");
                        File.WriteAllBytes(file, profile.Avatar);
                        kept.AvatarFile = file;
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Accounts] Could not keep the profile picture: {ex.Message}");
                    }
                }
                Save();
            }
        }

        public void SetDisplayName(string host, string username, string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return;
            lock (_lock)
            {
                CachedAccount kept = Items.FirstOrDefault(a => Same(a, host, username));
                if (kept == null || kept.DisplayName == displayName)
                    return;
                kept.DisplayName = displayName;
                Save();
            }
        }

        /// <summary>Stops an account signing in by itself (signing out), keeping its tile.</summary>
        public void Forget(string host, string username, bool removeTile)
        {
            lock (_lock)
            {
                if (removeTile)
                {
                    Items.RemoveAll(a => Same(a, host, username));
                }
                else
                {
                    foreach (CachedAccount a in Items.Where(x => Same(x, host, username)))
                        a.Remember = false;
                }
                Save();
            }
        }

        public void Remove(Guid id)
        {
            lock (_lock)
            {
                Items.RemoveAll(a => a.Id == id);
                Save();
            }
        }

        public string SecretOf(CachedAccount account)
        {
            return Unprotect(account?.Secret);
        }

        // Called with the lock held.
        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(_items, Formatting.Indented));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Accounts] Could not keep the saved accounts: {ex.Message}");
            }
        }

        private static string Protect(string secret)
        {
            if (string.IsNullOrEmpty(secret))
                return null;
            try
            {
                byte[] sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(sealedBytes);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Accounts] Could not encrypt a saved sign-in: {ex.Message}");
                return null;
            }
        }

        private static string Unprotect(string sealedText)
        {
            if (string.IsNullOrEmpty(sealedText))
                return null;
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(sealedText), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Accounts] Could not decrypt a saved sign-in: {ex.Message}");
                return null;
            }
        }
    }
}
