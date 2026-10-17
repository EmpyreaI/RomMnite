using Playnite.SDK.Controls;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using RomM.Saves;

namespace RomM.Account
{
    /// <summary>
    /// The STORE and LIBRARY tabs, for the window's top bar beside the account badge, as in
    /// Steam. A theme places them with &lt;ContentControl x:Name="RomM_NavTabs" /&gt; in the
    /// main window. The tab for what is showing is lit and underlined. Resting the pointer on
    /// a tab drops a small menu under it, as Steam's do: the Store's Home, then its Platforms,
    /// Genres and Series pages; the library's Home and Downloads.
    /// </summary>
    public class NavTabsControl : PluginUserControl
    {
        // Steam's highlight blue.
        private static readonly Brush Lit = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush MenuText = new SolidColorBrush(Color.FromRgb(0xDC, 0xDE, 0xDF));

        private readonly CloudStatusModel _model;
        private readonly TextBlock _storeText;
        private readonly TextBlock _libraryText;
        private readonly Border _storeLine;
        private readonly Border _libraryLine;
        private bool _overStore;
        private bool _overLibrary;

        // The open drop-down, the tab it belongs to, and the check that closes it once the
        // pointer has left both.
        private Popup _menu;
        private FrameworkElement _menuTab;
        private readonly DispatcherTimer _menuCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        /// <param name="showLibrary">Switches Playnite to its library view.</param>
        /// <param name="showLibraryPage">Shows a library page: "Home" or "Downloads".</param>
        public NavTabsControl(CloudStatusModel model, NavigationHistory history, Action showLibrary, Action<string> showLibraryPage = null)
        {
            _model = model;
            _history = history;
            Focusable = false;
            VerticalAlignment = VerticalAlignment.Center;
            WindowChrome.SetIsHitTestVisibleInChrome(this, true);

            var storeMenu = new List<Tuple<string, Action>>
            {
                Tuple.Create<string, Action>("Home", () => _model.OpenStoreHome()),
                Tuple.Create<string, Action>("Platforms", () => _model.OpenStoreDirectory("Platforms")),
                Tuple.Create<string, Action>("Genres", () => _model.OpenStoreDirectory("Genres")),
                Tuple.Create<string, Action>("Series", () => _model.OpenStoreDirectory("Series")),
            };
            List<Tuple<string, Action>> libraryMenu = null;
            if (showLibraryPage != null)
            {
                libraryMenu = new List<Tuple<string, Action>>
                {
                    Tuple.Create<string, Action>("Home", () => showLibraryPage("Home")),
                    Tuple.Create<string, Action>("Downloads", () => showLibraryPage("Downloads")),
                };
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            // Back and forward, as at the left of Steam's bar. The mouse's own back and forward
            // buttons, and Alt with the arrow keys, do the same.
            if (_history != null)
            {
                _backArrow = Arrow("\uE72B", "Back", () => _history.Back());
                _forwardArrow = Arrow("\uE72A", "Forward", () => _history.Forward());
                row.Children.Add(_backArrow);
                row.Children.Add(_forwardArrow);
                ((FrameworkElement)_forwardArrow).Margin = new Thickness(0, 0, 8, 0);
                _history.Changed += () => Dispatcher.BeginInvoke(new Action(PaintArrows));
                Loaded += (s, e) => HookWindowKeys();
                PaintArrows();
            }

            row.Children.Add(Tab("STORE", out _storeText, out _storeLine, () => _model.OpenStoreHome(), over => { _overStore = over; Paint(); }, storeMenu));
            row.Children.Add(Tab("LIBRARY", out _libraryText, out _libraryLine, () =>
            {
                _model.StoreVisible = false;
                showLibrary?.Invoke();
            }, over => { _overLibrary = over; Paint(); }, libraryMenu));
            Content = row;

            _menuCheck.Tick += (s, e) =>
            {
                bool inside = (_menuTab != null && _menuTab.IsMouseOver) || (_menu?.Child != null && _menu.Child.IsMouseOver);
                if (!inside)
                    CloseMenu();
            };

            _model.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(CloudStatusModel.StoreVisible))
                    Dispatcher.BeginInvoke(new Action(Paint));
            };
            Paint();
        }

        private readonly NavigationHistory _history;
        private Border _backArrow;
        private Border _forwardArrow;
        private bool _windowHooked;

