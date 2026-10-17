using Playnite.SDK;
using RomM.Games;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RomM.Store
{
    /// <summary>
    /// Steam's "About This Game" drawn as Steam draws it: headings, paragraphs, lists, pictures
    /// and the looping banner videos many games use in place of text. Steam's HTML is read
    /// loosely, tag by tag; anything not understood is passed over, keeping its text.
    ///
    /// Videos play through the local relay (WPF cannot play https), muted and looping, from
    /// their MP4 source; WPF cannot play Steam's WebM sources. Pictures in formats Windows
    /// cannot read (Steam's AVIF video posters, on machines without the AV1 extension) simply
    /// do not appear.
    /// </summary>
    internal static class SteamAbout
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly Regex Tag = new Regex(@"<(/?)([a-zA-Z0-9]+)([^>]*)>", RegexOptions.Compiled);

        /// <summary>The content, or null when Steam's HTML holds nothing to show.</summary>
        public static UIElement Build(string html, VideoRelay relay)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;
            try
            {
                var builder = new Builder(relay);
                builder.Read(html);
                return builder.Count > 0 ? builder.Panel : null;
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Steam] Could not draw Steam's description: {ex.Message}");
                return null;
            }
        }

        private static string Attribute(string attributes, string name)
        {
            var match = Regex.Match(attributes ?? "", name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;
            string value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            return WebUtility.HtmlDecode(value);
        }

        private static double Number(string attributes, string name)
        {
            return double.TryParse(Attribute(attributes, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0;
        }

        private sealed class Builder
        {
            private readonly VideoRelay _relay;
            public readonly StackPanel Panel = new StackPanel();
            public int Count;

            private TextBlock _paragraph;
            private bool _heading;
            private int _bold;
            private int _italic;

            // Inside a <video>: its poster, size and the MP4 to play.
            private bool _inVideo;
            private string _poster;
            private string _mp4;
            private double _videoWidth;
            private double _videoHeight;

            public Builder(VideoRelay relay)
            {
                _relay = relay;
            }

            public void Read(string html)
            {
                int at = 0;
                foreach (Match tag in Tag.Matches(html))
                {
                    if (tag.Index > at)
                        AddText(html.Substring(at, tag.Index - at));
                    at = tag.Index + tag.Length;

                    bool closing = tag.Groups[1].Value == "/";
                    string name = tag.Groups[2].Value.ToLowerInvariant();
                    string attributes = tag.Groups[3].Value;
                    OnTag(name, closing, attributes);
                }
                if (at < html.Length)
                    AddText(html.Substring(at));
                Flush();
            }

            private void OnTag(string name, bool closing, string attributes)
            {
                switch (name)
                {
                    case "br":
                        if (_paragraph != null)
                            _paragraph.Inlines.Add(new LineBreak());
                        break;
                    case "p":
                    case "div":
                    case "ul":
                    case "ol":
                        Flush();
                        break;
                    case "h1":
                    case "h2":
                    case "h3":
                    case "h4":
                    case "h5":
                    case "h6":
                        Flush();
                        _heading = !closing;
                        break;
                    case "li":
                        Flush();
                        if (!closing)
                        {
                            Start();
                            _paragraph.Margin = new Thickness(14, 0, 0, 6);
                            _paragraph.Inlines.Add(new Run("•  "));
                        }
                        break;
                    case "b":
                    case "strong":
                        _bold = Math.Max(0, _bold + (closing ? -1 : 1));
                        break;
                    case "i":
                    case "em":
                        _italic = Math.Max(0, _italic + (closing ? -1 : 1));
                        break;
                    case "img":
                        if (!_inVideo)
                        {
                            Flush();
                            AddPicture(Attribute(attributes, "src"), Number(attributes, "width"), Number(attributes, "height"));
                        }
                        break;
                    case "video":
                        if (!closing)
                        {
                            Flush();
                            _inVideo = true;
                            _poster = Attribute(attributes, "poster");
                            _mp4 = null;
                            _videoWidth = Number(attributes, "width");
                            _videoHeight = Number(attributes, "height");
                        }
                        else if (_inVideo)
                        {
                            _inVideo = false;
                            AddVideo();
                        }
                        break;
                    case "source":
                        if (_inVideo && _mp4 == null)
                        {
                            string src = Attribute(attributes, "src");
                            string type = Attribute(attributes, "type") ?? "";
                            if (!string.IsNullOrEmpty(src) && (type.IndexOf("mp4", StringComparison.OrdinalIgnoreCase) >= 0 || src.IndexOf(".mp4", StringComparison.OrdinalIgnoreCase) >= 0))
                                _mp4 = src;
                        }
                        break;
                }
            }

            private void Start()
            {
                if (_paragraph != null)
                    return;
                _paragraph = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = _heading ? 16 : 14,
                    FontWeight = _heading ? FontWeights.SemiBold : FontWeights.Normal,
                    LineHeight = _heading ? 22 : 22,
                    Margin = _heading ? new Thickness(0, 14, 0, 8) : new Thickness(0, 0, 0, 14),
                };
                _paragraph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            }

            private void AddText(string raw)
            {
                if (_inVideo)
                    return;
                string text = Regex.Replace(WebUtility.HtmlDecode(raw.Replace("\r", "").Replace("\n", " ")), @"[ \t ]+", " ");
                if (_paragraph == null)
                    text = text.TrimStart();
                if (text.Length == 0)
                    return;
                Start();
                var run = new Run(_heading ? text.ToUpperInvariant() : text);
                if (_bold > 0)
                    run.FontWeight = FontWeights.SemiBold;
                if (_italic > 0)
                    run.FontStyle = FontStyles.Italic;
                _paragraph.Inlines.Add(run);
            }

            private void Flush()
            {
                if (_paragraph == null)
                    return;
                // Nothing but spaces and line breaks is not worth a paragraph.
                bool any = false;
                foreach (Inline inline in _paragraph.Inlines)
                {
                    if (inline is Run run && run.Text.Trim().Length > 0 && run.Text.Trim() != "•")
                        any = true;
                }
                if (any)
                {
                    Panel.Children.Add(_paragraph);
                    Count++;
                }
                _paragraph = null;
            }

            // A frame that keeps the picture's shape at whatever width the column has.
            private Grid Frame(double width, double height)
            {
                var frame = new Grid { Margin = new Thickness(0, 0, 0, 10), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
                if (width > 0 && height > 0)
                {
                    double ratio = height / width;
                    frame.MaxWidth = width;
                    frame.HorizontalAlignment = HorizontalAlignment.Stretch;
                    frame.SizeChanged += (s, e) =>
                    {
                        if (e.WidthChanged)
                            frame.Height = Math.Round(e.NewSize.Width * ratio);
                    };
                }
                return frame;
            }

            private void AddPicture(string src, double width, double height)
            {
                if (string.IsNullOrEmpty(src))
                    return;
                var frame = Frame(width, height);
                var image = new Image { Stretch = Stretch.Uniform, StretchDirection = width > 0 ? StretchDirection.Both : StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
                StoreImages.Show(image, src, 1200);
                frame.Children.Add(image);
                Panel.Children.Add(frame);
                Count++;
            }

            private void AddVideo()
            {
                if (string.IsNullOrEmpty(_mp4) && string.IsNullOrEmpty(_poster))
                    return;
                var frame = Frame(_videoWidth, _videoHeight);
                if (!string.IsNullOrEmpty(_poster))
                {
                    var poster = new Image { Stretch = Stretch.UniformToFill };
                    StoreImages.Show(poster, _poster, 1200);
                    frame.Children.Add(poster);
                }

                string local = string.IsNullOrEmpty(_mp4) ? null : _relay?.LocalUrl(_mp4);
                if (local != null)
                {
                    var player = new MediaElement
                    {
                        LoadedBehavior = MediaState.Manual,
                        UnloadedBehavior = MediaState.Close,
                        IsMuted = true,
                        Stretch = Stretch.UniformToFill,
                        IsHitTestVisible = false,
                        Opacity = 0,
                    };
                    player.MediaOpened += (s, e) => player.Opacity = 1;
                    player.MediaEnded += (s, e) =>
                    {
                        player.Position = TimeSpan.Zero;
                        player.Play();
                    };
                    player.MediaFailed += (s, e) =>
                    {
                        s_log.Debug($"[Steam] Could not play a description video: {e.ErrorException?.Message}");
                        player.Close();
                        frame.Children.Remove(player);
                    };
                    player.Loaded += (s, e) =>
                    {
                        if (player.Source == null)
                            player.Source = new Uri(local, UriKind.Absolute);
                        player.Play();
                    };
                    frame.Children.Add(player);
                }
                Panel.Children.Add(frame);
                Count++;
            }
        }
    }
}
