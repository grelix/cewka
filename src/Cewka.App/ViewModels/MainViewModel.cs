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
    private readonly string _queueFile;
    private readonly QueueStateWriter _queueWriter;

    private QueueItemViewModel? _current;
    private TrackMetadata? _currentMetadata;
    private int _metadataGeneration;
    private Bitmap? _cover;
    private IReadOnlyList<Color> _palette = [];
    private bool _usingPlaceholderCover = true;
    private bool _panelOpen;
    private bool _queueOpen;
    private bool _isPlaying;
    private bool _isSeeking;
    private double _progress;
    private string _elapsed = "0:00";
    private string _total = "0:00";
    private string _title = string.Empty;
    private string _artistLine = string.Empty;
    private string _formatBadge = string.Empty;

    public MainViewModel(SettingsStore settings)
        : this(settings, new PlaybackEngine(), new LoudnessService(), AppPaths.QueueFile)
    {
    }

    /// <summary>Dla testów: silnik, pomiar głośności i plik kolejki podane z zewnątrz.</summary>
    internal MainViewModel(SettingsStore settings, PlaybackEngine engine, LoudnessService loudness, string queueFile)
    {
        _settings = settings;
        _queueFile = queueFile;
        _queueWriter = new QueueStateWriter(queueFile);
        var s = settings.Current;

        _panelOpen = s.PanelOpen;
        _queueOpen = s.QueueOpen;

        // Jakość konwersji i rozmiar bufora czytane są przy tworzeniu dekodera i urządzenia,
        // więc muszą stać na miejscu, zanim cokolwiek powstanie.
        AudioQuality.ResamplerFilterOrder = AudioPreferences.FilterOrder(s.ResamplerQuality);

        _loudness = loudness;
        _loudness.TargetLufs = AudioPreferences.Lufs(s.LoudnessTarget);
        _loudness.AlwaysAnalyse = s.AlwaysAnalyse;

        _engine = engine;
        _engine.PreferredPeriodSize = AudioPreferences.PeriodFrames(s.OutputLatency);
        _engine.Graph.GainSource = _loudness;
        _engine.Graph.NormalisationEnabled = s.NormalisationEnabled;
        _engine.Graph.Limiter.Enabled = s.LimiterEnabled;
        _engine.Graph.Equaliser.Enabled = s.EqualiserEnabled;
        _engine.Graph.Equaliser.Preamp = s.Preamp;
        _volume = s.Volume;
        _isMuted = s.IsMuted;
        _engine.Graph.Volume.Target = _isMuted ? 0f : (float)VolumeCurve.ToGain(_volume);

        for (var band = 0; band < BandLabels.Length; band++)
            _engine.Graph.Equaliser.SetGain(band, s.EqualiserGains[band]);

        Preamp = new EqBandViewModel("PREAMP", s.Preamp);
        Preamp.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EqBandViewModel.Gain)) return;
            _engine.Graph.Equaliser.Preamp = Preamp.Gain;
            _settings.Current.Preamp = Preamp.Gain;
            _settings.Touch();
            Raise(nameof(CanResetEqualiser));
            RaiseProcessing();
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
                Raise(nameof(CanResetEqualiser));
                RaiseProcessing();
            };
        }

        SoundEffects = BuildSoundEffects(s);

        Queue = [];

        ApplyOutputDevice();
        UpdateEffects();

        _engine.TrackChanged += index => Dispatcher.UIThread.Post(() => OnTrackChanged(index));
        _engine.TrackFailed += (index, failure) => Dispatcher.UIThread.Post(() => OnTrackFailed(index, failure));
        _engine.QueueFinished += () => Dispatcher.UIThread.Post(() => IsPlaying = false);
        _engine.DeviceLost += () => Dispatcher.UIThread.Post(OnDeviceLost);

        _title = Strings.Current["NoTrack"];
        _artistLine = string.Empty;
        _formatBadge = string.Empty;

        // Okładka domyślna od razu, żeby okno nigdy nie pokazało pustej płyty.
        ShowCover(null);

        _themeWatcher = (_, _) => Dispatcher.UIThread.Post(OnThemeChanged);
        App.Theme.Changed += _themeWatcher;

        _themeModeWatcher = (_, _) => RaiseThemeMode();
        App.Theme.ModeChanged += _themeModeWatcher;

        _refresh = new DispatcherTimer { Interval = RefreshInterval };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        _noticeTimer.Tick += (_, _) => ShowNotice(string.Empty);

        _queueSave.Tick += (_, _) =>
        {
            _queueSave.Stop();
            SaveQueueStateInBackground();
        };
    }

    // ---------- Utwór ----------

    public Bitmap? Cover { get => _cover; private set => Set(ref _cover, value); }

    /// <summary>Colours drawn from the cover, driving the animated background.</summary>
    public IReadOnlyList<Color> Palette
    {
        get => _palette;
        private set
        {
            // Ta sama paleta nie może restartować przejścia barw w tle okna.
            if (_palette.SequenceEqual(value)) return;
            Set(ref _palette, value);
        }
    }

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
            if (value) WakeRefresh();

            Raise(nameof(IsPaused));
            _mediaPanel?.SetStatus(value ? MediaPanelStatus.Playing : MediaPanelStatus.Paused);
            MediaKeys.Playing = value;
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
                Elapsed = FormatTime(_engine.Duration.TotalSeconds * Math.Clamp(value, 0, 1), _engine.Duration.TotalHours >= 1);
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
    public void BeginScrub()
    {
        _scrubbing = true;
        WakeRefresh();
    }

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

    /// <summary>
    /// Położenie suwaka głośności, 0–1. Wzmocnienie wynika z niego przez
    /// <see cref="VolumeCurve"/>; przesunięcie suwaka przywraca dźwięk po wyciszeniu.
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            var unmute = _isMuted && clamped > 0;
            if (Math.Abs(_volume - clamped) < 1e-9 && !unmute) return;

            _volume = clamped;
            _settings.Current.Volume = clamped;
            if (unmute) SetMuted(false);

            ApplyVolume();
            _settings.Touch();
            Raise();
            Raise(nameof(VolumeLevelText));
        }
    }

    private double _volume;
    private bool _isMuted;

    /// <summary>Wyciszenie niezależne od położenia suwaka.</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;

            SetMuted(value);
            ApplyVolume();
            _settings.Touch();
        }
    }

    private void SetMuted(bool muted)
    {
        _isMuted = muted;
        _settings.Current.IsMuted = muted;
        Raise(nameof(IsMuted));
    }

    /// <summary>Poziom w procentach, do nazwy dostępności suwaka.</summary>
    public string VolumeLevelText =>
        string.Format(Strings.Current["VolumeLevel"], (int)Math.Round(_volume * 100));

    /// <summary>Wzmocnienie dla toru i poziom dla panelu systemowego, który zna tylko jedną liczbę.</summary>
    private void ApplyVolume()
    {
        _engine.Graph.Volume.Target = _isMuted ? 0f : (float)VolumeCurve.ToGain(_volume);
        _mediaPanel?.SetVolume(PanelVolume);
    }

    private double PanelVolume => _isMuted ? 0 : _volume;

    public bool ShuffleEnabled
    {
        get => _engine.Shuffle;
        set { _engine.Shuffle = value; Raise(); TouchQueue(); }
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

        RaiseRepeat();
        TouchQueue();
    }

    private void RaiseRepeat()
    {
        Raise(nameof(RepeatEnabled));
        Raise(nameof(RepeatTrack));
        Raise(nameof(RepeatQueue));
        Raise(nameof(RepeatTooltip));
    }

    // ---------- Obszary okna ----------

    /// <summary>
    /// Pas dolny: korektor i efekty. Od wersji 0.8.0 niezależny od kolejki.
    ///
    /// <para>Wcześniej jeden stan rządził oboma, bo dzieliły jeden panel u dołu okna. Odkąd
    /// kolejka stoi we własnej kolumnie, są to dwa obszary w dwóch różnych miejscach i wiązanie
    /// ich razem kazało pokazywać korektor komuś, kto chciał tylko zobaczyć, co dalej zagra.</para>
    /// </summary>
    public bool PanelOpen
    {
        get => _panelOpen;
        set
        {
            if (!Set(ref _panelOpen, value)) return;
            _settings.Current.PanelOpen = value;
            _settings.Touch();
            Raise(nameof(ShowProcessingDot));
        }
    }

    /// <summary>Kolumna kolejki po prawej stronie okna.</summary>
    public bool QueueOpen
    {
        get => _queueOpen;
        set
        {
            if (!Set(ref _queueOpen, value)) return;
            _settings.Current.QueueOpen = value;
            _settings.Touch();
        }
    }

    /// <summary>
    /// Dźwięk zmienia korektor z niepłaską krzywą albo włączony efekt. Limiter i normalizacja są
    /// włączone domyślnie i pilnują tylko poziomu, więc z nimi kropka świeciłaby zawsze.
    /// </summary>
    public bool ProcessingActive =>
        (EqualiserEnabled && CanResetEqualiser) || SoundEffects.Any(effect => effect.Enabled);

    /// <summary>Kropka na przycisku pasa: przetwarzanie działa, a pasa nie widać.</summary>
    public bool ShowProcessingDot => !PanelOpen && ProcessingActive;

    private void RaiseProcessing()
    {
        Raise(nameof(ProcessingActive));
        Raise(nameof(ShowProcessingDot));
    }

    // ---------- Motyw ----------

    public ThemeMode ThemeMode => App.Theme.Mode;

    public bool ThemeIsSystem => ThemeMode == ThemeMode.System;

    public bool ThemeIsDark => ThemeMode == ThemeMode.Dark;

    public bool ThemeIsLight => ThemeMode == ThemeMode.Light;

    /// <summary>Podpowiedź i nazwa przycisku motywu z bieżącym trybem i skrótem.</summary>
    public string ThemeTooltip => Strings.Current[ThemeMode switch
    {
        ThemeMode.Dark => "TooltipThemeDark",
        ThemeMode.Light => "TooltipThemeLight",
        _ => "TooltipThemeSystem",
    }];

    private void RaiseThemeMode()
    {
        Raise(nameof(ThemeMode));
        Raise(nameof(ThemeIsSystem));
        Raise(nameof(ThemeIsDark));
        Raise(nameof(ThemeIsLight));
        Raise(nameof(ThemeTooltip));
    }

    /// <summary>Where the window buttons sit; changed from the settings window.</summary>
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
        RebuildQueue();
        if (_current is null) Title = IdleTitle;
        RaiseRepeat();
        Raise(nameof(AudioFailure));
        Raise(nameof(UpdateNotice));
        Raise(nameof(LatencyDescription));
        Raise(nameof(VolumeLevelText));
        Raise(nameof(ThemeTooltip));
        Preamp.RefreshText();
        foreach (var band in Bands) band.RefreshText();

        foreach (var effect in SoundEffects) effect.RefreshLabels();
    }

    // ---------- Efekty dźwiękowe ----------

    /// <summary>
    /// Pięć efektów w kolejności, w jakiej stoją w oknie. Każdy ma przełącznik i suwak siły,
    /// a zmiana jednego i drugiego trafia od razu do łańcucha i do zapisanych ustawień.
    /// </summary>
    public IReadOnlyList<EffectViewModel> SoundEffects { get; }

    private IReadOnlyList<EffectViewModel> BuildSoundEffects(AppSettings s)
    {
        var effects = new[]
        {
            new EffectViewModel("Crossfeed", s.CrossfeedEnabled, s.CrossfeedStrength),
            new EffectViewModel("Loudness", s.LoudnessEnabled, s.LoudnessStrength),
            new EffectViewModel("VirtualBass", s.VirtualBassEnabled, s.VirtualBassStrength),
            new EffectViewModel("DynamicRange", s.DynamicRangeEnabled, s.DynamicRangeStrength),
            new EffectViewModel("StereoWidth", s.StereoWidthEnabled, s.StereoWidthStrength),
        };

        foreach (var effect in effects)
        {
            ApplyEffect(effect);

            var captured = effect;
            var stored = (effect.Enabled, effect.Strength);
            captured.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(EffectViewModel.Enabled)
                    && e.PropertyName != nameof(EffectViewModel.Strength)) return;

                // Suwak wracający na stopień zgłasza zmianę bez zmiany wartości.
                if ((captured.Enabled, captured.Strength) == stored) return;
                stored = (captured.Enabled, captured.Strength);

                ApplyEffect(captured);
                StoreEffect(captured);
                _settings.Touch();
                RaiseProcessing();
            };
        }

        return effects;
    }

    /// <summary>
    /// Przenosi stan jednego efektu do łańcucha. Siła podawana jest stopniom w procentach,
    /// a interfejs trzyma ją w zakresie 0–1, bo tego wymaga suwak — stąd mnożenie w jednym
    /// miejscu zamiast dwóch różnych jednostek krążących po całym programie.
    /// </summary>
    private void ApplyEffect(EffectViewModel effect)
    {
        var graph = _engine.Graph;
        var percent = effect.Strength * 100;

        switch (effect.Key)
        {
            case "Crossfeed":
                graph.Crossfeed.Enabled = effect.Enabled;
                graph.Crossfeed.Strength = percent;
                break;
            case "Loudness":
                graph.Loudness.Enabled = effect.Enabled;
                graph.Loudness.Strength = percent;
                break;
            case "VirtualBass":
                graph.VirtualBass.Enabled = effect.Enabled;
                graph.VirtualBass.Strength = percent;
                break;
            case "DynamicRange":
                graph.DynamicRange.Enabled = effect.Enabled;
                graph.DynamicRange.Strength = percent;
                break;
            case "StereoWidth":
                graph.StereoWidth.Enabled = effect.Enabled;
                graph.StereoWidth.Strength = percent;
                break;
        }
    }

    private void StoreEffect(EffectViewModel effect)
    {
        var s = _settings.Current;

        switch (effect.Key)
        {
            case "Crossfeed":
                s.CrossfeedEnabled = effect.Enabled;
                s.CrossfeedStrength = effect.Strength;
                break;
            case "Loudness":
                s.LoudnessEnabled = effect.Enabled;
                s.LoudnessStrength = effect.Strength;
                break;
            case "VirtualBass":
                s.VirtualBassEnabled = effect.Enabled;
                s.VirtualBassStrength = effect.Strength;
                break;
            case "DynamicRange":
                s.DynamicRangeEnabled = effect.Enabled;
                s.DynamicRangeStrength = effect.Strength;
                break;
            case "StereoWidth":
                s.StereoWidthEnabled = effect.Enabled;
                s.StereoWidthStrength = effect.Strength;
                break;
        }
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

        _mediaPanel.SetVolume(PanelVolume);

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

    /// <summary>
    /// Czy tryb automatyczny ma ograniczyć ruch: praca na baterii albo animacje wyłączone
    /// w systemie. Testy podstawiają własną odpowiedź.
    /// </summary>
    internal Func<bool> AutomaticReduction { get; set; } =
        () => PowerStatus.IsOnBattery || !SystemAnimations.Enabled;

    internal void UpdateEffects()
    {
        var reduced = _settings.Current.Effects switch
        {
            EffectsMode.Reduced => true,
            EffectsMode.Full => false,
            _ => AutomaticReduction(),
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
        RaiseOutputState();
    }

    /// <summary>
    /// Points the engine at the chosen device. A name that no longer matches anything — the
    /// headset was unplugged since it was chosen — falls back to the system default rather than
    /// leaving the application silent; the engine resolves the name when it opens the device.
    ///
    /// <para>Urządzenie obecne na liście może mimo to odmówić otwarcia: słuchawki zdążyły się
    /// rozłączyć, inny program zajął je na wyłączność. Grający utwór przechodzi wtedy na
    /// urządzenie domyślne, a komunikat zostaje na ekranie, dopóki gra ono zamiast wybranego.
    /// Wstrzymany zostaje przy wybranym urządzeniu, żeby następne odtworzenie spróbowało je
    /// otworzyć jeszcze raz.</para>
    /// </summary>
    private void ApplyOutputDevice()
    {
        var chosen = ChosenDevice;
        var wasPlaying = _isPlaying;

        if (TryPlayback(() => _engine.SwitchDevice(chosen)) || chosen is null || !wasPlaying) return;

        _engine.SwitchDevice(null);
        if (TryPlayback(_engine.Play)) SyncPlaying();
    }

    private string? ChosenDevice =>
        string.IsNullOrEmpty(_settings.Current.OutputDevice) ? null : _settings.Current.OutputDevice;

    /// <summary>Gra urządzenie domyślne, bo wybrane odmówiło otwarcia.</summary>
    private bool OnFallbackDevice => _engine.IsDeviceOpen && _engine.PreferredDeviceName != ChosenDevice;

    /// <summary>Stan wyjścia mógł się zmienić: otwarcie, zamknięcie albo inne urządzenie.</summary>
    private void RaiseOutputState()
    {
        Raise(nameof(IsOutputOpen));
        Raise(nameof(ActiveDeviceName));
        Raise(nameof(LatencyDescription));
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
        TryPlayback(_engine.Reopen);
        RaiseOutputState();
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
            return string.Format(Strings.Current.Numbers, Strings.Current["LatencyMeasured"], frames, milliseconds);
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
            RaiseProcessing();
        }
    }

    public double EqualiserOpacity => EqualiserEnabled ? 1.0 : 0.4;

    /// <summary>Polecenie zerowania ma sens tylko wtedy, gdy któreś pasmo albo przedwzmacniacz odbiega od zera.</summary>
    public bool CanResetEqualiser => Preamp.Gain != 0 || Bands.Any(band => band.Gain != 0);

    /// <summary>Ustawia wszystkie pasma i przedwzmacniacz na 0 dB.</summary>
    public void ResetEqualiser()
    {
        foreach (var band in Bands) band.Gain = 0;
        Preamp.Gain = 0;
    }

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
            RaiseProcessing();
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
            RaiseProcessing();
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
    public Task OpenPaths(IEnumerable<string> paths, bool replace = true) =>
        Open(paths, files => Load(files, replace));

    /// <summary>
    /// Dokłada pliki do kolejki, a pustą wypełnia nimi i zaczyna grać. O tym, czy kolejka jest
    /// pusta, rozstrzyga chwila dołożenia: folder przeglądany w tle mógł ją w międzyczasie wypełnić.
    /// </summary>
    public Task AddPaths(IEnumerable<string> paths) =>
        Open(paths, files => Load(files, replace: IsQueueEmpty));

    private void Load(List<string> files, bool replace)
    {
        if (replace)
        {
            _resume.Discard();
            if (TryPlayback(() => _engine.SetQueue(files))) SyncPlaying();
            RebuildQueue();
        }
        else
        {
            _engine.Enqueue(files);
            AppendRows();
        }
    }

    private Task _openChain = Task.CompletedTask;

    /// <summary>
    /// Zamienia ścieżki na pliki dźwiękowe i przekazuje je dalej. Same pliki idą od razu;
    /// foldery są przeglądane poza wątkiem interfejsu, bo duży katalog na wolnym dysku
    /// zamroziłby okno. Kolejne otwarcia czekają na poprzednie, więc dwa szybkie upuszczenia
    /// nie zamienią się kolejnością.
    /// </summary>
    private Task Open(IEnumerable<string> paths, Action<List<string>> apply)
    {
        var list = paths.ToList();

        if (_openChain.IsCompleted && !list.Any(Directory.Exists))
        {
            var files = ExpandToAudioFiles(list, out _);
            if (files.Count > 0) apply(files);
            return Task.CompletedTask;
        }

        return _openChain = OpenAfterAsync(_openChain, list, apply);
    }

    private async Task OpenAfterAsync(Task previous, List<string> paths, Action<List<string>> apply)
    {
        await previous;

        try
        {
            string? unreadable = null;
            var files = await Task.Run(() => ExpandToAudioFiles(paths, out unreadable));
            if (_disposed) return;

            if (files.Count > 0) apply(files);
            if (unreadable is not null)
                ShowNotice(string.Format(Strings.Current["FolderReadFailed"], FolderName(unreadable)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się dodać plików: {ex}");
        }
    }

    private static string FolderName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }

    /// <summary>
    /// Bierze pliki przychodzące spoza programu — z wiersza polecenia, z „Otwórz za pomocą",
    /// od drugiej kopii programu albo upuszczone na okno — i postępuje z nimi tak, jak wybrano
    /// w ustawieniach.
    ///
    /// <para>Wybór plików wewnątrz programu (<c>Ctrl+O</c>) tą drogą nie idzie: tam użytkownik
    /// właśnie nacisnął „dodaj", więc pytanie, czy dodać, byłoby już rozstrzygnięte.</para>
    /// </summary>
    public Task OpenFromOutside(IEnumerable<string> paths) => Open(paths, files =>
    {
        switch (_settings.Current.FileOpenAction)
        {
            case FileOpenAction.ReplaceAndPlay:
                _resume.Discard();
                if (TryPlayback(() => _engine.SetQueue(files))) SyncPlaying();
                RebuildQueue();
                break;

            case FileOpenAction.AppendAndPlay:
            {
                // Numer pierwszego dołożonego pliku trzeba odczytać przed dołożeniem: potem
                // długość kolejki obejmuje już nowe pozycje i nie wiadomo, gdzie się zaczynają.
                var first = Queue.Count;
                _engine.Enqueue(files);
                AppendRows();
                _resume.Discard();
                if (TryPlayback(() => _engine.PlayIndex(first))) SyncPlaying();
                RestoreCurrentMarker();
                break;
            }

            default:
                // Pusta kolejka to przypadek osobny: nie ma czego przerywać, a program, który
                // po otwarciu pliku milczy, wygląda na zepsuty.
                if (IsQueueEmpty)
                {
                    if (TryPlayback(() => _engine.SetQueue(files))) SyncPlaying();
                    RebuildQueue();
                }
                else
                {
                    _engine.Enqueue(files);
                    AppendRows();
                }

                break;
        }
    });

    // ---------- Awaria wyjścia dźwięku ----------

    /// <summary>Klucz słownika z opisem awarii; pusty, gdy wszystko działa.</summary>
    private string _audioFailureKey = string.Empty;

    /// <summary>
    /// Why playback is impossible, or empty when all is well.
    /// <para>
    /// Opening the output device can fail for reasons that have nothing to do with the
    /// application: no sound card, a device taken exclusively by something else, a missing
    /// native library in a hand-assembled build. None of them is a reason to take the window
    /// down — the queue, the settings and the reason itself are all still worth showing.
    /// Przyczyna jest opisana zwykłymi słowami w języku interfejsu; tekst wyjątku trafia
    /// wyłącznie do dziennika.
    /// </para>
    /// </summary>
    public string AudioFailure => _audioFailureKey.Length == 0 ? string.Empty : Strings.Current[_audioFailureKey];

    public bool HasAudioFailure => _audioFailureKey.Length > 0;

    private void SetAudioFailure(string key)
    {
        if (_audioFailureKey == key) return;

        _audioFailureKey = key;
        Raise(nameof(AudioFailure));
        Raise(nameof(HasAudioFailure));
    }

    /// <summary>
    /// Polecenie „Spróbuj ponownie” przy komunikacie o awarii: otwiera wyjście od nowa
    /// i wznawia to, co miało grać.
    /// </summary>
    public void RetryAudio()
    {
        if (_engine.PreferredDeviceName != ChosenDevice)
        {
            ApplyOutputDevice();
            if (_isPlaying || HasAudioFailure) return;
        }

        if (ResumeRestoredTrack()) return;

        if (Queue.Count == 0)
        {
            SetAudioFailure(string.Empty);
            return;
        }

        if (TryPlayback(_engine.Play)) SyncPlaying();
    }

    /// <summary>Polecenie „Wybierz urządzenie”: okno otwiera Ustawienia na sekcji dźwięku.</summary>
    public event Action? DeviceSelectionRequested;

    public void RequestDeviceSelection() => DeviceSelectionRequested?.Invoke();

    /// <summary>Runs an engine operation, turning a failure into a message instead of a crash.</summary>
    private bool TryPlayback(Action operation)
    {
        var wasOpen = _engine.IsDeviceOpen;

        try
        {
            operation();
            SetAudioFailure(OnFallbackDevice ? "AudioFailureFallback" : string.Empty);

            if (wasOpen != _engine.IsDeviceOpen)
            {
                RaiseOutputState();
                if (_engine.UsingFallbackDevice) ShowNotice(Strings.Current["DeviceFallbackNotice"]);
            }

            return true;
        }
        catch (Exception ex)
        {
            SetAudioFailure(FailureText.AudioFailureKey(ex));
            Console.Error.WriteLine($"[cewka] wyjście dźwięku: {ex}");

            IsPlaying = false;
            RaiseOutputState();
            return false;
        }
    }

    /// <summary>Urządzenie przestało pracować samo, na przykład odłączono słuchawki.</summary>
    private void OnDeviceLost()
    {
        IsPlaying = false;
        RaiseOutputState();
        ShowNotice(Strings.Current["DeviceLostNotice"]);
    }

    /// <summary>
    /// Folders are walked recursively; anything unrecognised is left out. W każdym katalogu
    /// najpierw pliki, potem podkatalogi, jedne i drugie w porządku naturalnym: „2 - y” przed
    /// „10 - x”, tak jak w menedżerze plików.
    ///
    /// <para>Folder, którego nie da się przejrzeć, jest pomijany, a pierwszy taki trafia do
    /// <paramref name="unreadable"/>. Nieczytelny podkatalog pomija się po cichu.</para>
    /// </summary>
    private static List<string> ExpandToAudioFiles(IEnumerable<string> paths, out string? unreadable)
    {
        unreadable = null;
        var result = new List<string>();
        var visited = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    var folder = new DirectoryInfo(path);
                    var real = IsLink(folder) ? folder.ResolveLinkTarget(returnFinalTarget: true)?.FullName : null;
                    WalkFolder(folder, real ?? folder.FullName, visited, result, root: true);
                }
                catch (Exception ex) when (IsFolderError(ex))
                {
                    Console.Error.WriteLine($"[cewka] folder „{path}”: {ex.Message}");
                    unreadable ??= path;
                }
            }
            else if (File.Exists(path) && IsAudio(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static readonly EnumerationOptions FolderOptions = new() { IgnoreInaccessible = true };

    /// <summary>
    /// Dowiązania do katalogów są przeglądane, ale każdy katalog tylko raz, rozpoznany po
    /// ścieżce docelowej <paramref name="real"/>. Dowiązanie do katalogu nadrzędnego dałoby
    /// inaczej rekurencję bez końca, a do innej części biblioteki — powtórzone pliki.
    /// </summary>
    private static void WalkFolder(DirectoryInfo folder, string real, HashSet<string> visited, List<string> result, bool root)
    {
        if (!visited.Add(Path.TrimEndingDirectorySeparator(real))) return;

        List<string> files;
        List<DirectoryInfo> folders;

        try
        {
            files = folder.EnumerateFiles("*", FolderOptions).Select(file => file.FullName).Where(IsAudio).ToList();
            folders = folder.EnumerateDirectories("*", FolderOptions).ToList();
        }
        catch (Exception ex) when (!root && IsFolderError(ex))
        {
            Console.Error.WriteLine($"[cewka] pominięto podkatalog „{folder.FullName}”: {ex.Message}");
            return;
        }

        files.Sort(NaturalOrder.Instance);
        result.AddRange(files);

        folders.Sort((a, b) => NaturalOrder.Instance.Compare(a.FullName, b.FullName));
        foreach (var child in folders)
        {
            string? childReal;

            try
            {
                childReal = IsLink(child)
                    ? child.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    : Path.Combine(real, child.Name);
            }
            catch (Exception ex) when (IsFolderError(ex))
            {
                childReal = null;
            }

            if (childReal is not null) WalkFolder(child, childReal, visited, result, root: false);
        }
    }

    /// <summary>Dowiązanie symboliczne albo punkt połączenia; pliki chmurowe OneDrive nim nie są.</summary>
    private static bool IsLink(DirectoryInfo folder) =>
        (folder.Attributes & FileAttributes.ReparsePoint) != 0 && folder.LinkTarget is not null;

    private static bool IsFolderError(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static bool IsAudio(string path) =>
        AudioFileFormatDetector.SupportedExtensions
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Wiersze od nowa dla całej kolejki. Potrzebne przy jej zastąpieniu i zmianie języka;
    /// przesunięcie, usunięcie i dołożenie zmieniają tylko dotknięte wiersze.
    /// </summary>
    private void RebuildQueue()
    {
        Interlocked.Increment(ref _metadataGeneration);
        _current = null;
        Queue.Clear();

        var entries = _engine.Queue;
        for (var i = 0; i < entries.Count; i++) Queue.Add(CreateRow(entries[i], i + 1));

        RestoreCurrentMarker();
        QueueChanged();
        _ = FillMetadataInBackgroundAsync(entries);
    }

    /// <summary>Dokłada wiersze dla wpisów dodanych na końcu kolejki silnika.</summary>
    private void AppendRows()
    {
        var entries = _engine.Queue;
        var added = new List<QueueEntry>();

        for (var i = Queue.Count; i < entries.Count; i++)
        {
            Queue.Add(CreateRow(entries[i], i + 1));
            added.Add(entries[i]);
        }

        QueueChanged();
        _ = FillMetadataInBackgroundAsync(added);
    }

    private void QueueChanged()
    {
        Raise(nameof(QueueSummary));
        Raise(nameof(IsQueueEmpty));
        if (_current is null) Title = IdleTitle;
        TouchQueue();
    }

    /// <summary>Numery od <paramref name="from"/> do końca, po przesunięciu albo usunięciu.</summary>
    private void Renumber(int from)
    {
        for (var i = Math.Max(0, from); i < Queue.Count; i++) Queue[i].Number = i + 1;
    }

    private static QueueItemViewModel CreateRow(QueueEntry entry, int number)
    {
        var row = new QueueItemViewModel
        {
            Number = number,
            Entry = entry,
            Title = entry.Title,
            Artist = Strings.Current["UnknownArtist"],
            Duration = FormatTime(entry.Duration.TotalSeconds),
            Path = entry.Path,
        };

        ApplyMetadataToRow(row, entry);
        return row;
    }

    /// <summary>
    /// Re-attaches the „now playing" marker after the rows changed.
    /// <para>
    /// The engine knows which entry is playing, so the marker is re-derived from it rather
    /// than remembered.
    /// </para>
    /// </summary>
    private void RestoreCurrentMarker()
    {
        var index = _engine.CurrentIndex;
        var row = index >= 0 && index < Queue.Count ? Queue[index] : null;

        if (!ReferenceEquals(row, _current) && _current is not null) _current.IsCurrent = false;

        _current = row;
        if (_current is not null) _current.IsCurrent = true;

        Raise(nameof(CurrentIndex));
    }

    /// <summary>
    /// Reads tags away from the interface thread, only for entries not read yet.
    /// <para>
    /// Adding a folder has to feel instant, so entries appear straight away with nothing but
    /// their file names. The durations and titles then fill in as the tags are read. Reading
    /// two thousand files takes a few seconds; doing it up front would freeze the window for
    /// exactly that long. Wynik trafia do wiersza tego samego wpisu, bo w międzyczasie wiersz
    /// mógł zostać przesunięty.
    /// </para>
    /// </summary>
    private async Task FillMetadataInBackgroundAsync(IReadOnlyList<QueueEntry> entries)
    {
        var pending = entries.Where(entry => entry.Metadata is null).ToList();
        if (pending.Count == 0) return;

        var token = Volatile.Read(ref _metadataGeneration);

        await Task.Run(() =>
        {
            foreach (var entry in pending)
            {
                if (token != Volatile.Read(ref _metadataGeneration)) return;

                try { entry.EnsureMetadata(); }
                catch { continue; }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token != _metadataGeneration || RowFor(entry) is not { } row) return;
                    ApplyMetadataToRow(row, entry);
                }, DispatcherPriority.Background);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token == _metadataGeneration) Raise(nameof(QueueSummary));
            }, DispatcherPriority.Background);
        });
    }

    private QueueItemViewModel? RowFor(QueueEntry entry)
    {
        foreach (var row in Queue)
            if (ReferenceEquals(row.Entry, entry)) return row;

        return null;
    }

    /// <summary>Jedna reguła dla wiersza nowego i uzupełnianego: wykonawca albo wykonawca albumu.</summary>
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
        row.UnsupportedReason = FailureText.ForTrack(TrackFailureReason.UnsupportedFormat, entry.Format);
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
        if (TryPlayback(() => _engine.PlayIndex(index))) SyncPlaying();
    }

    public void Next()
    {
        _resume.Discard();
        if (TryPlayback(_engine.Next)) SyncPlaying();
    }

    public void Previous()
    {
        _resume.Discard();
        if (TryPlayback(_engine.Previous)) SyncPlaying();
    }

    public void TogglePlay()
    {
        // Pierwsze nacisniecie po starcie wznawia utwor przywrocony z poprzedniej sesji.
        if (ResumeRestoredTrack()) return;

        // Pusta kolejka: nie ma czego grać, więc nie ma też powodu zajmować karty dźwiękowej.
        if (Queue.Count == 0) return;

        if (TryPlayback(_engine.TogglePlay)) IsPlaying = _engine.State == PlaybackState.Playing;
    }

    /// <summary>Stan przycisku wprost z silnika: „Poprzedni” w pauzie tylko przewija i nie wznawia gry.</summary>
    private void SyncPlaying() => IsPlaying = _engine.State == PlaybackState.Playing;

    public void Pause()
    {
        _engine.Pause();
        IsPlaying = false;
    }

    public void TogglePanel() => PanelOpen = !PanelOpen;

    public void ToggleQueue() => QueueOpen = !QueueOpen;

    // ---------- Operacje na kolejce ----------

    /// <summary>
    /// Usuwa wpis z kolejki. Usunięcie grającego utworu zatrzymuje odtwarzanie i nie uruchamia
    /// niczego samo; pierwsze naciśnięcie odtwarzania rusza od utworu, który zajął jego miejsce.
    /// </summary>
    public void RemoveFromQueue(QueueItemViewModel item)
    {
        var index = Queue.IndexOf(item);
        if (index < 0) return;

        var wasCurrent = _engine.CurrentIndex == index;

        _engine.RemoveAt(index);
        _resume.OnRemoved(index);

        Queue.RemoveAt(index);
        Renumber(index);
        QueueChanged();

        if (!wasCurrent || _engine.CurrentIndex >= 0)
        {
            RestoreCurrentMarker();
            return;
        }

        ResetNowPlaying();
        if (Queue.Count > 0) _resume.Arm(Math.Min(index, Queue.Count - 1), 0, Queue.Count);
    }

    public void MoveInQueue(int from, int to)
    {
        if (from < 0 || to < 0 || from == to) return;
        if (from >= Queue.Count || to >= Queue.Count) return;

        _engine.Move(from, to);
        _resume.OnMoved(from, to);

        Queue.Move(from, to);
        Renumber(Math.Min(from, to));
        RestoreCurrentMarker();
        QueueChanged();
    }

    public void ClearQueue()
    {
        _engine.ClearQueue();
        _resume.Discard();

        RebuildQueue();
        ResetNowPlaying();
    }

    /// <summary>
    /// Zdejmuje z okna i z panelu systemowego wszystko, co dotyczyło utworu, który przestał
    /// być bieżący: tytuł, wykonawcę, plakietkę formatu i okładkę.
    /// </summary>
    private void ResetNowPlaying()
    {
        if (_current is not null) _current.IsCurrent = false;

        _current = null;
        _currentMetadata = null;
        _shownBitrate = 0;

        Title = IdleTitle;
        ArtistLine = string.Empty;
        FormatBadge = string.Empty;
        IsPlaying = _engine.State == PlaybackState.Playing;

        _shownEntry = null;
        _covers.Cancel();
        ShowCover(null);
        PublishToMediaPanel();
        Raise(nameof(CurrentIndex));
    }

    /// <summary>Tytuł, gdy nic nie jest wczytane: inny przy pustej kolejce, inny przy pełnej.</summary>
    private string IdleTitle => Strings.Current[Queue.Count == 0 ? "NoTrack" : "NothingPlaying"];

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
    /// Mutes and unmutes. The slider keeps its place while muted, so unmuting returns to it;
    /// a slider left at zero comes back at half, because silence is not what unmuting asks for.
    /// </summary>
    public void ToggleMute()
    {
        if (_isMuted)
        {
            IsMuted = false;
            if (_volume <= 0) Volume = 0.5;
        }
        else
        {
            IsMuted = true;
        }
    }

    // ---------- Trwałość kolejki ----------

    /// <summary>
    /// Odstęp zapisu po zmianie kolejki. Seria zmian (przeciąganie, usuwanie kilku wpisów)
    /// daje jeden zapis, a awaria albo zabicie procesu nie gubi zmian z całej sesji.
    /// </summary>
    private readonly DispatcherTimer _queueSave = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Co tyle w trakcie gry zapisywana jest pozycja w utworze.</summary>
    private static readonly TimeSpan PositionSaveInterval = TimeSpan.FromSeconds(30);

    private DateTime _lastPositionSave = DateTime.UtcNow;

    /// <summary>Zapis kolejki jest zamówiony i czeka na zegar.</summary>
    internal bool QueueSavePending => _queueSave.IsEnabled;

    /// <summary>Zamawia zapis kolejki za chwilę.</summary>
    private void TouchQueue()
    {
        _queueSave.Stop();
        _queueSave.Start();
    }

    /// <summary>
    /// Writes the queue and position so the next run resumes where this one stopped. Wołane
    /// przy zamykaniu, więc zapisuje od razu; zapisy w trakcie pracy idą przez
    /// <see cref="SaveQueueStateInBackground"/>.
    /// </summary>
    public void SaveQueueState() => _queueWriter.SaveNow(CaptureQueueState());

    internal Task SaveQueueStateInBackground() => _queueWriter.SaveInBackground(CaptureQueueState());

    private QueueState CaptureQueueState()
    {
        _queueSave.Stop();
        _lastPositionSave = DateTime.UtcNow;

        // Dopóki nic nie zagrało, silnik nie zna pozycji z poprzedniej sesji; czeka ona
        // w _resume i to ją trzeba zapisać, inaczej samo otwarcie i zamknięcie programu ją kasuje.
        var armed = _resume.IsArmed;

        return new QueueState
        {
            Paths = _engine.QueuePaths.ToList(),
            CurrentIndex = armed ? _resume.Index : _engine.CurrentIndex,
            PositionSeconds = armed ? _resume.Seconds : _engine.Position.TotalSeconds,
            Shuffle = _engine.Shuffle,
            Repeat = _engine.Repeat.ToString(),
        };
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

        var state = QueueStateStore.Load(_queueFile);
        if (state is null || state.Paths.Count == 0) return;

        // Pliki mogły zniknąć między uruchomieniami. Numer bieżącego wpisu liczy się po
        // odfiltrowaniu, żeby wskazywał ten sam plik; gdy zniknął on sam, wznowienia nie ma.
        var existing = new List<string>(state.Paths.Count);
        var resumeIndex = -1;

        for (var i = 0; i < state.Paths.Count; i++)
        {
            if (!File.Exists(state.Paths[i])) continue;
            if (i == state.CurrentIndex) resumeIndex = existing.Count;
            existing.Add(state.Paths[i]);
        }

        if (existing.Count == 0) return;

        _engine.Enqueue(existing);
        _engine.Shuffle = state.Shuffle;
        _engine.Repeat = state.ReadRepeat();

        RebuildQueue();
        Raise(nameof(ShuffleEnabled));
        RaiseRepeat();

        if (_current is null) Title = IdleTitle;

        _resume.Arm(resumeIndex, state.PositionSeconds, existing.Count);
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
        _skippedInNotice = 0;
        Notice = text;

        // Ponowne wywołanie odlicza czas od nowa, zamiast gasić komunikat w połowie czytania.
        _noticeTimer.Stop();
        if (text.Length > 0) _noticeTimer.Start();
    }

    // ---------- Nowsze wydanie ----------

    private Version? _availableUpdate;

    /// <summary>
    /// Informacja o dostępnym nowszym wydaniu. Trwała, nie gasnąca sama: opisuje stan, a nie
    /// zdarzenie, i po zamknięciu okna ustawień ma nadal być widoczna. Składana przy odczycie,
    /// żeby zmiana języka obejmowała i ją.
    /// </summary>
    public string UpdateNotice => _availableUpdate is null
        ? string.Empty
        : string.Format(Strings.Current["UpdateNotice"], _availableUpdate.ToString(3));

    public bool HasUpdateNotice => _availableUpdate is not null;

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

        if (!result.Succeeded)
        {
            // Brak połączenia nie jest wart pokazywania: użytkownik nie prosił o wynik teraz,
            // więc informacja o nieudanej próbie byłaby zawracaniem uwagi. Data sprawdzenia
            // nie jest zapisywana, żeby laptop bez sieci tuż po wybudzeniu nie tracił próby na dobę.
            Console.Error.WriteLine($"[cewka] sprawdzanie wersji: {result.Failure}");
            return;
        }

        _settings.Current.LastUpdateCheck = DateTimeOffset.UtcNow;
        _settings.Touch();

        var latest = result.Latest!;
        if (latest <= UpdateCheck.Current) return;

        UpdateNoticeUrl = result.ReleaseUrl ?? UpdateCheck.ReleasesUrl(UpdateCheck.Repository) ?? string.Empty;
        Raise(nameof(UpdateNoticeUrl));

        _availableUpdate = latest;
        Raise(nameof(UpdateNotice));
        Raise(nameof(HasUpdateNotice));
    }

    // ---------- Listy odtwarzania ----------

    /// <summary>Obecna kolejka w postaci gotowej do zapisania na listę.</summary>
    /// <remarks>
    /// Z wpisów silnika, bo wiersz zastępuje brak wykonawcy napisem w języku interfejsu,
    /// który na liście udawałby prawdziwego wykonawcę.
    /// </remarks>
    public IReadOnlyList<PlaylistEntry> QueueAsPlaylist() => _engine.Queue
        .Select(entry => new PlaylistEntry(
            entry.Path,
            entry.Title,
            entry.Metadata?.Artist ?? entry.Metadata?.AlbumArtist,
            entry.Duration.TotalSeconds))
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

        RebuildQueue();
        ResetNowPlaying();

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

        // Pozycja za końcem utworu (plik podmieniono na krótszy) zaczyna go od początku,
        // zamiast od razu przeskakiwać do następnego.
        var duration = _engine.Queue.ElementAtOrDefault(index)?.Duration ?? TimeSpan.Zero;
        if (seconds <= 1 || (duration > TimeSpan.Zero && seconds >= duration.TotalSeconds - 1)) seconds = 0;

        if (TryPlayback(() => _engine.PlayIndex(index, TimeSpan.FromSeconds(seconds))))
            IsPlaying = _engine.State == PlaybackState.Playing;

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
        var hours = duration.TotalHours >= 1;

        Total = duration > TimeSpan.Zero ? FormatTime(duration.TotalSeconds, hours) : "0:00";

        // Podczas przeciagania suwak nalezy do wskaznika, nie do odtwarzania - inaczej
        // odswiezanie wyrywaloby uchwyt spod kursora.
        if (!_scrubbing)
        {
            Elapsed = FormatTime(position.TotalSeconds, hours);

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

        if (playing && DateTime.UtcNow - _lastPositionSave > PositionSaveInterval) SaveQueueStateInBackground();

        RefreshBitrateBadge();

        // Bez gry wartości stoją w miejscu: zegar zwalnia, a wraca do pełnego tempa przy
        // wznowieniu i przy przeciąganiu suwaka.
        _refresh.Interval = playing || _scrubbing ? RefreshInterval : IdleRefreshInterval;

        // Stan zasilania zmienia się rzadko, a odczyt jest wywołaniem systemowym; pięć sekund
        // wystarcza, żeby podłączenie zasilacza zauważyć, a nie kosztuje praktycznie nic.
        if (DateTime.UtcNow - _lastPowerCheck < PowerCheckInterval) return;

        _lastPowerCheck = DateTime.UtcNow;
        if (_settings.Current.Effects == EffectsMode.Auto) UpdateEffects();
    }

    private static readonly TimeSpan IdleRefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PowerCheckInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastPowerCheck = DateTime.UtcNow;

    /// <summary>Zegar odświeżania wraca do pełnego tempa od razu, bez czekania na tyknięcie bezczynne.</summary>
    private void WakeRefresh()
    {
        if (_refresh.Interval == RefreshInterval) return;

        _refresh.Interval = RefreshInterval;
        _refresh.Stop();
        _refresh.Start();
    }

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

    /// <summary>
    /// Silnik zgłasza zmianę także po przewinięciu i po każdej zmianie kolejki, nie tylko przy
    /// nowym utworze. Znacznik grającego wiersza przenosi się zawsze; tytuł, okładka i panel
    /// systemowy zmieniają się tylko wtedy, gdy zmienił się utwór albo doszły jego znaczniki.
    /// </summary>
    private void OnTrackChanged(int index)
    {
        // Numer ze zdarzenia powstał na innym wątku i mógł się zestarzeć po edycji kolejki.
        var live = _engine.CurrentIndex;
        if (live >= 0) index = live;

        if (index < 0 || index >= Queue.Count) return;
        if (_engine.Queue.ElementAtOrDefault(index) is not { } entry) return;

        var row = Queue[index];
        if (!ReferenceEquals(row, _current))
        {
            if (_current is not null) _current.IsCurrent = false;
            _current = row;
            row.IsCurrent = true;
        }

        row.ClearTemporaryFailure();

        var metadata = entry.Metadata;
        var newTrack = !ReferenceEquals(entry, _shownEntry);

        if (newTrack || (metadata is not null && !ReferenceEquals(metadata, _currentMetadata)))
        {
            _shownEntry = entry;
            _currentMetadata = metadata;
            _shownBitrate = 0;

            Title = metadata?.Title ?? entry.Title;
            ArtistLine = BuildAlbumLine(entry);
            FormatBadge = metadata?.BuildBadge(null) ?? string.Empty;

            // Duration read from the tags is usually more accurate than the decoder's estimate.
            if (metadata?.Duration > TimeSpan.Zero)
            {
                row.Duration = FormatTime(metadata.Duration.TotalSeconds);
                row.DurationSeconds = metadata.Duration.TotalSeconds;
            }

            PublishToMediaPanel();
        }

        if (newTrack)
        {
            LoadCover(entry.Path);
            TouchQueue();
        }

        Raise(nameof(CurrentIndex));
    }

    /// <summary>Wpis, którego tytuł i okładka są na ekranie.</summary>
    private QueueEntry? _shownEntry;

    public int CurrentIndex => _current is null ? -1 : Queue.IndexOf(_current);

    /// <summary>
    /// Silnik pominął plik. Wiersz zostaje wyszarzony z przyczyną w podpowiedzi, a komunikat
    /// mówi, co pominięto i dlaczego; seria porażek (folder plików bez kodeka) daje jedno
    /// podsumowanie zamiast komunikatu za każdy plik. Szczegół techniczny zostaje w dzienniku.
    /// </summary>
    private void OnTrackFailed(int index, TrackFailure failure)
    {
        if (index < 0 || index >= Queue.Count) return;

        var entry = _engine.Queue.ElementAtOrDefault(index);
        var row = Queue[index];
        var reason = FailureText.ForTrack(failure.Reason, entry?.Format ?? AudioFileFormat.Unknown);

        row.ShowFailure(failure.Reason, reason, reason);

        var count = _skippedInNotice + 1;
        ShowNotice(count == 1
            ? string.Format(Strings.Current["TrackSkipped"], Path.GetFileName(row.Path), reason)
            : string.Format(Strings.Current["TracksSkipped"], count));
        _skippedInNotice = count;
    }

    /// <summary>Ile pominiętych plików obejmuje komunikat, który jest teraz na ekranie.</summary>
    private int _skippedInNotice;

    // ---------- Okładka ----------

    /// <summary>
    /// Okładka bieżącego utworu: odczyt z pliku, zdekodowanie w zmniejszonej rozdzielczości
    /// i próbka do palety, wszystko poza wątkiem interfejsu. Spóźniony wynik jest zwalniany.
    /// </summary>
    private readonly CoverLoader<DecodedCover> _covers =
        new(path => DecodeCover(MetadataReader.ReadCover(path)), cover => cover.Display.Dispose());

    /// <summary>Ostatni zamówiony odczyt okładki; testy czekają na jego koniec.</summary>
    internal Task CoverTask { get; private set; } = Task.CompletedTask;

    private void LoadCover(string path) => CoverTask = _covers.LoadAsync(path, ShowCover);

    /// <summary>
    /// Szerokość, do której dekodowana jest okładka. Płyta ma kilkaset pikseli średnicy także
    /// przy skalowaniu ekranu 2×; okładki osadzone w plikach miewają 3000 px i więcej.
    /// </summary>
    private const int CoverDecodeWidth = 640;

    /// <summary>Obraz do pokazania i próbka pikseli do palety; null, gdy okładki nie ma albo jest uszkodzona.</summary>
    internal static DecodedCover? DecodeCover(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            var display = Bitmap.DecodeToWidth(stream, CoverDecodeWidth, BitmapInterpolationMode.HighQuality);

            byte[]? pixels = null;
            try
            {
                pixels = CoverPalette.Sample(bytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cewka] próbka okładki: {ex.Message}");
            }

            return new DecodedCover(display, pixels);
        }
        catch (Exception ex)
        {
            // Uszkodzona okładka nie może zatrzymać odtwarzania.
            Console.Error.WriteLine($"[cewka] okładka nieczytelna: {ex.Message}");
            return null;
        }
    }

    /// <summary>Próbka pikseli bieżącej okładki; palety przy zmianie motywu i intensywności liczą się z niej.</summary>
    private byte[]? _coverPixels;

    private void ShowCover(DecodedCover? cover)
    {
        if (cover is not null)
        {
            _usingPlaceholderCover = false;
            _coverPixels = cover.Pixels;
            ReplaceCover(cover.Display);
        }
        else
        {
            _usingPlaceholderCover = true;
            _coverPixels = null;
            ReplaceCover(LoadPlaceholder());
        }
    }

    /// <summary>Podmienia obraz na płycie, przelicza barwy i zwalnia poprzedni obraz.</summary>
    private void ReplaceCover(Bitmap? next)
    {
        var previous = _cover;

        Cover = next;
        Palette = ExtractPalette();

        if (!ReferenceEquals(previous, next)) previous?.Dispose();
    }

    /// <summary>
    /// Swaps the placeholder when the palette changes. Only relevant while the placeholder
    /// is on screen: a real cover belongs to the record, not to the interface, and must not
    /// change with the theme.
    /// </summary>
    public void OnThemeChanged()
    {
        if (_usingPlaceholderCover) ReplaceCover(LoadPlaceholder());
        else Palette = ExtractPalette();
    }

    /// <summary>
    /// Pulls the background colours out of whatever is currently on the record, at the strength
    /// the user chose.
    /// </summary>
    private Color[] ExtractPalette()
    {
        var dark = App.Theme.IsDark;
        var saturation = ColourPreferences.Saturation(_settings.Current.ColourIntensity);

        // Akcent liczony jest z palety pobranej w pełnej sile, a nie z tej, którą przyciemniło
        // ustawienie intensywności. Intensywność dotyczy tła; przełączniki i suwaki mają
        // pokazywać barwę okładki zawsze i w całości.
        if (_coverPixels is { } pixels)
        {
            ApplyAccent(CoverPalette.FromPixels(pixels, dark, 1.0), dark);
            return CoverPalette.FromPixels(pixels, dark, saturation);
        }

        ApplyAccent(CoverPalette.Extract(Cover, dark, 1.0), dark);

        // Przy okładce domyślnej barwy pary są znane dokładnie, więc nie ma po co odczytywać ich
        // z narysowanego obrazka.
        if (_usingPlaceholderCover) return PlaceholderBackdrop(dark);

        return CoverPalette.Extract(Cover, dark, saturation);
    }

    /// <summary>
    /// Plamy tła dla okładki domyślnej, wzięte wprost ze skrajnych barw pary.
    ///
    /// <para><b>Dlaczego nie przez analizę obrazka.</b> Dla prawdziwej okładki nie ma innego
    /// wyjścia: barwy trzeba z niej odczytać. Ale okładkę domyślną program rysuje sam i zna jej
    /// parę co do składowej — a droga przez analizę te barwy psuje. <see cref="CoverPalette"/>
    /// dzieli przestrzeń barw na kubełki i uśrednia zawartość każdego z nich, co samo w sobie
    /// ciągnie wynik do szarości; do tego spirala to cienkie kreski na ciemnym podłożu, więc
    /// w kubełkach przeważa tło, a nie barwa kreski. Skutek: para „turkus i róż" dawała na tle
    /// dwa odcienie tego samego zamglonego fioletu.</para>
    ///
    /// <para><b>Skrajne, nie środkowa.</b> Spirala przechodzi od pierwszej barwy pary do drugiej,
    /// więc to te dwie mówią, czym para jest. Barwa środkowa jest ich mieszaniną i na tle nie
    /// wnosi nic poza rozmyciem różnicy. Cztery plamy dostają więc obie skrajne barwy po dwa
    /// razy, dzięki czemu tło jest dwubarwne tak samo jak okładka.</para>
    ///
    /// <para>Ustawienie intensywności barw działa tu dalej — skaluje nasycenie, tak jak robi to
    /// dla barw wyciąganych z prawdziwej okładki. Przełączniki i suwaki barwy nie stąd biorą:
    /// akcent liczony jest osobno, z pełnej palety i bez tego skalowania.</para>
    /// </summary>
    private Color[] PlaceholderBackdrop(bool darkTheme)
    {
        // Ta sama droga, którą poszło rysowanie, więc przy ustawieniu losowym tło dostaje barwy
        // tej pary, która naprawdę jest na ekranie.
        var drawn = _placeholderDraw.For(_settings.Current.PlaceholderPalette, _current?.Path);
        var ramp = CoilCover.RampColours(drawn, darkTheme);

        if (ramp.Length == 0) return [];

        var saturation = ColourPreferences.Saturation(_settings.Current.ColourIntensity);
        var first = Contrast.LimitForBackdrop(Saturate(ramp[0], saturation), darkTheme);
        var last = Contrast.LimitForBackdrop(Saturate(ramp[^1], saturation), darkTheme);

        return [first, last, first, last];
    }

    /// <summary>Skaluje nasycenie, zostawiając odcień i jasność w spokoju.</summary>
    private static Color Saturate(Color colour, double multiplier)
    {
        if (Math.Abs(multiplier - 1.0) < 1e-6) return colour;

        var hsl = colour.ToHsl();
        return new HslColor(hsl.A, hsl.H, Math.Clamp(hsl.S * multiplier, 0, 1), hsl.L).ToRgb();
    }

    /// <summary>
    /// Wstawia barwę akcentu do zasobów aplikacji. Wszystko, co sięga po nią przez
    /// <c>DynamicResource</c> — przełączniki, suwaki, wypełnienia — przemalowuje się samo.
    /// </summary>
    private static void ApplyAccent(IReadOnlyList<Color> vivid, bool darkTheme)
    {
        var resources = Avalonia.Application.Current?.Resources;
        if (resources is null) return;

        var accent = AccentColour.From(vivid, darkTheme);

        // Podmiana zasobu rozgłasza zmianę po drzewie obu okien; przy tej samej barwie szkoda zachodu.
        if (resources.TryGetValue("AccentColor", out var shown) && shown is Color same && same == accent) return;

        resources["AccentColor"] = accent;
        resources["AccentBrush"] = new SolidColorBrush(accent);
    }

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

        if (_usingPlaceholderCover) ReplaceCover(LoadPlaceholder());
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

    /// <summary>
    /// m:ss, a od godziny h:mm:ss. Przy utworze dłuższym niż godzina także pozycja poniżej
    /// godziny dostaje postać z godzinami, żeby szerokość pola nie skakała.
    /// </summary>
    internal static string FormatTime(double seconds, bool hours = false)
    {
        var whole = (int)Math.Max(0, Math.Floor(seconds));

        return hours || whole >= 3600
            ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}"
            : $"{whole / 60}:{whole % 60:00}";
    }

    private readonly EventHandler _themeWatcher;
    private readonly EventHandler _themeModeWatcher;

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        App.Theme.Changed -= _themeWatcher;
        App.Theme.ModeChanged -= _themeModeWatcher;
        _queueSave.Stop();
        _refresh.Stop();
        _noticeTimer.Stop();
        _covers.Cancel();
        _engine.Dispose();
        _loudness.Dispose();
        _cover?.Dispose();
    }
}
