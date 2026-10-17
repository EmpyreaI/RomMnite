using Newtonsoft.Json.Linq;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.Store
{
    /// <summary>
    /// One game on several platforms. The Store lists it once, as the version on the best
    /// platform here; the other platforms' versions are on its page, as Steam lists a game's
    /// editions.
    ///
    /// The same game: the same IGDB or MobyGames id (both name a game, not a release, unlike
    /// ScreenScraper's, which RomM's own "siblings" use for the same platform's regions and
    /// revisions); or, where one has neither id, the same name. Two games with different IGDB
    /// ids are never one, however alike their names (Doom of 1993 and of 2016).
    ///
    /// The best version: one whose platform is set up to play here first; then the platform's
    /// standing (Games.PlatformRanks: later and stronger home consoles over handhelds
    /// of their time, PlayStation 2 over PSP, and the original Xbox below PlayStation 2 and
    /// GameCube, which emulate better); then the larger file (the fuller release).
    /// </summary>
    public partial class StoreView
    {

        // Other platforms' versions of a listed game (its id), as the listings found them.
        private readonly Dictionary<int, List<HomeItem>> _otherVersions = new Dictionary<int, List<HomeItem>>();

        private int VersionScore(HomeItem item)
        {
            RomMRom rom = item.Rom;
            int rank = global::RomM.Games.PlatformRanks.OfSlug(rom.PlatformSlug);
            return (Playable(rom) ? 1000 : 0) + rank;
        }

        private static int IdOf(JObject raw, string field)
        {
            JToken value = raw?[field];
            if (value == null || value.Type == JTokenType.Null)
                return 0;
            return int.TryParse(value.ToString(), out int id) ? id : 0;
        }

        private static string VersionName(RomMRom rom) => Wishlist.Normalize(rom.Name ?? rom.FileNameNoExt ?? "");

        /// <summary>
        /// The items in groups of one game each, in the order the items came (a group where its
        /// first item was), each group's best version first.
        /// </summary>
        private List<List<HomeItem>> GroupVersions(List<HomeItem> items)
        {
            int n = items.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            Func<int, int> find = null;
            find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
            Action<int, int> join = (a, b) =>
            {
                int ra = find(a), rb = find(b);
                if (ra != rb)
                    parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            };

            var byIgdb = new Dictionary<int, int>();
            var byMoby = new Dictionary<int, int>();
            var byNameAny = new Dictionary<string, int>();
            var byNameBare = new Dictionary<string, int>();   // items with neither id
            var igdbOf = new int[n];
            for (int i = 0; i < n; i++)
            {
                RomMRom rom = items[i].Rom;
                int igdb = rom.IgdbId ?? 0;
                int moby = IdOf(items[i].Raw, "moby_id");
                igdbOf[i] = igdb;
                string name = VersionName(rom);
                if (igdb > 0)
                {
                    if (byIgdb.TryGetValue(igdb, out int other))
                        join(i, other);
                    else
                        byIgdb[igdb] = i;
                }
                if (moby > 0)
                {
                    // Not across two different IGDB games.
                    if (byMoby.TryGetValue(moby, out int other) && (igdb == 0 || igdbOf[other] == 0 || igdbOf[other] == igdb))
                        join(i, other);
                    else if (!byMoby.ContainsKey(moby))
                        byMoby[moby] = i;
                }
                if (name.Length == 0)
                    continue;
                bool bare = igdb == 0 && moby == 0;
                if (bare && byNameAny.TryGetValue(name, out int named))
                    join(i, named);
                else if (!bare && byNameBare.TryGetValue(name, out int unnamed))
                    join(i, unnamed);
                if (!byNameAny.ContainsKey(name))
                    byNameAny[name] = i;
                if (bare && !byNameBare.ContainsKey(name))
                    byNameBare[name] = i;
            }

            var groups = new Dictionary<int, List<int>>();
            var order = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int root = find(i);
                if (!groups.TryGetValue(root, out List<int> members))
                {
                    groups[root] = members = new List<int>();
                    order.Add(root);
                }
                members.Add(i);
            }
            return order.Select(root => groups[root]
                    .Select(i => items[i])
                    .OrderByDescending(VersionScore)
                    .ThenByDescending(i => i.Rom.FileSizeBytes)
                    .ToList())
                .ToList();
        }

        /// <summary>
        /// For a listing: each game once, as its best version, the others kept for its page.
        /// <paramref name="ownedDrops"/>: a game the library has on any platform is left out (the
        /// Store offers what is not owned); else the owned version stands for the game.
        /// </summary>
        private List<HomeItem> OneVersionEach(IEnumerable<HomeItem> items, bool ownedDrops)
        {
            var listed = new List<HomeItem>();
            foreach (List<HomeItem> group in GroupVersions(items.Where(i => i?.Rom != null).ToList()))
            {
                HomeItem owned = group.FirstOrDefault(i => _inLibrary.Contains(i.Rom.Id));
                if (owned != null && ownedDrops)
                    continue;
                HomeItem shown = owned ?? group[0];
                listed.Add(shown);
                var others = group.Where(i => !ReferenceEquals(i, shown) && i.Rom.PlatformId != shown.Rom.PlatformId).ToList();
                if (others.Count > 0)
                    _otherVersions[shown.Rom.Id] = others;
            }
            return listed;
        }

        // ---- On a game's page --------------------------------------------------------------

        /// <summary>
        /// "&lt;GAME&gt; ON OTHER PLATFORMS": the game's other platforms' versions on the server, as
        /// Steam lists editions: the name and platform, size and whether it is ready to play here,
        /// and Add to Library (or In Library); the row opens that version's page. Filled once the
        /// server has been searched by the game's name; nothing when it has no other version.
        /// </summary>
        private void AddPlatformVersions(Panel column, JObject data, RomMRom rom)
        {
            var host = new StackPanel { Visibility = Visibility.Collapsed };
            column.Children.Add(host);
            var self = new HomeItem { Rom = rom, Raw = data };
            List<HomeItem> known = _otherVersions.TryGetValue(rom.Id, out List<HomeItem> kept) ? kept : new List<HomeItem>();
            string name = rom.Name ?? rom.FileNameNoExt ?? "";
            if (string.IsNullOrWhiteSpace(name) || Host.Length == 0)
                return;
            Task.Run(() =>
            {
                var candidates = new List<HomeItem> { self };
                candidates.AddRange(known);
                try
                {
                    foreach (JToken found in Get($"/api/roms?limit=60&search_term={Uri.EscapeDataString(name)}")?["items"] as JArray ?? new JArray())
                    {
                        HomeItem item = ItemFrom(found as JObject, null, null);
                        if (item != null && candidates.All(c => c.Rom.Id != item.Rom.Id))
                            candidates.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    s_log.Info($"[Store] Could not look for {name} on other platforms: {ex.Message}");
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_currentRom == null || _currentRom.Id != rom.Id)
                        return;
                    List<HomeItem> group = GroupVersions(candidates).FirstOrDefault(g => g.Any(i => i.Rom.Id == rom.Id)) ?? new List<HomeItem>();
                    // One row per platform (that platform's best copy), this one's platform not again.
                    var rows = group.Where(i => i.Rom.PlatformId != rom.PlatformId)
                        .GroupBy(i => i.Rom.PlatformId).Select(g => g.First())
                        .ToList();
                    if (rows.Count == 0)
                        return;
                    foreach (HomeItem item in rows)
                        _raw[item.Rom.Id] = item.Raw;
                    host.Children.Add(SectionTitle((name + " on other platforms").ToUpperInvariant(), 28));
                    host.Children.Add(VersionRow(self, true));
                    foreach (HomeItem item in rows)
                        host.Children.Add(VersionRow(item, false));
                    host.Visibility = Visibility.Visible;
                }));
            });
        }

        private UIElement VersionRow(HomeItem item, bool current)
        {
            RomMRom rom = item.Rom;
            string platform = !string.IsNullOrEmpty(rom.PlatformDisplayName) ? rom.PlatformDisplayName : rom.PlatformName ?? "";
            bool owned = _inLibrary.Contains(rom.Id);
            bool ready = Playable(rom);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(current ? (byte)0x38 : (byte)0x26, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(18, 14, 14, 14),
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = current ? Cursors.Arrow : Cursors.Hand,
            };
            var row = new DockPanel();
            card.Child = row;

            // The button, as Steam's Add to Cart.
            var buttonText = Text(owned ? "In Library" : "Add to Library", 13, false);
            buttonText.Foreground = Brushes.White;
            var button = new Border
            {
                Background = owned ? (Brush)new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) : new LinearGradientBrush(Color.FromRgb(0x75, 0xB0, 0x22), Color.FromRgb(0x58, 0x8A, 0x1B), 90),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(16, 8, 16, 8),
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = owned ? Cursors.Arrow : Cursors.Hand,
                Child = buttonText,
            };
            if (!owned)
            {
                button.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    AddVersionToLibrary(rom, buttonText);
                };
            }
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);

            var platformTag = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = Text(platform, 12, true),
            };
            DockPanel.SetDock(platformTag, Dock.Right);
            row.Children.Add(platformTag);

            var words = new StackPanel();
            var title = Text($"{rom.Name ?? rom.FileNameNoExt} — {platform}", 17, false);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.TextWrapping = TextWrapping.NoWrap;
            words.Children.Add(title);
            var details = new List<string>();
            if (current)
                details.Add("The version you're viewing");
            details.Add(ready ? "Ready to play here" : "Its emulator isn't set up yet");
            if (rom.FileSizeBytes > 0)
                details.Add(FormatSize(rom.FileSizeBytes));
            var line = Text(string.Join("   ·   ", details), 12, false);
            line.Foreground = current ? new SolidColorBrush(Color.FromRgb(0x67, 0xC1, 0xF5)) : new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
            line.Margin = new Thickness(0, 4, 0, 0);
            words.Children.Add(line);
            row.Children.Add(words);

            if (!current)
            {
                card.MouseEnter += (s, e) => card.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                card.MouseLeave += (s, e) => card.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                card.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    ShowGame(rom);
                };
            }
            return card;
        }

        // Another platform's version added from the page it is listed on; the page stays.
        private void AddVersionToLibrary(RomMRom rom, TextBlock label)
        {
            if (_adding)
                return;
            _adding = true;
            label.Text = "Adding...";
            RomMRom page = _currentRom;
            Task.Run(() =>
            {
                string problem = _plugin.AddToLibrary(rom.Id);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _adding = false;
                    RefreshLibraryIds();
                    if (problem != null)
                    {
                        label.Text = "Add to Library";
                        _plugin.Playnite.Dialogs.ShowErrorMessage($"{rom.Name} could not be added to your library.\n\n{problem}", "RomMnite Store");
                        return;
                    }
                    if (page != null && _currentRom != null && _currentRom.Id == page.Id && _gamePage.Visibility == Visibility.Visible)
                        RedrawGame(page);
                }));
            });
        }
    }
}
