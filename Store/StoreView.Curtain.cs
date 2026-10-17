using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RomM.Store
{
    /// <summary>
    /// The Store's loading curtain: while Home is first put together, a curtain over the Store
    /// with a spinner and how far it has got, lifted once the page is there and at least half
    /// of the pictures it asked for have arrived (or failed), so the Store is not seen as a
    /// page of empty frames filling in. It lifts after twelve seconds whatever the count, and
    /// at once when the server cannot be reached (the page says so).
    ///
    /// Before the pictures, the genres and series are counted (StoreView.Menu: CountCategories),
    /// with the pictures held back meanwhile, so the bar's menus and the Genres and Series
    /// pages are right from the first look.
    ///
    /// The bar: the page's data 25%, the genres and series 25%, the pictures the rest, full at
    /// half of them in.
    /// </summary>
    public partial class StoreView
    {
        private static readonly TimeSpan CurtainLongest = TimeSpan.FromSeconds(12);
        private const double CurtainPictures = 0.5;

        private Grid _curtain;
        private Border _curtainFill;
        private TextBlock _curtainText;
        private DispatcherTimer _curtainTimer;
        private DateTime _curtainSince;
        private DateTime? _curtainDataAt;   // when Home's data was laid out
        private bool _categoriesReady;       // the genres and series counted (or nothing to count)

        private void BuildCurtain(Grid root)
        {
            var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var spinner = Spinner.Create(56, 4);
            spinner.HorizontalAlignment = HorizontalAlignment.Center;
            spinner.Margin = new Thickness(0, 0, 0, 22);
            words.Children.Add(spinner);
            var title = Text("Loading the store", 18, true);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            words.Children.Add(title);
            _curtainText = Text("", 12, false);
            _curtainText.Opacity = 0.6;
            _curtainText.HorizontalAlignment = HorizontalAlignment.Center;
            _curtainText.Margin = new Thickness(0, 6, 0, 14);
            words.Children.Add(_curtainText);
            var track = new Border { Width = 260, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) };
            _curtainFill = new Border { Width = 0, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF)) };
            track.Child = _curtainFill;
            words.Children.Add(track);

            _curtain = new Grid
            {
                Background = new LinearGradientBrush(ThemeColors.MainDark, ThemeColors.Mix(ThemeColors.MainDark, ThemeColors.Main, 0.35), 90),
                Visibility = Visibility.Collapsed,
            };
            _curtain.Children.Add(words);
            Panel.SetZIndex(_curtain, 20);
            root.Children.Add(_curtain);

            _curtainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _curtainTimer.Tick += (s, e) => PaintCurtain();
        }

        /// <summary>The curtain down, as Home starts to be put together.</summary>
        private void LowerCurtain()
        {
            if (_curtain == null)
                return;
            StoreImages.StartCounting();
            _curtainSince = DateTime.UtcNow;
            _curtainDataAt = null;
            _curtain.BeginAnimation(OpacityProperty, null);
            _curtain.Opacity = 1;
            _curtain.Visibility = Visibility.Visible;
            _curtainFill.Width = 0;
            _curtainText.Text = "Asking the server...";
            _curtainTimer.Start();
        }

        /// <summary>Home's data is laid out: from here on the pictures decide.</summary>
        private void CurtainDataReady(bool reached)
        {
            if (_curtain == null || _curtain.Visibility != Visibility.Visible)
                return;
            _curtainDataAt = DateTime.UtcNow;
            if (!reached)
                LiftCurtain();
        }

        private void PaintCurtain()
        {
            if (_curtain.Visibility != Visibility.Visible)
            {
                _curtainTimer.Stop();
                return;
            }
            int asked = StoreImages.Asked, settled = Math.Min(StoreImages.Settled, asked);
            double pictures = asked == 0 ? 0 : Math.Min(1, settled / (asked * CurtainPictures));
            double waited = (DateTime.UtcNow - _curtainSince).TotalSeconds;
            // The data and the counts: each creeping toward its quarter while it is asked for.
            double data = _curtainDataAt != null ? 1 : 1 - Math.Exp(-waited / 2);
            double counts = _categoriesReady ? 1 : 1 - Math.Exp(-waited / 3);
            if (_curtainDataAt != null && asked == 0 && DateTime.UtcNow - _curtainDataAt.Value > TimeSpan.FromMilliseconds(600))
                pictures = 1;   // no pictures asked for a moment after the page is up
            double progress = 0.25 * data + 0.25 * counts + 0.5 * (_categoriesReady ? pictures : 0);
            _curtainText.Text = !_categoriesReady ? "Sorting genres and series..."
                : _curtainDataAt == null ? "Asking the server..."
                : asked > 0 ? $"Loading pictures... {settled} of {asked}" : "Almost there...";
            _curtainFill.Width = 260 * progress;

            bool ready = _curtainDataAt != null && _categoriesReady && pictures >= 1;
            if (ready || DateTime.UtcNow - _curtainSince > CurtainLongest)
                LiftCurtain();
        }

        private void LiftCurtain()
        {
            _curtainTimer.Stop();
            if (_curtain.Visibility != Visibility.Visible)
                return;
            _curtainFill.Width = 260;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (s, e) =>
            {
                _curtain.Visibility = Visibility.Collapsed;
                _curtain.BeginAnimation(OpacityProperty, null);
                _curtain.Opacity = 1;
                // Home is up: the Platforms, Genres and Series pages' pictures next.
                PrefetchDirectories();
            };
            _curtain.BeginAnimation(OpacityProperty, fade);
        }
    }
}
