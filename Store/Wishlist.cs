using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Store
{
    /// <summary>A game wanted that the server does not have (yet).</summary>
    public class WishlistEntry
    {
        public int IgdbId { get; set; }          // 0 when not known
        public string Name { get; set; }
        public string Cover { get; set; }        // a full picture address
        public string Series { get; set; }       // the series it was found through
        public string Platform { get; set; }     // the platform it was looked for on, if any
        public DateTime AddedUtc { get; set; }
        public int AvailableRomId { get; set; }  // the server's game, once it has it
        public DateTime? NotifiedUtc { get; set; }

        [JsonIgnore]
        public bool Available => AvailableRomId > 0;
    }

    /// <summary>
    /// The user's wishlist: games found through a series that the RomM server lacks. Kept on
    /// disk. Every so often the server is asked whether it has any of them now, and a
    /// Playnite notification says so once for each that turns up.
    /// </summary>
    public class Wishlist
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly string _path;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _checking = new SemaphoreSlim(1);
        private List<WishlistEntry> _items;

        /// <summary>Raised (on any thread) when an entry is added, removed or turns up.</summary>
        public event Action Changed;

        public Wishlist(string path)
        {
            _path = path;
        }

        /// <summary>A game's name reduced for comparing: lower case, letters and digits only.</summary>
        public static string Normalize(string name)
        {
            var text = new StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                    text.Append(c);
            }
            return text.ToString();
        }

        private List<WishlistEntry> Items
        {
            get
            {
                if (_items != null)
                    return _items;
                try
                {
                    if (File.Exists(_path))
                        _items = JsonConvert.DeserializeObject<List<WishlistEntry>>(File.ReadAllText(_path));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Wishlist] Could not read the wishlist: {ex.Message}");
                }
                return _items ?? (_items = new List<WishlistEntry>());
            }
        }

        public List<WishlistEntry> All()
        {
            lock (_lock)
                return Items.OrderByDescending(e => e.Available).ThenByDescending(e => e.AddedUtc).ToList();
        }

        public int Count
        {
            get
            {
                lock (_lock)
                    return Items.Count;
            }
        }

        private static bool Same(WishlistEntry entry, int igdbId, string name)
        {
            if (igdbId > 0 && entry.IgdbId > 0)
                return igdbId == entry.IgdbId;
            return Normalize(entry.Name) == Normalize(name);
        }

        public bool Contains(int igdbId, string name)
        {
            lock (_lock)
                return Items.Any(e => Same(e, igdbId, name));
        }

        public void Add(WishlistEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                return;
            lock (_lock)
            {
                if (Items.Any(e => Same(e, entry.IgdbId, entry.Name)))
                    return;
                entry.AddedUtc = DateTime.UtcNow;
                Items.Add(entry);
                Save();
            }
            Changed?.Invoke();
        }

        public void Remove(int igdbId, string name)
        {
            lock (_lock)
            {
                if (Items.RemoveAll(e => Same(e, igdbId, name)) == 0)
                    return;
                Save();
            }
            Changed?.Invoke();
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
                s_log.Warn($"[Wishlist] Could not keep the wishlist: {ex.Message}");
            }
        }

        /// <summary>
        /// Asks the server, by name, for each wished-for game it did not have, matching on the
        /// IGDB id where both sides know it and on the name otherwise. Calls
        /// <paramref name="arrived"/> once for each game newly found.
        /// </summary>
        public async Task CheckAsync(string host, Action<WishlistEntry> arrived)
        {
            if (string.IsNullOrEmpty(host) || !await _checking.WaitAsync(0).ConfigureAwait(false))
                return;
            try
            {
                List<WishlistEntry> waiting;
                lock (_lock)
                    waiting = Items.Where(e => !e.Available).ToList();

                var found = new List<WishlistEntry>();
                foreach (WishlistEntry entry in waiting)
                {
                    int romId = await FindAsync(host, entry).ConfigureAwait(false);
                    if (romId <= 0)
                        continue;
                    lock (_lock)
                    {
                        entry.AvailableRomId = romId;
                        if (!entry.NotifiedUtc.HasValue)
                        {
                            entry.NotifiedUtc = DateTime.UtcNow;
                            found.Add(entry);
                        }
                        Save();
                    }
                }

                if (found.Count == 0)
                    return;
                Changed?.Invoke();
                foreach (WishlistEntry entry in found)
                    arrived?.Invoke(entry);
            }
            finally
            {
                _checking.Release();
            }
        }

        // The server's game for a wished-for one, or 0.
        private static async Task<int> FindAsync(string host, WishlistEntry entry)
        {
            try
            {
                string url = $"{host}/api/roms?limit=20&search_term={Uri.EscapeDataString(entry.Name)}";
                using (var response = await HttpClientSingleton.Instance.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return 0;
                    var root = JToken.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    string wanted = Normalize(entry.Name);
                    foreach (JToken rom in root["items"] as JArray ?? new JArray())
                    {
                        int id = (int?)rom["id"] ?? 0;
                        if (id <= 0)
                            continue;
                        int igdb = (int?)rom["igdb_id"] ?? 0;
                        if (entry.IgdbId > 0 && igdb > 0)
                        {
                            if (igdb == entry.IgdbId)
                                return id;
                            continue;
                        }
                        if (Normalize((string)rom["name"]) == wanted)
                            return id;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Wishlist] Could not look for {entry.Name}: {ex.Message}");
            }
            return 0;
        }
    }
}
