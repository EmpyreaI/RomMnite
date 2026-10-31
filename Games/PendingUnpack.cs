using Playnite.SDK;
using SharpCompress.Archives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace RomM.Games
{
    /// <summary>
    /// Games whose download is still an archive on disk: the unpacking that should have followed
    /// the download never happened (the extraction was switched off when it was downloaded, it
    /// was interrupted -- Playnite closed, the computer slept -- or it failed), or the files were
    /// put there by hand. Such a game is unpacked before it starts (RomM.OnGameStarting), the way
    /// the download would have done it.
    /// </summary>
    internal static class PendingUnpack
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        // The same list the download queue unpacks.
        private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz" };

        public static bool LooksLikeArchive(string path) =>
            // A command line rather than a path (quotes, a pipe ...) is not one, and is not a path to ask about.
            !string.IsNullOrEmpty(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
            && ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a file is a whole archive: it opens and lists its files. A download cut short
        /// does not (a zip's and a 7z's index are at the end), so it is not taken for an install.
        /// </summary>
        public static bool IsComplete(string path)
        {
            if (!LooksLikeArchive(path) || !File.Exists(path))
                return false;
            // Only formats whose list of files is quick to read; a .tar.gz would be read through
            // whole, so it is taken as complete.
            if (!new[] { ".zip", ".7z", ".rar" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                return true;
            try
            {
                using (var archive = ArchiveFactory.Open(path))
                    return archive.Entries.Any(e => !e.IsDirectory);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Unpacks an archive into a folder, refusing any entry that would land outside it. With
        /// 7-Zip set in the settings it does the work (formats SharpCompress cannot open).
        /// </summary>
        public static void Extract(string archivePath, string destination, string pathTo7z, Action<double> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(destination);
            if (!string.IsNullOrEmpty(pathTo7z) && pathTo7z.EndsWith("7z.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(pathTo7z))
            {
                // Entry names checked first, as the download queue does.
                try
                {
                    using (var archive = ArchiveFactory.Open(archivePath))
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                            RomMInstallPaths.ResolveWithin(destination, entry.Key);
                }
                catch (ArgumentException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Unpack] Could not inspect {archivePath} before extracting it: {ex.Message}");
                }
                var start = new ProcessStartInfo
                {
                    FileName = pathTo7z,
                    Arguments = $"x \"{archivePath.Replace("\"", "\\\"")}\" -o\"{destination.Replace("\"", "\\\"")}\" -y",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(start))
                {
                    while (!process.WaitForExit(250))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            ct.ThrowIfCancellationRequested();
                        }
                    }
                    if (process.ExitCode != 0)
                        throw new Exception($"7-Zip could not extract {Path.GetFileName(archivePath)} (exit code {process.ExitCode}).");
                }
                return;
            }

            using (var archive = ArchiveFactory.Open(archivePath))
            {
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                int done = 0;
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string target = RomMInstallPaths.ResolveWithin(destination, entry.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var source = entry.OpenEntryStream())
                    using (var file = File.Create(target))
                        source.CopyTo(file);
                    done++;
                    progress?.Invoke(entries.Count == 0 ? 1 : (double)done / entries.Count);
                }
            }
        }

        /// <summary>
        /// What to start of a Windows game unpacked into a folder (WindowsLaunch: its own .com or
        /// .bat, else its likeliest .exe), with whether that is sure. Empty when there is none.
        /// </summary>
        public static List<string> WindowsLaunchFiles(string folder, IList<string> types, string title = null)
            => WindowsLaunchFiles(folder, types, title, out bool _);

        public static List<string> WindowsLaunchFiles(string folder, IList<string> types, string title, out bool sure)
        {
            WindowsLaunch.Ranking ranking = WindowsLaunch.Rank(folder, title, types);
            sure = ranking.Sure;
            var found = new List<string>();
            if (ranking.BestPath != null)
            {
                found.Add(ranking.BestPath);
                s_log.Info($"[Launch] {title ?? folder}: starts with {ranking.BestPath}" +
                           (ranking.Chosen != null ? " (chosen when packed)" : sure ? "" : " (not sure: asked at the first Play)") +
                           (ranking.Best != null ? $"; {string.Join("; ", ranking.Candidates.Take(4))}" : ""));
            }
            return found;
        }
    }
}
