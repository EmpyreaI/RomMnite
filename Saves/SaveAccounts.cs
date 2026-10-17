using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves
{
    /// <summary>
    /// Keeps each account's saves apart on a computer several accounts share. The emulators and
    /// games keep one save per game, in one place, whoever plays; so this notes whose save is on
    /// disk for each game, and when another account launches the game, packs the save on disk
    /// into its owner's own store (under the plugin's data) and puts the launching account's
    /// back from its store, or clears it when it has none here (the save sync then brings the
    /// account's own from the server, if it has one there).
    ///
    /// Swapping at launch, not at sign-in, costs nothing for games not played and runs a save
    /// tool (Ludusavi) only for the one game. Saves are moved with the same packing the save sync
    /// uses, so whatever it can sync it can swap; what it cannot (an emulator with no save
    /// handler) is not kept apart either.
    ///
    /// Who owns the save on disk:
    ///   - an account's key: theirs;
    ///   - "" : packed away already, so what is on disk belongs to nobody (a swap that stopped
    ///     part-way: it is cleared or replaced, never packed again over the good copy);
    ///   - no entry: not known (saves from before accounts were kept apart).
    /// </summary>
    internal sealed class SaveAccounts
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private const string Nobody = "";

        private sealed class Stored
        {
            public string FileName { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
            public string Hash { get; set; }
            public string EmulatorTag { get; set; }
            public DateTime StoredUtc { get; set; }
        }

        private readonly RomM _plugin;
        private readonly string _dataFolder;
        private readonly string _ownersPath;
        private readonly object _lock = new object();
        private Dictionary<Guid, string> _owners;

        public SaveAccounts(RomM plugin, string dataFolder)
        {
            _plugin = plugin;
            _dataFolder = dataFolder;
            _ownersPath = Path.Combine(dataFolder, "SaveOwners.json");
        }

        private Dictionary<Guid, string> Owners
        {
            get
            {
                if (_owners != null)
                    return _owners;
                try
                {
                    if (File.Exists(_ownersPath))
                        _owners = JsonConvert.DeserializeObject<Dictionary<Guid, string>>(File.ReadAllText(_ownersPath));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[SaveAccounts] Could not read whose saves are on disk: {ex.Message}");
                }
                return _owners ?? (_owners = new Dictionary<Guid, string>());
            }
        }

        private string OwnerOf(Guid gameId)
        {
            lock (_lock)
                return Owners.TryGetValue(gameId, out string owner) ? owner : null;
        }

        private void SetOwner(Guid gameId, string owner)
        {
            lock (_lock)
            {
                Owners[gameId] = owner;
                try
                {
                    Directory.CreateDirectory(_dataFolder);
                    string temp = _ownersPath + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(Owners, Formatting.Indented));
                    if (File.Exists(_ownersPath))
                        File.Replace(temp, _ownersPath, null);
                    else
                        File.Move(temp, _ownersPath);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[SaveAccounts] Could not note whose save is on disk: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Whether the save on disk may be synced as this account's: it is theirs, or nobody's
        /// is known and only one account has used this computer.
        /// </summary>
        public bool IsMine(Game game, string accountKey)
        {
            if (game == null || string.IsNullOrEmpty(accountKey))
                return true;
            // An emulator with users of its own: the save on disk is the account's when the
            // emulator is set to its user (see Account.EmulatorUsers).
            if (_plugin.EmulatorUsers.Separates(game))
                return _plugin.EmulatorUsers.IsActiveFor(game);
            string owner = OwnerOf(game.Id);
            if (owner == null)
                return _plugin.Libraries.KnownAccounts <= 1;
            return string.Equals(owner, accountKey, StringComparison.OrdinalIgnoreCase);
        }

        private string StoreFor(string accountKey, Guid gameId)
        {
            string root = accountKey == null
                ? Path.Combine(_dataFolder, "UnclaimedSaves")
                : Path.Combine(AccountFiles.Folder(_dataFolder, accountKey), "Saves");
            return Path.Combine(root, gameId.ToString());
        }

        /// <summary>
        /// Before a game launches: the launching account's save in place of whoever's is there.
        /// False when that could not be done, and the game should not start (it would play, and
        /// then sync, another account's save).
        /// </summary>
        public bool Prepare(Game game, string accountKey)
        {
            if (game == null || game.PluginId != _plugin.Id || string.IsNullOrEmpty(accountKey))
                return true;
            if (!RomMGameId.TryParse(game.GameId, out int romId, out string _))
                return true;
            // Kept apart by the emulator itself (a user per account): nothing to swap.
            if (_plugin.EmulatorUsers.Separates(game))
                return true;

            string owner = OwnerOf(game.Id);
            if (string.Equals(owner, accountKey, StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                lock (_plugin.SaveSync.LockFor(romId))
                {
                    SaveTarget target = _plugin.SaveSync.TargetFor(game, out string reason);
                    if (target == null)
                        return true;   // saves this cannot find, it cannot keep apart either

                    if (owner == null)
                    {
                        // Nobody's known yet: one account on this computer, or no save, makes it
                        // simple; otherwise the player says whether it is theirs.
                        if (!target.Exists || _plugin.Libraries.KnownAccounts <= 1)
                        {
                            SetOwner(game.Id, accountKey);
                            return true;
                        }
                        var answer = _plugin.Playnite.Dialogs.ShowMessage(
                            $"There is a save for {game.Name} on this computer from before accounts kept their saves apart.\n\n" +
                            "Is it yours? If not, it is kept aside for whoever it belongs to and you start from your own save.",
                            "Whose save is this?", System.Windows.MessageBoxButton.YesNo);
                        if (answer == System.Windows.MessageBoxResult.Yes)
                        {
                            SetOwner(game.Id, accountKey);
                            return true;
                        }
                        // Kept for the one other account here that has the game, or set aside.
                        List<string> others = _plugin.Libraries.AccountsWith(game.Id)
                            .Where(k => !string.Equals(k, accountKey, StringComparison.OrdinalIgnoreCase)).ToList();
                        owner = others.Count == 1 ? others[0] : null;
                        Pack(game, target, owner);
                        SetOwner(game.Id, Nobody);
                    }
                    else if (owner != Nobody)
                    {
                        if (target.Exists)
                            Pack(game, target, owner);
                        SetOwner(game.Id, Nobody);
                    }

                    // On disk now: nobody's. The launching account's save, or none.
                    Unpack(game, accountKey);
                    SetOwner(game.Id, accountKey);
                    s_log.Info($"[SaveAccounts] {game.Name}: the save on disk is now {accountKey}'s.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                s_log.Error(ex, $"[SaveAccounts] Could not swap the saves of {game.Name}.");
                _plugin.Playnite.Dialogs.ShowErrorMessage(
                    $"{game.Name} was not started: the save on this computer could not be swapped for yours, " +
                    $"so you would have played on someone else's.\n\n{ex.Message}", "Saves");
                return false;
            }
        }

        // Packs the save on disk into an account's store (or the unclaimed ones), keeping the
        // copy it replaces as .last.
        private void Pack(Game game, SaveTarget target, string accountKey)
        {
            string folder = StoreFor(accountKey, game.Id);
            Directory.CreateDirectory(folder);
            string blob = Path.Combine(folder, "save.bin");
            string temp = blob + ".tmp";

            var stored = new Stored
            {
                UpdatedAtUtc = target.UpdatedAtUtc,
                Hash = target.ContentHash(),
                EmulatorTag = target.EmulatorTag,
                StoredUtc = DateTime.UtcNow,
            };
            using (PreparedUpload packed = target.PrepareUpload())
            {
                File.Copy(packed.FilePath, temp, true);
                stored.FileName = packed.FileName;
            }
            if (new FileInfo(temp).Length == 0)
                throw new InvalidDataException("The packed save is empty.");

            if (File.Exists(blob))
            {
                File.Copy(blob, blob + ".last", true);
                File.Delete(blob);
            }
            File.Move(temp, blob);
            File.WriteAllText(Path.Combine(folder, "save.json"), JsonConvert.SerializeObject(stored, Formatting.Indented));
            s_log.Info($"[SaveAccounts] {game.Name}: kept {(accountKey ?? "an unclaimed account")}'s save ({target.Describe()}) in {folder}.");
        }

        // Puts an account's stored save on disk, or clears the save when it has none here.
        private void Unpack(Game game, string accountKey)
        {
            string folder = StoreFor(accountKey, game.Id);
            string blob = Path.Combine(folder, "save.bin");
            SaveTarget target = _plugin.SaveSync.TargetFor(game, out string reason);
            if (target == null)
                throw new InvalidOperationException(reason ?? "The save's location could not be found.");

            // Whatever is on disk goes first (it is packed away already, or nobody's), so nothing
            // of it is left mixed in with the save put back.
            target.Clear();
            if (!File.Exists(blob))
            {
                s_log.Info($"[SaveAccounts] {game.Name}: {accountKey} has no save kept here; cleared the one on disk.");
                return;
            }

            Stored stored = null;
            try
            {
                stored = JsonConvert.DeserializeObject<Stored>(File.ReadAllText(Path.Combine(folder, "save.json")));
            }
            catch (Exception ex)
            {
                s_log.Warn($"[SaveAccounts] Could not read the details of {game.Name}'s kept save: {ex.Message}");
            }

            // A save kept under another emulator's packing cannot go back under this one's.
            if (stored != null && !string.IsNullOrEmpty(stored.EmulatorTag)
                && !string.Equals(stored.EmulatorTag, target.EmulatorTag, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Your kept save is {stored.EmulatorTag}'s, but the game now uses {target.EmulatorTag}. It is in {folder}.");
            }

            target.ApplyDownload(File.ReadAllBytes(blob), stored?.UpdatedAtUtc);

            // Back on disk: the kept copy steps aside (kept once more, as .last, in case).
            File.Copy(blob, blob + ".last", true);
            File.Delete(blob);
            s_log.Info($"[SaveAccounts] {game.Name}: put {accountKey}'s kept save back.");
        }
    }
}
