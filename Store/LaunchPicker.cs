using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Games;
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
    /// "What starts this game?": a Windows game's programs (WindowsLaunch's ranking, best first,
    /// marked Recommended), each with where it is in the game's folder, its own name for itself,
    /// its size and whether it is 64-bit. Shown at the first Play of a game whose program was not
    /// sure, and from the game's menu ("Choose what to launch..."). The program chosen, or null.
    /// On the UI thread.
    /// </summary>
    internal static class LaunchPicker
    {
        private static readonly Brush Blue = Frozen(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush RowBrush = Frozen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        private static readonly Brush RowHover = Frozen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

        /// <param name="current">The program the game starts with now (selected first when listed).</param>
        /// <param name="playing">Asked at Play (the button says Play), or from the menu (Save).</param>
        public static string Show(IPlayniteAPI api, Game game, WindowsLaunch.Ranking ranking, string current, bool playing)
        {
            List<WindowsLaunch.Candidate> candidates = ranking?.Candidates ?? new List<WindowsLaunch.Candidate>();
            if (candidates.Count == 0)
            {
                api.Dialogs.ShowMessage($"No program to start was found in {game.Name}'s folder.", "RomMnite");
                return null;
            }

            var window = api.Dialogs.CreateWindow(new WindowCreationOptions { ShowMinimizeButton = false, ShowMaximizeButton = false, ShowCloseButton = true });
            window.Title = "What starts " + game.Name + "?";
            window.Width = 640;
            window.SizeToContent = SizeToContent.Height;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowInTaskbar = false;
            window.Owner = api.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var body = new StackPanel { Margin = new Thickness(24, 12, 24, 20) };
            body.Children.Add(Words(playing
                ? "This game has more than one program, and it isn't clear which one starts it. Pick it once; this computer remembers."
                : "The program this game starts with on this computer.", 13, FontWeights.Normal, 0.8));

            var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var scroll = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            body.Children.Add(scroll);

            string chosen = null;
            var rows = new List<Border>();
            Action<string> select = null;
            select = path =>
            {
                chosen = path;
                for (int i = 0; i < rows.Count; i++)
                {
                    bool on = string.Equals((string)rows[i].Tag, path, StringComparison.OrdinalIgnoreCase);
                    rows[i].BorderBrush = on ? Blue : Brushes.Transparent;
                    rows[i].Background = on ? RowHover : RowBrush;
                }
            };

            Button ok = null;
            foreach (WindowsLaunch.Candidate c in candidates.Take(12))
            {
                var line = new StackPanel();
                var title = Words(c.Relative, 14, FontWeights.SemiBold, 1.0);
                if (c == ranking.Best)
                    title.Inlines.Add(new System.Windows.Documents.Run("   RECOMMENDED") { FontSize = 10, Foreground = Blue, FontWeight = FontWeights.Bold });
                if (string.Equals(c.Path, current, StringComparison.OrdinalIgnoreCase))
                    title.Inlines.Add(new System.Windows.Documents.Run("   IN USE") { FontSize = 10, FontWeight = FontWeights.Bold });
                line.Children.Add(title);
                var facts = new List<string>();
                if (!string.IsNullOrEmpty(c.Product))
                    facts.Add(c.Product);
                if (c.Size > 0)
                    facts.Add(InstallDialog.Size((ulong)c.Size));
                if (c.Is64.HasValue)
                    facts.Add(c.Is64.Value ? "64-bit" : "32-bit");
                if (c.SteamLaunches)
                    facts.Add("Steam starts this");
                else if (c.Launcher)
                    facts.Add("a launcher");
                line.Children.Add(Words(string.Join("  ·  ", facts), 12, FontWeights.Normal, 0.65));

                var row = new Border
                {
                    Child = line,
                    Tag = c.Path,
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 4),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Background = RowBrush,
                    Cursor = Cursors.Hand,
                };
                string path = c.Path;
                row.MouseLeftButtonUp += (s, e) => select(path);
                row.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        select(path);
                        ok?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                };
                rows.Add(row);
                list.Children.Add(row);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "Cancel", MinWidth = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
            ok = new Button { Content = playing ? "Play" : "Save", MinWidth = 120, Height = 34, FontWeight = FontWeights.Bold, IsDefault = true };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            body.Children.Add(buttons);
            window.Content = body;

            bool confirmed = false;
            cancel.Click += (s, e) => window.Close();
            ok.Click += (s, e) =>
            {
                confirmed = chosen != null;
                window.Close();
            };
            window.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                    window.Close();
            };

            bool currentListed = current != null && candidates.Any(c => string.Equals(c.Path, current, StringComparison.OrdinalIgnoreCase));
            select(currentListed ? current : ranking.Best.Path);
            window.ShowDialog();
            return confirmed ? chosen : null;
        }

        private static TextBlock Words(string text, double size, FontWeight weight, double opacity)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Opacity = opacity, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
