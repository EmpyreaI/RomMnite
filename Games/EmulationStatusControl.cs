using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RomM.Games
{
    /// <summary>
    /// The custom element RomM_EmulationStatus: how the game runs in its emulator, as that
    /// emulator's compatibility list has it, drawn as the Controller section is. Xbox 360 games
    /// in Xenia (XeniaCompat): the list's verdict and known problems, the settings the game needs
    /// (applied for this game only, on request), and the community's patches for it, fixes first,
    /// each switched on or off here. PlayStation 1-3 and Vita games (CompatLists): the verdict of
    /// DuckStation's, PCSX2's, RPCS3's or Vita3K's list, its notes, and the fixes PCSX2 applies
    /// by itself. Hidden for platforms with no list.
    /// </summary>
    public class EmulationStatusControl : PluginUserControl
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Brush Good = Frozen(Color.FromRgb(0x66, 0xC0, 0xF4));
        private static readonly Brush Warning = Frozen(Color.FromRgb(0xE5, 0xB1, 0x43));
        private static readonly Brush Bad = Frozen(Color.FromRgb(0xE0, 0x5A, 0x4F));

        private readonly RomM _plugin;
        private readonly StackPanel _body = new StackPanel();
        private Game _game;
        private int _asked;

        public EmulationStatusControl(RomM plugin)
        {
            _plugin = plugin;
            Focusable = false;
            var title = new TextBlock { Text = "Emulation" };
            title.SetResourceReference(StyleProperty, "DetailViewSectionTitleTextBlockStyle");
            var card = new Border { Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 12, 0, 0), Child = _body };
            card.SetResourceReference(StyleProperty, "DetailViewCardBorder");
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };
            panel.Children.Add(title);
            panel.Children.Add(card);
            Content = panel;
            Visibility = Visibility.Collapsed;
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _game = newContext;
            Visibility = Visibility.Collapsed;
            int ask = ++_asked;
            Game game = newContext;
            string spec = newContext?.Platforms?.Select(p => p?.SpecificationId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? "";
            if (!IsXbox360(newContext) && CompatLists.Covers(spec) && _plugin.CompatLists != null)
            {
                List<string> names = NamesOf(game);
                List<string> installed = InstalledOf(game);
                string folder = EmulatorFolder(game);
                Task.Run(() => _plugin.CompatLists.FindAsync(spec, game.Name, names, installed, folder)).ContinueWith(t =>
                {
                    CompatReport report = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ask == _asked && report != null)
                            ShowList(report);
                    }));
                });
                return;
            }
            if (!IsXbox360(newContext) || _plugin.XeniaCompat == null)
                return;
            Task.Run(() => _plugin.XeniaCompat.FindAsync(game.Name)).ContinueWith(t =>
            {
                XeniaReport report = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ask != _asked)
                        return;
                    Show(game, report);
                }));
            });
        }

        private static bool IsXbox360(Game game) =>
            game?.Platforms?.Any(p => p != null && (string.Equals(p.SpecificationId, "xbox360", StringComparison.OrdinalIgnoreCase)
                || (p.Name ?? "").IndexOf("Xbox 360", StringComparison.OrdinalIgnoreCase) >= 0)) == true;

        // The game's file names (its serial or title id is often in them): the installed files and
        // the file it downloads as.
        private List<string> NamesOf(Game game)
        {
            var names = new List<string>();
            foreach (GameRom rom in game.Roms ?? new System.Collections.ObjectModel.ObservableCollection<GameRom>())
                names.Add(rom.Path);
            try
            {
                var data = _plugin.LoadGameData(game);
                foreach (var version in data?.ROMVersions ?? new List<Models.RomM.Rom.RomMRevision>())
                {
                    names.Add(version.FileName);
                    names.Add(version.FolderName);
                }
            }
            catch
            {
                // Not a RomM game, or no record: its own files only.
            }
            return names.Where(n => !string.IsNullOrEmpty(n)).ToList();
        }

        // Where an installed game's files are, as real paths (a PARAM.SFO is looked for there).
        private List<string> InstalledOf(Game game)
        {
            var paths = new List<string>();
            if (!game.IsInstalled)
                return paths;
            try
            {
                if (!string.IsNullOrEmpty(game.InstallDirectory))
                    paths.Add(PlaynitePath.Resolve(_plugin.Playnite, _plugin.Playnite.ExpandGameVariables(game, game.InstallDirectory)));
                foreach (GameRom rom in game.Roms ?? new System.Collections.ObjectModel.ObservableCollection<GameRom>())
                    paths.Add(PlaynitePath.Resolve(_plugin.Playnite, _plugin.Playnite.ExpandGameVariables(game, rom.Path)));
            }
            catch
            {
                // Paths that do not resolve are passed over.
            }
            return paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The folder of the emulator playing the game's platform, or null.
        private string EmulatorFolder(Game game)
        {
            try
            {
                var mapping = _plugin.MappingForPlatform(_plugin.PlatformIdOf(game)) ?? _plugin.MappingFor(game);
                string folder = PlaynitePath.Resolve(_plugin.Playnite, mapping?.Emulator?.InstallDir);
                return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
            }
            catch
            {
                return null;
            }
        }

        // Another emulator's list: its verdict, notes and the fixes it applies by itself.
        private void ShowList(CompatReport report)
        {
            _body.Children.Clear();
            Visibility = Visibility.Visible;
            Brush tone = report.Tone == 0 ? Good : report.Tone == 1 ? Warning : report.Tone == 2 ? Bad : null;
            var icon = new TextBlock
            {
                Text = "\uE7FC",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 30,
                Margin = new Thickness(0, 2, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (tone != null)
                icon.Foreground = tone;
            else
                icon.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Line(report.Headline, bold: true));
            foreach (string note in report.Notes)
                words.Children.Add(Line(note, muted: true));
            var source = new TextBlock { FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            source.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
            source.Inlines.Add($"From {report.Source}" + (report.MatchedAs != null ? $" ({report.MatchedAs})" : ""));
            if (report.Url != null)
            {
                source.Inlines.Add(": ");
                source.Inlines.Add(Link("report", report.Url));
            }
            words.Children.Add(source);
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(words);
            _body.Children.Add(row);

            if (report.AutoFixes.Count > 0)
            {
                var section = Section($"Fixed automatically by {report.Emulator}");
                foreach (string fix in report.AutoFixes)
                    section.Children.Add(Line("\u2022  " + fix, muted: true));
                _body.Children.Add(section);
            }
        }

        // Xenia's own folder for the game's platform (where its settings and patches go), or null
        // when the platform is not set up with Xenia on this computer.
        private string XeniaFolder(Game game)
        {
            try
            {
                var mapping = _plugin.MappingForPlatform(_plugin.PlatformIdOf(game)) ?? _plugin.MappingFor(game);
                Emulator emulator = mapping?.Emulator;
                string folder = PlaynitePath.Resolve(_plugin.Playnite, emulator?.InstallDir);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    return null;
                bool xenia = string.Equals(emulator.BuiltInConfigId, "xenia", StringComparison.OrdinalIgnoreCase)
                             || Directory.GetFiles(folder, "xenia*.exe").Length > 0;
                return xenia ? folder : null;
            }
            catch
            {
                return null;
            }
        }

        private void Show(Game game, XeniaReport report)
        {
            _body.Children.Clear();
            Visibility = Visibility.Visible;
            string xenia = XeniaFolder(game);

            // The verdict.
            string headline;
            Brush tone;
            switch (report?.State)
            {
                case "Playable": headline = "Plays well in Xenia"; tone = Good; break;
                case "Gameplay": headline = "Playable in Xenia, with some issues"; tone = Warning; break;
                case "Loads": headline = "Starts in Xenia, but doesn't get into play yet"; tone = Bad; break;
                case "Unplayable": headline = "Doesn't run in Xenia yet"; tone = Bad; break;
                default: headline = report == null ? "Not on Xenia's compatibility list yet" : "Xenia hasn't rated this game yet"; tone = null; break;
            }
            var icon = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 30,
                Margin = new Thickness(0, 2, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (tone != null)
                icon.Foreground = tone;
            else
                icon.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Line(headline, bold: true));
            List<string> issues = Issues(report);
            foreach (string issue in issues)
                words.Children.Add(Line("•  " + issue, muted: true));
            if (report?.Url != null)
            {
                var source = new TextBlock { FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
                source.SetResourceReference(TextBlock.ForegroundProperty, "PromptBrush");
                source.Inlines.Add($"From Xenia's compatibility list ({report.Title}, {report.TitleId}): ");
                source.Inlines.Add(Link("report", report.Url));
                words.Children.Add(source);
            }
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(words);
            _body.Children.Add(row);
            if (report == null)
                return;

            // Settings the game needs, for this game only.
            List<XeniaSetting> needed = XeniaCompat.Needed(report);
            if (needed.Count > 0)
            {
                var section = Section("Recommended settings");
                foreach (XeniaSetting setting in needed)
                    section.Children.Add(Line("•  " + setting.Why, muted: true));
                bool applied = xenia != null && XeniaCompat.Applied(xenia, report, needed);
                var apply = new Button { Content = applied ? "Applied for this game" : "Apply for this game", IsEnabled = xenia != null && !applied, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 6, 0, 0) };
                apply.Click += (s, e) =>
                {
                    string problem = XeniaCompat.Apply(xenia, report, needed);
                    if (problem != null)
                        _plugin.Playnite.Dialogs.ShowErrorMessage("The settings could not be written.\n\n" + problem, "RomMnite");
                    Show(game, report);
                };
                section.Children.Add(apply);
                section.Children.Add(Line("Takes effect the next time the game starts.", muted: true));
                if (xenia == null)
                    section.Children.Add(Line("Set up Xenia for this platform first.", muted: true));
                _body.Children.Add(section);
            }

            // The community's patches: fixes first, the rest (60 FPS, widescreen...) after.
            var fixes = report.Patches.Where(p => p.IsFix).ToList();
            var others = report.Patches.Where(p => !p.IsFix).ToList();
            if (fixes.Count > 0)
                _body.Children.Add(PatchList("Fixes available", fixes, xenia, report));
            if (others.Count > 0)
                _body.Children.Add(PatchList(fixes.Count > 0 ? "Other patches" : "Patches available", others, xenia, report));
            if (report.Patches.Count > 0 && xenia == null)
                _body.Children.Add(Line("Set up Xenia for this platform to switch patches on.", muted: true));
            else if (report.Patches.Count > 0)
                _body.Children.Add(Line("Patches switched here take effect the next time the game starts.", muted: true));
        }

        private StackPanel PatchList(string heading, List<XeniaPatch> patches, string xenia, XeniaReport report)
        {
            var section = Section(heading);
            foreach (XeniaPatch patch in patches)
            {
                var box = new CheckBox
                {
                    Content = new TextBlock { Text = patch.Name, TextWrapping = TextWrapping.Wrap },
                    IsChecked = xenia != null && _plugin.XeniaCompat.IsEnabled(xenia, patch),
                    IsEnabled = xenia != null,
                    Margin = new Thickness(0, 4, 0, 0),
                    ToolTip = string.IsNullOrEmpty(patch.Author) ? null : "By " + patch.Author,
                };
                box.Click += (s, e) =>
                {
                    string problem = _plugin.XeniaCompat.SetEnabled(xenia, patch, box.IsChecked == true);
                    if (problem != null)
                    {
                        box.IsChecked = !box.IsChecked;
                        _plugin.Playnite.Dialogs.ShowErrorMessage($"\"{patch.Name}\" could not be switched.\n\n{problem}", "RomMnite");
                    }
                };
                section.Children.Add(box);
                if (!string.IsNullOrEmpty(patch.Description))
                {
                    var desc = Line(patch.Description, muted: true);
                    desc.Margin = new Thickness(28, 0, 0, 2);
                    desc.FontSize = 11.5;
                    section.Children.Add(desc);
                }
            }
            return section;
        }

        // ---- What the labels mean ------------------------------------------------------------

        private static readonly Dictionary<string, string> Problems = new Dictionary<string, string>
        {
            ["gpu-drawing-corrupt"] = "Some graphics draw incorrectly",
            ["gpu-drawing-missing"] = "Some graphics are missing",
            ["gpu-occlusion-query"] = "Some effects (lights, lens flares) may flicker or be missing",
            ["gpu-occlusion-querybatch"] = "Some effects (lights, lens flares) may flicker or be missing",
            ["gpu-viz-query"] = "Some effects (lights, lens flares) may flicker or be missing",
            ["gpu-readback"] = "Some effects rely on reading the GPU's output back, which can be slow or missing",
            ["gpu-shader-errors"] = "Some shaders fail to work",
            ["gpu-slow"] = "Graphics run slowly",
            ["gpu-async-compile-issues"] = "Glitches while shaders compile",
            ["gpu-driver-issues-amd"] = "Has problems on AMD graphics",
            ["gpu-driver-issues-nvidia"] = "Has problems on NVIDIA graphics",
            ["apu-garbage"] = "Audio may crackle or distort",
            ["apu-silent"] = "Some audio is missing",
            ["apu-slow"] = "Audio may slow down",
            ["crash-random"] = "May crash at random",
            ["crash-always"] = "Crashes at a certain point",
            ["cpu-timing"] = "Runs at an uneven speed",
            ["kernel-save-file-errors"] = "Saving may fail",
            ["xam-live-signin-required"] = "Needs an Xbox Live sign-in",
            ["tech-kinect-required"] = "Needs Kinect, which isn't emulated",
            ["tech-microphone-required"] = "Needs a microphone",
            ["requires_hdd_install"] = "Needs its disc installed to the hard drive (in Xenia: File, Install Content)",
            ["custom-patch-required"] = "Needs a patch to work properly",
            ["regression"] = "Runs worse in recent Xenia builds than it once did",
        };

        internal static List<string> Issues(XeniaReport report) =>
            (report?.Labels ?? new List<string>()).Where(Problems.ContainsKey).Select(l => Problems[l]).Distinct().ToList();

        // ---- Pieces --------------------------------------------------------------------------

        private static StackPanel Section(string heading)
        {
            var section = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 10) };
            line.SetResourceReference(Border.BackgroundProperty, "PanelSeparatorBrush");
            section.Children.Add(line);
            section.Children.Add(Line(heading, bold: true));
            return section;
        }

        private static TextBlock Line(string text, bool bold = false, bool muted = false)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, bold ? 0 : 2, 0, 0) };
            block.SetResourceReference(StyleProperty, "BaseTextBlockStyle");
            block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "PromptBrush" : "TextBrush");
            if (bold)
                block.FontWeight = FontWeights.SemiBold;
            return block;
        }

        private static Hyperlink Link(string text, string url)
        {
            var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
            link.SetResourceReference(TextElement.ForegroundProperty, "GlyphBrush");
            link.RequestNavigate += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch
                {
                    // No browser to open it with.
                }
                e.Handled = true;
            };
            return link;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
