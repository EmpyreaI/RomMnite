using Newtonsoft.Json.Linq;
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
    /// "Other games in this series" on a game's page, and the wishlist they feed. The series'
    /// games on the server open their Store pages; those it lacks (found through IGDB, by way of
    /// the server's own metadata search, and the game's remakes, remasters and ports) can be put
    /// on the wishlist, which the plugin checks against the server and announces when one
    /// arrives. The wishlist itself drops from the right of the Store's bar.
    /// </summary>
    public partial class StoreView
    {
        private static readonly Brush WishBlue = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush WishGreen = new SolidColorBrush(Color.FromRgb(0x5C, 0xB8, 0x1D));

        // A game of the series the server lacks.
        private sealed class SeriesGame
        {
            public int IgdbId;
            public string Name;
            public string Cover;
        }

        // What the metadata search found for a series on a platform, kept for the session.
        private readonly Dictionary<string, List<SeriesGame>> _seriesSearches = new Dictionary<string, List<SeriesGame>>(StringComparer.OrdinalIgnoreCase);

        private static string BigCover(string url)
        {
            string full = External(url);
            return full?.Replace("/t_thumb/", "/t_cover_big/");
        }

        // What the server answered when asked for a game by name (its game, or null when it
        // has none), kept for the session, so a series seen before fills in at once.
        private readonly Dictionary<string, JObject> _serverByName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The row of the game's series. What is known already (games loaded before, its own
        /// remakes and ports, answers kept from earlier) is laid out at once; the server's list
        /// of the series and the wider search fill in as they come, each tile in its place: the
        /// server's games first, oldest first, then the ones it lacks. A game first shown as
        /// lacking turns into the server's tile if the server turns out to have it. The row
        /// keeps its room while loading, so the page below it does not jump.
        /// </summary>
        private void AddSeriesSection(Panel page, JObject data, RomMRom rom)
        {
            if (Host.Length == 0 || _plugin.Wishlist == null)
                return;
            var franchises = Strings(data, "metadatum.franchises");
            var collections = Strings(data, "metadatum.collections");
            // Its series: IGDB's collection ("Kingdom Hearts"), not a franchise it shares with
            // others ("Disney"), which would fill the row and the wishlist with other games.
            List<string> seriesList = SeriesOfData(data);
            string series = seriesList.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(series))
                return;

            int romId = rom.Id;
            int ownIgdb = rom.IgdbId ?? 0;
            string ownName = Wishlist.Normalize(rom.Name);
            string platform = Str(data, "platform_display_name") ?? Str(data, "platform_name");
            string seriesKey = Wishlist.Normalize(series);
            var seriesNames = new HashSet<string>(seriesList, StringComparer.OrdinalIgnoreCase);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var status = Text("Looking for the rest of the series...", 11, false);
            status.Opacity = 0.5;
            status.Margin = new Thickness(0, -6, 0, 8);
            var section = new StackPanel();
            section.Children.Add(SectionTitle("OTHER GAMES IN THE " + series.ToUpperInvariant() + " SERIES", 28));
            section.Children.Add(status);
            // A tile's height, held while the row is empty, so nothing below moves as it fills.
            var holder = new Grid { MinHeight = 262 };
            holder.Children.Add(SideScroll.Row(row, true));
            section.Children.Add(holder);
            page.Children.Add(section);

            // ---- The row's bookkeeping (on this thread only) --------------------------------

            var placed = new Dictionary<string, FrameworkElement>();       // by name
            var serverTiles = new List<Tuple<double, FrameworkElement>>();   // by release, oldest first
            var serverKeys = new HashSet<string>();
            var serverIgdb = new HashSet<int>();
            var wishIgdb = new Dictionary<int, string>();                    // IGDB id -> name key
            var verifiers = new Dictionary<string, Action<bool>>();          // a lacking tile's "checked" switch

            Func<JObject, bool> isOwn = o => (int?)o["id"] == romId || Wishlist.Normalize(Str(o, "name")) == ownName;

            // The server's game; in place of a lacking tile of the same game (by name, or the
            // name it was looked for under).
            Action<JObject, string> addServer = (o, lookedFor) =>
            {
                if (o == null || isOwn(o))
                    return;
                RomMRom member = o.ToObject<RomMRom>();
                if (member == null)
                    return;
                string key = Wishlist.Normalize(member.Name);
                if (serverKeys.Contains(key))
                {
                    // Already shown: only a playable copy replaces one that is not.
                    if (!Playable(member) || !(placed[key].Tag is RomMRom shownRom) || Playable(shownRom))
                        return;
                }
                _raw[member.Id] = o;
                var tile = (FrameworkElement)BuildSimilarTile(member, _inLibrary.Contains(member.Id) ? "In your library" : "On the server");
                tile.Tag = member;
                tile.VerticalAlignment = VerticalAlignment.Top;

                // Out with what it replaces: an earlier copy, or the lacking tile it turned out to be.
                foreach (string old in new[] { key, lookedFor == null ? null : Wishlist.Normalize(lookedFor) })
                {
                    if (old != null && placed.TryGetValue(old, out FrameworkElement gone))
                    {
                        row.Children.Remove(gone);
                        serverTiles.RemoveAll(t => t.Item2 == gone);
                        placed.Remove(old);
                    }
                }

                double released = Number(o, "metadatum.first_release_date") ?? Number(o, "igdb_metadata.first_release_date") ?? double.MaxValue;
                int at = serverTiles.Count(t => t.Item1 <= released);
                serverTiles.Insert(at, Tuple.Create(released, tile));
                row.Children.Insert(at, tile);
                placed[key] = tile;
                serverKeys.Add(key);
                int igdb = (int?)o["igdb_id"] ?? 0;
                if (igdb > 0)
                {
                    serverIgdb.Add(igdb);
                    // A lacking tile of the same game under another name goes too.
                    if (wishIgdb.TryGetValue(igdb, out string wishKey) && placed.TryGetValue(wishKey, out FrameworkElement twin) && twin != tile)
                    {
                        row.Children.Remove(twin);
                        placed.Remove(wishKey);
                    }
                }
            };

            // A game the server may lack: shown at the end, checked with the server unless the
            // answer is known. Returns whether it needs asking.
            Func<SeriesGame, bool> addWish = game =>
            {
                string key = Wishlist.Normalize(game.Name);
                if (key.Length == 0 || key == ownName || (game.IgdbId > 0 && (game.IgdbId == ownIgdb || serverIgdb.Contains(game.IgdbId) || wishIgdb.ContainsKey(game.IgdbId))))
                    return false;
                if (placed.ContainsKey(key))
                    return false;
                if (_serverByName.TryGetValue(key, out JObject known))
                {
                    if (known != null)
                    {
                        addServer(known, game.Name);
                        return false;
                    }
                }
                var tile = WishTile(game, series, platform, out Action<bool> verified);
                row.Children.Add(tile);
                placed[key] = tile;
                if (game.IgdbId > 0)
                    wishIgdb[game.IgdbId] = key;
                if (_serverByName.ContainsKey(key))
                {
                    verified(true);
                    return false;
                }
                verifiers[key] = verified;
                return true;
            };

            // ---- What is known now ------------------------------------------------------------

            try
            {
                foreach (JObject o in _raw.Values.ToList())
                {
                    if (SeriesOfData(o).Any(seriesNames.Contains))
                        addServer(o, null);
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Store] Could not look through the loaded games for the series: {ex.Message}");
            }

            var toCheck = new List<SeriesGame>();
            foreach (string path in new[] { "igdb_metadata.remakes", "igdb_metadata.remasters", "igdb_metadata.ports", "igdb_metadata.expanded_games" })
            {
                foreach (JToken game in data.SelectToken(path) as JArray ?? new JArray())
                {
                    string name = Str(game, "name");
                    if (string.IsNullOrEmpty(name))
                        continue;
                    var candidate = new SeriesGame { IgdbId = (int?)game["id"] ?? 0, Name = name, Cover = BigCover(Str(game, "cover_url")) };
                    if (addWish(candidate))
                        toCheck.Add(candidate);
                }
            }

            string searchKey = seriesKey + "|" + (platform ?? "");
            bool searchedBefore = _seriesSearches.TryGetValue(searchKey, out List<SeriesGame> searched);
            if (searchedBefore)
            {
                foreach (SeriesGame candidate in searched)
                {
                    if (addWish(candidate))
                        toCheck.Add(candidate);
                }
            }

            // ---- What has to be asked for, side by side ---------------------------------------

            int running = 0;
            Action started = () => running++;
            Action finished = () =>
            {
                if (--running > 0)
                    return;
                status.Visibility = Visibility.Collapsed;
                holder.MinHeight = 0;
                if (row.Children.Count == 0)
                    section.Visibility = Visibility.Collapsed;
            };
            Action<Action> onUi = act => Dispatcher.BeginInvoke(act);
            var slots = new System.Threading.SemaphoreSlim(4);

            // Is a lacking game on the server after all? Asked by name, four at a time.
            Action<SeriesGame> check = null;
            check = game =>
            {
                started();
                string key = Wishlist.Normalize(game.Name);
                Task.Run(async () =>
                {
                    JObject match = null;
                    bool answered = false;
                    await slots.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        JToken found = Get($"/api/roms?limit=10&search_term={Uri.EscapeDataString(game.Name)}");
                        answered = found != null;
                        foreach (JToken item in found?["items"] as JArray ?? new JArray())
                        {
                            int igdb = (int?)item["igdb_id"] ?? 0;
                            bool same = game.IgdbId > 0 && igdb > 0 ? igdb == game.IgdbId : Wishlist.Normalize(Str(item, "name")) == key;
                            if (same && item is JObject hit)
                            {
                                match = hit;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[Store] Could not ask the server for {game.Name}: {ex.Message}");
                    }
                    finally
                    {
                        slots.Release();
                    }
                    onUi(() =>
                    {
                        if (answered)
                            _serverByName[key] = match;
                        if (match != null)
                            addServer(match, game.Name);
                        else if (verifiers.TryGetValue(key, out Action<bool> verified))
                            verified(true);
                        finished();
                    });
                });
            };
            foreach (SeriesGame candidate in toCheck)
                check(candidate);

            // The series on the server.
            started();
            Task.Run(() =>
            {
                var members = new List<JObject>();
                try
                {
                    Action<string, IEnumerable<string>> ask = (field, values) =>
                    {
                        foreach (string value in values.Take(3))
                        {
                            // The series as its page lists it (SeriesGames).
                            members.AddRange(SeriesGames(value, 60));
                        }
                    };
                    ask(collections.Count > 0 ? "collections" : "franchises", seriesList);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Store] Could not list the {series} series: {ex.Message}");
                }
                onUi(() =>
                {
                    foreach (JObject o in members)
                        addServer(o, null);
                    finished();
                });
            });

            // The rest of the series, from IGDB through the server's metadata search (the one
            // RomM's "match" dialog uses), kept to titles carrying the series' name, so a loose
            // search does not pull in other games. Once per series and platform a session.
            if (!searchedBefore)
            {
                started();
                Task.Run(() =>
                {
                    var hits = new List<SeriesGame>();
                    try
                    {
                        JToken found = Get($"/api/search/roms?rom_id={romId}&search_by=name&search_term={Uri.EscapeDataString(series)}");
                        foreach (JToken hit in (found as JArray) ?? (found?["items"] as JArray) ?? new JArray())
                        {
                            string name = Str(hit, "name");
                            if (string.IsNullOrEmpty(name) || !Wishlist.Normalize(name).Contains(seriesKey))
                                continue;
                            string cover = Str(hit, "igdb_url_cover") ?? Str(hit, "url_cover") ?? Str(hit, "cover_url")
                                ?? Str(hit, "ss_url_cover") ?? Str(hit, "moby_url_cover");
                            hits.Add(new SeriesGame { IgdbId = (int?)hit["igdb_id"] ?? 0, Name = name, Cover = BigCover(cover) });
                        }
                    }
                    catch (Exception ex)
                    {
                        s_log.Info($"[Store] The server's metadata search gave nothing for {series}: {ex.Message}");
                    }
                    onUi(() =>
                    {
                        _seriesSearches[searchKey] = hits;
                        foreach (SeriesGame candidate in hits.Take(24))
                        {
                            if (addWish(candidate))
                                check(candidate);
                        }
                        finished();
                    });
                });
            }
        }

        // A game the server lacks: its cover (darkened), its name in a fixed two-line space so
        // the buttons of a row line up, and the wishlist button, which waits until the server
        // has been asked about the game.
        private FrameworkElement WishTile(SeriesGame game, string series, string platform, out Action<bool> verified)
        {
            var frame = new Grid { Width = 140, Height = 187, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), ClipToBounds = true };
            if (!string.IsNullOrEmpty(game.Cover))
            {
                var cover = new Image { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.Fant);
                ShowPicture(cover, game.Cover, 320);
                frame.Children.Add(cover);
                frame.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00)), IsHitTestVisible = false });
            }
            else
            {
                // No cover: the name stands in for it.
                var title = Text(game.Name, 12, false);
                title.TextAlignment = TextAlignment.Center;
                title.VerticalAlignment = VerticalAlignment.Center;
                title.Margin = new Thickness(8);
                title.Opacity = 0.7;
                frame.Children.Add(title);
            }
            frame.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x24, 0x2C)),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "NOT ON SERVER", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Opacity = 0.85 },
            });

            var label = Text(game.Name, 12, false);
            label.Margin = new Thickness(2, 6, 2, 4);
            label.Height = 34;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Opacity = 0.75;

            var words = new TextBlock { FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
            var button = new Border { Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(2), Child = words };
            bool ready = false;
            Action paint = () =>
            {
                if (!ready)
                {
                    words.Text = "Checking the server...";
                    words.Opacity = 0.6;
                    button.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                    button.Cursor = null;
                    button.ToolTip = null;
                    return;
                }
                bool wished = _plugin.Wishlist.Contains(game.IgdbId, game.Name);
                words.Opacity = 1;
                words.Text = wished ? "\u2713  On wishlist" : "+  Add to wishlist";
                button.Background = wished ? new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)) : WishBlue;
                button.Cursor = Cursors.Hand;
                button.ToolTip = wished ? "Click to take it off your wishlist" : "You'll be told when the server has it";
            };
            paint();
            verified = ok =>
            {
                ready = ok;
                paint();
            };
            button.MouseEnter += (s, e) => { if (ready) button.Opacity = 0.85; };
            button.MouseLeave += (s, e) => button.Opacity = 1;
            button.MouseLeftButtonDown += (s, e) => e.Handled = true;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (!ready)
                    return;
                if (_plugin.Wishlist.Contains(game.IgdbId, game.Name))
                    _plugin.Wishlist.Remove(game.IgdbId, game.Name);
                else
                    _plugin.Wishlist.Add(new WishlistEntry { IgdbId = game.IgdbId, Name = game.Name, Cover = game.Cover, Series = series, Platform = platform });
                paint();
            };

            var stack = new StackPanel { Width = 140 };
            stack.Children.Add(frame);
            stack.Children.Add(label);
            stack.Children.Add(button);
            return new Border { Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(4), Child = stack, ToolTip = game.Name, VerticalAlignment = VerticalAlignment.Top };
        }

        // ---- The wishlist in the bar -----------------------------------------------------------

        private TextBlock _wishText;

        private UIElement WishlistNavItem()
        {
            var item = (Border)NavItem("Wishlist", true, () => OpenWishlistMenu());
            DockPanel.SetDock(item, Dock.Right);
            _wishText = item.Child as TextBlock;
            PaintWishCount();
            _plugin.Wishlist.Changed += () => Dispatcher.BeginInvoke(new Action(PaintWishCount));
            return item;
        }

        private void PaintWishCount()
        {
            if (_wishText == null || _plugin.Wishlist == null)
                return;
            int count = _plugin.Wishlist.Count;
            int arrived = _plugin.Wishlist.All().Count(e => e.Available);
            _wishText.Text = (count > 0 ? $"Wishlist  {count}" : "Wishlist") + "  \u25BE";
            // Green once something has arrived; otherwise the theme's text colour (the nav
            // items take theirs from the theme, and clearing it left WPF's default black).
            if (arrived > 0)
                _wishText.Foreground = WishGreen;
            else
                _wishText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }

        private void OpenWishlistMenu()
        {
            HookMenuClosing();
            CloseSuggestions();
            if (_menu != null)
                _menu.IsOpen = false;
            _plugin.CheckWishlistNow();

            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
            body.Children.Add(MenuHeading("YOUR WISHLIST"));
            List<WishlistEntry> entries = _plugin.Wishlist.All();
            if (entries.Count == 0)
            {
                var none = Text("Nothing yet. Games in a series that the server doesn't have can be added from their series row on a game's page.", 13, false);
                none.TextWrapping = TextWrapping.Wrap;
                none.Opacity = 0.7;
                body.Children.Add(none);
            }
            else
            {
                var list = new StackPanel();
                foreach (WishlistEntry entry in entries)
                    list.Children.Add(WishRow(entry));
                var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 440, Content = list };
                SmoothScroll.Attach(scroller);
                body.Children.Add(scroller);
            }

            var panel = new Border
            {
                Width = 440,
                Background = ThemeColors.Menu(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1, 0, 1, 1),
                Child = body,
            };
            MiddleClickScroll.Install(panel);

            // Under the bar, at its right end, beneath the Wishlist item.
            _menu = new Popup
            {
                PlacementTarget = _navBar,
                Placement = PlacementMode.Bottom,
                HorizontalOffset = Math.Max(0, _navBar.ActualWidth - 440),
                StaysOpen = true,
                AllowsTransparency = true,
                Child = panel,
            };
            _menu.IsOpen = true;
            WatchMenuLeave();
        }

        private UIElement WishRow(WishlistEntry entry)
        {
            var picture = new Grid { Width = 46, Height = 61, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), ClipToBounds = true };
            if (!string.IsNullOrEmpty(entry.Cover))
            {
                var cover = new Image { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.Fant);
                ShowPicture(cover, entry.Cover, 120);
                picture.Children.Add(cover);
            }

            var words = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = Text(entry.Name, 13, true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(name);
            string where = string.Join("  \u00B7  ", new[] { entry.Series, entry.Platform }.Where(w => !string.IsNullOrEmpty(w)));
            if (where.Length > 0)
            {
                var whereText = Text(where, 11, false);
                whereText.Opacity = 0.6;
                words.Children.Add(whereText);
            }
            var status = new TextBlock
            {
                Text = entry.Available ? "Now on the server \u203A" : "Not on the server yet",
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = entry.Available ? WishGreen : Brushes.White,
                Opacity = entry.Available ? 1 : 0.5,
            };
            words.Children.Add(status);

            var remove = new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = Brushes.White,
                Opacity = 0.5,
                Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "Remove from wishlist",
            };
            remove.MouseEnter += (s, e) => remove.Opacity = 1;
            remove.MouseLeave += (s, e) => remove.Opacity = 0.5;

            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4), Background = Brushes.Transparent };
            DockPanel.SetDock(picture, Dock.Left);
            DockPanel.SetDock(remove, Dock.Right);
            line.Children.Add(picture);
            line.Children.Add(remove);
            line.Children.Add(words);

            var row = new Border { Padding = new Thickness(4), Child = line, Background = Brushes.Transparent };
            remove.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _plugin.Wishlist.Remove(entry.IgdbId, entry.Name);
                row.Visibility = Visibility.Collapsed;
            };
            if (entry.Available)
            {
                row.Cursor = Cursors.Hand;
                row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                int romId = entry.AvailableRomId;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    CloseNavMenu();
                    _plugin.CloudStatus.OpenStoreGame(romId);
                };
            }
            return row;
        }
    }
}
