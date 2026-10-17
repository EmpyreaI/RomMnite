using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfDocument = global::Windows.Data.Pdf.PdfDocument;
using PdfPageRenderOptions = global::Windows.Data.Pdf.PdfPageRenderOptions;
using StorageFile = global::Windows.Storage.StorageFile;
using InMemoryRandomAccessStream = global::Windows.Storage.Streams.InMemoryRandomAccessStream;

namespace RomM.Manuals
{
    /// <summary>
    /// Shows a game's manual inside the library, one page at a time. A theme places it with
    /// &lt;ContentControl x:Name="RomM_ManualViewer" /&gt;, typically in a tab of its own.
    ///
    /// The manual is a PDF on the RomM server. It is downloaded once into the plugin's data
    /// folder and drawn with Windows' own PDF renderer, so nothing extra has to be installed.
    /// Nothing is fetched until the viewer is actually on screen, so merely selecting a game with
    /// a manual costs nothing.
    /// </summary>
    public class ManualViewerControl : PluginUserControl
    {
        // Pixels across a rendered page: enough to stay sharp when the page fills a wide window.
        private const uint RenderWidth = 1600;

        private readonly Guid _pluginId;
        private readonly string _cacheDir;
        private readonly ILogger _logger = LogManager.GetLogger();

        private readonly Image _page;
        private readonly TextBlock _status;
        private readonly TextBlock _position;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly Button _external;

        private PdfDocument _document;
        private string _documentPath;
        private Guid _documentGameId = Guid.Empty;
        private int _pageIndex;
        private int _generation; // bumped whenever what should be shown changes

        public ManualViewerControl(Guid pluginId, string cacheDir)
        {
            _pluginId = pluginId;
            _cacheDir = cacheDir;

            _previous = ToolButton("\uE76B", "Previous page");
            _next = ToolButton("\uE76C", "Next page");
            _external = ToolButton("\uE8A7", "Open in your PDF reader");
            _previous.Click += (s, e) => Turn(-1);
            _next.Click += (s, e) => Turn(+1);
            _external.Click += (s, e) => OpenExternally();

            _position = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), MinWidth = 90, TextAlignment = TextAlignment.Center };

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
            bar.Children.Add(_previous);
            bar.Children.Add(_position);
            bar.Children.Add(_next);
            bar.Children.Add(new Border { Width = 16 });
            bar.Children.Add(_external);

            _status = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 24), TextWrapping = TextWrapping.Wrap };

            _page = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(_page, BitmapScalingMode.Fant);

            var layout = new StackPanel();
            layout.Children.Add(bar);
            layout.Children.Add(_status);
            layout.Children.Add(_page);
            Content = layout;

            IsVisibleChanged += (s, e) => { if (IsVisible) Refresh(); };
            ShowStatus(null);
            UpdateBar();
        }

        private static Button ToolButton(string glyph, string tip)
        {
            return new Button
            {
                Content = glyph,
                ToolTip = tip,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Width = 36,
                Height = 32,
                Margin = new Thickness(2, 0, 2, 0),
                Focusable = false,
            };
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            if (IsVisible)
                Refresh();
        }

        private static bool IsPdf(string manual)
        {
            if (string.IsNullOrEmpty(manual))
                return false;

            int query = manual.IndexOf('?');
            string path = query >= 0 ? manual.Substring(0, query) : manual;
            return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        }

        // Loads the selected game's manual if it is not the one already open.
        private async void Refresh()
        {
            int generation = ++_generation;
            try
            {
                var game = GameContext;
                if (game == null || game.PluginId != _pluginId || !IsPdf(game.Manual))
                {
                    Close();
                    ShowStatus(game != null && !string.IsNullOrEmpty(game.Manual) && !IsPdf(game.Manual)
                        ? "This game's manual is not a PDF, so it cannot be shown here."
                        : "This game has no manual.");
                    return;
                }

                if (_document != null && _documentGameId == game.Id)
                {
                    await ShowPage(generation);
                    return;
                }

                Close();
                ShowStatus("Loading the manual...");

                string path = await FetchAsync(game);
                if (generation != _generation)
                    return;

                var file = await StorageFile.GetFileFromPathAsync(path);
                var document = await PdfDocument.LoadFromFileAsync(file);
                if (generation != _generation)
                    return;

                _document = document;
                _documentPath = path;
                _documentGameId = game.Id;
                _pageIndex = 0;

                await ShowPage(generation);
            }
            catch (Exception ex)
            {
                if (generation != _generation)
                    return;

                _logger.Warn($"[Manual] Could not show the manual for {GameContext?.Name}: {ex.Message}");
                Close();
                ShowStatus("The manual could not be loaded. " + ex.Message);
            }
        }

        /// <summary>The manual's local copy, downloaded the first time and whenever its address changes.</summary>
        private async Task<string> FetchAsync(Game game)
        {
            Directory.CreateDirectory(_cacheDir);
            string pdf = Path.Combine(_cacheDir, game.Id + ".pdf");
            string source = pdf + ".source";

            // Local file given as the manual: use it where it is.
            if (!game.Manual.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return game.Manual;

            if (File.Exists(pdf) && File.Exists(source) && File.ReadAllText(source) == game.Manual && new FileInfo(pdf).Length > 0)
                return pdf;

            using (var response = await HttpClientSingleton.Instance.GetAsync(game.Manual).ConfigureAwait(true))
            {
                response.EnsureSuccessStatusCode();
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(true);

                string partial = pdf + ".part";
                File.WriteAllBytes(partial, bytes);
                if (File.Exists(pdf))
                    File.Delete(pdf);
                File.Move(partial, pdf);
                File.WriteAllText(source, game.Manual);
            }
            return pdf;
        }

        private async void Turn(int by)
        {
            if (_document == null)
                return;

            int target = _pageIndex + by;
            if (target < 0 || target >= (int)_document.PageCount)
                return;

            _pageIndex = target;
            int generation = ++_generation;
            try
            {
                await ShowPage(generation);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Manual] Could not draw page {target + 1}: {ex.Message}");
                ShowStatus("That page could not be drawn. " + ex.Message);
            }
        }

        private async Task ShowPage(int generation)
        {
            var document = _document;
            if (document == null)
                return;

            UpdateBar();

            BitmapImage bitmap;
            using (var page = document.GetPage((uint)_pageIndex))
            using (var stream = new InMemoryRandomAccessStream())
            {
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = RenderWidth });
                stream.Seek(0);

                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream.AsStream();
                bitmap.EndInit();
                bitmap.Freeze();
            }

            if (generation != _generation)
                return;

            _page.Source = bitmap;
            ShowStatus(null);
            UpdateBar();
        }

        private void Close()
        {
            _document = null;
            _documentPath = null;
            _documentGameId = Guid.Empty;
            _pageIndex = 0;
            _page.Source = null;
            UpdateBar();
        }

        private void ShowStatus(string text)
        {
            _status.Text = text ?? string.Empty;
            _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateBar()
        {
            int count = _document == null ? 0 : (int)_document.PageCount;
            _position.Text = count == 0 ? string.Empty : $"Page {_pageIndex + 1} of {count}";
            _previous.IsEnabled = count > 0 && _pageIndex > 0;
            _next.IsEnabled = count > 0 && _pageIndex < count - 1;
            _external.IsEnabled = !string.IsNullOrEmpty(_documentPath);
        }

        private void OpenExternally()
        {
            try
            {
                if (!string.IsNullOrEmpty(_documentPath) && File.Exists(_documentPath))
                    Process.Start(new ProcessStartInfo(_documentPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Manual] Could not open the manual externally: {ex.Message}");
            }
        }
    }
}
