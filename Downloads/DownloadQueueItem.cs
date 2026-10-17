using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RomM.Downloads
{
    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Extracting,
        Completed,
        Failed,
        Canceled,
        Paused
    }

    public class DownloadQueueItem : ObservableObject
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string StatusText { get; set; }
        public DownloadStatus Status { get; set; }

        public bool IsIndeterminate { get; set; }
        public double ProgressValue { get; set; }     // bytes or 0..100 depending on stage
        public double ProgressMaximum { get; set; }   // bytes or 100

        public DateTime QueuedOn { get; set; }
        public CancellationTokenSource Cts { get; set; }

        // Set by the user; the download loop notices it between reads and parks the transfer.
        private volatile bool _pauseRequested;
        public bool PauseRequested
        {
            get => _pauseRequested;
            set
            {
                _pauseRequested = value;
                OnPropertyChanged(nameof(PauseRequested));
                OnPropertyChanged(nameof(PauseButtonText));
            }
        }

        /// <summary>Pausing only means something before extraction starts.</summary>
        public bool CanPause => Status == DownloadStatus.Queued || Status == DownloadStatus.Downloading || Status == DownloadStatus.Paused;

        public string PauseButtonText => PauseRequested ? "Resume" : "Pause";

        /// <summary>Current transfer rate; 0 when not downloading.</summary>
        public double BytesPerSecond { get; private set; }

        public void SetSpeed(double bytesPerSecond)
        {
            BytesPerSecond = bytesPerSecond < 0 ? 0 : bytesPerSecond;
            OnPropertyChanged(nameof(BytesPerSecond));
            OnPropertyChanged(nameof(Summary));
        }

        /// <summary>Progress as 0..100, or null while the size is not known.</summary>
        public double? Percent => IsIndeterminate || ProgressMaximum <= 0 ? (double?)null : Math.Min(100.0, ProgressValue / ProgressMaximum * 100.0);

        /// <summary>
        /// One short phrase for a library row, in the manner of Steam's: "30% (510.7 Mbps)",
        /// "Download Queued", "Paused 30%", "Extracting 40%".
        /// </summary>
        public string Summary
        {
            get
            {
                string pct = Percent.HasValue ? Percent.Value.ToString("0") + "%" : null;
                switch (Status)
                {
                    case DownloadStatus.Queued:
                        return "Download Queued";
                    case DownloadStatus.Paused:
                        return pct == null ? "Paused" : "Paused " + pct;
                    case DownloadStatus.Extracting:
                        return pct == null ? "Extracting" : "Extracting " + pct;
                    case DownloadStatus.Downloading:
                        if (pct == null)
                            return "Downloading";
                        return BytesPerSecond > 0 ? $"{pct} ({FormatSpeed(BytesPerSecond)})" : pct;
                    case DownloadStatus.Completed:
                        return "Installed";
                    case DownloadStatus.Failed:
                        return "Download Failed";
                    case DownloadStatus.Canceled:
                        return "Download Cancelled";
                    default:
                        return string.Empty;
                }
            }
        }

        /// <summary>Megabits per second, as download clients quote it; kilobits below one.</summary>
        public static string FormatSpeed(double bytesPerSecond)
        {
            double megabits = bytesPerSecond * 8.0 / 1000000.0;
            return megabits >= 1 ? megabits.ToString("0.0") + " Mbps" : (megabits * 1000.0).ToString("0") + " Kbps";
        }

        public void SetStatus(DownloadStatus st, string text)
        {
            Status = st;
            StatusText = text;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(Summary));
        }

        /// <summary>The download's size in bytes, once known (kept through extraction, whose progress is in files).</summary>
        public double TotalBytes { get; private set; }

        /// <summary>Bytes downloaded so far (all of them once extracting).</summary>
        public double DownloadedBytes => Status == DownloadStatus.Extracting || Status == DownloadStatus.Completed ? TotalBytes : Math.Min(ProgressValue, TotalBytes);

        public void SetProgress(double value, double max, bool indeterminate)
        {
            if (!indeterminate && max > 1024 && Status != DownloadStatus.Extracting && Status != DownloadStatus.Completed)
                TotalBytes = max;
            ProgressValue = value;
            ProgressMaximum = max < 1 ? 1 : max;
            IsIndeterminate = indeterminate;
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(ProgressMaximum));
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(Summary));
        }
    }
}
