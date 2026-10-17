using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Account
{
    /// <summary>
    /// Which RomM games each account has in its library, kept on this computer (for now; a
    /// later step may keep it on the server). Playnite has one library for everyone, so on
    /// switching accounts the RomM games the new account does not have are hidden, and the ones
    /// it has that were hidden this way are shown again. Only games this hid are ever shown
    /// again: a game the user hid stays hidden. Games from other libraries (Steam, ...) are
    /// everyone's and are never touched.
    ///
    /// The first account seen keeps the library as it is; an account new to this computer
    /// starts with no RomM games.
    /// </summary>
    public class AccountLibraries
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private sealed class State
        {
            public string Active { get; set; }
            public Dictionary<string, List<Guid>> Owned { get; set; } = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<Guid> HiddenBySwitch { get; set; } = new HashSet<Guid>();
        }

        /// <summary>
        /// Whether RomMnite hid a game for a reason of its own (a lesser copy of a game on several
        /// platforms, Games/LibraryVersions): hidden, but not by the user, so another account's
        /// switch may take it over and give it back.
        /// </summary>
        public static Func<Guid, bool> HiddenElsewhere;

        private readonly string _path;
        private readonly object _lock = new object();
        private State _state;

        public AccountLibraries(string path)
        {
            _path = path;
        }

        /// <summary>The key an account is known by: its server and name.</summary>
        public static string KeyOf(string host, string username)
        {
            return ((host ?? "").Trim().TrimEnd('/') + "|" + (username ?? "").Trim()).ToLowerInvariant();
        }

        private State Current
        {
            get
            {
                if (_state != null)
                    return _state;
                try
                {
                    if (File.Exists(_path))
                        _state = JsonConvert.DeserializeObject<State>(File.ReadAllText(_path));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Accounts] Could not read the accounts' libraries: {ex.Message}");
                }
                if (_state != null)
                {
                    // JSON gives back a case-sensitive dictionary; keys are lower case anyway.
                    _state.Owned = new Dictionary<string, List<Guid>>(_state.Owned ?? new Dictionary<string, List<Guid>>(), StringComparer.OrdinalIgnoreCase);
                    _state.HiddenBySwitch = _state.HiddenBySwitch ?? new HashSet<Guid>();
                }
                return _state ?? (_state = new State());
            }
        }

        /// <summary>Whether a game is hidden only because the signed-in account does not have it.</summary>
        public bool HiddenForAccount(Guid gameId)
        {
            lock (_lock)
                return Current.HiddenBySwitch.Contains(gameId);
        }

        /// <summary>
        /// Puts the account's library in place: the leaving account's RomM games are noted as
        /// its own, the arriving account's are shown and the rest hidden. Nothing changes when
        /// the account is the one already in place. Returns the account that left, or null when
        /// none did (the first account, or the same one again).
        /// </summary>
        public string Switch(IPlayniteAPI api, Guid pluginId, string accountKey)
        {
            if (api == null || string.IsNullOrEmpty(accountKey))
                return null;
            lock (_lock)
            {
                State state = Current;
                List<Game> romm = api.Database.Games.Where(g => g.PluginId == pluginId).ToList();
                Func<List<Guid>> visibleNow = () => romm.Where(g => !state.HiddenBySwitch.Contains(g.Id)).Select(g => g.Id).ToList();

                if (string.IsNullOrEmpty(state.Active))
                {
                    // The first account seen: the library as it is is its own.
                    if (!state.Owned.ContainsKey(accountKey))
                        state.Owned[accountKey] = visibleNow();
                    state.Active = accountKey;
                    Save();
                    return null;
                }
                if (string.Equals(state.Active, accountKey, StringComparison.OrdinalIgnoreCase))
                    return null;
                string leaving = state.Active;

                // The leaving account keeps what it has now (games added since included).
                state.Owned[state.Active] = visibleNow();

                var owned = new HashSet<Guid>(state.Owned.TryGetValue(accountKey, out List<Guid> mine) ? mine : new List<Guid>());
                var changed = new List<Game>();
                foreach (Game game in romm)
                {
                    bool has = owned.Contains(game.Id);
                    if (!has && (!game.Hidden || HiddenElsewhere?.Invoke(game.Id) == true))
                    {
                        game.Hidden = true;
                        state.HiddenBySwitch.Add(game.Id);
                        changed.Add(game);
                    }
                    else if (has && state.HiddenBySwitch.Contains(game.Id))
                    {
                        game.Hidden = false;
                        state.HiddenBySwitch.Remove(game.Id);
                        changed.Add(game);
                    }
                }
                state.Owned[accountKey] = owned.ToList();
                state.Active = accountKey;
                Save();

                if (changed.Count > 0)
                {
                    try
                    {
                        using (api.Database.BufferedUpdate())
                        {
                            foreach (Game game in changed)
                                api.Database.Games.Update(game);
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Accounts] Could not switch the library to the account: {ex.Message}");
                    }
                }
                s_log.Info($"[Accounts] Switched the library to {accountKey}: {owned.Count} RomM games, {changed.Count} shown or hidden.");
                return leaving;
            }
        }

        /// <summary>
        /// A game already in Playnite (another account's) goes into the signed-in account's
        /// library too: shown again if it was hidden for this account. On the UI thread.
        /// </summary>
        public void Own(IPlayniteAPI api, Guid gameId)
        {
            lock (_lock)
            {
                State state = Current;
                Game game = api.Database.Games.Get(gameId);
                if (game == null)
                    return;
                if (state.HiddenBySwitch.Remove(gameId) && game.Hidden)
                {
                    game.Hidden = false;
                    api.Database.Games.Update(game);
                }
                if (!string.IsNullOrEmpty(state.Active))
                {
                    if (!state.Owned.TryGetValue(state.Active, out List<Guid> mine))
                        state.Owned[state.Active] = mine = new List<Guid>();
                    if (!mine.Contains(gameId))
                        mine.Add(gameId);
                }
                Save();
            }
        }

        /// <summary>How many accounts have used this computer's library.</summary>
        public int KnownAccounts
        {
            get
            {
                lock (_lock)
                {
                    State state = Current;
                    var keys = new HashSet<string>(state.Owned.Keys, StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(state.Active))
                        keys.Add(state.Active);
                    return keys.Count;
                }
            }
        }

        /// <summary>The accounts on this computer that have the game in their library.</summary>
        public List<string> AccountsWith(Guid gameId)
        {
            lock (_lock)
                return Current.Owned.Where(kv => kv.Value != null && kv.Value.Contains(gameId)).Select(kv => kv.Key).ToList();
        }

        /// <summary>Whether another account on this computer has the game in its library.</summary>
        public bool OthersHave(Guid gameId)
        {
            lock (_lock)
            {
                State state = Current;
                return state.Owned.Any(kv => !string.Equals(kv.Key, state.Active, StringComparison.OrdinalIgnoreCase) && kv.Value != null && kv.Value.Contains(gameId));
            }
        }

        /// <summary>
        /// Takes a game out of the signed-in account's library. When another account on this
        /// computer has it, it stays in Playnite, hidden for this one, and this returns true;
        /// when nobody else does, this returns false and the caller removes it from Playnite.
        /// On the UI thread.
        /// </summary>
        public bool Disown(IPlayniteAPI api, Guid gameId)
        {
            lock (_lock)
            {
                State state = Current;
                if (!string.IsNullOrEmpty(state.Active) && state.Owned.TryGetValue(state.Active, out List<Guid> mine))
                    mine.Remove(gameId);
                bool others = state.Owned.Any(kv => !string.Equals(kv.Key, state.Active, StringComparison.OrdinalIgnoreCase) && kv.Value != null && kv.Value.Contains(gameId));
                if (!others)
                {
                    Save();
                    return false;
                }
                Game game = api.Database.Games.Get(gameId);
                if (game != null && (!game.Hidden || HiddenElsewhere?.Invoke(game.Id) == true))
                {
                    game.Hidden = true;
                    state.HiddenBySwitch.Add(gameId);
                    api.Database.Games.Update(game);
                }
                Save();
                return true;
            }
        }

        /// <summary>The signed-in account's RomM games now: those not hidden for it.</summary>
        public HashSet<Guid> OwnedNow(IPlayniteAPI api, Guid pluginId)
        {
            lock (_lock)
                return new HashSet<Guid>(api.Database.Games.Where(g => g.PluginId == pluginId && !Current.HiddenBySwitch.Contains(g.Id)).Select(g => g.Id));
        }

        /// <summary>
        /// Makes the signed-in account's RomM games exactly these (from a sync with the server):
        /// the others hidden, these shown if this had hidden them. On the UI thread.
        /// </summary>
        public void ApplyOwned(IPlayniteAPI api, Guid pluginId, string accountKey, HashSet<Guid> owned)
        {
            lock (_lock)
            {
                State state = Current;
                var changed = new List<Game>();
                foreach (Game game in api.Database.Games.Where(g => g.PluginId == pluginId).ToList())
                {
                    bool has = owned.Contains(game.Id);
                    if (!has && (!game.Hidden || HiddenElsewhere?.Invoke(game.Id) == true))
                    {
                        game.Hidden = true;
                        state.HiddenBySwitch.Add(game.Id);
                        changed.Add(game);
                    }
                    else if (has && state.HiddenBySwitch.Contains(game.Id))
                    {
                        game.Hidden = false;
                        state.HiddenBySwitch.Remove(game.Id);
                        changed.Add(game);
                    }
                }
                state.Owned[accountKey] = owned.ToList();
                Save();
                if (changed.Count == 0)
                    return;
                try
                {
                    using (api.Database.BufferedUpdate())
                    {
                        foreach (Game game in changed)
                            api.Database.Games.Update(game);
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Accounts] Could not apply the account's library: {ex.Message}");
                }
            }
        }

        // Called with the lock held.
        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Accounts] Could not keep the accounts' libraries: {ex.Message}");
            }
        }
    }
}
