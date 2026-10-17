using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RomM.Settings
{
    /// <summary>
    /// A "RomMnite" category in Playnite's own Settings window (beside General, Appearance,
    /// Input...), holding RomMnite's page (RomMnitePage). Playnite has no way for an extension to
    /// add one, so it is put in when the window opens: an entry at the end of the window's list of
    /// categories, and the page laid over the category's area while that entry is chosen. Saved
    /// with the window's Save, dropped with Cancel or on closing.
    ///
    /// Found by the window's parts, not names Playnite promises: the window whose type is
    /// SettingsWindow, its tree of categories, the area showing the chosen one (the largest content
    /// area beside the tree), and its Save button. When any is not found, nothing is added and
    /// the page stays reachable from the extension's own settings.
    /// </summary>
    internal static class PlayniteSettingsPage
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static global::RomM.RomM s_plugin;
        private static bool s_installed;

        public static void Install(global::RomM.RomM plugin)
        {
            s_plugin = plugin;
            if (s_installed)
                return;
            s_installed = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is Window window) || e.OriginalSource != window
                || window.GetType().Name.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) < 0
                || window.GetType().Assembly == typeof(PlayniteSettingsPage).Assembly)
                return;
            // Once laid out (sizes known), and once per window.
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!Add(window, out string why))
                        s_log.Info($"[Settings] RomMnite's category was not added to {window.GetType().Name}: {why}");
                }
                catch (Exception ex)
                {
                    s_log.Warn(ex, "[Settings] Could not add RomMnite to Playnite's settings.");
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // The words of a category's header, however Playnite filled it.
        private static string HeaderText(object header)
        {
            switch (header)
            {
                case string text: return text.Trim();
                case TextBlock block: return block.Text?.Trim();
                case ContentControl control: return HeaderText(control.Content);
                default: return null;
            }
        }

        private static string Localized(string key, string english)
        {
            string text = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(text) || text.StartsWith("<!") ? english : text;
        }

        private static bool Add(Window window, out string why)
        {
            why = null;
            SettingsViewModel model = SettingsViewModel.Instance;
            if (model == null || s_plugin == null)
            {
                why = "the extension's settings are not loaded";
                return false;
            }

            // Playnite's list of categories: a tree whose first entries include "General".
            string general = Localized("LOCSettingsGeneralLabel", "General");
            TreeView tree = All<TreeView>(window).FirstOrDefault(t => t.Items.Cast<object>().Take(4)
                .Any(i => i is TreeViewItem item && (string.Equals(HeaderText(item.Header), general, StringComparison.CurrentCultureIgnoreCase)
                                                     || string.Equals(HeaderText(item.Header), "General", StringComparison.OrdinalIgnoreCase))));
            if (tree == null)
            {
                why = "no list of categories starting with General";
                return false;
            }
            if (tree.ItemsSource != null)
            {
                why = "its categories are generated from a list, which takes no new entries";
                return false;
            }
            if (tree.Items.OfType<TreeViewItem>().Any(i => HeaderText(i.Header) == global::RomM.RomM.DisplayName))
                return true;
            Button save = All<Button>(window).FirstOrDefault(IsSave);
            if (save == null)
            {
                why = "no Save button";
                return false;
            }

            // The area showing the chosen category: walking out from the list, the first panel
            // that also holds something large beside it (not the buttons).
            FrameworkElement content = null;
            Panel panel = null;
            for (DependencyObject at = tree; at != null && at != window; at = VisualTreeHelper.GetParent(at))
            {
                if (!(VisualTreeHelper.GetParent(at) is Panel parent) || parent.IsItemsHost || parent.Children.Count < 2)
                    continue;
                content = parent.Children.OfType<FrameworkElement>()
                    .Where(c => c != at && c.Visibility == Visibility.Visible && c.ActualWidth > 150 && c.ActualHeight > 100 && !IsInside(save, c))
                    .OrderByDescending(c => c.ActualWidth * c.ActualHeight)
                    .FirstOrDefault();
                if (content != null)
                {
                    panel = parent;
                    break;
                }
            }
            if (content == null)
            {
                why = "no category area beside the list";
                return false;
            }

            // The area wrapped, so the page can sit over it in the same place whatever the layout.
            int index = panel.Children.IndexOf(content);
            var wrapper = new Grid();
            foreach (DependencyProperty attached in new[] { Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty, DockPanel.DockProperty, Canvas.LeftProperty, Canvas.TopProperty })
                wrapper.SetValue(attached, content.GetValue(attached));
            panel.Children.RemoveAt(index);
            wrapper.Children.Add(content);
            var page = new RomMnitePage(s_plugin, model) { Visibility = Visibility.Collapsed, Margin = content.Margin };
            wrapper.Children.Add(page);
            panel.Children.Insert(index, wrapper);

            model.BeginEdit();
            bool saved = false;
            // What the page can change, as it was: put back on Cancel (CancelEdit restores the
            // file, not the settings in use).
            var before = new
            {
                model.ShowUnlockedAchievements,
                model.ShowLockedAchievements,
                model.AutoPlayVideos,
                model.OverlayButton,
                model.OverlayForPcGames,
                model.EmulatorMenusOnStartSelect,
                model.EmulatorsAtDisplayResolution,
                model.SunshineEnabled,
                model.SunshineUrl,
                model.SunshineUser,
                model.SunshinePasswordProtected,
            };

            // Below General.
            var entry = new TreeViewItem { Header = global::RomM.RomM.DisplayName };
            int at_ = tree.Items.Cast<object>().ToList().FindIndex(i => i is TreeViewItem item
                && (string.Equals(HeaderText(item.Header), general, StringComparison.CurrentCultureIgnoreCase) || string.Equals(HeaderText(item.Header), "General", StringComparison.OrdinalIgnoreCase)));
            tree.Items.Insert(at_ + 1, entry);
            tree.SelectedItemChanged += (s, args) =>
            {
                bool ours = ReferenceEquals(args.NewValue, entry);
                page.Visibility = ours ? Visibility.Visible : Visibility.Collapsed;
                content.Visibility = ours ? Visibility.Hidden : Visibility.Visible;
            };

            // Saved with Playnite's own Save (this runs first), put back otherwise.
            save.Click += (s, args) =>
            {
                saved = true;
                try
                {
                    model.EndEdit();
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Settings] Could not save RomMnite's settings: {ex.Message}");
                }
            };
            window.Closed += (s, args) =>
            {
                if (saved)
                    return;
                try
                {
                    model.ShowUnlockedAchievements = before.ShowUnlockedAchievements;
                    model.ShowLockedAchievements = before.ShowLockedAchievements;
                    model.AutoPlayVideos = before.AutoPlayVideos;
                    model.OverlayButton = before.OverlayButton;
                    model.OverlayForPcGames = before.OverlayForPcGames;
                    model.EmulatorMenusOnStartSelect = before.EmulatorMenusOnStartSelect;
                    model.EmulatorsAtDisplayResolution = before.EmulatorsAtDisplayResolution;
                    model.SunshineEnabled = before.SunshineEnabled;
                    model.SunshineUrl = before.SunshineUrl;
                    model.SunshineUser = before.SunshineUser;
                    model.SunshinePasswordProtected = before.SunshinePasswordProtected;
                    model.CancelEdit();
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[Settings] {ex.Message}");
                }
            };
            s_log.Info("[Settings] RomMnite's category added to Playnite's settings.");
            return true;
        }

        private static bool IsSave(Button button)
        {
            string text = (button.Content as string ?? (button.Content as TextBlock)?.Text ?? "").Replace("_", "").Trim();
            if (text.Length == 0)
                return false;
            string localized = ResourceProvider.GetString("LOCSaveLabel");
            return string.Equals(text, "Save", StringComparison.OrdinalIgnoreCase)
                   || (!string.IsNullOrEmpty(localized) && !localized.StartsWith("<!") && string.Equals(text, localized.Replace("_", ""), StringComparison.CurrentCultureIgnoreCase));
        }

        private static bool IsInside(DependencyObject node, DependencyObject ancestor)
        {
            for (DependencyObject at = node; at != null; at = VisualTreeHelper.GetParent(at))
            {
                if (at == ancestor)
                    return true;
            }
            return false;
        }

        private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                DependencyObject node = queue.Dequeue();
                if (node is T found)
                    yield return found;
                int count = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
                for (int i = 0; i < count; i++)
                    queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
