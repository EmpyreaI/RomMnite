using Playnite.SDK.Controls;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using RomM.Settings;

namespace RomM.Account
{
    /// <summary>
    /// The account badge as a theme element: the signed-in user's picture and name, with the
    /// account menu behind a click. A theme places it with
    /// &lt;ContentControl x:Name="RomM_AccountBadge" /&gt;, in the main window or library view.
    /// </summary>
    public class AccountBadgeControl : PluginUserControl
    {
        private readonly AccountBadge _badge = new AccountBadge();
        private readonly Border _frame;

        /// <param name="activated">Run on a click, with whether someone is signed in.</param>
        public AccountBadgeControl(Action<AccountBadgeControl> activated)
        {
            _badge.UseBarLayout();
            _frame = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Child = _badge,
                Cursor = Cursors.Hand,
            };
            Content = _frame;
            Focusable = false;
            VerticalAlignment = VerticalAlignment.Center;

            // The title strip is part of the area Windows treats as the window's drag handle;
            // without this, clicks there would move the window instead of reaching the badge.
            WindowChrome.SetIsHitTestVisibleInChrome(this, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_frame, true);

            _frame.MouseEnter += (s, e) => _frame.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            _frame.MouseLeave += (s, e) => _frame.Background = Brushes.Transparent;
            _frame.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                activated?.Invoke(this);
            };
        }

        public bool SignedIn => _badge.SignedIn;

        internal void Attach(SettingsViewModel settings) => _badge.Attach(settings);

        public void Refresh() => _badge.Refresh();

        public void ShowMenu(Action signOut) => _badge.ShowMenu(signOut);
    }
}
