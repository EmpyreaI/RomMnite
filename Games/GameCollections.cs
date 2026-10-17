using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Games
{
    /// <summary>A collection the user made and named, of any games in the library.</summary>
    public class GameCollection
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        public List<Guid> Games { get; set; } = new List<Guid>();
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// The user's own collections, as Steam's library has, kept by the plugin on disk rather
    /// than as Playnite categories: categories are filled from game metadata (RomM's IGDB
    /// collections among them) and rewritten when it is refreshed, which would mix the user's
    /// collections in with imported ones and could empty them.
    /// </summary>
    public class GameCollections
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        /// <summary>The section their entries sit under in a game's menu.</summary>
        public const string MenuSection = "Add to Collection";

        private string _path;
        private readonly object _lock = new object();
        private List<GameCollection> _items;

        /// <summary>Raised (on the calling thread) after any change.</summary>
        public event Action Changed;

        /// <summary>Raised after a collection is made, and after one is deleted.</summary>
        public event Action<GameCollection> Created;
        public event Action<Guid> Deleted;

        public GameCollections(string path)
        {
            _path = path;
        }

        /// <summary>
        /// Switches to another file (another account's collections). Without one yet, the first
        /// account to switch takes the collections kept before accounts had their own.
        /// </summary>
        public void UseFile(string path, string legacyPath = null)
        {
            lock (_lock)
            {
                if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _items != null)
                    return;
                AccountFiles.Adopt(path, legacyPath);
                _path = path;
                _items = null;
            }
            Changed?.Invoke();
        }

        /// <summary>Sets a collection's games outright (a sync with the server).</summary>
        public void SetGames(Guid id, IEnumerable<Guid> games)
        {
            var list = games.Distinct().ToList();
            Change(items =>
            {
                GameCollection collection = items.FirstOrDefault(c => c.Id == id);
                if (collection != null)
                    collection.Games = list;
            });
        }

        private List<GameCollection> Items
        {
            get
            {
                if (_items != null)
                    return _items;
                try
                {
                    if (File.Exists(_path))
                        _items = JsonConvert.DeserializeObject<List<GameCollection>>(File.ReadAllText(_path));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Collections] Could not read the collections: {ex.Message}");
                }
                return _items ?? (_items = new List<GameCollection>());
            }
        }

        /// <summary>Every collection, by name; copies, safe to keep.</summary>
        public List<GameCollection> All()
        {
            lock (_lock)
            {
                return Items
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(c => new GameCollection { Id = c.Id, Name = c.Name, Games = c.Games.ToList(), CreatedUtc = c.CreatedUtc })
                    .ToList();
            }
        }

        public GameCollection Create(string name, IEnumerable<Guid> games = null)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
                return null;
            var made = new GameCollection { Name = name, Games = (games ?? new Guid[0]).Distinct().ToList() };
            Change(items => items.Add(made));
            Created?.Invoke(made);
            return made;
        }

        public void Rename(Guid id, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length > 0)
                Change(items => items.Where(c => c.Id == id).ToList().ForEach(c => c.Name = name));
        }

        public void Delete(Guid id)
        {
            Change(items => items.RemoveAll(c => c.Id == id));
            Deleted?.Invoke(id);
        }

        public void Add(Guid id, IEnumerable<Guid> games)
        {
            var adding = games.ToList();
            Change(items =>
            {
                GameCollection collection = items.FirstOrDefault(c => c.Id == id);
                if (collection == null)
                    return;
                foreach (Guid game in adding)
                {
                    if (!collection.Games.Contains(game))
                        collection.Games.Add(game);
                }
            });
        }

        public void Remove(Guid id, IEnumerable<Guid> games)
        {
            var removing = new HashSet<Guid>(games);
            Change(items => items.FirstOrDefault(c => c.Id == id)?.Games.RemoveAll(removing.Contains));
        }

        /// <summary>Whether every one of the games is in the collection.</summary>
        public bool HasAll(Guid id, IEnumerable<Guid> games)
        {
            lock (_lock)
            {
                GameCollection collection = Items.FirstOrDefault(c => c.Id == id);
                return collection != null && games.All(collection.Games.Contains);
            }
        }

        private void Change(Action<List<GameCollection>> change)
        {
            lock (_lock)
            {
                change(Items);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, JsonConvert.SerializeObject(_items, Formatting.Indented));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Collections] Could not keep the collections: {ex.Message}");
                }
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// Puts the games in the collection, or takes them out when they are all in it already;
        /// the toggle behind each entry in a game's menu.
        /// </summary>
        public void Toggle(Guid id, IList<Guid> games)
        {
            if (HasAll(id, games))
                Remove(id, games);
            else
                Add(id, games);
        }

        /// <summary>Asks for a name and makes a collection of the games; null if cancelled.</summary>
        public GameCollection CreateWithPrompt(IPlayniteAPI api, IEnumerable<Guid> games, string suggested = "")
        {
            var answer = api.Dialogs.SelectString("Name the new collection:", "New collection", suggested ?? "");
            if (answer == null || !answer.Result || string.IsNullOrWhiteSpace(answer.SelectedString))
                return null;
            return Create(answer.SelectedString, games);
        }
    }
}
