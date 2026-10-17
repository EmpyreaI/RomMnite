using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RomM.Games
{
    /// <summary>
    /// Plays a game's video straight from the RomM server; nothing is saved by the plugin. A
    /// theme places it with &lt;ContentControl x:Name="RomM_GameVideo" /&gt;.
    ///
    /// It always starts muted and loops. Clicking the video turns the sound on or off. It only
    /// holds a connection to the server while it is actually on screen.
    /// </summary>
    public class GameVideoControl : PluginUserControl
    {
        private static readonly ILogger s_log = LogManager.GetLogger();

        private readonly RomM _plugin;
        private readonly MediaElement _player;
        private string _url;

        // The names below (IsPlayerMuted, VideoPlayCommand, VideoPauseCommand, VideoMuteCommand)
        // are the ones the ExtraMetadataLoader extension's player has, so a theme's existing
        // play, pause and mute buttons work once they point at RomM_GameVideo.
        public static readonly DependencyProperty IsPlayerMutedProperty = DependencyProperty.Register(
            nameof(IsPlayerMuted), typeof(bool), typeof(GameVideoControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((GameVideoControl)d)._player.IsMuted = (bool)e.NewValue));

        public bool IsPlayerMuted
        {
            get => (bool)GetValue(IsPlayerMutedProperty);
            set => SetCurrentValue(IsPlayerMutedProperty, value);
        }

        public RelayCommand<object> VideoPlayCommand { get; }
        public RelayCommand VideoPauseCommand { get; }
        public RelayCommand<object> VideoMuteCommand { get; }

        // Paused by the user: stays paused until they press play or pick another game.
        private bool _paused;
        private string _name;

        public GameVideoControl(RomM plugin)
        {
            _plugin = plugin;
            _player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Close,
                IsMuted = true,
                Stretch = Stretch.Uniform,
            };
            _player.MediaEnded += (s, e) =>
            {
                _player.Position = TimeSpan.Zero;
                if (!_paused)
                    _player.Play();
            };

            VideoPlayCommand = new RelayCommand<object>(o =>
            {
                _paused = false;
                if (_player.Source == null)
                    Apply();
                else
                    _player.Play();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }, o => _paused && _url != null);
            VideoPauseCommand = new RelayCommand(() =>
            {
                _paused = true;
                if (_player.Source != null)
                    _player.Pause();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }, () => !_paused && _url != null);

            // Themes show the play button only while the play command is available and the
            // pause button only while it is not, so each command is offered in one state only.
            VideoMuteCommand = new RelayCommand<object>(o => IsPlayerMuted = !IsPlayerMuted);
            _player.MediaFailed += (s, e) =>
            {
                s_log.Warn($"[Video] Could not play the video for {_name}: {e.ErrorException?.Message}");
                Stop();
                Visibility = Visibility.Collapsed;
            };

            // A transparent background makes the whole area clickable, not just the picture.
            Content = new Grid { Background = Brushes.Transparent, Children = { _player } };
            ToolTip = "Click to turn the sound on or off";
            Focusable = false;
            Visibility = Visibility.Collapsed;

            MouseLeftButtonUp += (s, e) =>
            {
                IsPlayerMuted = !IsPlayerMuted;
                e.Handled = true;
            };
            IsVisibleChanged += (s, e) => Apply();
            Unloaded += (s, e) => Stop();
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            Stop();
            _paused = false;
            _name = newContext?.Name;
            _url = null;

            try
            {
                if (newContext != null && newContext.PluginId == _plugin.Id)
                    _url = _plugin.VideoUrl(newContext.Id);
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Video] Could not look up the video for {_name}: {ex.Message}");
            }

            Visibility = _url == null ? Visibility.Collapsed : Visibility.Visible;
            Apply();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        // Plays while on screen, lets go of the stream otherwise (another tab, video switched
        // off in the theme, another view).
        private void Apply()
        {
            if (_url == null || !IsVisible)
            {
                Stop();
                return;
            }

            if (_player.Source != null)
                return;

            try
            {
                // Every game starts silent, whatever the last one was left at.
                IsPlayerMuted = true;
                _player.IsMuted = true;
                // Never hand the player the server's own (https) address: see VideoRelay.
                string local = _plugin.VideoRelay.LocalUrl(_url);
                if (local == null)
                {
                    _url = null;
                    Visibility = Visibility.Collapsed;
                    return;
                }

                _player.Source = new Uri(local, UriKind.Absolute);
                if (_paused)
                    _player.Pause();
                else
                    _player.Play();
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Video] Could not start the video for {_name}: {ex.Message}");

                // Give up on this game outright. Left with a source it could not open, the
                // player tries again by itself later, where nothing can catch the failure.
                _url = null;
                try { _player.Source = null; } catch { }
                Visibility = Visibility.Collapsed;
            }
        }

        private void Stop()
        {
            try
            {
                if (_player.Source == null)
                    return;
                _player.Stop();
                _player.Close();
                _player.Source = null;
            }
            catch
            {
                // Nothing useful to do if the player refuses to close.
            }
        }
    }
}
