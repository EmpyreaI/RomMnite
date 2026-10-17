using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace RomM.Setup
{
    /// <summary>
    /// The theme the extension is made for (RomMnite: FusionX by sakasaki, reworked around the
    /// RomM elements), shipped
    /// inside the extension (Theme\Desktop\&lt;theme id&gt;\...) and kept in Playnite's Themes folder:
    /// at each start, any file that differs from the extension's copy is written over (the one
    /// it replaces kept once as .before-romm.bak); files only Playnite's copy has are left. The
    /// first time, Playnite is also switched to the theme. Playnite reads its theme only when it
    /// starts, so a change asks for a restart.
    /// </summary>
    internal static class ThemeInstaller
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        /// <summary>The bundled desktop theme Playnite is switched to the first time.</summary>
        public const string DesktopThemeId = "RomMnite_68e12a1e-110b-47f3-88f5-d4f6b6747d86";

        /// <summary>Brings Playnite's copy in step; on the UI thread, after start-up.</summary>
        public static void Sync(IPlayniteAPI api, string dataFolder)
        {
            HideSidebarOnce(api, dataFolder);
            try
            {
                string source = Path.Combine(Path.GetDirectoryName(typeof(ThemeInstaller).Assembly.Location) ?? "", "Theme");
                if (!Directory.Exists(source))
                    return;
                string themes = Path.Combine(api.Paths.ConfigurationPath, "Themes");

                int written = 0;
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(source.Length).TrimStart('\\', '/');
                    string target = Path.Combine(themes, relative);
                    if (File.Exists(target) && SameContent(file, target))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target) && !File.Exists(target + ".before-romm.bak"))
                        File.Copy(target, target + ".before-romm.bak");
                    File.Copy(file, target, true);
                    written++;
                }
                if (written > 0)
                    s_log.Info($"[Theme] {written} theme file(s) brought up to date in {themes}.");

                // The first time: Playnite switched to the theme, its settings file written at once
                // (Playnite writes it only when quit from its menu), and a restart offered. Marked
                // done only once it took, so a failed attempt is tried again at the next start.
                string marker = Path.Combine(dataFolder, "ThemeSelected-" + DesktopThemeId + ".txt");
                bool selected = false;
                if (!File.Exists(marker))
                {
                    bool? result = SelectDesktopTheme(api);
                    if (result != null)
                        File.WriteAllText(marker, DesktopThemeId);
                    selected = result == true;
                }

                bool active = string.Equals(CurrentTheme(api), DesktopThemeId, StringComparison.OrdinalIgnoreCase);
                if (selected || (written > 0 && active))
                    OfferRestart(api, selected
                        ? "The RomMnite theme is installed. Restart Playnite now to use it?"
                        : "The RomMnite theme was updated. Restart Playnite now to see the changes?");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Theme] Could not install the theme: {ex.Message}");
            }
        }

        // Playnite's own settings object (PlayniteSettings), behind the API: Playnite has no API
        // for its theme setting.
        private static object PlayniteSettings(IPlayniteAPI api)
        {
            object wrapper = api.ApplicationSettings;
            return wrapper?.GetType().GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(wrapper);
        }

        private static string CurrentTheme(IPlayniteAPI api)
        {
            try
            {
                object settings = PlayniteSettings(api);
                return settings?.GetType().GetProperty("Theme")?.GetValue(settings) as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets Playnite's desktop theme and writes its settings file. True when set now, false
        /// when it already was, null when it could not be (tried again next time).
        /// </summary>
        private static bool? SelectDesktopTheme(IPlayniteAPI api)
        {
            try
            {
                object settings = PlayniteSettings(api);
                PropertyInfo theme = settings?.GetType().GetProperty("Theme", BindingFlags.Instance | BindingFlags.Public);
                if (theme == null || !theme.CanWrite)
                {
                    s_log.Info("[Theme] Could not select the theme here; choose RomMnite in Playnite's settings (Appearance).");
                    return null;
                }
                if (string.Equals(theme.GetValue(settings) as string, DesktopThemeId, StringComparison.OrdinalIgnoreCase))
                    return false;
                theme.SetValue(settings, DesktopThemeId);

                MethodInfo save = settings.GetType().GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (save == null)
                {
                    s_log.Info("[Theme] Theme set, but Playnite's settings could not be written now; they are when Playnite is quit from its menu.");
                    return true;
                }
                save.Invoke(settings, null);
                s_log.Info($"[Theme] Playnite's desktop theme set to {DesktopThemeId} and saved (from the next start).");
                return true;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Theme] Could not select the theme: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Playnite's sidebar (Library, Downloads and the add-ons' buttons down the side) off, the
        /// first time only: the theme has its own way to everything there, and the user can turn
        /// it back on (main menu, View, Sidebar, Enabled) and it stays on. Playnite shows
        /// the change at once; its settings file is written now too.
        /// </summary>
        private static void HideSidebarOnce(IPlayniteAPI api, string dataFolder)
        {
            try
            {
                string marker = Path.Combine(dataFolder, "SidebarHidden.txt");
                if (File.Exists(marker))
                    return;
                object settings = PlayniteSettings(api);
                PropertyInfo show = settings?.GetType().GetProperty("ShowSidebar", BindingFlags.Instance | BindingFlags.Public);
                if (show == null || !show.CanWrite)
                {
                    s_log.Info("[Theme] Could not turn Playnite's sidebar off here.");
                    return;
                }
                if (show.GetValue(settings) is bool on && on)
                {
                    show.SetValue(settings, false);
                    settings.GetType().GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)?.Invoke(settings, null);
                    s_log.Info("[Theme] Playnite's sidebar turned off (first start).");
                }
                Directory.CreateDirectory(dataFolder);
                File.WriteAllText(marker, "Playnite's sidebar was turned off once; turning it back on is left alone.");
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Theme] Could not turn Playnite's sidebar off: {ex.Message}");
            }
        }

        // Once Playnite has settled: asks, then restarts it as its own settings do (saving them first).
        private static void OfferRestart(IPlayniteAPI api, string question)
        {
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (api.Dialogs.ShowMessage(question, "RomMnite", System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
                        return;

                    // PlayniteApplication.Current.Restart(bool saveSettings), in Playnite.dll: not in the SDK.
                    Type app = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("Playnite.PlayniteApplication", false))
                        .FirstOrDefault(t => t != null);
                    object current = app?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                    MethodInfo restart = current?.GetType().GetMethod("Restart", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
                    if (restart == null)
                    {
                        api.Dialogs.ShowMessage("Playnite could not be restarted from here: close it and start it again to use the theme.", "RomMnite");
                        return;
                    }
                    restart.Invoke(current, new object[] { true });
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Theme] Could not restart Playnite: {ex.Message}");
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private static bool SameContent(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length)
                return false;
            using (var sha = SHA1.Create())
            {
                byte[] ha, hb;
                using (var sa = File.OpenRead(a))
                    ha = sha.ComputeHash(sa);
                using (var sb = File.OpenRead(b))
                    hb = sha.ComputeHash(sb);
                return ha.SequenceEqual(hb);
            }
        }
    }
}
