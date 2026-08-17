using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.Audio;
using Cewka.Audio.Decoding;
using Cewka.Audio.Devices;
using Cewka.Audio.Dsp;
using Cewka.Audio.Metadata;
using Cewka.Audio.Playback;
using Cewka.Platform;

namespace Cewka.App.ViewModels;

/// <summary>
/// State behind the player window, driven by the real audio engine.
/// <para>
/// The engine reports position from the audio thread, so the interface polls it on a timer
/// rather than being pushed to. Sixty milliseconds is far below what anyone perceives as
/// lag on a progress bar and costs nothing.
/// </para>
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string[] BandLabels =
        ["32", "64", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(60);

    private readonly SettingsStore _settings;
    private readonly PlaybackEngine _engine;
    private readonly LoudnessService _loudness;
    private readonly DispatcherTimer _refresh;

    private QueueItemViewModel? _current;
    private TrackMetadata? _currentMetadata;
    private int _metadataGeneration;
    private Bitmap? _cover;
    private IReadOnlyList<Color> _palette = [];
    private bool _usingPlaceholderCover = true;
    private bool _panelOpen;
    private bool _isPlaying;
    private bool _isSeeking;
    private double _progress;
    private string _elapsed = "0:00";
    private string _total = "0:00";
    private string _title = string.Empty;
    private string _artistLine = string.Empty;
    private string _formatBadge = string.Empty;

    public MainViewModel(SettingsStore settings)
    {
        _settings = settings;
        var s = settings.Current;

        _panelOpen = s.PanelOpen;

        // Jakość konwersji i rozmiar bufora czytane są przy tworzeniu dekodera i urządzenia,
        // więc muszą stać na miejscu, zanim cokolwiek powstanie.
        AudioQuality.ResamplerFilterOrder = AudioPreferences.FilterOrder(s.ResamplerQuality);

        _loudness = new LoudnessService
        {
            TargetLufs = AudioPreferences.Lufs(s.LoudnessTarget),
            AlwaysAnalyse = s.AlwaysAnalyse,
        };

        _engine = new PlaybackEngine { PreferredPeriodSize = AudioPreferences.PeriodFrames(s.OutputLatency) };
        _engine.Graph.GainSource = _loudness;
        _engine.Graph.NormalisationEnabled = s.NormalisationEnabled;
        _engine.Graph.Limiter.Enabled = s.LimiterEnabled;
        _engine.Graph.Equaliser.Enabled = s.EqualiserEnabled;
        _engine.Graph.Equaliser.Preamp = s.Preamp;
        _engine.Graph.Volume.Target = (float)s.Volume;

        for (var band = 0; band < BandLabels.Length; band++)
            _engine.Graph.Equaliser.SetGain(band, s.EqualiserGains[band]);

        Preamp = new EqBandViewModel("PREAMP", s.Preamp);
        Preamp.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EqBandViewModel.Gain)) return;
            _engine.Graph.Equaliser.Preamp = Preamp.Gain;
            _settings.Current.Preamp = Preamp.Gain;
            _settings.Touch();
        };

        Bands = new ObservableCollection<EqBandViewModel>(
            BandLabels.Select((label, i) => new EqBandViewModel(label, s.EqualiserGains[i])));

        for (var i = 0; i < Bands.Count; i++)
        {
            var index = i;
            Bands[i].PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(EqBandViewModel.Gain)) return;
                _engine.Graph.Equaliser.SetGain(index, Bands[index].Gain);
                _settings.Current.EqualiserGains[index] = Bands[index].Gain;
                _settings.Touch();
            };
        }

        Queue = [];

        ApplyOutputDevice();
        UpdateEffects();

        _engine.TrackChanged += index => Dispatcher.UIThread.Post(() => OnTrackChanged(index));
        _engine.TrackFailed += (index, reason) => Dispatcher.UIThread.Post(() => OnTrackFailed(index, reason));
        _engine.QueueFinished += () => Dispatcher.UIThread.Post(() => IsPlaying = false);

        _title = Strings.Current["NoTrack"];
        _artistLine = string.Empty;
        _formatBadge = string.Empty;

        // Okładka domyślna od razu, żeby okno nigdy nie pokazało pustej płyty.
        UpdateArtwork(null);

        App.Theme.Changed += (_, _) => Dispatcher.UIThread.Post(OnThemeChanged);

        _refresh = new DispatcherTimer { Interval = RefreshInterval };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        _noticeTimer.Tick += (_, _) => ShowNotice(string.Empty);
    }

    // ---------- Utwór ----------

    public Bitmap? Cover { get => _cover; private set => Set(ref _cover, value); }

    /// <summary>Colours drawn from the cover, driving the animated background.</summary>
    public IReadOnlyList<Color> Palette { get => _palette; private set => Set(ref _palette, value); }

    public string Title { get => _title; private set => Set(ref _title, value); }

    public string ArtistLine
    {
        get => _artistLine;
        private set
        {
            if (!Set(ref _artistLine, value)) return;
            Raise(nameof(HasArtistLine));
        }
    }

    /// <summary>
    /// False when the file carries no artist, album or year worth showing. The line is then
    /// removed rather than filled with a placeholder — a stray dash or a stray "1" under the
    /// title is noise, and it costs the title its descenders.
    /// </summary>
    public bool HasArtistLine => !string.IsNullOrWhiteSpace(_artistLine);

    public string FormatBadge
    {
        get => _formatBadge;
        private set
        {
            if (!Set(ref _formatBadge, value)) return;
            Raise(nameof(HasFormatBadge));
        }
    }

    /// <summary>
    /// The badge is removed rather than filled with a dash when nothing is playing, and stays
    /// hidden altogether when the user has switched it off in the settings.
    /// </summary>
    public bool HasFormatBadge => _settings.Current.ShowFormatBadge
                                  && !string.IsNullOrWhiteSpace(_formatBadge)
                                  && _formatBadge != "—";

    /// <summary>Shows or hides the source-quality badge without reopening the window.</summary>
    public void SetShowFormatBadge(bool show)
    {
        if (_settings.Current.ShowFormatBadge == show) return;

        _settings.Current.ShowFormatBadge = show;
        _settings.Touch();

        Raise(nameof(HasFormatBadge));
    }

    // ---------- Odtwarzanie ----------

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;

            Raise(nameof(IsPaused));
            _mediaPanel?.SetStatus(value ? MediaPanelStatus.Playing : MediaPanelStatus.Paused);
        }
    }

    public bool IsPaused => !_isPlaying;

    /// <summary>Amplitude driving the waveform, taken from the live signal analysis.</summary>
    public float WaveLevel => _engine.Graph.Analyser.Level;

    public double Progress
    {
        get => _progress;
        set
        {
            if (!Set(ref _progress, value)) return;

            // Ustawienie z kodu przy odswiezaniu nie moze przewijac odtwarzania.
            if (_isSeeking) return;

            // W trakcie przeciagania suwak tylko sie przesuwa; przewiniecie nastepuje po
            // puszczeniu przycisku (CommitScrub).
            if (_scrubbing)
            {
                Elapsed = FormatTime(_engine.Duration.TotalSeconds * Math.Clamp(value, 0, 1));
                return;
            }

            SeekToFraction(value);
        }
    }

    private bool _scrubbing;

    /// <summary>
    /// Called while the user holds the progress bar.
    /// <para>
    /// Seeking on every pointer move would issue a full buffer handshake per pixel of travel:
    /// the decoder parks, the audio thread fades out and discards, the file is repositioned.
    /// Dragging across a long track would do that hundreds of times. The indicator follows the
    /// pointer, the music waits for the button to be released.
    /// </para>
    /// </summary>
    public void BeginScrub() => _scrubbing = true;

    /// <summary>Called when the progress bar is released; this is where playback actually moves.</summary>
    public void CommitScrub(double value)
    {
        _scrubbing = false;
        SeekToFraction(value);
    }

    private void SeekToFraction(double value)
    {
        var duration = _engine.Duration;
        if (duration <= TimeSpan.Zero) return;

        _engine.SeekTo(TimeSpan.FromSeconds(duration.TotalSeconds * Math.Clamp(value, 0, 1)));
    }

    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }

    public string Total { get => _total; private set => Set(ref _total, value); }

    public double Volume
    {
        get => _engine.Graph.Volume.Target;
        set
        {
            var clamped = (float)Math.Clamp(value, 0, 1);
            if (Math.Abs(_engine.Graph.Volume.Target - clamped) < 1e-6f) return;

            _engine.Graph.Volume.Target = clamped;
            _settings.Current.Volume = clamped;
            _settings.Touch();
            Raise();
        }
    }

    public bool ShuffleEnabled
    {
        get => _engine.Shuffle;
        set { _engine.Shuffle = value; Raise(); }
    }

    /// <summary>True for either repeat mode; drives the accent colour on the button.</summary>
    public bool RepeatEnabled => _engine.Repeat != RepeatMode.None;

    /// <summary>True only for repeat-one; decides which of the two icons is shown.</summary>
    public bool RepeatTrack => _engine.Repeat == RepeatMode.Track;

    public bool RepeatQueue => _engine.Repeat == RepeatMode.Queue;

    /// <summary>
    /// Advances the repeat mode: off, then the whole queue, then the single track. Three states
    /// on one button need the tooltip to say which is in force — the accent alone cannot.
    /// </summary>
    public string RepeatTooltip => Strings.Current[_engine.Repeat switch
    {
        RepeatMode.Queue => "TooltipRepeatQueue",
        RepeatMode.Track => "TooltipRepeatTrack",
        _ => "TooltipRepeatOff",
    }];

    public void CycleRepeat()
    {
        _engine.Repeat = _engine.Repeat switch
        {
            RepeatMode.None => RepeatMode.Queue,
            RepeatMode.Queue => RepeatMode.Track,
            _ => RepeatMode.None,
        };

        Raise(nameof(RepeatEnabled));
        Raise(nameof(RepeatTrack));
        Raise(nameof(RepeatQueue));
        Raise(nameof(RepeatTooltip));
    }

    // ---------- Panel ----------

    public bool PanelOpen
    {
        get => _panelOpen;
        set
        {
            if (!Set(ref _panelOpen, value)) return;
            _settings.Current.PanelOpen = value;
            _settings.Touch();
        }
    }

    /// <summary>Where the window buttons sit; changed from the settings window in stage 6.</summary>
    public WindowControlsPosition WindowControls => _settings.Current.WindowControls;

    public bool ControlsOnRight => WindowControls == WindowControlsPosition.Right;

    public bool ControlsOnLeft => WindowControls != WindowControlsPosition.Right;

    /// <summary>Applies a new placement without restarting the application.</summary>
    public void SetWindowControls(WindowControlsPosition position)
    {
        if (_settings.Current.WindowControls == position) return;

        _settings.Current.WindowControls = position;
        _settings.Touch();

        Raise(nameof(WindowControls));
        Raise(nameof(ControlsOnRight));
        Raise(nameof(ControlsOnLeft));
    }

    /// <summary>Applies a new interface language and refreshes the text already on screen.</summary>
    public void SetLanguage(string code)
    {
        if (_settings.Current.Language == code) return;

        _settings.Current.Language = code;
        _settings.Touch();

        Strings.Current.SetLanguage(code);
        Raise(nameof(QueueSummary));
        RefreshTextForLanguage();
    }

    /// <summary>
    /// Re-reads the pieces of text the view model built itself. Bindings written as
    /// <c>{l:Translate}</c> refresh on their own, but a title or a queue row assembled in code
    /// keeps whatever language it was assembled in until it is rebuilt.
    /// </summary>
    private void RefreshTextForLanguage()
    {
        if (_current is null) Title = Strings.Current["NoTrack"];
        RebuildQueue();
    }

    // ---------- Panel systemowy multimediów ----------

    private IMediaPanel? _mediaPanel;

    /// <summary>
    /// Connects the desktop's media panel, once the window it belongs to exists. Everything the
    /// panel shows is pushed from here, so the two never disagree about what is playing.
    /// </summary>
    public void AttachMediaPanel(IMediaPanel panel)
    {
        _mediaPanel = panel;
        PublishToMediaPanel();
    }

    private void PublishToMediaPanel()
    {
        if (_mediaPanel is null) return;

        if (_current is null)
        {
            _mediaPanel.Clear();
            _mediaPanel.SetStatus(MediaPanelStatus.Closed);
            return;
        }

        _mediaPanel.SetTrack(
            Title,
            _currentMetadata?.Artist ?? _currentMetadata?.AlbumArtist,
            _currentMetadata?.Album,
            _engine.Duration);

        _mediaPanel.SetStatus(_isPlaying ? MediaPanelStatus.Playing : MediaPanelStatus.Paused);
    }

    /// <summary>Moves playback to an absolute position, as a media panel may ask.</summary>
    public void SeekToPosition(TimeSpan position)
    {
        var duration = _engine.Duration;
        if (duration <= TimeSpan.Zero) return;

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > duration) position = duration;

        _engine.SeekTo(position);
    }

    // ---------- Efekty ----------

    private bool _reducedEffects;

    /// <summary>
    /// True while the decorative animation runs in its cheaper form. Bound by the window rather
    /// than read from the settings directly, because in the automatic mode it also depends on
    /// whether the machine is on battery.
    /// </summary>
    public bool ReducedEffects => _reducedEffects;

    public void SetEffects(EffectsMode mode)
    {
        if (_settings.Current.Effects == mode) return;

        _settings.Current.Effects = mode;
        _settings.Touch();
        UpdateEffects();
    }

    private void UpdateEffects()
    {
        var reduced = _settings.Current.Effects switch
        {
            EffectsMode.Reduced => true,
            EffectsMode.Full => false,
            _ => PowerStatus.IsOnBattery,
        };

        if (reduced == _reducedEffects) return;

        _reducedEffects = reduced;
        Raise(nameof(ReducedEffects));
    }

    // ---------- Urządzenie wyjściowe ----------

    /// <summary>Name of the device chosen in the settings; <c>null</c> means the system default.</summary>
    public string? OutputDevice => _settings.Current.OutputDevice;

    /// <summary>Device actually in use, which differs from the above when the chosen one is gone.</summary>
    public string ActiveDeviceName => _engine.DeviceName;

    /// <summary>False while nothing has been played yet and no device has been opened.</summary>
    public bool IsOutputOpen => _engine.IsDeviceOpen;

    public void SetOutputDevice(string? name)
    {
        if (_settings.Current.OutputDevice == name) return;

        _settings.Current.OutputDevice = name;
        _settings.Touch();

        ApplyOutputDevice();
        Raise(nameof(OutputDevice));
        Raise(nameof(ActiveDeviceName));
    }

    /// <summary>
    /// Points the engine at the chosen device. A name that no longer matches anything — the
    /// headset was unplugged since it was chosen — falls back to the system default rather than
    /// leaving the application silent.
    /// </summary>
    private void ApplyOutputDevice()
    {
        var index = -1;
        var name = _settings.Current.OutputDevice;

        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                var match = AudioDeviceList.Enumerate()
                    .FirstOrDefault(device => device.Name == name);

                if (match is not null) index = match.Index;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cewka] odczyt listy urządzeń: {ex.Message}");
            }
        }

        _engine.SwitchDevice(index);
    }

    // ---------- Jakość przetwarzania ----------

    public void SetResamplerQuality(ResamplerQuality quality)
    {
        if (_settings.Current.ResamplerQuality == quality) return;

        _settings.Current.ResamplerQuality = quality;
        _settings.Touch();

        // Zmiana obowiązuje od następnego dekodera; podmiana filtru w trakcie utworu dałaby
        // trzask dokładnie tam, gdzie zmiana miała poprawić brzmienie.
        AudioQuality.ResamplerFilterOrder = AudioPreferences.FilterOrder(quality);
    }

    public void SetOutputLatency(OutputLatency latency)
    {
        if (_settings.Current.OutputLatency == latency) return;

        _settings.Current.OutputLatency = latency;
        _settings.Touch();

        _engine.PreferredPeriodSize = AudioPreferences.PeriodFrames(latency);

        // Rozmiar okresu ustala się przy otwarciu urządzenia, więc trzeba je otworzyć na nowo.
        _engine.Reopen();
        Raise(nameof(LatencyDescription));
    }

    /// <summary>
    /// Rozmiar okresu przyjęty przez urządzenie, wraz z odpowiadającym mu czasem. Żądanie jest
    /// tylko podpowiedzią dla sterownika, więc pokazywana jest wartość odczytana, nie wybrana.
    /// </summary>
    public string LatencyDescription
    {
        get
        {
            var frames = _engine.PeriodSizeInFrames;
            if (frames <= 0) return Strings.Current["LatencyUnknown"];

            var milliseconds = frames * 1000.0 / Math.Max(1, _engine.SampleRate);
            return string.Format(Strings.Current["LatencyMeasured"], frames, milliseconds);
        }
    }

    public void SetLoudnessTarget(LoudnessTarget target)
    {
        if (_settings.Current.LoudnessTarget == target) return;

        _settings.Current.LoudnessTarget = target;
        _settings.Touch();

        _loudness.TargetLufs = AudioPreferences.Lufs(target);
        _engine.RefreshTrackGain();
    }

    /// <summary>Whether ReplayGain tags are ignored in favour of the player's own measurement.</summary>
    public bool AlwaysAnalyse
    {
        get => _loudness.AlwaysAnalyse;
        set
        {
            if (_loudness.AlwaysAnalyse == value) return;

            _loudness.AlwaysAnalyse = value;
            _settings.Current.AlwaysAnalyse = value;
            _settings.Touch();

            _engine.RefreshTrackGain();
            Raise();
        }
    }

    // ---------- Odtwarzanie ----------

    public bool RestoreSessionEnabled
    {
        get => _settings.Current.RestoreSession;
        set
        {
            if (_settings.Current.RestoreSession == value) return;

            _settings.Current.RestoreSession = value;
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>Seconds the arrow keys move playback by.</summary>
    public int SeekStep
    {
        get => _settings.Current.SeekStep;
        set
        {
            if (_settings.Current.SeekStep == value) return;

            _settings.Current.SeekStep = value;
            _settings.Touch();
            Raise();
        }
    }

    // ---------- Korektor ----------

    public EqBandViewModel Preamp { get; }

    public ObservableCollection<EqBandViewModel> Bands { get; }

    public bool EqualiserEnabled
    {
        get => _engine.Graph.Equaliser.Enabled;
        set
        {
            if (_engine.Graph.Equaliser.Enabled == value) return;

            _engine.Graph.Equaliser.Enabled = value;
            _settings.Current.EqualiserEnabled = value;
            _settings.Touch();
            Raise();
            Raise(nameof(EqualiserOpacity));
        }
    }

    public double EqualiserOpacity => EqualiserEnabled ? 1.0 : 0.4;

    public bool LimiterEnabled
    {
        get => _engine.Graph.Limiter.Enabled;
        set
        {
            if (_engine.Graph.Limiter.Enabled == value) return;

            _engine.Graph.Limiter.Enabled = value;
            _settings.Current.LimiterEnabled = value;
            _settings.Touch();
            Raise();
        }
    }

    public bool NormalisationEnabled
    {
        get => _engine.Graph.NormalisationEnabled;
        set
        {
            if (_engine.Graph.NormalisationEnabled == value) return;

            _engine.Graph.NormalisationEnabled = value;
            _settings.Current.NormalisationEnabled = value;
            _settings.Touch();

            // Bez tego przełącznik działałby dopiero od następnego utworu, choć naciska się go
            // po to, żeby usłyszeć różnicę w tym.
            _engine.RefreshTrackGain();
            Raise();
        }
    }

    // ---------- Kolejka ----------

    public ObservableCollection<QueueItemViewModel> Queue { get; }

    /// <summary>Drives the opening prompt shown when there is nothing to play.</summary>
    public bool IsQueueEmpty => Queue.Count == 0;

    public string QueueSummary
    {
        get
        {
            if (Queue.Count == 0) return Strings.Current["QueueEmpty"];

            var total = Queue.Sum(item => item.DurationSeconds);
            return $"{Queue.Count} · {FormatTime(total)}";
        }
    }

    /// <summary>Loads files or folders into the queue and starts playing.</summary>
    public void OpenPaths(IEnumerable<string> paths, bool replace = true)
    {
        var files = ExpandToAudioFiles(paths).ToList();
        if (files.Count == 0) return;

        if (replace)
        {
            if (TryPlayback(() => _engine.SetQueue(files))) IsPlaying = true;
        }
        else
        {
            _engine.Enqueue(files);
        }

        RebuildQueue();
    }

    /// <summary>
    /// Bierze pliki przychodzące spoza programu — z wiersza polecenia, z „Otwórz za pomocą",
    /// od drugiej kopii programu albo upuszczone na okno — i postępuje z nimi tak, jak wybrano
    /// w ustawieniach.
    ///
    /// <para>Wybór plików wewnątrz programu (<c>Ctrl+O</c>) tą drogą nie idzie: tam użytkownik
    /// właśnie nacisnął „dodaj", więc pytanie, czy dodać, byłoby już rozstrzygnięte.</para>
    /// </summary>
    public void OpenFromOutside(IEnumerable<string> paths)
    {
        var files = ExpandToAudioFiles(paths).ToList();
        if (files.Count == 0) return;

        switch (_settings.Current.FileOpenAction)
        {
            case FileOpenAction.ReplaceAndPlay:
                if (TryPlayback(() => _engine.SetQueue(files))) IsPlaying = true;
                break;

            case FileOpenAction.AppendAndPlay:
            {
                // Numer pierwszego dołożonego pliku trzeba odczytać przed dołożeniem: potem
                // długość kolejki obejmuje już nowe pozycje i nie wiadomo, gdzie się zaczynają.
                var first = Queue.Count;
                _engine.Enqueue(files);
                _resume.Discard();
                if (TryPlayback(() => _engine.PlayIndex(first))) IsPlaying = true;
                break;
            }

            default:
                // Pusta kolejka to przypadek osobny: nie ma czego przerywać, a program, który
                // po otwarciu pliku milczy, wygląda na zepsuty.
                if (IsQueueEmpty)
                {
                    if (TryPlayback(() => _engine.SetQueue(files))) IsPlaying = true;
                }
                else
                {
                    _engine.Enqueue(files);
                }

                break;
        }

        RebuildQueue();
    }

    // ---------- Awaria wyjścia dźwięku ----------

    private string _audioFailure = string.Empty;

    /// <summary>
    /// Why playback is impossible, or empty when all is well.
    /// <para>
    /// Opening the output device can fail for reasons that have nothing to do with the
    /// application: no sound card, a device taken exclusively by something else, a missing
    /// native library in a hand-assembled build. None of them is a reason to take the window
    /// down — the queue, the settings and the reason itself are all still worth showing.
    /// </para>
    /// </summary>
    public string AudioFailure
    {
        get => _audioFailure;
        private set
        {
            if (!Set(ref _audioFailure, value)) return;
            Raise(nameof(HasAudioFailure));
        }
    }

    public bool HasAudioFailure => !string.IsNullOrEmpty(_audioFailure);

    /// <summary>Runs an engine operation, turning a failure into a message instead of a crash.</summary>
    private bool TryPlayback(Action operation)
    {
        try
        {
            operation();
            AudioFailure = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            AudioFailure = $"{Strings.Current["AudioFailure"]} {ex.Message}";
            Console.Error.WriteLine($"[cewka] wyjście dźwięku: {ex}");

            IsPlaying = false;
            return false;
        }
    }

    /// <summary>Folders are walked recursively; anything unrecognised is left out.</summary>
    private static IEnumerable<string> ExpandToAudioFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(path, "*", options).Order())
                {
                    if (IsAudio(file)) yield return file;
                }
            }
            else if (File.Exists(path) && IsAudio(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsAudio(string path) =>
        AudioFileFormatDetector.SupportedExtensions
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void RebuildQueue()
    {
        Queue.Clear();

        var entries = _engine.Queue;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            Queue.Add(new QueueItemViewModel
            {
                Number = i + 1,
                Title = entry.Title,
                Artist = entry.Artist ?? Strings.Current["UnknownArtist"],
                AlbumLine = BuildAlbumLine(entry),
                Duration = FormatTime(entry.Duration.TotalSeconds),
                DurationSeconds = entry.Duration.TotalSeconds,
                Format = entry.Metadata?.FormatBadge ?? Strings.Current["NoFormat"],
                Path = entry.Path,
            });
        }

        RestoreCurrentMarker();

        Raise(nameof(QueueSummary));
        Raise(nameof(IsQueueEmpty));
        _ = FillMetadataInBackgroundAsync();
    }

    /// <summary>
    /// Re-attaches the „now playing" marker after the rows were rebuilt.
    /// <para>
    /// The rows are throwaway objects, so anything that rebuilds them — reordering, removing an
    /// entry, changing language — leaves <see cref="_current"/> pointing at a row that is no
    /// longer in the list. The engine still knows which entry is playing, so the marker is
    /// re-derived from it rather than remembered.
    /// </para>
    /// </summary>
    private void RestoreCurrentMarker()
    {
        var index = _engine.CurrentIndex;

        _current = index >= 0 && index < Queue.Count ? Queue[index] : null;
        if (_current is not null) _current.IsCurrent = true;

        Raise(nameof(CurrentIndex));
    }

    /// <summary>
    /// Reads tags for the whole queue away from the interface thread.
    /// <para>
    /// Adding a folder has to feel instant, so entries appear straight away with nothing but
    /// their file names. The durations and titles then fill in as the tags are read. Reading
    /// two thousand files takes a few seconds; doing it up front would freeze the window for
    /// exactly that long.
    /// </para>
    /// </summary>
    private async Task FillMetadataInBackgroundAsync()
    {
        var token = ++_metadataGeneration;
        var entries = _engine.Queue;

        await Task.Run(() =>
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (token != _metadataGeneration) return;

                var index = i;
                var entry = entries[index];

                try { entry.EnsureMetadata(); }
                catch { continue; }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token != _metadataGeneration || index >= Queue.Count) return;
                    ApplyMetadataToRow(Queue[index], entry);
                }, DispatcherPriority.Background);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token == _metadataGeneration) Raise(nameof(QueueSummary));
            }, DispatcherPriority.Background);
        });
    }

    private static void ApplyMetadataToRow(QueueItemViewModel row, QueueEntry entry)
    {
        if (entry.Metadata is { } metadata)
        {
            row.Duration = FormatTime(metadata.Duration.TotalSeconds);
            row.DurationSeconds = metadata.Duration.TotalSeconds;
            row.Title = metadata.Title;
            row.Artist = metadata.Artist ?? metadata.AlbumArtist ?? Strings.Current["UnknownArtist"];
        }

        if (!entry.IsUnsupported) return;

        row.IsUnsupported = true;
        row.UnsupportedReason = entry.UnsupportedReason;
    }

    /// <summary>
    /// "Performer — album · year", with every missing piece simply left out. Nothing is
    /// substituted for an absent tag; an empty line is hidden entirely by the view.
    /// </summary>
    private static string BuildAlbumLine(QueueEntry entry)
    {
        var metadata = entry.Metadata;
        if (metadata is null) return string.Empty;

        var artist = metadata.Artist ?? metadata.AlbumArtist;
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(metadata.Album))
            parts.Add($"{artist} — {metadata.Album}");
        else if (!string.IsNullOrWhiteSpace(artist)) parts.Add(artist);
        else if (!string.IsNullOrWhiteSpace(metadata.Album)) parts.Add(metadata.Album);

        if (metadata.Year is > 0) parts.Add(metadata.Year.Value.ToString());

        return string.Join(" · ", parts);
    }

    public void PlayItem(QueueItemViewModel item)
    {
        var index = Queue.IndexOf(item);
        if (index < 0) return;

        _resume.Discard();
        if (TryPlayback(() => _engine.PlayIndex(index))) IsPlaying = true;
    }

    public void Next()
    {
        _resume.Discard();
        if (TryPlayback(_engine.Next)) IsPlaying = true;
    }

    public void Previous()
    {
        _resume.Discard();
        if (TryPlayback(_engine.Previous)) IsPlaying = true;
    }

    public void TogglePlay()
    {
        // Pierwsze nacisniecie po starcie wznawia utwor przywrocony z poprzedniej sesji.
        if (ResumeRestoredTrack()) return;

        if (TryPlayback(_engine.TogglePlay)) IsPlaying = _engine.State == PlaybackState.Playing;
    }

    public void Pause()
    {
        _engine.Pause();
        IsPlaying = false;
    }

    public void TogglePanel() => PanelOpen = !PanelOpen;

    // ---------- Operacje na kolejce ----------

    public void RemoveFromQueue(QueueItemViewModel item)
    {
        var index = Queue.IndexOf(item);
        if (index < 0) return;

        _engine.RemoveAt(index);
        RebuildQueue();
    }

    public void MoveInQueue(int from, int to)
    {
        if (from < 0 || to < 0 || from == to) return;
        if (from >= Queue.Count || to >= Queue.Count) return;

        _engine.Move(from, to);
        RebuildQueue();
    }

    public void ClearQueue()
    {
        _engine.ClearQueue();
        _current = null;
        _currentMetadata = null;
        _shownBitrate = 0;

        Title = Strings.Current["NoTrack"];
        ArtistLine = string.Empty;
        FormatBadge = string.Empty;
        IsPlaying = false;

        RebuildQueue();
        UpdateArtwork(null);
        PublishToMediaPanel();
    }

    // ---------- Sterowanie z klawiatury ----------

    /// <summary>Moves the position by a number of seconds, forwards or back.</summary>
    public void SeekRelative(double seconds)
    {
        var duration = _engine.Duration;
        if (duration <= TimeSpan.Zero) return;

        var target = _engine.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > duration) target = duration;

        _engine.SeekTo(target);
    }

    public void AdjustVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 1);

    /// <summary>
    /// Mutes and unmutes. The level before muting is remembered, so unmuting returns to it
    /// rather than to some default.
    /// </summary>
    public void ToggleMute()
    {
        if (Volume > 0.0001)
        {
            _volumeBeforeMute = Volume;
            Volume = 0;
        }
        else
        {
            Volume = _volumeBeforeMute > 0.01 ? _volumeBeforeMute : 0.5;
        }
    }

    private double _volumeBeforeMute = 0.5;

    // ---------- Trwałość kolejki ----------

    /// <summary>Writes the queue and position so the next run resumes where this one stopped.</summary>
    public void SaveQueueState()
    {
        var state = new QueueState
        {
            Paths = _engine.QueuePaths.ToList(),
            CurrentIndex = _engine.CurrentIndex,
            PositionSeconds = _engine.Position.TotalSeconds,
            Shuffle = _engine.Shuffle,
            Repeat = _engine.Repeat.ToString(),
        };

        QueueStateStore.Save(state);
    }

    /// <summary>
    /// Restores the previous queue without starting playback. Coming back to a player that
    /// immediately makes noise is rarely what anyone wants; the position is restored so that
    /// pressing play carries on from where it stopped.
    /// </summary>
    public void RestoreQueueState()
    {
        // Ustawienie wyłączone: program zaczyna od pustej kolejki. Zapis kolejki trwa mimo to,
        // żeby ponowne włączenie przywracania miało co przywrócić.
        if (!_settings.Current.RestoreSession) return;

        var state = QueueStateStore.Load();
        if (state is null || state.Paths.Count == 0) return;

        // Pliki mogly zniknac miedzy uruchomieniami.
        var existing = state.Paths.Where(File.Exists).ToList();
        if (existing.Count == 0) return;

        _engine.Enqueue(existing);
        _engine.Shuffle = state.Shuffle;
        _engine.Repeat = state.ReadRepeat();

        RebuildQueue();
        Raise(nameof(ShuffleEnabled));
        Raise(nameof(RepeatEnabled));
        Raise(nameof(RepeatTrack));

        _resume.Arm(state.CurrentIndex, state.PositionSeconds, existing.Count);
    }

    // ---------- Komunikaty ----------

    /// <summary>Jak długo komunikat zostaje na ekranie, zanim zgaśnie sam.</summary>
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _noticeTimer = new() { Interval = NoticeDuration };
    private string _notice = string.Empty;

    /// <summary>
    /// Krótki komunikat o czynności, która się właśnie wykonała — zapisaniu listy, brakujących
    /// plikach. Znika sam po <see cref="NoticeDuration"/>.
    ///
    /// <para>Osobno od <see cref="AudioFailure"/>, bo tamten opisuje stan trwały: dopóki nie ma
    /// wyjścia dźwięku, informacja o tym musi zostać na ekranie. Ten opisuje zdarzenie i po
    /// przeczytaniu nie jest już do niczego potrzebny.</para>
    /// </summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (!Set(ref _notice, value)) return;
            Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice.Length > 0;

    public void ShowNotice(string text)
    {
        Notice = text;

        // Ponowne wywołanie odlicza czas od nowa, zamiast gasić komunikat w połowie czytania.
        _noticeTimer.Stop();
        if (text.Length > 0) _noticeTimer.Start();
    }

    // ---------- Nowsze wydanie ----------

    private string _updateNotice = string.Empty;

    /// <summary>
    /// Informacja o dostępnym nowszym wydaniu. Trwała, nie gasnąca sama: opisuje stan, a nie
    /// zdarzenie, i po zamknięciu okna ustawień ma nadal być widoczna.
    /// </summary>
    public string UpdateNotice
    {
        get => _updateNotice;
        private set
        {
            if (!Set(ref _updateNotice, value)) return;
            Raise(nameof(HasUpdateNotice));
        }
    }

    public bool HasUpdateNotice => _updateNotice.Length > 0;

    /// <summary>Strona wydania, którą otwiera odsyłacz obok komunikatu.</summary>
    public string UpdateNoticeUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Pyta o nowsze wydanie, jeśli użytkownik na to przystał i jeśli minęła doba od ostatniego
    /// sprawdzenia.
    ///
    /// <para>Wywoływane przy otwarciu okna i celowo bez oczekiwania na wynik: uruchomienie
    /// programu nie może zależeć od tego, czy sieć odpowiada. Gdy sprawdzanie jest wyłączone —
    /// a tak jest domyślnie — ta metoda nie wykonuje żadnego połączenia i kończy się od razu.</para>
    /// </summary>
    public async Task CheckForUpdatesIfDueAsync()
    {
        if (!_settings.Current.CheckForUpdates) return;

        var last = _settings.Current.LastUpdateCheck;
        if (last is not null && DateTimeOffset.UtcNow - last.Value < UpdateCheck.AutomaticInterval) return;

        var result = await UpdateCheck.LatestAsync(UpdateCheck.Repository, UpdateCheck.Current);

        _settings.Current.LastUpdateCheck = DateTimeOffset.UtcNow;
        _settings.Touch();

        if (!result.Succeeded)
        {
            // Brak połączenia nie jest wart pokazywania: użytkownik nie prosił o wynik teraz,
            // więc informacja o nieudanej próbie byłaby zawracaniem uwagi.
            Console.Error.WriteLine($"[cewka] sprawdzanie wersji: {result.Failure}");
            return;
        }

        var latest = result.Latest!;
        if (latest <= UpdateCheck.Current) return;

        UpdateNoticeUrl = result.ReleaseUrl ?? UpdateCheck.ReleasesUrl(UpdateCheck.Repository) ?? string.Empty;
        Raise(nameof(UpdateNoticeUrl));

        UpdateNotice = string.Format(Strings.Current["UpdateNotice"], latest.ToString(3));
    }

    // ---------- Listy odtwarzania ----------

    /// <summary>Obecna kolejka w postaci gotowej do zapisania na listę.</summary>
    public IReadOnlyList<PlaylistEntry> QueueAsPlaylist() => Queue
        .Select(item => new PlaylistEntry(item.Path, item.Title, item.Artist, item.DurationSeconds))
        .ToList();

    /// <summary>
    /// Wstawia listę odtwarzania na miejsce obecnej kolejki. Zwraca wynik odczytu, żeby okno
    /// mogło powiedzieć, ilu plików z listy już nie ma.
    ///
    /// <para>Odtwarzanie nie zaczyna się samo. Pierwszy utwór jest tylko uzbrojony tym samym
    /// mechanizmem, którym wraca utwór z poprzedniej sesji, więc pierwsze naciśnięcie
    /// odtwarzania rusza od początku listy. Wczytanie listy jest czynnością porządkową —
    /// program, który po niej od razu zaczyna grać, przerywa to, czego nikt nie kazał przerywać.</para>
    /// </summary>
    public PlaylistLoad LoadPlaylist(string path)
    {
        var load = PlaylistFile.Load(path);
        if (load.Paths.Count == 0) return load;

        _resume.Discard();
        _engine.ClearQueue();
        _engine.Enqueue(load.Paths);

        IsPlaying = false;
        RebuildQueue();

        _resume.Arm(0, 0, load.Paths.Count);

        return load;
    }

    /// <summary>Zapisuje obecną kolejkę jako listę M3U pod wskazaną ścieżką.</summary>
    public void SavePlaylist(string path) => PlaylistFile.Save(path, QueueAsPlaylist());

    private readonly PendingResume _resume = new();

    /// <summary>
    /// Starts the restored track at its saved position. Called when the user first presses
    /// play, so that reopening the application stays silent until asked.
    /// </summary>
    private bool ResumeRestoredTrack()
    {
        // Warunki zużycia i porzucenia zapamiętanej pozycji siedzą w PendingResume; tutaj
        // zostaje samo uruchomienie.
        if (!_resume.TryTake(_engine.CurrentIndex >= 0, out var index, out var seconds)) return false;

        if (!TryPlayback(() => _engine.PlayIndex(index))) return true;

        if (seconds > 1) _engine.SeekTo(TimeSpan.FromSeconds(seconds));

        IsPlaying = true;
        return true;
    }

    // ---------- Odświeżanie ----------

    /// <summary>
    /// Wykonuje odświeżenie od razu, zamiast czekać na zegar. Wyłącznie dla narzędzia zrzutów.
    ///
    /// <para>Zegar odświeżania ma odstęp 60 ms i w zwykłej pracy programu tyka sam. Narzędzie
    /// zrzutów pracuje jednak bez pętli komunikatów — pompuje dyspozytora wywołaniami
    /// <c>RunJobs</c> w pętli — a wtedy zegar potrafi nie dojść do głosu wcale. Sprawdzian
    /// odczytujący czas trwania utworu opierał się przez to na tym, czy pętla odpytująca
    /// przypadkiem zostawi zegarowi dość miejsca: przy uśpieniu 50 ms zostawiała, przy 10 ms
    /// już nie. Tutaj odczyt jest wymuszany wprost, więc czekanie dotyczy wyłącznie tego, aż
    /// dekoder faktycznie otworzy plik.</para>
    /// </summary>
    public void RefreshForCapture() => Refresh();

    private void Refresh()
    {
        Raise(nameof(WaveLevel));

        var duration = _engine.Duration;
        var position = _engine.Position;

        Total = duration > TimeSpan.Zero ? FormatTime(duration.TotalSeconds) : "0:00";

        // Podczas przeciagania suwak nalezy do wskaznika, nie do odtwarzania - inaczej
        // odswiezanie wyrywaloby uchwyt spod kursora.
        if (!_scrubbing)
        {
            Elapsed = FormatTime(position.TotalSeconds);

            // Ustawienie przez pole omija setter, zeby odswiezanie nie wywolalo przewijania.
            _isSeeking = true;
            var progress = duration > TimeSpan.Zero
                ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
                : 0;
            if (Math.Abs(progress - _progress) > 1e-4)
            {
                _progress = progress;
                Raise(nameof(Progress));
            }
            _isSeeking = false;
        }

        // Panel systemowy pyta o pozycję rzadko i we własnym rytmie, więc dostaje ją stąd
        // zamiast sięgać do silnika z obcego wątku.
        _mediaPanel?.SetPosition(position);

        var playing = _engine.State == PlaybackState.Playing;
        if (playing != _isPlaying) IsPlaying = playing;

        RefreshBitrateBadge();

        // Stan zasilania zmienia się rzadko, a odczyt jest wywołaniem systemowym; pięć sekund
        // wystarcza, żeby podłączenie zasilacza zauważyć, a nie kosztuje praktycznie nic.
        if (++_ticksSincePowerCheck < PowerCheckTicks) return;

        _ticksSincePowerCheck = 0;
        if (_settings.Current.Effects == EffectsMode.Auto) UpdateEffects();
    }

    private static readonly int PowerCheckTicks = (int)(5000 / RefreshInterval.TotalMilliseconds);
    private int _ticksSincePowerCheck;

    /// <summary>
    /// Keeps the badge in step with the live bitrate. Only variable-bitrate files are
    /// touched: for everything else the value never changes, and a badge that redraws itself
    /// for no reason is a distraction.
    /// </summary>
    private void RefreshBitrateBadge()
    {
        if (_currentMetadata is not { IsVariableBitrate: true } metadata) return;

        var live = _engine.InstantaneousBitrate;
        if (live <= 0) return;

        // Kwantyzacja do 2 kbps: bez niej ostatnia cyfra migotalaby kilka razy na sekunde.
        var quantised = (int)Math.Round(live / 2.0) * 2;
        if (quantised == _shownBitrate) return;

        _shownBitrate = quantised;
        FormatBadge = metadata.BuildBadge(quantised);
    }

    private int _shownBitrate;

    private void OnTrackChanged(int index)
    {
        if (index < 0 || index >= Queue.Count) return;

        _shownBitrate = 0;

        if (_current is not null) _current.IsCurrent = false;
        _current = Queue[index];
        _current.IsCurrent = true;

        var entry = _engine.Queue.ElementAtOrDefault(index);
        if (entry?.Metadata is { } metadata)
        {
            _currentMetadata = metadata;
            Title = metadata.Title;
            ArtistLine = BuildAlbumLine(entry);
            FormatBadge = metadata.BuildBadge(null);

            // Duration read from the tags is usually more accurate than the decoder's estimate.
            if (metadata.Duration > TimeSpan.Zero)
            {
                _current.Duration = FormatTime(metadata.Duration.TotalSeconds);
                _current.DurationSeconds = metadata.Duration.TotalSeconds;
            }

            UpdateArtwork(metadata.CoverArt);
        }

        PublishToMediaPanel();
        Raise(nameof(CurrentIndex));
    }

    public int CurrentIndex => _current is null ? -1 : Queue.IndexOf(_current);

    private void OnTrackFailed(int index, string reason)
    {
        if (index < 0 || index >= Queue.Count) return;

        Queue[index].IsUnsupported = true;
        Queue[index].UnsupportedReason = reason;
    }

    private void UpdateArtwork(byte[]? bytes)
    {
        var previousCover = _cover;

        try
        {
            if (bytes is { Length: > 0 })
            {
                using var stream = new MemoryStream(bytes);
                Cover = new Bitmap(stream);
                _usingPlaceholderCover = false;
            }
            else
            {
                Cover = LoadPlaceholder();
                _usingPlaceholderCover = true;
            }

            Palette = ExtractPalette();
        }
        catch
        {
            // Uszkodzona okładka nie może zatrzymać odtwarzania.
            Cover = LoadPlaceholder();
            _usingPlaceholderCover = true;
            Palette = ExtractPalette();
        }
        finally
        {
            if (!ReferenceEquals(previousCover, _cover)) previousCover?.Dispose();
        }
    }

    /// <summary>
    /// Swaps the placeholder when the palette changes. Only relevant while the placeholder
    /// is on screen: a real cover belongs to the record, not to the interface, and must not
    /// change with the theme.
    /// </summary>
    public void OnThemeChanged()
    {
        if (!_usingPlaceholderCover)
        {
            Palette = ExtractPalette();
            return;
        }

        var previous = _cover;
        Cover = LoadPlaceholder();
        Palette = ExtractPalette();

        if (!ReferenceEquals(previous, _cover)) previous?.Dispose();
    }

    /// <summary>
    /// Pulls the background colours out of whatever is currently on the record, at the strength
    /// the user chose.
    /// </summary>
    private Color[] ExtractPalette() => CoverPalette.Extract(
        Cover, App.Theme.IsDark, ColourPreferences.Saturation(_settings.Current.ColourIntensity));

    /// <summary>Progi siły plam tła; czytane przez tło okna, które waha się między nimi.</summary>
    public double BackdropMinimum => ColourPreferences.BackdropRange(_settings.Current.ColourIntensity).Minimum;

    public double BackdropMaximum => ColourPreferences.BackdropRange(_settings.Current.ColourIntensity).Maximum;

    /// <summary>
    /// Applies a new colour intensity. Both halves have to move together — the palette is
    /// measured once per track, the background strength is read every frame.
    /// </summary>
    public void SetColourIntensity(ColourIntensity intensity)
    {
        if (_settings.Current.ColourIntensity == intensity) return;

        _settings.Current.ColourIntensity = intensity;
        _settings.Touch();

        Palette = ExtractPalette();
        Raise(nameof(BackdropMinimum));
        Raise(nameof(BackdropMaximum));
    }

    /// <summary>
    /// Applies a new default-cover colour pair. Only visible while a file without its own
    /// cover is loaded, but the palette behind the window changes with it, so the whole
    /// window takes on the new pair.
    /// </summary>
    public void SetPlaceholderPalette(PlaceholderPalette palette)
    {
        if (_settings.Current.PlaceholderPalette == palette) return;

        _settings.Current.PlaceholderPalette = palette;
        _settings.Touch();

        // Wybranie losowania ma dać skutek od razu, a nie dopiero przy następnym utworze.
        _placeholderDraw.Forget();

        if (!_usingPlaceholderCover) return;

        var previous = _cover;
        Cover = LoadPlaceholder();
        Palette = ExtractPalette();

        if (!ReferenceEquals(previous, _cover)) previous?.Dispose();
    }

    private readonly PlaceholderDraw _placeholderDraw = new();

    /// <summary>
    /// The default sleeve: a coil, after the application's name. Drawn rather than loaded, so
    /// the colour pair and the theme both take effect without a file for every combination.
    ///
    /// <para>Para barw przechodzi przez <see cref="PlaceholderDraw"/>, a nie wprost z ustawień:
    /// przy wyborze losowym barwy mają być wylosowane raz na utwór, a ta metoda wołana jest
    /// także przy przewinięciu i przy zmianie motywu.</para>
    /// </summary>
    private Bitmap? LoadPlaceholder()
    {
        try
        {
            var palette = _placeholderDraw.For(_settings.Current.PlaceholderPalette, _current?.Path);
            return CoilCover.Render(palette, App.Theme.IsDark);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTime(double seconds)
    {
        var whole = (int)Math.Max(0, Math.Floor(seconds));
        return $"{whole / 60}:{whole % 60:00}";
    }

    public void Dispose()
    {
        _refresh.Stop();
        _noticeTimer.Stop();
        _engine.Dispose();
        _loudness.Dispose();
        _cover?.Dispose();
    }
}
