using Playnite.SDK.Models;
using RomM.Downloads;
using RomM.Games;
using RomM.Store;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.LibraryHome
{
    /// <summary>
    /// The Recent Games shelf's downloads, as Steam's: games downloading now come first, each as a
    /// wide card with its art, its progress bar and what it is doing; a game that has finished
    /// downloading then takes its place among the recent games by when it finished (unless it
    /// has been played since).
    /// </summary>
    public partial class LibraryHomeView
    {
        private UIElement RecentShelf(List<Game> games, out FrameworkElement arrows)
        {
            arrows = null;
            var byId = games.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
            List<DownloadQueueItem> downloading = (_downloads?.ViewModel.Items.ToList() ?? new List<DownloadQueueItem>())
                .Where(i => i.Status != DownloadStatus.Canceled && i.Status != DownloadStatus.Failed && i.Status != DownloadStatus.Completed
                            && byId.ContainsKey(i.GameId))
                .OrderByDescending(i => i.QueuedOn)
                .ToList();
            var busy = new HashSet<Guid>(downloading.Select(i => i.GameId));

            // Played or downloaded, whichever was later.
            Func<Game, DateTime?> when = g =>
            {
                DateTime? played = g.LastActivity;
                DateTime? fetched = _downloads?.Recent?.When(g.Id);
                if (played == null)
                    return fetched;
                if (fetched == null)
                    return played;
                return played.Value > fetched.Value ? played : fetched;
            };
            List<Tuple<Game, DateTime>> recent = games
                .Where(g => !busy.Contains(g.Id))
                .Select(g => Tuple.Create(g, when(g)))
                .Where(t => t.Item2.HasValue)
                .OrderByDescending(t => t.Item2.Value)
                .Take(20)
                .Select(t => Tuple.Create(t.Item1, t.Item2.Value))
                .ToList();

            if (downloading.Count == 0 && recent.Count == 0)
                return EmptyShelf("Games you play or download show up here.");
            return SideScroll.RowWithHeaderArrows(RecentRow(downloading.Select(i => Tuple.Create(byId[i.GameId], i)).ToList(), recent), out arrows);
        }

        // A downloading game, two covers wide: its art and logo, and along the bottom a pause
        // button, what the download is doing, a progress bar and the speed and time left. An
        // "x" in the corner, shown while pointed at, cancels it. Follows the download live.
        private UIElement DownloadCard(Game game, DownloadQueueItem item)
        {
            double width = RecentWidth * 2 + Gap;
            var frame = new Grid { Width = width, Height = RecentHeight, ClipToBounds = true, Background = Brushes.Black, Cursor = Cursors.Hand };

            string art = FullPath(game.BackgroundImage) ?? FullPath(game.CoverImage);
            Image picture = null;
            if (art != null)
            {
                picture = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.Fant);
                StoreImages.Show(picture, art, 640);
                frame.Children.Add(picture);
            }

            string logo = null;
            try
            {
                string path = GameLogoControl.LogoPath(_api, game.Id);
                if (System.IO.File.Exists(path))
                    logo = path;
            }
            catch
            {
                logo = null;
            }
            var top = new Grid { Height = RecentHeight - 84, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            if (logo != null)
            {
                var mark = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(28, 18, 28, 10) };
                RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.Fant);
                StoreImages.Show(mark, logo, 500);
                top.Children.Add(mark);
            }
            else
            {
                top.Children.Add(new TextBlock
                {
                    Text = game.Name ?? "", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16),
                });
            }
            frame.Children.Add(top);

            // The bottom panel.
            var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xA0, 0x1E, 0x2A, 0x3A), 0));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0x16, 0x1C, 0x26), 1));
            var panel = new DockPanel { Height = 84, VerticalAlignment = VerticalAlignment.Bottom, Background = shade };

            var blue = Color.FromRgb(0x1A, 0x9F, 0xFF);
            var glyph = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var pause = new Border
            {
                Width = 42, Height = 42, Margin = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(blue), CornerRadius = new CornerRadius(2), Child = glyph,
            };
            pause.MouseEnter += (s, e) => pause.Opacity = 0.85;
            pause.MouseLeave += (s, e) => pause.Opacity = 1;
            pause.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _downloads?.TogglePause(game.Id);
            };
            DockPanel.SetDock(pause, Dock.Left);
            panel.Children.Add(pause);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var state = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Opacity = 0.85 };
            var track = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 6), Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)) };
            var fill = new Border
            {
                Background = new SolidColorBrush(blue),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(0, 1),
            };
            track.Children.Add(fill);
            var detail = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xCB, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis };
            words.Children.Add(state);
            words.Children.Add(track);
            words.Children.Add(detail);
            panel.Children.Add(words);
            frame.Children.Add(panel);

            // Cancel, in the corner while pointed at.
            var cancel = new Border
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 8, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x18, 0x1C, 0x24)), CornerRadius = new CornerRadius(13),
                Visibility = Visibility.Collapsed, ToolTip = "Cancel download",
                Child = new TextBlock
                {
                    Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            cancel.MouseEnter += (s, e) => cancel.Background = new SolidColorBrush(Color.FromRgb(0xA8, 0x32, 0x32));
            cancel.MouseLeave += (s, e) => cancel.Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x18, 0x1C, 0x24));
            cancel.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _downloads?.Cancel(game.Id);
            };
            frame.Children.Add(cancel);
            frame.MouseEnter += (s, e) => cancel.Visibility = Visibility.Visible;
            frame.MouseLeave += (s, e) => cancel.Visibility = Visibility.Collapsed;

            Action paint = () =>
            {
                double? pct = item.Percent;
                ((ScaleTransform)fill.RenderTransform).ScaleX = (pct ?? 0) / 100.0;
                bool paused = item.Status == DownloadStatus.Paused || item.PauseRequested;
                glyph.Text = paused ? "" : "";
                pause.ToolTip = paused ? "Resume" : "Pause";
                pause.Visibility = item.CanPause ? Visibility.Visible : Visibility.Hidden;
                switch (item.Status)
                {
                    case DownloadStatus.Queued: state.Text = "QUEUED"; break;
                    case DownloadStatus.Paused: state.Text = "PAUSED"; break;
                    case DownloadStatus.Extracting: state.Text = "INSTALLING"; break;
                    default: state.Text = item.PauseRequested ? "PAUSING" : "DOWNLOADING"; break;
                }
                var parts = new List<string>();
                if (pct.HasValue)
                    parts.Add(pct.Value.ToString("0") + "%");
                if (item.Status == DownloadStatus.Downloading && item.BytesPerSecond > 0)
                {
                    parts.Add(DownloadQueueItem.FormatSpeed(item.BytesPerSecond));
                    double left = item.ProgressMaximum - item.ProgressValue;
                    if (pct.HasValue && item.ProgressMaximum > 100 && left > 0)
                        parts.Add(Remaining(TimeSpan.FromSeconds(left / item.BytesPerSecond)));
                }
                detail.Text = parts.Count > 0 ? string.Join("  •  ", parts) : " ";
            };
            paint();
            // One repaint waiting at a time, whatever number of changes arrive meanwhile.
            int pending = 0;
            PropertyChangedEventHandler changed = (s, e) =>
            {
                if (e.PropertyName != nameof(DownloadQueueItem.Summary) && e.PropertyName != nameof(DownloadQueueItem.PauseRequested))
                    return;
                if (System.Threading.Interlocked.Exchange(ref pending, 1) == 1)
                    return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    pending = 0;
                    paint();
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            frame.Loaded += (s, e) => { item.PropertyChanged -= changed; item.PropertyChanged += changed; paint(); };
            frame.Unloaded += (s, e) => item.PropertyChanged -= changed;

            var tile = new Border { Margin = new Thickness(0, 0, Gap, Gap), Child = frame };
            tile.MouseLeftButtonUp += (s, e) => Open(game);
            TileShine.Apply(tile, frame, 1.06, picture ?? new FrameworkElement());
            AttachGameMenu(tile, game);
            return tile;
        }

        private static string Remaining(TimeSpan left)
        {
            if (left.TotalMinutes < 1)
                return "less than a minute remaining";
            if (left.TotalHours < 1)
                return $"{Math.Ceiling(left.TotalMinutes):0} min remaining";
            return $"{(int)left.TotalHours} hr {left.Minutes} min remaining";
        }
    }
}
