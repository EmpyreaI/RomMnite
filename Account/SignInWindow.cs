using Playnite.SDK;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RomM.Settings;

namespace RomM.Account
{
    /// <summary>
    /// The sign-in prompt shown when the plugin has no server or no credentials. Takes the
    /// server address and either a username and password or an API key, checks them against the
    /// server, and only then stores them in the plugin's settings.
    /// </summary>
    internal static class SignInWindow
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static bool s_open;

        /// <summary>Whether the plugin lacks what it needs to talk to a RomM server.</summary>
        public static bool IsNeeded(SettingsViewModel settings)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.RomMHost))
                return true;

            bool hasLogin = settings.UseBasicAuth
                && !string.IsNullOrEmpty(settings.RomMUsername) && !string.IsNullOrEmpty(settings.RomMPassword);
            bool hasKey = !settings.UseBasicAuth && !string.IsNullOrWhiteSpace(settings.RomMClientToken);
            return !hasLogin && !hasKey;
        }

        /// <summary>Shows the prompt. Returns true when the user signed in.</summary>
        public static bool Show(IPlayniteAPI api, SettingsViewModel settings)
        {
            if (s_open)
                return false;

            s_open = true;
            try
            {
                return ShowCore(api, settings);
            }
            catch (Exception ex)
            {
                s_log.Error(ex, "[SignIn] The sign-in window failed.");
                return false;
            }
            finally
            {
                s_open = false;
            }
        }

        private static bool ShowCore(IPlayniteAPI api, SettingsViewModel settings)
        {
            var window = api.Dialogs.CreateWindow(new WindowCreationOptions { ShowMinimizeButton = false, ShowMaximizeButton = false });
            window.Title = "Sign in to RomM";
            window.Width = 460;
            window.SizeToContent = SizeToContent.Height;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Owner = api.Dialogs.GetCurrentAppWindow();

            var host = new TextBox { Text = settings.RomMHost ?? "" };
            var username = new TextBox { Text = settings.RomMUsername ?? "" };
            var password = new PasswordBox();
            var apiKey = new TextBox();
            var status = Label("", 12);
            status.TextWrapping = TextWrapping.Wrap;
            var signIn = new Button { Content = "Sign in", IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            var later = new Button { Content = "Not now", IsCancel = true, MinWidth = 90 };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(Label("Server address", 0));
            panel.Children.Add(host);
            panel.Children.Add(Label("Username", 12));
            panel.Children.Add(username);
            panel.Children.Add(Label("Password", 12));
            panel.Children.Add(password);
            panel.Children.Add(Label("Or, in place of a username and password, an API key", 20));
            panel.Children.Add(apiKey);
            panel.Children.Add(status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(later);
            buttons.Children.Add(signIn);
            panel.Children.Add(buttons);
            window.Content = panel;

            bool signedIn = false;
            signIn.Click += async (s, e) =>
            {
                string address = (host.Text ?? "").Trim().TrimEnd('/');
                string user = (username.Text ?? "").Trim();
                string pass = password.Password ?? "";
                string key = (apiKey.Text ?? "").Trim();
                bool useKey = key.Length > 0;

                if (address.Length == 0)
                {
                    status.Text = "Enter the server address, for example https://romm.example.com";
                    return;
                }
                if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    address = "https://" + address;
                if (!useKey && (user.Length == 0 || pass.Length == 0))
                {
                    status.Text = "Enter a username and password, or an API key.";
                    return;
                }

                signIn.IsEnabled = false;
                status.Text = "Signing in...";
                string problem;
                try
                {
                    var auth = useKey
                        ? new AuthenticationHeaderValue("Bearer", key)
                        : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}")));
                    problem = await Task.Run(() => Check(address, auth));
                }
                catch (Exception ex)
                {
                    problem = ex.Message;
                }

                if (problem != null)
                {
                    status.Text = problem;
                    signIn.IsEnabled = true;
                    return;
                }

                // Only now are the details kept, and the plugin's own connection switched over.
                settings.RomMHost = address;
                settings.UseBasicAuth = !useKey;
                if (useKey)
                {
                    settings.RomMClientToken = key;
                    HttpClientSingleton.ConfigureAPIAuth(key);
                }
                else
                {
                    settings.RomMUsername = user;
                    settings.RomMPassword = pass;
                    HttpClientSingleton.ConfigureBasicAuth(user, pass);
                }
                settings.Persist();

                signedIn = true;
                window.Close();
            };
            later.Click += (s, e) => window.Close();

            window.ShowDialog();
            return signedIn;
        }

        // Asks the server who these credentials belong to. Null when they are accepted, otherwise
        // a sentence saying what went wrong. Uses its own connection so a failed attempt never
        // disturbs the one the rest of the plugin is using.
        private static string Check(string address, AuthenticationHeaderValue auth)
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                using (var request = new HttpRequestMessage(HttpMethod.Get, address + "/api/users/me"))
                {
                    request.Headers.Authorization = auth;
                    using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                    {
                        int code = (int)response.StatusCode;
                        if (response.IsSuccessStatusCode)
                            return null;
                        if (code == 401 || code == 403)
                            return "The server did not accept those details. Check them and try again." +
                                   (auth.Scheme == "Bearer" ? " An API key also needs the \"me.read\" permission." : "");
                        return $"The server answered with an error ({code}). Check the address.";
                    }
                }
            }
            catch (Exception ex)
            {
                s_log.Warn($"[SignIn] Could not reach {address}: {ex.GetBaseException().Message}");
                return "Could not reach the server. Check the address and that the server is running.";
            }
        }

        private static TextBlock Label(string text, double top)
        {
            var label = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 4) };
            label.SetResourceReference(FrameworkElement.StyleProperty, "BaseTextBlockStyle");
            return label;
        }
    }
}
