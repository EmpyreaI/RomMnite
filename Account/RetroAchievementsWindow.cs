using Playnite.SDK;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RomM.Account
{
    /// <summary>
    /// Signing in to RetroAchievements, in the sign-in window's look: the name is the one set in
    /// the RomM profile (shown, not typed, so the achievements cannot end up on someone else's
    /// account), the password is asked for once and not kept.
    /// </summary>
    public static class RetroAchievementsWindow
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Brush Back = new LinearGradientBrush(Color.FromRgb(0x23, 0x26, 0x2E), Color.FromRgb(0x14, 0x16, 0x1C), 90);
        private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x32, 0x35, 0x3C));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xAF));
        private static readonly Brush Problem = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E));

        /// <summary>Shows the window; true when it signed in. On the UI thread.</summary>
        public static bool Show(RetroAchievementsSignIn signIn)
        {
            bool done = false;
            try
            {
                var window = new Window
                {
                    Width = 400,
                    SizeToContent = SizeToContent.Height,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = Back,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Title = "Sign in to RetroAchievements",
                };
                Window owner = Application.Current?.MainWindow;
                if (owner != null && owner.IsVisible)
                    window.Owner = owner;
                else
                    window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                var page = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
                page.Children.Add(new TextBlock { Text = "Sign in to RetroAchievements", Foreground = Brushes.White, FontSize = 20, Margin = new Thickness(0, 0, 0, 14) });
                page.Children.Add(new TextBlock
                {
                    Text = "Your achievements are shown, and earned in your emulators, as:",
                    Foreground = Muted,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
                page.Children.Add(new TextBlock { Text = signIn.RomMUsername, Foreground = Brushes.White, FontSize = 16, Margin = new Thickness(0, 4, 0, 2) });
                page.Children.Add(new TextBlock
                {
                    Text = "From your RomM profile. To use another account, change it there.",
                    Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xAF, 0xAF, 0xAF)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 16),
                });

                page.Children.Add(new TextBlock { Text = "PASSWORD", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
                var password = new PasswordBox
                {
                    Height = 36,
                    Padding = new Thickness(10, 0, 10, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Field,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontSize = 14,
                    CaretBrush = Brushes.White,
                };
                page.Children.Add(password);
                page.Children.Add(new TextBlock
                {
                    Text = "Only a sign-in token is kept, for this Windows user; never the password.",
                    Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xAF, 0xAF, 0xAF)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });

                var problem = new TextBlock { Foreground = Problem, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
                page.Children.Add(problem);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
                Border cancel = MakeButton("Cancel", false);
                Border go = MakeButton("Sign in", true);
                cancel.Margin = new Thickness(0, 0, 10, 0);
                buttons.Children.Add(cancel);
                buttons.Children.Add(go);
                page.Children.Add(buttons);
                window.Content = page;

                bool busy = false;
                Action submit = () =>
                {
                    if (busy)
                        return;
                    busy = true;
                    string typed = password.Password;
                    password.IsEnabled = false;
                    ((TextBlock)go.Child).Text = "Signing in...";
                    problem.Visibility = Visibility.Collapsed;
                    Task.Run(() =>
                    {
                        bool ok = signIn.SignIn(typed, out string why);
                        window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            busy = false;
                            if (ok)
                            {
                                done = true;
                                window.Close();
                                return;
                            }
                            password.IsEnabled = true;
                            password.Clear();
                            password.Focus();
                            ((TextBlock)go.Child).Text = "Sign in";
                            problem.Text = why;
                            problem.Visibility = Visibility.Visible;
                        }));
                    });
                };

                go.MouseLeftButtonUp += (s, e) => submit();
                cancel.MouseLeftButtonUp += (s, e) => window.Close();
                window.PreviewKeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        submit();
                    }
                    else if (e.Key == Key.Escape)
                    {
                        e.Handled = true;
                        window.Close();
                    }
                };
                page.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.OriginalSource == page)
                        window.DragMove();
                };
                window.Loaded += (s, e) => password.Focus();
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                s_log.Error(ex, "[RetroAchievements] The sign-in window failed.");
            }
            return done;
        }

        private static Border MakeButton(string text, bool primary)
        {
            Brush normal = primary
                ? (Brush)new LinearGradientBrush(Color.FromRgb(0x06, 0xBF, 0xFF), Color.FromRgb(0x2D, 0x73, 0xFF), 0)
                : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            Brush lit = primary
                ? (Brush)new LinearGradientBrush(Color.FromRgb(0x3C, 0xD0, 0xFF), Color.FromRgb(0x4D, 0x8A, 0xFF), 0)
                : new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
            var button = new Border
            {
                Background = normal,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(18, 8, 18, 8),
                MinWidth = 96,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center },
            };
            button.MouseEnter += (s, e) => button.Background = lit;
            button.MouseLeave += (s, e) => button.Background = normal;
            return button;
        }
    }
}
