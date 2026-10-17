using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.Store
{
    /// <summary>
    /// The drop-down under the Store's search box, after Steam's. Before anything is typed:
    /// a few recommended games, the games viewed lately, and the genres played most. While
    /// typing: the best few matches (games already in the library included, marked), and
    /// genres and series whose names match, to browse. "Advanced Search", or Enter, opens
    /// the full results.
    /// </summary>
    public partial class StoreView
    {
        private Popup _suggest;
        private Grid _searchBoxHost;
        private int _suggestRequest;

        // Games whose Store pages were opened lately, newest first.
        private readonly List<RomMRom> _recentlyViewed = new List<RomMRom>();

        private static readonly Brush SuggestBack = new SolidColorBrush(Color.FromRgb(0x3D, 0x44, 0x50));
        private static readonly Brush SuggestRow = new SolidColorBrush(Color.FromArgb(0x40, 0x67, 0x70, 0x7B));
        private static readonly Brush SuggestRowLit = new SolidColorBrush(Color.FromArgb(0x90, 0x67, 0x70, 0x7B));
        private static readonly Brush LinkBlue = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));

        private void NoteViewed(RomMRom rom)
        {
            if (rom == null)
                return;
            _recentlyViewed.RemoveAll(r => r.Id == rom.Id);
            _recentlyViewed.Insert(0, rom);
            if (_recentlyViewed.Count > 6)
                _recentlyViewed.RemoveAt(_recentlyViewed.Count - 1);
        }

        private bool _searchHooked;

        /// <summary>
        /// A click anywhere outside the search box and its drop-down closes the drop-down and
        /// ends typing in the box; the click itself goes on to whatever was clicked. So do
        /// leaving the window and the Store going out of sight.
        /// </summary>
        private void HookSearchClosing()
        {
            if (_searchHooked)
                return;
            Window window = Window.GetWindow(this);
            if (window == null)
                return;
            _searchHooked = true;
            window.PreviewMouseDown += (s, e) =>
            {
                if ((_searchBoxHost != null && _searchBoxHost.IsMouseOver) || (_suggest?.Child?.IsMouseOver ?? false))
                    return;
                CloseSuggestions();
                if (_search.IsKeyboardFocusWithin)
                {
                    _searchPause?.Stop();
                    // To the window itself, so its own keys (Alt+arrows and Playnite's) still work.
                    FocusManager.SetFocusedElement(FocusManager.GetFocusScope(_search), null);
                    Keyboard.Focus(window);
                }
            };
            window.Deactivated += (s, e) => CloseSuggestions();
            window.LocationChanged += (s, e) => CloseSuggestions();
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible)
                    CloseSuggestions();
            };
        }

        private void CloseSuggestions()
        {
            _suggestRequest++;
            if (_suggest != null)
                _suggest.IsOpen = false;
        }

        // Opens (or refreshes) the drop-down for what is in the box now.
        private void UpdateSuggestions()
        {
            if (_searchBoxHost == null || !_search.IsKeyboardFocusWithin || Host.Length == 0 || !_loadedOnce)
                return;

            string text = (_search.Text ?? "").Trim();
            int request = ++_suggestRequest;

            var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
            if (text.Length == 0)
            {
                FillIdleSuggestions(body);
                ShowSuggestions(body);
                return;
            }

            // Matches come from the server; the genres and series from the menus' lists.
            var genres = MatchingNames(_genre, text).Where(n => (CategoryCount("genre", n) ?? 2) > 1).Select(n => Tuple.Create(n, "Genre", CategoryKind.Genre));
            var series = MatchingNames(_series, text).Select(n => Tuple.Create(n, "Series", CategoryKind.Series));
            var browse = genres.Concat(series).OrderBy(t => t.Item1.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1).Take(3).ToList();

            var waiting = SuggestHeading("Search results");
            body.Children.Add(waiting);
            var results = new StackPanel();
            body.Children.Add(results);
            var searching = Text("Searching...", 12, false);
            searching.Opacity = 0.6;
            searching.Margin = new Thickness(2, 2, 0, 6);
            results.Children.Add(searching);
            AddBrowseBy(body, browse);
            AddAdvancedSearch(body, text);
            ShowSuggestions(body);

            Task.Run(() =>
            {
                var found = new List<HomeItem>();
                try
                {
                    foreach (JToken rom in Get($"/api/roms?limit=5&search_term={Uri.EscapeDataString(text)}")?["items"] as JArray ?? new JArray())
                    {
                        HomeItem item = ItemFrom(rom as JObject, null, null);
                        if (item != null)
                            found.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Store] Suggestions for \"{text}\" failed: {ex.Message}");
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (request != _suggestRequest)
                        return;
                    results.Children.Clear();
                    foreach (HomeItem item in found)
                        _raw[item.Rom.Id] = item.Raw;
                    // Playable platforms first; games in the library stay, marked.
                    foreach (HomeItem item in found.OrderBy(i => Playable(i.Rom) ? 0 : 1).Take(4))
                        results.Children.Add(SuggestGameRow(item));
                    if (found.Count == 0)
                    {
                        var none = Text($"No games match \"{text}\".", 12, false);
                        none.Opacity = 0.6;
                        none.Margin = new Thickness(2, 2, 0, 6);
                        results.Children.Add(none);
                    }
                }));
            });
        }

        private static List<string> MatchingNames(ComboBox list, string text)
        {
            var names = new List<string>();
            for (int i = 1; i < list.Items.Count; i++)
            {
                string name = list.Items[i]?.ToString();
                if (!string.IsNullOrEmpty(name) && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    names.Add(name);
            }
            return names;
        }

        private void ShowSuggestions(UIElement body)
        {
            var panel = new Border
            {
                Background = ThemeColors.Menu(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Width = Math.Max(380, _searchBoxHost.ActualWidth),
                Child = body,
            };
            if (_suggest == null)
            {
                _suggest = new Popup
                {
                    PlacementTarget = _searchBoxHost,
                    Placement = PlacementMode.Bottom,
                    VerticalOffset = 4,
                    AllowsTransparency = true,
                    // Closed by hand (HookSearchClosing): a popup that closes itself holds the
                    // mouse while open, which swallowed clicks on the bar and the page.
                    StaysOpen = true,
                };
            }
            _suggest.Child = panel;
            // One drop-down at a time: the bar's menus (the wishlist, genres...) give way.
            if (_menu != null && _menu.IsOpen)
                CloseNavMenu();
            _suggest.IsOpen = true;
        }

        private TextBlock SuggestHeading(string text)
        {
            var heading = Text(text, 13, false);
            heading.Opacity = 0.85;
            heading.Margin = new Thickness(2, 4, 0, 6);
            return heading;
        }

        // Before anything is typed: recommendations, what was viewed lately, top genres.
        private void FillIdleSuggestions(StackPanel body)
        {
            var picks = ForStore(_feedItems ?? new List<HomeItem>()).Take(4).ToList();
            if (picks.Count > 0)
            {
                body.Children.Add(SuggestHeading("Recommended for you"));
                foreach (HomeItem item in picks)
                    body.Children.Add(SuggestGameRow(item));
            }

            if (_recentlyViewed.Count > 0)
            {
                body.Children.Add(SuggestHeading("Recently viewed"));
                var links = new WrapPanel { Margin = new Thickness(2, 0, 0, 6) };
                for (int i = 0; i < _recentlyViewed.Count; i++)
                {
                    RomMRom rom = _recentlyViewed[i];
                    var link = new TextBlock { Text = (rom.Name ?? rom.FileNameNoExt ?? "") + (i < _recentlyViewed.Count - 1 ? ", " : ""), FontSize = 13, Foreground = LinkBlue, Cursor = Cursors.Hand };
                    link.MouseEnter += (s, e) => link.TextDecorations = TextDecorations.Underline;
                    link.MouseLeave += (s, e) => link.TextDecorations = null;
                    link.MouseLeftButtonUp += (s, e) =>
                    {
                        CloseSuggestions();
                        ShowGame(rom);
                    };
                    links.Children.Add(link);
                }
                body.Children.Add(links);
            }

            var top = TopGenres(3);
            if (top.Count > 0)
            {
                body.Children.Add(SuggestHeading("Your top genres"));
                var tiles = new UniformGrid2Row(top.Count);
                foreach (string genre in top)
                    tiles.Add(GenreTile(genre));
                body.Children.Add(tiles.Panel);
            }

            AddAdvancedSearch(body, "");
        }

        // The genres of the library's games, the most played counting most.
        private List<string> TopGenres(int count)
        {
            return LibraryWeights(g => g.Genres?.Select(x => x?.Name))
                .OrderByDescending(w => w.Value).Take(count).Select(w => w.Key).ToList();
        }

        // A genre as a picture tile with its name on a white label, as Steam's are.
        private UIElement GenreTile(string genre)
        {
            var tile = new Grid { Height = 64, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x2A, 0x2A)) };

            // Art from a game of that genre, if one with fan art is known.
            string art = null;
            foreach (JObject data in _raw.Values)
            {
                string fanart = Str(data, "ss_metadata.fanart_path");
                if (!string.IsNullOrEmpty(fanart) && Strings(data, "metadatum.genres").Any(g => string.Equals(g, genre, StringComparison.OrdinalIgnoreCase)))
                {
                    art = "/assets/romm/resources/" + fanart;
                    break;
                }
            }
            if (art != null)
            {
                var image = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.55 };
                ShowPicture(image, Url(art), 300);
                tile.Children.Add(image);
            }
            tile.Children.Add(new Border
            {
                Background = Brushes.White,
                Padding = new Thickness(8, 1, 8, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = genre.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.Black },
            });
            tile.MouseLeftButtonUp += (s, e) =>
            {
                CloseSuggestions();
                BrowseGenre(genre);
            };
            return tile;
        }

        // A game: its wide picture, name, and platform; marked when it is in the library.
        private UIElement SuggestGameRow(HomeItem item)
        {
            RomMRom rom = item.Rom;
            var picture = new Grid { Width = 148, Height = 56, Background = Brushes.Black, ClipToBounds = true };
            var image = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
            ShowPicture(image, Url(Wide(item)), 300);
            picture.Children.Add(image);
            UIElement logo = FanartPath(rom) != null ? LogoOverlay(rom, 1, 4, 240) : null;
            if (logo != null)
                picture.Children.Add(logo);

            var words = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = Text(rom.Name ?? rom.FileNameNoExt ?? "", 13, true);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(name);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            if (_inLibrary.Contains(rom.Id))
            {
                line.Children.Add(new Border
                {
                    Background = LinkBlue,
                    Padding = new Thickness(5, 0, 5, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock { Text = "IN LIBRARY", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
                });
            }
            else if (Unavailable(rom))
            {
                line.Children.Add(UnavailableTag(10, new Thickness(5, 0, 5, 1), new Thickness(0, 0, 6, 0), MarkOf(rom)));
            }
            var where = Text(PlatformOf(item) ?? "", 11, false);
            where.Opacity = Playable(rom) ? 0.7 : 0.45;
            line.Children.Add(where);
            words.Children.Add(line);

            var row = new DockPanel { Background = SuggestRow, Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand };
            DockPanel.SetDock(picture, Dock.Left);
            row.Children.Add(picture);
            row.Children.Add(words);
            row.MouseEnter += (s, e) => row.Background = SuggestRowLit;
            row.MouseLeave += (s, e) => row.Background = SuggestRow;
            row.MouseLeftButtonUp += (s, e) =>
            {
                CloseSuggestions();
                ShowGame(rom);
            };
            return row;
        }

        // "Browse by...": genres and series whose names match what is typed.
        private void AddBrowseBy(StackPanel body, List<Tuple<string, string, CategoryKind>> entries)
        {
            if (entries.Count == 0)
                return;
            body.Children.Add(SuggestHeading("Browse by..."));
            foreach (var entry in entries)
            {
                string value = entry.Item1;
                CategoryKind kind = entry.Item3;
                var label = new Border
                {
                    Background = Brushes.White,
                    Padding = new Thickness(8, 1, 8, 1),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = value.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.Black },
                };
                var what = Text(entry.Item2, 12, false);
                what.Opacity = 0.7;
                what.VerticalAlignment = VerticalAlignment.Center;
                what.Margin = new Thickness(0, 0, 12, 0);
                var row = new DockPanel { Height = 40, Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, LastChildFill = false };
                var shade = new LinearGradientBrush(Color.FromArgb(0xC0, 0x7A, 0x2A, 0x2A), Color.FromArgb(0x40, 0x7A, 0x4A, 0x2A), 0);
                row.Background = shade;
                DockPanel.SetDock(what, Dock.Right);
                row.Children.Add(what);
                row.Children.Add(label);
                row.MouseEnter += (s, e) => row.Opacity = 0.85;
                row.MouseLeave += (s, e) => row.Opacity = 1;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    CloseSuggestions();
                    ClearFilters(null);
                    ShowCategory(kind, value, null);
                };
                body.Children.Add(row);
            }
        }

        private void AddAdvancedSearch(StackPanel body, string text)
        {
            var label = Text("Advanced Search", 13, false);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var button = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x50, 0x67, 0x70, 0x7B)),
                Padding = new Thickness(0, 8, 0, 8),
                Margin = new Thickness(0, 8, 0, 0),
                Cursor = Cursors.Hand,
                Child = label,
            };
            button.MouseEnter += (s, e) => button.Background = new SolidColorBrush(Color.FromArgb(0x90, 0x67, 0x70, 0x7B));
            button.MouseLeave += (s, e) => button.Background = new SolidColorBrush(Color.FromArgb(0x50, 0x67, 0x70, 0x7B));
            button.MouseLeftButtonUp += (s, e) =>
            {
                CloseSuggestions();
                if (text.Length > 0)
                {
                    RunSearch(true);
                }
                else
                {
                    // Nothing typed: every game, in the plain grid.
                    ClearFilters(null);
                    if (_platforms.Items.Count > 1)
                    {
                        if (_platforms.SelectedIndex == 1)
                            LoadGames();
                        else
                            _platforms.SelectedIndex = 1;
                    }
                }
            };
            body.Children.Add(button);
        }
    }
}
