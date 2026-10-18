using Playnite.SDK;
using Playnite.SDK.Plugins;
using RomM.Games;
using SharpCompress.Archives;
using SharpCompress.Common;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Downloads
{
    public class DownloadQueueController
    {
        private readonly IPlayniteAPI api;
        private readonly DownloadQueueViewModel vm;

        private readonly SemaphoreSlim concurrencyGate;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> activeDownloads =
            new System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource>();

        public ILogger Logger => LogManager.GetLogger();
        public int MaxConcurrent { get; }

        public DownloadQueueController(IPlayniteAPI api, DownloadQueueViewModel vm, int maxConcurrent)
        {
            this.api = api;
            this.vm = vm;

            MaxConcurrent = Math.Max(1, maxConcurrent);
            concurrencyGate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        }

        public DownloadQueueViewModel ViewModel => vm;

        internal IPlayniteAPI Api => api;

        /// <summary>When games finished downloading here (the Recent Games shelf's order); may be null.</summary>
        public RecentDownloads Recent { get; set; }

        /// <summary>A download finished and its game is installed (the game's id; on the download's thread).</summary>
        public event Action<Guid> Finished;

        public void Enqueue(DownloadRequest req)
        {
            var item = new DownloadQueueItem
            {
                GameId = req.GameId,
                GameName = req.GameName,
                QueuedOn = DateTime.Now,
                Cts = new CancellationTokenSource()
            };

            activeDownloads[item.GameId] = item.Cts;

            // Started now: the game keeps its place at the front of Recent Games whatever becomes
            // of the download (finished, or cancelled), as Steam's does.
            Recent?.MarkAdded(item.GameId);

            item.SetStatus(DownloadStatus.Queued, "Queued");
            item.SetProgress(0, 1, true);

            api.MainView.UIDispatcher.Invoke(() => vm.Items.Add(item));

            // fire and forget background worker
            Task.Run(async () => await ProcessItem(item, req));
        }

        public void Cancel(Guid gameId)
        {
            if (activeDownloads.TryGetValue(gameId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "An error occurred while cancelling a download.");
                }
            }
        }

        /// <summary>The queue entry for a game, or null when it is not downloading.</summary>
        public DownloadQueueItem Find(Guid gameId)
        {
            return vm.Items.FirstOrDefault(i => i.GameId == gameId);
        }

        /// <summary>
        /// Pauses a download, or resumes a paused one. A pause takes effect at the next chunk; the
        /// partial file is kept and the download carries on from there when resumed, provided the
        /// server supports it (otherwise it starts again).
        /// </summary>
        public void TogglePause(Guid gameId)
        {
            var item = Find(gameId);
            if (item != null && item.CanPause)
                item.PauseRequested = !item.PauseRequested;
        }

        // Parks the download while the user has it paused. Cancelling still works: the token is
        // what ends the wait.
        // How often a download's progress is reported to the UI.
        private const int UiUpdateMs = 250;

        // Retries in a row, with no real progress between them, before a download gives up.
        private const int RetryLimit = 8;
        // Progress that counts as the connection working again (resets the count).
        private const long RetryProgressReset = 4 * 1024 * 1024;
        // How long a connection may go without data before it is taken for dead.
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

        /// <summary>A failure worth retrying: the server is busy, or the connection broke.</summary>
        private sealed class TransientDownloadException : Exception
        {
            public TransientDownloadException(string message) : base(message) { }
        }

        // Connection-level failures: a dropped or reset socket (IOException, the SslStream
        // "disposed object" when the connection is torn down under the read), a request that
        // could not be sent or timed out. An HTTP error the server meant (404, 401...) is not.
        private static bool IsTransient(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is TransientDownloadException || e is IOException || e is ObjectDisposedException
                    || e is System.Net.WebException || e is System.Net.Sockets.SocketException || e is TimeoutException
                    || e is TaskCanceledException)
                    return true;
                // Without an inner exception it is EnsureSuccessStatusCode's, for an answer the
                // server meant (5xx, 408 and 429 were turned into retries before it).
                if (e is HttpRequestException && e.InnerException == null)
                    return false;
            }
            return false;
        }

        private static async Task WaitWhilePaused(DownloadQueueItem item, long downloaded, long? totalBytes, CancellationToken ct)
        {
            if (!item.PauseRequested)
                return;

            item.SetSpeed(0);
            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                item.SetProgress(downloaded, totalBytes.Value, false);
                item.SetStatus(DownloadStatus.Paused, "Paused " + ((double)downloaded / totalBytes.Value * 100.0).ToString("0") + "%");
            }
            else
            {
                item.SetProgress(0, 1, false);
                item.SetStatus(DownloadStatus.Paused, "Paused");
            }

            while (item.PauseRequested)
                await Task.Delay(250, ct).ConfigureAwait(false);
        }

        private async Task ProcessItem(DownloadQueueItem item, DownloadRequest req)
        {
            await concurrencyGate.WaitAsync().ConfigureAwait(false);

            try
            {
                await DownloadAndInstall(item, req).ConfigureAwait(false);

                item.SetStatus(DownloadStatus.Completed, "Completed");
                item.SetProgress(item.ProgressMaximum, item.ProgressMaximum, false);

                await Task.Delay(1000).ConfigureAwait(false);

                RemoveFromList(item);
            }
            catch (OperationCanceledException)
            {
                item.SetStatus(DownloadStatus.Canceled, "Canceled");
                item.SetProgress(0, 1, false);

                TryCleanupPartialInstall(req);

                req.OnCanceled?.Invoke();

                await Task.Delay(500).ConfigureAwait(false);
                RemoveFromList(item);
            }
            catch (Exception ex)
            {
                item.SetStatus(DownloadStatus.Failed, "Failed");
                TryCleanupPartialInstall(req);

                req.OnFailed?.Invoke(ex);

                await Task.Delay(1500).ConfigureAwait(false);
                RemoveFromList(item);
            }
            finally
            {
                activeDownloads.TryRemove(item.GameId, out _);
                concurrencyGate.Release();
            }
        }

        private async Task DownloadAndInstall(DownloadQueueItem item, DownloadRequest req)
        {
            var ct = item.Cts.Token;

            Directory.CreateDirectory(req.InstallDir);

            byte[] buffer = new byte[1024 * 256];
            long downloaded = 0;
            long? totalBytes = null;

            // One pass per connection. A pause closes the connection and the next pass asks the
            // server to carry on from where the file ends, so a paused download holds no socket
            // open for the server to time out. A connection that drops or stalls is retried the
            // same way, after a short wait; only failures in a row without any progress between
            // them give up (see RetryLimit).
            int failuresInARow = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await WaitWhilePaused(item, downloaded, totalBytes, ct).ConfigureAwait(false);

                item.SetStatus(DownloadStatus.Downloading, "Downloading...");
                if (downloaded == 0)
                    item.SetProgress(0, 1, true);

                bool pausedMidway = false;
                long passStart = downloaded;

                try
                {
                using (var request = new HttpRequestMessage(HttpMethod.Get, req.DownloadUrl))
                {
                    if (downloaded > 0)
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(downloaded, null);

                    using (var response = await HttpClientSingleton.Instance
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        if (code >= 500 || code == 408 || code == 429)
                            throw new TransientDownloadException($"The server answered {code}.");
                        response.EnsureSuccessStatusCode();

                        // 206 means the server honoured the range and is sending the remainder.
                        // Anything else is the whole file again, so the download starts over.
                        bool resumed = downloaded > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                        if (!resumed)
                        {
                            if (downloaded > 0)
                                Logger.Info($"The server cannot resume {req.GameName}; its download is starting again.");

                            downloaded = 0;
                            totalBytes = response.Content.Headers.ContentLength;
                        }

                        item.SetProgress(downloaded, totalBytes ?? 1, !totalBytes.HasValue);

                        // Progress is reported a few times a second, not per chunk: at LAN speeds a
                        // byte threshold meant a hundred updates a second, each a dozen property
                        // changes the UI thread had to take, which left the Store unresponsive.
                        var uiClock = Stopwatch.StartNew();

                        var speedClock = Stopwatch.StartNew();
                        long speedMark = downloaded;

                        // A connection that goes quiet (no data for StallTimeout) is closed, which
                        // ends the read below with an error, and the pass is retried.
                        var sinceData = Stopwatch.StartNew();
                        bool stalled = false;
                        using (var watchdog = new Timer(_ =>
                        {
                            if (!item.PauseRequested && sinceData.Elapsed > StallTimeout && !stalled)
                            {
                                stalled = true;
                                try { response.Dispose(); } catch { }
                            }
                        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)))
                        using (var httpStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var fileStream = new FileStream(req.GamePath, resumed ? FileMode.Append : FileMode.Create,
                            FileAccess.Write, FileShare.None, buffer.Length, true))
                        {
                            while (true)
                            {
                                ct.ThrowIfCancellationRequested();

                                if (item.PauseRequested)
                                {
                                    pausedMidway = true;
                                    break;
                                }

                                int read;
                                try
                                {
                                    read = await httpStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                                }
                                catch (Exception) when (stalled && !ct.IsCancellationRequested)
                                {
                                    throw new TransientDownloadException($"No data from the server for {StallTimeout.TotalSeconds:0} seconds.");
                                }
                                if (read <= 0)
                                {
                                    break;
                                }
                                sinceData.Restart();

                                await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);

                                downloaded += read;
                                if (downloaded - passStart >= RetryProgressReset)
                                    failuresInARow = 0;

                                // Rate over the last second or so, so the figure is steady enough to read.
                                if (speedClock.ElapsedMilliseconds >= 1000)
                                {
                                    item.SetSpeed((downloaded - speedMark) / speedClock.Elapsed.TotalSeconds);
                                    speedMark = downloaded;
                                    speedClock.Restart();
                                }

                                if (uiClock.ElapsedMilliseconds >= UiUpdateMs)
                                {
                                    uiClock.Restart();

                                    if (totalBytes.HasValue && totalBytes.Value > 0)
                                    {
                                        item.SetProgress(downloaded, totalBytes.Value, false);
                                        var pct = (double)downloaded / totalBytes.Value * 100.0;
                                        item.SetStatus(DownloadStatus.Downloading, "Downloading... " + pct.ToString("0") + "%");
                                    }
                                    else
                                    {
                                        item.SetProgress(downloaded, Math.Max(1, downloaded), true);
                                        item.SetStatus(DownloadStatus.Downloading, "Downloading...");
                                    }
                                }
                            }
                        }
                    }
                }

                // The whole file is here (a short read with a known length is a dropped connection).
                if (!pausedMidway && totalBytes.HasValue && totalBytes.Value > 0 && downloaded < totalBytes.Value)
                    throw new TransientDownloadException($"The connection closed at {downloaded} of {totalBytes.Value} bytes.");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsTransient(ex))
                {
                    item.SetSpeed(0);
                    failuresInARow++;
                    if (failuresInARow > RetryLimit)
                    {
                        Logger.Warn($"{req.GameName}: giving up after {RetryLimit} retries without progress: {ex.Message}");
                        throw;
                    }
                    // What reached the disk is kept, and the next pass asks for the rest.
                    try { downloaded = File.Exists(req.GamePath) ? new FileInfo(req.GamePath).Length : 0; } catch { }
                    TimeSpan wait = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failuresInARow, 5)));
                    Logger.Warn($"{req.GameName}: download interrupted at {downloaded} bytes ({ex.GetType().Name}: {ex.Message}); " +
                                $"retry {failuresInARow} of {RetryLimit} in {wait.TotalSeconds:0}s.");
                    item.SetStatus(DownloadStatus.Downloading, $"Connection lost, retrying in {wait.TotalSeconds:0}s ({failuresInARow}/{RetryLimit})...");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                item.SetSpeed(0);

                if (!pausedMidway)
                    break;
            }

            // The whole download's size, as "Downloading data" shows it: the server's own length
            // for the file, else what reached the disk.
            req.DownloadedSize = totalBytes.HasValue && totalBytes.Value > 0 ? totalBytes.Value : downloaded;

            // Extract if needed (we treat extract as 0..100 in its own bar)
            // Don't extract archives when install flat is enabled
            if (req.DownloadAsArchive || (req.AutoExtract && IsFileCompressed(req.GamePath) && !req.InstallFlat))
            {
                item.SetStatus(DownloadStatus.Extracting, "Extracting...");
                Logger.Info($"Extracting {req.GamePath}...");

                if (req.Use7z && !string.IsNullOrEmpty(req.PathTo7Z) && req.PathTo7Z.EndsWith("7z.exe", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractArchiveWith7z(req.PathTo7Z, req.GamePath, req.InstallDir, item, ct);
                }
                else
                {
                    ExtractArchiveWithEntryProgress(req.GamePath, req.InstallDir, item, ct);
                }
                try { File.Delete(req.GamePath); } catch { }
            }

            // Build rom list + signal installed
            var roms = req.BuildRoms != null ? req.BuildRoms() : null;

            // Finished: at the front of Recent Games, and listed under Completed on the Downloads
            // page. Marked before the game is set installed, so the shelves redrawn for that
            // already have it at the front.
            Recent?.Mark(item.GameId);

            req.OnInstalled?.Invoke(new GameInstalledEventArgs(new GameInstallationData
            {
                InstallDirectory = req.InstallDir,
                Roms = roms
            }));

            try
            {
                Finished?.Invoke(item.GameId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Downloads] A finished-download listener failed: {ex.Message}");
            }
        }

        // What is unpacked after downloading: archives by their extension, and then only when they
        // really are one. Disc images are left alone, whatever SharpCompress makes of them -- it
        // took a Wii .wbfs for an archive, "extracted" it to nothing, and the install failed.
        private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz" };

        private static bool IsFileCompressed(string filePath)
        {
            if (!ArchiveExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            return ArchiveFactory.IsArchive(filePath, out var type);
        }


        // 7-Zip extracts into the output dir and drops absolute paths and ".." components unless -spf
        // is passed (we never pass it), but the entry names are checked up front anyway so both
        // extraction paths refuse traversal the same way. Formats SharpCompress cannot open fall back
        // to 7-Zip's own handling rather than failing a download that used to work.
        private void EnsureEntriesContained(string archivePath, string installDir)
        {
            try
            {
                using (var archive = ArchiveFactory.Open(archivePath))
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        RomMInstallPaths.ResolveWithin(installDir, entry.Key);
                    }
                }
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not inspect {archivePath} before extraction: {ex.Message}");
            }
        }

        private void ExtractArchiveWith7z(string pathTo7z, string archivePath, string installDir, DownloadQueueItem item, CancellationToken ct)
        {
            if (archivePath == null || archivePath.Contains("../") || archivePath.Contains(@"..\"))
            {
                throw new ArgumentException("Invalid archive path");
            }
            if (installDir == null || installDir.Contains("../") || installDir.Contains(@"..\"))
            {
                throw new ArgumentException("Invalid install directory path");
            }

            EnsureEntriesContained(archivePath, installDir);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = pathTo7z,
                Arguments = $"x \"{archivePath.Replace("\"", "\\\"")}\" -o\"{installDir.Replace("\"", "\\\"")}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            ct.ThrowIfCancellationRequested();
            using (Process process = Process.Start(startInfo))
            { 
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new Exception($"7z extraction failed for {archivePath} with exit code {process.ExitCode}.");
                }
            }
        }
        private void ExtractArchiveWithEntryProgress(string archivePath, string installDir, DownloadQueueItem item, CancellationToken ct)
        {
            using (var archive = ArchiveFactory.Open(archivePath))
            {
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                int total = entries.Count;
                int done = 0;
                var uiClock = Stopwatch.StartNew();

                item.SetProgress(0, Math.Max(1, total), false);

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    // Entry names come from the downloaded archive, so they are untrusted: handing a
                    // "../" or rooted key to ExtractFullPath would write outside installDir. Resolve
                    // and verify each destination, then copy the entry there ourselves.
                    var destination = RomMInstallPaths.ResolveWithin(installDir, entry.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));

                    using (var entryStream = entry.OpenEntryStream())
                    using (var file = File.Create(destination))
                    {
                        entryStream.CopyTo(file);
                    }

                    done++;
                    // A few times a second (and at the end): archives of thousands of small files
                    // would otherwise report thousands of times a second.
                    if (done == total || uiClock.ElapsedMilliseconds >= UiUpdateMs)
                    {
                        uiClock.Restart();
                        item.SetProgress(done, total, false);
                        var pct = total > 0 ? (double)done / total * 100.0 : 100.0;
                        item.SetStatus(DownloadStatus.Extracting, "Extracting... " + pct.ToString("0") + "%");
                    }
                }
            }
        }

        private void RemoveFromList(DownloadQueueItem item)
        {
            if (item == null) return;

            api.MainView.UIDispatcher.Invoke(() =>
            {
                vm.Items.Remove(item);
            });
        }

        private void TryCleanupPartialInstall(DownloadRequest req)
        {
            try
            {
                // delete partial downloaded file first
                SafeDeleteFileWithRetry(req.GamePath);

                // delete folder (recursively) if it exists
                if(!req.InstallFlat)
                    SafeDeleteDirectoryWithRetry(req.InstallDir);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Cleanup failed for {req.GameName} ({req.GameId}).");
                // don't rethrow - cancel should still succeed
            }
        }

        private static void SafeDeleteFileWithRetry(string path, int retries = 6, int delayMs = 150)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    if (!File.Exists(path))
                        return;

                    // clear attributes that can block deletion
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(delayMs);
                }
            }
        }

        private static void SafeDeleteDirectoryWithRetry(string dir, int retries = 6, int delayMs = 150)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    if (!Directory.Exists(dir))
                        return;

                    // make sure children are deletable
                    ClearAttributesRecursive(dir);

                    Directory.Delete(dir, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(delayMs);
                }
            }
        }

        private static void ClearAttributesRecursive(string dir)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    try { new DirectoryInfo(d).Attributes = FileAttributes.Normal; } catch { }
                }
                try { new DirectoryInfo(dir).Attributes = FileAttributes.Normal; } catch { }
            }
            catch
            {
                // ignore - best effort
            }
        }
    }
}
