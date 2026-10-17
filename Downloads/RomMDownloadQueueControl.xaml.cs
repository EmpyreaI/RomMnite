using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
using RomM.Store;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RomM.Downloads
{
    /// <summary>
    /// The Downloads page, laid out as Steam's:
    ///
    ///   A banner for the download under way: the game's art and logo, a graph of the speed, the
    ///   speed now and at its peak, "Downloading data" (bytes) and "Installing files" (unpacking)
    ///   bars, the time left, pause and cancel.
    ///   Up Next: the other downloads, each a row with its picture, size, state, pause and remove.
    ///   Completed: what finished since last cleared, when, and Play; "Clear All".
    ///
    /// The rows are made when the queue changes; what moves (bars, speeds, states) is redrawn
    /// twice a second while the page is showing.
    /// </summary>
    public partial class RomMDownloadQueueControl : UserControl
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Color Blue = Color.FromRgb(0x1A, 0x9F, 0xFF);
        private static readonly Color Green = Color.FromRgb(0x5C, 0xB8, 0x1D);
        private static readonly Color GreenBright = Color.FromRgb(0x75, 0xD1, 0x2E);
        private static readonly Color Grey = Color.FromArgb(0x60, 0x67, 0x70, 0x80);
        private static readonly Color Red = Color.FromRgb(0xA8, 0x32, 0x32);
        private static readonly Color Track = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        private static readonly Color Line = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        private const int GraphSamples = 60;
        private const string Icons = "Segoe Fluent Icons, Segoe MDL2 Assets";

        private readonly DownloadQueueController controller;
        private readonly IPlayniteAPI _api;
        private readonly DispatcherTimer _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly List<Action> _updates = new List<Action>();
        private readonly Queue<double> _speeds = new Queue<double>();
        private double _peak;
        private bool _stale = true;
        private Guid _heroId = Guid.Empty;

        public RomMDownloadQueueControl(DownloadQueueController controller)
        {
            InitializeComponent();
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _api = controller.Api;
            DataContext = controller.ViewModel;

            controller.ViewModel.Items.CollectionChanged += OnQueueChanged;
            if (controller.Recent != null)
                controller.Recent.Changed += () => Dispatcher.BeginInvoke(new Action(() => { _stale = true; Rebuild(); }));

            _tick.Tick += (s, e) => Tick();
            Loaded += (s, e) =>
            {
                Rebuild();
                _tick.Start();
            };
            Unloaded += (s, e) => _tick.Stop();
        }

        private void OnQueueChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _stale = true;
            Dispatcher.BeginInvoke(new Action(Rebuild));
        }

        private void Tick()
        {
            List<DownloadQueueItem> active = Active();
            double speed = active.Sum(i => i.Status == DownloadStatus.Downloading ? i.BytesPerSecond : 0);
            _speeds.Enqueue(speed);
            while (_speeds.Count > GraphSamples)
                _speeds.Dequeue();
            if (speed > _peak)
                _peak = speed;
            if (active.Count == 0)
                _peak = 0;

            // The banner follows the first download under way; when that changes, the page is redrawn.
            Guid hero = Hero(active)?.GameId ?? Guid.Empty;
            if (_stale || hero != _heroId)
            {
                Rebuild();
                return;
            }
            RunUpdates();
        }

        private void RunUpdates()
        {
            foreach (Action update in _updates)
            {
                try
                {
                    update();
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Downloads] {ex.Message}");
                }
            }
        }

        private List<DownloadQueueItem> Active() =>
            controller.ViewModel.Items.Where(i => i.Status != DownloadStatus.Canceled && i.Status != DownloadStatus.Failed && i.Status != DownloadStatus.Completed).ToList();

        // The one the banner shows: downloading or unpacking first, then paused, then the oldest queued.
        private static DownloadQueueItem Hero(List<DownloadQueueItem> active) =>
            active.Where(i => i.Status == DownloadStatus.Downloading || i.Status == DownloadStatus.Extracting).OrderBy(i => i.QueuedOn).FirstOrDefault()
            ?? active.Where(i => i.Status == DownloadStatus.Paused).OrderBy(i => i.QueuedOn).FirstOrDefault()
            ?? active.OrderBy(i => i.QueuedOn).FirstOrDefault();

        // ---- Building ---------------------------------------------------------------------

        private void Rebuild()
        {
            _stale = false;
            _updates.Clear();
            try
            {
                List<DownloadQueueItem> active = Active();
                DownloadQueueItem hero = Hero(active);
                _heroId = hero?.GameId ?? Guid.Empty;

                var page = new DockPanel { LastChildFill = true };
                var banner = Banner(hero);
                DockPanel.SetDock(banner, Dock.Top);
                page.Children.Add(banner);

                var body = new StackPanel { Margin = new Thickness(14, 18, 14, 24) };

                List<DownloadQueueItem> next = active.Where(i => i != hero).OrderBy(i => i.QueuedOn).ToList();
                body.Children.Add(Heading("Up Next", next.Count, null));
                if (next.Count == 0)
                    body.Children.Add(Hint("There are no downloads in the queue"));
                foreach (DownloadQueueItem item in next)
                    body.Children.Add(QueueRow(item));

                var done = (controller.Recent?.Completed() ?? new List<KeyValuePair<Guid, DateTime>>())
                    .Where(p => !active.Any(i => i.GameId == p.Key))
                    .Select(p => Tuple.Create(_api.Database.Games.Get(p.Key), p.Value))
                    .Where(t => t.Item1 != null)
                    .ToList();
                var clear = done.Count == 0 ? null : FlatButton("Clear All", Grey, () => controller.Recent?.ClearCompleted());
                var completedHead = Heading("Completed", done.Count, clear);
                completedHead.Margin = new Thickness(0, 30, 0, 10);
                body.Children.Add(completedHead);
                if (done.Count == 0)
                    body.Children.Add(Hint("Games you download show up here once they are installed"));
                foreach (var pair in done)
                    body.Children.Add(CompletedRow(pair.Item1, pair.Item2));

                page.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
                Root.Content = page;
                RunUpdates();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Downloads] Could not draw the downloads page: {ex.Message}");
            }
        }

        // The banner across the top: the download under way, or a quiet strip when there is none.
        private FrameworkElement Banner(DownloadQueueItem item)
        {
            var frame = new Grid { Height = item == null ? 64 : 200, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22)) };
            if (item == null)
            {
                var none = Text("Nothing is downloading", 13, false);
                none.Opacity = 0.6;
                none.VerticalAlignment = VerticalAlignment.Center;
                none.Margin = new Thickness(18, 0, 0, 0);
                frame.Children.Add(none);
                return frame;
            }

            Game game = _api.Database.Games.Get(item.GameId);
            string art = FullPath(game?.BackgroundImage) ?? FullPath(game?.CoverImage);
            if (art != null)
            {
                var picture = new Image { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.Fant);
                StoreImages.Show(picture, art, 1600);
                frame.Children.Add(picture);
            }
            // Darker towards the right, where the numbers are.
            var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0x10, 0x14, 0x1C), 0));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, 0x10, 0x14, 0x1C), 0.45));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0x10, 0x14, 0x1C), 0.62));
            frame.Children.Add(new Border { Background = shade });

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(560) });
            frame.Children.Add(columns);

            // The logo (or the name), left.
            string logo = null;
            try
            {
                string path = game == null ? null : GameLogoControl.LogoPath(_api, game.Id);
                if (path != null && File.Exists(path))
                    logo = path;
            }
            catch
            {
                logo = null;
            }
            FrameworkElement mark;
            if (logo != null)
            {
                var image = new Image { Stretch = Stretch.Uniform, MaxWidth = 340, MaxHeight = 120, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40, 0, 20, 0) };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                StoreImages.Show(image, logo, 600);
                mark = image;
            }
            else
            {
                mark = new TextBlock
                {
                    Text = item.GameName ?? game?.Name ?? "", FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40, 0, 20, 0),
                };
            }
            mark.Cursor = Cursors.Hand;
            mark.MouseLeftButtonUp += (s, e) => OpenGame(item.GameId);
            columns.Children.Add(mark);

            // The speed graph, by the numbers.
            var graph = new Canvas { Width = 150, Height = 150, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 16, 0), ClipToBounds = true };
            Grid.SetColumn(graph, 1);
            columns.Children.Add(graph);
            _updates.Add(() => DrawGraph(graph));

            // The numbers.
            var panel = new StackPanel { Margin = new Thickness(0, 18, 18, 14) };
            Grid.SetColumn(panel, 2);
            columns.Children.Add(panel);

            var stats = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            TextBlock network = Stat(stats, "NETWORK", Blue);
            TextBlock peak = Stat(stats, "PEAK", Color.FromRgb(0x2A, 0x6F, 0xB8));
            panel.Children.Add(stats);

            TextBlock dataAmount = BarHeading(panel, "Downloading data", 0);
            Border dataBar = Bar(panel, Blue);
            TextBlock unpackAmount = BarHeading(panel, "Installing files", 8);
            Border unpackBar = Bar(panel, Green);

            var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var pauseGlyph = Glyph("", 15);
            var pause = IconButton(pauseGlyph, Blue, () => controller.TogglePause(item.GameId));
            DockPanel.SetDock(pause, Dock.Right);
            foot.Children.Add(pause);
            var cancel = IconButton(Glyph("", 13), Grey, () => controller.Cancel(item.GameId), "Cancel download", Red);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(cancel, Dock.Right);
            foot.Children.Add(cancel);
            var estimate = Text("", 11, false);
            estimate.Opacity = 0.7;
            estimate.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(estimate);
            panel.Children.Add(foot);

            _updates.Add(() =>
            {
                double speed = _speeds.Count > 0 ? _speeds.Last() : 0;
                network.Text = DownloadQueueItem.FormatSpeed(speed);
                peak.Text = DownloadQueueItem.FormatSpeed(_peak);

                bool unpacking = item.Status == DownloadStatus.Extracting;
                double total = item.TotalBytes;
                double got = item.DownloadedBytes;
                dataAmount.Text = total > 0 ? $"{Size(got)} / {Size(total)}" : (item.Status == DownloadStatus.Queued ? "Queued" : "");
                SetBar(dataBar, unpacking ? 1 : total > 0 ? got / total : 0);
                double unpack = unpacking && item.Percent.HasValue ? item.Percent.Value / 100.0 : 0;
                unpackAmount.Text = (unpack * 100).ToString("0") + "%";
                SetBar(unpackBar, unpack);

                bool paused = item.Status == DownloadStatus.Paused || item.PauseRequested;
                pauseGlyph.Text = paused ? "" : "";
                pause.ToolTip = paused ? "Resume" : "Pause";
                pause.Visibility = item.CanPause ? Visibility.Visible : Visibility.Collapsed;

                if (paused)
                    estimate.Text = "Paused";
                else if (unpacking)
                    estimate.Text = "Installing...";
                else if (item.Status == DownloadStatus.Queued)
                    estimate.Text = "Waiting to start";
                else if (item.BytesPerSecond > 0 && total > got)
                    estimate.Text = "Estimated " + Clock(TimeSpan.FromSeconds((total - got) / item.BytesPerSecond)) + " remaining";
                else
                    estimate.Text = item.StatusText ?? "";
            });
            return frame;
        }

        // The speed over the last half minute, as bars rising from the bottom, the newest at the right.
        private void DrawGraph(Canvas graph)
        {
            graph.Children.Clear();
            double[] samples = _speeds.ToArray();
            double top = Math.Max(_peak, 1);
            double width = graph.Width / GraphSamples;
            var brush = new SolidColorBrush(Color.FromArgb(0xD0, Blue.R, Blue.G, Blue.B));
            for (int i = 0; i < samples.Length; i++)
            {
                double height = Math.Max(1, samples[i] / top * graph.Height * 0.9);
                var bar = new Rectangle { Width = Math.Max(1, width - 1), Height = height, Fill = brush };
                Canvas.SetLeft(bar, graph.Width - (samples.Length - i) * width);
                Canvas.SetTop(bar, graph.Height - height);
                graph.Children.Add(bar);
            }
        }

        private FrameworkElement QueueRow(DownloadQueueItem item)
        {
            Game game = _api.Database.Games.Get(item.GameId);
            var row = Row(game, item.GameName, out StackPanel words, out TextBlock sub, out DockPanel right);

            var remove = IconButton(Glyph("", 13), Grey, () => controller.Cancel(item.GameId), "Remove from the queue", Red);
            DockPanel.SetDock(remove, Dock.Right);
            right.Children.Add(remove);
            var pauseGlyph = Glyph("", 15);
            var pause = IconButton(pauseGlyph, Grey, () => controller.TogglePause(item.GameId), null, Blue);
            pause.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(pause, Dock.Right);
            right.Children.Add(pause);
            var state = Text("", 11, true);
            state.Opacity = 0.7;
            state.VerticalAlignment = VerticalAlignment.Center;
            state.Margin = new Thickness(0, 0, 18, 0);
            DockPanel.SetDock(state, Dock.Right);
            right.Children.Add(state);

            var bar = new Grid { Height = 3, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0), Background = new SolidColorBrush(Track) };
            var fill = new Border { Background = new SolidColorBrush(Blue), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(0, 1) };
            bar.Children.Add(fill);
            words.Children.Add(bar);

            _updates.Add(() =>
            {
                bool paused = item.Status == DownloadStatus.Paused || item.PauseRequested;
                pauseGlyph.Text = paused ? "" : "";
                pause.ToolTip = paused ? "Resume" : "Pause";
                pause.Visibility = item.CanPause ? Visibility.Visible : Visibility.Collapsed;
                double total = item.TotalBytes;
                sub.Text = total > 0 ? $"{Size(item.DownloadedBytes)} / {Size(total)}" : "";
                SetBar(fill, (item.Percent ?? 0) / 100.0);
                bar.Visibility = item.Status == DownloadStatus.Queued ? Visibility.Collapsed : Visibility.Visible;
                switch (item.Status)
                {
                    case DownloadStatus.Queued: state.Text = "QUEUED"; break;
                    case DownloadStatus.Paused: state.Text = "PAUSED"; break;
                    case DownloadStatus.Extracting: state.Text = "INSTALLING " + (item.Percent ?? 0).ToString("0") + "%"; break;
                    default: state.Text = (item.Percent.HasValue ? item.Percent.Value.ToString("0") + "%  •  " : "") + DownloadQueueItem.FormatSpeed(item.BytesPerSecond); break;
                }
            });
            return row;
        }

        private FrameworkElement CompletedRow(Game game, DateTime when)
        {
            var row = Row(game, game.Name, out StackPanel words, out TextBlock sub, out DockPanel right);
            sub.Text = game.InstallSize.HasValue && game.InstallSize.Value > 0 ? Size(game.InstallSize.Value) : "";

            bool installed = game.IsInstalled;
            var play = FlatButton(installed ? "▶  PLAY" : "INSTALL", installed ? Green : Blue, () =>
            {
                try
                {
                    if (game.IsInstalled)
                        _api.StartGame(game.Id);
                    else
                        _api.InstallGame(game.Id);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Downloads] Could not start {game.Name}: {ex.Message}");
                }
            }, installed ? GreenBright : (Color?)null);
            play.MinWidth = 130;
            DockPanel.SetDock(play, Dock.Right);
            right.Children.Add(play);
            var stamp = Text("COMPLETED: " + Day(when).ToUpperInvariant() + " " + when.ToString("h:mm tt").ToUpperInvariant(), 11, true);
            stamp.Opacity = 0.65;
            stamp.VerticalAlignment = VerticalAlignment.Center;
            stamp.Margin = new Thickness(0, 0, 18, 0);
            DockPanel.SetDock(stamp, Dock.Right);
            right.Children.Add(stamp);
            return row;
        }

        // A row: the game's picture, its name and a line under it, and room at the right.
        private FrameworkElement Row(Game game, string name, out StackPanel words, out TextBlock sub, out DockPanel right)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Height = 60, Background = Brushes.Transparent };

            // Steam's capsule: the game's fan art, its logo over it (the cover when there is no fan art).
            var picture = new Grid { Width = 128, Height = 60, ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)), Cursor = Cursors.Hand };
            string fanart = FullPath(game?.BackgroundImage);
            string art = fanart ?? FullPath(game?.CoverImage);
            if (art != null)
            {
                var image = new Image { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                StoreImages.Show(image, art, 260);
                picture.Children.Add(image);
            }
            string logo = null;
            try
            {
                string path = game == null ? null : GameLogoControl.LogoPath(_api, game.Id);
                if (path != null && File.Exists(path))
                    logo = path;
            }
            catch
            {
                logo = null;
            }
            if (logo != null && fanart != null)
            {
                // A little shade so a pale logo still reads on bright art.
                picture.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00)) });
                var mark = new Image
                {
                    Stretch = Stretch.Uniform, Margin = new Thickness(12, 7, 12, 7),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.7 },
                };
                RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.Fant);
                StoreImages.Show(mark, logo, 240);
                picture.Children.Add(mark);
            }
            if (game != null)
                picture.MouseLeftButtonUp += (s, e) => OpenGame(game.Id);
            DockPanel.SetDock(picture, Dock.Left);
            row.Children.Add(picture);

            right = new DockPanel { VerticalAlignment = VerticalAlignment.Center, LastChildFill = false, Margin = new Thickness(0, 0, 4, 0) };
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);

            words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 18, 0) };
            var title = Text(name ?? "", 15, false);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(title);
            sub = Text("", 10, true);
            sub.Opacity = 0.55;
            sub.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(sub);
            row.Children.Add(words);

            row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            return row;
        }

        // ---- Pieces ----------------------------------------------------------------------------

        private static TextBlock Text(string text, double size, bool bold)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        private static TextBlock Glyph(string glyph, double size) => new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily(Icons), FontSize = size, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        private static TextBlock Hint(string words)
        {
            var hint = Text(words, 13, false);
            hint.Opacity = 0.55;
            hint.Margin = new Thickness(10, 0, 0, 8);
            return hint;
        }

        // "Up Next (0) ────────────── [extra]"
        private static FrameworkElement Heading(string title, int count, FrameworkElement extra)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
            var name = Text(title, 17, false);
            name.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(name, Dock.Left);
            head.Children.Add(name);
            var number = Text($" ({count})", 13, false);
            number.Opacity = 0.55;
            number.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(number, Dock.Left);
            head.Children.Add(number);
            if (extra != null)
            {
                extra.Margin = new Thickness(12, 0, 0, 0);
                DockPanel.SetDock(extra, Dock.Right);
                head.Children.Add(extra);
            }
            head.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Line), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 2, 0, 0) });
            return head;
        }

        private static TextBlock Stat(Panel into, string label, Color swatch)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 28, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Rectangle { Width = 8, Height = 8, Fill = new SolidColorBrush(swatch), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var name = Text(label, 10, true);
            name.Opacity = 0.75;
            head.Children.Add(name);
            box.Children.Add(head);
            var value = Text("", 13, true);
            box.Children.Add(value);
            into.Children.Add(box);
            return value;
        }

        // "Downloading data                102.2 MB / 17.6 GB": returns the right-hand text.
        private static TextBlock BarHeading(Panel into, string label, double above)
        {
            var head = new DockPanel { Margin = new Thickness(0, above, 0, 0) };
            var amount = Text("", 11, true);
            DockPanel.SetDock(amount, Dock.Right);
            head.Children.Add(amount);
            head.Children.Add(Text(label, 11, false));
            into.Children.Add(head);
            return amount;
        }

        // A thin bar; its fill is set with SetBar.
        private static Border Bar(Panel into, Color color)
        {
            var track = new Grid { Height = 4, Margin = new Thickness(0, 4, 0, 0), Background = new SolidColorBrush(Track) };
            var fill = new Border { Background = new SolidColorBrush(color), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(0, 1) };
            track.Children.Add(fill);
            into.Children.Add(track);
            return fill;
        }

        private static void SetBar(Border fill, double fraction) =>
            ((ScaleTransform)fill.RenderTransform).ScaleX = Math.Max(0, Math.Min(1, fraction));

        private static Color Lighter(Color color) =>
            Color.FromArgb(0xFF, (byte)Math.Min(255, color.R + 30), (byte)Math.Min(255, color.G + 30), (byte)Math.Min(255, color.B + 30));

        private static Border IconButton(TextBlock glyph, Color color, Action click, string tip = null, Color? hover = null)
        {
            var normal = new SolidColorBrush(color);
            var lit = new SolidColorBrush(hover ?? Lighter(color));
            var button = new Border { Width = 32, Height = 32, Background = normal, CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand, Child = glyph, ToolTip = tip };
            button.MouseEnter += (s, e) => button.Background = lit;
            button.MouseLeave += (s, e) => button.Background = normal;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                click();
            };
            return button;
        }

        private static Border FlatButton(string label, Color color, Action click, Color? hover = null)
        {
            var normal = new SolidColorBrush(color);
            var lit = new SolidColorBrush(hover ?? Lighter(color));
            var button = new Border
            {
                Background = normal, CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand, Padding = new Thickness(22, 7, 22, 7),
                Child = new TextBlock { Text = label, FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
            };
            button.MouseEnter += (s, e) => button.Background = lit;
            button.MouseLeave += (s, e) => button.Background = normal;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                click();
            };
            return button;
        }

        private string FullPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : _api.Database.GetFullFilePath(path);
            }
            catch
            {
                return null;
            }
        }

        private void OpenGame(Guid id)
        {
            try
            {
                _api.MainView.SwitchToLibraryView();
                _api.MainView.SelectGame(id);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Downloads] Could not open the game: {ex.Message}");
            }
        }

        private static string Size(double bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (1L << 20)).ToString("0.0") + " MB";
            if (bytes >= 1L << 10) return (bytes / (1L << 10)).ToString("0") + " KB";
            return bytes.ToString("0") + " B";
        }

        private static string Clock(TimeSpan left) =>
            left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{left.Minutes:00}:{left.Seconds:00}";

        private static string Day(DateTime when)
        {
            int days = (DateTime.Now.Date - when.Date).Days;
            if (days == 0) return "Today";
            if (days == 1) return "Yesterday";
            if (days < 7) return when.ToString("dddd");
            return when.ToString(when.Year == DateTime.Now.Year ? "ddd, MMMM d" : "MMM d, yyyy");
        }
    }
}
