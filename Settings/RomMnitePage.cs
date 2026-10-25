using Playnite.SDK;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RomM.Settings
{
    /// <summary>
    /// RomMnite's own page of settings: achievements, videos, RetroAchievements sign-in, and
    /// streaming with Sunshine. Shown as the "RomMnite" category of Playnite's Settings window
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

            var page = new StackPanel { Margin = new Thickness(10, 0, 20, 20), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

            // Achievements
            page.Children.Add(Heading("Achievements", first: true));
            page.Children.Add(Check("Show unlocked achievements in game details", nameof(SettingsViewModel.ShowUnlockedAchievements)));
            page.Children.Add(Check("Show locked achievements in game details", nameof(SettingsViewModel.ShowLockedAchievements)));

            // RetroAchievements
            page.Children.Add(Heading("RetroAchievements"));
            page.Children.Add(_raStatus);
            page.Children.Add(_raActions);
            page.Children.Add(Note("Signing in lets your emulators earn achievements as you, and shows your progress here. The password is not kept: RetroAchievements gives a login token, kept for this Windows user only."));

            // Videos
            page.Children.Add(Heading("Videos"));
            page.Children.Add(Check("Play game videos automatically", nameof(SettingsViewModel.AutoPlayVideos)));

            // In-game overlay
            page.Children.Add(Heading("In-game overlay"));
            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var buttonLabel = Words("Open it with");
            buttonLabel.VerticalAlignment = VerticalAlignment.Center;
            buttonLabel.Margin = new Thickness(0, 0, 12, 0);
            buttonRow.Children.Add(buttonLabel);
            var button = new ComboBox { Width = 280, SelectedValuePath = "Tag" };
            button.Items.Add(new ComboBoxItem { Content = "Home button (Guide)", Tag = "Guide" });
            button.Items.Add(new ComboBoxItem { Content = "Back + Start, held", Tag = "BackStart" });
            button.Items.Add(new ComboBoxItem { Content = "L3 + R3 (both sticks), held", Tag = "L3R3" });
            button.Items.Add(new ComboBoxItem { Content = "Keyboard only (Shift+Tab)", Tag = "None" });
            button.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(nameof(SettingsViewModel.OverlayButton)) { Mode = BindingMode.TwoWay });
            buttonRow.Children.Add(button);
            page.Children.Add(buttonRow);
            page.Children.Add(Note("Shift+Tab opens it from the keyboard whichever you choose."));
            page.Children.Add(Check("Show it over Windows games too (they keep running while it's open; Steam games keep Steam's own overlay)", nameof(SettingsViewModel.OverlayForPcGames)));
            page.Children.Add(Check("Make windowed Windows games borderless", nameof(SettingsViewModel.BorderlessWindowedGames)));
            page.Children.Add(Note("The overlay can't show over a game in exclusive fullscreen. Set such a game to windowed in its own display settings and this fills the screen with it, without borders, so the overlay works. Best with the game's resolution set to your screen's."));
            page.Children.Add(Check("Open emulators' own menus with Start + Select (DuckStation, PCSX2, RetroArch, PPSSPP), so the Home button is the overlay's alone", nameof(SettingsViewModel.EmulatorMenusOnStartSelect)));
            page.Children.Add(Note("Applied as each game starts, and only where you haven't put the emulator's menu on a controller button yourself. Not while the overlay is on Back + Start, which shares those buttons."));
            page.Children.Add(Check("Render emulated games at your display's resolution (DuckStation, PCSX2, RPCS3, Dolphin, Azahar, Xenia, Vita3K, Flycast, xemu)", nameof(SettingsViewModel.EmulatorsAtDisplayResolution)));
            page.Children.Add(Note("Applied as each game starts, only where the emulator's resolution is still its default: a resolution you choose in the emulator stays. Sharper, but heavier on the graphics card; lower it in the emulator if a game slows down. Turning this off puts back the emulators' defaults."));
            var guideWarning = Note("");
            guideWarning.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xB1, 0x43));
            guideWarning.Opacity = 1;
            Action showGuideWarning = () =>
            {
                string by = (button.SelectedValue as string) == "Guide" ? global::RomM.Overlay.GameOverlay.GuideTakenBy(_plugin.Playnite) : null;
                guideWarning.Visibility = by != null ? Visibility.Visible : Visibility.Collapsed;
                if (by == null)
                    return;
                string fix = by == "Playnite" ? "Turn off Playnite's own \"Guide button\" setting (Settings, Input)"
                    : by == "Steam" ? "Close Steam"
                    : "Turn off \"Open Xbox Game Bar using this button on a controller\" in Windows' Game Bar settings";
                guideWarning.Text = $"{by} has the Home button right now, so games use Hold Start + Select for the overlay instead (emulators' menus are taken off it then). {fix} for the Home button to open the overlay.";
            };
            button.SelectionChanged += (s, e) => showGuideWarning();
            Loaded += (s, e) => showGuideWarning();
            page.Children.Add(guideWarning);

            // Streaming
            page.Children.Add(Heading("Streaming (Sunshine)"));
            page.Children.Add(Check("List installed games in Sunshine, so Moonlight can stream them (removed again when uninstalled)", nameof(SettingsViewModel.SunshineEnabled)));
            var fields = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++)
                fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddField(fields, 0, "Sunshine address", Field(nameof(SettingsViewModel.SunshineUrl)));
            AddField(fields, 1, "Web UI username", Field(nameof(SettingsViewModel.SunshineUser)));
            AddField(fields, 2, "Web UI password", _sunshinePassword);
            page.Children.Add(fields);
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
            var test = new Button { Content = "Test Sunshine", Padding = new Thickness(10, 2, 10, 2) };
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
            page.Children.Add(testRow);
            page.Children.Add(Note("The name and password are the ones you set up in Sunshine's web UI. Only games RomMnite adds are changed; your other Sunshine apps stay as they are."));

            // Credits
            page.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 10) });
            var made = Words("RomMnite " + global::RomM.PluginVersion.Current + " · Created by Empyreai");
            made.FontWeight = FontWeights.SemiBold;
            page.Children.Add(made);
            page.Children.Add(Note("Based on the RomM Library Importer by The RomM Team, whose work it builds on."));

            Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

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
                _raStatus.Text = $"Not signed in. Your RomM profile's RetroAchievements username is {name}.";
                var signIn = new Button { Content = "Sign in to RetroAchievements", Padding = new Thickness(10, 2, 10, 2) };
                signIn.Click += (s, e) => _plugin.SignInToRetroAchievements();
                _raActions.Children.Add(signIn);
                return;
            }

            // No username in the RomM profile yet: entered here, saved there.
            _raStatus.Text = "Enter your RetroAchievements username. It is saved to your RomM profile, then you can sign in.";
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
