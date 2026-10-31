using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;

namespace RomM.Downloads
{
    public class DownloadRequest
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string DownloadUrl { get; set; }
        public string InstallDir { get; set; }      // folder where to extract/install
        public string GamePath { get; set; }        // full path to the downloaded file on disk
        public bool DownloadAsArchive { get; set; } // download is the ROM's folder as one archive, so it always gets extracted
        public bool AutoExtract { get; set; } = true;
        public bool Use7z { get; set; } = false;
        public string PathTo7Z { get; set; } = "";
        public bool InstallFlat { get; set; } = false;

        /// Optional function used after extraction to build rom list for Playnite
        public Func<List<GameRom>> BuildRoms { get; set; }

        // Callbacks
        public Action<GameInstalledEventArgs> OnInstalled { get; set; }

        /// <summary>
        /// The size of what was downloaded (the server's whole file, as "Downloading data" shows
        /// it), set once the download is done, before <see cref="OnInstalled"/>; 0 when unknown.
        /// </summary>
        public long DownloadedSize { get; set; }
        public Action OnCanceled { get; set; }
        public Action<Exception> OnFailed { get; set; }

        /// <summary>
        /// The files of the zip downloaded, read before it was unpacked (name, CRC-32, size), for
        /// telling an update's changed files later (Games.GameUpdates); null when it was not one.
        /// </summary>
        public List<Games.InstalledManifest.ZipEntryRecord> ArchiveEntries { get; set; }

        /// <summary>Whether the download was unpacked into <see cref="InstallDir"/> (and deleted).</summary>
        public bool Extracted { get; set; }

        /// <summary>
        /// Work done in place of the usual download and unpacking (an update: Games.GameUpdates),
        /// with the queue's entry to report progress on. <see cref="OnInstalled"/> is not called.
        /// </summary>
        public Func<DownloadQueueItem, System.Threading.CancellationToken, System.Threading.Tasks.Task> Work { get; set; }

        /// <summary>
        /// Leaves the install folder alone when cancelled or failed (an update of a game already
        /// installed): only what <see cref="Work"/> itself made is cleaned up, by it.
        /// </summary>
        public bool KeepInstallOnFailure { get; set; }
    }
}
