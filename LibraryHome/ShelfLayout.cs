using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.LibraryHome
{
    /// <summary>One shelf of the library's Home page: what it shows.</summary>
    public class ShelfSetting
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// favorites, collection, installed, library, uncategorized, all, recent, playing,
        /// collections (the collections themselves) or playnext.
        /// </summary>
        public string Kind { get; set; }

        public Guid CollectionId { get; set; }   // for "collection"
        public Guid Library { get; set; }        // for "library": the library plugin's id (empty: Playnite's own)

        public ShelfSetting Copy() => new ShelfSetting { Id = Id, Kind = Kind, CollectionId = CollectionId, Library = Library };
    }

    /// <summary>
    /// The shelves of the library's Home page, in order, as the user arranged them; kept on
    /// disk. Null until the user (or the page's first showing) sets them.
    /// </summary>
    public class ShelfLayout
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private string _path;
        private readonly object _lock = new object();
        private List<ShelfSetting> _items;
        private bool _read;

        public ShelfLayout(string path)
        {
            _path = path;
        }

        /// <summary>Switches to another file (another account's shelves), as GameCollections.UseFile.</summary>
        public void UseFile(string path, string legacyPath = null)
        {
            lock (_lock)
            {
                if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _read)
                    return;
                Games.AccountFiles.Adopt(path, legacyPath);
                _path = path;
                _items = null;
                _read = false;
            }
        }

        /// <summary>The shelves (copies), or null when none have been set yet.</summary>
        public List<ShelfSetting> Get()
        {
            lock (_lock)
            {
                if (!_read)
                {
                    _read = true;
                    try
                    {
                        if (File.Exists(_path))
                            _items = JsonConvert.DeserializeObject<List<ShelfSetting>>(File.ReadAllText(_path));
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Shelves] Could not read the shelves: {ex.Message}");
                    }
                }
                return _items?.Select(s => s.Copy()).ToList();
            }
        }

        public void Set(List<ShelfSetting> shelves)
        {
            lock (_lock)
            {
                _read = true;
                _items = shelves.Select(s => s.Copy()).ToList();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, JsonConvert.SerializeObject(_items, Formatting.Indented));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Shelves] Could not keep the shelves: {ex.Message}");
                }
            }
        }

        /// <summary>A shelf for a collection just made, before All Games (or last).</summary>
        public void AddCollectionShelf(Guid collectionId)
        {
            List<ShelfSetting> shelves = Get();
            if (shelves == null || shelves.Any(s => s.Kind == "collection" && s.CollectionId == collectionId))
                return;
            int at = shelves.FindIndex(s => s.Kind == "all");
            shelves.Insert(at < 0 ? shelves.Count : at, new ShelfSetting { Kind = "collection", CollectionId = collectionId });
            Set(shelves);
        }

        /// <summary>The shelves of a collection that is gone.</summary>
        public void RemoveCollectionShelves(Guid collectionId)
        {
            List<ShelfSetting> shelves = Get();
            if (shelves != null && shelves.RemoveAll(s => s.Kind == "collection" && s.CollectionId == collectionId) > 0)
                Set(shelves);
        }
    }
}
