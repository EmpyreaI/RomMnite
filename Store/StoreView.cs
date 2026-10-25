using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using RomM.Games;
using RomM.Models.RomM.Platform;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace RomM.Store
{
    /// <summary>
    /// The Store: everything on the RomM server, browsed by platform, whether or not Playnite
    /// has an emulator set up for it. A theme places it over the main content with
    /// &lt;ContentControl x:Name="RomM_Store" Grid.Row="1" /&gt;; it shows itself while
    /// CloudStatus.StoreVisible is on (the STORE tab) and is otherwise collapsed.
    ///
    /// Nothing is cached: platforms and each page of games come from the server when looked at.
    /// </summary>
    public partial class StoreView : PluginUserControl
    {
        private const int PageSize = 60;
        private const double TileWidth = 172;
        private const double CoverHeight = 230;

        // How wide the Store's pages and its navigation bar may grow.
        private const double ContentWidth = 1440;

        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly RomM _plugin;
        private readonly ListBox _platforms = new ListBox();
        private readonly WrapPanel _tiles = new WrapPanel();
        private readonly ScrollViewer _scroll;
        private readonly TextBlock _heading;
        private readonly TextBlock _status;
        private readonly TextBlock _pageLabel;
        private readonly Button _previous = new Button { Content = "\u2039  Previous", Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _next = new Button { Content = "Next  \u203A", Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBox _search = new TextBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid _browse = new Grid();
        private readonly ScrollViewer _gamePage = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };

        // The grid of games with its heading, filters and pager; the Home page shares its place.
        private DockPanel _gridArea;
        private readonly ComboBox _genre = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 8, 0) };
        private readonly ComboBox _series = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 8, 0) };
        private readonly ComboBox _sort = new ComboBox { MinWidth = 170 };
        private bool _fillingFilters;
        private SmoothScroll _gridGlide;
        private SmoothScroll _pageGlide;

        // Marks the "Home" entry in the list on the left.
        private static readonly object HomeTag = new object();

        // How the grid can be ordered: label, the server's column, direction.
        private static readonly string[][] SortChoices =
        {
            new[] { "Name (A to Z)", "name", "asc" },
            new[] { "Name (Z to A)", "name", "desc" },
            new[] { "Recently added", "created_at", "desc" },
            new[] { "Newest release", "first_release_date", "desc" },
            new[] { "Oldest release", "first_release_date", "asc" },
            new[] { "Highest rated", "average_rating", "desc" },
            new[] { "Largest", "fs_size_bytes", "desc" },
        };

        private HashSet<int> _inLibrary = new HashSet<int>();
        private bool _loadedOnce;
        private int _request;       // bumped per fetch, so a slow answer to an old question is dropped
        private int _page;
        private int _total;

        public StoreView(RomM plugin)
        {
            _plugin = plugin;
            Focusable = false;
            SetResourceReference(BackgroundProperty, "MainBackgroundBrush");

            _heading = Text("", 24, true);
            _status = Text("", 14, false);
            _status.Opacity = 0.7;
            _status.Margin = new Thickness(0, 4, 0, 0);
            _pageLabel = Text("", 14, false);
            _pageLabel.VerticalAlignment = VerticalAlignment.Center;

            var root = new Grid();
            // The Store's own navigation bar, as on Steam's store: Home, the browse menus and
            // the search. It lies over the top of the pages, which leave room for it, so the
            // Home page can scroll beneath it as the bar slides away (see UpdateNavBand).
            var nav = BuildStoreNav();
            Panel.SetZIndex(nav, 10);
            root.Children.Add(nav);
            WireSearch();
            _plugin.CloudStatus.StoreTabPressed += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                // STORE pressed while already in the Store: back from a game's page to browsing.
                if (Visibility == Visibility.Visible && _gamePage.Visibility == Visibility.Visible)
                    ShowBrowse();
            }));

            // Browsing (platforms and the grid of games) and a game's own page share the area
            // under the tabs; one is shown at a time, as in Steam.
            root.Children.Add(_browse);
            root.Children.Add(_gamePage);

            // What is being browsed (Home, all games, or one platform) is still kept as the
            // selection of this list, but the list itself is no longer shown: the navigation
            // bar's menus set it.
            _platforms.SelectionChanged += (s, e) =>
            {
                var chosen = _platforms.SelectedItem as ListBoxItem;
                if (chosen == null)
                    return;

                if (ReferenceEquals(chosen.Tag, HomeTag))
                {
                    ShowHome();
                }
                else
                {
                    _page = 0;
                    LoadGames();
                }
            };

            // The grid of games, centred at the same width as the navigation bar.
            var middle = new DockPanel { Margin = new Thickness(16, 8, 16, 0), MaxWidth = ContentWidth };
            _navBand.SizeChanged += (s, e) =>
            {
                // Room for the bar over the grid and a game's page; the Home page makes its own.
                double h = _navBand.ActualHeight;
                middle.Margin = new Thickness(16, 8 + h, 16, 0);
                _gamePageInset.Padding = new Thickness(0, h, 0, 0);
                _categoryInset.Padding = new Thickness(0, h, 0, 0);
                _homeInset.Padding = new Thickness(0, h, 0, 0);
                UpdateNavBand();
            };

            _gridArea = middle;
            _gridArea.Visibility = Visibility.Collapsed;

            var header = new StackPanel { Margin = new Thickness(8, 0, 0, 12) };
            header.Children.Add(_heading);
            header.Children.Add(_status);

            // Narrowing and ordering, as Steam's category menus do.
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            filters.Children.Add(_genre);
            filters.Children.Add(_series);
            filters.Children.Add(_sort);
            header.Children.Add(filters);

            _genre.Items.Add("All genres");
            _genre.SelectedIndex = 0;
            _series.Items.Add("All series");
            _series.SelectedIndex = 0;
            foreach (string[] choice in SortChoices)
                _sort.Items.Add(choice[0]);
            _sort.SelectedIndex = 0;
            SelectionChangedEventHandler refilter = (s, e) =>
            {
                e.Handled = true;   // or the list on the left would take it for its own
                if (_fillingFilters || !_loadedOnce || _gridArea.Visibility != Visibility.Visible)
                    return;
                _page = 0;
                LoadGames();
            };
            _genre.SelectionChanged += refilter;
            _series.SelectionChanged += refilter;
            _sort.SelectionChanged += refilter;
            DockPanel.SetDock(header, Dock.Top);
            middle.Children.Add(header);

            var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 14) };
            pager.Children.Add(_previous);
            pager.Children.Add(_pageLabel);
            pager.Children.Add(_next);
            DockPanel.SetDock(pager, Dock.Bottom);
            middle.Children.Add(pager);
            _previous.Click += (s, e) => { if (_page > 0) { _page--; LoadGames(); } };
            _next.Click += (s, e) => { if ((_page + 1) * PageSize < _total) { _page++; LoadGames(); } };

            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _tiles };
            middle.Children.Add(_scroll);
            _browse.Children.Add(middle);

            _browse.Children.Add(_home);
            InitCategory();

            SmoothScroll.WidenScrollBars(this);
            _gridGlide = SmoothScroll.Attach(_scroll);
            _pageGlide = SmoothScroll.Attach(_gamePage);
            _homeGlide = SmoothScroll.Attach(_home);
            _home.Content = _homeInset;
            _home.ScrollChanged += (s, e) => UpdateNavBand();
            _home.IsVisibleChanged += (s, e) => UpdateNavBand();
            _gamePage.IsVisibleChanged += (s, e) => UpdateNavBand();
            _gamePage.ScrollChanged += (s, e) => UpdateNavBand();
            _gamePage.Content = _gamePageInset;

            // Over everything while Home is first put together (StoreView.Curtain).
            BuildCurtain(root);
            Content = root;

            _plugin.CloudStatus.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Saves.CloudStatusModel.StoreVisible))
                    Dispatcher.BeginInvoke(new Action(ApplyVisibility));
            };

            // From the library: a game's own Store page (its "Store Page" button or menu item).
            _plugin.CloudStatus.StoreGameRequested += romId => Dispatcher.BeginInvoke(new Action(() =>
            {
                // The Store's appearing is this step, not one of its own.
                if (Visibility != Visibility.Visible)
                    _goingToGame = true;
                _pendingRomId = romId;
                if (_platforms.Items.Count > 0)
                    OpenPendingGame();
                // Otherwise the platforms are still loading; the page opens once they are in.
            }));

            // Back and forward to a Store page.
            if (_plugin.History != null)
            {
                _plugin.History.Navigate += entry =>
                {
                    if (entry.Store)
                        Dispatcher.BeginInvoke(new Action(() => NavigateTo(entry)));
                };
            }

            // Signed out: nothing of the last account's browsing is kept.
            _plugin.CloudStatus.AccountLeft += () => Dispatcher.BeginInvoke(new Action(ForgetPlace));

            // The STORE tab's menu: Home.
            _plugin.CloudStatus.StoreDirectoryRequested += which => Dispatcher.BeginInvoke(new Action(() =>
            {
                // Its appearing is this step, not a step of its own.
                if (Visibility != Visibility.Visible)
                    _goingToGame = true;
                ShowDirectory(which);
            }));
            _plugin.CloudStatus.StoreHomeRequested += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                // Its appearing is this step (Home), not a step of its own.
                if (Visibility != Visibility.Visible)
                    _goingToGame = true;
                if (_platforms.Items.Count == 0)
                    return;   // still loading, and Home is where it lands
                if (_platforms.SelectedIndex == 0)
                    ShowHome();
                else
                    _platforms.SelectedIndex = 0;
            }));
            ApplyVisibility();

            // Loaded out of sight once the library is up (StoreView.Preload).
            _plugin.CloudStatus.StorePreloadRequested += () => Dispatcher.BeginInvoke(new Action(Preload));
            if (_plugin.CloudStatus.StorePreloadWanted)
                Dispatcher.BeginInvoke(new Action(Preload));

            // The library can change while the Store is open: a game removed, added, imported.
            _plugin.Playnite.Database.Games.ItemCollectionChanged += (s, e) =>
            {
                bool ours = (e.AddedItems?.Any(g => g.PluginId == _plugin.Id) ?? false)
                         || (e.RemovedItems?.Any(g => g.PluginId == _plugin.Id) ?? false);
                if (ours)
                    Dispatcher.BeginInvoke(new Action(OnLibraryChanged));
            };
            _plugin.Playnite.Database.Games.ItemUpdated += (s, e) =>
            {
                // Installed or uninstalled: the button on its page changes between Install and Play.
                if (_currentRom != null && e.UpdatedItems.Any(u => u.NewData.PluginId == _plugin.Id && u.OldData.IsInstalled != u.NewData.IsInstalled))
                    Dispatcher.BeginInvoke(new Action(OnLibraryChanged));
            };
        }

        // ---- Navigation bar ----------------------------------------------------------------

        private System.Windows.Controls.Primitives.Popup _menu;
        private Border _navBar;     // the bar's contents, at the page's width
        private Border _navBand;    // the band behind them, the window's width
        private readonly TranslateTransform _navShift = new TranslateTransform();

        // The Home page's content sits in this, which leaves room at its top for the bar.
        private readonly Border _homeInset = new Border();

        // The same for a game's page and for the category pages, which the bar now also leaves.
        private readonly Border _gamePageInset = new Border();
        private readonly Border _categoryInset = new Border();

        /// <summary>
        /// On the Home page the bar stays while the featured carousel is in view, then goes up
        /// with the page as it scrolls on, fading as it goes, until it is out of sight. Scrolling
        /// back up brings it back the same way. On every other page it stays put.
        /// </summary>
        // How far the page is scrolled, past the point where the bar starts to go, for it to
        // be gone: it follows the scrolling, over this distance, rather than snapping away.
        private const double BandFadeDistance = 240;

        /// <summary>
        /// The bar slides up and fades out as a page is scrolled on, following the scroll over
        /// <see cref="BandFadeDistance"/>, and comes back the same way. On the Home page
        /// (endless) it starts once the featured carousel has passed under it; on the other
        /// pages (a game's page, a category or search) once a quarter of the page has been
        /// scrolled. The plain grid keeps it. The pages run beneath the bar.
        /// </summary>
        private void UpdateNavBand()
        {
            if (_navBand == null)
                return;
            double height = _navBand.ActualHeight;
            double gone = 0;   // 0 showing .. 1 gone
            if (height > 0)
            {
                if (_gamePage.IsVisible)
                {
                    gone = Progress(_gamePage, _gamePage.ScrollableHeight * 0.25);
                }
                else if (_home.IsVisible)
                {
                    // Where the carousel ends, on the page; without a carousel, the top.
                    double end = height;
                    if (_featuredBlock != null && _featuredBlock.IsVisible && _featuredBlock.IsDescendantOf(_homeInset))
                        end = _featuredBlock.TransformToAncestor(_homeInset).Transform(new Point(0, _featuredBlock.ActualHeight)).Y;
                    gone = Progress(_home, end - height);
                }
                else if (_category.IsVisible)
                {
                    gone = Progress(_category, _category.ScrollableHeight * 0.25);
                }
            }

            // Eased, so it starts and ends gently.
            double eased = gone * gone * (3 - 2 * gone);
            _navShift.Y = -height * eased;
            _navBand.Opacity = 1 - eased;
            _navBand.IsHitTestVisible = gone < 0.5;
            if (gone > 0.5 && _menu != null && _menu.IsOpen)
                CloseNavMenu();
        }

        private static double Progress(ScrollViewer page, double start)
        {
            if (page.ScrollableHeight <= 0)
                return 0;
            return Math.Max(0, Math.Min(1, (page.VerticalOffset - Math.Max(0, start)) / BandFadeDistance));
        }

        private UIElement BuildStoreNav()
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 0, 6, 0) };

            row.Children.Add(NavItem("Home", false, () =>
            {
                if (_platforms.Items.Count > 0)
                {
                    if (_platforms.SelectedIndex == 0)
                        ShowHome();
                    else
                        _platforms.SelectedIndex = 0;
                }
            }));
            row.Children.Add(NavItem("Platforms", true, () => OpenMenu(PlatformMenu, PlatformChoices())));
            row.Children.Add(NavItem("Genres", true, () => OpenMenu(GenreMenu, FilterChoices(_genre))));
            row.Children.Add(NavItem("Series", true, () => OpenMenu(SeriesMenu, FilterChoices(_series))));

            // The wishlist, at the right end, as Steam's.
            if (_plugin.Wishlist != null)
                row.Children.Add(WishlistNavItem());

            // Search, filling what is left of the bar.
            var searchHint = Text("Search the store", 13, false);
            searchHint.Opacity = 0.5;
            searchHint.IsHitTestVisible = false;
            searchHint.VerticalAlignment = VerticalAlignment.Center;
            // Where the caret starts: the box's padding, its border and the text's own 2px inset.
            searchHint.Margin = new Thickness(11, 0, 0, 0);
            // One height for the box and its button (the button is square), so they line up.
            var searchBox = new Grid { Margin = new Thickness(18, 5, 4, 5), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 260, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            searchBox.ColumnDefinitions.Add(new ColumnDefinition());
            searchBox.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _searchBoxHost = searchBox;
            _search.Width = double.NaN;
            _search.VerticalContentAlignment = VerticalAlignment.Center;
            _search.VerticalAlignment = VerticalAlignment.Stretch;   // the full height, as the button
            _search.Template = SquareTextBox();
            _search.Padding = new Thickness(8, 0, 8, 0);
            _search.BorderThickness = new Thickness(1);
            searchBox.Children.Add(_search);
            searchBox.Children.Add(searchHint);
            searchBox.Children.Add(SearchButton());
            _search.TextChanged += (s, e) => searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            row.Children.Add(searchBox);

            // The bar's contents keep to the page's width; the band behind them runs the whole
            // width of the window, as Steam's does.
            _navBar = new Border
            {
                MaxWidth = ContentWidth - 64,
                Margin = new Thickness(32, 0, 32, 0),
                Child = row,
            };

            // In the theme's own colours, solid, since the Home page scrolls beneath it.
            Brush band = ThemeColors.NavBand();
            _navBand = new Border
            {
                BorderBrush = ThemeColors.Edge(),
                BorderThickness = new Thickness(0, 0, 0, 1),
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0, 3, 0, 3),
                Background = band,
                RenderTransform = _navShift,
                Child = _navBar,
            };
            return _navBand;
        }

        // A text box with square corners (the theme's are rounded), keeping the theme's colours,
        // border and padding; its edge lights up in the Store's blue while typing in it.
        private static ControlTemplate SquareTextBox()
        {
            var edge = new FrameworkElementFactory(typeof(Border), "Edge");
            edge.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            edge.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            edge.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            edge.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            edge.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(ScrollViewer.FocusableProperty, false);
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            edge.AppendChild(host);

            var template = new ControlTemplate(typeof(TextBox)) { VisualTree = edge };
            var typing = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            typing.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)), "Edge"));
            template.Triggers.Add(typing);
            return template;
        }

        private UIElement NavItem(string label, bool opens, Action click)
        {
            var text = Text(opens ? label + "  \u25BE" : label, 13, true);
            text.VerticalAlignment = VerticalAlignment.Center;
            // A thin outline (kept transparent when not wanted, so nothing shifts) marks the item
            // whose menu is open, as Steam's bar does.
            var item = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 8, 13, 8),
                Cursor = Cursors.Hand,
                Child = text,
            };
            Action paint = () =>
            {
                bool open = opens && ReferenceEquals(_menuOwner, item) && _menu != null && _menu.IsOpen;
                item.BorderBrush = open ? new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
                item.Background = open
                    ? new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF))
                    : item.IsMouseOver ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            };
            _navPainters.Add(paint);
            item.MouseEnter += (s, e) =>
            {
                paint();
                // With another menu open, pointing at this one swaps to it, as Steam's bar does.
                if (opens && _menu != null && _menu.IsOpen && !ReferenceEquals(_menuOwner, item))
                {
                    _menuOwner = item;
                    click();
                    PaintNav();
                }
            };
            item.MouseLeave += (s, e) => paint();
            item.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                // A second click on the open menu's own item closes it.
                if (opens && _menu != null && _menu.IsOpen && ReferenceEquals(_menuOwner, item))
                {
                    CloseNavMenu();
                    return;
                }
                CloseNavMenu();
                if (opens)
                    _menuOwner = item;
                click();
                PaintNav();
            };
            DockPanel.SetDock(item, Dock.Left);
            return item;
        }

        // The bar's item whose menu is open, and how to repaint every item (the open menu's own
        // item stays lit while its menu is showing).
        private Border _menuOwner;
        private readonly List<Action> _navPainters = new List<Action>();
        private bool _menuHooked;

        private void PaintNav()
        {
            foreach (Action paint in _navPainters)
                paint();
        }

        // Closes the open menu once the pointer has been off both the bar and the menu for a
        // moment (a short grace, so crossing a gap or overshooting an edge does not close it).
        private System.Windows.Threading.DispatcherTimer _menuLeave;
        private int _menuOutside;

        private void WatchMenuLeave()
        {
            if (_menuLeave == null)
            {
                _menuLeave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _menuLeave.Tick += (s, e) =>
                {
                    if (_menu == null || !_menu.IsOpen)
                    {
                        _menuLeave.Stop();
                        return;
                    }
                    var panel = _menu.Child as UIElement;
                    // Held (a middle-click scroll in the menu, a dragged scroll bar): left open.
                    bool held = Mouse.Captured is DependencyObject captured && panel != null && (captured == panel || panel.IsAncestorOf(captured));
                    bool inside = held || _navBand.IsMouseOver || (panel?.IsMouseOver ?? false);
                    _menuOutside = inside ? 0 : _menuOutside + 1;
                    if (_menuOutside >= 3)
                        CloseNavMenu();
                };
            }
            _menuOutside = 0;
            _menuLeave.Start();
        }

        private void CloseNavMenu()
        {
            _menuLeave?.Stop();
            if (_menu != null)
                _menu.IsOpen = false;
            _menuOwner = null;
            PaintNav();
        }

        // The menu stays open while the pointer moves about (so the bar's other items still
        // light up under it), and closes on a click anywhere outside it and the bar, on Escape,
        // or when the window loses focus.
        private void HookMenuClosing()
        {
            if (_menuHooked)
                return;
            Window window = Window.GetWindow(this);
            if (window == null)
                return;
            _menuHooked = true;
            window.PreviewMouseDown += (s, e) =>
            {
                if (_menu != null && _menu.IsOpen && !_navBar.IsMouseOver && !(_menu.Child?.IsMouseOver ?? false))
                    CloseNavMenu();
            };
            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _menu != null && _menu.IsOpen)
                    CloseNavMenu();
            };
            window.Deactivated += (s, e) => CloseNavMenu();
        }

        // The entries of a menu: what to show, whether it is the current choice, what picking it does.
        private sealed class MenuChoice
        {
            public string Label;
            public string Name;      // the bare name, for tiles and chips (Label may carry a count)
            public bool Current;
            public bool Special;     // "All games", "All genres": in the list, never a tile
            public object Tag;       // a platform's RomMPlatform
            public Action Pick;
        }

        private List<MenuChoice> PlatformChoices()
        {
            var choices = new List<MenuChoice>();
            for (int i = 1; i < _platforms.Items.Count; i++)   // 0 is Home
            {
                int index = i;
                var entry = (ListBoxItem)_platforms.Items[i];
                choices.Add(new MenuChoice
                {
                    Label = entry.Content?.ToString() ?? "",
                    Name = (entry.Tag as RomMPlatform)?.PlayniteName,
                    Special = !(entry.Tag is RomMPlatform),
                    Tag = entry.Tag,
                    Current = _platforms.SelectedIndex == i,
                    Pick = () =>
                    {
                        // Choosing from the bar starts afresh: no genre, series or search carried over.
                        ClearFilters(null);

                        // A platform has a page of its own; "All games" keeps the plain grid.
                        if (entry.Tag is RomMPlatform chosenPlatform)
                        {
                            ShowCategory(CategoryKind.Platform, chosenPlatform.PlayniteName, chosenPlatform);
                            return;
                        }
                        // Every game: a page laid out as the categories are.
                        ShowAllGames();
                    },
                });
            }
            return choices;
        }

        // Genres or series: picking one narrows the grid, and from Home goes to all games first.
        private List<MenuChoice> FilterChoices(ComboBox filter)
        {
            var choices = new List<MenuChoice>();
            for (int i = 0; i < filter.Items.Count; i++)
            {
                int index = i;
                choices.Add(new MenuChoice
                {
                    Label = filter.Items[i]?.ToString() ?? "",
                    Special = i == 0,
                    Current = filter.SelectedIndex == i,
                    Pick = () => PickFilter(filter, index),
                });
            }
            return choices;
        }

        // Choosing a genre or series starts afresh: that one across every platform, with the
        // other menu and the search cleared.
        private void PickFilter(ComboBox filter, int index)
        {
            // A genre or series has a page of its own, as Steam's categories do.
            if (index > 0 && index < filter.Items.Count)
            {
                string value = filter.Items[index]?.ToString();
                ClearFilters(null);
                ShowCategory(ReferenceEquals(filter, _series) ? CategoryKind.Series : CategoryKind.Genre, value, null);
                return;
            }

            // "All genres" / "All series": every game.
            ClearFilters(null);
            ShowAllGames();
        }

        /// <summary>Every game on the server, on a page like a category's.</summary>
        private void ShowAllGames()
        {
            ShowCategory(CategoryKind.All, "All Games", null);
        }

        /// <summary>Every game of a genre, as picking it from the Genres menu does.</summary>
        private void BrowseGenre(string genre)
        {
            if (string.IsNullOrEmpty(genre))
                return;
            int index = -1;
            for (int i = 1; i < _genre.Items.Count; i++)
            {
                if (string.Equals(_genre.Items[i]?.ToString(), genre, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                // Not in the server's list (yet): added, so it can be chosen all the same.
                _fillingFilters = true;
                try { index = _genre.Items.Add(genre); }
                finally { _fillingFilters = false; }
            }
            CloseHoverCard();
            PickFilter(_genre, index);
        }

        // A genre tag that browses its genre when clicked, as Steam's tags do.
        private UIElement GenreChip(string genre, Thickness padding, Thickness margin)
        {
            var normal = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            var lit = new SolidColorBrush(Color.FromArgb(0x60, 0x67, 0xC1, 0xF5));
            var chip = new Border
            {
                Background = normal,
                CornerRadius = new CornerRadius(2),
                Padding = padding,
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = $"Browse {genre} games",
                Child = Text(genre, 11, false),
            };
            chip.MouseEnter += (s, e) => chip.Background = lit;
            chip.MouseLeave += (s, e) => chip.Background = normal;
            chip.MouseLeftButtonDown += (s, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                BrowseGenre(genre);
            };
            return chip;
        }

        // Resets genre, series and search, except the one menu being set. Reloads nothing itself.
        private void ClearFilters(ComboBox keep)
        {
            _fillingFilters = true;
            try
            {
                if (!ReferenceEquals(keep, _genre) && _genre.Items.Count > 0)
                    _genre.SelectedIndex = 0;
                if (!ReferenceEquals(keep, _series) && _series.Items.Count > 0)
                    _series.SelectedIndex = 0;
            }
            finally
            {
                _fillingFilters = false;
            }

            _search.Text = "";
            _searched = "";
            _searchPause?.Stop();
        }

        // Runs the search in the box: on Enter, and shortly after typing stops.
        private System.Windows.Threading.DispatcherTimer _searchPause;
        private string _searched = "";

        private void WireSearch()
        {
            // Typing fills the suggestions under the box (a short pause first, so each key does
            // not ask the server); Enter, or "Advanced Search", opens the full results.
            _searchPause = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchPause.Tick += (s, e) =>
            {
                _searchPause.Stop();
                UpdateSuggestions();
            };
            _search.TextChanged += (s, e) =>
            {
                _searchPause.Stop();
                if (_search.IsKeyboardFocusWithin)
                    _searchPause.Start();
            };
            _search.GotKeyboardFocus += (s, e) =>
            {
                HookSearchClosing();
                UpdateSuggestions();
            };
            _search.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (_suggest == null || !_suggest.IsOpen)
                    UpdateSuggestions();
            };
            _search.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    _searchPause.Stop();
                    CloseSuggestions();
                    RunSearch(true);
                }
                else if (e.Key == Key.Escape)
                {
                    if (_suggest != null && _suggest.IsOpen)
                        CloseSuggestions();
                    else
                        _search.Text = "";
                }
            };

            // Still honoured, for a theme that keeps a search box in the window's bar.
            _plugin.CloudStatus.StoreSearchRequested += text => Dispatcher.BeginInvoke(new Action(() => _search.Text = text));
        }

        private void RunSearch(bool always = false)
        {
            string text = (_search.Text ?? "").Trim();
            if ((text == _searched && !always) || !_loadedOnce)
                return;
            _searched = text;

            // A search has a results page of its own; clearing the box from there goes Home.
            if (text.Length > 0)
            {
                ShowCategory(CategoryKind.Search, text, null);
                return;
            }
            if (_category.Visibility == Visibility.Visible && _catKind == CategoryKind.Search)
            {
                if (_platforms.SelectedIndex == 0)
                    ShowHome();
                else
                    _platforms.SelectedIndex = 0;
                return;
            }

            _page = 0;
            var chosen = _platforms.SelectedItem as ListBoxItem;
            if (chosen != null && ReferenceEquals(chosen.Tag, HomeTag) && _platforms.Items.Count > 1)
            {
                // From Home, a search looks through all games; clearing it changes nothing.
                if (text.Length > 0)
                    _platforms.SelectedIndex = 1;
            }
            else
            {
                LoadGames();
            }
        }

        private static TextBlock Text(string text, double size, bool bold)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        private void ApplyVisibility()
        {
            bool show = _plugin.CloudStatus.StoreVisible;
            // Pictures asked for while it is out of sight wait behind the library's; once it
            // shows, those still waiting are no longer background (ShowPicture).
            s_outOfSight = !show;
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                // Back to the library: a game's page, and any video on it, does not linger.
                ShowBrowse();
                return;
            }

            RefreshLibraryIds();
            // Coming to the Store is a step back can return to: it is where it was left. Not when
            // it is coming to a game's page, which is the step.
            if (!_goingToGame)
                _plugin.History?.Record(_storeCurrent ?? new Account.NavEntry { Store = true, Kind = "home" });
            _goingToGame = false;
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                // Down from the first look, while the platforms load; Home lowers it again.
                if (Host.Length > 0)
                    LowerCurtain();
                LoadPlatforms();
            }
            else if (_gridArea.Visibility == Visibility.Visible)
            {
                // Marks may be stale after a library update.
                LoadGames();
            }
        }

        /// <summary>
        /// The Store loaded before it is opened: the platforms, Home, the genre and series counts
        /// and Home's pictures, all out of sight, the same as on its first opening. Opened while
        /// that is still going, it shows the loading curtain where it has got to; opened after,
        /// it is simply there. Not signed in, or the server not reached: it is tried again as
        /// the Store opens, as before.
        /// </summary>
        private void Preload()
        {
            if (_loadedOnce || Host.Length == 0)
                return;
            s_log.Info("[Store] Loading the store in the background.");
            RefreshLibraryIds();
            _loadedOnce = true;
            LowerCurtain();
            LoadPlatforms();
        }

        // Whether the Store's pictures are being got ready out of sight (it is not showing, or
        // a page is being prefetched): then they go in StoreImages' background lane, behind
        // what is on screen. Static, as some of the pages' builders are.
        private static volatile bool s_outOfSight = true;
        private static volatile bool s_prefetching;

        // A prefetched page's pictures are background for good; the others while the Store is
        // out of sight (asked again until they download, so opening the Store promotes them).
        private static readonly Func<bool> WhileOutOfSight = () => s_outOfSight;

        private static void ShowPicture(Image target, string url, int decodeWidth, string keepIn = null, string fallback = null) =>
            StoreImages.Show(target, url, decodeWidth, keepIn, fallback, s_prefetching ? StoreImages.Always : WhileOutOfSight);

        // Work the user did not ask for (the genre and series counts, a prefetched page's art),
        // for the code it runs, its parallel requests included: it waits for what they are
        // doing, also while the Store shows.
        private static readonly System.Threading.AsyncLocal<bool> s_background = new System.Threading.AsyncLocal<bool>();

        // Before a request to the server: while the Store is out of sight, or for background work,
        // what the user is doing goes first (Foreground: a Store page just opened, a game picked
        // in the library, saves syncing...). Never on the UI thread, which must not wait.
        private void WaitForForeground()
        {
            if ((s_outOfSight || s_background.Value) && !Dispatcher.CheckAccess())
            {
                bool background = s_background.Value;
                global::RomM.Foreground.WaitQuiet(() => s_outOfSight || background);
            }
        }

        // The UI work of a page put together out of sight: after input and drawing, so the
        // library stays responsive while it happens.
        private System.Windows.Threading.DispatcherPriority BuildPriority =>
            s_outOfSight ? System.Windows.Threading.DispatcherPriority.Background : System.Windows.Threading.DispatcherPriority.Normal;

        // Which server games Playnite already has, to mark them.
        private void RefreshLibraryIds()
        {
            var ids = new HashSet<int>();
            try
            {
                foreach (Game game in _plugin.Playnite.Database.Games)
                {
                    // Not games hidden because the signed-in account does not have them.
                    if (game.PluginId == _plugin.Id && _plugin.InAccountLibrary(game) && RomMGameId.TryParse(game.GameId, out int romId, out string _))
                        ids.Add(romId);
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not read the library: {ex.Message}");
            }
            _inLibrary = ids;

            // Platforms with an emulator set up, whose games can be added and played.
            var playable = new HashSet<int>();
            try
            {
                foreach (var mapping in _plugin.Settings?.Mappings ?? Enumerable.Empty<global::RomM.Settings.EmulatorMapping>())
                {
                    if (mapping.Enabled && mapping.IsImportable)
                        playable.Add(mapping.RomMPlatformId);
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not read the platform mappings: {ex.Message}");
            }
            _playable = playable;

            // Again in the background: a platform's files may have been uploaded, or a setup tried.
            var known = _serverPlatforms?.Select(p => p.Id).ToList();
            if (known != null && System.Threading.Interlocked.Exchange(ref _checkingFiles, 1) == 0)
            {
                Task.Run(() =>
                {
                    try
                    {
                        CheckMissingFiles(known);
                    }
                    finally
                    {
                        _checkingFiles = 0;
                    }
                });
            }
        }

        // Whether a game is offered but cannot be played here: not in the library, its platform
        // has no emulator set up, and none can be set up for it (no emulator this knows of for
        // it). A platform an emulator can be set up for is not marked: the game's button offers
        // "Install Emulator".
        private bool Unavailable(RomMRom rom) => MarkOf(rom) != null;

        /// <summary>
        /// The tag a game the Store offers gets when it cannot be played here, or null:
        /// "UNSUPPORTED PLATFORM" when no emulator can be set up for its platform; "MISSING
        /// EMULATOR" when one could, but a file its setup needs (a BIOS, keys, or the emulator
        /// itself) is not on the RomM server.
        /// </summary>
        private string MarkOf(RomMRom rom)
        {
            if (rom == null || _inLibrary.Contains(rom.Id) || Playable(rom))
                return null;
            if (!Supported(rom.PlatformId))
                return "UNSUPPORTED PLATFORM";
            return _missingFiles.ContainsKey(rom.PlatformId) ? "MISSING EMULATOR" : null;
        }

        // Platforms whose emulator could be set up but lacks a file on the server (by id), as last
        // checked: when the Store loads, and again as the library changes (a setup tried).
        private System.Collections.Concurrent.ConcurrentDictionary<int, bool> _missingFiles = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        private int _checkingFiles;

        // Off the UI thread.
        private void CheckMissingFiles(IEnumerable<int> platformIds)
        {
            try
            {
                var ids = platformIds.Where(id => !_playable.Contains(id) && Supported(id)).ToList();
                HashSet<int> missing = ids.Count == 0 ? new HashSet<int>() : _plugin.EmulatorSetup?.PlatformsMissingFiles(ids);
                if (missing == null)
                    return;
                var now = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
                foreach (int id in missing)
                    now[id] = true;
                _missingFiles = now;
                _plugin.NoteMissingFiles(ids, missing);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not check the platforms' files: {ex.Message}");
            }
        }

        // Whether an emulator can be set up for a platform (EmulatorSetup has one for it, or it is
        // Windows), by platform id; worked out once per platform.
        private readonly Dictionary<int, bool> _supported = new Dictionary<int, bool>();

        // A platform the user mapped to an emulator of their own counts too (asked each time: the
        // settings can change while the Store is open).
        private bool Supported(int platformId) => SetUpHere(platformId) || _plugin.MappingForPlatform(platformId) != null;

        private bool SetUpHere(int platformId)
        {
            if (_supported.TryGetValue(platformId, out bool known))
                return known;
            bool supported = false;
            try
            {
                var platform = _plugin.Settings?.RomMPlatforms?.FirstOrDefault(p => p.Id == platformId);
                supported = platform != null
                            && (global::RomM.Setup.EmulatorSetup.IsWindowsPlatform(platform) || _plugin.EmulatorSetup?.PlanFor(platformId) != null);
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not tell whether platform {platformId} can be set up: {ex.Message}");
            }
            _supported[platformId] = supported;
            return supported;
        }

        private static readonly Brush UnavailableGrey = new SolidColorBrush(Color.FromRgb(0x5B, 0x63, 0x6E));

        // The "UNSUPPORTED PLATFORM" tag, in the same shape as the "IN LIBRARY" one beside it.
        private static Border UnavailableTag(double fontSize, Thickness padding, Thickness margin, string text = "UNSUPPORTED PLATFORM")
        {
            return new Border
            {
                Background = UnavailableGrey,
                Padding = padding,
                Margin = margin,
                ToolTip = "This game's platform is not set up in Playnite, so it cannot be played here.",
                Child = new TextBlock { Text = text, FontSize = fontSize, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
            };
        }

        private HashSet<int> _playable = new HashSet<int>();

        // Whether a game's platform is set up in Playnite, so the game could be played.
        private bool Playable(RomMRom rom) => rom != null && _playable.Contains(rom.PlatformId);

        // What the Store offers: games not already in the library, those on platforms that are
        // set up coming first (the order is otherwise kept).
        // Each game once, as its best platform's version (StoreView.Versions), and not one the
        // library has on any platform.
        private List<HomeItem> ForStore(IEnumerable<HomeItem> items)
        {
            return OneVersionEach(items, ownedDrops: true)
                .OrderBy(i => Playable(i.Rom) ? 0 : 1)
                .ToList();
        }

        private string Host => (_plugin.Settings?.RomMHost ?? "").TrimEnd('/');

        private void LoadPlatforms()
        {
            if (Host.Length == 0)
            {
                _heading.Text = "Not signed in";
                _status.Text = "Sign in to RomM from the account menu to browse the store.";
                _loadedOnce = false;
                _gridArea.Visibility = Visibility.Visible;
                return;
            }

            _heading.Text = "Store";
            _status.Text = "Loading platforms...";
            string url = Host + "/api/platforms";

            Task.Run(() =>
            {
                try
                {
                    WaitForForeground();
                    using (var response = HttpClientSingleton.Instance.GetAsync(url).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        var all = JsonConvert.DeserializeObject<List<RomMPlatform>>(body) ?? new List<RomMPlatform>();
                        // Platforms added to the server since the plugin last read its list (when
                        // signing in, or before a library update) are not known to the emulator
                        // setup until then: the list is brought up to date here first.
                        KeepPlatformsCurrent(all);
                        var platforms = all
                            .Where(p => p.RomCount > 0)
                            .OrderBy(p => p.PlayniteName, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        _serverPlatforms = platforms;
                        // Which platforms lack a file their emulator needs, before the tags are drawn.
                        CheckMissingFiles(platforms.Select(p => p.Id));
                        Dispatcher.BeginInvoke(new Action(() => ShowPlatforms(platforms)), BuildPriority);
                    }

                    // What the grid can be narrowed by. Optional: without it the menus just
                    // stay at "All".
                    try
                    {
                        WaitForForeground();
                        using (var response = HttpClientSingleton.Instance.GetAsync(Host + "/api/roms/filters").GetAwaiter().GetResult())
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                var values = JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                                var genres = Strings(values, "genres").OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                                // Series are IGDB's collections (Games/SeriesNames); a server
                                // without them (older RomM) has its franchises instead.
                                var series = Strings(values, "collections");
                                _seriesField = series.Count > 0 ? "collections" : "franchises";
                                if (series.Count == 0)
                                    series = Strings(values, "franchises");
                                // Sub-series folded into the series they belong to.
                                series = SetSeriesFamilies(series);
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    _fillingFilters = true;
                                    try
                                    {
                                        foreach (string value in genres) _genre.Items.Add(value);
                                        foreach (string value in series) _series.Items.Add(value);
                                    }
                                    finally
                                    {
                                        _fillingFilters = false;
                                    }
                                }));
                                // How many games each has, so the bar's menus can leave out the
                                // genres and series too small to browse.
                                CountCategories(genres, series);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Store] Could not load the filter lists: {ex.Message}");
                    }
                    finally
                    {
                        // Counted, or nothing to count: the curtain need not wait for it.
                        Dispatcher.BeginInvoke(new Action(() => _categoriesReady = true));
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not load platforms: {ex.Message}");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _status.Text = "Could not reach the RomM server. Open the Store again to retry.";
                        _loadedOnce = false;
                        _gridArea.Visibility = Visibility.Visible;
                    }));
                }
            });
        }

        // Off the UI thread. Adds the server's platforms the plugin's list lacks (kept, not
        // replaced: the mappings point at its entries) and saves the settings when it did.
        private void KeepPlatformsCurrent(List<RomMPlatform> server)
        {
            try
            {
                var settings = _plugin.Settings;
                if (settings == null || server.Count == 0)
                    return;
                var known = new HashSet<int>((settings.RomMPlatforms ?? new List<RomMPlatform>()).Select(p => p.Id));
                var added = server.Where(p => !known.Contains(p.Id)).ToList();
                if (added.Count == 0)
                    return;
                Dispatcher.Invoke(new Action(() =>
                {
                    settings.RomMPlatforms = (settings.RomMPlatforms ?? new List<RomMPlatform>()).Concat(added).ToList();
                    settings.Persist();
                    _supported.Clear();
                }));
                s_log.Info($"[Store] Platforms new to the plugin's list: {string.Join(", ", added.Select(p => p.PlayniteName))}.");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not bring the platform list up to date: {ex.Message}");
            }
        }

        private void ShowPlatforms(List<RomMPlatform> platforms)
        {
            _platforms.Items.Clear();
            _platforms.Items.Add(new ListBoxItem { Content = "Home", Tag = HomeTag, Padding = new Thickness(8, 6, 8, 6) });
            _platforms.Items.Add(new ListBoxItem { Content = $"All games  ({platforms.Sum(p => p.RomCount)})", Tag = null, Padding = new Thickness(8, 6, 8, 6) });
            foreach (var platform in platforms)
                _platforms.Items.Add(new ListBoxItem { Content = $"{platform.PlayniteName}  ({platform.RomCount})", Tag = platform, Padding = new Thickness(8, 6, 8, 6) });

            _platforms.SelectedIndex = 0;   // Home
            if (_pendingRomId > 0)
                OpenPendingGame();
            else if (_pendingDirectory != null)
                ShowDirectory(_pendingDirectory);
        }

        // ---- Back and forward ------------------------------------------------------------------

        private Account.NavEntry _storeCurrent;   // the Store's page now
        private bool _goingToGame;                 // the Store is opening at a game's page
        private Account.NavEntry _browseEntry;    // the browsing page (Home, a category, the grid) under a game's page

        private void RecordStore(Account.NavEntry entry, bool browsing)
        {
            // A Store page opened: for a moment it goes before anything in the background.
            if (_plugin.CloudStatus.StoreVisible)
                global::RomM.Foreground.Touch(TimeSpan.FromSeconds(5));
            _storeCurrent = entry;
            if (browsing)
                _browseEntry = entry;
            if (_plugin.CloudStatus.StoreVisible)
                _plugin.History?.Record(entry);
        }

        // Signed out: the Store goes back to Home, out of sight, with no game's page, search or
        // place to return to, so the next account does not open it where the last one left it.
        private void ForgetPlace()
        {
            try
            {
                ShowBrowse();
                _pendingRomId = 0;
                _pendingDirectory = null;
                _goingToGame = false;
                ClearFilters(null);
                if (_platforms.Items.Count > 0)
                {
                    if (_platforms.SelectedIndex == 0)
                        ShowHome();
                    else
                        _platforms.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not reset after signing out: {ex.Message}");
            }
            // Whatever Home recorded on the way, nothing is left to come back to.
            _storeCurrent = null;
            _browseEntry = null;
        }

        // Shows a Store place that back or forward led to.
        private void NavigateTo(Account.NavEntry entry)
        {
            try
            {
                switch (entry.Kind)
                {
                    case "home":
                        if (_platforms.Items.Count > 0)
                        {
                            if (_platforms.SelectedIndex == 0)
                                ShowHome();
                            else
                                _platforms.SelectedIndex = 0;
                        }
                        break;
                    case "category":
                        if (Enum.TryParse(entry.SubKind, out CategoryKind kind))
                        {
                            RomMPlatform platform = null;
                            foreach (object item in _platforms.Items)
                            {
                                if ((item as ListBoxItem)?.Tag is RomMPlatform candidate && candidate.Id == entry.Id)
                                    platform = candidate;
                            }
                            ShowCategory(kind, entry.Value, platform);
                        }
                        break;
                    case "directory":
                        ShowDirectory(entry.Value);
                        break;
                    case "game":
                        _pendingRomId = entry.Id;
                        if (_platforms.Items.Count > 0)
                            OpenPendingGame();
                        break;
                    case "grid":
                        int index = 1;
                        for (int i = 2; i < _platforms.Items.Count; i++)
                        {
                            if ((_platforms.Items[i] as ListBoxItem)?.Tag is RomMPlatform candidate && candidate.Id == entry.Id)
                                index = i;
                        }
                        if (_platforms.Items.Count > index)
                        {
                            if (_platforms.SelectedIndex == index)
                                LoadGames();
                            else
                                _platforms.SelectedIndex = index;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not go back to {entry.Kind}: {ex.Message}");
            }
        }

        // A game asked for from the library, waiting for the Store to be ready.
        private int _pendingRomId;

        // Opens the asked-for game's page, from what is known of it or from the server. Games in
        // the library are not listed in the Store, but their pages are there all the same.
        private void OpenPendingGame()
        {
            int romId = _pendingRomId;
            _pendingRomId = 0;
            if (romId <= 0 || Host.Length == 0)
                return;

            if (_raw.TryGetValue(romId, out JObject known))
            {
                RomMRom rom = known.ToObject<RomMRom>();
                if (rom != null)
                {
                    ShowGame(rom);
                    return;
                }
            }

            Task.Run(() =>
            {
                try
                {
                    var data = Get($"/api/roms/{romId}") as JObject;
                    RomMRom rom = data?.ToObject<RomMRom>();
                    if (rom == null)
                        return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _raw[romId] = data;
                        ShowGame(rom);
                    }));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not open the Store page of rom {romId}: {ex.Message}");
                }
            });
        }

        private void LoadGames()
        {
            if (Host.Length == 0 || _platforms.SelectedItem == null)
                return;

            var entry = _platforms.SelectedItem as ListBoxItem;
            if (entry != null && ReferenceEquals(entry.Tag, HomeTag))
                return;

            var platform = entry?.Tag as RomMPlatform;
            string term = (_search.Text ?? "").Trim();
            int request = ++_request;
            int page = _page;

            _heading.Text = platform?.PlayniteName ?? "All games";
            _status.Text = "Loading...";
            RecordStore(new Account.NavEntry { Store = true, Kind = "grid", Id = platform?.Id ?? 0 }, true);
            ShowBrowse();
            HideCategory();
            _home.Visibility = Visibility.Collapsed;
            _gridArea.Visibility = Visibility.Visible;

            string[] sort = SortChoices[Math.Max(0, Math.Min(SortChoices.Length - 1, _sort.SelectedIndex))];

            // A search is ordered by how well each game matches unless another order was picked.
            string url = $"{Host}/api/roms?limit={PageSize}&offset={page * PageSize}";
            if (term.Length == 0 || _sort.SelectedIndex > 0)
                url += $"&order_by={sort[1]}&order_dir={sort[2]}";
            if (_genre.SelectedIndex > 0)
                url += $"&genres={Uri.EscapeDataString(_genre.SelectedItem.ToString())}";
            if (_series.SelectedIndex > 0)
                url += $"&franchises={Uri.EscapeDataString(_series.SelectedItem.ToString())}";
            if (platform != null)
                url += $"&platform_ids={platform.Id}";
            if (term.Length > 0)
                url += $"&search_term={Uri.EscapeDataString(term)}";

            Task.Run(() =>
            {
                try
                {
                    using (var response = HttpClientSingleton.Instance.GetAsync(url).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        var json = JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        var roms = json["items"]?.ToObject<List<RomMRom>>() ?? new List<RomMRom>();

                        // The untyped form of each game too: the game page reads a good deal
                        // more from it than the importer's model describes.
                        var raw = new Dictionary<int, JObject>();
                        foreach (JToken item in json["items"] as JArray ?? new JArray())
                        {
                            if (item is JObject o && o["id"] != null)
                                raw[(int)o["id"]] = o;
                        }
                        int total = (int?)json["total"] ?? (page * PageSize + roms.Count);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (request == _request)
                            {
                                // Added to what is already known, not swapped for it: games shown
                                // on the Home page keep their data while the grid is browsed.
                                if (_raw.Count > 3000)
                                    _raw.Clear();
                                foreach (var known in raw)
                                    _raw[known.Key] = known.Value;
                                ShowGames(roms, total, term);
                            }
                        }));
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not load games: {ex.Message}");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (request == _request)
                            _status.Text = "Could not load games from the RomM server.";
                    }));
                }
            });
        }

        private Dictionary<int, JObject> _raw = new Dictionary<int, JObject>();

        private void ShowGames(List<RomMRom> roms, int total, string term)
        {
            _total = total;
            _shownRoms = roms;
            _shownTerm = term;
            _tiles.Children.Clear();
            // Games already in the library are not offered; playable platforms come first.
            foreach (var rom in roms.Where(r => !_inLibrary.Contains(r.Id)).OrderBy(r => Playable(r) ? 0 : 1))
                _tiles.Children.Add(BuildTile(rom));
            _gridGlide.ToTop();

            int pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
            _status.Text = total == 0
                ? (term.Length > 0 ? $"Nothing matches \"{term}\"." : "No games.")
                : $"{total} game{(total == 1 ? "" : "s")}{(term.Length > 0 ? $" matching \"{term}\"" : "")}";
            _pageLabel.Text = $"Page {_page + 1} of {pages}";
            _previous.IsEnabled = _page > 0;
            _next.IsEnabled = _page + 1 < pages;
        }

        private UIElement BuildTile(RomMRom rom)
        {
            var cover = new Grid { Width = TileWidth, Height = CoverHeight };
            cover.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(6) });
            var image = new Image { Stretch = Stretch.UniformToFill };
            ShowPicture(image, Url(rom.PathCoverS ?? rom.PathCoverL), 320);
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
            image.Clip = new RectangleGeometry(new Rect(0, 0, TileWidth, CoverHeight), 6, 6);
            cover.Children.Add(image);

            if (_inLibrary.Contains(rom.Id))
            {
                var mark = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x10)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "IN LIBRARY", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
                };
                cover.Children.Add(mark);
            }
            else if (Unavailable(rom))
            {
                var mark = UnavailableTag(10, new Thickness(6, 2, 6, 2), new Thickness(6), MarkOf(rom));
                mark.CornerRadius = new CornerRadius(4);
                mark.HorizontalAlignment = HorizontalAlignment.Left;
                mark.VerticalAlignment = VerticalAlignment.Top;
                cover.Children.Add(mark);
            }

            var name = Text(rom.Name ?? rom.FileNameNoExt ?? "", 13, false);
            name.Margin = new Thickness(2, 6, 2, 0);
            name.MaxHeight = 36;
            name.TextTrimming = TextTrimming.CharacterEllipsis;

            var stack = new StackPanel { Width = TileWidth };
            stack.Children.Add(cover);
            stack.Children.Add(name);

            var tile = new Border { Margin = new Thickness(8), Padding = new Thickness(4), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = stack };
            tile.MouseEnter += (s, e) => tile.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
            tile.MouseLeftButtonUp += (s, e) => ShowGame(rom);
            AttachHoverCard(tile, rom);
            return tile;
        }

        // The full address of something the server gave as a path.
        private string Url(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            return Host + (path.StartsWith("/") ? path : "/" + path);
        }

        // The video playing on a game's page, if any, so it can be stopped when the page goes.
        private MediaElement _video;

        private void StopVideo()
        {
            var video = _video;
            _video = null;
            if (video == null)
                return;

            try
            {
                video.Stop();
                video.Close();
                video.Source = null;
            }
            catch
            {
                // Nothing useful to do if the player refuses to close.
            }
        }

        // What is on screen, so it can be redrawn when the library changes underneath it.
        private RomMRom _currentRom;
        private List<RomMRom> _shownRoms = new List<RomMRom>();
        private string _shownTerm = "";
        private System.Windows.Threading.DispatcherTimer _libraryChanged;

        /// <summary>
        /// Games were added to or removed from Playnite (from the Store, the library, a library
        /// update, anywhere). Redraws whatever is showing so the "in library" marks and the
        /// button on a game's page are right. Many changes in a burst cause one redraw.
        /// </summary>
        private void OnLibraryChanged()
        {
            if (_libraryChanged == null)
            {
                _libraryChanged = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _libraryChanged.Tick += (s, e) =>
                {
                    _libraryChanged.Stop();
                    try
                    {
                        RefreshLibraryIds();
                        if (Visibility != Visibility.Visible)
                            return;   // redrawn anyway when the Store is next opened

                        if (_gamePage.Visibility == Visibility.Visible && _currentRom != null)
                        {
                            // Keep the reader's place on the page.
                            RedrawGame(_currentRom);
                        }
                        else if (_gridArea.Visibility == Visibility.Visible)
                        {
                            double offset = _scroll.VerticalOffset;
                            ShowGames(_shownRoms, _total, _shownTerm);
                            _scroll.ScrollToVerticalOffset(offset);
                        }
                        else
                        {
                            _showFeatured?.Invoke();
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Store] Could not refresh after a library change: {ex.Message}");
                    }
                };
            }

            _libraryChanged.Stop();
            _libraryChanged.Start();
        }

        // The game page's slideshow, and what happens when its video ends.
        private System.Windows.Threading.DispatcherTimer _slideshow;
        private System.Windows.Threading.DispatcherTimer _videoTicker;

        // Windows' own icon font, for the video controls (play, pause, sound).
        private static readonly FontFamily Icons = new FontFamily("Segoe MDL2 Assets");
        private Action _afterVideo;

        private void StopSlideshow()
        {
            _videoTicker?.Stop();
            _videoTicker = null;
            _slideshow?.Stop();
            _slideshow = null;
            _afterVideo = null;
        }

        private void ShowBrowse()
        {
            CloseHoverCard();
            StopHoverVideo();
            StopSlideshow();
            _currentRom = null;
            StopVideo();
            _gamePage.Visibility = Visibility.Collapsed;
            _gamePageInset.Child = null;
            _browse.Visibility = Visibility.Visible;
        }

        private Game LibraryGame(int romId)
        {
            try
            {
                return _plugin.Playnite.Database.Games.FirstOrDefault(g =>
                    g.PluginId == _plugin.Id && RomMGameId.TryParse(g.GameId, out int id, out string _) && id == romId);
            }
            catch
            {
                return null;
            }
        }

        // A game's screenshots. Fan art is not one of them: it is the backdrop (see FanartPath).
        private List<string> MediaPaths(RomMRom rom)
        {
            var paths = new List<string>();
            if (rom.MergedScreenshots != null)
                paths.AddRange(rom.MergedScreenshots.Where(p => !string.IsNullOrEmpty(p)));
            return paths.Distinct().ToList();
        }

        // A game's fan art, or null when the server has none.
        private static string FanartPath(RomMRom rom)
        {
            return string.IsNullOrEmpty(rom.SSMetadata?.FanartPath) ? null : "/assets/romm/resources/" + rom.SSMetadata.FanartPath;
        }

        /// <summary>
        /// A game's page, laid out after Steam's: title; pictures with a strip of thumbnails on
        /// the left and the cover, short description and facts on the right; the install bar
        /// under them; then the full description.
        /// </summary>
        /// <summary>
        /// The game's page drawn again in its new state (added, its emulator set up...), at the
        /// place it was scrolled to. The place is set again once the new page has been laid out:
        /// before then it is shorter than the old one, and the offset would be cut back to the top.
        /// </summary>
        private void RedrawGame(RomMRom rom)
        {
            double offset = _gamePage.VerticalOffset;
            ShowGame(rom);
            _gamePage.ScrollToVerticalOffset(offset);
            Dispatcher.BeginInvoke(new Action(() => _gamePage.ScrollToVerticalOffset(offset)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ShowGame(RomMRom rom)
        {
            _currentRom = rom;
            NoteViewed(rom);
            RecordStore(new Account.NavEntry { Store = true, Kind = "game", Id = rom.Id }, false);
            CloseSuggestions();
            var page = new StackPanel { MaxWidth = ContentWidth, Margin = new Thickness(32, 8, 32, 40) };

            var back = Text("\u2039  Back to " + _heading.Text, 13, false);
            back.Opacity = 0.7;
            back.Cursor = Cursors.Hand;
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.MouseEnter += (s, e) => back.Opacity = 1;
            back.MouseLeave += (s, e) => back.Opacity = 0.7;
            back.MouseLeftButtonUp += (s, e) =>
            {
                ShowBrowse();
                if (_browseEntry != null)
                    RecordStore(_browseEntry, true);
            };
            page.Children.Add(back);

            // On its anniversary (or the days around it), the game's page says so, in gold.
            UIElement anniversary = AnniversaryBanner(rom);
            if (anniversary != null)
                page.Children.Add(anniversary);

            var title = Text(rom.Name ?? rom.FileNameNoExt ?? "", 28, false);
            title.Margin = new Thickness(0, 10, 0, 14);
            if (AnniversaryToday(rom))
            {
                title.Foreground = Gold;
                title.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xF2, 0xC2, 0x4B), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.55 };
            }
            page.Children.Add(title);

            // --- Top block: media left, summary right -----------------------------------------
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StopVideo();
            StopSlideshow();
            CloseHoverCard();
            StopHoverVideo();
            var media = MediaPaths(rom);
            string videoPath = rom.SSMetadata?.VideoPath ?? rom.SSMetadata?.VideoNormalizedPath;
            bool hasVideo = !string.IsNullOrEmpty(videoPath);

            // Its own height (not stretched to the row): the side column is sized from it.
            var mediaColumn = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };

            // A fixed 16:9 stage, as on Steam: pictures and video are fitted inside it, so
            // nothing below moves when switching between them or while one is loading.
            var main = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(main, BitmapScalingMode.Fant);
            var player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Close,
                IsMuted = true,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed,
            };
            var sound = new TextBlock
            {
                Text = "Sound off  \u2014  click to turn on",
                FontSize = 12,
                Foreground = Brushes.White,
                Margin = new Thickness(8, 4, 8, 4),
            };
            var soundTag = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                Child = sound,
            };
            var stage = new Grid { Background = Brushes.Black, Height = 360 };
            stage.Children.Add(main);
            stage.Children.Add(player);
            // Playback controls along the bottom of the picture while a video is showing: play or
            // pause, a position bar to drag, the time, and sound on or off. Shown while the
            // pointer is over the picture, or while the video is paused.
            bool videoPaused = false;
            bool seeking = false;
            bool settingSeek = false;

            var playPause = new TextBlock { Text = "\uE769", FontFamily = Icons, FontSize = 14, Foreground = Brushes.White, Width = 22, TextAlignment = TextAlignment.Center, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var mute = new TextBlock { Text = "\uE74F", FontFamily = Icons, FontSize = 14, Foreground = Brushes.White, Width = 24, TextAlignment = TextAlignment.Center, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Sound on or off" };
            var clock = new TextBlock { Text = "0:00 / 0:00", FontSize = 11, Foreground = Brushes.White, Opacity = 0.85, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var seek = new Slider { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };

            var controlsRow = new DockPanel { Margin = new Thickness(10, 4, 10, 4) };
            DockPanel.SetDock(playPause, Dock.Left);
            DockPanel.SetDock(mute, Dock.Right);
            DockPanel.SetDock(clock, Dock.Right);
            controlsRow.Children.Add(playPause);
            controlsRow.Children.Add(mute);
            controlsRow.Children.Add(clock);
            controlsRow.Children.Add(seek);
            var controls = new Border
            {
                Background = new LinearGradientBrush(Color.FromArgb(0x00, 0, 0, 0), Color.FromArgb(0xC0, 0, 0, 0), 90),
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(0, 14, 0, 0),
                Visibility = Visibility.Collapsed,
                Child = controlsRow,
            };
            stage.Children.Add(controls);

            playPause.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                videoPaused = !videoPaused;
                if (videoPaused)
                    player.Pause();
                else
                    player.Play();
                playPause.Text = videoPaused ? "\uE768" : "\uE769";
            };
            mute.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                player.IsMuted = !player.IsMuted;
                mute.Text = player.IsMuted ? "\uE74F" : "\uE767";
            };
            seek.PreviewMouseDown += (s, e) => seeking = true;
            seek.PreviewMouseUp += (s, e) =>
            {
                seeking = false;
                if (player.NaturalDuration.HasTimeSpan)
                    player.Position = TimeSpan.FromSeconds(seek.Value * player.NaturalDuration.TimeSpan.TotalSeconds);
            };
            seek.ValueChanged += (s, e) =>
            {
                // Dragging follows live; the bar's own updates below do not seek.
                if (!settingSeek && seeking && player.NaturalDuration.HasTimeSpan)
                    player.Position = TimeSpan.FromSeconds(seek.Value * player.NaturalDuration.TimeSpan.TotalSeconds);
            };

            // Full screen: the stage itself moved into a window of its own over the whole screen
            // and back (the video plays on through it), by the button at its top right; inside,
            // Esc or F11 leaves it, the arrow keys step through the media and Space plays or pauses.
            Window fullWindow = null;
            Action<int> fullStep = null;   // the slideshow's previous / next, set once it exists
            Action toggleFull = null;
            var fullIcon = new TextBlock { Text = "\uE740", FontFamily = Icons, FontSize = 15, Foreground = Brushes.White };
            var fullButton = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = "Full screen",
                Visibility = Visibility.Collapsed,
                Child = fullIcon,
            };
            stage.Children.Add(fullButton);
            toggleFull = () =>
            {
                // Moved without the video closing (it would, leaving its place).
                player.UnloadedBehavior = MediaState.Manual;
                if (fullWindow == null)
                {
                    Window owner = Window.GetWindow(this);
                    mediaColumn.Children.Remove(stage);
                    stage.Height = double.NaN;
                    var window = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                        Background = Brushes.Black,
                        ShowInTaskbar = false,
                        Title = rom.Name ?? "RomMnite",
                        Owner = owner,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Content = stage,
                    };
                    if (owner != null)
                    {
                        // Opened on the Store's screen, then filling it.
                        window.Left = owner.Left + 20;
                        window.Top = owner.Top + 20;
                        window.Width = 640;
                        window.Height = 360;
                    }
                    window.Loaded += (s, e) => window.WindowState = WindowState.Maximized;
                    window.PreviewKeyDown += (s, e) =>
                    {
                        if (e.Key == Key.Escape || e.Key == Key.F11)
                            toggleFull();
                        else if (e.Key == Key.Left)
                            fullStep?.Invoke(-1);
                        else if (e.Key == Key.Right)
                            fullStep?.Invoke(1);
                        else if (e.Key == Key.Space && player.Visibility == Visibility.Visible)
                            playPause.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                        else
                            return;
                        e.Handled = true;
                    };
                    window.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(fullWindow, window))
                            toggleFull();
                    };
                    fullWindow = window;
                    fullIcon.Text = "\uE73F";
                    fullButton.ToolTip = "Leave full screen";
                    window.Show();
                    window.Activate();
                }
                else
                {
                    Window window = fullWindow;
                    fullWindow = null;
                    window.Content = null;
                    stage.Height = mediaColumn.ActualWidth > 0 ? Math.Round(mediaColumn.ActualWidth * 9.0 / 16.0) : 360;
                    mediaColumn.Children.Insert(0, stage);
                    fullIcon.Text = "\uE740";
                    fullButton.ToolTip = "Full screen";
                    if (window.IsVisible)
                        window.Close();
                    Window.GetWindow(this)?.Activate();
                }
                Dispatcher.BeginInvoke(new Action(() => player.UnloadedBehavior = MediaState.Close), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            // Full screen with its button only (a double click on the picture does nothing).
            fullButton.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                toggleFull();
            };

            Func<TimeSpan, string> clockText = t => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
            _videoTicker?.Stop();
            _videoTicker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _videoTicker.Tick += (s, e) =>
            {
                fullButton.Visibility = stage.IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
                bool showing = player.Visibility == Visibility.Visible;
                controls.Visibility = showing && (stage.IsMouseOver || videoPaused) ? Visibility.Visible : Visibility.Collapsed;
                if (!showing || !player.NaturalDuration.HasTimeSpan)
                    return;

                TimeSpan length = player.NaturalDuration.TimeSpan;
                clock.Text = clockText(player.Position) + " / " + clockText(length);
                if (!seeking && length.TotalSeconds > 0)
                {
                    settingSeek = true;
                    seek.Value = Math.Min(1, player.Position.TotalSeconds / length.TotalSeconds);
                    settingSeek = false;
                }
                mute.Text = player.IsMuted ? "\uE74F" : "\uE767";
            };
            _videoTicker.Start();
            mediaColumn.Children.Add(stage);
            mediaColumn.SizeChanged += (s, e) =>
            {
                if (e.WidthChanged && e.NewSize.Width > 0)
                    stage.Height = Math.Round(e.NewSize.Width * 9.0 / 16.0);
            };

            player.MediaEnded += (s, e) =>
            {
                // At the end of the video the slideshow moves on to the screenshots; with
                // nothing else to show, the video plays again.
                if (_afterVideo != null)
                {
                    _afterVideo();
                    return;
                }
                player.Position = TimeSpan.Zero;
                player.Play();
            };
            player.MediaFailed += (s, e) =>
                s_log.Warn($"[Store] Could not play the video for {rom.Name}: {e.ErrorException?.Message}");
            soundTag.MouseLeftButtonUp += (s, e) =>
            {
                player.IsMuted = !player.IsMuted;
                sound.Text = player.IsMuted ? "Sound off  \u2014  click to turn on" : "Sound on  \u2014  click to turn off";
                e.Handled = true;
            };

            Action<string> showPicture = path =>
            {
                StopVideo();
                player.Visibility = Visibility.Collapsed;
                soundTag.Visibility = Visibility.Collapsed;
                main.Visibility = Visibility.Visible;
                ShowPicture(main, Url(path), 1280);
            };
            Action showVideo = () =>
            {
                try
                {
                    // Streamed from the server through the local relay, like the library's video.
                    string local = _plugin.VideoRelay.LocalUrl($"{Host}/assets/romm/resources/{videoPath}");
                    if (local == null)
                        return;

                    StopVideo();
                    main.Visibility = Visibility.Collapsed;
                    player.Visibility = Visibility.Visible;
                    soundTag.Visibility = Visibility.Visible;
                    player.IsMuted = true;
                    sound.Text = "Sound off  \u2014  click to turn on";
                    player.Source = new Uri(local, UriKind.Absolute);
                    player.Play();
                    videoPaused = false;
                    playPause.Text = "\uE769";
                    _video = player;
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not start the video for {rom.Name}: {ex.Message}");
                    try { player.Source = null; } catch { }
                }
            };

            // The slideshow: every thumbnail is a step; the pictures move on by themselves every
            // few seconds, as on Steam, unless the pointer is over them or the video is playing.
            var steps = new List<Action>();
            int current = 0;
            bool onVideo = false;
            StackPanel stripRef = null;
            ScrollViewer thumbScroller = null;

            // Scrolls the thumbnail strip sideways to show the current one. (BringIntoView would
            // also scroll the whole page back up to it, under someone reading further down.)
            Action<FrameworkElement> reveal = frame =>
            {
                if (thumbScroller == null || stripRef == null)
                    return;
                double thumbLeft = frame.TranslatePoint(new Point(0, 0), stripRef).X;
                double thumbRight = thumbLeft + frame.ActualWidth;
                if (thumbLeft < thumbScroller.HorizontalOffset)
                    thumbScroller.ScrollToHorizontalOffset(Math.Max(0, thumbLeft - 12));
                else if (thumbRight > thumbScroller.HorizontalOffset + thumbScroller.ViewportWidth)
                    thumbScroller.ScrollToHorizontalOffset(thumbRight - thumbScroller.ViewportWidth + 12);
            };
            Action<int> goTo = index =>
            {
                if (steps.Count == 0)
                    return;
                current = ((index % steps.Count) + steps.Count) % steps.Count;
                steps[current]();
                _slideshow?.Stop();
                _slideshow?.Start();
            };
            fullStep = by => goTo(current + by);

            if (media.Count > 0 || hasVideo)
            {
                var strip = new StackPanel { Orientation = Orientation.Horizontal };
                stripRef = strip;
                Border chosen = null;
                Action<Border> choose = frame =>
                {
                    if (chosen != null)
                        chosen.BorderBrush = Brushes.Transparent;
                    frame.BorderBrush = Brushes.White;
                    chosen = frame;
                };

                if (hasVideo)
                {
                    // The video's thumbnail: the first picture, dimmed, under a play mark.
                    var still = new Image { Width = 116, Height = 65, Stretch = Stretch.UniformToFill, Opacity = 0.6 };
                    ShowPicture(still, Url(media.Count > 0 ? media[0] : (rom.PathCoverL ?? rom.PathCoverS)), 240);
                    var cell = new Grid { Width = 116, Height = 65, Background = Brushes.Black };
                    cell.Children.Add(still);
                    cell.Children.Add(new TextBlock { Text = "\u25B6", FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                    var frame = new Border { BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Margin = new Thickness(0, 6, 6, 0), Cursor = Cursors.Hand, Child = cell };
                    int step = steps.Count;
                    steps.Add(() => { onVideo = true; choose(frame); showVideo(); reveal(frame); });
                    frame.MouseLeftButtonUp += (s, e) => goTo(step);
                    strip.Children.Add(frame);
                    choose(frame);
                }

                foreach (string path in media)
                {
                    var thumb = new Image { Width = 116, Height = 65, Stretch = Stretch.UniformToFill };
                    ShowPicture(thumb, Url(path), 240);
                    var frame = new Border { BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Margin = new Thickness(0, 6, 6, 0), Cursor = Cursors.Hand, Background = Brushes.Black, Child = thumb };
                    string picked = path;
                    int step = steps.Count;
                    steps.Add(() => { onVideo = false; choose(frame); showPicture(picked); reveal(frame); });
                    frame.MouseLeftButtonUp += (s, e) => goTo(step);
                    if (chosen == null)
                        choose(frame);
                    strip.Children.Add(frame);
                }

                // A bar under the thumbnails, when they do not all fit, besides the arrows.
                thumbScroller = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = strip,
                };

                // Arrows either side of the strip, shown only when there is more than fits.
                ScrollViewer scroller = thumbScroller;
                Func<string, int, Border> stripArrow = (glyph, direction) =>
                {
                    var arrowText = Text(glyph, 26, false);
                    arrowText.HorizontalAlignment = HorizontalAlignment.Center;
                    arrowText.VerticalAlignment = VerticalAlignment.Center;
                    var arrow = new Border
                    {
                        Width = 28,
                        Margin = new Thickness(0, 6, direction < 0 ? 4 : 0, 0),
                        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        Cursor = Cursors.Hand,
                        Child = arrowText,
                        Visibility = Visibility.Collapsed,
                    };
                    arrow.MouseEnter += (s, e) => arrow.Background = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
                    arrow.MouseLeave += (s, e) => arrow.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                    arrow.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        // Most of a view's width at a time, so the last thumbnail seen stays in sight.
                        double by = Math.Max(122, scroller.ViewportWidth - 122);
                        scroller.ScrollToHorizontalOffset(Math.Max(0, Math.Min(scroller.ScrollableWidth, scroller.HorizontalOffset + direction * by)));
                    };
                    return arrow;
                };
                Border stripBack = stripArrow("\u2039", -1);
                Border stripForward = stripArrow("\u203A", 1);
                scroller.ScrollChanged += (s, e) =>
                {
                    bool more = scroller.ScrollableWidth > 1;
                    stripBack.Visibility = stripForward.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
                    stripBack.Opacity = scroller.HorizontalOffset > 1 ? 1 : 0.35;
                    stripForward.Opacity = scroller.HorizontalOffset < scroller.ScrollableWidth - 1 ? 1 : 0.35;
                };

                var stripRow = new DockPanel();
                DockPanel.SetDock(stripBack, Dock.Left);
                DockPanel.SetDock(stripForward, Dock.Right);
                stripRow.Children.Add(stripBack);
                stripRow.Children.Add(stripForward);
                stripRow.Children.Add(scroller);
                mediaColumn.Children.Add(stripRow);

                // As on Steam, the video leads and starts by itself, silent.
                if (hasVideo)
                {
                    onVideo = true;
                    showVideo();
                }
                else
                {
                    showPicture(media[0]);
                }

                if (steps.Count > 1)
                {
                    if (hasVideo)
                        _afterVideo = () => goTo(current + 1);

                    _slideshow = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    _slideshow.Tick += (s, e) =>
                    {
                        // Held while the video plays, or while the pointer is on the pictures.
                        if (onVideo || stage.IsMouseOver || (stripRef?.IsMouseOver ?? false) || !IsVisible)
                            return;
                        goTo(current + 1);
                    };
                    _slideshow.Start();

                    // Previous and next over the picture's edges, shown while the pointer is on it.
                    Func<string, int, Border> stageArrow = (glyph, direction) =>
                    {
                        var arrowText = new TextBlock { Text = glyph, FontSize = 44, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
                        var arrow = new Border
                        {
                            Width = 54,
                            HorizontalAlignment = direction < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Center,
                            Height = 120,
                            Background = Brushes.Transparent,
                            Cursor = Cursors.Hand,
                            Opacity = 0,
                            Child = arrowText,
                        };
                        arrow.MouseEnter += (s, e) => arrowText.Opacity = 1;
                        arrow.MouseLeave += (s, e) => arrowText.Opacity = 0.75;
                        arrow.MouseLeftButtonUp += (s, e) =>
                        {
                            e.Handled = true;
                            goTo(current + direction);
                        };
                        return arrow;
                    };
                    Border stageBack = stageArrow("\u2039", -1);
                    Border stageForward = stageArrow("\u203A", 1);
                    stage.Children.Add(stageBack);
                    stage.Children.Add(stageForward);
                    stage.MouseEnter += (s, e) => stageBack.Opacity = stageForward.Opacity = 1;
                    stage.MouseLeave += (s, e) => stageBack.Opacity = stageForward.Opacity = 0;
                }
            }
            else
            {
                // No screenshots on the server: the fan art stands in, else the cover.
                showPicture(FanartPath(rom) ?? rom.PathCoverL ?? rom.PathCoverS);
            }
            top.Children.Add(mediaColumn);

            // The side column is as tall as the media beside it: header at the top, the facts at
            // the foot, and the description taking all the room between (scrolling where it runs
            // out); never shorter than the header and facts need.
            var side = new Grid();
            Grid.SetColumn(side, 1);
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new StackPanel();
            var facts = new StackPanel();
            Grid.SetRow(facts, 2);
            side.Children.Add(head);
            side.Children.Add(facts);
            Action fitSide = () =>
            {
                double needed = head.ActualHeight + facts.ActualHeight + 24 + 3 * 18;
                side.Height = Math.Max(mediaColumn.ActualHeight, needed);
            };
            mediaColumn.SizeChanged += (s, e) => fitSide();
            head.SizeChanged += (s, e) => fitSide();
            facts.SizeChanged += (s, e) => fitSide();
            if (media.Count > 0 || hasVideo)
            {
                head.Children.Add(BuildHeader(rom, media));
            }

            // Steam's text when the game is matched on Steam (fetched if need be, then put in
            // place), RomM's until then or otherwise.
            string summary = ShortText(rom);
            var sideBlurb = Text(summary, 13, false);
            sideBlurb.Margin = new Thickness(0, 0, 6, 0);
            // A long description scrolls in the room it has; its scroll bar shows only while the
            // pointer is on it (the wheel scrolls it either way, then the page at its end).
            var blurbScroll = new ScrollViewer
            {
                Content = sideBlurb,
                // Room for the bar only when the text overflows; the bar itself is faded in and
                // out, so the text does not reflow as the pointer comes and goes.
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 12, 0, 12),
                Focusable = false,
                Visibility = summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            Func<System.Windows.Controls.Primitives.ScrollBar> blurbBar = () =>
            {
                blurbScroll.ApplyTemplate();
                return blurbScroll.Template?.FindName("PART_VerticalScrollBar", blurbScroll) as System.Windows.Controls.Primitives.ScrollBar;
            };
            blurbScroll.Loaded += (s, e) =>
            {
                var bar = blurbBar();
                if (bar != null)
                    bar.Opacity = 0;
            };
            blurbScroll.MouseEnter += (s, e) =>
            {
                var bar = blurbBar();
                if (bar != null)
                    bar.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            };
            blurbScroll.MouseLeave += (s, e) =>
            {
                var bar = blurbBar();
                if (bar != null)
                    bar.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            };
            Grid.SetRow(blurbScroll, 1);
            side.Children.Add(blurbScroll);

            // The facts in a small box of their own, the review score first, as on Steam's page.
            var info = new StackPanel();
            UIElement reviews = ReviewRow(rom);
            if (reviews != null)
                info.Children.Add(reviews);
            long? released = rom.Metadatum?.Release_Date ?? rom.FirstReleaseDate;
            if (released.HasValue && released.Value > 0)
            {
                // The server gives milliseconds; older ones gave seconds.
                long seconds = released.Value > 100000000000 ? released.Value / 1000 : released.Value;
                info.Children.Add(Fact("RELEASE DATE", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToString("d MMM, yyyy")));
            }
            // Developer and publisher separately when the server tells them apart.
            _raw.TryGetValue(rom.Id, out JObject sideData);
            var developers = Strings(sideData, "metadatum.developers");
            var publishers = Strings(sideData, "metadatum.publishers");
            if (developers.Count > 0 || publishers.Count > 0)
            {
                if (developers.Count > 0)
                    info.Children.Add(Fact("DEVELOPER", string.Join(", ", developers)));
                if (publishers.Count > 0)
                    info.Children.Add(Fact("PUBLISHER", string.Join(", ", publishers)));
            }
            else
            {
                var companies = rom.Metadatum?.Companies ?? rom.Companies;
                if (companies != null && companies.Count > 0)
                    info.Children.Add(Fact("DEVELOPER / PUBLISHER", string.Join(", ", companies)));
            }
            info.Children.Add(Fact("PLATFORM", !string.IsNullOrEmpty(rom.PlatformDisplayName) ? rom.PlatformDisplayName : rom.PlatformName));

            facts.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(12, 10, 12, 10),
                Child = info,
            });

            var genres = rom.Metadatum?.Genres ?? rom.Genres;
            if (genres != null && genres.Count > 0)
            {
                var tags = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                foreach (string genre in genres.Take(8))
                {
                    tags.Children.Add(GenreChip(genre, new Thickness(7, 2, 7, 2), new Thickness(0, 0, 4, 4)));
                }
                facts.Children.Add(tags);
            }
            top.Children.Add(side);
            page.Children.Add(top);


            // --- Lower block: description and long sections left, fact boxes right -------------
            _raw.TryGetValue(rom.Id, out JObject data);
            data = data ?? new JObject();

            var lower = new Grid { Margin = new Thickness(0, 28, 0, 0) };
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var right = new StackPanel();
            Grid.SetColumn(right, 1);
            lower.Children.Add(left);
            lower.Children.Add(right);

            // The box to get the game, at the head of the left column as Steam's "Buy" box is.
            left.Children.Add(BuildInstallBar(rom));

            // "About this game": Steam's own, drawn as Steam draws it (pictures, banner videos,
            // headings), when the game is matched on Steam; RomM's text otherwise.
            var aboutTitle = SectionTitle("ABOUT THIS GAME", 0);
            var aboutHost = new ContentControl { Focusable = false };
            // Priority: Steam's description > the box art > Wikipedia's introduction > RomM's
            // summary (shown while Wikipedia is asked, and when it has nothing).
            WikipediaArticle wiki = null;
            bool wikiAsked = false;
            bool coversBlank = false;   // the server's box art turned out to be placeholders
            Action fillAbout = null;
            fillAbout = () =>
            {
                UIElement steamAbout = SteamAbout.Build(_plugin.Steam?.Cached(rom.SteamId)?.AboutHtml, _plugin.VideoRelay);
                if (steamAbout != null)
                {
                    aboutHost.Content = steamAbout;
                    aboutTitle.Visibility = Visibility.Visible;
                    return;
                }
                // Not on Steam: the box's front and back, side by side, when the server has them
                // (the summary is already beside the screenshots above).
                UIElement covers = coversBlank ? null : BoxArt(rom, () =>
                {
                    if (_currentRom == null || _currentRom.Id != rom.Id || coversBlank)
                        return;
                    coversBlank = true;
                    fillAbout();
                });
                if (covers != null)
                {
                    aboutHost.Content = covers;
                    aboutTitle.Visibility = Visibility.Visible;
                    return;
                }
                if (wiki != null)
                {
                    aboutHost.Content = WikipediaAbout(wiki);
                    aboutTitle.Visibility = Visibility.Visible;
                    return;
                }
                string about = (rom.Summary ?? "").Trim();
                var full = Text(about, 14, false);
                full.LineHeight = 22;
                aboutHost.Content = full;
                aboutTitle.Visibility = about.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

                if (!wikiAsked && _plugin.Wikipedia != null && !string.IsNullOrWhiteSpace(rom.Name))
                {
                    wikiAsked = true;
                    _plugin.Wikipedia.GetAsync(rom.Name).ContinueWith(t =>
                    {
                        WikipediaArticle found = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                        if (found == null)
                            return;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_currentRom == null || _currentRom.Id != rom.Id)
                                return;
                            wiki = found;
                            fillAbout();
                        }));
                    });
                }
            };
            fillAbout();
            // The game on other platforms, as Steam lists editions, above its About.
            try
            {
                AddPlatformVersions(left, data, rom);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not list {rom.Name} on other platforms: {ex.Message}");
            }
            left.Children.Add(aboutTitle);
            left.Children.Add(aboutHost);

            WhenSteamText(rom, steam =>
            {
                if (_currentRom == null || _currentRom.Id != rom.Id)
                    return;
                string shortText = ShortText(rom);
                sideBlurb.Text = shortText;
                blurbScroll.Visibility = shortText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                blurbScroll.ScrollToTop();
                fillAbout();
            });

            try
            {
                AddVersions(left, data, rom.Id);
                AddFactBoxes(right, data, rom);
            }
            catch (Exception ex)
            {
                // The extra sections are read loosely from whatever the server sent; a field
                // in an unexpected shape should cost that section, not the page.
                s_log.Warn($"[Store] Could not build every section for {rom.Name}: {ex.Message}");
            }

            page.Children.Add(lower);

            try
            {
                // Full width, under everything else, as on RomM's own game page; its series
                // (with what the server lacks, for the wishlist) first.
                AddSeriesSection(page, data, rom);
                AddRelated(page, data, "EXPANSIONS", "igdb_metadata.expansions");
                AddRelated(page, data, "DLC", "igdb_metadata.dlcs");
                AddRelated(page, data, "REMAKES", "igdb_metadata.remakes");
                AddRelated(page, data, "EXPANDED EDITIONS", "igdb_metadata.expanded_games");
                AddSimilarOnServer(page, data, rom.Id);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not build the related games for {rom.Name}: {ex.Message}");
            }

            _gamePageInset.Child = WithBackdrop(rom, page);
            _pageGlide.ToTop();
            _browse.Visibility = Visibility.Collapsed;
            _gamePage.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// The game's fan art behind its page, as Steam's store pages have: heavily blurred and
        /// dimmed, fading out toward both sides and down the page, so it tints the top of the
        /// page without edges. It does not take part in the page's layout.
        /// </summary>
        private UIElement WithBackdrop(RomMRom rom, UIElement page)
        {
            string art = FanartPath(rom) ?? MediaPaths(rom).FirstOrDefault();
            if (art == null)
                return page;

            var image = new Image
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.55,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 70, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
            ShowPicture(image, Url(art), 640);

            // Down the page: full at the top, gone by the bottom of the frame.
            var down = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            down.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0));
            down.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 0.5));
            down.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            // The blur reaches past the edges of the picture, which would darken them; the
            // picture is drawn a little larger than its frame so those edges fall outside.
            var inner = new Grid { Margin = new Thickness(-90, -90, -90, 0), OpacityMask = down };
            inner.Children.Add(image);

            // Across the page: faded in from both sides.
            var sides = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            sides.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
            sides.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.25));
            sides.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.75));
            sides.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            var frame = new Grid
            {
                Height = 900,
                VerticalAlignment = VerticalAlignment.Top,
                ClipToBounds = true,
                IsHitTestVisible = false,
                OpacityMask = sides,
                // Drawn once and kept as a picture. Otherwise the blur (70 pixels across a
                // picture wider than the page) and the two fades are drawn again for every frame
                // the page is drawn: each step of a scroll, and each frame of a video playing
                // anywhere on the page, which made scrolling lag. Half size is plenty for
                // something this blurred.
                CacheMode = new BitmapCache { RenderAtScale = 0.5 },
            };
            frame.Children.Add(inner);

            var holder = new Grid();
            holder.Children.Add(frame);

            // On the game's anniversary, a warm golden light over the top of the page.
            if (AnniversaryToday(rom))
            {
                var warm = new RadialGradientBrush { Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.7, RadiusY = 0.9 };
                warm.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0xF2, 0xC2, 0x4B), 0));
                warm.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xF2, 0xC2, 0x4B), 1));
                holder.Children.Add(new Border { Height = 700, VerticalAlignment = VerticalAlignment.Top, Background = warm, IsHitTestVisible = false });
            }

            holder.Children.Add(page);
            return holder;
        }

        // The picture beside the media: fan art with the game's logo centred on it.
        private UIElement BuildHeader(RomMRom rom, List<string> media)
        {
            string backdrop = !string.IsNullOrEmpty(rom.SSMetadata?.FanartPath)
                ? "/assets/romm/resources/" + rom.SSMetadata.FanartPath
                : media.FirstOrDefault();

            var header = new Grid { Background = Brushes.Black, Height = 160, ClipToBounds = true };
            header.SizeChanged += (s, e) =>
            {
                // Steam's header proportions (460 x 215).
                if (e.WidthChanged && e.NewSize.Width > 0)
                    header.Height = Math.Round(e.NewSize.Width * 215.0 / 460.0);
            };

            var art = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.Fant);
            ShowPicture(art, Url(backdrop ?? rom.PathCoverL ?? rom.PathCoverS), 800);
            header.Children.Add(art);

            UIElement logo = LogoOverlay(rom, 1, 4, 700);
            if (logo != null)
                header.Children.Add(logo);

            return header;
        }

        /// <summary>
        /// A game's logo centred over a picture, with a light shade under it so a pale logo
        /// still reads against pale art; null when the game has no logo. The logo keeps a
        /// margin of side : middle : side of the picture on each axis.
        /// </summary>
        private UIElement LogoOverlay(RomMRom rom, int side, int middle, int decodeWidth)
        {
            string logoUrl = RomMMetadataMapper.LogoUrl(Host, rom, out bool _);
            if (string.IsNullOrEmpty(logoUrl))
                return null;

            var logo = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.Fant);
            ShowPicture(logo, logoUrl, decodeWidth);

            var holder = new Grid { IsHitTestVisible = false, Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)) };
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(side, GridUnitType.Star) });
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(middle, GridUnitType.Star) });
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(side, GridUnitType.Star) });
            holder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(side, GridUnitType.Star) });
            holder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(middle, GridUnitType.Star) });
            holder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(side, GridUnitType.Star) });
            Grid.SetColumn(logo, 1);
            Grid.SetRow(logo, 1);
            holder.Children.Add(logo);
            return holder;
        }

        private UIElement SectionTitle(string text, double top)
        {
            var panel = new StackPanel { Margin = new Thickness(0, top, 0, 12) };
            panel.Children.Add(Text(text, 14, false));
            panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 6, 0, 0) });
            return panel;
        }

        // ---- Loose readers for the server's untyped game data -------------------------------

        private static string Str(JToken token, string path)
        {
            JToken found = token?.SelectToken(path);
            if (found == null || found.Type == JTokenType.Null || found is JContainer)
                return null;
            string text = found.ToString().Trim();
            return text.Length == 0 ? null : text;
        }

        private static List<string> Strings(JToken token, string path)
        {
            var list = new List<string>();
            JToken found = token?.SelectToken(path);
            if (found is JArray array)
            {
                foreach (JToken item in array)
                {
                    if (item is JValue && item.Type != JTokenType.Null && item.ToString().Trim().Length > 0)
                        list.Add(item.ToString().Trim());
                }
            }
            else if (found is JValue && found.Type != JTokenType.Null && found.ToString().Trim().Length > 0)
            {
                list.Add(found.ToString().Trim());
            }
            return list;
        }

        private static double? Number(JToken token, string path)
        {
            string text = Str(token, path);
            return text != null && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value
                : (double?)null;
        }

        // ---- Sections ------------------------------------------------------------------------

        // A titled box in the right-hand column; nothing is added when it would be empty.
        private void AddBox(Panel column, string title, List<UIElement> rows)
        {
            if (rows.Count == 0)
                return;

            var body = new StackPanel();
            var heading = Text(title, 11, false);
            heading.Opacity = 0.55;
            heading.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(heading);
            foreach (UIElement row in rows)
                body.Children.Add(row);

            column.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = body,
            });
        }

        // A plain line in a box, like Steam's feature rows.
        private UIElement Line(string text)
        {
            var line = Text(text, 12, false);
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 2),
                Child = line,
            };
        }

        private static string Hours(double seconds)
        {
            // Stored in seconds; guard against a server that kept the source's hundredths.
            double hours = seconds / 3600.0;
            if (hours > 2000)
                hours /= 100.0;
            return hours >= 10 ? $"{hours:0} hours" : $"{hours:0.#} hours";
        }

        private void AddFactBoxes(Panel column, JObject data, RomMRom rom)
        {
            // The scores are with the game's facts at the top (ReviewRow).
            AddAchievements(column, data);
            AddSteamAchievements(column, rom);

            // Time to beat.
            var times = new List<UIElement>();
            double? main = Number(data, "hltb_metadata.main_story");
            double? extra = Number(data, "hltb_metadata.main_plus_extra");
            double? complete = Number(data, "hltb_metadata.completionist");
            if (main.HasValue && main.Value > 0) times.Add(Fact("MAIN STORY", Hours(main.Value)));
            if (extra.HasValue && extra.Value > 0) times.Add(Fact("MAIN + EXTRAS", Hours(extra.Value)));
            if (complete.HasValue && complete.Value > 0) times.Add(Fact("COMPLETIONIST", Hours(complete.Value)));
            AddBox(column, "HOW LONG TO BEAT", times);

            // How it runs in its emulator, from the emulator's own list, above the features:
            // filled in when the lookup returns (the lists may have to be fetched first).
            AddCompatibility(column, data, rom);

            // Features, as plain rows.
            var features = new List<UIElement>();
            foreach (string mode in Strings(data, "metadatum.game_modes"))
                features.Add(Line(mode));
            string players = Str(data, "ss_metadata.player_count");
            if (players != null && players != "0")
                features.Add(Line($"Players: {players}"));
            int achievements = (data.SelectToken("merged_ra_metadata.achievements") as JArray)?.Count ?? 0;
            if (achievements > 0)
                features.Add(Line($"RetroAchievements ({achievements})"));
            if ((bool?)data["has_manual"] == true)
                features.Add(Line("Manual included"));
            if ((bool?)data["has_soundtrack"] == true)
                features.Add(Line("Soundtrack included"));
            foreach (string view in Strings(data, "igdb_metadata.player_perspectives"))
                features.Add(Line(view));
            AddBox(column, "FEATURES", features);

            // Your own record of the game on the server.
            var mine = new List<UIElement>();
            string status = Str(data, "rom_user.status");
            if (status != null)
                mine.Add(Fact("STATUS", System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(status.Replace('_', ' '))));
            else if ((bool?)data.SelectToken("rom_user.now_playing") == true)
                mine.Add(Fact("STATUS", "Now playing"));
            else if ((bool?)data.SelectToken("rom_user.backlogged") == true)
                mine.Add(Fact("STATUS", "Backlogged"));
            if (DateTime.TryParse(Str(data, "rom_user.last_played"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime played))
                mine.Add(Fact("LAST PLAYED", played.ToLocalTime().ToString("d MMM, yyyy")));
            double? myRating = Number(data, "rom_user.rating");
            if (myRating.HasValue && myRating.Value > 0)
                mine.Add(Fact("YOUR RATING", $"{myRating.Value:0} / 10"));
            double? completion = Number(data, "rom_user.completion");
            if (completion.HasValue && completion.Value > 0)
                mine.Add(Fact("COMPLETION", $"{completion.Value:0}%"));
            AddBox(column, "YOUR ACTIVITY", mine);

            // Details of the release.
            var details = new List<UIElement>();
            Action<string, List<string>> list = (label, values) =>
            {
                if (values.Count > 0)
                    details.Add(Fact(label, string.Join(", ", values.Distinct().Take(12))));
            };
            list("REGIONS", Strings(data, "regions"));
            list("LANGUAGES", Strings(data, "languages"));
            list("FRANCHISE", Strings(data, "metadatum.franchises"));
            list("COLLECTIONS", Strings(data, "metadatum.collections"));
            list("THEMES", Strings(data, "igdb_metadata.themes"));
            list("TAGS", Strings(data, "tags"));
            list("KEYWORDS", Strings(data, "igdb_metadata.keywords"));
            string revision = Str(data, "revision");
            if (revision != null)
                details.Add(Fact("REVISION", revision));
            list("ALSO KNOWN AS", Strings(data, "alternative_names"));
            AddBox(column, "DETAILS", details);

            // The rating boards' badges, under the details.
            AddAgeRatings(column, data);

            // The file itself.
            var file = new List<UIElement>();
            string fileName = Str(data, "fs_name");
            if (fileName != null)
                file.Add(Fact("FILE", fileName));
            file.Add(Fact("SIZE", FormatSize(rom.FileSizeBytes)));
            int files = (data["files"] as JArray)?.Count ?? 0;
            if (files > 1)
                file.Add(Fact("FILES", files.ToString()));
            foreach (var hash in new[] { new[] { "CRC", "crc_hash" }, new[] { "MD5", "md5_hash" }, new[] { "SHA-1", "sha1_hash" } })
            {
                string value = Str(data, hash[1]);
                if (value != null)
                    file.Add(Fact(hash[0], value));
            }
            AddBox(column, "FILE", file);
        }

        // The game's standing in its emulator's compatibility list: the verdict, what is known to
        // go wrong, and whether fixes exist (not which). Only what the server's knowledge of the
        // game settles: nothing for a game the list hasn't rated, nor for one that could only be
        // found from its installed files.
        private void AddCompatibility(Panel column, JObject data, RomMRom rom)
        {
            if (_plugin.CompatWatch == null || CompatWatch.SpecOfSlug(rom.PlatformSlug) == null)
                return;
            var slot = new ContentControl { Visibility = Visibility.Collapsed };
            column.Children.Add(slot);
            var names = new List<string> { Str(data, "fs_name"), Str(data, "fs_name_no_ext") };
            foreach (JToken file in data["files"] as JArray ?? new JArray())
                names.Add(Str(file, "file_name"));
            names = names.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            string name = rom.Name ?? rom.FileNameNoExt;
            Task.Run(async () =>
            {
                StoreCompat compat = await _plugin.CompatWatch.ForRomAsync(rom.Id, rom.PlatformId, rom.PlatformSlug, name, names).ConfigureAwait(false);
                if (compat == null)
                    return;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    var rows = new List<UIElement>();
                    Color dot = compat.Tone == 0 ? Color.FromRgb(0x66, 0xC0, 0xF4) : compat.Tone == 1 ? Color.FromRgb(0xE5, 0xB1, 0x43) : Color.FromRgb(0xE0, 0x5A, 0x4F);
                    var headline = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                    var mark = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(dot), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(mark, Dock.Left);
                    headline.Children.Add(mark);
                    var verdict = Text(compat.Headline, 13, true);
                    verdict.TextWrapping = TextWrapping.Wrap;
                    headline.Children.Add(verdict);
                    rows.Add(headline);
                    foreach (string note in compat.Notes.Take(6))
                    {
                        var line = Text("\u2022  " + note, 12, false);
                        line.TextWrapping = TextWrapping.Wrap;
                        line.Opacity = 0.75;
                        line.Margin = new Thickness(0, 2, 0, 0);
                        rows.Add(line);
                    }
                    if (compat.FixesNote != null)
                    {
                        var fixes = Text(compat.FixesNote, 12, false);
                        fixes.TextWrapping = TextWrapping.Wrap;
                        fixes.Opacity = 0.75;
                        fixes.Margin = new Thickness(0, 6, 0, 0);
                        rows.Add(fixes);
                    }
                    var source = Text("From " + compat.Source + (compat.MatchedAs != null ? $" ({compat.MatchedAs})" : ""), 11, false);
                    source.TextWrapping = TextWrapping.Wrap;
                    source.Opacity = 0.55;
                    source.Margin = new Thickness(0, 8, 0, 0);
                    if (compat.Url != null)
                    {
                        source.Cursor = Cursors.Hand;
                        source.TextDecorations = TextDecorations.Underline;
                        source.ToolTip = compat.Url;
                        source.MouseLeftButtonUp += (s, e) =>
                        {
                            try { System.Diagnostics.Process.Start(compat.Url); } catch { }
                        };
                    }
                    rows.Add(source);
                    var box = new StackPanel();
                    AddBox(box, "EMULATION", rows);
                    slot.Content = box;
                    slot.Visibility = Visibility.Visible;
                }));
            });
        }

        // An address that may be given without its scheme ("//images.igdb.com/...").
        private static string External(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            if (url.StartsWith("//"))
                return "https:" + url;
            return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
        }

        // Age ratings as the rating boards' own badges where the server has a picture address
        // for them; the first as a labelled chip when none has one.
        private void AddAgeRatings(Panel column, JObject data)
        {
            var badges = new WrapPanel();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JToken rating in data.SelectToken("igdb_metadata.age_ratings") as JArray ?? new JArray())
            {
                string label = Str(rating, "rating");
                string category = Str(rating, "category");
                string picture = External(Str(rating, "rating_cover_url"));
                if (label == null || !seen.Add(category + ":" + label))
                    continue;
                seen.Add(label);

                string tip = category != null ? $"{category}: {label}" : label;
                if (picture != null)
                {
                    var image = new Image { Height = 40, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 6), ToolTip = tip };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                    // Rating badges are a small fixed set: kept on disk after the first download.
                    ShowPicture(image, picture, 160, RatingBadgeFolder);
                    badges.Children.Add(image);
                }
                else
                {
                    badges.Children.Add(Chip(tip));
                }
            }

            foreach (JToken rating in data.SelectToken("ss_metadata.age_ratings") as JArray ?? new JArray())
            {
                string label = Str(rating, "rating");
                string category = Str(rating, "category");
                if (label != null && seen.Add(category + ":" + label))
                {
                    seen.Add(label);
                    badges.Children.Add(Chip(category != null ? $"{category}: {label}" : label));
                }
            }

            // Anything the server lists that neither source described.
            foreach (string label in Strings(data, "metadatum.age_ratings"))
            {
                if (seen.Add(label))
                    badges.Children.Add(Chip(label));
            }

            // The boards' badges where there are pictures for them; ratings with none are left
            // out, unless none has one: then the first rating alone, as a chip.
            var pictures = badges.Children.OfType<Image>().Cast<UIElement>().ToList();
            var shown = pictures.Count > 0 ? pictures : badges.Children.Cast<UIElement>().Take(1).ToList();
            badges.Children.Clear();
            foreach (UIElement badge in shown)
                badges.Children.Add(badge);
            if (badges.Children.Count > 0)
                AddBox(column, "AGE RATING", new List<UIElement> { badges });
        }

        private string RatingBadgeFolder =>
            System.IO.Path.Combine(_plugin.Playnite.Paths.ExtensionsDataPath, _plugin.Id.ToString(), "StoreCache", "AgeRatings");

        private UIElement Chip(string text)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Text(text, 11, false),
            };
        }

        // A row of related titles (expansions, remakes...): cover and name, scrolling sideways.
        // Shown as information only; they are not things the server necessarily has.
        private void AddRelated(Panel page, JObject data, string title, string path)
        {
            var games = data.SelectToken(path) as JArray;
            if (games == null || games.Count == 0)
                return;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (JToken game in games.Take(24))
            {
                string name = Str(game, "name");
                if (name == null)
                    continue;

                var cover = new Image { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.Fant);
                ShowPicture(cover, External(Str(game, "cover_url")), 320);
                var frame = new Grid { Width = 140, Height = 187, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), ClipToBounds = true };
                frame.Children.Add(cover);

                var label = Text(name, 12, false);
                label.Margin = new Thickness(2, 6, 2, 0);
                label.MaxHeight = 34;
                label.TextTrimming = TextTrimming.CharacterEllipsis;

                var stack = new StackPanel { Width = 140 };
                stack.Children.Add(frame);
                stack.Children.Add(label);

                row.Children.Add(new Border { Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(4), Child = stack });
            }

            if (row.Children.Count == 0)
                return;

            page.Children.Add(SectionTitle(title, 28));
            page.Children.Add(SideScroll.Row(row, true));
        }

        /// <summary>
        /// "More like this", worked out from the server's own library: games in the same series
        /// (franchise or collection) first, then games sharing the most genres, themes and
        /// keywords. Everything shown is on the server and opens its own store page.
        ///
        /// A handful of ordinary list requests fetch the candidates; the ranking is done here.
        /// </summary>
        private void AddSimilarOnServer(Panel page, JObject data, int romId)
        {
            if (Host.Length == 0)
                return;

            var franchises = Strings(data, "metadatum.franchises");
            var collections = Strings(data, "metadatum.collections");
            // Its series (IGDB's collections); a shared franchise ("Disney") only counts a little.
            var series = SeriesOfData(data);
            var genres = Strings(data, "metadatum.genres");
            var flavour = Strings(data, "igdb_metadata.themes").Concat(Strings(data, "igdb_metadata.keywords"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var makers = Strings(data, "metadatum.developers");
            if (series.Count == 0 && genres.Count == 0)
                return;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var section = new StackPanel { Visibility = Visibility.Collapsed };
            section.Children.Add(SectionTitle("MORE LIKE THIS", 28));
            section.Children.Add(SideScroll.Row(row, true));
            page.Children.Add(section);

            string host = Host;
            string ownName = Str(data, "name") ?? "";
            Func<string, List<string>, string> many = (name, values) =>
                string.Concat(values.Take(8).Select(v => $"&{name}={Uri.EscapeDataString(v)}"));

            Task.Run(() =>
            {
                try
                {
                    // The server's own answer first, where it has one: it keeps a similarity
                    // graph of its library and can say why two games are alike. The ranking
                    // below is only for servers without it.
                    try
                    {
                        var similar = Get($"/api/roms/{romId}/similar?limit=14") as JArray;
                        if (similar != null && similar.Count > 0)
                        {
                            var fromServer = new List<Tuple<string, JObject>>();
                            foreach (JToken entry in similar)
                            {
                                if (entry["rom"] is JObject rom && rom["id"] != null)
                                    fromServer.Add(Tuple.Create(Reasons(entry), rom));
                            }

                            if (fromServer.Count > 0)
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    foreach (var pick in fromServer)
                                    {
                                        RomMRom match = pick.Item2.ToObject<RomMRom>();
                                        if (match == null || _inLibrary.Contains(match.Id))
                                            continue;
                                        _raw[match.Id] = pick.Item2;
                                        row.Children.Add(BuildSimilarTile(match, pick.Item1));
                                    }
                                    section.Visibility = row.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                                }));
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Info($"[Store] The server's similar-games list was not usable ({ex.Message}); ranking here instead.");
                    }

                    // Candidate lists. Series members are few and all wanted; genre matches can
                    // be very many, so games sharing every genre are asked for first and games
                    // sharing any are only a top-up.
                    var queries = new List<string>();
                    if (franchises.Count > 0) queries.Add("limit=60" + many("franchises", franchises));
                    if (collections.Count > 0) queries.Add("limit=60" + many("collections", collections));
                    if (genres.Count > 1) queries.Add("limit=100&genres_logic=all" + many("genres", genres));
                    if (genres.Count > 0) queries.Add("limit=100&genres_logic=any" + many("genres", genres));

                    var candidates = new Dictionary<int, JObject>();
                    foreach (string query in queries)
                    {
                        // Enough already, before the broad "any genre" top-up.
                        if (query.Contains("genres_logic=any") && candidates.Count >= 40)
                            break;

                        WaitForForeground();
                        using (var response = HttpClientSingleton.Instance.GetAsync($"{host}/api/roms?order_by=name&order_dir=asc&{query}").GetAwaiter().GetResult())
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                s_log.Warn($"[Store] Recommendation query failed ({(int)response.StatusCode}): {query}");
                                continue;
                            }

                            var json = JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                            foreach (JToken item in json["items"] as JArray ?? new JArray())
                            {
                                if (item is JObject o && o["id"] != null)
                                    candidates[(int)o["id"]] = o;
                            }
                        }
                    }

                    // Rank. Same series outweighs any amount of genre overlap.
                    var ranked = new List<Tuple<double, string, JObject>>();
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownName };
                    foreach (var pair in candidates.OrderBy(c => c.Key))
                    {
                        JObject other = pair.Value;
                        string name = Str(other, "name") ?? Str(other, "fs_name_no_ext") ?? "";
                        if (pair.Key == romId || name.Length == 0)
                            continue;

                        bool sameSeries = SeriesOfData(other).Intersect(series, StringComparer.OrdinalIgnoreCase).Any();
                        bool sameFranchise = !sameSeries && Strings(other, "metadatum.franchises").Intersect(franchises, StringComparer.OrdinalIgnoreCase).Any();
                        var sharedGenres = Strings(other, "metadatum.genres").Intersect(genres, StringComparer.OrdinalIgnoreCase).ToList();
                        int sharedFlavour = Strings(other, "igdb_metadata.themes").Concat(Strings(other, "igdb_metadata.keywords"))
                            .Intersect(flavour, StringComparer.OrdinalIgnoreCase).Count();
                        bool sameMaker = Strings(other, "metadatum.developers").Intersect(makers, StringComparer.OrdinalIgnoreCase).Any();

                        double score = (sameSeries ? 100 : 0) + (sameFranchise ? 4 : 0) + sharedGenres.Count * 3 + Math.Min(sharedFlavour, 8) + (sameMaker ? 2 : 0);

                        // Share of this game's genres matched, so a game matching 2 of 2 beats
                        // one matching 2 of 6.
                        if (genres.Count > 0)
                            score += 3.0 * sharedGenres.Count / genres.Count;
                        if (score <= 0)
                            continue;

                        string why = sameSeries ? "Same series" : string.Join(", ", sharedGenres.Take(3));
                        ranked.Add(Tuple.Create(score, why, other));
                    }

                    var picks = new List<Tuple<double, string, JObject>>();
                    foreach (var entry in ranked.OrderByDescending(r => r.Item1).ThenBy(r => Str(r.Item3, "name"), StringComparer.OrdinalIgnoreCase))
                    {
                        // The same game on another platform, or this game's other versions, once.
                        if (!names.Add(Str(entry.Item3, "name") ?? ""))
                            continue;
                        picks.Add(entry);
                        if (picks.Count == 14)
                            break;
                    }

                    if (picks.Count == 0)
                        return;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        foreach (var pick in picks)
                        {
                            RomMRom match = pick.Item3.ToObject<RomMRom>();
                            if (match == null || _inLibrary.Contains(match.Id))
                                continue;
                            _raw[match.Id] = pick.Item3;
                            row.Children.Add(BuildSimilarTile(match, pick.Item2));
                        }
                        section.Visibility = row.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    }));
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not work out recommendations: {ex.Message}");
                }
            });
        }

        private UIElement BuildSimilarTile(RomMRom rom, string why)
        {
            var cover = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.Fant);
            ShowPicture(cover, Url(rom.PathCoverS ?? rom.PathCoverL), 320);
            var frame = new Grid { Width = 140, Height = 187, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), ClipToBounds = true };
            frame.Children.Add(cover);
            if (_inLibrary.Contains(rom.Id))
            {
                frame.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "IN LIBRARY", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
                });
            }
            else if (Unavailable(rom))
            {
                var mark = UnavailableTag(9, new Thickness(5, 1, 5, 1), new Thickness(5), MarkOf(rom));
                mark.HorizontalAlignment = HorizontalAlignment.Left;
                mark.VerticalAlignment = VerticalAlignment.Top;
                frame.Children.Add(mark);
            }

            var label = Text(rom.Name ?? rom.FileNameNoExt ?? "", 12, false);
            label.Margin = new Thickness(2, 6, 2, 0);
            label.MaxHeight = 34;
            label.TextTrimming = TextTrimming.CharacterEllipsis;

            var stack = new StackPanel { Width = 140 };
            stack.Children.Add(frame);
            stack.Children.Add(label);
            if (!string.IsNullOrEmpty(why))
            {
                // Why it was suggested, so the row can be judged at a glance.
                var reason = Text(why, 10, false);
                reason.Opacity = 0.55;
                reason.Margin = new Thickness(2, 2, 2, 0);
                reason.TextWrapping = TextWrapping.NoWrap;
                reason.TextTrimming = TextTrimming.CharacterEllipsis;
                stack.Children.Add(reason);
            }

            var tile = new Border { Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = stack };
            tile.MouseEnter += (s, e) => tile.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
            tile.MouseLeftButtonUp += (s, e) => ShowGame(rom);
            AttachHoverCard(tile, rom);
            return tile;
        }

        // RetroAchievements for the game, boxed in the right column as Steam's "Steam Achievements"
        // is: the count and points, the first few badges and a tile to see them all.
        private void AddAchievements(Panel column, JObject data)
        {
            var achievements = data.SelectToken("merged_ra_metadata.achievements") as JArray;
            if (achievements == null || achievements.Count == 0)
                return;

            double points = 0;
            foreach (JToken a in achievements)
                points += Number(a, "points") ?? 0;
            var badges = new List<Badge>();
            foreach (JToken a in achievements.OrderBy(t => Number(t, "display_order") ?? 0))
            {
                // The server's copy of the badge, else RetroAchievements' own. RomM hands the
                // path out already under its resources mount ("/assets/romm/resources/roms/...").
                string path = Str(a, "badge_path");
                badges.Add(new Badge
                {
                    Url = string.IsNullOrEmpty(path) ? null
                        : Url(path.StartsWith("/assets/") || path.StartsWith("assets/") ? path : "/assets/romm/resources/" + path.TrimStart('/')),
                    Fallback = Str(a, "badge_url"),
                    Tip = $"{Str(a, "title")}\n{Str(a, "description")}\n{Number(a, "points") ?? 0:0} points",
                });
            }
            AddBadgeBox(column, "RETROACHIEVEMENTS", $"{achievements.Count} achievements  \u00B7  {points:0} points", badges);
        }

        // An achievement's picture in a box, and what pointing at it says.
        private sealed class Badge
        {
            public string Url;
            public string Fallback;
            public string Tip;
        }

        // A box of achievements: a line, the first few badges and a tile to see them all.
        private void AddBadgeBox(Panel column, string heading, string countLine, List<Badge> all)
        {
            const double size = 64;
            const int few = 4;
            var badges = new WrapPanel();
            bool expanded = false;

            var count = Text(countLine, 12, false);
            count.Opacity = 0.7;
            count.Margin = new Thickness(0, 0, 0, 8);

            Action fill = null;
            fill = () =>
            {
                badges.Children.Clear();
                foreach (Badge a in expanded ? all : all.Take(few))
                {
                    var picture = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
                    RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.Fant);
                    ShowPicture(picture, a.Url, 128, null, a.Fallback);
                    badges.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                        Margin = new Thickness(0, 0, 6, 6),
                        Child = picture,
                        ToolTip = a.Tip,
                    });
                }

                // "View all 47", or "Show fewer" once open.
                if (all.Count > few)
                {
                    var label = Text(expanded ? "Show\nfewer" : $"View all\n{all.Count}", 13, false);
                    label.TextAlignment = TextAlignment.Center;
                    label.HorizontalAlignment = HorizontalAlignment.Center;
                    label.VerticalAlignment = VerticalAlignment.Center;
                    label.Opacity = 0.75;
                    var normal = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                    var tile = new Border
                    {
                        Width = size,
                        Height = size,
                        Margin = new Thickness(0, 0, 6, 6),
                        Background = normal,
                        Cursor = Cursors.Hand,
                        Child = label,
                    };
                    tile.MouseEnter += (s, e) => { tile.Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)); label.Opacity = 1; };
                    tile.MouseLeave += (s, e) => { tile.Background = normal; label.Opacity = 0.75; };
                    tile.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        expanded = !expanded;
                        fill();
                    };
                    badges.Children.Add(tile);
                }
            };
            fill();
            AddBox(column, heading, new List<UIElement> { count, badges });
        }

        /// <summary>
        /// Steam's achievements for a Windows game RomM matched on Steam, boxed as RetroAchievements'
        /// are: the count, the first few badges (with how many players have each, as Steam says)
        /// and a tile to see them all. Filled in once Steam's list is here (fetched once a month,
        /// EmulatorAchievements.SteamListFor); nothing when the game has none on Steam.
        /// </summary>
        private void AddSteamAchievements(Panel column, RomMRom rom)
        {
            int appId = rom?.SteamId ?? 0;
            if (appId <= 0 || _plugin.EmulatorAchievements == null)
                return;
            var host = new StackPanel();
            column.Children.Add(host);
            Task.Run(() =>
            {
                List<global::RomM.Games.SteamEmuFiles.Entry> list = null;
                try
                {
                    list = _plugin.EmulatorAchievements.SteamListFor(appId);
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Store] Could not get Steam's achievements of app {appId}: {ex.Message}");
                }
                if (list == null || list.Count == 0)
                    return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_currentRom == null || _currentRom.Id != rom.Id)
                        return;
                    // Hidden ones last, as everywhere achievements are listed.
                    var badges = list.OrderBy(a => a.Hidden).ThenBy(a => a.Order).Select(a => new Badge
                    {
                        Url = global::RomM.Games.EmulatorAchievements.SteamIconUrl(appId, a.Icon),
                        Tip = a.Hidden && string.IsNullOrEmpty(a.Description)
                            ? $"{a.Title}\nHidden achievement" + Rarity(a)
                            : $"{a.Title}\n{a.Description}" + Rarity(a),
                    }).ToList();
                    AddBadgeBox(host, "STEAM ACHIEVEMENTS", $"{list.Count} achievements", badges);
                }));
            });
        }

        private static string Rarity(global::RomM.Games.SteamEmuFiles.Entry entry) =>
            entry.Percent.HasValue ? $"\n{entry.Percent.Value:0.#}% of players have this" : "";

        // Other copies of the same game on the server (regions, revisions, hacks).
        private void AddVersions(Panel column, JObject data, int romId)
        {
            var siblings = data["sibling_roms"] as JArray ?? data["siblings"] as JArray;
            if (siblings == null || siblings.Count == 0)
                return;

            column.Children.Add(SectionTitle("OTHER VERSIONS ON THE SERVER", 28));
            foreach (JToken sibling in siblings.Take(20))
            {
                string name = Str(sibling, "fs_name_no_ext") ?? Str(sibling, "name");
                if (name != null)
                    column.Children.Add(Line(name));
            }
        }

        // ---- Hover card ------------------------------------------------------------------

        private System.Windows.Controls.Primitives.Popup _hoverCard;
        private System.Windows.Threading.DispatcherTimer _hoverDelay;
        private FrameworkElement _hoverTarget;
        private RomMRom _hoverRom;

        /// <summary>
        /// Shows a card of the game's details beside a tile while the pointer rests on it, as
        /// Steam's store does: name, release date, a screenshot, the start of the description,
        /// its rating and genres. It appears after a short pause, so sweeping across a row
        /// does not flash a card for every tile.
        /// </summary>
        private void AttachHoverCard(FrameworkElement tile, RomMRom rom)
        {
            tile.MouseEnter += (s, e) =>
            {
                if (_hoverDelay == null)
                {
                    _hoverDelay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                    _hoverDelay.Tick += (ts, te) =>
                    {
                        _hoverDelay.Stop();
                        if (_hoverTarget != null && _hoverTarget.IsMouseOver && _hoverTarget.IsVisible)
                            OpenHoverCard(_hoverTarget, _hoverRom);
                    };
                }
                CloseHoverCard();
                _hoverTarget = tile;
                _hoverRom = rom;
                _hoverDelay.Stop();
                _hoverDelay.Start();
            };
            tile.MouseLeave += (s, e) =>
            {
                if (ReferenceEquals(_hoverTarget, tile))
                {
                    _hoverDelay?.Stop();
                    _hoverTarget = null;
                }
                CloseHoverCard();
            };
            tile.MouseLeftButtonDown += (s, e) => CloseHoverCard();
        }

        // The screenshots fading through the open card.
        private FadeSlides _hoverSlides;

        // ---- Descriptions -------------------------------------------------------------------

        private static string Clip(string text, int length)
        {
            return text.Length > length ? text.Substring(0, length).TrimEnd() + "..." : text;
        }

        // The short description: Steam's when the game is matched on Steam and it is here, else RomM's.
        private string ShortText(RomMRom rom)
        {
            var steam = _plugin.Steam?.Cached(rom.SteamId);
            if (!string.IsNullOrWhiteSpace(steam?.Short))
                return steam.Short.Trim();
            return (rom.Summary ?? "").Trim();
        }

        // "About this game": Steam's when the game is matched on Steam and it is here, else RomM's.
        private string LongText(RomMRom rom)
        {
            var steam = _plugin.Steam?.Cached(rom.SteamId);
            string about = steam?.AboutText;
            if (!string.IsNullOrWhiteSpace(about))
                return about;
            return (rom.Summary ?? "").Trim();
        }

        // Fetches Steam's text when the game is matched on Steam and it is not here yet, then
        // runs <paramref name="apply"/> (if any) on the UI thread.
        private void WhenSteamText(RomMRom rom, Action<SteamText> apply)
        {
            var steam = _plugin.Steam;
            if (steam == null || !steam.Enabled || rom?.SteamId == null || steam.Cached(rom.SteamId) != null)
                return;
            steam.GetAsync(rom.SteamId).ContinueWith(task =>
            {
                SteamText text = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                if (text != null && apply != null)
                    Dispatcher.BeginInvoke(new Action(() => apply(text)));
            });
        }

        private void CloseHoverCard()
        {
            _hoverSlides?.Stop();
            _hoverSlides = null;
            if (_hoverCard != null)
                _hoverCard.IsOpen = false;
        }

        // "10 Dec, 2020", or null when the server has no date.
        private static string ReleaseText(RomMRom rom)
        {
            long? released = rom.Metadatum?.Release_Date ?? rom.FirstReleaseDate;
            if (!released.HasValue || released.Value <= 0)
                return null;
            long seconds = released.Value > 100000000000 ? released.Value / 1000 : released.Value;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToString("d MMM, yyyy");
        }

        private void OpenHoverCard(FrameworkElement tile, RomMRom rom)
        {
            try
            {
                CloseHoverCard();
                _raw.TryGetValue(rom.Id, out JObject data);

                var body = new StackPanel { Width = 300 };

                var name = Text(rom.Name ?? rom.FileNameNoExt ?? "", 16, true);
                body.Children.Add(name);

                string released = ReleaseText(rom);
                string platform = !string.IsNullOrEmpty(rom.PlatformDisplayName) ? rom.PlatformDisplayName : rom.PlatformName;
                var facts = Text(string.Join("  \u00B7  ", new[] { released != null ? "Released: " + released : null, platform }.Where(x => !string.IsNullOrEmpty(x))), 11, false);
                facts.Opacity = 0.65;
                facts.Margin = new Thickness(0, 2, 0, 8);
                body.Children.Add(facts);

                // The screenshots, one fading into the next; the fan art when there are none.
                List<string> shots = MediaPaths(rom).Select(path => Url(path)).Where(u => !string.IsNullOrEmpty(u)).ToList();
                string fanart = FanartPath(rom);
                if (shots.Count == 0 && fanart != null)
                    shots.Add(Url(fanart));
                if (shots.Count > 0)
                {
                    var slides = new FadeSlides(168, 600);
                    slides.Stage.Margin = new Thickness(0, 0, 0, 8);
                    slides.ShowNow(shots[0]);
                    slides.Play(shots, true);
                    _hoverSlides = slides;
                    body.Children.Add(slides.Stage);
                }

                // Steam's text if it is already here; asking for it now has it ready next time.
                string summary = ShortText(rom);
                WhenSteamText(rom, null);
                if (summary.Length > 0)
                {
                    var blurb = Text(Clip(summary, 260), 12, false);
                    blurb.LineHeight = 17;
                    blurb.Margin = new Thickness(0, 0, 0, 8);
                    body.Children.Add(blurb);
                }

                double? igdb = Number(data, "igdb_metadata.total_rating");
                if (igdb.HasValue && igdb.Value > 0)
                {
                    var score = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                        Padding = new Thickness(8, 5, 8, 5),
                        Margin = new Thickness(0, 0, 0, 8),
                        Child = Text($"Rating: {igdb.Value:0} / 100", 12, false),
                    };
                    body.Children.Add(score);
                }

                var genres = rom.Metadatum?.Genres ?? rom.Genres;
                if (genres != null && genres.Count > 0)
                {
                    var label = Text("Genres:", 11, false);
                    label.Opacity = 0.65;
                    label.Margin = new Thickness(0, 0, 0, 4);
                    body.Children.Add(label);
                    var chips = new WrapPanel();
                    foreach (string genre in genres.Take(6))
                        chips.Children.Add(Chip(genre));
                    body.Children.Add(chips);
                }

                if (_inLibrary.Contains(rom.Id))
                {
                    var owned = Text("In your library", 11, true);
                    owned.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
                    owned.Margin = new Thickness(0, 4, 0, 0);
                    body.Children.Add(owned);
                }
                else if (Unavailable(rom))
                {
                    body.Children.Add(UnavailableTag(10, new Thickness(6, 1, 6, 1), new Thickness(0, 4, 0, 0), MarkOf(rom)));
                    ((FrameworkElement)body.Children[body.Children.Count - 1]).HorizontalAlignment = HorizontalAlignment.Left;
                }

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x2A, 0x3A, 0x4E)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14, 12, 14, 12),
                    IsHitTestVisible = false,
                    Child = body,
                };

                // To the right of the tile, or the left when there is no room on the right.
                _hoverCard = new System.Windows.Controls.Primitives.Popup
                {
                    PlacementTarget = tile,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Right,
                    HorizontalOffset = 4,
                    AllowsTransparency = true,
                    StaysOpen = true,
                    IsHitTestVisible = false,
                    Child = card,
                };
                _hoverCard.IsOpen = true;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not show the details card for {rom.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// "REVIEWS  Mostly Positive (75.9 / 100)", as Steam's review line: RomM's average of the
        /// sources' scores, in Steam's words and colours by Steam's bands (95, 80, 70, 40, 20).
        /// The bands are for a share of positive reviews and these are scores, so the words are
        /// a guide. The sources' own scores in its tooltip. Null when there is no score.
        /// </summary>
        private UIElement ReviewRow(RomMRom rom)
        {
            double score = rom.Metadatum?.Average_Rating ?? 0;
            if (score <= 0)
                return null;
            string words;
            Color color;
            if (score >= 95) { words = "Overwhelmingly Positive"; color = Color.FromRgb(0x66, 0xC0, 0xF4); }
            else if (score >= 80) { words = "Very Positive"; color = Color.FromRgb(0x66, 0xC0, 0xF4); }
            else if (score >= 70) { words = "Mostly Positive"; color = Color.FromRgb(0x66, 0xC0, 0xF4); }
            else if (score >= 40) { words = "Mixed"; color = Color.FromRgb(0xB9, 0xA0, 0x74); }
            else if (score >= 20) { words = "Mostly Negative"; color = Color.FromRgb(0xA3, 0x4C, 0x25); }
            else { words = "Very Negative"; color = Color.FromRgb(0xA3, 0x4C, 0x25); }

            var row = new Grid { Margin = new Thickness(0, 2, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var name = Text("REVIEWS", 10, false);
            name.Opacity = 0.55;
            name.VerticalAlignment = VerticalAlignment.Center;
            var value = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
            value.Inlines.Add(new System.Windows.Documents.Run(words) { Foreground = new SolidColorBrush(color), FontWeight = FontWeights.SemiBold });
            var number = new System.Windows.Documents.Run($"  ({score:0.#} / 100)") { FontSize = 11 };
            number.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
            value.Inlines.Add(number);
            Grid.SetColumn(value, 1);
            row.Children.Add(name);
            row.Children.Add(value);

            // Where the average comes from.
            _raw.TryGetValue(rom.Id, out JObject data);
            var sources = new List<string>();
            double? igdb = Number(data, "igdb_metadata.total_rating");
            if (igdb.HasValue && igdb.Value > 0) sources.Add($"IGDB: {igdb.Value:0} / 100");
            double? critics = Number(data, "igdb_metadata.aggregated_rating");
            if (critics.HasValue && critics.Value > 0) sources.Add($"Critics: {critics.Value:0} / 100");
            string ss = Str(data, "ss_metadata.ss_score");
            if (ss != null && ss != "0") sources.Add($"ScreenScraper: {ss} / 10");
            double? hltb = Number(data, "hltb_metadata.review_score");
            if (hltb.HasValue && hltb.Value > 0) sources.Add($"HowLongToBeat: {hltb.Value:0} / 100");
            row.ToolTip = sources.Count > 0 ? "The average of\n" + string.Join("\n", sources) : "RomM's average score";
            return row;
        }

        private UIElement Fact(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var name = Text(label, 10, false);
            name.Opacity = 0.55;
            name.VerticalAlignment = VerticalAlignment.Center;
            var text = Text(value ?? "", 12, false);
            Grid.SetColumn(text, 1);
            row.Children.Add(name);
            row.Children.Add(text);
            return row;
        }

        // Steam's "Play <game>" box: the name on the left, the size and the green button on the
        // right, overlapping the box's lower edge.
        private bool _adding;

        // Adds the game to the library in the background, then redraws its page, where the
        // button has become "Install".
        private void AddToLibrary(RomMRom rom)
        {
            if (_adding)
                return;
            _adding = true;

            var dialogs = _plugin.Playnite.Dialogs;
            Task.Run(() =>
            {
                string problem = _plugin.AddToLibrary(rom.Id);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _adding = false;
                    RefreshLibraryIds();
                    if (problem != null)
                        dialogs.ShowErrorMessage($"{rom.Name} could not be added to your library.\n\n{problem}", "RomMnite Store");

                    // Still on this game's page: show its new state, where the reader was.
                    if (_gamePage.Visibility == Visibility.Visible)
                        RedrawGame(rom);
                }));
            });
        }

        /// <summary>
        /// What a platform's emulator setup needs from the server, as a list: what was found
        /// bright, with a tick; what is missing greyed, with a cross. Filled in once the server
        /// has been asked.
        /// </summary>
        private UIElement RequirementList(int platformId)
        {
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
            var waiting = Text("Checking your RomM server...", 12, false);
            waiting.Opacity = 0.5;
            list.Children.Add(waiting);
            Task.Run(() =>
            {
                List<Setup.EmulatorSetup.Requirement> needs = null;
                try
                {
                    needs = _plugin.EmulatorSetup?.Requirements(platformId);
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Store] Could not list platform {platformId}'s files: {ex.Message}");
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    list.Children.Clear();
                    if (needs == null || needs.Count == 0)
                    {
                        var none = Text("Couldn't read the files on your RomM server.", 12, false);
                        none.Opacity = 0.5;
                        list.Children.Add(none);
                        return;
                    }
                    foreach (Setup.EmulatorSetup.Requirement need in needs)
                    {
                        string label = need.Label ?? "";
                        if (label.Length > 0)
                            label = char.ToUpperInvariant(label[0]) + label.Substring(1);
                        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0), Opacity = need.Found ? 1.0 : 0.45 };
                        var mark = new TextBlock
                        {
                            Text = need.Found ? "\u2714" : "\u2716",
                            Width = 18,
                            FontSize = 12,
                            FontWeight = FontWeights.Bold,
                            Foreground = need.Found ? new SolidColorBrush(Color.FromRgb(0xA4, 0xD0, 0x07)) : Brushes.White,
                            VerticalAlignment = VerticalAlignment.Top,
                        };
                        DockPanel.SetDock(mark, Dock.Left);
                        row.Children.Add(mark);
                        var text = Text(label, 12, need.Found);
                        text.TextWrapping = TextWrapping.Wrap;
                        row.Children.Add(text);
                        list.Children.Add(row);
                    }
                }));
            });
            return list;
        }

        /// <summary>
        /// A Wikipedia introduction for "About this game", credited as its licence asks: the
        /// article linked, and CC BY-SA named.
        /// </summary>
        private UIElement WikipediaAbout(WikipediaArticle article)
        {
            var panel = new StackPanel();
            foreach (string paragraph in (article.Extract ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var text = Text(paragraph.Trim(), 14, false);
                text.LineHeight = 22;
                text.Margin = new Thickness(0, 0, 0, 12);
                panel.Children.Add(text);
            }
            var credit = Text("From Wikipedia: " + article.Title + " (CC BY-SA 4.0)", 12, false);
            credit.Opacity = 0.6;
            if (!string.IsNullOrEmpty(article.Url))
            {
                credit.Cursor = Cursors.Hand;
                credit.TextDecorations = TextDecorations.Underline;
                credit.ToolTip = article.Url;
                credit.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        System.Diagnostics.Process.Start(article.Url);
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Store] Could not open {article.Url}: {ex.Message}");
                    }
                };
            }
            panel.Children.Add(credit);
            return panel;
        }

        /// <summary>
        /// The box's front and back (ScreenScraper's 2D box art, the copy on the server first,
        /// else ScreenScraper's address), each half the width, side by side; one alone keeps to
        /// its half. Null when there is neither.
        /// </summary>
        // allBlank: called (on the UI thread) when every picture turns out to be a placeholder.
        private UIElement BoxArt(RomMRom rom, Action allBlank = null)
        {
            var ss = rom?.SSMetadata;
            var urls = new[]
                {
                    !string.IsNullOrEmpty(ss?.Box2dPath) ? Url("/assets/romm/resources/" + ss.Box2dPath) : External(ss?.Box2dUrl),
                    !string.IsNullOrEmpty(ss?.Box2dBackPath) ? Url("/assets/romm/resources/" + ss.Box2dBackPath) : External(ss?.Box2dBackUrl),
                }
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();
            if (urls.Count == 0)
                return null;

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int blanks = 0;
            for (int i = 0; i < urls.Count; i++)
            {
                var image = new Image
                {
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(i == 0 ? 0 : 6, 0, i == 0 ? 6 : 0, 0),
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                // A scraper's stand-in for a missing scan (a flat colour, such as a bright green
                // card) is left out once it arrives; with nothing left, the next choice shows.
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(image, (s, e) =>
                {
                    if (image.Visibility != Visibility.Visible || !IsPlaceholder(image.Source as BitmapSource))
                        return;
                    image.Visibility = Visibility.Collapsed;
                    if (++blanks == urls.Count)
                        allBlank?.Invoke();
                });
                ShowPicture(image, urls[i], 1024);
                Grid.SetColumn(image, i);
                grid.Children.Add(image);
            }
            return grid;
        }

        /// <summary>
        /// Whether a picture is a placeholder rather than a scan: nearly all of it one flat colour
        /// (or see-through). Looked at shrunk to 48 pixels across, colours grouped coarsely so
        /// compression noise does not count as detail. A real box's back has far more to it.
        /// </summary>
        internal static bool IsPlaceholder(BitmapSource picture)
        {
            if (picture == null || picture.PixelWidth < 1 || picture.PixelHeight < 1)
                return false;
            try
            {
                double scale = 48.0 / picture.PixelWidth;
                BitmapSource small = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
                small = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int width = small.PixelWidth, height = small.PixelHeight;
                if (width < 1 || height < 1)
                    return false;
                var pixels = new byte[width * height * 4];
                small.CopyPixels(pixels, width * 4, 0);
                var counts = new Dictionary<int, int>();
                int total = width * height, clear = 0;
                for (int p = 0; p < pixels.Length; p += 4)
                {
                    if (pixels[p + 3] < 16)
                    {
                        clear++;
                        continue;
                    }
                    int bucket = ((pixels[p + 2] >> 4) << 8) | ((pixels[p + 1] >> 4) << 4) | (pixels[p] >> 4);
                    counts.TryGetValue(bucket, out int n);
                    counts[bucket] = n + 1;
                }
                if (clear >= total * 0.95)
                    return true;
                if (counts.Count == 0)
                    return false;
                // The commonest colour, then everything near it (a flat colour can straddle two groups).
                int main = counts.OrderByDescending(c => c.Value).First().Key;
                int r = ((main >> 8) << 4) + 8, g = (((main >> 4) & 15) << 4) + 8, b = ((main & 15) << 4) + 8;
                int near = 0;
                for (int p = 0; p < pixels.Length; p += 4)
                {
                    if (pixels[p + 3] >= 16 && Math.Abs(pixels[p + 2] - r) <= 24 && Math.Abs(pixels[p + 1] - g) <= 24 && Math.Abs(pixels[p] - b) <= 24)
                        near++;
                }
                return near + clear >= total * 0.92;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The Store's "without an emulator": the game added to the library when it is not in it,
        /// then downloaded with no emulator set up (RomM.InstallWithoutEmulator).
        /// </summary>
        private void DownloadWithoutEmulator(RomMRom rom, TextBlock link)
        {
            if (_adding)
                return;
            Game present = LibraryGame(rom.Id);
            Game game = present != null && _plugin.InAccountLibrary(present) ? present : null;
            var dialogs = _plugin.Playnite.Dialogs;

            Action download = () =>
            {
                try
                {
                    Game added = LibraryGame(rom.Id);
                    if (added != null)
                        _plugin.InstallWithoutEmulator(added.Id);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not download {rom.Name} without an emulator: {ex.Message}");
                }
                RefreshLibraryIds();
                if (_gamePage.Visibility == Visibility.Visible)
                    RedrawGame(rom);
            };

            if (game != null)
            {
                download();
                return;
            }

            _adding = true;
            link.Text = "Adding...";
            link.Cursor = Cursors.Arrow;
            Task.Run(() =>
            {
                string problem = _plugin.AddToLibrary(rom.Id);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _adding = false;
                    RefreshLibraryIds();
                    if (problem != null)
                    {
                        dialogs.ShowErrorMessage($"{rom.Name} could not be added to your library.\n\n{problem}", "RomMnite Store");
                        if (_gamePage.Visibility == Visibility.Visible)
                            RedrawGame(rom);
                        return;
                    }
                    download();
                }));
            });
        }

        private UIElement BuildInstallBar(RomMRom rom)
        {
            // A game in Playnite for another account only (hidden for this one) is not in this
            // account's library: it is added first, as any other, even when it is installed.
            Game present = LibraryGame(rom.Id);
            Game game = present != null && _plugin.InAccountLibrary(present) ? present : null;

            var bar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(16, 16, 16, 34),
            };

            // Steam's order: Install Emulator (nothing here plays its platform yet, or its emulator
            // is gone), Add to Library, Install, Play. The button follows the game from there on
            // (Downloading / Cancel, Launching...); this page redraws when it moves past the
            // first two, for the words that go with it.
            bool ready = game == null ? _plugin.PlatformReady(rom.PlatformId) : !_plugin.NeedsEmulator(game);
            bool addable = game == null && ready;
            bool supported = ready || _plugin.EmulatorAvailable(rom.PlatformId);
            bool lacking = !ready && supported && _missingFiles.ContainsKey(rom.PlatformId);
            string label = !ready && !(game != null && game.IsInstalling)
                    ? (!supported ? "Emulator Unsupported" : lacking ? "Emulator Unavailable" : "Install Emulator")
                : game == null ? "Add to Library"
                : game.IsInstalled ? "Play"
                : "Install";
            Setup.EmulatorSetup.Plan setup = !ready ? _plugin.EmulatorSetup?.PlanFor(rom.PlatformId) : null;

            var button = new GameActionButton(_plugin, rom.Id, rom.PlatformId,
                () => AddToLibrary(rom),
                () =>
                {
                    RefreshLibraryIds();
                    if (_gamePage.Visibility == Visibility.Visible)
                        RedrawGame(rom);
                })
            {
                VerticalAlignment = VerticalAlignment.Center,
            };
            // The size and the button sit in a dark block that hangs off the box's lower edge,
            // where Steam puts the price and "Add to Cart".
            var size = Text(FormatSize(rom.FileSizeBytes), 13, false);
            size.VerticalAlignment = VerticalAlignment.Center;
            size.Margin = new Thickness(10, 0, 12, 0);
            var cluster = new StackPanel { Orientation = Orientation.Horizontal };
            cluster.Children.Add(size);
            cluster.Children.Add(button);
            var hanging = new Border
            {
                Background = Brushes.Black,
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 16, -18),
                Child = cluster,
            };

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Text((label == "Install Emulator" ? "Set up " : label != "Install Emulator" && !ready ? "Get " : game == null ? "Get " : game.IsInstalled ? "Play " : "View ") + (rom.Name ?? "") + (game != null && !game.IsInstalled && ready ? " in your library" : ""), 21, false));
            if (game != null && !game.IsInstalling)
            {
                // In the library: it can be taken back out from here too (uninstalled first,
                // unless another account on this computer has it).
                var remove = Text("Remove from library", 12, false);
                remove.Opacity = 0.7;
                remove.Cursor = Cursors.Hand;
                remove.Margin = new Thickness(0, 4, 0, 0);
                remove.TextDecorations = TextDecorations.Underline;
                remove.HorizontalAlignment = HorizontalAlignment.Left;
                remove.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        // Removed or uninstalled, the page redraws itself; only hidden, it is redrawn here.
                        _plugin.RemoveFromAccountLibrary(new List<Game> { game }, () =>
                        {
                            RefreshLibraryIds();
                            ShowGame(rom);
                        });
                    }
                    catch (Exception ex)
                    {
                        s_log.Warn($"[Store] Could not remove {game.Name} from the library: {ex.Message}");
                    }
                };
                words.Children.Add(remove);
            }

            if (label == "Install Emulator" || label == "Emulator Unavailable" || label == "Emulator Unsupported")
            {
                string platformName = (_plugin.Settings?.RomMPlatforms?.FirstOrDefault(p => p.Id == rom.PlatformId)
                                       ?? _serverPlatforms?.FirstOrDefault(p => p.Id == rom.PlatformId))?.PlayniteName ?? "its platform";
                var note = Text(label == "Emulator Unsupported"
                    ? $"{platformName} isn't supported yet."
                    : label == "Emulator Unavailable"
                        ? $"{setup?.EmulatorName ?? "Its emulator"} needs these files in {platformName}'s firmware on your RomM server:"
                        : setup != null
                            ? $"Needs {setup.EmulatorName}, which isn't installed yet."
                            : $"No emulator for {platformName} yet.", 12, false);
                note.Opacity = 0.7;
                note.Margin = new Thickness(0, 4, 0, 0);
                note.TextWrapping = TextWrapping.Wrap;
                words.Children.Add(note);
                if (label == "Emulator Unavailable")
                    words.Children.Add(RequirementList(rom.PlatformId));

                // The files alone, emulator or not: added to the library first when it is not in it.
                if (game == null || (!game.IsInstalled && !game.IsInstalling))
                {
                    var bare = Text(game == null ? "Get it without an emulator" : "Download without an emulator", 12, false);
                    bare.Opacity = 0.7;
                    bare.Cursor = Cursors.Hand;
                    bare.Margin = new Thickness(0, 4, 0, 0);
                    bare.TextDecorations = TextDecorations.Underline;
                    bare.HorizontalAlignment = HorizontalAlignment.Left;
                    bare.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        DownloadWithoutEmulator(rom, bare);
                    };
                    words.Children.Add(bare);
                }
            }

            if (game == null && addable)
            {
                var note = Text(present != null && present.IsInstalled
                    ? "Already on this computer. Add it to your library to play."
                    : "Add it to your library, then install it.", 12, false);
                note.Opacity = 0.7;
                note.Margin = new Thickness(0, 4, 0, 0);
                words.Children.Add(note);
            }
            bar.Child = words;

            var holder = new Grid { Margin = new Thickness(0, 0, 0, 44) };
            holder.Children.Add(bar);
            holder.Children.Add(hanging);
            return holder;
        }

        private void ShowInLibrary(int romId)
        {
            try
            {
                var game = _plugin.Playnite.Database.Games.FirstOrDefault(g =>
                    g.PluginId == _plugin.Id && RomMGameId.TryParse(g.GameId, out int id, out string _) && id == romId);
                if (game == null)
                    return;

                _plugin.CloudStatus.StoreVisible = false;
                _plugin.Playnite.MainView.SwitchToLibraryView();
                _plugin.Playnite.MainView.SelectGame(game.Id);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Store] Could not show the game in the library: {ex.Message}");
            }
        }

        private static string FormatSize(ulong bytes)
        {
            if (bytes >= 1UL << 30) return $"{bytes / (double)(1UL << 30):0.0} GB";
            if (bytes >= 1UL << 20) return $"{bytes / (double)(1UL << 20):0} MB";
            return $"{bytes / 1024.0:0} KB";
        }
    }
}
