using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RomM.Games
{
    /// <summary>How long a game takes, in hours, as RomM has it from HowLongToBeat.</summary>
    public class HltbTimes
    {
        public double MainStory { get; set; }
        public double MainExtra { get; set; }
        public double Completionist { get; set; }
        public DateTime FetchedUtc { get; set; }

        [JsonIgnore]
        public bool Any => MainStory > 0 || MainExtra > 0 || Completionist > 0;
    }

    /// <summary>
    /// The selected game's HowLongToBeat times, bound to by themes in place of the HowLongToBeat
    /// extension's: {PluginSettings Plugin=RomM, Path=Hltb.HasData} and Hltb.MainStoryFormat,
    /// MainExtraFormat, CompletionistFormat ("-" when unknown, which themes written for the
    /// extension already hide), PlayedFormat and Percent (time played against the main story).
    /// </summary>
    public class HltbSummary : ObservableObject
    {
        private bool _hasData;
        private string _main = "-";
        private string _extra = "-";
        private string _complete = "-";
        private string _played = "-";
        private int _percent;

        public bool HasData { get => _hasData; set => SetValue(ref _hasData, value); }
        public string MainStoryFormat { get => _main; set => SetValue(ref _main, value); }
        public string MainExtraFormat { get => _extra; set => SetValue(ref _extra, value); }
        public string CompletionistFormat { get => _complete; set => SetValue(ref _complete, value); }
        public string PlayedFormat { get => _played; set => SetValue(ref _played, value); }
        public int Percent { get => _percent; set => SetValue(ref _percent, value); }

        /// <summary>The numbers behind the formats, for the progress bar.</summary>
        public HltbTimes Times { get; private set; }
        public double PlayedHours { get; private set; }

        /// <summary>Raised after Show, once every value has changed.</summary>
        public event Action Shown;

        public static string Format(double hours)
        {
            if (hours <= 0)
                return "-";
            return hours >= 10 ? $"{hours:0} Hours" : $"{hours:0.#} Hours";
        }

        public void Show(HltbTimes times, ulong playtimeSeconds)
        {
            bool has = times != null && times.Any;
            Times = has ? times : null;
            PlayedHours = playtimeSeconds / 3600.0;
            MainStoryFormat = has ? Format(times.MainStory) : "-";
            MainExtraFormat = has ? Format(times.MainExtra) : "-";
            CompletionistFormat = has ? Format(times.Completionist) : "-";
            PlayedFormat = PlayedHours > 0 ? Format(PlayedHours) : "-";
            double target = has ? (times.MainStory > 0 ? times.MainStory : times.MainExtra > 0 ? times.MainExtra : times.Completionist) : 0;
            Percent = target > 0 ? (int)Math.Min(100, Math.Round(100 * PlayedHours / target)) : 0;
            HasData = has;
            Shown?.Invoke();
        }
    }

    /// <summary>
    /// HowLongToBeat times of RomM games, from the server (which matches its games on
    /// HowLongToBeat), kept on disk so a game's times show at once the next time. Asked again
    /// after a week, in case the server has matched the game since.
    /// </summary>
    public class HltbStore
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

        private readonly string _path;
        private readonly object _lock = new object();
        private Dictionary<int, HltbTimes> _known;
        private readonly ConcurrentDictionary<int, Task<HltbTimes>> _asking = new ConcurrentDictionary<int, Task<HltbTimes>>();

        public HltbStore(string path)
        {
            _path = path;
        }

        private Dictionary<int, HltbTimes> Known
        {
            get
            {
                if (_known != null)
                    return _known;
                try
                {
                    if (File.Exists(_path))
                        _known = JsonConvert.DeserializeObject<Dictionary<int, HltbTimes>>(File.ReadAllText(_path));
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[HLTB] Could not read the kept times: {ex.Message}");
                }
                return _known ?? (_known = new Dictionary<int, HltbTimes>());
            }
        }

        /// <summary>What is kept for the game, fresh or not; null when never asked.</summary>
        public HltbTimes Kept(int romId)
        {
            lock (_lock)
                return Known.TryGetValue(romId, out HltbTimes times) ? times : null;
        }

        public bool Fresh(int romId)
        {
            HltbTimes times = Kept(romId);
            return times != null && DateTime.UtcNow - times.FetchedUtc < KeepFor;
        }

        /// <summary>Asks the server for the game's times (once at a time per game) and keeps them.</summary>
        public Task<HltbTimes> FetchAsync(string host, int romId)
        {
            return _asking.GetOrAdd(romId, id => Task.Run(async () =>
            {
                try
                {
                    using (var response = await HttpClientSingleton.Instance.GetAsync($"{host}/api/roms/{id}").ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return Kept(id);
                        var rom = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        var times = new HltbTimes
                        {
                            MainStory = HoursOf(rom.SelectToken("hltb_metadata.main_story")),
                            MainExtra = HoursOf(rom.SelectToken("hltb_metadata.main_plus_extra")),
                            Completionist = HoursOf(rom.SelectToken("hltb_metadata.completionist")),
                            FetchedUtc = DateTime.UtcNow,
                        };
                        lock (_lock)
                        {
                            Known[id] = times;
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                                File.WriteAllText(_path, JsonConvert.SerializeObject(_known));
                            }
                            catch (Exception ex)
                            {
                                s_log.Debug($"[HLTB] Could not keep the times: {ex.Message}");
                            }
                        }
                        return times;
                    }
                }
                catch (Exception ex)
                {
                    s_log.Debug($"[HLTB] Could not ask for the times of rom {id}: {ex.Message}");
                    return Kept(id);
                }
                finally
                {
                    _asking.TryRemove(id, out _);
                }
            }));
        }

        // RomM keeps the times in seconds (as the Store page reads them); a server that kept
        // HowLongToBeat's hundredths would give absurd hours, so those are scaled back.
        private static double HoursOf(JToken value)
        {
            double seconds;
            try
            {
                seconds = value == null || value.Type == JTokenType.Null ? 0 : value.Value<double>();
            }
            catch
            {
                return 0;
            }
            double hours = seconds / 3600.0;
            if (hours > 2000)
                hours /= 100.0;
            return Math.Max(0, hours);
        }
    }

    /// <summary>
    /// The selected game's time played against its HowLongToBeat times, as the HowLongToBeat
    /// extension's progress bar: a track as long as the longest time, filled to the time
    /// played, with marks at the main story and main + extras. A theme places it with
    /// &lt;ContentControl x:Name="RomM_HltbProgressBar" /&gt;; it is collapsed when there are no times.
    /// </summary>
    public class HltbProgressBar : PluginUserControl
    {
        private static readonly Brush Fill = new SolidColorBrush(Color.FromRgb(0x1A, 0x9F, 0xFF));
        private static readonly Brush Done = new SolidColorBrush(Color.FromRgb(0x5C, 0xB8, 0x1D));
        private static readonly Brush Track = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        private readonly HltbSummary _summary;
        private readonly Grid _bar = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = false };
        private readonly TextBlock _label = new TextBlock { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        public HltbProgressBar(HltbSummary summary)
        {
            _summary = summary;
            Focusable = false;
            _label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_label, Dock.Right);
            row.Children.Add(_label);
            row.Children.Add(_bar);
            Content = row;

            _summary.Shown += () => Dispatcher.BeginInvoke(new Action(Draw));
            _bar.SizeChanged += (s, e) => Draw();
            Draw();
        }

        private void Draw()
        {
            HltbTimes times = _summary.Times;
            Visibility = times == null ? Visibility.Collapsed : Visibility.Visible;
            _bar.Children.Clear();
            if (times == null)
                return;

            double width = _bar.ActualWidth;
            double longest = Math.Max(Math.Max(times.MainStory, times.MainExtra), Math.Max(times.Completionist, _summary.PlayedHours));
            double main = times.MainStory > 0 ? times.MainStory : times.MainExtra > 0 ? times.MainExtra : times.Completionist;
            bool finished = main > 0 && _summary.PlayedHours >= main;

            _bar.Children.Add(new Border { Background = Track, CornerRadius = new CornerRadius(4) });
            if (width > 0 && longest > 0)
            {
                _bar.Children.Add(new Border
                {
                    Background = finished ? Done : Fill,
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(0, width * Math.Min(1, _summary.PlayedHours / longest)),
                });
                foreach (double mark in new[] { times.MainStory, times.MainExtra })
                {
                    if (mark <= 0 || mark >= longest)
                        continue;
                    _bar.Children.Add(new Border
                    {
                        Width = 2,
                        Margin = new Thickness(width * mark / longest - 1, -3, 0, -3),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Background = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                    });
                }
            }

            _label.Text = _summary.PlayedHours > 0 ? $"{_summary.PlayedFormat} played \u00B7 {_summary.Percent}%" : "Not played yet";
            ToolTip = $"Main story: {_summary.MainStoryFormat}\nMain + extras: {_summary.MainExtraFormat}\nCompletionist: {_summary.CompletionistFormat}";
        }
    }
}
