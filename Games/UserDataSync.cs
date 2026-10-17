using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace RomM.Games
{
    /// <summary>
    /// Keeps what the signed-in account has made of its RomM games in step with the server,
    /// where RomM keeps them per user:
    ///
    ///   - completion status and score: the game's user properties on the server ("rom_user");
    ///   - notes: a private RomM note titled "Playnite" on the game;
    ///   - play time: each session played here is sent to RomM's play sessions; the server's
    ///     total (other computers and devices included) raises the play time here, never lowers
    ///     it, and its last played date moves the one here forward.
    ///
    /// Status, score and notes merge three ways against what the last sync agreed: a change on
    /// one side goes to the other; a change on both keeps this computer's. Reading the whole
    /// library from the server is slow, so it happens on signing in and every few hours (a
    /// "full" sync); in between, changes made here are sent on their own. Favorites go with the
    /// collections (RomM keeps them as one). Offline, nothing happens and sessions wait.
    /// </summary>
    public class UserDataSync
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        public const string NoteTitle = "Playnite";
        private const string NotPlayed = "Not Played";
        private static readonly TimeSpan FullEvery = TimeSpan.FromHours(3);
        private const int MaxQueuedSessions = 2000;

        public sealed class Agreed
        {
            public string Status { get; set; }   // the Playnite status name; null: not synced
            public int Rating { get; set; }      // 0-10, as RomM keeps it
            public string Notes { get; set; }    // null: not synced
        }

        public sealed class PendingSession
        {
            public int RomId { get; set; }
            public DateTime StartUtc { get; set; }
            public DateTime EndUtc { get; set; }
            public long DurationMs { get; set; }
        }

        private sealed class SyncState
        {
            public Dictionary<int, Agreed> Agreed { get; set; } = new Dictionary<int, Agreed>();
            public Dictionary<int, int> NoteIds { get; set; } = new Dictionary<int, int>();
            public List<PendingSession> Sessions { get; set; } = new List<PendingSession>();
            public DateTime LastFullUtc { get; set; }
        }

        // A game here, as read on the UI thread.
        private sealed class Local
        {
            public Guid GameId;
            public string Status;      // null: a status RomM has no equivalent for
            public int Rating;
            public string Notes;
            public ulong Playtime;
            public DateTime? LastActivity;
        }

        // A game on the server.
        private sealed class Remote
        {
            public string Status;
            public int Rating;
            public DateTime? LastPlayed;
            public bool? HasNotes;     // null: the server does not say (older RomM)
        }

        private readonly RomM _plugin;
        private readonly SemaphoreSlim _running = new SemaphoreSlim(1);
        private readonly object _stateLock = new object();
        private string _statePath;
        private string _accountKey;
        private bool _fullDue = true;

        public UserDataSync(RomM plugin)
        {
            _plugin = plugin;
        }

        /// <summary>The account whose state is used (its own file); its first sync reads everything.</summary>
        public void UseAccount(string accountKey, string statePath)
        {
            _fullDue = true;
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
                    read.Agreed = read.Agreed ?? new Dictionary<int, Agreed>();
                    read.NoteIds = read.NoteIds ?? new Dictionary<int, int>();
                    read.Sessions = read.Sessions ?? new List<PendingSession>();
                    return read;
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sync] Could not read the game data sync state: {ex.Message}");
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
                s_log.Warn($"[Sync] Could not keep the game data sync state: {ex.Message}");
            }
        }

        /// <summary>A session just played here, to send to the server at the next sync.</summary>
        public void QueueSession(int romId, DateTime endUtc, ulong seconds)
        {
            string path = _statePath;
            if (path == null || seconds == 0)
                return;
            lock (_stateLock)
            {
                SyncState state = Load(path);
                state.Sessions.Add(new PendingSession
                {
                    RomId = romId,
                    StartUtc = endUtc.AddSeconds(-(double)seconds),
                    EndUtc = endUtc,
                    DurationMs = (long)seconds * 1000,
                });
                if (state.Sessions.Count > MaxQueuedSessions)
                    state.Sessions.RemoveRange(0, state.Sessions.Count - MaxQueuedSessions);
                Save(path, state);
            }
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
                s_log.Info($"[Sync] The server could not be reached; game data will sync later ({ex.Message}).");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Sync] Game data did not sync: {ex.Message}");
            }
            finally
            {
                _running.Release();
            }
        }

        private void RunCore()
        {
            if (_plugin.Settings == null || !_plugin.Settings.KeepRomMSynced)
                return;
            string host = _plugin.Settings.RomMHost?.TrimEnd('/');
            if (string.IsNullOrEmpty(host))
                return;
            IPlayniteAPI api = _plugin.Playnite;
            string statePath = _statePath;
            string accountKey = _accountKey;
            Func<bool> stillSame = () => statePath == _statePath && accountKey == _accountKey;

            SyncState state;
            lock (_stateLock)
                state = Load(statePath);

            // ---- Sessions played here ---------------------------------------------------------------
            List<PendingSession> sent = SendSessions(host, state.Sessions);

            // ---- The games here -------------------------------------------------------------------------
            var locals = new Dictionary<int, Local>();
            api.MainView.UIDispatcher.Invoke(() =>
            {
                foreach (Game game in api.Database.Games.Where(g => g.PluginId == _plugin.Id))
                {
                    if (!RomMGameId.TryParse(game.GameId, out int romId, out string _) || locals.ContainsKey(romId))
                        continue;
                    string statusName = game.CompletionStatusId == Guid.Empty ? null : api.Database.CompletionStatuses.Get(game.CompletionStatusId)?.Name;
                    locals[romId] = new Local
                    {
                        GameId = game.Id,
                        Status = ToSyncedStatus(statusName),
                        Rating = game.UserScore.HasValue ? Math.Max(0, Math.Min(10, (int)Math.Round(game.UserScore.Value / 10.0))) : 0,
                        Notes = game.Notes ?? "",
                        Playtime = game.Playtime,
                        LastActivity = game.LastActivity,
                    };
                }
            });

            bool full = _fullDue || DateTime.UtcNow - state.LastFullUtc > FullEvery;
            Dictionary<int, Remote> remote = null;
            Dictionary<int, KeyValuePair<ulong, DateTime>> played = null;
            int me = -1;
            if (full)
            {
                me = (int?)GetJson(host, "/api/users/me")?["id"] ?? -1;
                remote = FetchRomUsers(host);
                played = FetchPlayed(host);
            }
            if (!stillSame())
                return;

            var changes = new Dictionary<Guid, Action<Game>>();
            int pushed = 0, failed = 0;
            foreach (KeyValuePair<int, Local> pair in locals)
            {
                int romId = pair.Key;
                Local here = pair.Value;
                state.Agreed.TryGetValue(romId, out Agreed agreed);
                Remote there = null;
                if (full && !remote.TryGetValue(romId, out there))
                    continue;   // not on the server (any more), or hidden there
                if (!full && agreed == null)
                    continue;   // never synced: waits for a full sync, which knows the server's side
                try
                {
                    Action<Game> change = SyncGame(host, me, romId, here, there, played, agreed, state, api, out bool sentProps);
                    if (sentProps)
                        pushed++;
                    if (change != null)
                        changes[here.GameId] = change;
                }
                catch (HttpRequestException ex) when (ex.InnerException == null)
                {
                    // The server refused this game (not unreachable): the others go on.
                    failed++;
                    s_log.Warn($"[Sync] Game data for RomM game {romId} did not sync: {ex.Message}");
                }
            }

            if (!stillSame())
                return;
            if (changes.Count > 0)
            {
                api.MainView.UIDispatcher.Invoke(() =>
                {
                    if (!stillSame())
                        return;
                    using (api.Database.BufferedUpdate())
                    {
                        foreach (KeyValuePair<Guid, Action<Game>> change in changes)
                        {
                            Game game = api.Database.Games.Get(change.Key);
                            if (game == null)
                                continue;
                            change.Value(game);
                            _plugin.SuppressSync(game.Id);
                            api.Database.Games.Update(game);
                        }
                    }
                });
            }

            lock (_stateLock)
            {
                // Sessions queued while this ran stay queued.
                SyncState now = Load(statePath);
                state.Sessions = now.Sessions.Where(s => !sent.Any(x => x.RomId == s.RomId && x.StartUtc == s.StartUtc)).ToList();
                if (full)
                {
                    state.LastFullUtc = DateTime.UtcNow;
                    _fullDue = false;
                }
                Save(statePath, state);
            }
            s_log.Info($"[Sync] Game data synced ({(full ? "full" : "changes only")}): {pushed} sent, {changes.Count} updated here, {sent.Count} sessions sent{(failed > 0 ? $", {failed} failed" : "")}.");
        }

        // One game's status, score, notes and play time; what to change on the game here, or null.
        private Action<Game> SyncGame(string host, int me, int romId, Local here, Remote there,
            Dictionary<int, KeyValuePair<ulong, DateTime>> played, Agreed agreed, SyncState state, IPlayniteAPI api, out bool sentProps)
        {
            // Status and score.
            bool statusSynced = here.Status != null;
            string status = here.Status;
            int rating = here.Rating;
            bool push;
            if (there != null)
            {
                if (statusSynced)
                    status = Pick(agreed?.Status != null, agreed?.Status, here.Status, there.Status, NotPlayed);
                rating = Pick(agreed != null, agreed?.Rating ?? 0, here.Rating, there.Rating, 0);
                push = (statusSynced && status != there.Status) || rating != there.Rating;
            }
            else
            {
                push = (statusSynced && status != agreed.Status) || rating != agreed.Rating;
            }
            if (push)
                PutProps(host, romId, statusSynced ? status : null, rating);
            sentProps = push;

            // Kept only once all of this game is done, so a failure part-way is retried as a whole.
            var next = new Agreed { Status = agreed?.Status, Rating = agreed?.Rating ?? 0, Notes = agreed?.Notes };
            if (statusSynced)
                next.Status = status;
            next.Rating = rating;

            var change = new List<Action<Game>>();
            if (statusSynced && status != here.Status)
            {
                string name = status;
                change.Add(g => g.CompletionStatusId = api.Database.CompletionStatuses.FirstOrDefault(c => c.Name == name)?.Id ?? g.CompletionStatusId);
            }
            if (rating != here.Rating)
            {
                int score = rating;
                change.Add(g => g.UserScore = score == 0 ? (int?)null : score * 10);
            }

            // Notes: a full sync reads the server's; in between, a change here is sent.
            string notes = SyncNotes(host, me, romId, here.Notes, there, next, state);
            if (notes != null && notes != here.Notes)
                change.Add(g => g.Notes = notes.Length == 0 ? null : notes);

            // Play time and last played only ever move forward.
            if (there != null)
            {
                DateTime? last = there.LastPlayed;
                if (played != null && played.TryGetValue(romId, out KeyValuePair<ulong, DateTime> p))
                {
                    if (p.Key > here.Playtime)
                    {
                        ulong seconds = p.Key;
                        change.Add(g => g.Playtime = Math.Max(g.Playtime, seconds));
                    }
                    if (!last.HasValue || p.Value > last.Value.ToUniversalTime())
                        last = p.Value;
                }
                if (last.HasValue && (!here.LastActivity.HasValue || last.Value.ToUniversalTime() > here.LastActivity.Value.ToUniversalTime()))
                {
                    DateTime when = last.Value.ToLocalTime();
                    change.Add(g => g.LastActivity = when);
                }
            }

            state.Agreed[romId] = next;
            if (change.Count == 0)
                return null;
            return g => change.ForEach(c => c(g));
        }

        // The three-way pick for one value.
        private static T Pick<T>(bool known, T agreed, T here, T there, T none)
        {
            var eq = EqualityComparer<T>.Default;
            if (eq.Equals(here, there))
                return here;
            if (!known)
                return eq.Equals(there, none) ? here : there;   // first sync: whichever side has something
            if (eq.Equals(here, agreed))
                return there;                                    // only the server changed
            return here;                                         // changed here (or on both): this computer's
        }

        // Playnite's status names that RomM has an equivalent for; null for the others.
        private static string ToSyncedStatus(string name)
        {
            if (string.IsNullOrEmpty(name))
                return NotPlayed;
            return RomMRomUser.CompletionStatusMap.ContainsValue(name) ? name : null;
        }

        // ---- Notes ------------------------------------------------------------------------------

        // The notes agreed after this sync, or null when they were left alone.
        private string SyncNotes(string host, int me, int romId, string here, Remote there, Agreed agreed, SyncState state)
        {
            state.NoteIds.TryGetValue(romId, out int noteId);
            if (there == null)
            {
                // Between full syncs: only a change here, on a note known on the server or none.
                if (agreed.Notes == null || here == agreed.Notes)
                    return null;
                WriteNote(host, romId, ref noteId, agreed.Notes, here);
                Remember(state, romId, noteId);
                agreed.Notes = here;
                return here;
            }

            // An older RomM that does not say which games have notes: notes are left alone.
            if (!there.HasNotes.HasValue)
                return null;
            if (!there.HasNotes.Value && here.Length == 0 && agreed.Notes == null)
                return null;   // nothing anywhere

            string serverNotes = "";
            noteId = 0;
            if (there.HasNotes.Value)
            {
                foreach (JToken note in (GetJson(host, $"/api/roms/{romId}/notes") as JArray) ?? new JArray())
                {
                    if ((int?)note["user_id"] == me && string.Equals((string)note["title"], NoteTitle, StringComparison.Ordinal))
                    {
                        serverNotes = (string)note["content"] ?? "";
                        noteId = (int?)note["id"] ?? 0;
                        break;
                    }
                }
            }
            string notes = Pick(agreed.Notes != null, agreed.Notes, here, serverNotes, "");
            if (notes != serverNotes)
                WriteNote(host, romId, ref noteId, serverNotes, notes);
            Remember(state, romId, noteId);
            agreed.Notes = notes;
            return notes;
        }

        private static void Remember(SyncState state, int romId, int noteId)
        {
            if (noteId > 0)
                state.NoteIds[romId] = noteId;
            else
                state.NoteIds.Remove(romId);
        }

        private static void WriteNote(string host, int romId, ref int noteId, string was, string now)
        {
            if (now.Length == 0)
            {
                if (noteId > 0)
                    Send(HttpMethod.Delete, $"{host}/api/roms/{romId}/notes/{noteId}", null, HttpStatusCode.NotFound);
                noteId = 0;
                return;
            }
            var body = new JObject { ["title"] = NoteTitle, ["content"] = now, ["is_public"] = false };
            if (noteId > 0)
            {
                Send(HttpMethod.Put, $"{host}/api/roms/{romId}/notes/{noteId}", body, null);
                return;
            }
            JToken made = Send(HttpMethod.Post, $"{host}/api/roms/{romId}/notes", body, null);
            noteId = (int?)made?["id"] ?? 0;
        }

        // ---- The server -------------------------------------------------------------------------------

        private static void PutProps(string host, int romId, string status, int rating)
        {
            var props = new JObject { ["rating"] = rating };
            if (status != null)
            {
                string key = RomMRomUser.CompletionStatusMap
                    .FirstOrDefault(kv => kv.Value == status && kv.Value != "Playing" && kv.Value != "Plan to Play" && kv.Value != NotPlayed).Key;
                props["backlogged"] = status == "Plan to Play";
                props["now_playing"] = status == "Playing";
                props["status"] = key == null ? JValue.CreateNull() : new JValue(key);
            }
            // Current RomM reads the fields themselves; older versions read them under "data".
            props["data"] = props.DeepClone();
            Send(HttpMethod.Put, $"{host}/api/roms/{romId}/props", props, null);
        }

        // Every game's user properties, a page at a time.
        private static Dictionary<int, Remote> FetchRomUsers(string host)
        {
            const int pageSize = 500;
            var result = new Dictionary<int, Remote>();
            for (int offset = 0; ; offset += pageSize)
            {
                JToken page = GetJson(host, $"/api/roms?limit={pageSize}&offset={offset}&order_by=id&order_dir=asc" +
                    "&with_rom_id_index=false&with_char_index=false&with_filter_values=false");
                JArray items = page as JArray ?? page?["items"] as JArray ?? new JArray();
                foreach (JToken item in items)
                {
                    int? id = (int?)item["id"];
                    if (id == null)
                        continue;
                    RomMRomUser user = item["rom_user"]?.Type == JTokenType.Object ? item["rom_user"].ToObject<RomMRomUser>() : new RomMRomUser();
                    result[id.Value] = new Remote
                    {
                        Status = RomMCompletionStatus.ResolvePlayniteStatusName(user),
                        Rating = Math.Max(0, Math.Min(10, user.Rating)),
                        LastPlayed = user.LastPlayed,
                        HasNotes = item["has_notes"]?.Type == JTokenType.Boolean ? (bool?)item["has_notes"] : null,
                    };
                }
                if (items.Count < pageSize)
                    return result;
            }
        }

        // Seconds played and the last session's end per game, from every device; null when the
        // server keeps no play sessions (older RomM) or does not let this sign-in read them.
        private static Dictionary<int, KeyValuePair<ulong, DateTime>> FetchPlayed(string host)
        {
            const int pageSize = 1000;
            var result = new Dictionary<int, KeyValuePair<ulong, DateTime>>();
            for (int offset = 0; ; offset += pageSize)
            {
                JArray items;
                using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/play-sessions?limit={pageSize}&offset={offset}").GetAwaiter().GetResult())
                {
                    if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Forbidden
                        || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                    {
                        s_log.Info($"[Sync] The server's play sessions are not available ({(int)response.StatusCode}); play time is not read from it.");
                        return null;
                    }
                    response.EnsureSuccessStatusCode();
                    items = JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JArray ?? new JArray();
                }
                foreach (JToken item in items)
                {
                    int? romId = (int?)item["rom_id"];
                    long ms = (long?)item["duration_ms"] ?? 0;
                    DateTime? end = (DateTime?)item["end_time"];
                    if (romId == null || ms <= 0)
                        continue;
                    result.TryGetValue(romId.Value, out KeyValuePair<ulong, DateTime> sum);
                    DateTime endUtc = end?.ToUniversalTime() ?? DateTime.MinValue;
                    result[romId.Value] = new KeyValuePair<ulong, DateTime>(sum.Key + (ulong)(ms / 1000),
                        endUtc > sum.Value ? endUtc : sum.Value);
                }
                if (items.Count < pageSize)
                    return result;
            }
        }

        // Sends the queued sessions, a hundred at a time; returns those the server took (or
        // already had). A server without play sessions keeps them queued.
        private static List<PendingSession> SendSessions(string host, List<PendingSession> queued)
        {
            var sent = new List<PendingSession>();
            for (int at = 0; at < queued.Count; at += 100)
            {
                List<PendingSession> batch = queued.Skip(at).Take(100).ToList();
                var body = new JObject
                {
                    ["sessions"] = new JArray(batch.Select(s => new JObject
                    {
                        ["rom_id"] = s.RomId,
                        ["start_time"] = s.StartUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        ["end_time"] = s.EndUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        ["duration_ms"] = s.DurationMs,
                    })),
                };
                using (var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                using (var response = HttpClientSingleton.Instance.PostAsync($"{host}/api/play-sessions", content).GetAwaiter().GetResult())
                {
                    if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                    {
                        s_log.Info("[Sync] The server keeps no play sessions; sessions stay queued.");
                        return sent;
                    }
                    if ((int)response.StatusCode == 400 || (int)response.StatusCode == 422)
                    {
                        // A batch the server will never take: dropped, so it does not block the rest.
                        s_log.Warn($"[Sync] The server refused {batch.Count} play sessions: {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
                        sent.AddRange(batch);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    sent.AddRange(batch);
                }
            }
            return sent;
        }

        private static JToken GetJson(string host, string path)
        {
            using (var response = HttpClientSingleton.Instance.GetAsync(host + path).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                return JToken.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        }

        // Sends JSON; the answer's JSON, or null. A status in "tolerated" counts as done.
        private static JToken Send(HttpMethod method, string url, JObject body, HttpStatusCode? tolerated)
        {
            using (var request = new HttpRequestMessage(method, url))
            {
                if (body != null)
                    request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = HttpClientSingleton.Instance.SendAsync(request).GetAwaiter().GetResult())
                {
                    if (tolerated.HasValue && response.StatusCode == tolerated.Value)
                        return null;
                    response.EnsureSuccessStatusCode();
                    string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    try
                    {
                        return string.IsNullOrWhiteSpace(text) ? null : JToken.Parse(text);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }
        }
    }
}
