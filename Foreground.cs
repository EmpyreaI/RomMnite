using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RomM
{
    /// <summary>
    /// What the user is doing now, so work they did not ask for (the Store getting ready out of
    /// sight, its other pages' pictures, the library's artwork kept on disk) waits for it and is
    /// never felt. Foreground, while any of it lasts:
    ///
    ///   cloud saves syncing                      SaveSyncService.Sync
    ///   a library update                         RomM.ImportGames
    ///   a game running (and starting)            OnGameStarting .. OnGameStopped
    ///   a game just picked in the library        OnGameSelected: a few seconds, and its
    ///                                            achievements while they are fetched
    ///   anything else registered with Also        a download in progress
    ///
    /// Background work calls <see cref="WaitQuiet"/> (or <see cref="WaitQuietAsync"/>) before
    /// each request or picture it starts: what is already on its way finishes, nothing new
    /// starts until the foreground is quiet. The Store's own requests while it is showing are
    /// foreground and never wait.
    /// </summary>
    internal static class Foreground
    {
        private static int s_busy;
        private static long s_quietFromTicks;   // UTC ticks: busy until then after a touch
        private static readonly List<Func<bool>> s_also = new List<Func<bool>>();

        /// <summary>Foreground until the returned object is disposed.</summary>
        public static IDisposable Busy()
        {
            Interlocked.Increment(ref s_busy);
            return new Release();
        }

        public static void Begin() => Interlocked.Increment(ref s_busy);

        public static void End()
        {
            if (Interlocked.Decrement(ref s_busy) < 0)
                Interlocked.Exchange(ref s_busy, 0);
        }

        /// <summary>Foreground for a moment from now (a game picked in the library).</summary>
        public static void Touch(TimeSpan forHow)
        {
            long until = DateTime.UtcNow.Add(forHow).Ticks;
            long seen;
            do
            {
                seen = Interlocked.Read(ref s_quietFromTicks);
                if (seen >= until)
                    return;
            }
            while (Interlocked.CompareExchange(ref s_quietFromTicks, until, seen) != seen);
        }

        /// <summary>Something else that is foreground while it says so (asked on each check).</summary>
        public static void Also(Func<bool> busy)
        {
            lock (s_also)
                s_also.Add(busy);
        }

        public static bool Quiet
        {
            get
            {
                if (Volatile.Read(ref s_busy) > 0 || DateTime.UtcNow.Ticks < Interlocked.Read(ref s_quietFromTicks))
                    return false;
                Func<bool>[] also;
                lock (s_also)
                    also = s_also.ToArray();
                foreach (Func<bool> busy in also)
                {
                    try
                    {
                        if (busy())
                            return false;
                    }
                    catch
                    {
                        // Unknown: not busy.
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Waits (off the UI thread) until the foreground is quiet, or until the work stops being
        /// background (<paramref name="stillBackground"/> false: the Store was opened, and what
        /// it is waiting for is now what the user is looking at).
        /// </summary>
        public static void WaitQuiet(Func<bool> stillBackground = null)
        {
            while (!Quiet && (stillBackground == null || stillBackground()))
                Thread.Sleep(250);
        }

        public static async Task WaitQuietAsync(Func<bool> stillBackground = null)
        {
            while (!Quiet && (stillBackground == null || stillBackground()))
                await Task.Delay(250).ConfigureAwait(false);
        }

        private sealed class Release : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                    End();
            }
        }
    }
}
