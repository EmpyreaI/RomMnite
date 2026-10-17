using Playnite.SDK;
using System.Collections.Generic;
using System.Windows.Input;

namespace RomM.Downloads
{
    /// <summary>
    /// The selected game's download, for themes: what Steam shows beside its Download button.
    /// A theme binds to it with {PluginSettings Plugin=RomM, Path=Download.Headline} and so on.
    /// </summary>
    public class DownloadStatusModel : ObservableObject
    {
        private bool _visible;
        private string _headline = "";
        private string _detail = "";
        private double _percent;
        private bool _isIndeterminate;
        private bool _canPause;
        private string _pauseGlyph = "\uE769";
        private string _pauseLabel = "Pause";

        /// <summary>Whether the selected game is downloading, queued, paused or extracting.</summary>
        public bool Visible { get => _visible; set => SetValue(ref _visible, value); }

        /// <summary>"DOWNLOADING", "DOWNLOAD QUEUED", "PAUSED", "EXTRACTING".</summary>
        public string Headline { get => _headline; set => SetValue(ref _headline, value); }

        /// <summary>"2% Complete", with the speed while downloading.</summary>
        public string Detail { get => _detail; set => SetValue(ref _detail, value); }

        /// <summary>0..100, for a progress bar.</summary>
        public double Percent { get => _percent; set => SetValue(ref _percent, value); }

        public bool IsIndeterminate { get => _isIndeterminate; set => SetValue(ref _isIndeterminate, value); }

        public bool CanPause { get => _canPause; set => SetValue(ref _canPause, value); }

        /// <summary>Pause or play symbol from the Segoe icon fonts, for an icon button.</summary>
        public string PauseGlyph { get => _pauseGlyph; set => SetValue(ref _pauseGlyph, value); }

        /// <summary>"Pause" or "Resume", for a tooltip or a text button.</summary>
        public string PauseLabel { get => _pauseLabel; set => SetValue(ref _pauseLabel, value); }

        /// <summary>Pauses the selected game's download, or resumes it.</summary>
        public ICommand PauseResumeCommand { get; set; }

        /// <summary>
        /// Cancels the selected game's download (queued, downloading, paused or extracting);
        /// themes put it where the Play button is while a download is under way.
        /// </summary>
        public ICommand CancelCommand { get; set; }

        /// <summary>Shows the library's Downloads page; themes put it on the progress bar.</summary>
        public ICommand OpenDownloadsCommand { get; set; }
    }
}
