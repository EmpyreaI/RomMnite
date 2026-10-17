using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Downloads;
using RomM.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.Store
{
    /// <summary>
    /// Steam's install dialog, for the Store's Install: where the game goes (its platform's own
    /// folder, or another install location, or a new one added here), how much room it needs,
    /// and the chosen drive's space as a bar (used, this game, free). Install is only offered
    /// when the game fits. Returns whether to install and where (null: the platform's folder).
    /// </summary>
    internal static class InstallDialog
    {
        private static readonly Brush Blue = Frozen(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush Used = Frozen(Color.FromRgb(0x5B, 0x63, 0x6E));
        private static readonly Brush FreeBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Warning = Frozen(Color.FromRgb(0xE0, 0x5A, 0x4F));
        private const string AddNew = "add";

        private sealed class Choice
        {
            public string Root;      // null: the platform's own folder
            public string Folder;    // where the game's platform folder is
            public string Label;
            public override string ToString() => Label;
        }

        public static bool Show(RomM plugin, Game game, EmulatorMapping mapping, out string root)
        {
            root = null;
            IPlayniteAPI api = plugin.Playnite;
            string platformFolder = Path.GetFileName((mapping?.DestinationPath ?? "").TrimEnd('\\', '/'));
            ulong size = game.InstallSize ?? 0;
            bool unpacks = plugin.UnpacksAfterDownload(game, mapping);
            // Room for the download and, for an archive, what it unpacks to (about as much again)
            // until the archive is deleted.
            ulong needed = unpacks ? size * 2 : size;

            var window = api.Dialogs.CreateWindow(new WindowCreationOptions { ShowMinimizeButton = false, ShowMaximizeButton = false, ShowCloseButton = true });
            window.Title = "Install " + game.Name;
            window.Width = 560;
            window.SizeToContent = SizeToContent.Height;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowInTaskbar = false;
            window.Owner = api.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            body.Children.Add(Words("Install " + game.Name, 22, FontWeights.Bold, 1.0));

            var sizes = new Grid { Margin = new Thickness(0, 14, 0, 16) };
            sizes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sizes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sizes.Children.Add(Fact("DOWNLOAD SIZE", size > 0 ? Size(size) : "Unknown"));
            var neededFact = Fact("SPACE REQUIRED", size > 0 ? Size(needed) + (unpacks ? " while unpacking" : "") : "Unknown");
            Grid.SetColumn(neededFact, 1);
            sizes.Children.Add(neededFact);
            body.Children.Add(sizes);

            body.Children.Add(Words("INSTALL LOCATION", 11, FontWeights.SemiBold, 0.6));
            var locations = new ComboBox { Margin = new Thickness(0, 6, 0, 12), Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
            body.Children.Add(locations);

            // The chosen drive: used, this game, free.
            var bar = new Grid { Height = 12, Margin = new Thickness(0, 2, 0, 6) };
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            var usedPart = new Border { Background = Used, CornerRadius = new CornerRadius(2, 0, 0, 2) };
            var gamePart = new Border { Background = Blue };
            var freePart = new Border { Background = FreeBrush, CornerRadius = new CornerRadius(0, 2, 2, 0) };
            Grid.SetColumn(gamePart, 1);
            Grid.SetColumn(freePart, 2);
            bar.Children.Add(usedPart);
            bar.Children.Add(gamePart);
            bar.Children.Add(freePart);
            body.Children.Add(bar);

            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            TextBlock usedText = LegendItem(legend, Used);
            TextBlock gameText = LegendItem(legend, Blue);
            TextBlock freeText = LegendItem(legend, FreeBrush);
            body.Children.Add(legend);

            var problem = Words("", 13, FontWeights.SemiBold, 1.0);
            problem.Foreground = Warning;
            problem.Margin = new Thickness(0, 8, 0, 0);
            problem.Visibility = Visibility.Collapsed;
            body.Children.Add(problem);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var cancel = new Button { Content = "Cancel", MinWidth = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
            var install = new Button { Content = "Install", MinWidth = 120, Height = 34, FontWeight = FontWeights.Bold, IsDefault = true };
            buttons.Children.Add(cancel);
            buttons.Children.Add(install);
            body.Children.Add(buttons);
            window.Content = body;

            // The choices: the platform's own folder, each other location, and "add".
            Action<string> fill = null;
            fill = select =>
            {
                locations.Items.Clear();
                locations.Items.Add(new Choice { Root = null, Folder = mapping?.DestinationPathResolved, Label = "Default — " + (mapping?.DestinationPathResolved ?? "the platform's folder") + FreeLabel(mapping?.DestinationPathResolved) });
                foreach (string location in plugin.Locations.All())
                {
                    string folder = string.IsNullOrEmpty(platformFolder) ? location : Path.Combine(location, platformFolder);
                    locations.Items.Add(new Choice { Root = location, Folder = folder, Label = location + FreeLabel(location) });
                }
                locations.Items.Add(new Choice { Root = AddNew, Label = "Add a new install location…" });
                locations.SelectedItem = locations.Items.Cast<Choice>().FirstOrDefault(c => c.Root != AddNew && string.Equals(c.Root, select, StringComparison.OrdinalIgnoreCase))
                    ?? locations.Items[0];
            };

            Choice current = null;
            Action paint = () =>
            {
                Choice choice = locations.SelectedItem as Choice;
                if (choice == null || choice.Root == AddNew)
                    return;
                current = choice;
                bool known = InstallLocations.Space(choice.Folder, out ulong free, out ulong total) && total > 0;
                if (!known)
                {
                    bar.ColumnDefinitions[0].Width = new GridLength(0);
                    bar.ColumnDefinitions[1].Width = new GridLength(0);
                    bar.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                    usedText.Text = "Space on this drive could not be read";
                    gameText.Text = "";
                    freeText.Text = "";
                    problem.Visibility = Visibility.Collapsed;
                    install.IsEnabled = true;
                    return;
                }
                ulong used = total - free;
                ulong game_ = Math.Min(needed, free);
                bar.ColumnDefinitions[0].Width = new GridLength(used, GridUnitType.Star);
                bar.ColumnDefinitions[1].Width = new GridLength(Math.Max(game_, total / 200), GridUnitType.Star);   // at least a sliver, to be seen
                bar.ColumnDefinitions[2].Width = new GridLength(free - game_, GridUnitType.Star);
                usedText.Text = "Used " + Size(used);
                gameText.Text = "This game " + (size > 0 ? Size(needed) : "?");
                freeText.Text = "Free after install " + Size(free - game_) + " of " + Size(total);
                bool fits = needed <= free;
                problem.Text = fits ? "" : $"Not enough space: {Size(needed)} is needed and {Size(free)} is free on this drive. Choose another location or free up space.";
                problem.Visibility = fits ? Visibility.Collapsed : Visibility.Visible;
                install.IsEnabled = fits;
            };

            locations.SelectionChanged += (s, e) =>
            {
                Choice choice = locations.SelectedItem as Choice;
                if (choice == null)
                    return;
                if (choice.Root != AddNew)
                {
                    paint();
                    return;
                }
                // A new location: picked, kept, and chosen; or, cancelled, the one before again.
                // After this selection change has finished (the list is refilled).
                locations.Dispatcher.BeginInvoke(new Action(() =>
                {
                    string picked = api.Dialogs.SelectFolder();
                    if (!string.IsNullOrWhiteSpace(picked))
                    {
                        plugin.Locations.Add(picked);
                        fill(Path.GetFullPath(picked).TrimEnd('\\', '/'));
                    }
                    else
                    {
                        fill(current?.Root);
                    }
                }));
            };

            bool confirmed = false;
            cancel.Click += (s, e) => window.Close();
            install.Click += (s, e) =>
            {
                confirmed = true;
                window.Close();
            };
            window.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                    window.Close();
            };

            fill(null);
            paint();
            window.ShowDialog();

            if (!confirmed || current == null)
                return false;
            root = current.Root;
            return true;
        }

        private static string FreeLabel(string folder) =>
            InstallLocations.Space(folder, out ulong free, out ulong total) && total > 0
                ? $"   ({Size(free)} free of {Size(total)})"
                : "";

        private static TextBlock LegendItem(Panel legend, Brush color)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            item.Children.Add(new Border { Width = 10, Height = 10, Background = color, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            TextBlock text = Words("", 12, FontWeights.Normal, 0.8);
            item.Children.Add(text);
            legend.Children.Add(item);
            return text;
        }

        private static StackPanel Fact(string label, string value)
        {
            var panel = new StackPanel();
            panel.Children.Add(Words(label, 11, FontWeights.SemiBold, 0.6));
            panel.Children.Add(Words(value, 16, FontWeights.SemiBold, 1.0));
            return panel;
        }

        private static TextBlock Words(string text, double size, FontWeight weight, double opacity)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Opacity = opacity, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        internal static string Size(ulong bytes)
        {
            if (bytes >= 1UL << 40) return $"{bytes / (double)(1UL << 40):0.0} TB";
            if (bytes >= 1UL << 30) return $"{bytes / (double)(1UL << 30):0.0} GB";
            if (bytes >= 1UL << 20) return $"{bytes / (double)(1UL << 20):0} MB";
            return $"{bytes / 1024.0:0} KB";
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
