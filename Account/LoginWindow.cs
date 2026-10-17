using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RomM.Account
{
    /// <summary>What the sign-in window settled on.</summary>
    internal sealed class LoginResult
    {
        public string Host;
        public string Username;      // "API key" for a key
        public bool UsesKey;
        public string Secret;        // the password or key
        public bool Remember;
        public bool Verified;        // the server accepted it just now
        public CachedAccount Account; // the saved account used, if any
        public ServerProfile Profile; // the server's name and picture for the user, when it was reached
    }

    /// <summary>
    /// The sign-in window, after Steam's: required (closing it quits Playnite), dark, with the
    /// accounts that have signed in here as tiles ("Who's playing?") and a form for a new one.
    /// A new sign-in is always checked with the server; a saved account signs in offline when
    /// the server cannot be reached, if the server accepted it before.
    /// </summary>
    internal static class LoginWindow
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Brush Back = new LinearGradientBrush(Color.FromRgb(0x23, 0x26, 0x2E), Color.FromRgb(0x14, 0x16, 0x1C), 90);
        private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x32, 0x35, 0x3C));
        private static readonly Brush FieldLit = new SolidColorBrush(Color.FromRgb(0x39, 0x3C, 0x44));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xAF));
        private static readonly Brush Problem = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E));
        private static bool s_open;

        internal enum Answer { Accepted, Rejected, Unreachable, Error }

        /// <summary>
        /// Shows the window until someone signs in. Returns null when the window was closed,
        /// which the caller takes as quitting.
        /// </summary>
        public static LoginResult Show(AccountCache accounts, string message = null, CachedAccount prefill = null, string knownHost = null)
        {
            if (s_open)
                return null;
            s_open = true;
            try
            {
                return new Session(accounts, message, prefill, knownHost).Run();
            }
            catch (Exception ex)
            {
                s_log.Error(ex, "[SignIn] The sign-in window failed.");
                return null;
            }
            finally
            {
                s_open = false;
            }
        }

        // ---- Asking the server ------------------------------------------------------------------

        public static AuthenticationHeaderValue AuthFor(bool usesKey, string username, string secret)
        {
            return usesKey
                ? new AuthenticationHeaderValue("Bearer", secret)
                : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{secret}")));
        }

        /// <summary>
        /// Asks the server who these credentials belong to (its own connection, so a failed
        /// attempt never disturbs the plugin's). The message says what went wrong, if anything.
        /// </summary>
        public static Answer Check(string address, AuthenticationHeaderValue auth, out string message)
        {
            return Check(address, auth, out message, out ServerProfile _);
        }

        /// <summary>
        /// As Check, and when accepted also what the server says of the user: their name and
        /// their profile picture (RomM's own, not one made up here).
        /// </summary>
        public static Answer Check(string address, AuthenticationHeaderValue auth, out string message, out ServerProfile profile)
        {
            message = null;
            profile = null;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                using (var request = new HttpRequestMessage(HttpMethod.Get, address.TrimEnd('/') + "/api/users/me"))
                {
                    request.Headers.Authorization = auth;
                    using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                    {
                        int code = (int)response.StatusCode;
                        if (response.IsSuccessStatusCode)
                        {
                            profile = Profile(client, address.TrimEnd('/'), auth, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                            return Answer.Accepted;
                        }
                        if (code == 401 || code == 403)
                        {
                            message = "The server did not accept those details. Check them and try again." +
                                      (auth.Scheme == "Bearer" ? " An API key also needs the \"me.read\" permission." : "");
                            return Answer.Rejected;
                        }
                        message = $"The server answered with an error ({code}). Check the address.";
                        return Answer.Error;
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[SignIn] Could not reach {address}: {ex.GetBaseException().Message}");
                message = "Could not reach the server. Check the address and that the server is running.";
                return Answer.Unreachable;
            }
        }

        // The user's name and picture from the server's answer about them. The picture is asked
        // for where RomM 5 serves it, else from its assets (older servers); none if neither.
        private static ServerProfile Profile(HttpClient client, string address, AuthenticationHeaderValue auth, string body)
        {
            var profile = new ServerProfile();
            try
            {
                var me = Newtonsoft.Json.Linq.JObject.Parse(body);
                profile.DisplayName = (string)me["username"];
                int id = (int?)me["id"] ?? 0;
                string avatarPath = (string)me["avatar_path"];
                if (string.IsNullOrEmpty(avatarPath))
                    return profile;
                foreach (string url in new[] { $"{address}/api/users/{id}/avatar", $"{address}/api/raw/assets/{avatarPath.TrimStart('/')}" })
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.Authorization = auth;
                        using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                        {
                            if (!response.IsSuccessStatusCode)
                                continue;
                            byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                            if (bytes.Length > 0)
                            {
                                profile.Avatar = bytes;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Debug($"[SignIn] Could not read the user's profile: {ex.Message}");
            }
            return profile;
        }

        // ---- The window ---------------------------------------------------------------------------

        private sealed class Session
        {
            private readonly AccountCache _accounts;
            private readonly Window _window;
            private readonly Grid _pages = new Grid();
            private LoginResult _result;
            private string _host;   // the server chosen (or last used)

            public Session(AccountCache accounts, string message, CachedAccount prefill, string knownHost)
            {
                _accounts = accounts;
                _host = string.IsNullOrWhiteSpace(knownHost) ? accounts.All().FirstOrDefault()?.Host : knownHost.Trim().TrimEnd('/');
                _window = new Window
                {
                    Title = "Sign in to RomM",
                    Width = 600,
                    SizeToContent = SizeToContent.Height,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                Window owner = Application.Current?.MainWindow;
                if (owner != null && owner.IsVisible)
                {
                    _window.Owner = owner;
                    _window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }

                var frame = new Border
                {
                    Background = Back,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(16),
                    Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.7, Direction = 270 },
                };
                var body = new DockPanel();
                body.Children.Add(TitleBar());
                body.Children.Add(_pages);
                frame.Child = body;
                _window.Content = frame;

                var saved = _accounts.All();
                // Never connected: the server first. A saved account to sign in again: its form.
                // Otherwise the accounts.
                if (prefill != null)
                    ShowForm(prefill, message);
                else if (saved.Count == 0 && string.IsNullOrEmpty(_host))
                    ShowServer(message);
                else if (saved.Count == 0)
                    ShowForm(null, message);
                else
                    ShowPicker(saved, message);
            }

            public LoginResult Run()
            {
                _window.ShowDialog();
                return _result;
            }

            private void Done(LoginResult result)
            {
                _result = result;
                _window.Close();
            }

            private UIElement TitleBar()
            {
                var bar = new DockPanel { Background = Brushes.Transparent, Height = 34 };
                DockPanel.SetDock(bar, Dock.Top);
                var close = new Border { Width = 40, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Quit Playnite" };
                close.Child = new TextBlock
                {
                    Text = "\uE8BB",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 11,
                    Foreground = Muted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                close.MouseEnter += (s, e) => close.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
                close.MouseLeave += (s, e) => close.Background = Brushes.Transparent;
                // Its own press: the title bar drags the window on a press, which would swallow the click.
                close.MouseLeftButtonDown += (s, e) => e.Handled = true;
                close.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    _window.Close();
                };
                DockPanel.SetDock(close, Dock.Right);
                bar.Children.Add(close);
                bar.Children.Add(new TextBlock { Text = "RomM", Foreground = Muted, FontSize = 12, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                bar.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ButtonState == MouseButtonState.Pressed)
                    {
                        try { _window.DragMove(); } catch { }
                    }
                };
                return bar;
            }

            private void Show(UIElement page)
            {
                _pages.Children.Clear();
                _pages.Children.Add(page);
            }

            // ---- Who's playing? ------------------------------------------------------------------

            private void ShowPicker(List<CachedAccount> saved, string message)
            {
                var page = new StackPanel { Margin = new Thickness(36, 8, 36, 32) };
                page.Children.Add(new TextBlock { Text = "Who's playing?", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 20) });
                var status = StatusLine(message);

                var tiles = new WrapPanel();
                foreach (CachedAccount account in saved)
                    tiles.Children.Add(AccountTile(account, status));
                tiles.Children.Add(AddTile());
                page.Children.Add(tiles);
                page.Children.Add(status);
                Show(page);
            }

            private UIElement AccountTile(CachedAccount account, TextBlock status)
            {
                var face = Avatar(account.Label, 72, account.AvatarFile);
                var name = new TextBlock { Text = account.Label, Foreground = Brushes.White, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 10, 0, 0) };
                var server = new TextBlock { Text = account.ServerLabel, Foreground = Muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var stack = new StackPanel { Width = 116 };
                stack.Children.Add(face);
                stack.Children.Add(name);
                stack.Children.Add(server);

                var remove = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x10, 0x14)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Cursor = Cursors.Hand,
                    ToolTip = "Remove this account from this computer",
                    Visibility = Visibility.Hidden,
                    Child = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                var holder = new Grid();
                holder.Children.Add(stack);
                holder.Children.Add(remove);
                var tile = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = holder };
                tile.MouseEnter += (s, e) => { tile.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)); remove.Visibility = Visibility.Visible; };
                tile.MouseLeave += (s, e) => { tile.Background = Brushes.Transparent; remove.Visibility = Visibility.Hidden; };
                remove.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    _accounts.Remove(account.Id);
                    var left = _accounts.All();
                    if (left.Count == 0)
                        ShowForm(null, null);
                    else
                        ShowPicker(left, null);
                };
                tile.MouseLeftButtonUp += async (s, e) =>
                {
                    string secret = _accounts.SecretOf(account);
                    if (string.IsNullOrEmpty(secret))
                    {
                        // Not remembered: the password again.
                        ShowForm(account, null);
                        return;
                    }
                    tile.IsEnabled = false;
                    status.Foreground = Muted;
                    status.Text = $"Signing in as {account.Label}...";
                    string problem = null;
                    ServerProfile profile = null;
                    Answer answer = await Task.Run(() => Check(account.Host, AuthFor(account.UsesKey, account.Username, secret), out problem, out profile));
                    tile.IsEnabled = true;
                    if (answer == Answer.Accepted || (answer == Answer.Unreachable && account.VerifiedUtc > DateTime.MinValue))
                    {
                        Done(new LoginResult
                        {
                            Host = account.Host,
                            Username = account.Username,
                            UsesKey = account.UsesKey,
                            Secret = secret,
                            Remember = true,
                            Verified = answer == Answer.Accepted,
                            Account = account,
                            Profile = profile,
                        });
                        return;
                    }
                    if (answer == Answer.Rejected)
                    {
                        ShowForm(account, "The server no longer accepts the saved sign-in for this account. Sign in again.");
                        return;
                    }
                    status.Foreground = Problem;
                    status.Text = problem;
                };
                return tile;
            }

            private UIElement AddTile()
            {
                var plus = new Border
                {
                    Width = 72,
                    Height = 72,
                    CornerRadius = new CornerRadius(36),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1.5),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock { Text = "+", FontSize = 30, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) },
                };
                var stack = new StackPanel { Width = 116 };
                stack.Children.Add(plus);
                stack.Children.Add(new TextBlock { Text = "Add account", Foreground = Muted, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
                var tile = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = stack };
                tile.MouseEnter += (s, e) => tile.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
                tile.MouseLeftButtonUp += (s, e) => ShowForm(null, null);
                return tile;
            }

            // The user's RomM profile picture; without one, the name's first letter on a colour.
            private static UIElement Avatar(string name, double size, string pictureFile = null)
            {
                if (!string.IsNullOrEmpty(pictureFile) && System.IO.File.Exists(pictureFile))
                {
                    try
                    {
                        var picture = new System.Windows.Media.Imaging.BitmapImage();
                        picture.BeginInit();
                        picture.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // the file is not held open
                        picture.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                        picture.DecodePixelWidth = (int)(size * 2);
                        picture.UriSource = new Uri(pictureFile);
                        picture.EndInit();
                        picture.Freeze();
                        return new Border
                        {
                            Width = size,
                            Height = size,
                            CornerRadius = new CornerRadius(4),
                            Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
                            HorizontalAlignment = HorizontalAlignment.Center,
                        };
                    }
                    catch (Exception ex)
                    {
                        s_log.Debug($"[SignIn] Could not show the profile picture: {ex.Message}");
                    }
                }

                string letter = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();
                int hash = 0;
                foreach (char c in name ?? "")
                    hash = hash * 31 + c;
                var hues = new[] { Color.FromRgb(0x2D, 0x73, 0xFF), Color.FromRgb(0x5C, 0xB8, 0x1D), Color.FromRgb(0xC2, 0x4E, 0xD8), Color.FromRgb(0xE0, 0x8A, 0x1E), Color.FromRgb(0x1A, 0x9F, 0xFF), Color.FromRgb(0xD8, 0x4E, 0x5E) };
                Color hue = hues[Math.Abs(hash % hues.Length)];
                return new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(4),
                    Background = new LinearGradientBrush(hue, Color.FromRgb((byte)(hue.R / 2), (byte)(hue.G / 2), (byte)(hue.B / 2)), 45),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock { Text = letter, FontSize = size * 0.45, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
            }

            // ---- Which server ------------------------------------------------------------------------

            /// <summary>
            /// The server's address, before any sign-in. Shown first, and required, on a Playnite
            /// that has never connected to RomM; later reached from the sign-in page's "Change".
            /// Next checks that a RomM server answers there.
            /// </summary>
            private void ShowServer(string message)
            {
                bool firstTime = string.IsNullOrEmpty(_host) && _accounts.All().Count == 0;
                var page = new StackPanel { Margin = new Thickness(36, 8, 36, 30) };
                // Back: to signing in, when a server was already chosen.
                page.Children.Add(Heading(firstTime ? "You haven't connected to a RomM instance yet!" : "Connect to a RomM server",
                    string.IsNullOrEmpty(_host) ? null : (Action)(() => ShowForm(null, null))));
                page.Children.Add(new TextBlock
                {
                    Text = "Enter the address of your RomM server: its URL, or its IP address and port.",
                    Foreground = Muted,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                });

                var address = Input(_host ?? "");
                page.Children.Add(Caption("SERVER ADDRESS", true));
                page.Children.Add(address);
                page.Children.Add(new TextBlock { Text = "For example https://romm.example.com or 192.168.1.20:8080", Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xAF, 0xAF, 0xAF)), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });

                var status = StatusLine(message);
                var next = BigButton("Next");
                next.Margin = new Thickness(0, 22, 0, 0);
                page.Children.Add(next);
                page.Children.Add(status);

                Func<System.Threading.Tasks.Task> go = async () =>
                {
                    string typed = (address.Text ?? "").Trim().TrimEnd('/');
                    status.Foreground = Problem;
                    if (typed.Length == 0)
                    {
                        status.Text = "Enter the server's address.";
                        return;
                    }
                    if (!typed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !typed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        // A bare IP address is most likely a server on the local network, without TLS.
                        bool looksLocal = System.Text.RegularExpressions.Regex.IsMatch(typed, @"^\d{1,3}(\.\d{1,3}){3}(:\d+)?(/.*)?$") || typed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
                        typed = (looksLocal ? "http://" : "https://") + typed;
                    }
                    next.IsEnabled = false;
                    status.Foreground = Muted;
                    status.Text = "Connecting...";
                    string problem = null;
                    bool found = await System.Threading.Tasks.Task.Run(() => IsRomM(typed, out problem));
                    next.IsEnabled = true;
                    if (!found)
                    {
                        status.Foreground = Problem;
                        status.Text = problem;
                        return;
                    }
                    _host = typed;
                    ShowForm(null, null);
                };
                next.MouseLeftButtonUp += async (s, e) => await go();
                address.KeyDown += async (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        await go();
                    }
                };

                Show(page);
                _window.Dispatcher.BeginInvoke(new Action(() => address.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            }

            // Whether a RomM server answers at the address (its open heartbeat, which needs no sign-in).
            private static bool IsRomM(string address, out string problem)
            {
                problem = null;
                try
                {
                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                    using (var response = client.GetAsync(address + "/api/heartbeat").GetAwaiter().GetResult())
                    {
                        if (response.IsSuccessStatusCode)
                            return true;
                        problem = $"Something answered at that address, but not as a RomM server ({(int)response.StatusCode}). Check the address and port.";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[SignIn] Could not reach {address}: {ex.GetBaseException().Message}");
                    problem = "Could not reach a server at that address. Check it, and that the server is running.";
                    return false;
                }
            }

            // A page's title, with a back arrow before it when there is somewhere to go back to.
            private static UIElement Heading(string title, Action back)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
                if (back != null)
                {
                    var arrow = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(-8, 0, 6, 0), ToolTip = "Back", VerticalAlignment = VerticalAlignment.Center };
                    arrow.Child = new TextBlock { Text = "\uE72B", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    arrow.MouseEnter += (s, e) => arrow.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                    arrow.MouseLeave += (s, e) => arrow.Background = Brushes.Transparent;
                    arrow.MouseLeftButtonUp += (s, e) => back();
                    DockPanel.SetDock(arrow, Dock.Left);
                    row.Children.Add(arrow);
                }
                row.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                return row;
            }

            // ---- Sign in --------------------------------------------------------------------------

            /// <summary>
            /// Signing in to the chosen server: an account name and password (or an API key) and
            /// "Remember me". The back arrow returns to the accounts, or, with none saved, to the
            /// server's address; "Change" there too. A sign-in that cannot reach the server says
            /// so and offers the way back to the address.
            /// </summary>
            private void ShowForm(CachedAccount prefill, string message)
            {
                if (prefill != null)
                    _host = prefill.Host;
                if (string.IsNullOrEmpty(_host))
                {
                    ShowServer(message);
                    return;
                }

                var page = new StackPanel { Margin = new Thickness(36, 8, 36, 30) };
                Action back = _accounts.All().Count > 0
                    ? (Action)(() => ShowPicker(_accounts.All(), null))
                    : () => ShowServer(null);
                page.Children.Add(Heading("Sign in", back));

                // The server, and the way to another.
                var serverRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
                var change = Link("Change");
                change.MouseLeftButtonUp += (s, e) => ShowServer(null);
                DockPanel.SetDock(change, Dock.Right);
                serverRow.Children.Add(change);
                string shown;
                try { shown = new Uri(_host).Authority; } catch { shown = _host; }
                serverRow.Children.Add(new TextBlock { Text = "Server: " + shown, Foreground = Muted, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
                page.Children.Add(serverRow);

                var user = Input(prefill != null && !prefill.UsesKey ? prefill.Username : "");
                var pass = new PasswordBox { Height = 36, Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center, Background = Field, Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 14, CaretBrush = Brushes.White };
                var key = Input("");
                bool usingKey = prefill != null && prefill.UsesKey;

                var userBlock = new StackPanel();
                userBlock.Children.Add(Caption("SIGN IN WITH ACCOUNT NAME", true));
                userBlock.Children.Add(user);
                userBlock.Children.Add(Caption("PASSWORD", false));
                userBlock.Children.Add(pass);
                var keyBlock = new StackPanel();
                keyBlock.Children.Add(Caption("SIGN IN WITH AN API KEY", true));
                keyBlock.Children.Add(key);
                page.Children.Add(userBlock);
                page.Children.Add(keyBlock);

                var remember = new CheckBox { IsChecked = true, Margin = new Thickness(0, 14, 0, 0), Foreground = Muted, Cursor = Cursors.Hand };
                remember.Content = new TextBlock { Text = "Remember me", Foreground = Muted, FontSize = 13 };
                page.Children.Add(remember);

                var status = StatusLine(message);
                var signIn = BigButton("Sign in");
                signIn.Margin = new Thickness(0, 18, 0, 0);
                page.Children.Add(signIn);
                page.Children.Add(status);
                // Shown when the server could not be reached: back to its address.
                var toServer = Link("Check the server address");
                toServer.Margin = new Thickness(0, 6, 0, 0);
                toServer.Visibility = Visibility.Collapsed;
                toServer.MouseLeftButtonUp += (s, e) => ShowServer(null);
                page.Children.Add(toServer);

                var links = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
                var swap = Link("");
                Action paintMode = () =>
                {
                    userBlock.Visibility = usingKey ? Visibility.Collapsed : Visibility.Visible;
                    keyBlock.Visibility = usingKey ? Visibility.Visible : Visibility.Collapsed;
                    swap.Text = usingKey ? "Use an account name and password instead" : "Use an API key instead";
                };
                swap.MouseLeftButtonUp += (s, e) => { usingKey = !usingKey; paintMode(); };
                paintMode();
                DockPanel.SetDock(swap, Dock.Left);
                links.Children.Add(swap);
                page.Children.Add(links);

                page.Children.Add(new TextBlock
                {
                    Text = "The server checks your details the first time you sign in. After that, a remembered account signs in by itself, even when the server can't be reached.",
                    Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xAF, 0xAF, 0xAF)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 18, 0, 0),
                });

                Func<Task> attempt = async () =>
                {
                    string address = _host;
                    string name = usingKey ? "API key" : (user.Text ?? "").Trim();
                    string secret = usingKey ? (key.Text ?? "").Trim() : (pass.Password ?? "");
                    status.Foreground = Problem;
                    toServer.Visibility = Visibility.Collapsed;
                    if (name.Length == 0 || secret.Length == 0)
                    {
                        status.Text = usingKey ? "Enter an API key." : "Enter your account name and password.";
                        return;
                    }

                    signIn.IsEnabled = false;
                    status.Foreground = Muted;
                    status.Text = "Signing in...";
                    string problem = null;
                    ServerProfile profile = null;
                    bool key2 = usingKey;
                    Answer answer = await Task.Run(() => Check(address, AuthFor(key2, name, secret), out problem, out profile));
                    signIn.IsEnabled = true;
                    if (answer != Answer.Accepted)
                    {
                        // A first sign-in must reach the server; there is nothing to check it against otherwise.
                        status.Foreground = Problem;
                        status.Text = problem;
                        if (answer == Answer.Unreachable || answer == Answer.Error)
                            toServer.Visibility = Visibility.Visible;
                        return;
                    }
                    Done(new LoginResult { Host = address, Username = name, UsesKey = key2, Secret = secret, Remember = remember.IsChecked == true, Verified = true, Profile = profile });
                };
                signIn.MouseLeftButtonUp += async (s, e) => await attempt();
                KeyEventHandler enter = async (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        await attempt();
                    }
                };
                user.KeyDown += enter;
                pass.KeyDown += enter;
                key.KeyDown += enter;

                Show(page);
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (usingKey)
                        key.Focus();
                    else if (user.Text.Length == 0)
                        user.Focus();
                    else
                        pass.Focus();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }

            private static TextBox Input(string text)
            {
                var box = new TextBox
                {
                    Text = text,
                    Height = 36,
                    Padding = new Thickness(8, 0, 8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Field,
                    Foreground = Brushes.White,
                    CaretBrush = Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontSize = 14,
                };
                box.MouseEnter += (s, e) => box.Background = FieldLit;
                box.MouseLeave += (s, e) => box.Background = Field;
                return box;
            }

            private static TextBlock Caption(string text, bool blue)
            {
                return new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = blue ? Blue : Muted, Margin = new Thickness(0, 14, 0, 6) };
            }

            private static TextBlock StatusLine(string message)
            {
                return new TextBlock { Text = message ?? "", Foreground = Problem, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            }

            private static TextBlock Link(string text)
            {
                var link = new TextBlock { Text = text, Foreground = Muted, FontSize = 12, Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline };
                link.MouseEnter += (s, e) => link.Foreground = Brushes.White;
                link.MouseLeave += (s, e) => link.Foreground = Muted;
                return link;
            }

            private static Border BigButton(string label)
            {
                var normal = new LinearGradientBrush(Color.FromRgb(0x06, 0xBF, 0xFF), Color.FromRgb(0x2D, 0x73, 0xFF), 0);
                var lit = new LinearGradientBrush(Color.FromRgb(0x3C, 0xD0, 0xFF), Color.FromRgb(0x4D, 0x8A, 0xFF), 0);
                var button = new Border
                {
                    Height = 44,
                    CornerRadius = new CornerRadius(2),
                    Background = normal,
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = label, FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                button.MouseEnter += (s, e) => button.Background = lit;
                button.MouseLeave += (s, e) => button.Background = normal;
                button.IsEnabledChanged += (s, e) => button.Opacity = button.IsEnabled ? 1 : 0.5;
                return button;
            }
        }
    }
}
