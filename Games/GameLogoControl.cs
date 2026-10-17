using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RomM.Games
{
    /// <summary>
    /// A game's logo, for the banner of a theme's details view. A theme places it with
    /// &lt;ContentControl x:Name="RomM_GameLogo" /&gt;.
    ///
    /// The logo is the Logo.png the importer saves for each game under Playnite's ExtraMetadata
    /// folder -- the location the ExtraMetadataLoader extension established, so logos fetched by
    /// it earlier keep working and nothing has to be moved.
    /// </summary>
    public class GameLogoControl : PluginUserControl
    {
        // Every logo is scaled, up or down, to fit this box, so wide and tall logos, large and
        // small files, all end up a comparable size. Change these two numbers to resize them all.
        private const double LogoBoxWidth = 440;
        private const double LogoBoxHeight = 170;

        // Pixels more transparent than this count as empty when trimming a logo's margins.
        private const byte VisibleAlpha = 16;

        private readonly IPlayniteAPI _api;
        private readonly Image _image;

        public GameLogoControl(IPlayniteAPI api)
        {
            _api = api;
            _image = new Image
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Fant);

            // The box sits at the top left of whatever area the theme gives the logo; the theme's
            // own margins keep it off the very corner. The logo fills the box from that corner.
            Content = new Grid
            {
                Width = LogoBoxWidth,
                Height = LogoBoxHeight,
                Children = { _image },
            };
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            Focusable = false;
            IsHitTestVisible = false;
            Visibility = Visibility.Collapsed;
        }

        /// <summary>Where a game's logo is kept, whether or not it exists.</summary>
        public static string LogoPath(IPlayniteAPI api, Guid gameId)
        {
            return Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", gameId.ToString(), "Logo.png");
        }

        /// <summary>
        /// Crops away the empty space around a logo. Logo files differ in how much transparent
        /// padding they carry, and with it left in, two logos fitted to the same box would still
        /// look different sizes. If anything about the image is unexpected it is returned as is.
        /// </summary>
        private static BitmapSource TrimTransparentMargins(BitmapSource source)
        {
            try
            {
                BitmapSource bgra = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

                int width = bgra.PixelWidth, height = bgra.PixelHeight;
                if (width <= 0 || height <= 0 || (long)width * height > 16000000)
                    return source;

                int stride = width * 4;
                var pixels = new byte[stride * height];
                bgra.CopyPixels(pixels, stride, 0);

                int left = width, top = height, right = -1, bottom = -1;
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        if (pixels[row + x * 4 + 3] < VisibleAlpha)
                            continue;

                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }

                // Fully transparent, or nothing to trim.
                if (right < left || bottom < top)
                    return source;
                if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1)
                    return source;

                var cropped = new CroppedBitmap(bgra, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
                cropped.Freeze();
                return cropped;
            }
            catch
            {
                return source;
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _image.Source = null;
            Visibility = Visibility.Collapsed;

            if (newContext == null)
                return;

            try
            {
                string path = LogoPath(_api, newContext.Id);
                if (!File.Exists(path))
                    return;

                // Read fully into memory so the file is not held open: the importer may replace it.
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();

                _image.Source = TrimTransparentMargins(bitmap);
                Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn($"[Logo] Could not show the logo for {newContext.Name}: {ex.Message}");
            }
        }
    }
}
