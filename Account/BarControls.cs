using Playnite.SDK;
using Playnite.SDK.Controls;
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using RomM.Saves;

namespace RomM.Account
{
    /// <summary>
    /// Finds parts of Playnite's own window. Its main menu and its library search belong to
    /// Playnite's top panel and cannot be placed anywhere else by a theme, so the bar's own
    /// button and search box drive the originals (which the theme hides) instead.
    /// </summary>
    internal static class PlayniteParts
    {
        public static FrameworkElement Find(Func<FrameworkElement, bool> wanted)
        {
            Window window = Application.Current?.MainWindow;
            return window == null ? null : Find(window, wanted);
        }

        /// <summary>Every matching part, wherever it is in the window.</summary>
        public static System.Collections.Generic.List<FrameworkElement> FindAll(Func<FrameworkElement, bool> wanted)
        {
            var found = new System.Collections.Generic.List<FrameworkElement>();
            Window window = Application.Current?.MainWindow;
            if (window != null)
                Collect(window, wanted, found);
            return found;
        }

        private static void Collect(DependencyObject parent, Func<FrameworkElement, bool> wanted, System.Collections.Generic.List<FrameworkElement> found)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element && wanted(element))
                    found.Add(element);
                Collect(child, wanted, found);
            }
        }

        private static FrameworkElement Find(DependencyObject parent, Func<FrameworkElement, bool> wanted)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element && wanted(element))
                    return element;
                FrameworkElement deeper = Find(child, wanted);
                if (deeper != null)
                    return deeper;
            }
            return null;
        }
    }

    /// <summary>
    /// The small logo button at the top left of the window's bar, like Steam's "Steam" button:
    /// it opens Playnite's main menu. A theme places it with
    /// &lt;ContentControl x:Name="RomM_MainMenu" /&gt;.
    /// </summary>
    public class MainMenuButtonControl : PluginUserControl
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        public MainMenuButtonControl()
        {
            Focusable = false;
            WindowChrome.SetIsHitTestVisibleInChrome(this, true);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            try
            {
                string icon = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "icon.png");
                if (File.Exists(icon))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 64;
                    bitmap.UriSource = new Uri(icon);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    var image = new Image { Source = bitmap, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                    row.Children.Add(image);
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Bar] Could not load the menu button's icon: {ex.Message}");
            }

            var label = new TextBlock { Text = "RomMnite", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            row.Children.Add(label);

            var button = new Border { Background = Brushes.Transparent, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Opacity = 0.8, Child = row };
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.MouseEnter += (s, e) => button.Opacity = 1;
            button.MouseLeave += (s, e) => button.Opacity = 0.8;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                OpenMenu(button);
            };
            Content = button;
        }

        // Playnite builds its main menu onto the top panel's own (now hidden) menu button; it is
        // borrowed from there and opened under this one.
        private static void OpenMenu(UIElement under)
        {
            try
            {
                FrameworkElement original = PlayniteParts.Find(e => e.Name == "PART_ElemMainMenu" && e.ContextMenu != null);
                ContextMenu menu = original?.ContextMenu;
                if (menu == null)
                {
                    s_log.Warn("[Bar] Playnite's main menu was not found; is the library view showing?");
                    return;
                }

                menu.PlacementTarget = under;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Bar] Could not open the main menu: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The search box in the middle of the window's bar. It searches whatever is showing: the
    /// library (through Playnite's own search, as you type) or the Store (on Enter, or after a
    /// short pause). Each keeps its own text. A theme places it with
    /// &lt;ContentControl x:Name="RomM_Search" /&gt;.
    /// </summary>
    public class BarSearchControl : PluginUserControl
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly CloudStatusModel _model;
        private readonly TextBox _box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _hint;
        private readonly DispatcherTimer _pause = new DispatcherTimer();
        private string _libraryText = "";
        private string _storeText = "";
        private bool _switching;

        public BarSearchControl(CloudStatusModel model)
        {
            _model = model;
            Focusable = false;
            WindowChrome.SetIsHitTestVisibleInChrome(this, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_box, true);

            _hint = new TextBlock { IsHitTestVisible = false, Opacity = 0.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var grid = new Grid();
            grid.Children.Add(_box);
            grid.Children.Add(_hint);
            Content = grid;

            _pause.Tick += (s, e) =>
            {
                _pause.Stop();
                Apply();
            };
            _box.TextChanged += (s, e) =>
            {
                _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_switching)
                    return;

                // The library narrows as you type; the Store asks the server, so it waits a moment.
                _pause.Stop();
                _pause.Interval = TimeSpan.FromMilliseconds(_model.StoreVisible ? 600 : 150);
                _pause.Start();
            };
            _box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    _pause.Stop();
                    Apply();
                }
                else if (e.Key == Key.Escape)
                {
                    _box.Text = "";
                }
            };

            _model.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(CloudStatusModel.StoreVisible))
                    Dispatcher.BeginInvoke(new Action(ShowModeText));
            };
            ShowModeText();
        }

        // Switching between Store and library: show that side's own search text.
        private void ShowModeText()
        {
            _pause.Stop();
            _switching = true;
            try
            {
                _hint.Text = _model.StoreVisible ? "Search the store" : "Search your library";
                _box.Text = _model.StoreVisible ? _storeText : _libraryText;
                _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _switching = false;
            }
        }

        private void Apply()
        {
            string text = _box.Text ?? "";
            if (_model.StoreVisible)
            {
                if (text == _storeText)
                    return;
                _storeText = text;
                _model.SearchStore(text);
            }
            else
            {
                _libraryText = text;
                SearchLibrary(text);
            }
        }

        // Playnite's own search box is still there, hidden by the theme and still wired to the
        // library's filter; typing is passed on by setting its text.
        private static void SearchLibrary(string text)
        {
            try
            {
                FrameworkElement original = PlayniteParts.Find(e => e.Name == "PART_TextMainSearch");
                DependencyProperty property = original?.GetType()
                    .GetField("TextProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?
                    .GetValue(null) as DependencyProperty;
                if (property == null)
                {
                    s_log.Warn("[Bar] Playnite's library search was not found; is the library view showing?");
                    return;
                }

                original.SetCurrentValue(property, text);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Bar] Could not search the library: {ex.Message}");
            }
        }
    }
}
