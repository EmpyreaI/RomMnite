using Playnite.SDK;
using System;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RomM.Downloads
{
    /// <summary>
    /// Keeps a <see cref="DownloadStatusModel"/> in step with the selected game's entry in the
    /// download queue: follows the selection, notices the entry arriving and leaving, and copies
    /// its progress across as it changes.
    /// </summary>
    internal sealed class DownloadStatusPresenter
    {
        private readonly DownloadQueueController _controller;
        private readonly DownloadStatusModel _model;
        private readonly object _lock = new object();
        private Guid _gameId = Guid.Empty;
        private DownloadQueueItem _item;

        public DownloadStatusPresenter(DownloadQueueController controller, DownloadStatusModel model)
        {
            _controller = controller;
            _model = model;
            _model.PauseResumeCommand = new RelayCommand(() =>
            {
                if (_gameId != Guid.Empty)
                    _controller.TogglePause(_gameId);
            });

            _model.CancelCommand = new RelayCommand(() =>
            {
                if (_gameId != Guid.Empty)
                    _controller.Cancel(_gameId);
            });

            _controller.ViewModel.Items.CollectionChanged += OnQueueChanged;
        }

        public void Select(Guid gameId)
        {
            lock (_lock)
            {
                _gameId = gameId;
                Attach();
            }
        }

        private void OnQueueChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            lock (_lock)
                Attach();
        }

        // Points at the selected game's queue entry, if it has one right now.
        private void Attach()
        {
            DownloadQueueItem item = null;
            try
            {
                item = _gameId == Guid.Empty ? null : _controller.Find(_gameId);
            }
            catch (InvalidOperationException)
            {
                // The queue changed while it was being read; the change raises this again.
            }

            if (!ReferenceEquals(item, _item))
            {
                if (_item != null)
                    _item.PropertyChanged -= OnItemChanged;

                _item = item;

                if (_item != null)
                    _item.PropertyChanged += OnItemChanged;
            }

            Publish();
        }

        // Every change raises several properties; the summary covers all that is shown.
        private void OnItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DownloadQueueItem.Summary) || e.PropertyName == nameof(DownloadQueueItem.Status))
                Publish();
        }

        private void Publish()
        {
            var item = _item;
            bool active = item != null
                          && (item.Status == DownloadStatus.Queued || item.Status == DownloadStatus.Downloading
                              || item.Status == DownloadStatus.Paused || item.Status == DownloadStatus.Extracting);
            if (!active)
            {
                _model.Visible = false;
                return;
            }

            double? percent = item.Percent;
            string complete = percent.HasValue ? percent.Value.ToString("0") + "% Complete" : "";

            switch (item.Status)
            {
                case DownloadStatus.Queued:
                    _model.Headline = "DOWNLOAD QUEUED";
                    _model.Detail = "Waiting to start";
                    break;
                case DownloadStatus.Paused:
                    _model.Headline = "PAUSED";
                    _model.Detail = complete;
                    break;
                case DownloadStatus.Extracting:
                    _model.Headline = "EXTRACTING";
                    _model.Detail = complete;
                    break;
                default:
                    _model.Headline = "DOWNLOADING";
                    _model.Detail = item.BytesPerSecond > 0 && complete.Length > 0
                        ? $"{complete} ({DownloadQueueItem.FormatSpeed(item.BytesPerSecond)})"
                        : complete;
                    break;
            }

            _model.Percent = percent ?? 0;
            _model.IsIndeterminate = !percent.HasValue;
            _model.CanPause = item.CanPause;
            _model.PauseGlyph = item.PauseRequested ? "\uE768" : "\uE769";
            _model.PauseLabel = item.PauseButtonText;
            _model.Visible = true;
        }
    }
}
