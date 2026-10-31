using Playnite.SDK;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RomM.Settings
{
    /// <summary>
    /// RomMnite's own page of settings, in tabs: Games (videos, updates, emulators), Achievements
    /// (what game details show, RetroAchievements sign-in), In-game overlay, and Streaming
    /// (Sunshine). Shown as the "RomMnite" category of Playnite's Settings window
    /// (PlayniteSettingsPage), else in a window of its own from the extension's settings. Its
    /// fields are bound to the extension's settings (SettingsViewModel.Instance); whoever shows it
    /// saves them.
    /// </summary>
    internal sealed class RomMnitePage : UserControl
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly global::RomM.RomM _plugin;
        private readonly SettingsViewModel _model;
        private readonly TextBlock _raStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        private readonly StackPanel _raActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        private readonly PasswordBox _sunshinePassword = new PasswordBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBlock _sunshineResult = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private bool _passwordShown;

        public RomMnitePage(global::RomM.RomM plugin, SettingsViewModel model)
        {
            _plugin = plugin;
            _model = model;
            DataContext = model;

            // ---- Games ---------------------------------------------------------------------------
            StackPanel games = Tab();
            games.Children.Add(Heading("Videos", first: true));
            games.Children.Add(Check("Play game videos automatically", nameof(SettingsViewModel.AutoPlayVideos)));

            games.Children.Add(Heading("Updates"));
            games.Children.Add(Check("Download game updates automatically", nameof(SettingsViewModel.AutoUpdateGames)));
            games.Children.Add(Note("Otherwise, the Play button turns into Update."));


            // ---- Graphics ----------------------------------------------------------------------
            StackPanel graphics = Tab();
            graphics.Children.Add(Heading("Emulators", first: true));
            graphics.Children.Add(Choice("Resolution", nameof(SettingsViewModel.GraphicsResolution),
                "Original", "Original", "2x", "2x", "3x", "3x", "4x", "4x", "Match my screen", "Screen"));
            graphics.Children.Add(Note("Higher is sharper, but harder on your graphics card."));
            graphics.Children.Add(Check("Widescreen", nameof(SettingsViewModel.GraphicsWidescreen)));
            graphics.Children.Add(Note("Shows more of the scene in 4:3 games. Some games glitch."));
            graphics.Children.Add(Check("Stretch to fill the screen", nameof(SettingsViewModel.GraphicsStretch)));
            graphics.Children.Add(Check("VSync", nameof(SettingsViewModel.GraphicsVSync)));
            graphics.Children.Add(Note("Stops tearing. Off lowers input lag."));
            graphics.Children.Add(Note("Applied to every emulator as each game starts."));

            // ---- Achievements ----------------------------------------------------------------------
            StackPanel achievements = Tab();
            achievements.Children.Add(Heading("Game details", first: true));
            achievements.Children.Add(Check("Show unlocked achievements", nameof(SettingsViewModel.ShowUnlockedAchievements)));
            achievements.Children.Add(Check("Show locked achievements", nameof(SettingsViewModel.ShowLockedAchievements)));

            achievements.Children.Add(Heading("RetroAchievements"));
            achievements.Children.Add(_raStatus);
            achievements.Children.Add(_raActions);
            achievements.Children.Add(Note("Lets your emulators earn achievements on your account."));

            // ---- In-game overlay ------------------------------------------------------------------
            StackPanel overlay = Tab();
            overlay.Children.Add(Heading("Opening it", first: true));
            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var buttonLabel = Words("Open the overlay with");
            buttonLabel.VerticalAlignment = VerticalAlignment.Center;
            buttonLabel.Margin = new Thickness(0, 0, 12, 0);
            buttonRow.Children.Add(buttonLabel);
            var button = new ComboBox { Width = 340, SelectedValuePath = "Tag" };
            // Each with the controller's own buttons (ControllerGlyphs).
            Func<string, UIElement> withGlyphs = text =>
            {
                var block = new TextBlock { FontSize = 13 };
                global::RomM.Overlay.ControllerGlyphs.Fill(block, text);
                return block;
            };
            button.Items.Add(new ComboBoxItem { Content = withGlyphs("{Guide}  Home button"), Tag = "Guide" });
            button.Items.Add(new ComboBoxItem { Content = withGlyphs("{Back} + {Start}  held"), Tag = "BackStart" });
            button.Items.Add(new ComboBoxItem { Content = withGlyphs("{L3} + {R3}  both sticks pressed in, held"), Tag = "L3R3" });
            button.Items.Add(new ComboBoxItem { Content = withGlyphs("Keyboard only"), Tag = "None" });
            button.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(nameof(SettingsViewModel.OverlayButton)) { Mode = BindingMode.TwoWay });
            buttonRow.Children.Add(button);
            overlay.Children.Add(buttonRow);
            var shiftTab = Note("");
            global::RomM.Overlay.ControllerGlyphs.Fill(shiftTab, "{Shift} + {Tab} also opens it.");
            overlay.Children.Add(shiftTab);
            var guideWarning = Note("");
            guideWarning.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xB1, 0x43));
            guideWarning.Opacity = 1;
            Action showGuideWarning = () =>
            {
                string by = (button.SelectedValue as string) == "Guide" ? global::RomM.Overlay.GameOverlay.GuideTakenBy(_plugin.Playnite) : null;
                guideWarning.Visibility = by != null ? Visibility.Visible : Visibility.Collapsed;
                if (by == null)
                    return;
                string fix = by == "Playnite" ? "turn off Playnite's Guide button setting"
                    : by == "Steam" ? "close Steam"
                    : "turn off the controller button in Game Bar settings";
                global::RomM.Overlay.ControllerGlyphs.Fill(guideWarning, $"{by} has the Home button, so the overlay uses {{Back}} + {{Start}}. To free it, {fix}.");
            };
            overlay.Children.Add(guideWarning);

            // Emulators' own menus: on whichever button the overlay leaves free (EmulatorMenus.For).
            var menusNow = Note("");
            overlay.Children.Add(menusNow);
            Action showMenus = () =>
            {
                string chosen = button.SelectedValue as string ?? "Guide";
                var place = global::RomM.Overlay.GameOverlay.EmulatorMenuButton(chosen, _plugin.Playnite);
                string words = place == global::RomM.Setup.EmulatorMenus.Place.Home ? "{Guide}"
                    : place == global::RomM.Setup.EmulatorMenus.Place.StartSelect ? "{Back} + {Start}"
                    : "{L3} + {R3}";
                global::RomM.Overlay.ControllerGlyphs.Fill(menusNow, "Emulator menus open with " + words + ".");
            };
            button.SelectionChanged += (s, e) => { showGuideWarning(); showMenus(); };
            Loaded += (s, e) => { showGuideWarning(); showMenus(); };

            overlay.Children.Add(Heading("Windows games"));
            overlay.Children.Add(Check("Show the overlay over Windows games", nameof(SettingsViewModel.OverlayForPcGames)));
            overlay.Children.Add(Check("Make windowed games borderless", nameof(SettingsViewModel.BorderlessWindowedGames)));
            overlay.Children.Add(Note("Lets the overlay show over games set to windowed mode."));

            // ---- Streaming ----------------------------------------------------------------------
            StackPanel streaming = Tab();
            streaming.Children.Add(Heading("Sunshine", first: true));
            streaming.Children.Add(Check("Add installed games to Sunshine", nameof(SettingsViewModel.SunshineEnabled)));
            streaming.Children.Add(Note("Stream them with Moonlight."));
            var fields = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++)
                fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddField(fields, 0, "Sunshine address", Field(nameof(SettingsViewModel.SunshineUrl)));
            AddField(fields, 1, "Username", Field(nameof(SettingsViewModel.SunshineUser)));
            AddField(fields, 2, "Password", _sunshinePassword);
            streaming.Children.Add(fields);
            _sunshinePassword.Loaded += (s, e) =>
            {
                _passwordShown = false;
                _sunshinePassword.Password = global::RomM.Integrations.SunshineSync.Unprotect(_model.SunshinePasswordProtected);
                _passwordShown = true;
            };
            _sunshinePassword.PasswordChanged += (s, e) =>
            {
                if (_passwordShown)
                    _model.SunshinePasswordProtected = global::RomM.Integrations.SunshineSync.Protect(_sunshinePassword.Password);
            };
            var test = new Button { Content = "Test connection", Padding = new Thickness(10, 2, 10, 2) };
            test.Click += async (s, e) =>
            {
                if (_plugin.Sunshine == null)
                    return;
                _sunshineResult.Text = "Asking Sunshine...";
                var settings = _model.SunshineSettings();
                settings.Password = _sunshinePassword.Password;
                string problem = await _plugin.Sunshine.TestAsync(settings);
                _sunshineResult.Text = problem ?? "Sunshine answered: ready.";
            };
            var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            testRow.Children.Add(test);
            testRow.Children.Add(_sunshineResult);
            streaming.Children.Add(testRow);

            // ---- The tabs, and who made it beneath them ------------------------------------------
            var tabs = new TabControl { Margin = new Thickness(10, 0, 20, 0), MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };
            tabs.Items.Add(new TabItem { Header = "Games", Content = Scroll(games) });
            tabs.Items.Add(new TabItem { Header = "Graphics", Content = Scroll(graphics) });
            tabs.Items.Add(new TabItem { Header = "Achievements", Content = Scroll(achievements) });
            tabs.Items.Add(new TabItem { Header = "In-game overlay", Content = Scroll(overlay) });
            tabs.Items.Add(new TabItem { Header = "Streaming", Content = Scroll(streaming) });

            var footer = new StackPanel { Margin = new Thickness(10, 12, 20, 10) };
            var made = Words("RomMnite " + global::RomM.PluginVersion.Current + " · Created by Empyreal");
            made.FontWeight = FontWeights.SemiBold;
            footer.Children.Add(made);
            footer.Children.Add(Note("Based on the RomM Library Importer by The RomM Team."));

            var root = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(tabs);
            Content = root;

            Action changed = () => Dispatcher.BeginInvoke(new Action(ShowRetroAchievements));
            Loaded += (s, e) =>
            {
                _plugin.RetroAchievements.Changed += changed;
                ShowRetroAchievements();
            };
            Unloaded += (s, e) => _plugin.RetroAchievements.Changed -= changed;
        }

        // Signed in or not, and what can be done about it.
        private void ShowRetroAchievements()
        {
            var ra = _plugin.RetroAchievements;
            _raActions.Children.Clear();
            string name = ra.RomMUsername;
            if (ra.SignedIn)
            {
                _raStatus.Text = $"Signed in as {ra.Username}.";
                var signOut = new Button { Content = "Sign out", Padding = new Thickness(10, 2, 10, 2) };
                signOut.Click += (s, e) => ra.SignOut();
                _raActions.Children.Add(signOut);
                return;
            }
            if (name.Length > 0)
            {
                _raStatus.Text = $"Not signed in ({name}).";
                var signIn = new Button { Content = "Sign in to RetroAchievements", Padding = new Thickness(10, 2, 10, 2) };
                signIn.Click += (s, e) => _plugin.SignInToRetroAchievements();
                _raActions.Children.Add(signIn);
                return;
            }

            // No username in the RomM profile yet: entered here, saved there.
            _raStatus.Text = "Enter your RetroAchievements username.";
            var field = new TextBox { Width = 220, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var save = new Button { Content = "Save username", Padding = new Thickness(10, 2, 10, 2) };
            var problem = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            save.Click += (s, e) =>
            {
                string typed = field.Text.Trim();
                if (typed.Length == 0)
                    return;
                save.IsEnabled = false;
                problem.Text = "Saving...";
                _plugin.SetRetroAchievementsUsername(typed, error => Dispatcher.BeginInvoke(new Action(() =>
                {
                    save.IsEnabled = true;
                    problem.Text = error ?? "";
                })));
            };
            _raActions.Children.Add(field);
            _raActions.Children.Add(save);
            _raActions.Children.Add(problem);
        }

        // ---- Pieces ----------------------------------------------------------------------------

        // A tab's page: its sections, one under the other.
        private static StackPanel Tab() => new StackPanel { Margin = new Thickness(12, 14, 16, 16), MaxWidth = 740, HorizontalAlignment = HorizontalAlignment.Left };

        private static ScrollViewer Scroll(UIElement page) =>
            new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        private static TextBlock Words(string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        private static UIElement Heading(string text, bool first = false)
        {
            var panel = new StackPanel { Margin = new Thickness(0, first ? 0 : 18, 0, 0) };
            var title = Words(text);
            title.FontWeight = FontWeights.Bold;
            title.FontSize = 15;
            panel.Children.Add(title);
            panel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 10) });
            return panel;
        }

        // A label and a drop-down bound to a setting; the choices in pairs, as shown then as saved.
        private static UIElement Choice(string label, string path, params string[] pairs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var name = Words(label);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(name);
            var box = new ComboBox { Width = 200, SelectedValuePath = "Tag" };
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                box.Items.Add(new ComboBoxItem { Content = pairs[i], Tag = pairs[i + 1] });
            box.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            row.Children.Add(box);
            return row;
        }

        private static TextBlock Note(string text)
        {
            var note = Words(text);
            note.Opacity = 0.7;
            note.Margin = new Thickness(0, 0, 0, 8);
            return note;
        }

        private static CheckBox Check(string text, string path)
        {
            var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 8) };
            box.SetBinding(ToggleButton_IsChecked, new Binding(path) { Mode = BindingMode.TwoWay });
            return box;
        }

        private static DependencyProperty ToggleButton_IsChecked => System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

        private static TextBox Field(string path)
        {
            var box = new TextBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            box.SetBinding(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            return box;
        }

        private static void AddField(Grid grid, int row, string label, FrameworkElement field)
        {
            var name = Words(label);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(0, 0, 12, 6);
            Grid.SetRow(name, row);
            grid.Children.Add(name);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
        }
    }
}
