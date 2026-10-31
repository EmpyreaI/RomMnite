using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace RomM
{
    /// <summary>
    /// How long the game page's background (Playnite's own PART_ImageBackground) takes to change
    /// after a game is picked, with what it had to load: in the log when it is slow (over a third
    /// of a second), to tell a big picture being decoded from one fetched from the internet or
    /// the window being busy. A measurement only; nothing is changed.
    /// </summary>
    internal static class BackgroundTiming
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Stopwatch s_clock = new Stopwatch();
        private static FrameworkElement s_watched;
        private static string s_game;
        private static string s_what;

        public static void Selected(Game game, IPlayniteAPI api)
        {
            try
            {
                Hook();
                if (game == null)
                {
                    s_game = null;
                    return;
                }
                s_game = game.Name;
                s_what = Describe(game.BackgroundImage, api);
                s_clock.Restart();
                // Still the last game's background three seconds on: said too.
                string name = game.Name;
                var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                check.Tick += (s, e) =>
                {
                    check.Stop();
                    if (s_game == name && s_clock.IsRunning)
                    {
                        s_clock.Stop();
                        s_log.Info($"[Background] {name}: the background had not changed 3 s after it was picked ({s_what}).");
                    }
                };
                check.Start();
            }
            catch
            {
                // A measurement only.
            }
        }

        private static string Describe(string background, IPlayniteAPI api)
        {
            if (string.IsNullOrEmpty(background))
                return "no background of its own";
            if (background.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return "a background from the internet (" + background + ")";
            string path = api.Database.GetFullFilePath(background);
            if (path == null || !File.Exists(path))
                return "a background file that is missing (" + background + ")";
            return $"{Path.GetExtension(path)} of {new FileInfo(path).Length / 1024} KB";
        }

        private static void Hook()
        {
            if (s_watched != null)
                return;
            FrameworkElement image = Account.PlayniteParts.Find(e => e.Name == "PART_ImageBackground");
            if (image == null)
                return;
            DependencyPropertyDescriptor source = DependencyPropertyDescriptor.FromName("Source", image.GetType(), image.GetType());
            if (source == null)
                return;
            s_watched = image;
            source.AddValueChanged(image, (s, e) =>
            {
                if (s_game == null || !s_clock.IsRunning)
                    return;
                long ms = s_clock.ElapsedMilliseconds;
                s_clock.Stop();
                if (ms > 330)
                    s_log.Info($"[Background] {s_game}: the background changed {ms} ms after it was picked ({s_what}).");
            });
        }
    }
}
