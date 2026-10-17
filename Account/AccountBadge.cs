using Playnite.SDK;
using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RomM.Settings;

namespace RomM.Account
{
    /// <summary>
    /// The signed-in user's picture and name, shown in Playnite's top bar. Clicking it opens a
    /// menu (sign out, for now); when nobody is signed in it reads "Sign in" and opens the
    /// sign-in window.
    /// </summary>
    internal sealed class AccountBadge : StackPanel
    {
        private const double AvatarSize = 22;

        private readonly Image _avatar;
        private readonly TextBlock _name;
        private readonly TextBlock _chevron;
        private SettingsViewModel _settings;

        public AccountBadge()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            Background = Brushes.Transparent;

            _avatar = new Image
            {
                Width = AvatarSize,
                Height = AvatarSize,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 8, 0),
                Clip = new RectangleGeometry(new Rect(0, 0, AvatarSize, AvatarSize), 3, 3),
            };
            RenderOptions.SetBitmapScalingMode(_avatar, BitmapScalingMode.Fant);

            _name = Text("Sign in");
            _chevron = Text("\u25BE");
            _chevron.Margin = new Thickness(6, 0, 0, 0);
            _chevron.FontSize = 10;

            Children.Add(_avatar);
            Children.Add(_name);
            Children.Add(_chevron);
        }

        private static TextBlock Text(string text)
        {
            var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        /// <summary>Whether someone is signed in, going by the stored settings.</summary>
        public bool SignedIn => _settings != null && !SignInWindow.IsNeeded(_settings);

        /// <summary>Starts following the plugin's settings. Called once they exist.</summary>
        public void Attach(SettingsViewModel settings)
        {
            if (_settings != null)
                _settings.PropertyChanged -= OnSettingsChanged;

            _settings = settings;
            if (_settings != null)
                _settings.PropertyChanged += OnSettingsChanged;

            Refresh();
        }

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            // Settings are also changed from background work (fetching the profile).
            Dispatcher.BeginInvoke(new Action(Refresh));
        }

        public void Refresh()
        {
            try
            {
                bool signedIn = SignedIn;
                string user = _settings?.RomMUser;
                bool named = !string.IsNullOrWhiteSpace(user) && user.Trim('-').Length > 0;

                _name.Text = !signedIn ? "Sign in" : named ? user : "RomM";
                _chevron.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

                ImageSource picture = signedIn ? Load(_settings?.ProfilePath) : null;
                _avatar.Source = picture;
                _avatar.Visibility = picture != null ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn($"[Account] Could not refresh the account display: {ex.Message}");
            }
        }

        // Read fully into memory so the picture file is not held open: it is replaced whenever
        // the profile is fetched again.
        private static ImageSource Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = 64;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Lays the badge out for a theme's title strip: a little larger, with breathing room.</summary>
        public void UseBarLayout()
        {
            Margin = new Thickness(10, 4, 10, 4);
        }

        /// <summary>Opens the account menu just under the badge.</summary>
        public void ShowMenu(Action signOut)
        {
            var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom };

            var header = new MenuItem { Header = $"Signed in as {_name.Text}", IsEnabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new Separator());

            // Further account options go here.
            var signOutItem = new MenuItem { Header = "Sign out..." };
            signOutItem.Click += (s, e) => signOut();
            menu.Items.Add(signOutItem);

            menu.IsOpen = true;
        }
    }
}
