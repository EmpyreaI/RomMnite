using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RomM.LibraryHome
{
    /// <summary>
    /// The library's filter button and its panel, after Steam's "Library Filters": headed groups
    /// of check boxes in columns (play state, features, platform, library, genre, completion),
    /// a tag box, and at the bottom Clear, Playnite's full filters, and Save as preset. Every
    /// tick applies at once, through Playnite's own filtering (the main view's filter
    /// settings), so the game list, its search and saved presets all keep working.
    /// A theme places it with &lt;ContentControl x:Name="RomM_LibraryFilters" /&gt;, in place of
    /// the top panel's filter toggle.
    /// </summary>
    public class LibraryFiltersControl : PluginUserControl
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Brush Heading = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush PanelBack = new SolidColorBrush(Color.FromArgb(0xF5, 0x2A, 0x2E, 0x38));
        private static readonly Brush BoxBack = new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00));
        private static readonly Brush BoxEdge = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Tick = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));

        private readonly IPlayniteAPI _api;
        private readonly RomM _plugin;
        private bool _dirty;   // ticks not applied yet
        private readonly Border _button;
        private readonly Border _dot;
        private Popup _popup;
        private FilterPresetSettings _settings;
        private DateTime _closedAt = DateTime.MinValue;
        private readonly DispatcherTimer _apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };

        public LibraryFiltersControl(IPlayniteAPI api, RomM plugin = null)
        {
            _api = api;
            _plugin = plugin;
            Focusable = false;

            var glyph = new TextBlock
            {
                Text = "\uE71C",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            // A blue dot while any filter is on, as the theme's own toggle has. Pointed at, it
            // becomes an x that clears the filters (the button around it is not pressed).
            var spot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Heading, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cross = new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var dotFace = new Grid();
            dotFace.Children.Add(spot);
            dotFace.Children.Add(cross);
            _dot = new Border
            {
                Width = 15,
                Height = 15,
                CornerRadius = new CornerRadius(7.5),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -1, -1, 0),
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand,
                ToolTip = "Clear filters",
                Child = dotFace,
            };
            _dot.MouseEnter += (s, e) =>
            {
                _dot.Background = Heading;
                spot.Visibility = Visibility.Collapsed;
                cross.Visibility = Visibility.Visible;
            };
            _dot.MouseLeave += (s, e) =>
            {
                _dot.Background = Brushes.Transparent;
                spot.Visibility = Visibility.Visible;
                cross.Visibility = Visibility.Collapsed;
            };
            _dot.MouseLeftButtonDown += (s, e) => e.Handled = true;
            _dot.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                ClearFilters();
            };
            var face = new Grid();
            face.Children.Add(glyph);
            face.Children.Add(_dot);
            _button = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "Library filters",
                Child = face,
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_button, true);
            _button.MouseEnter += (s, e) => _button.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            _button.MouseLeave += (s, e) => { if (_popup == null || !_popup.IsOpen) _button.Background = Brushes.Transparent; };
            _button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                // A click on the button while the panel is open first closes it (it closes on any
                // click outside it); that same click must not open it again.
                if (_popup != null && _popup.IsOpen)
                    _popup.IsOpen = false;
                else if (DateTime.UtcNow - _closedAt > TimeSpan.FromMilliseconds(250))
                    Open();
            };
            Content = _button;

            // Ticks come in bursts (a few clicks in a row); applied once they settle.
            _apply.Tick += (s, e) =>
            {
                _apply.Stop();
                Apply();
            };
            Loaded += (s, e) => PaintDot();
        }

        // ---- Filtering ----------------------------------------------------------------------

        private FilterPresetSettings Current()
        {
            try
            {
                return _api.MainView.GetCurrentFilterSettings() ?? new FilterPresetSettings();
            }
            catch (Exception ex)
            {
                s_log.Debug($"[Filters] Could not read the current filters: {ex.Message}");
                return new FilterPresetSettings();
            }
        }

        // Applies the ticks, if any changed. Playnite selects the list's first game when the
        // filters change; the game selected before is selected again (when it still passes),
        // and the moment is held so it is not taken as the user picking a game (which would
        // close the library's Home page).
        private void Apply()
        {
            if (_settings == null || !_dirty)
                return;
            _dirty = false;
            List<Guid> selected;
            try
            {
                selected = _api.MainView.SelectedGames?.Select(g => g.Id).ToList() ?? new List<Guid>();
            }
            catch
            {
                selected = new List<Guid>();
            }
            _plugin?.HoldSelection(TimeSpan.FromMilliseconds(1500));
            try
            {
                _api.MainView.ApplyFilterPreset(new FilterPreset { Name = "Library filters", Settings = _settings });
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Filters] Could not apply the filters: {ex.Message}");
            }
            PaintDot();

            if (selected.Count == 0)
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var still = new HashSet<Guid>(_api.MainView.FilteredGames?.Select(g => g.Id) ?? new Guid[0]);
                    var keep = selected.Where(still.Contains).ToList();
                    if (keep.Count == 0)
                        return;
                    _plugin?.HoldSelection(TimeSpan.FromMilliseconds(800));
                    if (keep.Count == 1)
                        _api.MainView.SelectGame(keep[0]);
                    else
                        _api.MainView.SelectGames(keep);
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Filters] Could not keep the selection: {ex.Message}");
                }
            }), DispatcherPriority.Background);
        }

        // Every filter off, at once; the panel, if open, redrawn with nothing ticked.
        private void ClearFilters()
        {
            bool open = _popup != null && _popup.IsOpen;
            _apply.Stop();
            _settings = new FilterPresetSettings();
            _dirty = true;
            Apply();
            if (open)
                Open();
        }

        private void ApplySoon()
        {
            _dirty = true;
            _apply.Stop();
            _apply.Start();
        }

        private static bool Any(IdItemFilterItemProperties item) => item != null && ((item.Ids?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(item.Text));
        private static bool Any(EnumFilterItemProperties item) => item != null && (item.Values?.Count ?? 0) > 0;

        private static bool Active(FilterPresetSettings f)
        {
            return f != null && (f.IsInstalled || f.IsUnInstalled || f.Favorite || f.Hidden
                || !string.IsNullOrWhiteSpace(f.Name) || !string.IsNullOrWhiteSpace(f.Version)
                || Any(f.Genre) || Any(f.Platform) || Any(f.Library) || Any(f.Feature) || Any(f.CompletionStatuses)
                || Any(f.Tag) || Any(f.Series) || Any(f.Category) || Any(f.Developer) || Any(f.Publisher)
                || Any(f.Source) || Any(f.Region) || Any(f.AgeRating) || Any(f.PlayTime) || Any(f.LastActivity)
                || Any(f.RecentActivity) || Any(f.Added) || Any(f.Modified) || Any(f.InstallSize)
                || Any(f.UserScore) || Any(f.CommunityScore) || Any(f.CriticScore)
                || (f.ReleaseYear?.Values?.Count ?? 0) > 0);
        }

        private void PaintDot()
        {
            _dot.Visibility = Active(Current()) ? Visibility.Visible : Visibility.Collapsed;
        }

        // An id list filter (genre, platform...) with one id ticked or unticked.
        private static IdItemFilterItemProperties Toggle(IdItemFilterItemProperties item, Guid id, bool on)
        {
            var ids = new List<Guid>(item?.Ids ?? new List<Guid>());
            ids.Remove(id);
            if (on)
                ids.Add(id);
            return ids.Count == 0 && string.IsNullOrWhiteSpace(item?.Text) ? null : new IdItemFilterItemProperties { Ids = ids, Text = item?.Text };
        }

        // ---- The panel ------------------------------------------------------------------------

        private void Open()
        {
            _settings = Current();
            List<Game> games = _api.Database.Games.Where(g => !g.Hidden || _settings.Hidden).ToList();

            var columns = new Grid();
            for (int i = 0; i < 3; i++)
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Play state.
            var state = Column(columns, 0);
            state.Children.Add(Section("PLAY STATE"));
            state.Children.Add(Check("Ready to play", _settings.IsInstalled, on => _settings.IsInstalled = on));
            state.Children.Add(Check("Not installed", _settings.IsUnInstalled, on => _settings.IsUnInstalled = on));
            int notPlayed = (int)PlaytimeCategory.NotPlayed;
            var playedValues = Enum.GetValues(typeof(PlaytimeCategory)).Cast<int>().Where(v => v != notPlayed).ToList();
            List<int> playtime = _settings.PlayTime?.Values ?? new List<int>();
            bool playedOn = playedValues.All(playtime.Contains);
            bool unplayedOn = playtime.Contains(notPlayed);
            Action<bool, bool> setPlayed = (wantPlayed, wantUnplayed) =>
            {
                var values = new List<int>();
                if (wantPlayed)
                    values.AddRange(playedValues);
                if (wantUnplayed)
                    values.Add(notPlayed);
                _settings.PlayTime = values.Count == 0 ? null : new EnumFilterItemProperties { Values = values };
            };
            CheckBox played = null;
            CheckBox unplayed = null;
            played = Check("Played", playedOn, on => setPlayed(on, unplayed?.IsChecked == true));
            unplayed = Check("Unplayed", unplayedOn, on => setPlayed(played?.IsChecked == true, on));
            state.Children.Add(played);
            state.Children.Add(unplayed);
            state.Children.Add(Check("Favorites", _settings.Favorite, on => _settings.Favorite = on));
            state.Children.Add(Check("Hidden", _settings.Hidden, on => _settings.Hidden = on));

            // Features (Steam's "Players": single player, multiplayer, co-op...).
            AddIdSection(state, "FEATURES", games.SelectMany(g => g.FeatureIds ?? new List<Guid>()),
                id => _api.Database.Features.Get(id)?.Name, () => _settings.Feature, v => _settings.Feature = v);

            // Platform and library.
            var middle = Column(columns, 1);
            AddIdSection(middle, "PLATFORM", games.SelectMany(g => g.PlatformIds ?? new List<Guid>()),
                id => _api.Database.Platforms.Get(id)?.Name, () => _settings.Platform, v => _settings.Platform = v);
            AddIdSection(middle, "LIBRARY", games.Select(g => g.PluginId), LibraryName, () => _settings.Library, v => _settings.Library = v);

            // Genre and completion.
            var right = Column(columns, 2);
            AddIdSection(right, "GENRE", games.SelectMany(g => g.GenreIds ?? new List<Guid>()),
                id => _api.Database.Genres.Get(id)?.Name, () => _settings.Genre, v => _settings.Genre = v);
            AddIdSection(right, "COMPLETION", games.Select(g => g.CompletionStatusId).Where(id => id != Guid.Empty),
                id => _api.Database.CompletionStatuses.Get(id)?.Name, () => _settings.CompletionStatuses, v => _settings.CompletionStatuses = v);

            // Tags and series, by text (as Steam's "store tags" box).
            var words = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            words.ColumnDefinitions.Add(new ColumnDefinition());
            words.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            words.ColumnDefinitions.Add(new ColumnDefinition());
            var tags = TextFilter("TAGS", "enter a tag", _settings.Tag?.Text, text =>
                _settings.Tag = string.IsNullOrWhiteSpace(text) && (_settings.Tag?.Ids?.Count ?? 0) == 0 ? null
                    : new IdItemFilterItemProperties { Ids = _settings.Tag?.Ids ?? new List<Guid>(), Text = string.IsNullOrWhiteSpace(text) ? null : text });
            var series = TextFilter("SERIES", "enter a series", _settings.Series?.Text, text =>
                _settings.Series = string.IsNullOrWhiteSpace(text) && (_settings.Series?.Ids?.Count ?? 0) == 0 ? null
                    : new IdItemFilterItemProperties { Ids = _settings.Series?.Ids ?? new List<Guid>(), Text = string.IsNullOrWhiteSpace(text) ? null : text });
            Grid.SetColumn(series, 2);
            words.Children.Add(tags);
            words.Children.Add(series);

            // Clear, Playnite's full filters, Save as preset.
            var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            var clear = FooterButton("Clear filters", false, ClearFilters);
            var more = FooterButton("All filters\u2026", false, OpenPlayniteFilters);
            var save = FooterButton("Save as preset", true, SavePreset);
            DockPanel.SetDock(clear, Dock.Left);
            DockPanel.SetDock(more, Dock.Left);
            DockPanel.SetDock(save, Dock.Right);
            footer.Children.Add(clear);
            footer.Children.Add(more);
            footer.Children.Add(save);

            var title = new TextBlock { Text = "Library Filters", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) };
            var body = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            body.Children.Add(title);
            body.Children.Add(columns);
            body.Children.Add(words);
            body.Children.Add(footer);

            var panel = new Border
            {
                Width = 640,
                Background = PanelBack,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 700, Content = body },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.6 },
            };

            bool wasOpen = _popup != null && _popup.IsOpen;
            if (_popup == null)
            {
                _popup = new Popup
                {
                    PlacementTarget = _button,
                    Placement = PlacementMode.Right,
                    HorizontalOffset = 8,
                    AllowsTransparency = true,
                    StaysOpen = false,
                    PopupAnimation = PopupAnimation.Fade,
                };
                _popup.Closed += (s, e) =>
                {
                    _closedAt = DateTime.UtcNow;
                    _button.Background = Brushes.Transparent;
                    _apply.Stop();
                    Apply();
                };
            }
            _popup.Child = panel;
            if (!wasOpen)
                _popup.IsOpen = true;
            _button.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        }

        private static StackPanel Column(Grid columns, int index)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, index < 2 ? 18 : 0, 0) };
            Grid.SetColumn(column, index);
            columns.Children.Add(column);
            return column;
        }

        private static TextBlock Section(string title)
        {
            return new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Heading,
                Margin = new Thickness(0, 10, 0, 6),
            };
        }

        // A headed list of check boxes, one per value the library's games carry, most used
        // first then by name; long lists scroll inside their section.
        private void AddIdSection(Panel column, string title, IEnumerable<Guid> used, Func<Guid, string> nameOf,
            Func<IdItemFilterItemProperties> get, Action<IdItemFilterItemProperties> set)
        {
            var counts = used.Where(id => id != Guid.Empty || title == "LIBRARY")
                .GroupBy(id => id)
                .Select(g => new { Id = g.Key, Name = nameOf(g.Key), Count = g.Count() })
                .Where(x => !string.IsNullOrEmpty(x.Name))
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            // Ticked ones the library no longer has are still shown, so they can be unticked.
            foreach (Guid id in get()?.Ids ?? new List<Guid>())
            {
                if (!counts.Any(c => c.Id == id))
                    counts.Add(new { Id = id, Name = nameOf(id) ?? "(removed)", Count = 0 });
            }
            if (counts.Count == 0)
                return;

            column.Children.Add(Section(title));
            var list = new StackPanel();
            foreach (var entry in counts)
            {
                Guid id = entry.Id;
                bool on = get()?.Ids?.Contains(id) == true;
                list.Children.Add(Check(entry.Name, on, ticked => set(Toggle(get(), id, ticked)), entry.Count));
            }
            column.Children.Add(counts.Count > 9
                ? (UIElement)new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 220, Content = list }
                : list);
        }

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
            return "Other";
        }

        // A Steam-style check box: a small square, then the words (and a dim count).
        private CheckBox Check(string label, bool on, Action<bool> changed, int? count = null)
        {
            var words = new TextBlock { Text = label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            words.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(words);
            if (count.HasValue && count.Value > 0)
            {
                var number = new TextBlock { Text = $"  {count.Value}", FontSize = 11, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center };
                number.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                line.Children.Add(number);
            }

            var box = new CheckBox { IsChecked = on, Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 2), Template = SquareCheck(), Content = line, ToolTip = label };
            box.Checked += (s, e) => { changed(true); ApplySoon(); };
            box.Unchecked += (s, e) => { changed(false); ApplySoon(); };
            return box;
        }

        private static ControlTemplate s_squareCheck;

        // A plain square box (blue tick when on), the words beside it, a light row when pointed at.
        private static ControlTemplate SquareCheck()
        {
            if (s_squareCheck != null)
                return s_squareCheck;

            var row = new FrameworkElementFactory(typeof(Border), "Row");
            row.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            row.SetValue(Border.PaddingProperty, new Thickness(2, 2, 6, 2));
            row.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            var line = new FrameworkElementFactory(typeof(StackPanel));
            line.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var square = new FrameworkElementFactory(typeof(Border), "Square");
            square.SetValue(FrameworkElement.WidthProperty, 16.0);
            square.SetValue(FrameworkElement.HeightProperty, 16.0);
            square.SetValue(Border.BackgroundProperty, BoxBack);
            square.SetValue(Border.BorderBrushProperty, BoxEdge);
            square.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            square.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 9, 0));
            square.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var mark = new FrameworkElementFactory(typeof(TextBlock), "Mark");
            mark.SetValue(TextBlock.TextProperty, "\uE73E");
            mark.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
            mark.SetValue(TextBlock.FontSizeProperty, 11.0);
            mark.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            square.AppendChild(mark);

            var words = new FrameworkElementFactory(typeof(ContentPresenter));
            words.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            line.AppendChild(square);
            line.AppendChild(words);
            row.AppendChild(line);

            var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = row };
            var ticked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            ticked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Mark"));
            ticked.Setters.Add(new Setter(Border.BackgroundProperty, Tick, "Square"));
            ticked.Setters.Add(new Setter(Border.BorderBrushProperty, Tick, "Square"));
            template.Triggers.Add(ticked);
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), "Row"));
            template.Triggers.Add(over);
            s_squareCheck = template;
            return template;
        }

        private UIElement TextFilter(string title, string hint, string value, Action<string> changed)
        {
            var stack = new StackPanel();
            stack.Children.Add(Section(title));
            var box = new TextBox
            {
                Text = value ?? "",
                Height = 28,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = BoxBack,
                BorderBrush = BoxEdge,
                BorderThickness = new Thickness(1),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
            };
            var hintText = new TextBlock { Text = hint, FontSize = 12, Opacity = 0.45, Foreground = Brushes.White, IsHitTestVisible = false, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            hintText.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (s, e) =>
            {
                hintText.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                changed(box.Text.Trim());
                ApplySoon();
            };
            var holder = new Grid();
            holder.Children.Add(box);
            holder.Children.Add(hintText);
            stack.Children.Add(holder);
            return stack;
        }

        private static Border FooterButton(string label, bool primary, Action click)
        {
            var words = new TextBlock { Text = label, FontSize = 13, Foreground = Brushes.White, Opacity = primary ? 1 : 0.85 };
            Brush normal = primary ? new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            Brush lit = primary ? new SolidColorBrush(Color.FromRgb(0x4D, 0xB5, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            var button = new Border { Background = normal, CornerRadius = new CornerRadius(2), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, primary ? 0 : 8, 0), Cursor = Cursors.Hand, Child = words };
            button.MouseEnter += (s, e) => button.Background = lit;
            button.MouseLeave += (s, e) => button.Background = normal;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                click();
            };
            return button;
        }

        // Saves the filters as a Playnite filter preset (Steam saves them as a dynamic
        // collection); presets show in Playnite's preset list and can be applied from there.
        private void SavePreset()
        {
            if (_settings == null)
                return;
            if (_popup != null)
                _popup.IsOpen = false;
            var answer = _api.Dialogs.SelectString("Name the filter preset:", "Save filters", "");
            if (answer == null || !answer.Result || string.IsNullOrWhiteSpace(answer.SelectedString))
                return;
            try
            {
                var preset = new FilterPreset { Name = answer.SelectedString.Trim(), Settings = _settings };
                _api.Database.FilterPresets.Add(preset);
                _api.MainView.ApplyFilterPreset(preset.Id);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Filters] Could not save the preset: {ex.Message}");
            }
        }

        // Playnite's own filter panel, with every field: its toggle stays in the theme (hidden),
        // so it is found beside this button and switched on.
        private void OpenPlayniteFilters()
        {
            if (_popup != null)
                _popup.IsOpen = false;
            DependencyObject node = this;
            for (int depth = 0; node != null && depth < 12; depth++)
            {
                node = VisualTreeHelper.GetParent(node);
                if (node is FrameworkElement element)
                {
                    CheckBox toggle = Find(element, "PART_ToggleFilter");
                    if (toggle != null)
                    {
                        toggle.IsChecked = true;
                        return;
                    }
                }
            }
            s_log.Info("[Filters] Playnite's filter toggle was not found next to the filter button.");
        }

        private static CheckBox Find(DependencyObject parent, string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is CheckBox box && box.Name == name)
                    return box;
                CheckBox deeper = Find(child, name);
                if (deeper != null)
                    return deeper;
            }
            return null;
        }
    }
}