        private Border Arrow(string glyph, string tip, Action go)
        {
            var text = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var arrow = new Border { Width = 30, Height = 30, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, Child = text, VerticalAlignment = VerticalAlignment.Center };
            WindowChrome.SetIsHitTestVisibleInChrome(arrow, true);
            arrow.MouseEnter += (s, e) => { if (arrow.IsEnabled) arrow.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)); };
            arrow.MouseLeave += (s, e) => arrow.Background = Brushes.Transparent;
            arrow.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (arrow.IsEnabled)
                    go();
            };
            return arrow;
        }

        private void PaintArrows()
        {
            if (_backArrow == null)
                return;
            _backArrow.IsEnabled = _history.CanGoBack;
            _backArrow.Opacity = _history.CanGoBack ? 0.85 : 0.3;
            _forwardArrow.IsEnabled = _history.CanGoForward;
            _forwardArrow.Opacity = _history.CanGoForward ? 0.85 : 0.3;
        }

        private void HookWindowKeys()
        {
            if (_windowHooked)
                return;
            Window window = Window.GetWindow(this);
            if (window == null)
                return;
            _windowHooked = true;
            window.PreviewMouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.XButton1) { _history.Back(); e.Handled = true; }
                else if (e.ChangedButton == MouseButton.XButton2) { _history.Forward(); e.Handled = true; }
            };
            window.PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Alt)
                    return;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Left) { _history.Back(); e.Handled = true; }
                else if (key == Key.Right) { _history.Forward(); e.Handled = true; }
            };
        }

        private UIElement Tab(string label, out TextBlock text, out Border line, Action click, Action<bool> hover, List<Tuple<string, Action>> menu)
        {
            text = new TextBlock { Text = label, FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            line = new Border { Height = 3, Margin = new Thickness(0, 3, 0, 0) };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(text);
            stack.Children.Add(line);

            var tab = new Border { Background = Brushes.Transparent, Padding = new Thickness(10, 2, 10, 0), Cursor = Cursors.Hand, Child = stack };
            WindowChrome.SetIsHitTestVisibleInChrome(tab, true);
            tab.MouseEnter += (s, e) =>
            {
                hover(true);
                if (menu != null && menu.Count > 0)
                    OpenMenu(tab, menu);
            };
            tab.MouseLeave += (s, e) => hover(false);
            tab.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                CloseMenu();
                click();
            };
            return tab;
        }

        private void OpenMenu(FrameworkElement tab, List<Tuple<string, Action>> entries)
        {
            if (_menu != null && ReferenceEquals(_menuTab, tab) && _menu.IsOpen)
                return;
            CloseMenu();

            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            for (int i = 0; i < entries.Count; i++)
            {
                Action pick = entries[i].Item2;
                var words = new TextBlock { Text = entries[i].Item1, FontSize = 14, Foreground = MenuText };
                var item = new Border { Padding = new Thickness(18, 8, 28, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = words };
                item.MouseEnter += (s, e) =>
                {
                    item.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                    words.Foreground = Brushes.White;
                };
                item.MouseLeave += (s, e) =>
                {
                    item.Background = Brushes.Transparent;
                    words.Foreground = MenuText;
                };
                item.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    CloseMenu();
                    pick();
                };
                // A thin rule under Home, as in Steam's menus.
                if (i == 1 && entries[0].Item1 == "Home")
                    list.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 2, 10, 2), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
                list.Children.Add(item);
            }

            var panel = new Border
            {
                MinWidth = 150,
                Background = global::RomM.Store.ThemeColors.Menu(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Child = list,
            };

            _menuTab = tab;
            _menu = new Popup
            {
                PlacementTarget = tab,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 2,
                AllowsTransparency = true,
                StaysOpen = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = panel,
            };

            // Placed by hand, at the tab's lower-left corner on the screen: in the window's title
            // area, placing relative to the tab put the menu at the corner of the screen.
            try
            {
                var source = PresentationSource.FromVisual(tab);
                if (source?.CompositionTarget != null)
                {
                    Point corner = tab.PointToScreen(new Point(0, tab.ActualHeight));
                    corner = source.CompositionTarget.TransformFromDevice.Transform(corner);
                    _menu.Placement = PlacementMode.Absolute;
                    _menu.HorizontalOffset = corner.X;
                    _menu.VerticalOffset = corner.Y + 2;
                }
            }
            catch
            {
                // Keep the placement relative to the tab.
            }
            _menu.IsOpen = true;
            _menuCheck.Start();
        }

        private void CloseMenu()
        {
            _menuCheck.Stop();
            if (_menu != null)
                _menu.IsOpen = false;
            _menu = null;
            _menuTab = null;
        }

        private void Paint()
        {
            bool store = _model.StoreVisible;
            PaintTab(_storeText, _storeLine, store, _overStore);
            PaintTab(_libraryText, _libraryLine, !store, _overLibrary);
        }

        private void PaintTab(TextBlock text, Border line, bool active, bool over)
        {
            if (active)
            {
                text.Foreground = Lit;
                text.Opacity = 1;
                line.Background = Lit;
            }
            else
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                text.Opacity = over ? 1 : 0.75;
                line.Background = Brushes.Transparent;
            }
        }
    }
}
