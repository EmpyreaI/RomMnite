using System;
using System.IO;
using System.Threading;

namespace RomM.Saves
{
    /// <summary>
    /// Watches a save directory while a game runs and calls back once writing has gone quiet, so
    /// a save made mid-session is uploaded without waiting for the game to close. A save is
    /// several files written in a burst; waiting for quiet uploads it once, complete.
    /// </summary>
    internal sealed class SaveWatch : IDisposable
    {
        private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private readonly Action _onQuiet;
        private int _disposed;

        public SaveWatch(string directory, Action onQuiet)
        {
            _onQuiet = onQuiet;
            _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
            _watcher.Changed += (s, e) => Touch();
            _watcher.Created += (s, e) => Touch();
            _watcher.Renamed += (s, e) => Touch();
            _watcher.EnableRaisingEvents = true;
        }

        private void Touch()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            try
            {
                _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Fire()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            try
            {
                _onQuiet();
            }
            catch
            {
                // The callback logs its own failures; a watcher must never take the process down.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _timer.Dispose();
        }
    }
}
