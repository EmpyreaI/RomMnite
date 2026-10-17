using Playnite.SDK;
using Playnite.SDK.Models;
using RomM.Models.RomM.Rom;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace RomM.Games
{
    // Logos and covers come in every aspect ratio, which makes list icons look ragged. This
    // downloads the game's icon image (logo, else cover) and centres it on a fixed-size transparent
    // canvas so every icon has the same dimensions.
    internal static class RomMIconNormalizer
    {
        // The canvas every icon is fitted into.
        public const int CanvasWidth = 640;
        public const int CanvasHeight = 480;

        // Client for logos hosted outside RomM (Screenscraper), so the RomM credentials on
        // HttpClientSingleton are never sent to a third-party host.
        private static readonly HttpClient s_externalHttp = new HttpClient();

        /// <summary>
        /// Replaces the icon url on the metadata with the normalised image itself. Best-effort:
        /// on any failure the url icon set by the mapper is left in place.
        /// </summary>
        public static void Apply(GameMetadata metadata, RomMRom rom, string romMHost, ILogger logger, CancellationToken cancelToken)
        {
            try
            {
                string url = RomMMetadataMapper.IconUrl(romMHost, rom, out bool servedByRomM);
                if (metadata == null || string.IsNullOrEmpty(url) || cancelToken.IsCancellationRequested)
                    return;

                var client = servedByRomM ? HttpClientSingleton.Instance : s_externalHttp;
                byte[] source;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    using (var response = client.GetAsync(url, cts.Token).GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            logger.Warn($"[Icon] Icon for {rom.Name} not normalised: HTTP {(int)response.StatusCode}");
                            return;
                        }
                        source = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    }
                }

                byte[] icon = FitToCanvas(source, CanvasWidth, CanvasHeight);
                if (icon != null)
                    metadata.Icon = new MetadataFile("icon.png", icon);
            }
            catch (Exception ex)
            {
                logger.Warn($"[Icon] Icon for {rom.Name} not normalised: {ex.Message}");
            }
        }

        /// <summary>
        /// Scales the image to fit inside the canvas (keeping its aspect ratio, never cropping),
        /// centres it on a transparent background and returns the result as PNG bytes.
        /// </summary>
        internal static byte[] FitToCanvas(byte[] source, int canvasWidth, int canvasHeight)
        {
            if (source == null || source.Length == 0)
                return null;

            using (var input = new MemoryStream(source))
            using (var image = Image.FromStream(input))
            using (var canvas = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb))
            {
                if (image.Width <= 0 || image.Height <= 0)
                    return null;

                double scale = Math.Min((double)canvasWidth / image.Width, (double)canvasHeight / image.Height);
                int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                int x = (canvasWidth - width) / 2;
                int y = (canvasHeight - height) / 2;

                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    using (var attributes = new ImageAttributes())
                    {
                        // Stops the resampler bleeding the canvas edge into the scaled image.
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(image, new Rectangle(x, y, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                    }
                }

                using (var output = new MemoryStream())
                {
                    canvas.Save(output, ImageFormat.Png);
                    return output.ToArray();
                }
            }
        }
    }
}
