using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Models.RomM.Collection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;

namespace RomM.Games
{
    /// <summary>
    /// Keeps the signed-in account's library and collections in step with its RomM server,
    /// through RomM's own collections (private to the user, and visible in RomM's web pages):
    ///
    ///   - the library (which RomM games the account has) is the collection "Playnite Library";
    ///   - each of the user's collections here is a RomM collection of the same name, and RomM
    ///     collections made on the server (by the user, not Favorites) appear here.
    ///
    /// Each sync is a three-way merge against what the last sync agreed: what was added or
    /// removed here since is applied to the server, and what was added or removed there is
    /// applied here, so neither side's changes are lost and nothing is undone. The first sync
    /// of anything joins both sides. Games from other libraries (Steam, ...) stay in collections
    /// here only: the server only knows RomM games. Without the server (offline), nothing
    /// happens and the next sync catches up.
    /// </summary>
    public class CollectionSync
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        public const string LibraryCollectionName = "Playnite Library";

        private sealed class SyncState
        {
            public int LibraryCollectionId { get; set; }
            public List<int> LibraryLast { get; set; }                          // null: never synced
            public List<int> FavoritesLast { get; set; }                        // null: never synced
            public Dictionary<Guid, int> ServerIds { get; set; } = new Dictionary<Guid, int>();
            public Dictionary<Guid, List<int>> CollectionLast { get; set; } = new Dictionary<Guid, List<int>>();
            public List<int> PendingDeletes { get; set; } = new List<int>();
            public List<int> RemovedHere { get; set; } = new List<int>();       // RomM games deleted from Playnite since
        }

        private readonly RomM _plugin;
        private readonly SemaphoreSlim _running = new SemaphoreSlim(1);
        private readonly object _stateLock = new object();
        private string _statePath;
        private string _accountKey;

        private volatile bool _applying;

        /// <summary>True while a sync is changing collections here (their changes are its own).</summary>
        public bool Applying => _applying;

        public CollectionSync(RomM plugin)
        {
            _plugin = plugin;
        }

        /// <summary>The account whose state is used (its own file).</summary>
        public void UseAccount(string accountKey, string statePath)
        {
            _accountKey = accountKey;
            _statePath = statePath;
        }

