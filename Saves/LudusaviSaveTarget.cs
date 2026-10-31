using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Saves
{
    /// <summary>
    /// A PC game's save, handled through Ludusavi. Ludusavi knows where each game keeps its saves
    /// (files and registry) and how to put them back on another machine or Windows user; this
    /// class only moves its backup to and from RomM.
    ///
    /// Each game gets a private working folder. Before the save is compared or uploaded, Ludusavi
    /// backs the game up into that folder; after a download is unpacked there, Ludusavi restores
    /// from it. What travels to RomM is Ludusavi's own backup of that one game, zipped.
    ///
    /// Ludusavi is only run when the save is actually looked at, so merely resolving the target
    /// (which happens several times around a play session) costs nothing.
    ///
    /// A game run with a Steam emulator keeps what it saves through Steam's cloud in the
    /// emulator's own folder, where Ludusavi does not look (it looks in Steam's userdata). That
    /// folder travels too, as one more folder of the backup ("SteamEmu"), copied in before each
    /// upload and back out after a download.
    ///
    /// Saves Ludusavi's manifest puts in the game's own folder ("&lt;base&gt;/...") are found here
    /// too (LudusaviManifest), since Ludusavi only knows a game's folder in a store's library, not
    /// in RomMnite's. They travel as the "InstallFolder" folder of the backup, by their paths
    /// within the game's folder.
    /// </summary>
    internal sealed class LudusaviSaveTarget : SaveTarget
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

        private readonly string _exe;
        private readonly string _workDir;
        private readonly string _gameName;
        private readonly ILogger _logger;
        private readonly string _customTitle;
        private FolderSetSaveTarget _backup;
        private string _statusHeadline;
        private string _statusDetail;
        private readonly string _steamEmuFolder;
        private const string SteamEmuUnit = "SteamEmu";
        private const string InstallUnit = "InstallFolder";
        private readonly string _installDir;
        private string _title;   // the title Ludusavi knows the game by, once found

        public override string StatusHeadline => _statusHeadline;

        public override string StatusDetail => _statusDetail;

        /// <param name="steamEmuFolder">The game's Steam emulator's cloud folder (SteamEmuFiles.CloudFolder), or null.</param>
        public LudusaviSaveTarget(string ludusaviExe, string workDir, string gameName, string fileName, ILogger logger, string customTitle = null,
            string steamEmuFolder = null, string installDir = null)
        {
            _installDir = installDir;
            _steamEmuFolder = steamEmuFolder;
            _customTitle = string.IsNullOrWhiteSpace(customTitle) ? null : customTitle.Trim();
            _exe = ludusaviExe;
            _workDir = workDir;
            _gameName = gameName;
            FileName = fileName;
            _logger = logger;
        }

        public override string EmulatorTag => "ludusavi";

        public override string Slot => "autosave";

        public override string FileName { get; }

        public override string Describe() => $"Ludusavi backup of \"{_gameName}\" in \"{_workDir}\"" +
            (_steamEmuFolder != null ? $", with its Steam emulator's saves in \"{_steamEmuFolder}\"" : "") +
            (_installDir != null ? $", and the saves its manifest puts in \"{_installDir}\"" : "");

        /// <summary>The backup as it stands after asking Ludusavi to refresh it, once per sync.</summary>
        private FolderSetSaveTarget Backup()
        {
            if (_backup != null)
                return _backup;

            Directory.CreateDirectory(_workDir);
            RefreshBackup();
            CopySteamEmuIn();
            CopyInstallFolderIn();
            return _backup = Wrap();
        }

        private FolderSetSaveTarget Wrap()
        {
            // The working folder holds this game alone, so every folder in it is the save.
            var folders = new DirectoryInfo(_workDir).GetDirectories()
                .Where(d => !d.Name.StartsWith(".", StringComparison.Ordinal) && !d.Name.EndsWith(".romm-old", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name);
            return new FolderSetSaveTarget(EmulatorTag, _workDir, folders, FileName, null);
        }

        private void RefreshBackup()
        {
            // A name given by hand is used exactly: no guessing at a close match, so a typo shows
            // up as "not recognised" instead of quietly syncing some other game's saves.
            if (_customTitle != null)
            {
                JObject custom = Run($"backup --force --api --path {Quote(_workDir)} {Quote(_customTitle)}");
                if (IsUnknown(custom, _customTitle))
                {
                    _logger?.Info($"[SaveSync] Ludusavi does not know the custom name \"{_customTitle}\" set for \"{_gameName}\".");
                    _statusHeadline = "Game not recognised";
                    _statusDetail = $"Ludusavi does not know a game called \"{_customTitle}\", the Ludusavi name set for this game. " +
                                    "Correct it from the game's right-click menu.";
                    return;
                }

                _title = _customTitle;
                if (((custom?["games"] as JObject)?.Count ?? 0) == 0)
                    _logger?.Info($"[SaveSync] Ludusavi found no saves on this computer for \"{_customTitle}\".");
                return;
            }

            JObject result = Run($"backup --force --api --path {Quote(_workDir)} {Quote(_gameName)}");
            _title = _gameName;

            // Ludusavi's titles are PCGamingWiki's, which need not match the library's. When it
            // does not know the name, ask it for its closest title and back that up instead.
            if (IsUnknown(result, _gameName))
            {
                string title = FindTitle(_gameName);
                if (title == null)
                {
                    _logger?.Info($"[SaveSync] Ludusavi does not know a game called \"{_gameName}\"; nothing to back up. " +
                                  "Add it as a custom game in Ludusavi to sync its saves.");
                    _statusHeadline = "Game not recognised";
                    _title = null;
                    _statusDetail = $"Ludusavi does not know a game called \"{_gameName}\", so its saves are not being synced. " +
                                    "Set its Ludusavi name from the game's right-click menu.";
                    return;
                }

                _logger?.Info($"[SaveSync] Ludusavi knows \"{_gameName}\" as \"{title}\".");
                _title = title;
                result = Run($"backup --force --api --path {Quote(_workDir)} {Quote(title)}");
            }

            int games = (result?["games"] as JObject)?.Count ?? 0;
            if (games == 0)
                _logger?.Info($"[SaveSync] Ludusavi found no saves on this computer for \"{_gameName}\".");
        }

        // The Steam emulator's cloud folder, as it is now, into the backup (or out of it, when
        // it has nothing).
        private void CopySteamEmuIn()
        {
            if (_steamEmuFolder == null)
                return;
            string unit = Path.Combine(_workDir, SteamEmuUnit);
            try
            {
                if (Directory.Exists(unit))
                    Directory.Delete(unit, true);
                if (!Directory.Exists(_steamEmuFolder) || !Directory.EnumerateFiles(_steamEmuFolder, "*", SearchOption.AllDirectories).Any())
                    return;
                int files = CopyTree(_steamEmuFolder, unit);
                _logger?.Info($"[SaveSync] \"{_gameName}\": {files} Steam emulator save file(s) from \"{_steamEmuFolder}\".");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[SaveSync] Could not copy the Steam emulator's saves of \"{_gameName}\": {ex.Message}");
            }
        }

        // The backup's Steam emulator folder back where the emulator keeps it. Whether there was one.
        private bool CopySteamEmuOut()
        {
            string unit = Path.Combine(_workDir, SteamEmuUnit);
            if (_steamEmuFolder == null || !Directory.Exists(unit))
                return false;
            if (Directory.Exists(_steamEmuFolder))
                Directory.Delete(_steamEmuFolder, true);
            int files = CopyTree(unit, _steamEmuFolder);
            _logger?.Info($"[SaveSync] \"{_gameName}\": {files} Steam emulator save file(s) restored to \"{_steamEmuFolder}\".");
            return true;
        }

        // ---- Saves in the game's own folder ------------------------------------------------------

        // The manifest's entry for the game, and which folder is its <base>; null when there is
        // nothing of the kind (no install folder, no manifest, no such saves).
        private LudusaviManifest.Entry InstallEntry(out string baseFolder)
        {
            baseFolder = null;
            if (string.IsNullOrEmpty(_installDir) || !Directory.Exists(_installDir))
                return null;
            string manifest = LudusaviManifest.PathFor(_exe);
            if (manifest == null)
                return null;
            string title = _title ?? _customTitle;
            if (title == null)
            {
                // Before any backup (a download first): the library's name, else Ludusavi's match.
                title = LudusaviManifest.For(manifest, _gameName) != null ? _gameName : FindTitle(_gameName);
                _title = title;
            }
            LudusaviManifest.Entry entry = LudusaviManifest.For(manifest, title);
            if (entry == null || entry.BasePaths.Count == 0)
                return null;
            baseFolder = LudusaviManifest.BaseFolder(_installDir, entry);
            return baseFolder == null ? null : entry;
        }

        private void CopyInstallFolderIn()
        {
            string unit = Path.Combine(_workDir, InstallUnit);
            try
            {
                if (Directory.Exists(unit))
                    Directory.Delete(unit, true);
                LudusaviManifest.Entry entry = InstallEntry(out string baseFolder);
                if (entry == null)
                    return;
                List<string> files = LudusaviManifest.Files(baseFolder, entry);
                if (files.Count == 0)
                {
                    _logger?.Info($"[SaveSync] \"{_gameName}\": no saves in its own folder yet ({string.Join(", ", entry.BasePaths)} in \"{baseFolder}\").");
                    return;
                }
                foreach (string relative in files)
                {
                    string target = Path.Combine(unit, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(Path.Combine(baseFolder, relative), target, true);
                }
                _logger?.Info($"[SaveSync] \"{_gameName}\": {files.Count} save file(s) in its own folder, \"{baseFolder}\" ({string.Join(", ", entry.BasePaths)}).");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[SaveSync] Could not copy the saves in \"{_gameName}\"'s own folder: {ex.Message}");
            }
        }

        // The backup's InstallFolder back into the game's folder. Whether there was one.
        private bool CopyInstallFolderOut()
        {
            string unit = Path.Combine(_workDir, InstallUnit);
            if (!Directory.Exists(unit))
                return false;
            LudusaviManifest.Entry entry = InstallEntry(out string baseFolder);
            if (entry == null)
            {
                _logger?.Warn($"[SaveSync] \"{_gameName}\": the save holds files for the game's own folder, but the game isn't installed here (or Ludusavi's manifest is missing); they were not put back.");
                return false;
            }
            int files = CopyTree(unit, baseFolder);
            _logger?.Info($"[SaveSync] \"{_gameName}\": {files} save file(s) restored into its own folder, \"{baseFolder}\".");
            return true;
        }

        // Files keep their times (File.Copy does), so an unchanged save hashes and dates the same.
        private static int CopyTree(string from, string to)
        {
            int count = 0;
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, file.Substring(from.TrimEnd('\\', '/').Length).TrimStart('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
                count++;
            }
            return count;
        }

        private static bool IsUnknown(JObject result, string name)
        {
            var unknown = result?["errors"]?["unknownGames"] as JArray;
            return unknown != null && unknown.Any(u => string.Equals((string)u, name, StringComparison.OrdinalIgnoreCase));
        }

        private string FindTitle(string name)
        {
            JObject result = Run($"find --api --normalized {Quote(name)}");
            return (result?["games"] as JObject)?.Properties().FirstOrDefault()?.Name;
        }

        public override bool Exists => Backup().Exists;

        public override DateTime UpdatedAtUtc => Backup().UpdatedAtUtc;

        public override long SizeBytes => Backup().SizeBytes;

        public override string ContentHash() => Backup().ContentHash();

        public override PreparedUpload PrepareUpload() => Backup().PrepareUpload();

        public override void ApplyDownload(byte[] payload, DateTime? serverUpdatedAtUtc)
        {
            Directory.CreateDirectory(_workDir);

            // Unpacked without a server timestamp: Ludusavi carries each save file's own time
            // through backup and restore, and overwriting those would make every file look changed.
            var incoming = _backup ?? Wrap();
            incoming.ApplyDownload(payload, null);
            _backup = Wrap();

            // The working folder holds this game alone, so "restore everything in it" is exactly
            // this game, whatever title Ludusavi filed it under.
            bool steamEmu = CopySteamEmuOut() | CopyInstallFolderOut();
            bool ludusaviPart = new DirectoryInfo(_workDir).GetDirectories()
                .Any(d => !d.Name.StartsWith(".", StringComparison.Ordinal) && d.Name != SteamEmuUnit && d.Name != InstallUnit);
            if (!ludusaviPart)
            {
                if (!steamEmu)
                    throw new InvalidOperationException("The downloaded save held nothing to restore.");
                return;
            }
            JObject result = Run($"restore --force --api --path {Quote(_workDir)}");
            int games = (result?["games"] as JObject)?.Count ?? 0;
            if (games == 0 && !steamEmu)
                throw new InvalidOperationException("Ludusavi restored nothing from the downloaded save.");

            if (games > 0)
                _logger?.Info($"[SaveSync] Ludusavi restored the save for \"{_gameName}\".");
        }

        /// <summary>
        /// Deletes the game's save files where the game keeps them: Ludusavi is asked which files
        /// a backup would take (a preview, which writes nothing) and those are removed. Saves kept
        /// in the registry are left as they are; they are logged. The working folder's backup,
        /// which no longer matches what is on disk, goes too.
        /// </summary>
        public override void Clear()
        {
            string title = _customTitle ?? _gameName;
            JObject report = Run($"backup --preview --api {Quote(title)}");
            if (_customTitle == null && IsUnknown(report, title))
            {
                title = FindTitle(_gameName);
                report = title == null ? null : Run($"backup --preview --api {Quote(title)}");
            }

            int files = 0, registry = 0;
            foreach (JProperty game in (report?["games"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                foreach (JProperty file in (game.Value["files"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    string path = file.Name.Replace('/', Path.DirectorySeparatorChar);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        files++;
                    }
                }
                registry += (game.Value["registry"] as JObject)?.Count ?? 0;
            }

            LudusaviManifest.Entry own = InstallEntry(out string ownFolder);
            if (own != null)
            {
                foreach (string relative in LudusaviManifest.Files(ownFolder, own))
                {
                    File.Delete(Path.Combine(ownFolder, relative));
                    files++;
                }
            }

            if (_steamEmuFolder != null && Directory.Exists(_steamEmuFolder))
            {
                foreach (string file in Directory.GetFiles(_steamEmuFolder, "*", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                    files++;
                }
            }

            if (Directory.Exists(_workDir))
                Directory.Delete(_workDir, true);
            _backup = null;

            _logger?.Info($"[SaveSync] Cleared {files} save files of \"{_gameName}\"" +
                          (registry > 0 ? $"; {registry} registry entries were left as they are." : "."));
        }

        /// <summary>
        /// Runs Ludusavi and returns its JSON report, or null when it produced none. Ludusavi
        /// exits non-zero for partial problems (an unknown game, a file it could not copy) while
        /// still printing a report, so the report is read whatever the exit code.
        /// </summary>
        private JObject Run(string arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = _exe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (var process = Process.Start(start))
            {
                // Both streams are drained while waiting, or a full pipe would stall Ludusavi.
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException($"Ludusavi did not finish within {ProcessTimeout.TotalSeconds:0} seconds ({arguments}).");
                }

                string stdout = output.GetAwaiter().GetResult();
                string stderr = error.GetAwaiter().GetResult();

                try
                {
                    int brace = stdout.IndexOf('{');
                    if (brace >= 0)
                        return JObject.Parse(stdout.Substring(brace));
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"[SaveSync] Could not read Ludusavi's report ({ex.Message}).");
                }

                _logger?.Warn($"[SaveSync] Ludusavi gave no report for: {arguments} (exit code {process.ExitCode}). " +
                              $"Output: {Shorten(stdout)} Errors: {Shorten(stderr)}");
                return null;
            }
        }

        private static string Shorten(string text)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length <= 400 ? text : text.Substring(0, 400) + "...";
        }

        // Windows command-line quoting: a trailing backslash would otherwise escape the closing quote.
        private static string Quote(string value)
        {
            value = (value ?? string.Empty).Replace("\"", "\\\"");
            if (value.EndsWith("\\", StringComparison.Ordinal))
                value += "\\";
            return "\"" + value + "\"";
        }
    }
}
