using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using RomM.Games;
using RomM.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.LibraryHome
{
    /// <summary>
    /// The shelves of the library's Home page, after Steam's: each shelf's name is a drop-down
    /// choosing what it shows (Favorites and the user's collections; installed games and each
    /// library; Uncategorized, All Games, Recent Games, Currently Playing, Collections View and
    /// Play Next), or deleting it; "Add shelf" at the bottom adds one; and a shelf is dragged
    /// by its name to a new place. The arrangement is kept (ShelfLayout).
    /// </summary>
    public partial class LibraryHomeView
    {
        private const string ShelfDragFormat = "RomM.LibraryShelf";
        private static readonly Brush DropBlue = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));

        private List<ShelfSetting> _shelves = new List<ShelfSetting>();
        private readonly List<Border> _dropLines = new List<Border>();
        private bool _dragScrollHooked;

        // What a new library's Home shows: as before shelves could be arranged.
        private List<ShelfSetting> DefaultShelves()
        {
            var shelves = new List<ShelfSetting>
            {
                new ShelfSetting { Kind = "recent" },
                new ShelfSetting { Kind = "playing" },
            };
            foreach (GameCollection collection in _collections?.All() ?? new List<GameCollection>())
                shelves.Add(new ShelfSetting { Kind = "collection", CollectionId = collection.Id });
            shelves.Add(new ShelfSetting { Kind = "all" });
            return shelves;
        }

        private void SaveShelves()
        {
            _layout?.Set(_shelves);
        }

        private void BuildShelves(List<Game> games)
        {
            _dropLines.Clear();
            HookDragScroll();

            List<GameCollection> collections = _collections?.All() ?? new List<GameCollection>();
            List<ShelfSetting> kept = _layout?.Get();
            _shelves = kept ?? DefaultShelves();
            // A collection deleted elsewhere takes its shelves with it.
            int before = _shelves.Count;
            _shelves.RemoveAll(s => s.Kind == "collection" && !collections.Any(c => c.Id == s.CollectionId));
            if (kept == null || _shelves.Count != before)
                SaveShelves();

            bool first = _whatsNew == null || _whatsNew.Children.Count == 0;
            foreach (ShelfSetting shelf in _shelves)
            {
                _body.Children.Add(ShelfBlock(shelf, games, collections, first));
                first = false;
            }
            _body.Children.Add(AddShelfButton(games, collections));
        }

        // ---- What a shelf shows ---------------------------------------------------------------

        private string LibraryName(Guid pluginId)
        {
            if (pluginId == Guid.Empty)
                return "Playnite";
            try
            {
                LibraryPlugin library = _api.Addons.Plugins.OfType<LibraryPlugin>().FirstOrDefault(p => p.Id == pluginId);
                if (library != null)
                    return library.Name;
            }
            catch
            {
                // Named below.
            }
            return "Other library";
        }

        private CompletionStatus PlayingStatus()
        {
            return _api.Database.CompletionStatuses.FirstOrDefault(s => string.Equals(s.Name, "Playing", StringComparison.OrdinalIgnoreCase));
        }

        private string ShelfTitle(ShelfSetting shelf, List<GameCollection> collections)
        {
            switch (shelf.Kind)
            {
                case "favorites": return "Favorites";
                case "collection": return collections.FirstOrDefault(c => c.Id == shelf.CollectionId)?.Name ?? "Collection";
                case "installed": return "Installed";
                case "library": return LibraryName(shelf.Library);
                case "uncategorized": return "Uncategorized";
                case "all": return "All Games";
                case "playing": return "Currently Playing";
                case "collections": return "Collections View";
                case "playnext": return "Play Next";
                default: return "Recent Games";
            }
        }

        private static IEnumerable<Game> RecentFirst(IEnumerable<Game> games)
        {
            return games.OrderByDescending(g => g.LastActivity ?? DateTime.MinValue)
                .ThenBy(g => g.SortingName ?? g.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A shelf's games, for the kinds that are a row of covers; null for the others
        /// (Recent Games, All Games and Collections View have layouts of their own).
        /// </summary>
        private List<Game> ShelfGames(ShelfSetting shelf, List<Game> games, List<GameCollection> collections)
        {
            switch (shelf.Kind)
            {
                case "favorites":
                    return RecentFirst(games.Where(g => g.Favorite)).ToList();
                case "collection":
                    var members = new HashSet<Guid>(collections.FirstOrDefault(c => c.Id == shelf.CollectionId)?.Games ?? new List<Guid>());
                    return RecentFirst(games.Where(g => members.Contains(g.Id))).ToList();
                case "installed":
                    return RecentFirst(games.Where(g => g.IsInstalled)).ToList();
                case "library":
                    return RecentFirst(games.Where(g => g.PluginId == shelf.Library)).ToList();
                case "uncategorized":
                    var collected = new HashSet<Guid>(collections.SelectMany(c => c.Games));
                    return games.Where(g => !collected.Contains(g.Id)).OrderBy(g => g.SortingName ?? g.Name, StringComparer.OrdinalIgnoreCase).ToList();
                case "playing":
                    CompletionStatus playing = PlayingStatus();
                    return playing == null ? new List<Game>() : RecentFirst(games.Where(g => g.CompletionStatusId == playing.Id)).ToList();
                case "playnext":
                    // Games not played yet: those marked "Plan to Play" first, then installed
                    // ones, then the most recently added.
                    var planned = _api.Database.CompletionStatuses
                        .Where(s => s.Name != null && (s.Name.IndexOf("plan", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf("not played", StringComparison.OrdinalIgnoreCase) >= 0))
                        .Select(s => s.Id).ToList();
                    return games.Where(g => g.Playtime == 0)
                        .OrderByDescending(g => planned.Contains(g.CompletionStatusId))
                        .ThenByDescending(g => g.IsInstalled)
                        .ThenByDescending(g => g.Added ?? DateTime.MinValue)
                        .Take(30)
                        .ToList();
                default:
                    return null;
            }
        }

        // ---- A shelf --------------------------------------------------------------------------

        private FrameworkElement ShelfBlock(ShelfSetting shelf, List<Game> games, List<GameCollection> collections, bool first)
        {
            string title = ShelfTitle(shelf, collections);
            int? count = null;
            UIElement content;
            FrameworkElement arrows = null;
            UIElement extra = null;

            switch (shelf.Kind)
            {
                case "recent":
                    content = RecentShelf(games, out arrows);
                    break;
                case "all":
                    count = games.Count;
                    content = AllGamesGrid(games, out extra);
                    break;
                case "collections":
                    count = collections.Count;
                    content = collections.Count == 0 ? EmptyShelf("No collections yet. Right-click a game and choose Add to Collection \u203A New collection...")
                        : SideScroll.RowWithHeaderArrows(CollectionTiles(shelf, games, collections), out arrows);
                    break;
                default:
                    List<Game> list = ShelfGames(shelf, games, collections) ?? new List<Game>();
                    if (shelf.Kind != "playnext")
                        count = list.Count;
                    if (list.Count == 0)
                    {
                        content = EmptyShelf(shelf.Kind == "collection"
                            ? "Empty. Right-click a game and choose Add to Collection \u203A " + title + "."
                            : "Nothing here yet.");
                    }
                    else
                    {
                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = LiftRoom };
                        foreach (Game game in list.Take(60))
                            row.Children.Add(Tile(game, shelf.Kind == "playing" && game.LastActivity.HasValue ? Short(game.LastActivity.Value) : null));
                        content = SideScroll.RowWithHeaderArrows(row, out arrows);
                    }
                    break;
            }

            var block = new StackPanel { Margin = new Thickness(0, first ? 0 : 22, 0, 0), AllowDrop = true, Background = Brushes.Transparent, Tag = shelf };
            Border lineBefore = DropLine();
            Border lineAfter = DropLine();

            // The heading: a grip (while pointed at), the name and its drop-down mark, the count,
            // then the sort (All Games) and the arrows at the right end.
            var grip = new TextBlock
            {
                Text = "\uE76F",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Opacity = 0,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Drag to move this shelf",
            };
            grip.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var name = Text(title, 17, false);
            name.VerticalAlignment = VerticalAlignment.Center;
            var chevron = new TextBlock
            {
                Text = "\uE70D",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Opacity = 0.7,
                Margin = new Thickness(8, 3, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var titleBox = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Choose what this shelf shows, or drag it to move it" };
            titleBox.Children.Add(grip);
            titleBox.Children.Add(name);
            titleBox.Children.Add(chevron);

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(titleBox);
            if (count.HasValue)
            {
                var number = Text($" ({count.Value})", 17, false);
                number.Opacity = 0.55;
                number.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(number);
            }
            if (extra != null)
                head.Children.Add(extra);

            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true, Background = Brushes.Transparent };
            if (arrows != null)
            {
                DockPanel.SetDock(arrows, Dock.Right);
                heading.Children.Add(arrows);
            }
            heading.Children.Add(head);
            heading.MouseEnter += (s, e) => grip.Opacity = 0.6;
            heading.MouseLeave += (s, e) => grip.Opacity = 0;
            if (shelf.Kind == "collection")
            {
                GameCollection collection = collections.FirstOrDefault(c => c.Id == shelf.CollectionId);
                if (collection != null)
                    heading.ContextMenu = CollectionMenu(collection);
            }

            block.Children.Add(lineBefore);
            block.Children.Add(heading);
            block.Children.Add(content);
            block.Children.Add(lineAfter);

            WireShelfDrag(titleBox, block, shelf, games, collections);
            WireShelfDrop(block, shelf, lineBefore, lineAfter);
            return block;
        }

        private static UIElement EmptyShelf(string words)
        {
            var hint = Text(words, 12, false);
            hint.Opacity = 0.55;
            hint.Margin = new Thickness(0, 0, 0, 6);
            return hint;
        }

        // Every game, in the chosen order, with the "SORT BY" choice for the heading.
        private UIElement AllGamesGrid(List<Game> games, out UIElement sortChoice)
        {
            IEnumerable<Game> all;
            switch (_sort)
            {
                case 1: all = games.OrderByDescending(g => g.LastActivity ?? DateTime.MinValue).ThenBy(g => g.Name); break;
                case 2: all = games.OrderByDescending(g => g.Added ?? DateTime.MinValue).ThenBy(g => g.Name); break;
                case 3: all = games.OrderByDescending(g => g.Playtime).ThenBy(g => g.Name); break;
                default: all = games.OrderBy(g => g.SortingName ?? g.Name, StringComparer.OrdinalIgnoreCase); break;
            }

            var sort = new ComboBox { MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
            foreach (string choice in SortChoices)
                sort.Items.Add(choice);
            sort.SelectedIndex = _sort;
            sort.SelectionChanged += (s, e) =>
            {
                _sort = Math.Max(0, sort.SelectedIndex);
                Build();
            };
            var sortRow = new StackPanel { Orientation = Orientation.Horizontal };
            var label = Text("SORT BY", 11, false);
            label.Opacity = 0.55;
            label.Margin = new Thickness(26, 0, 8, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            sortRow.Children.Add(label);
            sortRow.Children.Add(sort);
            sortChoice = sortRow;

            var panel = new StackPanel();
            var grid = new WrapPanel();
            foreach (Game game in all.Take(MaxAllGames))
                grid.Children.Add(Tile(game, null, AllWidth, AllHeight, AllGap));
            panel.Children.Add(grid);
            if (games.Count > MaxAllGames)
            {
                var more = Text($"Showing the first {MaxAllGames}. Use the list on the left for the rest.", 12, false);
                more.Opacity = 0.6;
                panel.Children.Add(more);
            }
            return panel;
        }

        // Collections View: a tile for each collection; clicking one goes to its shelf, adding
        // one under this shelf when there is none.
        private UIElement CollectionTiles(ShelfSetting shelf, List<Game> games, List<GameCollection> collections)
        {
            var byId = games.ToDictionary(g => g.Id);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = LiftRoom };
            foreach (GameCollection collection in collections)
            {
                Guid collectionId = collection.Id;
                List<Game> members = RecentFirst(collection.Games.Where(byId.ContainsKey).Select(id => byId[id])).ToList();

                var frame = new Grid { Width = 300, Height = 140, ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)) };
                string art = members.Select(g => FullPath(g.BackgroundImage) ?? FullPath(g.CoverImage)).FirstOrDefault(p => p != null);
                if (art != null)
                {
                    var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                    StoreImages.Show(image, art, 600);
                    frame.Children.Add(image);
                }
                var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0, 0, 0), 0.3));
                shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xD0, 0, 0, 0), 1));
                frame.Children.Add(new Border { Background = shade, IsHitTestVisible = false });
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 14, 10), IsHitTestVisible = false };
                words.Children.Add(new TextBlock { Text = collection.Name, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                words.Children.Add(new TextBlock { Text = members.Count == 1 ? "1 game" : $"{members.Count} games", FontSize = 12, Foreground = Brushes.White, Opacity = 0.75 });
                frame.Children.Add(words);

                var tile = new Border { Margin = new Thickness(0, 0, Gap, Gap), Cursor = Cursors.Hand, Child = frame };
                tile.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    GoToCollectionShelf(collectionId, shelf);
                };
                tile.ContextMenu = CollectionMenu(collection);
                TileShine.Apply(tile, frame, 1.03);
                row.Children.Add(tile);
            }
            return row;
        }

        private void GoToCollectionShelf(Guid collectionId, ShelfSetting from)
        {
            if (!_shelves.Any(s => s.Kind == "collection" && s.CollectionId == collectionId))
            {
                int at = _shelves.FindIndex(s => s.Id == from.Id);
                _shelves.Insert(at < 0 ? _shelves.Count : at + 1, new ShelfSetting { Kind = "collection", CollectionId = collectionId });
                SaveShelves();
                Build();
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (UIElement child in _body.Children)
                {
                    if (child is FrameworkElement block && block.Tag is ShelfSetting setting && setting.Kind == "collection" && setting.CollectionId == collectionId)
                    {
                        block.BringIntoView(new Rect(0, 0, block.ActualWidth, Math.Min(block.ActualHeight, 80)));
                        break;
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---- Choosing what a shelf shows ----------------------------------------------------------

        // The drop-down under a shelf's name (or under "Add shelf", with shelf null: a new one).
        private void OpenShelfMenu(FrameworkElement anchor, ShelfSetting shelf, List<Game> games, List<GameCollection> collections)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };

            Action<ShelfSetting> choose = picked =>
            {
                if (shelf == null)
                {
                    _shelves.Add(picked);
                }
                else
                {
                    int at = _shelves.FindIndex(s => s.Id == shelf.Id);
                    if (at < 0)
                        return;
                    picked.Id = shelf.Id;
                    _shelves[at] = picked;
                }
                SaveShelves();
                Build();
            };
            Action<string, ShelfSetting> entry = (label, picked) =>
            {
                bool current = shelf != null && shelf.Kind == picked.Kind && shelf.CollectionId == picked.CollectionId && shelf.Library == picked.Library;
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = current };
                item.Click += (s, e) => choose(picked);
                menu.Items.Add(item);
            };

            // Favorites and the user's collections.
            entry("Favorites", new ShelfSetting { Kind = "favorites" });
            foreach (GameCollection collection in collections)
                entry(collection.Name, new ShelfSetting { Kind = "collection", CollectionId = collection.Id });
            menu.Items.Add(new Separator());

            // Installed games, and each library the games come from.
            entry("Installed", new ShelfSetting { Kind = "installed" });
            foreach (Guid library in games.Select(g => g.PluginId).Distinct().OrderBy(id => LibraryName(id), StringComparer.CurrentCultureIgnoreCase))
                entry(LibraryName(library) + " games", new ShelfSetting { Kind = "library", Library = library });
            menu.Items.Add(new Separator());

            entry("Uncategorized", new ShelfSetting { Kind = "uncategorized" });
            entry("All Games", new ShelfSetting { Kind = "all" });
            entry("Recent Games", new ShelfSetting { Kind = "recent" });
            entry("Currently Playing", new ShelfSetting { Kind = "playing" });
            entry("Collections View", new ShelfSetting { Kind = "collections" });
            entry("Play Next", new ShelfSetting { Kind = "playnext" });
            menu.Items.Add(new Separator());

            if (_collections != null)
            {
                var create = new MenuItem { Header = "New collection..." };
                create.Click += (s, e) =>
                {
                    GameCollection made = _collections.CreateWithPrompt(_api, new Guid[0]);
                    if (made != null)
                        choose(new ShelfSetting { Kind = "collection", CollectionId = made.Id });
                };
                menu.Items.Add(create);
            }
            if (shelf != null)
            {
                var delete = new MenuItem { Header = "Delete this shelf" };
                delete.Click += (s, e) =>
                {
                    _shelves.RemoveAll(x => x.Id == shelf.Id);
                    SaveShelves();
                    Build();
                };
                menu.Items.Add(delete);
            }
            menu.IsOpen = true;
        }

        private UIElement AddShelfButton(List<Game> games, List<GameCollection> collections)
        {
            var words = Text("+  Add shelf", 13, false);
            words.Opacity = 0.75;
            var button = new Border
            {
                Margin = new Thickness(0, 30, 0, 0),
                Padding = new Thickness(14, 8, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                Child = words,
            };
            button.MouseEnter += (s, e) => { button.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)); words.Opacity = 1; };
            button.MouseLeave += (s, e) => { button.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)); words.Opacity = 0.75; };
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                OpenShelfMenu(button, null, games, collections);
            };
            return button;
        }

        // ---- Moving a shelf --------------------------------------------------------------------

        private Border DropLine()
        {
            var line = new Border { Height = 3, Background = DropBlue, Margin = new Thickness(0, 0, 0, 3), Visibility = Visibility.Hidden, IsHitTestVisible = false };
            _dropLines.Add(line);
            return line;
        }

        private void ShowDropLine(Border line)
        {
            foreach (Border other in _dropLines)
                other.Visibility = ReferenceEquals(other, line) ? Visibility.Visible : Visibility.Hidden;
        }

        // A press on the name that moves a little starts dragging the shelf; one that does not
        // opens its drop-down.
        private void WireShelfDrag(FrameworkElement handle, FrameworkElement block, ShelfSetting shelf, List<Game> games, List<GameCollection> collections)
        {
            Point? pressedAt = null;
            handle.PreviewMouseLeftButtonDown += (s, e) => pressedAt = e.GetPosition(this);
            handle.PreviewMouseMove += (s, e) =>
            {
                if (pressedAt == null || e.LeftButton != MouseButtonState.Pressed)
                    return;
                Vector moved = e.GetPosition(this) - pressedAt.Value;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                pressedAt = null;
                block.Opacity = 0.45;
                try
                {
                    DragDrop.DoDragDrop(handle, new DataObject(ShelfDragFormat, shelf.Id.ToString()), DragDropEffects.Move);
                }
                finally
                {
                    block.Opacity = 1;
                    ShowDropLine(null);
                }
            };
            handle.MouseLeftButtonUp += (s, e) =>
            {
                if (pressedAt == null)
                    return;   // the end of a drag
                pressedAt = null;
                e.Handled = true;
                OpenShelfMenu(handle, shelf, games, collections);
            };
        }

        // Dropped on the upper half of a shelf: above it; on the lower half: below it.
        private void WireShelfDrop(FrameworkElement block, ShelfSetting shelf, Border lineBefore, Border lineAfter)
        {
            Func<DragEventArgs, bool> below = e => e.GetPosition(block).Y > block.ActualHeight / 2;
            block.DragOver += (s, e) =>
            {
                e.Handled = true;
                if (!e.Data.GetDataPresent(ShelfDragFormat))
                {
                    e.Effects = DragDropEffects.None;
                    return;
                }
                e.Effects = DragDropEffects.Move;
                ShowDropLine(below(e) ? lineAfter : lineBefore);
            };
            block.Drop += (s, e) =>
            {
                e.Handled = true;
                ShowDropLine(null);
                if (!Guid.TryParse(e.Data.GetData(ShelfDragFormat) as string, out Guid moving) || moving == shelf.Id)
                    return;
                bool after = below(e);
                int from = _shelves.FindIndex(x => x.Id == moving);
                if (from < 0)
                    return;
                ShelfSetting dragged = _shelves[from];
                _shelves.RemoveAt(from);
                int to = _shelves.FindIndex(x => x.Id == shelf.Id);
                _shelves.Insert(to < 0 ? from : (after ? to + 1 : to), dragged);
                SaveShelves();
                // Rebuilt once the drag has finished, not from inside it.
                Dispatcher.BeginInvoke(new Action(Build));
            };
        }

        // While dragging, holding the pointer near the top or bottom of the page scrolls it.
        private void HookDragScroll()
        {
            if (_dragScrollHooked)
                return;
            _dragScrollHooked = true;
            _scroll.PreviewDragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(ShelfDragFormat))
                    return;
                double y = e.GetPosition(_scroll).Y;
                const double edge = 60;
                if (y < edge)
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - (edge - y) / 2);
                else if (y > _scroll.ActualHeight - edge)
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + (y - (_scroll.ActualHeight - edge)) / 2);
            };
        }
    }
}
