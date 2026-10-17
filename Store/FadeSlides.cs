using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RomM.Store
{
    /// <summary>
    /// A picture frame whose pictures fade one into the next, as on Steam: the hover cards'
    /// screenshots taking turns (<see cref="Play"/>), and the featured carousel's screenshot
    /// previews (<see cref="FadeTo"/> and <see cref="FadeOut"/>). Each picture is loaded out of
    /// sight first and only faded in once it has arrived, so the frame never goes blank
    /// between pictures.
    /// </summary>
    internal sealed class FadeSlides
    {
        private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(2600);

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = Every };
        private readonly int _decodeWidth;
        private readonly Duration _fadeTime;

        // The picture showing (_front) and the one the next picture loads into (_back).
        private Image _front;
        private Image _back;
        private string _frontUrl;
        private string _backUrl;
        private string _pending;      // asked for while a fade was under way
        private List<string> _urls;
        private int _next;
        private bool _waiting;
        private bool _fading;
        private bool _stopped;

        /// <summary>The frame, to place in a card.</summary>
        public Grid Stage { get; }

        /// <param name="height">The frame's height; NaN to fill whatever holds it.</param>
        /// <param name="seeThrough">Clear where no picture is showing, so what is beneath shows.</param>
        public FadeSlides(double height, int decodeWidth, bool seeThrough = false, int fadeMilliseconds = 550)
        {
            _decodeWidth = decodeWidth;
            _fadeTime = new Duration(TimeSpan.FromMilliseconds(fadeMilliseconds));
            _front = NewPicture();
            _back = NewPicture();
            _back.Opacity = 0;
            Panel.SetZIndex(_back, 1);

            Stage = new Grid
            {
                Height = height,
                Background = seeThrough ? null : new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)),
                ClipToBounds = true,
            };
            if (seeThrough)
            {
                _front.Opacity = 0;
                Stage.IsHitTestVisible = false;
            }
            Stage.Children.Add(_front);
            Stage.Children.Add(_back);

            _timer.Tick += (s, e) => Advance();

            // A hover card closing ends its show.
            if (!seeThrough)
                Stage.Unloaded += (s, e) => Stop();
        }

        /// <summary>Shows <paramref name="url"/> straight away, while the screenshots are still on their way.</summary>
        public void ShowNow(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;
            _frontUrl = url;
            StoreImages.Show(_front, url, _decodeWidth);
        }

        /// <summary>
        /// Starts the show. With <paramref name="skipFirst"/>, the first screenshot is taken to be
        /// the one already showing (see <see cref="ShowNow"/>) and the show goes on from the second.
        /// </summary>
        public void Play(List<string> urls, bool skipFirst)
        {
            if (_stopped || urls == null || urls.Count == 0)
                return;
            _urls = urls;
            _next = skipFirst ? 1 : 0;
            if (!skipFirst)
                Advance();
            if (urls.Count > 1)
                _timer.Start();
        }

        public void Stop()
        {
            _stopped = true;
            _timer.Stop();
            _waiting = false;
            _pending = null;
        }

        /// <summary>Fades to <paramref name="url"/>, from whatever is showing (or from nothing).</summary>
        public void FadeTo(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;
            if (_fading)
            {
                _pending = url;
                return;
            }
            _pending = null;
            if (!_waiting && url == _frontUrl && _front.Opacity > 0.99 && _front.Source != null)
                return;
            Load(url);
        }

        /// <summary>Takes the pictures away at once, with no fade.</summary>
        public void Clear()
        {
            _pending = null;
            _waiting = false;
            _fading = false;
            _frontUrl = null;
            foreach (Image image in new[] { _front, _back })
            {
                image.BeginAnimation(UIElement.OpacityProperty, null);
                image.Opacity = 0;
            }
        }

        /// <summary>Fades the pictures away, uncovering whatever is beneath the frame.</summary>
        public void FadeOut()
        {
            _pending = null;
            _waiting = false;
            _fading = false;
            _frontUrl = null;
            foreach (Image image in new[] { _front, _back })
            {
                var away = new DoubleAnimation(image.Opacity, 0, _fadeTime) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
                Image fading = image;
                away.Completed += (s, e) =>
                {
                    // Unless a new picture has started to come in meanwhile.
                    if (fading.HasAnimatedProperties && !_waiting && !_fading)
                    {
                        fading.BeginAnimation(UIElement.OpacityProperty, null);
                        fading.Opacity = 0;
                    }
                };
                image.BeginAnimation(UIElement.OpacityProperty, away);
            }
        }

        private Image NewPicture()
        {
            var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);

            // A picture asked for is faded in once it has loaded, however long that takes.
            DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(image, (s, e) =>
            {
                if (ReferenceEquals(image, _back) && _waiting && image.Source != null)
                    Fade();
            });
            return image;
        }

        // The next screenshot of the show.
        private void Advance()
        {
            if (_stopped || _urls == null || _urls.Count == 0 || _waiting || _fading)
                return;
            string url = _urls[_next % _urls.Count];
            _next++;
            Load(url);
        }

        // Loads a picture out of sight; it fades in once it is ready.
        private void Load(string url)
        {
            _back.BeginAnimation(UIElement.OpacityProperty, null);
            _back.Opacity = 0;
            _back.Source = null;
            _backUrl = url;
            _waiting = true;
            StoreImages.Show(_back, url, _decodeWidth);
        }

        private void Fade()
        {
            _waiting = false;
            _fading = true;
            Image incoming = _back;
            Image outgoing = _front;
            string incomingUrl = _backUrl;

            var fadeIn = new DoubleAnimation(0, 1, _fadeTime) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
            fadeIn.Completed += (s, e) =>
            {
                // Overtaken by FadeOut: nothing more to do.
                if (!_fading || !ReferenceEquals(_back, incoming))
                    return;

                // The new picture becomes the one showing; the old one goes behind for the next.
                incoming.BeginAnimation(UIElement.OpacityProperty, null);
                incoming.Opacity = 1;
                Panel.SetZIndex(incoming, 0);
                outgoing.BeginAnimation(UIElement.OpacityProperty, null);
                outgoing.Opacity = 0;
                Panel.SetZIndex(outgoing, 1);
                _front = incoming;
                _back = outgoing;
                _frontUrl = incomingUrl;
                _fading = false;

                if (_pending != null)
                {
                    string next = _pending;
                    _pending = null;
                    FadeTo(next);
                }
            };
            incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
    }
}
