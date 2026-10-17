using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace RomM.Downloads
{
    /// <summary>
    /// A game's download status as a short piece of text, for a row in the library list:
    /// " - 30% (510.7 Mbps)", " - Download Queued". Empty, and taking no space, when the game is
    /// not downloading. A theme places it with
    /// &lt;ContentControl x:Name="RomM_DownloadStatus" /&gt; inside its list item template.
    ///
    /// Playnite creates one of these per visible row and hands each its game.
    ///
    /// A game with copies on other platforms (Games/LibraryVersions), where only one is shown:
    /// a download of another copy shows on the shown one's row, with that copy's platform
    /// (" - PSP: 30% (510.7 Mbps)"), so the row the library has tells what is downloading.
    /// </summary>
    public class DownloadStatusListControl : PluginUserControl
    {
        private readonly DownloadQueueController _controller;
        private readonly Func<Game, List<Game>> _otherCopies;
        private readonly TextBlock _text;
        private DownloadQueueItem _item;
        private string _itemPlatform;   // another copy's download: its platform; else null
        private bool _listening;
        private int _pending;

        public DownloadStatusListControl(DownloadQueueController controller, Func<Game, List<Game>> otherCopies = null)
        {
            _controller = controller;
            _otherCopies = otherCopies;

            _text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            // The theme's accent colour, as Steam tints a downloading row. If the theme has no
            // such brush the text simply keeps the row's normal colour.
            _text.SetResourceReference(TextBlock.ForegroundProperty, "AccentSelectedBrush");

            Content = _text;
            Visibility = Visibility.Collapsed;
            Focusable = false;
            IsHitTestVisible = false;

            // Rows are recycled as the list scrolls, so only listen while actually on screen.
            Loaded += (s, e) => Listen(true);
            Unloaded += (s, e) => Listen(false);
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            Attach();
        }

        private void Listen(bool on)
        {
            if (on == _listening)
                return;

            _listening = on;
            if (on)
            {
                _controller.ViewModel.Items.CollectionChanged += OnQueueChanged;
                Attach();
            }
            else
            {
                _controller.ViewModel.Items.CollectionChanged -= OnQueueChanged;
                SetItem(null);
            }
        }

        private void OnQueueChanged(object sender, NotifyCollectionChangedEventArgs e) => Attach();

        private void Attach()
        {
            DownloadQueueItem item = null;
            string platform = null;
            try
            {
                var game = GameContext;
                item = !_listening || game == null ? null : _controller.Find(game.Id);
                if (item == null && _listening && game != null && _otherCopies != null)
                {
                    foreach (Game other in _otherCopies(game) ?? new List<Game>())
                    {
                        // Still going (or failed): not one done or cancelled.
                        item = _controller.Find(other.Id);
                        if (item != null && (item.Status == DownloadStatus.Completed || item.Status == DownloadStatus.Canceled))
                            item = null;
                        if (item != null)
                        {
                            platform = other.Platforms?.FirstOrDefault()?.Name ?? "Another version";
                            break;
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // The queue changed while it was being read; the change raises this again.
            }

            _itemPlatform = platform;
            SetItem(item);
        }

        private void SetItem(DownloadQueueItem item)
        {
            if (!ReferenceEquals(item, _item))
            {
                if (_item != null)
                    _item.PropertyChanged -= OnItemChanged;

                _item = item;

                if (_item != null)
                    _item.PropertyChanged += OnItemChanged;
            }

            Render();
        }

        // Progress arrives from the download's own thread; the text belongs to the UI thread.
        private void OnItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DownloadQueueItem.Summary))
                return;

            if (Dispatcher.CheckAccess())
                Render();
            else if (System.Threading.Interlocked.Exchange(ref _pending, 1) == 0)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _pending = 0;
                    Render();
                }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Render()
        {
            string summary = _item?.Summary;
            // Another copy's download, done or cancelled since: nothing to tell on this row.
            if (_itemPlatform != null && _item != null && (_item.Status == DownloadStatus.Completed || _item.Status == DownloadStatus.Canceled))
                summary = null;
            if (string.IsNullOrEmpty(summary))
            {
                _text.Text = string.Empty;
                Visibility = Visibility.Collapsed;
                return;
            }

            _text.Text = " - " + (_itemPlatform == null ? "" : _itemPlatform + ": ") + summary;
            Visibility = Visibility.Visible;
        }
    }
}