        private static SyncState Load(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    SyncState read = JsonConvert.DeserializeObject<SyncState>(File.ReadAllText(path)) ?? new SyncState();
                    read.ServerIds = read.ServerIds ?? new Dictionary<Guid, int>();
                    read.CollectionLast = read.CollectionLast ?? new Dictionary<Guid, List<int>>();
                    read.PendingDeletes = read.PendingDeletes ?? new List<int>();
                    read.RemovedHere = read.RemovedHere ?? new List<int>();
                    return read;
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sync] Could not read the sync state: {ex.Message}");
            }
            return new SyncState();
        }

        private static void Save(string path, SyncState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sync] Could not keep the sync state: {ex.Message}");
            }
        }

        /// <summary>A collection deleted here: deleted on the server at the next sync.</summary>
        public void NoteDeleted(Guid collectionId)
        {
            string path = _statePath;
            if (path == null)
                return;
            lock (_stateLock)
            {
                SyncState state = Load(path);
                if (state.ServerIds.TryGetValue(collectionId, out int serverId))
                {
                    state.ServerIds.Remove(collectionId);
                    state.CollectionLast.Remove(collectionId);
                    if (!state.PendingDeletes.Contains(serverId))
                        state.PendingDeletes.Add(serverId);
                    Save(path, state);
                }
            }
        }

        /// <summary>
        /// RomM games deleted from Playnite that the server's library still lists until the next
        /// sync: not to be brought back in by a library update meanwhile.
        /// </summary>
        public HashSet<int> RemovedSinceSync()
        {
            string path = _statePath;
            if (path == null)
                return new HashSet<int>();
            lock (_stateLock)
            {
                return new HashSet<int>(Load(path).RemovedHere);
            }
        }

        /// <summary>
        /// A RomM game deleted from Playnite: out of the library (and collections) on the server
        /// at the next sync. Games the server has that were never imported here are otherwise
        /// left as they are, since their absence here says nothing.
        /// </summary>
        public void NoteGameRemoved(int romId)
        {
            string path = _statePath;
            if (path == null)
                return;
            lock (_stateLock)
            {
                SyncState state = Load(path);
                if (!state.RemovedHere.Contains(romId))
                {
                    state.RemovedHere.Add(romId);
                    Save(path, state);
                }
            }
        }

        // What this side holds, for the merge: the RomM games here that are in it, and those of
        // the last agreed set that are not here at all (never imported), unless deleted here.
        private static HashSet<int> HereSide(IEnumerable<int> seen, List<int> last, Dictionary<int, Guid> local, HashSet<int> removed)
        {
            var here = new HashSet<int>(seen);
            if (last != null)
            {
                foreach (int id in last)
                {
                    if (!local.ContainsKey(id) && !removed.Contains(id))
                        here.Add(id);
                }
            }
            return here;
        }

        // The three-way merge: last agreed, and each side now.
        private static HashSet<int> Merge(List<int> last, HashSet<int> here, HashSet<int> there)
        {
            if (last == null)
                return new HashSet<int>(here.Union(there));
            var before = new HashSet<int>(last);
            var result = new HashSet<int>(before);
            foreach (int id in here.Except(before)) result.Add(id);
            foreach (int id in there.Except(before)) result.Add(id);
            foreach (int id in before.Except(here)) result.Remove(id);
            foreach (int id in before.Except(there)) result.Remove(id);
            return result;
        }

        /// <summary>One sync, on a background thread. Returns quietly when the server is out of reach.</summary>
        public void Run()
        {
            if (_statePath == null || !_running.Wait(0))
                return;
            try
            {
                RunCore();
            }
            catch (HttpRequestException ex)
            {
                s_log.Info($"[Sync] The server could not be reached; collections will sync later ({ex.Message}).");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sync] Collections did not sync: {ex.Message}");
            }
            finally
            {
                _running.Release();
            }
        }

        private void RunCore()
        {
            string host = _plugin.Settings?.RomMHost?.TrimEnd('/');
            if (string.IsNullOrEmpty(host))
                return;
            IPlayniteAPI api = _plugin.Playnite;
            // The account at the start; a sign-in to another meanwhile ends this sync unapplied.
            string statePath = _statePath;
            string accountKey = _accountKey;
            Func<bool> stillSame = () => statePath == _statePath && accountKey == _accountKey;
            SyncState state;
            lock (_stateLock)
                state = Load(statePath);
            var removed = new HashSet<int>(state.RemovedHere);

            // Who the server says we are, and our collections there.
            int me = (int?)Json(host, "/api/users/me")?["id"] ?? -1;
            var all = (Json(host, "/api/collections") as JArray ?? new JArray()).ToObject<List<RomMCollection>>() ?? new List<RomMCollection>();
            List<RomMCollection> mine = all.Where(c => !c.IsFavorite && (c.UserId == null || c.UserId == me)).ToList();

            // RomM games here, by server id.
            var gameByRom = new Dictionary<int, Guid>();
            var romByGame = new Dictionary<Guid, int>();
            foreach (Game game in api.Database.Games.Where(g => g.PluginId == _plugin.Id).ToList())
            {
                if (RomMGameId.TryParse(game.GameId, out int romId, out string _))
                {
                    gameByRom[romId] = game.Id;
                    romByGame[game.Id] = romId;
                }
            }

            // Deletions made here.
            foreach (int serverId in state.PendingDeletes.ToList())
            {
                if (mine.Any(c => c.Id == serverId))
                    Send(HttpMethod.Delete, host, $"/api/collections/{serverId}", null);
                state.PendingDeletes.Remove(serverId);
                mine.RemoveAll(c => c.Id == serverId);
            }

            // ---- The library ----------------------------------------------------------------------
            RomMCollection library = mine.FirstOrDefault(c => c.Id == state.LibraryCollectionId)
                ?? mine.FirstOrDefault(c => string.Equals(c.Name, LibraryCollectionName, StringComparison.OrdinalIgnoreCase));
            HashSet<Guid> ownedNow = null;
            api.MainView.UIDispatcher.Invoke(() => ownedNow = _plugin.Libraries.OwnedNow(api, _plugin.Id));
            var seenLib = new HashSet<int>(ownedNow.Where(romByGame.ContainsKey).Select(g => romByGame[g]));
            HashSet<int> hereLib = HereSide(seenLib, state.LibraryLast, gameByRom, removed);
            if (library == null)
            {
                library = Create(host, LibraryCollectionName, "The games in this account's Playnite library, kept by the RomM extension for Playnite.");
                state.LibraryLast = null;
            }
            var thereLib = new HashSet<int>(library.RomIds ?? new List<int>());
            HashSet<int> lib = Merge(state.LibraryLast, hereLib, thereLib);
            if (!stillSame())
                return;
            if (!lib.SetEquals(thereLib))
                Put(host, library.Id, null, lib);
            // Server games not imported here yet stay in the set; they show once imported.
            var owned = new HashSet<Guid>(lib.Where(gameByRom.ContainsKey).Select(r => gameByRom[r]));
            owned.UnionWith(ownedNow.Where(g => !romByGame.ContainsKey(g)));   // ids it cannot read: left as they are
            if (!owned.SetEquals(ownedNow))
                api.MainView.UIDispatcher.Invoke(() =>
                {
                    if (stillSame())
                        _plugin.Libraries.ApplyOwned(api, _plugin.Id, accountKey, owned);
                });
            state.LibraryCollectionId = library.Id;
            state.LibraryLast = lib.ToList();
            mine.Remove(library);

            // ---- Favorites (RomM keeps them as a collection of its own) ------------------------------
            if (_plugin.Settings.KeepRomMSynced)
            {
                RomMCollection favorites = all.FirstOrDefault(c => c.IsFavorite && (c.UserId == null || c.UserId == me));
                var seenFav = new HashSet<int>();
                api.MainView.UIDispatcher.Invoke(() =>
                {
                    foreach (Game game in api.Database.Games.Where(g => g.PluginId == _plugin.Id && g.Favorite))
                    {
                        if (romByGame.TryGetValue(game.Id, out int romId))
                            seenFav.Add(romId);
                    }
                });
                HashSet<int> hereFav = HereSide(seenFav, state.FavoritesLast, gameByRom, removed);
                if (favorites == null)
                {
                    favorites = Create(host, "Favorites", null, "is_favorite=true&is_public=false");
                    state.FavoritesLast = null;
                }
                var thereFav = new HashSet<int>(favorites.RomIds ?? new List<int>());
                HashSet<int> fav = Merge(state.FavoritesLast, hereFav, thereFav);
                if (!stillSame())
                    return;
                if (!fav.SetEquals(thereFav))
                    Put(host, favorites.Id, null, fav);
                var favGames = new HashSet<Guid>(fav.Where(gameByRom.ContainsKey).Select(r => gameByRom[r]));
                api.MainView.UIDispatcher.Invoke(() =>
                {
                    if (!stillSame())
                        return;
                    var changed = api.Database.Games
                        .Where(g => g.PluginId == _plugin.Id && romByGame.ContainsKey(g.Id) && g.Favorite != favGames.Contains(g.Id))
                        .ToList();
                    if (changed.Count == 0)
                        return;
                    using (api.Database.BufferedUpdate())
                    {
                        foreach (Game game in changed)
                        {
                            game.Favorite = favGames.Contains(game.Id);
                            _plugin.SuppressSync(game.Id);
                            api.Database.Games.Update(game);
                        }
                    }
                });
                state.FavoritesLast = fav.ToList();
            }
            mine.RemoveAll(c => c.IsFavorite);

            // ---- The user's collections ---------------------------------------------------------------
            GameCollections local = _plugin.Collections;
            _applying = true;
            try
            {
                foreach (GameCollection collection in local.All())
                {
                    if (!stillSame())
                        return;
                    var seenIds = new HashSet<int>(collection.Games.Where(romByGame.ContainsKey).Select(g => romByGame[g]));
                    RomMCollection server = null;
                    if (state.ServerIds.TryGetValue(collection.Id, out int serverId))
                    {
                        server = mine.FirstOrDefault(c => c.Id == serverId);
                        if (server == null)
                        {
                            // Deleted on the server since the last sync: deleted here too.
                            state.ServerIds.Remove(collection.Id);
                            state.CollectionLast.Remove(collection.Id);
                            Guid gone = collection.Id;
                            api.MainView.UIDispatcher.Invoke(() => local.Delete(gone));
                            continue;
                        }
                    }
                    else
                    {
                        // Never synced: one of the same name on the server is the same collection.
                        server = mine.FirstOrDefault(c => string.Equals(c.Name, collection.Name, StringComparison.OrdinalIgnoreCase) && !state.ServerIds.ContainsValue(c.Id));
                        if (server == null)
                            server = Create(host, collection.Name, null);
                        state.CollectionLast.Remove(collection.Id);
                    }

                    var thereIds = new HashSet<int>(server.RomIds ?? new List<int>());
                    state.CollectionLast.TryGetValue(collection.Id, out List<int> last);
                    HashSet<int> hereIds = HereSide(seenIds, last, gameByRom, removed);
                    HashSet<int> merged = Merge(last, hereIds, thereIds);

                    if (!merged.SetEquals(thereIds) || !string.Equals(server.Name, collection.Name, StringComparison.Ordinal))
                        Put(host, server.Id, collection.Name, merged);
                    if (!new HashSet<int>(merged.Where(gameByRom.ContainsKey)).SetEquals(seenIds))
                    {
                        // Its other games (not RomM's) stay; its RomM games become the merged ones.
                        var games = collection.Games.Where(g => !romByGame.ContainsKey(g)).ToList();
                        games.AddRange(merged.Where(gameByRom.ContainsKey).Select(r => gameByRom[r]));
                        Guid id = collection.Id;
                        api.MainView.UIDispatcher.Invoke(() => local.SetGames(id, games));
                    }
                    state.ServerIds[collection.Id] = server.Id;
                    state.CollectionLast[collection.Id] = merged.ToList();
                    mine.Remove(server);
                }

                // Made on the server (in RomM's pages, or on another computer): here too.
                foreach (RomMCollection server in mine.Where(c => !state.ServerIds.ContainsValue(c.Id)).ToList())
                {
                    var ids = server.RomIds ?? new List<int>();
                    var games = ids.Where(gameByRom.ContainsKey).Select(r => gameByRom[r]).ToList();
                    GameCollection made = null;
                    string name = server.Name;
                    api.MainView.UIDispatcher.Invoke(() => made = local.Create(name, games));
                    if (made == null)
                        continue;
                    state.ServerIds[made.Id] = server.Id;
                    state.CollectionLast[made.Id] = ids.ToList();
                }
            }
            finally
            {
                _applying = false;
            }

            if (!stillSame())
                return;
            state.RemovedHere.Clear();
            lock (_stateLock)
            {
                // Collections deleted here during the sync are kept as pending.
                SyncState now = Load(statePath);
                foreach (int pending in now.PendingDeletes)
                {
                    if (!state.PendingDeletes.Contains(pending))
                        state.PendingDeletes.Add(pending);
                    state.ServerIds = state.ServerIds.Where(p => p.Value != pending).ToDictionary(p => p.Key, p => p.Value);
                }
                foreach (int romId in now.RemovedHere.Except(removed))
                    state.RemovedHere.Add(romId);
                Save(statePath, state);
            }
            s_log.Info($"[Sync] Synced with the server: {lib.Count} games in the library, {state.ServerIds.Count} collections.");
        }

        // ---- The server -------------------------------------------------------------------------------

        private static JToken Json(string host, string path)
        {
            using (var response = HttpClientSingleton.Instance.GetAsync(host + path).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                return JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        }

        private static RomMCollection Create(string host, string name, string description, string query = "is_public=false")
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(name), "name");
            if (!string.IsNullOrEmpty(description))
                form.Add(new StringContent(description), "description");
            using (var response = HttpClientSingleton.Instance.PostAsync(host + "/api/collections?" + query, form).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                var made = JsonConvert.DeserializeObject<RomMCollection>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                made.RomIds = made.RomIds ?? new List<int>();
                return made;
            }
        }

        private static void Put(string host, int id, string name, IEnumerable<int> romIds)
        {
            var form = new MultipartFormDataContent();
            if (!string.IsNullOrEmpty(name))
                form.Add(new StringContent(name), "name");
            form.Add(new StringContent(JsonConvert.SerializeObject(romIds.OrderBy(r => r).ToList())), "rom_ids");
            using (var response = HttpClientSingleton.Instance.PutAsync($"{host}/api/collections/{id}?is_public=false", form).GetAwaiter().GetResult())
                response.EnsureSuccessStatusCode();
        }

        private static void Send(HttpMethod method, string host, string path, HttpContent content)
        {
            using (var request = new HttpRequestMessage(method, host + path) { Content = content })
            using (var response = HttpClientSingleton.Instance.SendAsync(request).GetAwaiter().GetResult())
                response.EnsureSuccessStatusCode();
        }
    }
}
