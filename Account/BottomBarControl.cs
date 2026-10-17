using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using RomM.Downloads;
using RomM.Saves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RomM.Account
{
    /// <summary>
    /// The bar along the bottom of the window, as in Steam: "Add a Game" on the left, and in the
    /// middle the downloads' progress ("Downloading 1 of 2", the game's icon, a bar and the
    /// percentage), or "Manage Downloads" when nothing is downloading. Clicking the middle
    /// opens the library's Downloads page. A theme places it with
    /// &lt;ContentControl x:Name="RomM_BottomBar" /&gt; along the bottom of the main window.
    /// </summary>
    public class BottomBarControl : PluginUserControl
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Brush Fill = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));

        private readonly IPlayniteAPI _api;
        private readonly CloudStatusModel _model;
        private readonly DownloadQueueController _downloads;
        private readonly Action<string> _showLibraryPage;
        private readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        private readonly Image _icon = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _percent = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Grid _track = new Grid { Height = 3, Margin = new Thickness(0, 4, 0, 0) };
        private readonly ColumnDefinition _done = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        private readonly ColumnDefinition _left = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        private readonly StackPanel _progress = new StackPanel { Width = 580, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _idle = new TextBlock { Text = "Manage Downloads", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        private Guid _iconFor = Guid.Empty;
        private Popup _addMenu;

        public BottomBarControl(IPlayniteAPI api, CloudStatusModel model, DownloadQueueController downloads, Action<string> showLibraryPage)
        {
            _api = api;
            _model = model;
            _downloads = downloads;
            _showLibraryPage = showLibraryPage;
            Focusable = false;

            foreach (TextBlock text in new[] { _status, _percent, _idle })
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.Fant);

            // Left: Add a Game.
            var plus = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            plus.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var addText = new TextBlock { Text = "Add a Game", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            addText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var addRow = new StackPanel { Orientation = Orientation.Horizontal };
            addRow.Children.Add(plus);
            addRow.Children.Add(addText);
            var add = new Border { Padding = new Thickness(14, 0, 14, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = addRow, Opacity = 0.75 };
            add.MouseEnter += (s, e) => add.Opacity = 1;
            add.MouseLeave += (s, e) => add.Opacity = 0.75;
            add.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                OpenAddMenu(add);
            };

            // Middle: the downloads.
            _track.ColumnDefinitions.Add(_done);
            _track.ColumnDefinitions.Add(_left);
            var doneBar = new Border { Background = Fill };
            var leftBar = new Border { Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) };
            Grid.SetColumn(leftBar, 1);
            _track.Children.Add(doneBar);
            _track.Children.Add(leftBar);
            var words = new Grid();
            words.Children.Add(_status);
            words.Children.Add(_percent);
            _progress.Children.Add(words);
            _progress.Children.Add(_track);

            var middleRow = new StackPanel { Orientation = Orientation.Horizontal };
            middleRow.Children.Add(_icon);
            middleRow.Children.Add(_progress);
            middleRow.Children.Add(_idle);
            var middle = new Border { HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 0, 12, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = middleRow, ToolTip = "Downloads" };
            middle.MouseEnter += (s, e) => _idle.Opacity = 1;
            middle.MouseLeave += (s, e) => _idle.Opacity = 0.6;
            middle.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _showLibraryPage?.Invoke("Downloads");
            };

            var bar = new Grid { Height = 40 };
            bar.Children.Add(add);
            add.HorizontalAlignment = HorizontalAlignment.Left;
            bar.Children.Add(middle);

            Content = new Border
            {
                Background = new SolidColorBrush(global::RomM.Store.ThemeColors.Mix(global::RomM.Store.ThemeColors.MainDark, global::RomM.Store.ThemeColors.Main, 0.25)),
                BorderBrush = global::RomM.Store.ThemeColors.Edge(),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = bar,
            };

            _tick.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { Refresh(); _tick.Start(); };
            Unloaded += (s, e) => _tick.Stop();
        }

        private static bool Active(DownloadQueueItem item)
        {
            return item.Status == DownloadStatus.Queued || item.Status == DownloadStatus.Downloading
                || item.Status == DownloadStatus.Extracting || item.Status == DownloadStatus.Paused;
        }

        private void Refresh()
        {
            try
            {
                List<DownloadQueueItem> active = _downloads?.ViewModel?.Items?.Where(Active).ToList() ?? new List<DownloadQueueItem>();
                if (active.Count == 0)
                {
                    // As Steam: what finished this session, else the way to the downloads.
                    int done = _downloads?.ViewModel?.Completed?.Count ?? 0;
                    _idle.Text = done > 0 ? $"Downloads - {done} of {done} Item{(done == 1 ? "" : "s")} Complete" : "Manage Downloads";
                    _icon.Visibility = Visibility.Collapsed;
                    _progress.Visibility = Visibility.Collapsed;
                    _idle.Visibility = Visibility.Visible;
                    return;
                }

                // The one in progress: downloading or unpacking, else the first waiting.
                DownloadQueueItem current = active.FirstOrDefault(i => i.Status == DownloadStatus.Downloading || i.Status == DownloadStatus.Extracting) ?? active[0];
                int position = active.IndexOf(current) + 1;
                string verb = current.Status == DownloadStatus.Extracting ? "Installing"
                    : current.Status == DownloadStatus.Paused ? "Paused"
                    : current.Status == DownloadStatus.Queued ? "Queued"
                    : "Downloading";
                _status.Text = $"{verb} {position} of {active.Count}  ·  {current.GameName}";
                double? percent = current.Percent;
                _percent.Text = percent.HasValue ? $"{percent.Value:0}%" : "";
                double share = percent.HasValue ? percent.Value / 100.0 : 0;
                _done.Width = new GridLength(share, GridUnitType.Star);
                _left.Width = new GridLength(1 - share, GridUnitType.Star);

                if (_iconFor != current.GameId)
                {
                    _iconFor = current.GameId;
                    _icon.Source = null;
                    Game game = _api.Database.Games.Get(current.GameId);
                    string path = game?.Icon == null ? null : (game.Icon.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? game.Icon : _api.Database.GetFullFilePath(game.Icon));
                    if (!string.IsNullOrEmpty(path))
                        global::RomM.Store.StoreImages.Show(_icon, path, 44);
                }

                _icon.Visibility = Visibility.Visible;
                _progress.Visibility = Visibility.Visible;
                _idle.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                s_log.Debug($"[BottomBar] {ex.Message}");
            }
        }

        // ---- Add a Game -----------------------------------------------------------------------

        // Playnite's own commands for adding games, found on the main window's view model. Only
        // those this version of Playnite has are offered.
        private ICommand MainCommand(params string[] names)
        {
            try
            {
                object model = Application.Current?.MainWindow?.DataContext;
                if (model == null)
                    return null;
                foreach (string name in names)
                {
                    PropertyInfo property = model.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property?.GetValue(model) is ICommand command)
                        return command;
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[BottomBar] {ex.Message}");
            }
            return null;
        }

        private void OpenAddMenu(FrameworkElement anchor)
        {
            if (_addMenu != null && _addMenu.IsOpen)
            {
                _addMenu.IsOpen = false;
                return;
            }

            var entries = new List<Tuple<string, Action>>
            {
                Tuple.Create<string, Action>("Browse the Store...", () => _model.OpenStoreHome()),
            };
            ICommand manual = MainCommand("AddCustomGameCommand");
            if (manual != null)
                entries.Add(Tuple.Create<string, Action>("Add a game manually...", () => manual.Execute(null)));
            ICommand installed = MainCommand("AddInstalledGamesCommand");
            if (installed != null)
                entries.Add(Tuple.Create<string, Action>("Scan for installed games...", () => installed.Execute(null)));
            ICommand emulated = MainCommand("AddEmulatedGamesCommand");
            if (emulated != null)
                entries.Add(Tuple.Create<string, Action>("Import emulated games...", () => emulated.Execute(null)));
            ICommand update = MainCommand("UpdateGamesCommand", "UpdateLibraryCommand");
            if (update != null)
                entries.Add(Tuple.Create<string, Action>("Update all libraries", () => update.Execute(null)));

            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            foreach (var entry in entries)
            {
                Action pick = entry.Item2;
                var words = new TextBlock { Text = entry.Item1, FontSize = 13, Foreground = Brushes.White, Opacity = 0.85 };
                var item = new Border { Padding = new Thickness(16, 7, 24, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = words };
                item.MouseEnter += (s, e) => { item.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); words.Opacity = 1; };
                item.MouseLeave += (s, e) => { item.Background = Brushes.Transparent; words.Opacity = 0.85; };
                item.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    _addMenu.IsOpen = false;
                    try
                    {
                        pick();
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[BottomBar] Could not {entry.Item1}: {ex.Message}");
                    }
                };
                list.Children.Add(item);
            }

            _addMenu = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Top,
                VerticalOffset = -2,
                AllowsTransparency = true,
                StaysOpen = false,
                Child = new Border
                {
                    MinWidth = 220,
                    Background = global::RomM.Store.ThemeColors.Menu(),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    Child = list,
                },
            };
            _addMenu.IsOpen = true;
        }
    }
}
