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

        public override string StatusHeadline => _statusHeadline;

        public override string StatusDetail => _statusDetail;

        public LudusaviSaveTarget(string ludusaviExe, string workDir, string gameName, string fileName, ILogger logger, string customTitle = null)
        {
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

        public override string Describe() => $"Ludusavi backup of \"{_gameName}\" in \"{_workDir}\"";

        /// <summary>The backup as it stands after asking Ludusavi to refresh it, once per sync.</summary>
        private FolderSetSaveTarget Backup()
        {
            if (_backup != null)
                return _backup;

            Directory.CreateDirectory(_workDir);
            RefreshBackup();
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

                if (((custom?["games"] as JObject)?.Count ?? 0) == 0)
                    _logger?.Info($"[SaveSync] Ludusavi found no saves on this computer for \"{_customTitle}\".");
                return;
            }

            JObject result = Run($"backup --force --api --path {Quote(_workDir)} {Quote(_gameName)}");

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
                    _statusDetail = $"Ludusavi does not know a game called \"{_gameName}\", so its saves are not being synced. " +
                                    "Set its Ludusavi name from the game's right-click menu.";
                    return;
                }

                _logger?.Info($"[SaveSync] Ludusavi knows \"{_gameName}\" as \"{title}\".");
                result = Run($"backup --force --api --path {Quote(_workDir)} {Quote(title)}");
            }

            int games = (result?["games"] as JObject)?.Count ?? 0;
            if (games == 0)
                _logger?.Info($"[SaveSync] Ludusavi found no saves on this computer for \"{_gameName}\".");
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
            JObject result = Run($"restore --force --api --path {Quote(_workDir)}");
            int games = (result?["games"] as JObject)?.Count ?? 0;
            if (games == 0)
                throw new InvalidOperationException("Ludusavi restored nothing from the downloaded save.");

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
