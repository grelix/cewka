using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.Audio;
using Cewka.Audio.Decoding;
using Cewka.Audio.Devices;
using Cewka.Audio.Dsp;
using Cewka.Audio.Metadata;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;
using Cewka.Platform;

namespace Cewka.App.ViewModels;

/// <summary>
/// Stan okna odtwarzacza, napędzany przez silnik dźwięku. Pozycję zgłasza wątek dźwiękowy,
/// więc interfejs odpytuje ją zegarem co 60 ms.
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
    private bool _queuePinned;
    private bool _isPlaying;
    private bool _isSeeking;
    private double _progress;
    private string _elapsed = "0:00";
    private string _total = "0:00";
    private string _title = string.Empty;
    private string _artistLine = string.Empty;
    private string _formatBadge = string.Empty;

    public MainViewModel(SettingsStore settings)
        : this(settings, new PlaybackEngine(), new LoudnessService(), AppPaths.QueueFile, radioLogos: RadioLogoService.CreateDefault())
    {
    }

    /// <summary>Dla testów: silnik, pomiar głośności, plik kolejki i sprawdzanie ścieżek podane z zewnątrz.</summary>
    internal MainViewModel(
        SettingsStore settings, PlaybackEngine engine, LoudnessService loudness, string queueFile, PathProbe? paths = null,
        string? radioFile = null, Func<RadioCatalog>? radioCatalog = null, RadioLogoService? radioLogos = null)
    {
        _settings = settings;
        _paths = paths ?? PathProbe.Disk;
        _queueFile = queueFile;
        _queueWriter = new QueueStateWriter(queueFile);
        var s = settings.Current;

        _queuePinned = s.QueuePinned;

        // Jakość konwersji i rozmiar bufora czytane są przy tworzeniu dekodera i urządzenia,
        // więc muszą stać na miejscu, zanim cokolwiek powstanie.
        AudioQuality.ResamplerFilterOrder = AudioPreferences.FilterOrder(s.ResamplerQuality);

        _loudness = loudness;
        _loudness.Measured += OnLoudnessMeasured;
        _loudness.TargetLufs = AudioPreferences.Lufs(s.LoudnessTarget);
        _loudness.AlwaysAnalyse = s.AlwaysAnalyse;

        _engine = engine;
        _engine.LiveUrlGuard ??= RadioBlocklist.UrlGuard;
        _engine.PreferredPeriodSize = AudioPreferences.PeriodFrames(s.OutputLatency);
        _engine.Graph.GainSource = _loudness;
        _engine.Graph.NormalisationEnabled = s.NormalisationEnabled;
        _engine.SoftPause = SoftPauseLength(s.SoftPause);
        _engine.Graph.Limiter.Enabled = s.LimiterEnabled;
        _engine.Graph.Equaliser.Enabled = s.EqualiserEnabled;
        _engine.Graph.Equaliser.Preamp = s.Preamp;
        _volume = s.Volume;
        _isMuted = s.IsMuted;
        _engine.Graph.Volume.Target = _isMuted ? 0f : (float)VolumeCurve.ToGain(_volume);

        for (var band = 0; band < BandLabels.Length; band++)
            _engine.Graph.Equaliser.SetGain(band, s.EqualiserGains[band]);

        Preamp = new EqBandViewModel("Preamp", s.Preamp);
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
        Queue.CollectionChanged += (_, _) => MarkQueueRowStale();

        ApplyOutputDevice();
        UpdateEffects();

        _engine.TrackChanged += index => Dispatcher.UIThread.Post(() => OnTrackChanged(index));
        _engine.TrackFailed += (index, failure) => Dispatcher.UIThread.Post(() => OnTrackFailed(index, failure));
        _engine.QueueFinished += () => Dispatcher.UIThread.Post(() =>
        {
            QueueEnded = true;
            IsPlaying = false;
        });
        _engine.DeviceLost += () => Dispatcher.UIThread.Post(OnDeviceLost);

        Radio = new RadioViewModel(
            _engine, settings, radioFile ?? AppPaths.RadioFile, TryPlayback, ShowNotice, radioCatalog, logos: radioLogos);
        Radio.StageLogoChanged += () =>
        {
            SampleRadioLogo();
            if (_radioMode)
            {
                PublishToMediaPanel();
                RefreshPalettes();
            }
        };
        Radio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RadioViewModel.Heading) && _radioMode) PublishToMediaPanel();
            if (e.PropertyName is nameof(RadioViewModel.Heading) or nameof(RadioViewModel.Subline))
            {
                Raise(nameof(ShownTitle));
                Raise(nameof(ShownLine));
                Raise(nameof(HasShownLine));
                Raise(nameof(SceneLine));
                Raise(nameof(HasSceneLine));
            }

            if (e.PropertyName is nameof(RadioViewModel.Heading) or nameof(RadioViewModel.Subline)
                or nameof(RadioViewModel.StateText) or nameof(RadioViewModel.ErrorText) or nameof(RadioViewModel.HasError)
                or nameof(RadioViewModel.HasStation))
            {
                Raise(nameof(SceneCaptionIsLive));
                Raise(nameof(SceneLiveCaption));
            }

            if (e.PropertyName == nameof(RadioViewModel.CurrentStation) && _radioMode)
                MarkQueueRowStale();
            else if (e.PropertyName == nameof(RadioViewModel.Badge) && _radioMode)
            {
                FormatBadge = Radio.Badge;
            }
        };
        Radio.StepCompleted += SyncPlaying;
        Radio.Failed += () => IsPlaying = false;
        _radioMode = s.Source == SourceMode.Radio && s.RadioEnabled;
        if (_radioMode) Radio.Activate();

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

    /// <summary>
    /// Obraz na płycie sceny. Scena jest zawsze ciemna, więc przy okładce domyślnej w jasnym motywie
    /// to jej ciemny wariant, ten sam, z którego powstaje rozmyte tło sceny, a przy zamkniętej scenie
    /// nic; poza tym ta sama bitmapa co <see cref="Cover"/>.
    /// </summary>
    public Bitmap? SceneCover { get => _sceneCover; private set => Set(ref _sceneCover, value); }

    private Bitmap? _sceneCover;

    /// <summary>
    /// Obraz wizualizacji Okładka: duży dekod, gdy jest, a do czasu jego ukończenia i przy braku
    /// okładki ten sam obraz co <see cref="SceneCover"/>. Zmienia się razem z okładką jednym powiadomieniem.
    /// </summary>
    public Bitmap? SceneCoverArt => _sceneLarge ?? _sceneCover;

    /// <summary>Największa wysokość dużego dekodu okładki w pikselach.</summary>
    internal const int MaxSceneCoverSize = 1440;

    private int _sceneCoverSize = 1080;

    /// <summary>
    /// Wysokość dużego dekodu okładki: wysokość ekranu, najwyżej <see cref="MaxSceneCoverSize"/>.
    /// Widok podaje ją przy wejściu do sceny.
    /// </summary>
    internal int SceneCoverSize
    {
        get => _sceneCoverSize;
        set => _sceneCoverSize = Math.Clamp(value, 1, MaxSceneCoverSize);
    }

    /// <summary>Odczyt i dekod okładki pliku do podanej wysokości; testy podają własny.</summary>
    internal Func<string, int, Bitmap?> ReadLargeCover { get; set; } = DecodeLargeCover;

    private Bitmap? _sceneLarge;
    private string? _largePath;
    private CoverLoader<Bitmap>? _largeCovers;

    /// <summary>Ostatnie zamówienie dużego dekodu; testy czekają na jego koniec.</summary>
    internal Task LargeCoverTask { get; private set; } = Task.CompletedTask;

    /// <summary>Duża okładka jest w pamięci.</summary>
    internal bool HasLargeCover => _sceneLarge is not null;

    private static Bitmap? DecodeLargeCover(string path, int height)
    {
        try
        {
            if (MetadataReader.ReadCover(path) is not { } bytes) return null;

            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToHeight(stream, height, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] duża okładka: {ex.Message}");
            return null;
        }
    }

    /// <summary>Duży dekod ma sens tylko przy otwartej scenie, wizualizacji Okładka i prawdziwej okładce.</summary>
    private bool WantsLargeCover =>
        _isSceneOpen && !_disposed && SceneVisual == SceneVisual.Cover && !_usingPlaceholderCover && _shownEntry is not null;

    /// <summary>
    /// Zamawia duży dekod okładki bieżącego utworu albo zwalnia go, gdy nie jest już potrzebny.
    /// Dekod trwa w tle; do jego ukończenia scena pokazuje zwykłą okładkę sceny.
    /// </summary>
    private void UpdateLargeCover()
    {
        if (!WantsLargeCover)
        {
            _largeCovers?.Cancel();
            _largePath = null;
            DropLargeCover(delayed: false);
            return;
        }

        var path = _shownEntry!.Path;
        if (_largePath == path) return;

        _largePath = path;
        _largeCovers ??= new CoverLoader<Bitmap>(p => ReadLargeCover(p, _sceneCoverSize), bitmap => bitmap.Dispose());
        LargeCoverTask = _largeCovers.LoadAsync(path, bitmap =>
        {
            if (bitmap is null) return;

            if (!WantsLargeCover || _largePath != path)
            {
                bitmap.Dispose();
                return;
            }

            _sceneLarge = bitmap;
            Raise(nameof(SceneCoverArt));
        });
    }

    /// <summary>
    /// Odpina dużą okładkę i zwalnia ją. Przy zmianie utworu zwolnienie jest odroczone, bo poprzedni
    /// obraz jeszcze znika z ekranu w przenikaniu; przy wyjściu ze sceny natychmiast.
    /// </summary>
    private void DropLargeCover(bool delayed, bool raise = true)
    {
        var old = _sceneLarge;
        if (old is null) return;

        _sceneLarge = null;
        if (raise) Raise(nameof(SceneCoverArt));

        if (delayed) DispatcherTimer.RunOnce(old.Dispose, TimeSpan.FromSeconds(2));
        else old.Dispose();
    }

    /// <summary>
    /// Scena jest otwarta. Ciemny wariant okładki domyślnej, potrzebny scenie w jasnym oknie,
    /// rysuje się dopiero przy jej otwarciu i zwalnia przy zamknięciu razem z tłem z niego.
    /// </summary>
    public bool IsSceneOpen
    {
        get => _isSceneOpen;
        set
        {
            if (_isSceneOpen == value) return;

            _isSceneOpen = value;
            if (value) RefreshBattery();
            UpdateNextUp();

            // W ciemnym oknie i przy prawdziwej okładce scena ma obraz okna: nie ma czego rysować ani zwalniać.
            if (_usingPlaceholderCover && !(App.Theme.IsDark && _cover is not null)) ShowScenePlaceholder();

            RefreshSceneEnvelope();
            UpdateLargeCover();
        }
    }

    private bool _isSceneOpen;

    /// <summary>
    /// Rozmyta okładka pod plamami sceny, mała, bo i tak jest rozmyta; zmienia się razem
    /// z <see cref="Cover"/>.
    /// </summary>
    public Bitmap? SceneBackdrop { get => _sceneBackdrop; private set => Set(ref _sceneBackdrop, value); }

    private Bitmap? _sceneBackdrop;

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

    /// <summary>
    /// Barwy plam nad rozmytą okładką sceny: jasne odcienie dominujących barw okładki, przy
    /// czarno-białej szarości. Nie zależą od motywu ani od intensywności barw.
    /// </summary>
    public IReadOnlyList<Color> SceneBlobs
    {
        get => _sceneBlobs;
        private set
        {
            if (_sceneBlobs.SequenceEqual(value)) return;
            Set(ref _sceneBlobs, value);
        }
    }

    private IReadOnlyList<Color> _sceneBlobs = [];

    /// <summary>Ścieżka pliku bieżącego utworu (wpisu, którego tytuł i okładka są na ekranie); pusta, gdy nic nie gra.</summary>
    public string CurrentFilePath => _shownEntry?.Path ?? string.Empty;

    public string Title
    {
        get => _title;
        private set
        {
            if (!Set(ref _title, value)) return;
            Raise(nameof(ShownTitle));
        }
    }

    public string ArtistLine
    {
        get => _artistLine;
        private set
        {
            if (!Set(ref _artistLine, value)) return;
            Raise(nameof(HasArtistLine));
            Raise(nameof(ShownLine));
            Raise(nameof(HasShownLine));
        }
    }

    /// <summary>Duży wiersz „Teraz gra”: utwór z pliku albo, w radiu, tytuł z metadanych lub nazwa stacji.</summary>
    public string ShownTitle => _radioMode ? Radio.Heading : _title;

    /// <summary>Drugi wiersz: album i wykonawca z pliku albo, w radiu, stan połączenia i stacja.</summary>
    public string ShownLine => _radioMode ? Radio.Subline : _artistLine;

    public bool HasShownLine => !string.IsNullOrWhiteSpace(ShownLine);

    /// <summary>
    /// Fałsz, gdy plik nie ma wykonawcy, albumu ani roku do pokazania; wiersz znika wtedy
    /// zamiast wypełniacza.
    /// </summary>
    public bool HasArtistLine => !string.IsNullOrWhiteSpace(_artistLine);

    /// <summary>
    /// Wykonawca bieżącego utworu albo, gdy go brak, wykonawca albumu; pusty, gdy tagi nie
    /// podają żadnego. Scena pokazuje go w osobnym wierszu.
    /// </summary>
    public string Artist
    {
        get => _artist;
        private set
        {
            if (!Set(ref _artist, value)) return;
            Raise(nameof(HasArtist));
            Raise(nameof(SceneLine));
            Raise(nameof(HasSceneLine));
        }
    }

    public bool HasArtist => !string.IsNullOrWhiteSpace(_artist);

    /// <summary>Drugi wiersz Sceny: wykonawca z pliku albo, w radiu, wykonawca i stacja przy tytule z metadanych.</summary>
    public string SceneLine => _radioMode ? (Radio.HasTrackTitle ? Radio.Subline : string.Empty) : _artist;

    public bool HasSceneLine => !string.IsNullOrWhiteSpace(SceneLine);

    /// <summary>Album w Scenie tylko dla plików; stacja nie ma albumu.</summary>
    public bool HasSceneAlbum => HasAlbum && !_radioMode;

    /// <summary>
    /// Napis pod kreską Sceny w radiu: błąd, stan łączenia albo „Na żywo”; pusty dla plików i bez stacji.
    /// </summary>
    public string SceneLiveCaption
    {
        get
        {
            if (!_radioMode || !Radio.HasStation) return string.Empty;
            if (Radio.HasError) return Radio.ErrorText;
            return Radio.StateText.Length > 0 ? Radio.StateText : Strings.Current["RadioLive"];
        }
    }

    /// <summary>Napis pod kreską Sceny to „Na żywo”, a nie błąd ani stan łączenia; tylko wtedy stoi przed nim kropka.</summary>
    public bool SceneCaptionIsLive => _radioMode && Radio.HasStation && !Radio.HasError && Radio.StateText.Length == 0;

    /// <summary>Album bieżącego utworu; pusty, gdy tagi go nie podają.</summary>
    public string Album
    {
        get => _album;
        private set
        {
            if (!Set(ref _album, value)) return;
            Raise(nameof(HasAlbum));
            Raise(nameof(HasSceneAlbum));
        }
    }

    public bool HasAlbum => !string.IsNullOrWhiteSpace(_album);

    private string _artist = string.Empty;
    private string _album = string.Empty;

    public string FormatBadge
    {
        get => _formatBadge;
        private set
        {
            if (!Set(ref _formatBadge, value)) return;
            Raise(nameof(HasFormatBadge));
            Raise(nameof(ShowsTrackQuality));
        }
    }

    /// <summary>
    /// Etykieta jakości stoi w wierszu wykonawcy tylko przy plikach; w radiu ten wiersz opisuje stację,
    /// a etykieta stoi za napisem „Na żywo”.
    /// </summary>
    public bool ShowsTrackQuality => HasFormatBadge && !_radioMode;

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
        Raise(nameof(ShowsTrackQuality));
    }

    // ---------- Odtwarzanie ----------

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            if (value)
            {
                WakeRefresh();
                QueueEnded = false;
            }

            Raise(nameof(IsPaused));
            Raise(nameof(ShowsPauseIcon));
            Raise(nameof(ShowsStopIcon));
            Raise(nameof(PlayPauseTooltip));
            _mediaPanel?.SetStatus(value ? MediaPanelStatus.Playing : MediaPanelStatus.Paused);
            MediaKeys.Playing = value;
        }
    }

    public bool IsPaused => !_isPlaying;

    /// <summary>Podpowiedź i nazwa przycisku odtwarzania: to, co zrobi jego naciśnięcie.</summary>
    public string PlayPauseTooltip =>
        Strings.Current[_isPlaying ? (_radioMode ? "TooltipStopLive" : "TooltipPause") : "TooltipPlay"];

    /// <summary>W radiu pauza to zatrzymanie (rozłączenie), więc przycisk pokazuje kwadrat, nie dwie kreski.</summary>
    public bool ShowsPauseIcon => _isPlaying && !_radioMode;

    public bool ShowsStopIcon => _isPlaying && _radioMode;

    /// <summary>Amplitude driving the waveform, taken from the live signal analysis.</summary>
    public float WaveLevel => _engine.Graph.Analyser.Level;

    public double Progress
    {
        get => _progress;
        set
        {
            if (!Set(ref _progress, value)) return;

            // Ustawienie z kodu przy odświeżaniu nie może przewijać odtwarzania.
            if (_isSeeking) return;

            // W trakcie przeciągania suwak tylko się przesuwa; przewinięcie następuje po
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
    /// Wołane w trakcie przeciągania paska postępu. Wskaźnik idzie za kursorem, a przewinięcie
    /// czeka na puszczenie przycisku, bo każde przewinięcie wycisza i opróżnia bufor.
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
        Raise(nameof(MuteTooltip));
        Raise(nameof(VolumeOpacity));
    }

    /// <summary>Suwak wyciszonej głośności przygasa, ale zostaje w swoim położeniu.</summary>
    public double VolumeOpacity => _isMuted ? 0.45 : 1.0;

    /// <summary>Podpowiedź i nazwa przycisku głośnika.</summary>
    public string MuteTooltip => Strings.Current[_isMuted ? "TooltipUnmute" : "TooltipMute"];

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
        set { _engine.Shuffle = value; Raise(); TouchQueue(); UpdateNextUp(); MarkQueueRowStale(); }
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
        UpdateNextUp();
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
    /// Okno „Korektor i efekty” jest otwarte. Okno główne otwiera je i zamyka według tej wartości,
    /// a zamknięcie okna ją zeruje. Po uruchomieniu okno jest zamknięte.
    /// </summary>
    public bool PanelOpen
    {
        get => _panelOpen;
        set
        {
            if (!Set(ref _panelOpen, value)) return;
            Raise(nameof(ShowProcessingDot));
        }
    }

    /// <summary>
    /// Panel kolejki (w radiu: stacji) przypięty: kolumna po prawej stronie okna albo, w wąskim oknie,
    /// nakładka. Nieprzypięty panel wysuwa się z uchwytu przy krawędzi i chowa po zjechaniu kursorem.
    /// </summary>
    public bool QueuePinned
    {
        get => _queuePinned;
        set => SetQueuePinned(value, remember: true);
    }

    /// <summary>
    /// Przypina albo odpina panel. Bez <paramref name="remember"/> ustawienia zachowują zamiar
    /// użytkownika: panel odpięty przez zbyt wąskie okno wraca przy następnym starcie.
    /// </summary>
    public void SetQueuePinned(bool value, bool remember)
    {
        if (!Set(ref _queuePinned, value, nameof(QueuePinned))) return;
        if (!remember) return;

        _settings.Current.QueuePinned = value;
        _settings.Touch();
    }

    /// <summary>Wysuwanie nieprzypiętego panelu: po najechaniu na uchwyt albo po jego kliknięciu.</summary>
    public QueueReveal QueueReveal
    {
        get => _settings.Current.QueueReveal;
        set => SetQueuePanelOption(QueueReveal, value, s => s.QueueReveal = value);
    }

    /// <summary>Czas chowania wysuniętego panelu po zjechaniu z niego kursorem.</summary>
    public QueueHideDelay QueueHideDelay
    {
        get => _settings.Current.QueueHideDelay;
        set => SetQueuePanelOption(QueueHideDelay, value, s => s.QueueHideDelay = value);
    }

    /// <summary>Zwłoka między najechaniem na uchwyt a wysunięciem panelu.</summary>
    public QueueRevealDelay QueueRevealDelay
    {
        get => _settings.Current.QueueRevealDelay;
        set => SetQueuePanelOption(QueueRevealDelay, value, s => s.QueueRevealDelay = value);
    }

    /// <summary>Szerokość panelu wybrana przeciąganiem krawędzi; <c>null</c> to domyślna dla okna.</summary>
    public double? QueueWidth
    {
        get => _settings.Current.QueueWidth;
        set
        {
            var width = value is { } chosen ? Math.Max(chosen, QueuePanelWidth.Minimum) : (double?)null;
            if (Nullable.Equals(_settings.Current.QueueWidth, width)) return;

            _settings.Current.QueueWidth = width;
            _settings.Touch();
            Raise();
        }
    }

    private void SetQueuePanelOption<T>(T current, T value, Action<AppSettings> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;

        apply(_settings.Current);
        _settings.Touch();
        Raise(name);
    }

    /// <summary>
    /// Dźwięk zmienia korektor z niepłaską krzywą albo włączony efekt. Limiter i normalizacja są
    /// włączone domyślnie i pilnują tylko poziomu, więc z nimi kropka świeciłaby zawsze.
    /// </summary>
    public bool ProcessingActive =>
        (EqualiserEnabled && CanResetEqualiser) || SoundEffects.Any(effect => effect.Enabled);

    /// <summary>Kropka na przycisku korektora: przetwarzanie działa, a okna korektora nie widać.</summary>
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
    /// Odczytuje od nowa teksty złożone w kodzie (tytuł, wiersze kolejki); wiązania
    /// <c>{l:Translate}</c> odświeżają się same.
    /// </summary>
    private void RefreshTextForLanguage()
    {
        RebuildQueue();
        Radio.RefreshLanguage();
        Raise(nameof(ShownTitle));
        Raise(nameof(ShownLine));
        Raise(nameof(SourceFilesTooltip));
        Raise(nameof(SourceRadioTooltip));
        Raise(nameof(QueueHandleName));
        if (_current is null) Title = IdleTitle;
        RaiseRepeat();
        Raise(nameof(AudioFailure));
        Raise(nameof(UpdateNotice));
        Raise(nameof(LatencyDescription));
        Raise(nameof(VolumeLevelText));
        Raise(nameof(ThemeTooltip));
        Raise(nameof(PlayPauseTooltip));
        Raise(nameof(MuteTooltip));
        Raise(nameof(UndoQueueLabel));
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
    /// Przenosi stan jednego efektu do toru. Tor przyjmuje siłę w procentach, interfejs trzyma
    /// ją w zakresie 0–1.
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

        // Radio: stacja zamiast utworu, bez osi czasu; logo z pamięci podręcznej trafia do panelu (MPRIS).
        if (_radioMode)
        {
            if (Radio.PanelInfo is not { } info)
            {
                _mediaPanel.Clear();
                _mediaPanel.SetStatus(MediaPanelStatus.Closed);
                return;
            }

            _mediaPanel.SetLive(true, Radio.StageLogoPath);
            _mediaPanel.SetNavigation(Radio.CanStep);
            _mediaPanel.SetTrack(info.Title, info.Artist, info.Album, TimeSpan.Zero);
            _mediaPanel.SetStatus(_isPlaying ? MediaPanelStatus.Playing : MediaPanelStatus.Paused);
            return;
        }

        _mediaPanel.SetLive(false, null);
        _mediaPanel.SetNavigation(true);

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
    /// Czy animacje działają w tańszej postaci. W trybie automatycznym zależy też od zasilania
    /// z baterii, dlatego okno wiąże to, a nie samo ustawienie.
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
        Raise(nameof(ActiveSceneVisual));
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
    /// Kieruje silnik na wybrane urządzenie. Nazwa, która już niczego nie wskazuje, przechodzi
    /// na urządzenie domyślne.
    ///
    /// <para>Urządzenie z listy może mimo to odmówić otwarcia (rozłączone słuchawki, wyłączność
    /// innego programu). Grający utwór przechodzi wtedy na domyślne, a komunikat zostaje, dopóki
    /// tak gra. Wstrzymany zostaje przy wybranym, żeby następne odtworzenie spróbowało znowu.</para>
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
    /// <summary>Długość wyciszenia i podgłośnienia miękkiej pauzy.</summary>
    public static readonly TimeSpan SoftPauseDuration = TimeSpan.FromMilliseconds(400);

    private static TimeSpan SoftPauseLength(bool on) => on ? SoftPauseDuration : TimeSpan.Zero;

    /// <summary>Pauza plików wycisza płynnie, a wznowienie podgłaśnia.</summary>
    public bool SoftPause
    {
        get => _settings.Current.SoftPause;
        set
        {
            if (_settings.Current.SoftPause == value) return;

            _settings.Current.SoftPause = value;
            _engine.SoftPause = SoftPauseLength(value);
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>Program uruchomiony w trybie radia od razu włącza ostatnią stację.</summary>
    public bool RadioPlayAtStart
    {
        get => _settings.Current.RadioPlayAtStart;
        set
        {
            if (_settings.Current.RadioPlayAtStart == value) return;

            _settings.Current.RadioPlayAtStart = value;
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>
    /// Po starcie w trybie radia, z włączonym <see cref="RadioPlayAtStart"/>, włącza ostatnią stację.
    /// Bez wybranej stacji i przy otwieraniu plików z zewnątrz nic nie robi.
    /// </summary>
    public void PlayRadioAtStart()
    {
        if (!_radioMode || !_settings.Current.RadioPlayAtStart || !Radio.HasStation) return;
        if (Radio.PlaySelected()) SyncPlaying();
    }

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

    // ---------- Pełny ekran ----------

    /// <summary>
    /// Wybrana wizualizacja sceny, zapamiętana między uruchomieniami. Widok rysuje płytę, okładkę
    /// albo scenę nocną zależnie od tej wartości.
    /// </summary>
    public SceneVisual SceneVisual
    {
        get => _settings.Current.SceneVisual;
        set
        {
            if (_settings.Current.SceneVisual == value) return;

            _settings.Current.SceneVisual = value;
            _settings.Touch();
            Raise();
            Raise(nameof(ActiveSceneVisual));
            RefreshSceneEnvelope();
            UpdateLargeCover();
        }
    }

    /// <summary>Wizualizacje rysowane przez widok danych dźwięku: reagujące na dźwięk oraz Horyzont.</summary>
    public static bool IsStageVisual(SceneVisual visual) => IsAudioVisual(visual) || visual == SceneVisual.Horizon;

    /// <summary>Wizualizacje reagujące na dźwięk; w trybie ograniczonego ruchu są niedostępne.</summary>
    public static bool IsAudioVisual(SceneVisual visual) =>
        visual is SceneVisual.Spectrum or SceneVisual.Waveform or SceneVisual.Aurora;

    /// <summary>
    /// Wizualizacja faktycznie rysowana: przy ograniczonym ruchu wybór Widma, Fali albo Zorzy zastępuje
    /// Płyta; to samo dla Zorzy bez GPU i na baterii. Zapamiętany wybór zostaje.
    /// </summary>
    public SceneVisual ActiveSceneVisual => IsVisualUsable(SceneVisual) ? SceneVisual : SceneVisual.Disc;

    /// <summary>Czy wizualizację można teraz narysować: ruch, radio, a dla Zorzy także GPU i zasilanie sieciowe.</summary>
    internal bool IsVisualUsable(SceneVisual visual) =>
        !(_reducedEffects && IsAudioVisual(visual))
        && IsVisualAvailable(visual, _radioMode)
        && (visual != SceneVisual.Aurora || AuroraAllowed);

    /// <summary>Horyzont potrzebuje pliku z końcem; stacja radiowa go nie ma, więc dostaje Płytę.</summary>
    public static bool IsVisualAvailable(SceneVisual visual, bool radio) =>
        visual != SceneVisual.Meters && !(radio && visual == SceneVisual.Horizon);

    /// <summary>
    /// Stan GPU poznany przy pierwszym rysowaniu sceny: true, gdy Skia ma kontekst GPU. Do czasu poznania
    /// (null) Zorza jest niedostępna.
    /// </summary>
    internal bool? SceneGpu
    {
        get => _sceneGpu;
        set
        {
            if (_sceneGpu == value) return;

            _sceneGpu = value;
            Raise(nameof(SceneGpuAvailable));
            Raise(nameof(ActiveSceneVisual));
        }
    }

    private bool? _sceneGpu;
    private bool _sceneGpuPinned;

    /// <summary>Wynik sondy sceny; ignorowany, gdy test ustawił stan jawnie przez <see cref="PinGpu"/>.</summary>
    internal void ReportGpu(bool gpu)
    {
        if (!_sceneGpuPinned) SceneGpu = gpu;
    }

    /// <summary>Ustawia stan GPU na stałe; headless nie ma GPU, więc testy ustawiają go same.</summary>
    internal void PinGpu(bool gpu)
    {
        _sceneGpuPinned = true;
        SceneGpu = gpu;
    }

    /// <summary>GPU wykryty, więc karta Zorzy ma sens w Ustawieniach.</summary>
    public bool SceneGpuAvailable => _sceneGpu == true;

    /// <summary>Zorza potrzebuje GPU i zasilania sieciowego.</summary>
    private bool AuroraAllowed => _sceneGpu == true && !_onBattery;

    private bool _onBattery;

    /// <summary>Odczyt zasilania; testy podstawiają własną odpowiedź.</summary>
    internal Func<bool> BatteryProbe { get; set; } = () => PowerStatus.IsOnBattery;

    private static readonly TimeSpan BatteryCheckInterval = TimeSpan.FromSeconds(60);
    private DateTime _lastBatteryCheck = DateTime.UtcNow;

    /// <summary>Odczytuje zasilanie; zmiana odświeża wybór rysowanej wizualizacji. Przy otwarciu sceny i co 60 s.</summary>
    internal void RefreshBattery()
    {
        _lastBatteryCheck = DateTime.UtcNow;
        var battery = BatteryProbe();
        if (battery == _onBattery) return;

        _onBattery = battery;
        Raise(nameof(ActiveSceneVisual));
    }

    /// <summary>Obwiednia bieżącego pliku dla Horyzontu; null do czasu policzenia i w radiu.</summary>
    public FileEnvelope? SceneEnvelope => _sceneEnvelope;

    private FileEnvelope? _sceneEnvelope;

    /// <summary>
    /// Pobiera obwiednię bieżącego pliku z pamięci podręcznej analiz, a jeśli jej nie ma, zleca
    /// policzenie w tle. Praca tylko przy otwartej scenie z Horyzontem; po innych wyborach obwiednia
    /// jest zwalniana.
    /// </summary>
    internal void RefreshSceneEnvelope()
    {
        FileEnvelope? envelope = null;
        if (_isSceneOpen && !_disposed && !_radioMode && SceneVisual == SceneVisual.Horizon &&
            _shownEntry is { IsLive: false, Path.Length: > 0 } entry)
            envelope = _loudness.RequestEnvelope(entry.Path);

        if (ReferenceEquals(envelope, _sceneEnvelope)) return;

        _sceneEnvelope = envelope;
        Raise(nameof(SceneEnvelope));
    }

    private void OnLoudnessMeasured(string path, double gain)
    {
        // Wątek analizy: odświeżenie idzie przez wątek interfejsu.
        if (!string.Equals(path, CurrentFilePath, StringComparison.OrdinalIgnoreCase)) return;

        Dispatcher.UIThread.Post(RefreshSceneEnvelope);
    }

    /// <summary>Dane dźwięku dla wizualizacji sceny; testy podstawiają atrapę.</summary>
    internal ISceneAudioSource SceneAudioData
    {
        get => _sceneAudio ??= new GraphSceneAudioSource(_engine.Graph.Scene);
        set => _sceneAudio = value;
    }

    private ISceneAudioSource? _sceneAudio;

    /// <summary>Kształt widma na scenie.</summary>
    public SpectrumShape SceneSpectrumShape
    {
        get => _settings.Current.SpectrumShape;
        set
        {
            if (_settings.Current.SpectrumShape == value) return;

            _settings.Current.SpectrumShape = value;
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>Rodzaj fali na scenie.</summary>
    public WaveformKind SceneWaveformKind
    {
        get => _settings.Current.WaveformKind;
        set
        {
            if (_settings.Current.WaveformKind == value) return;

            _settings.Current.WaveformKind = value;
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>Pierścień widma wokół płyty.</summary>
    public bool SceneDiscRing
    {
        get => _settings.Current.DiscRing;
        set
        {
            if (_settings.Current.DiscRing == value) return;

            _settings.Current.DiscRing = value;
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>
    /// Przechodzi o <paramref name="step"/> wizualizacji w przód albo w tył, cyklicznie; przy
    /// ograniczonym ruchu pomija wizualizacje reagujące na dźwięk.
    /// </summary>
    public void CycleSceneVisual(int step)
    {
        var count = Enum.GetValues<SceneVisual>().Length;
        var direction = step < 0 ? -1 : 1;
        var next = (int)SceneVisual;
        for (var i = 0; i < count; i++)
        {
            next = (next + direction + count) % count;
            var candidate = (SceneVisual)next;
            if (IsVisualUsable(candidate) && IsInCycle(candidate)) break;
        }

        SceneVisual = (SceneVisual)next;
    }

    /// <summary>Czy pełny ekran pokazuje kartę następnego utworu.</summary>
    public bool SceneShowNext
    {
        get => _settings.Current.SceneShowNext;
        set
        {
            if (_settings.Current.SceneShowNext == value) return;

            _settings.Current.SceneShowNext = value;
            _settings.Touch();
            Raise();
            UpdateNextUp();
        }
    }

    /// <summary>Ochrona ekranu OLED w scenie.</summary>
    public bool SceneOledCare
    {
        get => _settings.Current.SceneOledCare;
        set
        {
            if (_settings.Current.SceneOledCare == value) return;

            _settings.Current.SceneOledCare = value;
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>Wielkość tekstu w scenie.</summary>
    public SceneTextSize SceneTextSize
    {
        get => _settings.Current.SceneTextSize;
        set
        {
            if (_settings.Current.SceneTextSize == value) return;

            _settings.Current.SceneTextSize = value;
            _settings.Touch();
            Raise();
        }
    }

    // ----- Wygląd sceny: napisy, wielkość płyty i efekty tła -----

    /// <summary>Zapisuje zmienione ustawienie wyglądu sceny i powiadamia widok; bez zmiany nic nie robi.</summary>
    private void SetSceneLook<T>(T current, T value, Action<AppSettings> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;

        apply(_settings.Current);
        _settings.Touch();
        Raise(name);
    }

    public SceneTextPosition SceneTextPosition
    {
        get => _settings.Current.SceneTextPosition;
        set => SetSceneLook(SceneTextPosition, value, s => s.SceneTextPosition = value);
    }

    public bool SceneTextCentred
    {
        get => _settings.Current.SceneTextCentred;
        set => SetSceneLook(SceneTextCentred, value, s => s.SceneTextCentred = value);
    }

    public SceneTextInset SceneTextInset
    {
        get => _settings.Current.SceneTextInset;
        set => SetSceneLook(SceneTextInset, value, s => s.SceneTextInset = value);
    }

    public SceneArtSize SceneArtSize
    {
        get => _settings.Current.SceneArtSize;
        set => SetSceneLook(SceneArtSize, value, s => s.SceneArtSize = value);
    }

    public bool SceneBlobsOn
    {
        get => _settings.Current.SceneBlobs;
        set => SetSceneLook(SceneBlobsOn, value, s => s.SceneBlobs = value);
    }

    public SceneEffectStrength SceneBlobsStrength
    {
        get => _settings.Current.SceneBlobsStrength;
        set => SetSceneLook(SceneBlobsStrength, value, s => s.SceneBlobsStrength = value);
    }

    public bool SceneGlowOn
    {
        get => _settings.Current.SceneGlow;
        set => SetSceneLook(SceneGlowOn, value, s => s.SceneGlow = value);
    }

    public SceneEffectStrength SceneGlowStrength
    {
        get => _settings.Current.SceneGlowStrength;
        set => SetSceneLook(SceneGlowStrength, value, s => s.SceneGlowStrength = value);
    }

    public bool SceneTintOn
    {
        get => _settings.Current.SceneTint;
        set => SetSceneLook(SceneTintOn, value, s => s.SceneTint = value);
    }

    public SceneEffectStrength SceneTintStrength
    {
        get => _settings.Current.SceneTintStrength;
        set => SetSceneLook(SceneTintStrength, value, s => s.SceneTintStrength = value);
    }

    public bool SceneParticlesOn
    {
        get => _settings.Current.SceneParticles;
        set => SetSceneLook(SceneParticlesOn, value, s => s.SceneParticles = value);
    }

    public SceneEffectStrength SceneParticlesStrength
    {
        get => _settings.Current.SceneParticlesStrength;
        set => SetSceneLook(SceneParticlesStrength, value, s => s.SceneParticlesStrength = value);
    }

    /// <summary>Tempo obrotu płyty, w oknie i w scenie.</summary>
    public DiscSpin DiscSpin
    {
        get => _settings.Current.DiscSpin;
        set => SetSceneLook(DiscSpin, value, s => s.DiscSpin = value);
    }

    /// <summary>Mnożnik prędkości obrotu: 15, 9 albo 5 sekund na obrót; zero, gdy płyta stoi.</summary>
    public double DiscSpinFactor => DiscSpin switch
    {
        DiscSpin.Slow => 9.0 / 15.0,
        DiscSpin.Fast => 9.0 / 5.0,
        DiscSpin.Still => 0,
        _ => 1,
    };

    /// <summary>Tempo ruchu tła w oknie i w scenie.</summary>
    public BackdropMotion BackdropMotion
    {
        get => _settings.Current.BackdropMotion;
        set => SetSceneLook(BackdropMotion, value, s => s.BackdropMotion = value);
    }

    /// <summary>Mnożnik tempa ruchu tła.</summary>
    public double BackdropMotionFactor => BackdropMotion == BackdropMotion.Slow ? 0.5 : 1;

    /// <summary>Opadanie słupków widma w scenie.</summary>
    public SpectrumResponse SpectrumResponse
    {
        get => _settings.Current.SpectrumResponse;
        set => SetSceneLook(SpectrumResponse, value, s => s.SpectrumResponse = value);
    }

    /// <summary>Mnożnik czasu opadania słupków i szczytów widma.</summary>
    public double SpectrumFallFactor => SpectrumResponse switch
    {
        SpectrumResponse.Smooth => 1.8,
        SpectrumResponse.Fast => 0.5,
        _ => 1,
    };

    /// <summary>Wizualizacja jest w cyklu klawisza V. Płyta jest w nim zawsze.</summary>
    public bool IsInCycle(SceneVisual visual) =>
        visual == SceneVisual.Disc || !_settings.Current.SceneCycleOff.Contains(visual);

    /// <summary>Włącza albo wyłącza wizualizację w cyklu klawisza V; Płyty nie da się wyłączyć.</summary>
    public void SetInCycle(SceneVisual visual, bool on)
    {
        if (visual == SceneVisual.Disc || IsInCycle(visual) == on) return;

        var off = _settings.Current.SceneCycleOff;
        if (on) off.Remove(visual);
        else off.Add(visual);
        _settings.Touch();
        Raise(nameof(SceneCycleOff));
    }

    /// <summary>Wizualizacje pomijane przez klawisz V; zmiana podnosi powiadomienie o tej nazwie.</summary>
    public IReadOnlyList<SceneVisual> SceneCycleOff => _settings.Current.SceneCycleOff;

    /// <summary>Klucz ekranu, na którym otwiera się scena; <c>null</c> to ekran okna.</summary>
    public string? SceneScreen
    {
        get => _settings.Current.SceneScreen;
        set
        {
            if (_settings.Current.SceneScreen == value) return;

            _settings.Current.SceneScreen = value;
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>Marginesy sceny według kluczy ekranów; zmiana zgłasza tę właściwość.</summary>
    public IReadOnlyDictionary<string, SceneMargin> SceneMargins => _settings.Current.SceneMargins;

    /// <summary>Margines sceny na ekranie o danym kluczu.</summary>
    public SceneMargin GetSceneMargin(string? screenKey) => ScreenMargin.For(_settings.Current.SceneMargins, screenKey);

    /// <summary>Zapisuje margines ekranu od razu; scena otwarta na nim przelicza kadr.</summary>
    public void SetSceneMargin(string? screenKey, SceneMargin margin)
    {
        var margins = _settings.Current.SceneMargins ??= new();
        var key = screenKey ?? string.Empty;
        if (margins.TryGetValue(key, out var current) && current == margin) return;
        if (!margins.ContainsKey(key) && margin == ScreenMargin.Default) return;

        margins[key] = margin;
        _settings.Touch();
        Raise(nameof(SceneMargins));
    }

    // ---------- Karta „Następny” i koniec kolejki w scenie ----------

    /// <summary>Na ile przed końcem utworu pojawia się karta następnego.</summary>
    internal static readonly TimeSpan NextUpLead = TimeSpan.FromSeconds(15);

    /// <summary>Wysokość odczytu okładki karty: 8% wysokości sceny, z zapasem na ekrany o dużej gęstości.</summary>
    private const int NextUpCoverSize = 192;

    private QueueItemViewModel? _nextUpItem;
    private string _nextUpTitle = string.Empty;
    private string _nextUpArtist = string.Empty;
    private Bitmap? _nextUpCover;
    private CoverLoader<Bitmap>? _nextUpCovers;
    private bool _queueEnded;

    /// <summary>Karta następnego utworu jest widoczna.</summary>
    public bool HasNextUp => _nextUpItem is not null && !_radioMode;

    public string NextUpTitle => _nextUpTitle;

    public string NextUpArtist => _nextUpArtist;

    public bool HasNextUpArtist => _nextUpArtist.Length > 0;

    /// <summary>Okładka następnego utworu; null, gdy jej nie ma albo jeszcze się wczytuje.</summary>
    public Bitmap? NextUpCover => _nextUpCover;

    /// <summary>Ostatnie zamówienie okładki karty; testy czekają na jego koniec.</summary>
    internal Task NextUpCoverTask { get; private set; } = Task.CompletedTask;

    /// <summary>Kolejka doszła do końca i nic nie gra; do ruszenia odtwarzania.</summary>
    public bool QueueEnded
    {
        get => _queueEnded && !_radioMode;
        private set => Set(ref _queueEnded, value);
    }

    /// <summary>Odtwarza kolejkę od pierwszego wpisu.</summary>
    public void RestartQueue()
    {
        if (WaitsForRestore(RestartQueue)) return;
        if (Queue.Count == 0) return;

        _resume.Discard();
        if (TryPlayback(() => _engine.PlayIndex(0))) SyncPlaying();
    }

    /// <summary>
    /// Ustala, czy karta jest potrzebna i dla którego wpisu. Zmiana stanu to jedyne powiadomienie:
    /// odświeżanie w pauzie albo bez zmiany nic nie wysyła, więc scena nie dostaje powodu do klatki.
    /// </summary>
    private void UpdateNextUp()
    {
        QueueItemViewModel? item = null;

        if (_isSceneOpen && SceneShowNext && !_disposed)
        {
            var duration = _engine.Duration;
            if (duration > TimeSpan.Zero && duration - _engine.Position <= NextUpLead)
            {
                var index = _engine.NextIndex;
                if (index >= 0 && index < Queue.Count) item = Queue[index];
            }
        }

        var title = item?.Title ?? string.Empty;
        var artist = item?.Artist ?? string.Empty;
        var sameItem = ReferenceEquals(item, _nextUpItem);
        if (sameItem && title == _nextUpTitle && artist == _nextUpArtist) return;

        _nextUpItem = item;
        _nextUpTitle = title;
        _nextUpArtist = artist;

        if (!sameItem) ReplaceNextUpCover(item);

        Raise(nameof(HasNextUp));
        Raise(nameof(NextUpTitle));
        Raise(nameof(NextUpArtist));
        Raise(nameof(HasNextUpArtist));
    }

    private void ReplaceNextUpCover(QueueItemViewModel? item)
    {
        _nextUpCovers?.Cancel();

        var old = _nextUpCover;
        if (old is not null)
        {
            _nextUpCover = null;
            Raise(nameof(NextUpCover));

            // Poprzednia okładka jeszcze znika z ekranu.
            DispatcherTimer.RunOnce(old.Dispose, TimeSpan.FromSeconds(2));
        }

        if (item is null) return;

        var path = item.Path;
        _nextUpCovers ??= new CoverLoader<Bitmap>(p => ReadLargeCover(p, NextUpCoverSize), bitmap => bitmap.Dispose());
        NextUpCoverTask = _nextUpCovers.LoadAsync(path, bitmap =>
        {
            if (bitmap is null) return;

            if (_disposed || _nextUpItem?.Path != path)
            {
                bitmap.Dispose();
                return;
            }

            _nextUpCover = bitmap;
            Raise(nameof(NextUpCover));
        });
    }

    // ---------- Rząd okładek kolejki w scenie ----------

    /// <summary>Wysokość odczytu miniatury rzędu: 16% wysokości sceny, z zapasem na ekrany o dużej gęstości.</summary>
    private const int QueueRowCoverSize = 256;

    private bool _queueRowActive;
    private bool _queueRowFlushPosted;
    private bool _queueRowDirty;
    private string? _queueRowStation;
    private long _queueRowRevision;
    private long _queueRowVersion;
    private int _queueRowCurrent;

    /// <summary>Rząd ma co pokazać: kolejka nie jest pusta i nie gra źródło na żywo.</summary>
    public bool CanShowQueueRow => _radioMode ? Radio.SceneStations().List.Count > 0 : Queue.Count > 0 && !_engine.IsLive;

    /// <summary>Rośnie, gdy widok ma pobrać nową listę rzędu; zgłaszane tylko przy otwartym rzędzie.</summary>
    public long QueueRowRevision => _queueRowRevision;

    /// <summary>Widok ma otwarty rząd; przy zamkniętym nic tu nie kosztuje.</summary>
    internal bool QueueRowActive
    {
        get => _queueRowActive;
        set
        {
            _queueRowActive = value;
            _queueRowDirty = false;
        }
    }

    /// <summary>
    /// Lista rzędu w kolejności odtwarzania i numer bieżącego wpisu. Wiersz, którego model jeszcze nie
    /// ma, bo kolejka silnika zmieniła się przed chwilą, dojdzie przy następnym odświeżeniu.
    /// </summary>
    internal (IReadOnlyList<CoverRowItem> Items, int Current) BuildQueueRow()
    {
        if (_radioMode) return BuildStationRow();

        _queueRowDirty = false;
        _queueRowCurrent = _engine.CurrentIndex;

        var snapshot = _engine.PlaybackOrder;
        _queueRowVersion = snapshot.Version;

        var rows = new Dictionary<QueueEntry, QueueItemViewModel>(Queue.Count);
        foreach (var row in Queue)
            if (row.Entry is { } entry) rows[entry] = row;

        var items = new List<CoverRowItem>(snapshot.Entries.Count);
        var current = -1;

        for (var i = 0; i < snapshot.Entries.Count; i++)
        {
            var entry = snapshot.Entries[i];
            if (!rows.TryGetValue(entry, out var row)) continue;

            if (i == snapshot.CurrentPosition) current = items.Count;

            var path = row.Path;
            items.Add(new CoverRowItem(entry, row.Title, ArtistOf(entry.Metadata) ?? string.Empty,
                row.DurationSeconds > 0 ? row.Duration : null,
                () => ReadLargeCover(path, QueueRowCoverSize)));
        }

        return (items, current);
    }

    /// <summary>
    /// Rząd Sceny w radiu: stacje z listy, z której uruchomiono bieżącą, z logo z pamięci podręcznej
    /// (bez połączeń sieciowych); stacja bez zapisanego logo ma obraz zastępczy rzędu.
    /// </summary>
    private (IReadOnlyList<CoverRowItem> Items, int Current) BuildStationRow()
    {
        _queueRowDirty = false;
        _queueRowStation = Radio.CurrentStation?.Id;
        var (list, current) = Radio.SceneStations();
        var logos = Radio.LogosAllowed ? Radio.Logos : null;

        var items = list.Select(station => new CoverRowItem(
            station.Id,
            Radio.DisplayNameOf(station),
            string.Empty,
            null,
            () => ReadStationLogo(logos, station))).ToList();

        return (items, current);
    }

    private static Bitmap? ReadStationLogo(RadioLogoService? logos, RadioStation station)
    {
        if (logos?.TryGetCached(station) is not { } logo) return null;

        // Rząd przejmuje obraz i sam go zwalnia; mały obraz listy niepotrzebny.
        logo.List.Dispose();
        return logo.Stage;
    }

    /// <summary>Odtwarza wpis rzędu po jego tożsamości, bo numer mógł się już zmienić.</summary>
    internal void PlayQueueRowItem(object id)
    {
        if (_radioMode)
        {
            var (list, _) = Radio.SceneStations();
            if (id is string stationId && list.FirstOrDefault(s => s.Id == stationId) is { } station
                && Radio.Play(station, list))
                SyncPlayingState();
            return;
        }

        if (id is QueueEntry entry && RowFor(entry) is { } row) PlayItem(row);
    }

    /// <summary>Spirala dla wpisów bez okładki, zawsze ciemna jak scena, w parze barw z płyty; właścicielem jest rząd.</summary>
    internal Bitmap? RenderQueueRowPlaceholder()
    {
        try
        {
            return CoilCover.Render(_placeholderDraw.For(_settings.Current.PlaceholderPalette, _current?.Path), darkTheme: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] spirala rzędu okładek: {ex.Message}");
            return null;
        }
    }

    /// <summary>Zmiany przychodzą seriami (wczytanie kolejki to tysiące wierszy), więc widok dostaje jedno zgłoszenie.</summary>
    private void MarkQueueRowStale()
    {
        if (!_queueRowActive || _queueRowFlushPosted) return;

        _queueRowFlushPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queueRowFlushPosted = false;
            if (!_queueRowActive || _disposed) return;

            _queueRowRevision++;
            Raise(nameof(QueueRowRevision));
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Sprawdzane przy odświeżeniu: silnik zmienia kolejność także sam, np. układając następną rundę
    /// losowania, a znaczniki wpisów dochodzą pojedynczo i ich zgłoszenie zbiera się tu w jedno.
    /// </summary>
    private void CheckQueueRow()
    {
        if (!_queueRowActive) return;

        // W radiu rząd zależy tylko od bieżącej stacji; kolejka silnika nie ma z nim związku.
        var stale = _radioMode
            ? _queueRowDirty || Radio.CurrentStation?.Id != _queueRowStation
            : _queueRowDirty || _engine.PlaybackOrderVersion != _queueRowVersion || _engine.CurrentIndex != _queueRowCurrent;

        if (stale) MarkQueueRowStale();
    }

    /// <summary>Czy ekran nie wygasa podczas odtwarzania w scenie.</summary>
    public bool KeepScreenAwake
    {
        get => _settings.Current.KeepScreenAwake;
        set
        {
            if (_settings.Current.KeepScreenAwake == value) return;

            _settings.Current.KeepScreenAwake = value;
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

    /// <summary>
    /// Podpowiedź dodawania muzyki. Kolejka sprawdzana jeszcze po starcie nie jest pusta,
    /// tylko niegotowa, więc podpowiedź na nią czeka.
    /// </summary>
    public bool ShowsAddPrompt => !_radioMode && IsQueueEmpty && _unrestored is null;

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
        Open(paths, replace ? ReplaceOpen : AppendOpen);

    /// <summary>
    /// Dokłada pliki do kolejki, a pustą wypełnia nimi i zaczyna grać. O tym, czy kolejka jest
    /// pusta, rozstrzyga chwila dołożenia: folder przeglądany w tle mógł ją w międzyczasie wypełnić.
    /// </summary>
    public Task AddPaths(IEnumerable<string> paths) => Open(paths, AddOpen);

    // Akcje otwierania; nazwy trafiają do zapisu otwarć niedokończonych przy zamknięciu.
    private const string ReplaceOpen = "Replace";
    private const string AppendOpen = "Append";
    private const string AddOpen = "Add";
    private const string OutsideOpen = "Outside";
    private const string PlaylistOpen = "Playlist";

    // Listę przez tę drogę dokańcza następne uruchomienie, więc tylko dopisuje; zastępuje kolejkę LoadPlaylist.
    private Action<List<string>> ApplyFor(string action) => action switch
    {
        ReplaceOpen => files => Load(files, replace: true),
        AppendOpen or PlaylistOpen => files => Load(files, replace: false),
        OutsideOpen => ApplyFromOutside,
        _ => files => Load(files, replace: IsQueueEmpty),
    };

    private void Load(List<string> files, bool replace)
    {
        if (replace)
        {
            ReplaceQueue(files);
        }
        else
        {
            _engine.Enqueue(files);
            AppendRows();
        }
    }

    /// <summary>
    /// Wstawia pliki na miejsce kolejki i gra pierwszy. Przy zamykaniu okna pierwszy tylko czeka
    /// jak wznowienie i tak trafia do zapisu, bo odtwarzanie otworzyłoby urządzenie do końca zamykania.
    /// </summary>
    private void ReplaceQueue(List<string> files)
    {
        _resume.Discard();

        if (HoldsPlayback)
        {
            _engine.ClearQueue();
            _engine.Enqueue(files);
            RebuildQueue();
            if (!IsClosing) ResetNowPlaying();
            _resume.Arm(0, 0, Queue.Count);
            return;
        }

        if (TryPlayback(() => _engine.SetQueue(files))) SyncPlaying();
        RebuildQueue();
    }

    /// <summary>Termin dokańczania otwarć przy zamykaniu okna; brak, dopóki okno się nie zamyka.</summary>
    private long? _closingDeadline;

    private bool IsClosing => _closingDeadline is not null;

    /// <summary>Dokładane właśnie otwarcie z poprzedniego uruchomienia nie uruchamia odtwarzania.</summary>
    private bool _resumingOpen;

    /// <summary>Dokładane pliki tylko czekają jak wznowienie, zamiast grać.</summary>
    private bool HoldsPlayback => IsClosing || _resumingOpen;

    /// <summary>
    /// Okno zaczyna się zamykać. Od tej chwili biegnie jeden termin
    /// <see cref="SingleInstance.ClosingGrace"/> na dokończenie wszystkich otwarć, bo zamykanie
    /// trzyma rolę działającej kopii, a dokładane pliki nie uruchamiają już odtwarzania.
    /// </summary>
    public void BeginClosing() =>
        _closingDeadline ??= Environment.TickCount64 + (long)SingleInstance.ClosingGrace.TotalMilliseconds;

    private Task _openChain = Task.CompletedTask;

    /// <summary>Kończy się po dołożeniu wszystkiego, co dotąd czekało na sprawdzenie w tle.</summary>
    public Task OpensFinished => _openChain;

    /// <summary>Otwarcia przeglądane w tle i jeszcze niedołożone, w kolejności przybycia.</summary>
    private readonly List<PendingOpen> _pendingOpens = [];

    /// <summary>
    /// Otwarcia niedokończone w terminie zamykania, w kolejności przybycia. Trafiają do zapisu
    /// kolejki, a następne uruchomienie je dokańcza (<see cref="RestoreQueueState"/>).
    /// </summary>
    private readonly List<PendingOpenState> _unfinishedOpens = [];

    /// <summary>
    /// Zapis poprzedniej sesji, którego ścieżki sprawdzają się jeszcze w tle. Dopóki kolejka
    /// nie wróci, to on trafia do zapisu kolejki, żeby jej nie stracić.
    /// </summary>
    private QueueState? _unrestored;

    /// <summary>
    /// Zapis poprzedniej sesji, z której część plików leży na niedostępnym dysku. Trafia do zapisu
    /// kolejki bez zmian, dopóki użytkownik nie zmieni kolejki albo czegoś nie włączy; od tej
    /// chwili kolejką jest to, co widać na ekranie.
    /// </summary>
    private QueueState? _keptSession;

    /// <summary>Przywracanie nie skończyło się przed zamknięciem okna, więc otwarcia po nim też czekają.</summary>
    private bool _restoreLeftUnfinished;

    private sealed class PendingOpen(
        Task<(List<string> Files, string? Unreadable)> expansion, PendingOpenState? request, Action<List<string>> apply)
    {
        public Task<(List<string> Files, string? Unreadable)> Expansion { get; } = expansion;

        /// <summary>Surowe ścieżki i akcja, do zapisu, gdyby otwarcie nie zdążyło; brak przy przywracaniu kolejki.</summary>
        public PendingOpenState? Request { get; } = request;

        public Action<List<string>> Apply { get; } = apply;

        /// <summary>Kolejkę wyczyszczono przed końcem sprawdzania, więc wynik już nikogo nie obchodzi.</summary>
        public bool Dropped { get; set; }

        /// <summary>Kończy się z chwilą anulowania, żeby łańcuch otwarć nie czekał na sprawdzanie, które już nikogo nie obchodzi.</summary>
        public TaskCompletionSource DroppedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Błąd sprawdzania albo dokładania wyniku.</summary>
        public Exception? Failure { get; set; }
    }

    /// <summary>
    /// Zamienia ścieżki na pliki dźwiękowe i przekazuje je dalej. Pliki i foldery sprawdza się
    /// poza wątkiem interfejsu, bo niedostępny dysk sieciowy albo duży katalog zamroziłby okno.
    /// Dokładanie czeka na wcześniejsze otwarcia, więc dwa szybkie upuszczenia nie zamienią się
    /// kolejnością.
    /// </summary>
    /// <param name="now">
    /// Przy zamykaniu okna: kolejka zapisuje się zaraz potem, więc dokłada się przed powrotem,
    /// po wcześniejszych otwarciach. Ścieżki sprawdza się w tle i czeka na nie najwyżej do
    /// wspólnego terminu zamykania (<see cref="FinishPendingOpens"/>), także same pliki: na
    /// niedostępnym dysku sieciowym samo sprawdzenie istnienia potrafi trwać kilkanaście sekund.
    /// </param>
    /// <param name="resumed">Otwarcie z poprzedniego uruchomienia: dokłada bez uruchamiania odtwarzania.</param>
    private Task Open(IEnumerable<string> paths, string action, bool now = false, bool resumed = false)
    {
        // Otwarcie plików w radiu: akcja odtwarzająca przełącza na pliki, samo dopisanie zostawia radio z komunikatem.
        if (_radioMode && !resumed)
        {
            if (action is AppendOpen or PlaylistOpen) ShowNotice(Strings.Current["RadioFilesQueuedNotice"]);
            else SetSource(SourceMode.Files);
        }

        var list = paths.ToList();
        var request = new PendingOpenState { Paths = list, Action = action };
        var apply = ApplyFor(action);

        if (resumed)
        {
            var inner = apply;
            apply = files =>
            {
                _resumingOpen = true;
                try { inner(files); }
                finally { _resumingOpen = false; }
            };
        }

        var pending = new PendingOpen(ExpandInBackground(list, action), request, apply);

        _pendingOpens.Add(pending);

        if (now)
        {
            FinishPendingOpens();
            return Task.CompletedTask;
        }

        // Do chwili dołożenia otwarcie żyje tylko w zapisie kolejki, a nic innego go nie zamówi.
        TouchQueue();

        return _openChain = OpenAfterAsync(_openChain, pending);
    }

    private Task<(List<string> Files, string? Unreadable)> ExpandInBackground(List<string> paths, string action) =>
        Task.Run(() => action == PlaylistOpen
            ? (ReadPlaylist(paths[0]).Paths.ToList(), (string?)null)
            : (ExpandToAudioFiles(paths, out var unreadable), unreadable));

    /// <summary>Dokłada wynik sprawdzania; przy przywracaniu kolejka przestaje czekać także wtedy, gdy nic nie zostało.</summary>
    private void ApplyExpanded(PendingOpen pending, List<string> files)
    {
        if (pending.Request is null) ForgetRestore();

        // Bez plików nic nie zmieni kolejki, a otwarcie ma zniknąć z jej zapisu.
        if (files.Count > 0 || pending.Request is null) pending.Apply(files);
        else TouchQueue();

        if (pending.Request is null) RunCommandsAfterRestore();
    }

    private void ForgetRestore()
    {
        _unrestored = null;
        Raise(nameof(ShowsAddPrompt));
    }

    private async Task OpenAfterAsync(Task previous, PendingOpen pending)
    {
        await previous;

        try
        {
            // Anulowane żądanie zwalnia łańcuch od razu, nie czekając na nieodpowiadający dysk.
            if (await Task.WhenAny(pending.Expansion, pending.DroppedSignal.Task) != pending.Expansion) return;

            var (files, unreadable) = await pending.Expansion;

            // Zamykane okno mogło już dołożyć te pliki albo z nich zrezygnować.
            if (_disposed || !_pendingOpens.Remove(pending)) return;

            ApplyExpanded(pending, files);
            if (unreadable is not null)
                ShowNotice(string.Format(Strings.Current["FolderReadFailed"], FolderName(unreadable)));
        }
        catch (Exception ex)
        {
            pending.Failure = ex;
            _pendingOpens.Remove(pending);
            if (pending.Request is null) ForgetRestore();
            Console.Error.WriteLine($"[cewka] nie udało się dodać plików: {ex}");
        }
    }

    /// <summary>
    /// Dokłada przed zapisem kolejki pliki z otwarć przeglądanych jeszcze w tle, w kolejności
    /// przybycia. Ich nadawcy — inna kopia programu albo upuszczenie — uznali je za przyjęte.
    /// Czeka najwyżej do terminu liczonego od początku zamykania (<see cref="BeginClosing"/>),
    /// wspólnego dla wszystkich wywołań, bo zamykanie trzyma rolę działającej kopii, na którą
    /// czeka następna. Czego nie zdążono przejrzeć — razem z otwarciami po nim, żeby zachować
    /// kolejność — to trafia do zapisu kolejki i dokańcza się przy następnym uruchomieniu.
    /// Tak samo przywracana kolejka: jej zapis zostaje bez zmian.
    /// </summary>
    public void FinishPendingOpens()
    {
        BeginClosing();
        var deadline = _closingDeadline!.Value;

        // Przeglądanie w tle nie potrzebuje wątku interfejsu, więc czekanie tutaj go nie zakleszczy.
        foreach (var pending in _pendingOpens.ToList())
        {
            _pendingOpens.Remove(pending);

            try
            {
                var left = Math.Max(0, deadline - Environment.TickCount64);
                if (_restoreLeftUnfinished || _unfinishedOpens.Count > 0 || !pending.Expansion.Wait(TimeSpan.FromMilliseconds(left)))
                {
                    Console.Error.WriteLine("[cewka] otwarcie nie skończyło się przed zamknięciem okna; dokończy się przy następnym uruchomieniu");

                    if (pending.Request is null) _restoreLeftUnfinished = true;
                    else _unfinishedOpens.Add(pending.Request);

                    continue;
                }

                ApplyExpanded(pending, pending.Expansion.Result.Files);
            }
            catch (Exception ex)
            {
                if (pending.Request is null) ForgetRestore();
                Console.Error.WriteLine($"[cewka] nie udało się dodać plików: {ex}");
            }
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
    /// <para>Wybór plików w programie (<c>Ctrl+O</c>) idzie inną drogą, bo tam dodanie jest już
    /// rozstrzygnięte.</para>
    /// </summary>
    /// <param name="now">Przy zamykaniu okna: bez przeglądania w tle, zob. <see cref="Open"/>.</param>
    public Task OpenFromOutside(IEnumerable<string> paths, bool now = false) => Open(paths, OutsideOpen, now);

    private void ApplyFromOutside(List<string> files)
    {
        switch (_settings.Current.FileOpenAction)
        {
            case FileOpenAction.ReplaceAndPlay:
                ReplaceQueue(files);
                break;

            case FileOpenAction.AppendAndPlay:
            {
                // Numer pierwszego dołożonego pliku trzeba odczytać przed dołożeniem: potem
                // długość kolejki obejmuje już nowe pozycje i nie wiadomo, gdzie się zaczynają.
                var first = Queue.Count;
                _engine.Enqueue(files);
                AppendRows();
                _resume.Discard();

                // Przy zamykaniu pierwszy dołożony plik tylko czeka jak wznowienie, zob. ReplaceQueue.
                if (HoldsPlayback) _resume.Arm(first, 0, Queue.Count);
                else if (TryPlayback(() => _engine.PlayIndex(first))) SyncPlaying();

                RestoreCurrentMarker();
                break;
            }

            default:
                // Pusta kolejka to przypadek osobny: nie ma czego przerywać, a program, który
                // po otwarciu pliku milczy, wygląda na zepsuty.
                if (IsQueueEmpty)
                {
                    ReplaceQueue(files);
                }
                else
                {
                    _engine.Enqueue(files);
                    AppendRows();
                }

                break;
        }
    }

    // ---------- Awaria wyjścia dźwięku ----------

    /// <summary>Klucz słownika z opisem awarii; pusty, gdy wszystko działa.</summary>
    private string _audioFailureKey = string.Empty;

    /// <summary>
    /// Dlaczego odtwarzanie jest niemożliwe; puste, gdy wszystko działa. Brak wyjścia dźwięku
    /// nie zamyka okna. Tekst wyjątku trafia wyłącznie do dziennika.
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

    private readonly PathProbe _paths;

    /// <summary>
    /// Foldery przeglądane są rekurencyjnie, z pominięciem plików nierozpoznanych. W każdym katalogu
    /// najpierw pliki, potem podkatalogi, jedne i drugie w porządku naturalnym: „2 - y” przed
    /// „10 - x”, tak jak w menedżerze plików.
    ///
    /// <para>Folder, którego nie da się przejrzeć, jest pomijany, a pierwszy taki trafia do
    /// <paramref name="unreadable"/>. Nieczytelny podkatalog pomija się po cichu.</para>
    /// </summary>
    private List<string> ExpandToAudioFiles(IEnumerable<string> paths, out string? unreadable)
    {
        unreadable = null;
        var result = new List<string>();
        var visited = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (_paths.DirectoryExists(path))
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
            else if (_paths.FileExists(path) && IsAudio(path))
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
        _keptSession = null;
        SetUndo(null);
        Raise(nameof(QueueSummary));
        Raise(nameof(IsQueueEmpty));
        Raise(nameof(ShowsAddPrompt));
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
    /// Przywraca znacznik grającego wiersza po zmianie wierszy, na podstawie wpisu z silnika.
    /// </summary>
    private void RestoreCurrentMarker()
    {
        var index = _engine.CurrentIndex;
        var row = index >= 0 && index < Queue.Count ? Queue[index] : null;

        if (!ReferenceEquals(row, _current) && _current is not null) _current.IsCurrent = false;

        _current = row;
        if (_current is not null) _current.IsCurrent = true;

        Raise(nameof(CurrentIndex));
        UpdateNextUp();
        MarkQueueRowStale();
    }

    /// <summary>
    /// Czyta tagi poza wątkiem interfejsu, tylko dla wpisów jeszcze nieprzeczytanych; wiersze
    /// pojawiają się od razu z nazwą pliku. Wynik trafia do wiersza tego samego wpisu, bo wiersz
    /// mógł się w międzyczasie przesunąć.
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

                BeforeBackgroundMetadataRead?.Invoke(entry);
                try { entry.EnsureMetadata(); }
                catch { continue; }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token != _metadataGeneration || RowFor(entry) is not { } row) return;
                    ApplyMetadataToRow(row, entry);
                    _queueRowDirty = true;
                }, DispatcherPriority.Background);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token == _metadataGeneration) Raise(nameof(QueueSummary));
            }, DispatcherPriority.Background);
        });
    }

    /// <summary>
    /// Wołane w wątku tła tuż przed odczytem tagów wpisu. Program go nie ustawia; testy wstrzymują
    /// nim odczyt, żeby kolejność „odczyt — zmiana kolejki” nie zależała od tempa maszyny.
    /// </summary>
    internal Action<QueueEntry>? BeforeBackgroundMetadataRead { get; set; }

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

        var artist = ArtistOf(metadata);
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(metadata.Album))
            parts.Add($"{artist} — {metadata.Album}");
        else if (!string.IsNullOrWhiteSpace(artist)) parts.Add(artist);
        else if (!string.IsNullOrWhiteSpace(metadata.Album)) parts.Add(metadata.Album);

        if (metadata.Year is > 0) parts.Add(metadata.Year.Value.ToString());

        return string.Join(" · ", parts);
    }

    /// <summary>Wykonawca utworu, a gdy go brak, wykonawca albumu; jedna reguła dla wiersza okna i sceny.</summary>
    private static string? ArtistOf(TrackMetadata? metadata) => metadata?.Artist ?? metadata?.AlbumArtist;

    public void PlayItem(QueueItemViewModel item)
    {
        var index = Queue.IndexOf(item);
        if (index < 0) return;

        _resume.Discard();
        if (TryPlayback(() => _engine.PlayIndex(index))) SyncPlaying();
    }

    public void Next()
    {
        if (_radioMode)
        {
            Radio.StepDelayed(1);
            return;
        }

        if (WaitsForRestore(Next)) return;

        _resume.Discard();
        if (TryPlayback(_engine.Next)) SyncPlaying();
    }

    public void Previous()
    {
        if (_radioMode)
        {
            Radio.StepDelayed(-1);
            return;
        }

        if (WaitsForRestore(Previous)) return;

        _resume.Discard();
        if (TryPlayback(_engine.Previous)) SyncPlaying();
    }

    /// <summary>Polecenia naciśnięte, gdy kolejka dopiero wraca, w kolejności naciśnięcia.</summary>
    private readonly List<Action> _commandsDuringRestore = [];

    /// <summary>
    /// Na niewróconej jeszcze kolejce polecenie nie ma czego grać, a przepaść też nie powinno.
    /// Czeka więc na jej powrót, tak samo jak klawisz naciśnięty chwilę później.
    /// </summary>
    private bool WaitsForRestore(Action command)
    {
        if (_unrestored is null || Queue.Count > 0) return false;

        _commandsDuringRestore.Add(command);
        return true;
    }

    private void RunCommandsAfterRestore()
    {
        var commands = _commandsDuringRestore.ToList();
        _commandsDuringRestore.Clear();

        if (IsClosing || Queue.Count == 0) return;

        foreach (var command in commands) command();
    }

    public void TogglePlay()
    {
        if (_radioMode)
        {
            ToggleRadioPlay();
            return;
        }

        if (WaitsForRestore(TogglePlay)) return;

        // Pierwsze naciśnięcie po starcie wznawia utwór przywrócony z poprzedniej sesji.
        if (ResumeRestoredTrack()) return;

        // Pusta kolejka: nie ma czego grać, więc nie ma też powodu zajmować karty dźwiękowej.
        if (Queue.Count == 0) return;

        if (TryPlayback(_engine.TogglePlay)) IsPlaying = _engine.State == PlaybackState.Playing;
    }

    /// <summary>Stan przycisku wprost z silnika: „Poprzedni” w pauzie tylko przewija i nie wznawia gry.</summary>
    private void SyncPlaying() => IsPlaying = _engine.State == PlaybackState.Playing;

    /// <summary>Jawne „Play”: nie przełącza, więc nie zatrzyma grającego. Przy przywracaniu czeka w kolejce poleceń.</summary>
    public void Play()
    {
        if (WaitsForRestore(Play)) return;

        if (!IsPlaying) TogglePlay();
    }

    /// <summary>
    /// Przy przywracaniu pauza (i zatrzymanie) czeka w kolejce poleceń za wcześniejszym „Play”,
    /// bo na pustym silniku zadziałałaby od razu, a wcześniejsze „Play” ruszyłoby muzykę po powrocie.
    /// </summary>
    public void Pause()
    {
        if (WaitsForRestore(Pause)) return;

        _engine.Pause();
        IsPlaying = false;
    }

    public void TogglePanel() => PanelOpen = !PanelOpen;

    /// <summary>Klawisz L, przycisk kolejki w menu, uchwyt i przypinka w stopce: przypina albo odpina panel.</summary>
    public void ToggleQueue() => QueuePinned = !QueuePinned;

    // ---------- Źródło dźwięku: pliki albo radio ----------

    private bool _radioMode;

    /// <summary>Lista stacji i to, co gra ze stacji; wczytywana dopiero przy pierwszym wejściu w radio.</summary>
    public RadioViewModel Radio { get; }

    public bool IsRadioMode => _radioMode;

    public bool IsFilesMode => !_radioMode;

    /// <summary>Losowo i powtarzanie w radiu są ukryte, a miejsce zostaje: rząd przycisków się nie przesuwa.</summary>
    public double FilesOnlyOpacity => _radioMode ? 0 : 1;

    /// <summary>Odczyt stanu gra / pauza prosto z silnika, po poleceniu wydanym poza modelem widoku.</summary>
    public void SyncPlayingState() => SyncPlaying();

    public string SourceFilesTooltip => Strings.Current["TooltipSourceFiles"];

    public string SourceRadioTooltip => Strings.Current["TooltipSourceRadio"];

    /// <summary>Nazwa i podpowiedź uchwytu panelu: kolejka przy plikach, stacje przy radiu.</summary>
    public string QueueHandleName => Strings.Current[IsRadioMode ? "QueueHandleRadio" : "QueueHandleFiles"];

    /// <summary>
    /// Przełącza źródło jak w wieży: stan „gra / nie gra” się nie zmienia. Plik pauzuje w miejscu (silnik pamięta
    /// pozycję), a rusza ostatnia stacja; po powrocie do plików stacja się rozłącza i ten sam utwór gra dalej.
    /// </summary>
    public void SetSource(SourceMode mode)
    {
        var radio = mode == SourceMode.Radio;
        if (radio == _radioMode) return;
        if (radio && !RadioAvailable) return;

        var playing = _engine.State == PlaybackState.Playing;
        Radio.CancelPendingStep();
        _radioMode = radio;

        if (radio)
        {
            Radio.Activate();
            FormatBadge = Radio.Badge;
            if (playing && !Radio.PlaySelected())
            {
                _engine.Pause();
                playing = false;
            }
        }
        else
        {
            if (_engine.IsLive) _engine.StopLive();
            FormatBadge = _currentMetadata?.BuildBadge(null) ?? string.Empty;
            // Pozycja z poprzedniej sesji, której nic jeszcze nie zużyło, rusza razem z plikiem.
            if (playing && Queue.Count > 0 && !ResumeRestoredTrack()) TryPlayback(_engine.Play);
        }

        _settings.Current.Source = mode;
        _settings.Touch();

        SampleRadioLogo();
        RefreshPalettes();
        PublishToMediaPanel();
        SyncPlaying();
        Raise(nameof(IsRadioMode));
        Raise(nameof(QueueHandleName));
        Raise(nameof(ActiveSceneVisual));
        RefreshSceneEnvelope();
        Raise(nameof(IsFilesMode));
        Raise(nameof(ShowsTrackQuality));
        Raise(nameof(SceneLine));
        Raise(nameof(HasSceneLine));
        Raise(nameof(HasSceneAlbum));
        Raise(nameof(SceneCaptionIsLive));
        Raise(nameof(SceneLiveCaption));
        Raise(nameof(HasNextUp));
        Raise(nameof(QueueEnded));
        Raise(nameof(CanShowQueueRow));
        Raise(nameof(FilesOnlyOpacity));
        Raise(nameof(ShowsAddPrompt));
        Raise(nameof(ShownTitle));
        Raise(nameof(ShownLine));
        Raise(nameof(HasShownLine));
        Raise(nameof(ShowsPauseIcon));
        Raise(nameof(ShowsStopIcon));
        Raise(nameof(PlayPauseTooltip));
    }

    /// <summary>Radio jest włączone w ustawieniach; wyłączone chowa przełącznik źródła i skróty.</summary>
    public bool RadioAvailable => _settings.Current.RadioEnabled;

    /// <summary>
    /// Włącza albo wyłącza radio. Wyłączenie wraca do plików (stacja się rozłącza), zatrzymuje pobieranie logo
    /// i chowa przełącznik źródła, a z nim skróty Ctrl+1 i Ctrl+2.
    /// </summary>
    public void SetRadioEnabled(bool enabled)
    {
        if (_settings.Current.RadioEnabled == enabled) return;

        if (!enabled) SetSource(SourceMode.Files);
        _settings.Current.RadioEnabled = enabled;
        _settings.Touch();
        Radio.ApplyLogoPolicy();
        Raise(nameof(RadioAvailable));
    }

    public void ToggleSource() => SetSource(_radioMode ? SourceMode.Files : SourceMode.Radio);

    /// <summary>W radiu pauza to zatrzymanie: „Odtwórz” łączy od bieżącego miejsca transmisji.</summary>
    private void ToggleRadioPlay()
    {
        Radio.CancelPendingStep();
        if (_engine.State == PlaybackState.Playing && !Radio.HasFailed)
        {
            _engine.Pause();
            IsPlaying = false;
            return;
        }

        if (Radio.PlaySelected()) SyncPlaying();
    }

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
        var waiting = _resume.IsArmed && _resume.Index == index;
        var undo = new QueueUndo("QueueUndoRemove", [item.Path], index,
            wasCurrent || waiting,
            waiting ? _resume.Seconds : wasCurrent ? _engine.Position.TotalSeconds : 0,
            item);

        _engine.RemoveAt(index);
        _resume.OnRemoved(index);

        Queue.RemoveAt(index);
        Renumber(index);
        QueueChanged();

        if (!wasCurrent || _engine.CurrentIndex >= 0)
        {
            RestoreCurrentMarker();
        }
        else
        {
            ResetNowPlaying();
            if (Queue.Count > 0) _resume.Arm(Math.Min(index, Queue.Count - 1), 0, Queue.Count);
        }

        SetUndo(undo);
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
        var undo = Queue.Count > 0 ? SnapshotQueue("QueueUndoClear") : null;

        DropPendingOpens();

        _engine.ClearQueue();
        _resume.Discard();

        RebuildQueue();
        ResetNowPlaying();
        SetUndo(undo);
    }

    /// <summary>
    /// Otwarcia sprzed wyczyszczenia, czekające jeszcze na sprawdzenie, nie wracają po nim do kolejki.
    /// Zostaje przywracana poprzednia sesja, bo tej nikt jeszcze nie widział.
    /// </summary>
    private void DropPendingOpens()
    {
        foreach (var pending in _pendingOpens.Where(pending => pending.Request is not null).ToList())
        {
            pending.Dropped = true;
            pending.DroppedSignal.TrySetResult();
            _pendingOpens.Remove(pending);
        }
    }

    // ---------- Cofanie zmian kolejki ----------

    /// <summary>
    /// Jeden krok wstecz: ostatnie usunięcie wiersza, wyczyszczenie kolejki albo zastąpienie jej
    /// listą odtwarzania. Każda inna zmiana kolejki unieważnia go.
    /// </summary>
    /// <param name="Index">Wiersz usunięty albo wiersz, od którego wznawiać.</param>
    /// <param name="Resume">Po cofnięciu wznowienie czeka na wierszu <paramref name="Index"/>.</param>
    /// <param name="Row">
    /// Usunięty wiersz z jego wpisem. Cofnięcie wstawia ten sam wpis, więc utwór, który jeszcze
    /// gra z bufora, nie zaczyna się od nowa.
    /// </param>
    private sealed record QueueUndo(
        string LabelKey, IReadOnlyList<string> Paths, int Index, bool Resume, double Seconds,
        QueueItemViewModel? Row = null);

    private QueueUndo? _undo;

    public bool CanUndoQueue => _undo is not null;

    /// <summary>Nazwa polecenia cofnięcia, np. „Cofnij usunięcie”.</summary>
    public string UndoQueueLabel => _undo is null ? string.Empty : Strings.Current[_undo.LabelKey];

    private void SetUndo(QueueUndo? undo)
    {
        if (ReferenceEquals(_undo, undo)) return;

        _undo = undo;
        Raise(nameof(CanUndoQueue));
        Raise(nameof(UndoQueueLabel));
    }

    /// <summary>Cała kolejka z miejscem, od którego wznowić, przed jej wyczyszczeniem albo zastąpieniem.</summary>
    private QueueUndo SnapshotQueue(string labelKey)
    {
        var armed = _resume.IsArmed;
        var index = armed ? _resume.Index : _engine.CurrentIndex;

        return new QueueUndo(labelKey, _engine.QueuePaths.ToList(), index, index >= 0,
            armed ? _resume.Seconds : _engine.Position.TotalSeconds);
    }

    /// <summary>
    /// Cofa ostatnią zmianę kolejki. Przywrócona kolejka nie gra sama: utwór, który grał albo
    /// czekał na wznowienie, czeka na naciśnięcie odtwarzania w tym samym miejscu.
    /// </summary>
    public void UndoQueue()
    {
        if (_undo is not { } undo) return;
        SetUndo(null);

        if (undo.LabelKey == "QueueUndoRemove" && undo.Row is { Entry: { } entry } row)
        {
            var index = Math.Min(undo.Index, Queue.Count);

            _engine.Restore(entry, index);
            _resume.OnInserted(index);

            // Znacznik bieżącego ustawia niżej silnik; wiersz mógł go mieć przed usunięciem.
            // Tagi odczytane w tle, gdy wiersza nie było w kolejce, nie miały go gdzie wpisać.
            row.IsCurrent = false;
            Queue.Insert(index, row);
            ApplyMetadataToRow(row, entry);
            Renumber(index);
            RestoreCurrentMarker();
            QueueChanged();
            if (entry.Metadata is null) _ = FillMetadataInBackgroundAsync([entry]);

            if (undo.Resume && _engine.CurrentIndex < 0) _resume.Arm(index, undo.Seconds, Queue.Count);
            return;
        }

        _engine.ClearQueue();
        _resume.Discard();
        _engine.Enqueue(undo.Paths);

        RebuildQueue();
        ResetNowPlaying();

        if (undo.Resume) _resume.Arm(undo.Index, undo.Seconds, Queue.Count);
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
        Artist = string.Empty;
        Album = string.Empty;
        FormatBadge = string.Empty;
        IsPlaying = _engine.State == PlaybackState.Playing;

        _shownEntry = null;
        Raise(nameof(CurrentFilePath));
        RefreshSceneEnvelope();
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
        if (_radioMode) return;

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
    /// Zapisuje kolejkę i pozycję dla następnego uruchomienia. Wołane
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

        // Gdy gra stacja, Position opisuje strumień; plik czeka w zakładce silnika i to jego miejsce się zapisuje.
        var filePlace = _engine.IsLive ? _engine.FilePlaceBehindLive ?? (-1, 0) : ((int Index, double Seconds)?)null;

        // Otwarcia jeszcze niedołożone: odłożone przy zamykaniu i przeglądane w tle, żeby
        // nie przepadły przy nagłym końcu programu.
        var opens = _unfinishedOpens
            .Concat(_pendingOpens.Where(pending => pending.Request is not null).Select(pending => pending.Request!))
            .ToList();

        var state = new QueueState
        {
            Paths = _engine.QueuePaths.ToList(),
            CurrentIndex = armed ? _resume.Index : filePlace?.Index ?? _engine.CurrentIndex,
            PositionSeconds = armed ? _resume.Seconds : filePlace?.Seconds ?? _engine.Position.TotalSeconds,
            Shuffle = _engine.Shuffle,
            Repeat = _engine.Repeat.ToString(),
            PendingOpens = opens.Count > 0 ? opens : null,
        };

        // Poprzednia sesja jeszcze nie wróciła. Otwarcie sprzed niej mogło już dołożyć pliki
        // i wtedy bieżący wpis zostaje jego. Tryby wróciły od razu, więc są już w silniku.
        if (_unrestored is { } unrestored)
        {
            if (state.Paths.Count == 0)
            {
                state.CurrentIndex = unrestored.CurrentIndex;
                state.PositionSeconds = unrestored.PositionSeconds;
            }

            state.Paths.AddRange(unrestored.Paths);
        }

        if (_keptSession is { } kept)
        {
            state.Paths = kept.Paths;
            state.CurrentIndex = kept.CurrentIndex;
            state.PositionSeconds = kept.PositionSeconds;
        }

        return state;
    }

    /// <summary>
    /// Przywraca poprzednią kolejkę bez uruchamiania odtwarzania; pozycja czeka na pierwsze
    /// naciśnięcie odtwarzania. Potem dokańcza otwarcia niedokończone przy zamknięciu.
    /// </summary>
    /// <returns>Zadanie kończy się po dołożeniu wszystkiego, co czekało na sprawdzenie w tle.</returns>
    public Task RestoreQueueState()
    {
        var state = QueueStateStore.Load(_queueFile);

        // Ustawienie wyłączone: program zaczyna od pustej kolejki. Zapis kolejki trwa mimo to,
        // żeby ponowne włączenie przywracania miało co przywrócić.
        if (_settings.Current.RestoreSession) RestoreQueue(state);

        // Otwarcia to jawne żądania, a nie poprzednia sesja, więc dokańczają się zawsze.
        ResumeOpens(state);
        return _openChain;
    }

    /// <summary>
    /// Start ze ścieżkami z wiersza poleceń. Mają one pierwszeństwo przed poprzednią kolejką, ale
    /// tylko wtedy, gdy dają pliki: bez plików wraca ona tak jak przy zwykłym starcie, a dopóki
    /// to nie jest rozstrzygnięte, czeka w zapisie. Otwarcia niedokończone przy poprzednim
    /// zamknięciu dopisują się za tym wszystkim, bez odtwarzania.
    /// </summary>
    /// <returns>Zadanie kończy się po dołożeniu wszystkiego, co czekało na sprawdzenie w tle.</returns>
    public Task OpenAtStart(IEnumerable<string> paths)
    {
        var state = QueueStateStore.Load(_queueFile);

        OpenFromOutside(paths);

        if (_settings.Current.RestoreSession) RestoreQueue(state, _pendingOpens[^1].Expansion);

        ResumeOpens(state);
        return _openChain;
    }

    /// <summary>
    /// Otwiera zwykłą drogą, w tle i w kolejności przybycia, ale zawsze tylko dopisuje na koniec
    /// kolejki: nie zastępuje jej, nie przestawia bieżącego utworu i nie uruchamia odtwarzania,
    /// bo to, co gra po starcie, wybrał już użytkownik. Dotyczy to także listy odtwarzania, która
    /// zostaje listą, żeby znów przeczytać jej plik. Z zapisu kolejki znikają przy najbliższym
    /// zapisie po dołożeniu.
    /// </summary>
    private void ResumeOpens(QueueState? state)
    {
        foreach (var open in state?.PendingOpens ?? [])
            Open(open.Paths, open.Action == PlaylistOpen ? PlaylistOpen : AppendOpen, resumed: true);
    }

    /// <param name="state">Zapis poprzedniej sesji.</param>
    /// <param name="first">Otwarcie, które ma pierwszeństwo: gdy da pliki, poprzednia kolejka ustępuje mu miejsca.</param>
    private void RestoreQueue(QueueState? state, Task<(List<string> Files, string? Unreadable)>? first = null)
    {
        if (state is null) return;

        // Tryby wracają od razu: sprawdzanie ścieżek potrafi trwać, a przełączenie w tym czasie
        // jest już wyborem użytkownika, którego koniec przywracania nie może cofnąć.
        _engine.Shuffle = state.Shuffle;
        _engine.Repeat = state.ReadRepeat();
        Raise(nameof(ShuffleEnabled));
        RaiseRepeat();

        if (state.Paths.Count == 0) return;

        // Pliki mogły zniknąć między uruchomieniami. Numer bieżącego wpisu liczy się po
        // odfiltrowaniu, żeby wskazywał ten sam plik; gdy zniknął on sam, wznowienia nie ma.
        var resumeIndex = -1;
        var unreachable = false;
        var expansion = Task.Run(async () =>
        {
            var existing = new List<string>(state.Paths.Count);

            if (first is not null)
            {
                await Task.WhenAny(first).ConfigureAwait(false);
                if (first.IsCompletedSuccessfully && first.Result.Files.Count > 0) return (existing, (string?)null);
            }

            for (var i = 0; i < state.Paths.Count; i++)
            {
                if (!_paths.FileExists(state.Paths[i]))
                {
                    unreachable = unreachable || IsFolderUnreachable(state.Paths[i]);
                    continue;
                }

                if (i == state.CurrentIndex) resumeIndex = existing.Count;
                existing.Add(state.Paths[i]);
            }

            return (existing, (string?)null);
        });

        _unrestored = state;
        Raise(nameof(ShowsAddPrompt));

        var pending = new PendingOpen(expansion, null, existing => ApplyRestored(state, existing, resumeIndex, unreachable));
        _pendingOpens.Add(pending);
        _openChain = OpenAfterAsync(_openChain, pending);
    }

    /// <summary>Plik zniknął razem z folderem: to raczej niedostępny dysk niż usunięty plik.</summary>
    private bool IsFolderUnreachable(string path) =>
        Path.GetDirectoryName(path) is { Length: > 0 } folder && !_paths.DirectoryExists(folder);

    private void ApplyRestored(QueueState state, List<string> existing, int resumeIndex, bool unreachable)
    {
        if (existing.Count > 0)
        {
            // Przy włączonej losowości dołożone wpisy wchodzą w kolejność losową, a pierwsze
            // odtworzenie stawia na jej początku utwór wznawiany.
            _engine.Enqueue(existing);

            RebuildQueue();

            if (_current is null) Title = IdleTitle;

            _resume.Arm(resumeIndex, state.PositionSeconds, existing.Count);
        }

        // Ustawiane po przebudowie wierszy, bo każda zmiana kolejki je zdejmuje.
        _keptSession = unreachable && Queue.Count == existing.Count ? state : null;
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
    /// <para>Osobno od <see cref="AudioFailure"/>, który opisuje stan trwały.</para>
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
    /// Informacja o nowszym wydaniu. Opisuje stan, więc nie gaśnie sama; składana przy odczycie,
    /// żeby obejmowała ją zmiana języka.
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
    /// <para>Wołane przy otwarciu okna bez czekania na wynik, żeby start nie zależał od sieci.
    /// Przy sprawdzaniu wyłączonym (domyślnie) nie łączy się z niczym.</para>
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
    /// <para>Odtwarzanie nie zaczyna się samo: pierwszy utwór czeka tak jak wznowienie
    /// z poprzedniej sesji, więc pierwsze naciśnięcie odtwarzania rusza od początku listy.</para>
    ///
    /// <para>Plik listy i jej ścieżki sprawdza się poza wątkiem interfejsu, a lista jest zwykłym
    /// otwarciem w kolejce, więc nie zamieni się kolejnością z upuszczeniem. Gotowa w terminie
    /// zamykania wstawia się przed zapisem; niegotowa trafia do zapisu kolejki, a następne
    /// uruchomienie tylko dopisuje jej pliki na koniec, nie zastępując kolejki.</para>
    /// </summary>
    /// <returns>
    /// Wynik odczytu po dojściu kolejki otwarć do listy; null, gdy okno zaczęło się zamykać albo
    /// kolejkę wyczyszczono, bo nie ma już czego zgłaszać. Błąd odczytu należy do wołającego,
    /// a nie do otwarć czekających za listą.
    /// </returns>
    public Task<PlaylistLoad?> LoadPlaylist(string path)
    {
        var reading = Task.Run(() => ReadPlaylist(path));

        var pending = new PendingOpen(
            FilesOfAsync(reading),
            new PendingOpenState { Paths = [path], Action = PlaylistOpen },
            _ => ReplaceWithPlaylist(reading.Result));

        _pendingOpens.Add(pending);
        TouchQueue();
        _openChain = OpenAfterAsync(_openChain, pending);

        return PlaylistResultAsync(_openChain, reading, pending);
    }

    private PlaylistLoad ReadPlaylist(string path) => PlaylistFile.Load(path, null, _paths.FileExists);

    /// <summary>Bez powrotu na wątek interfejsu, bo zamykanie okna czeka na ten wynik właśnie na nim.</summary>
    private static async Task<(List<string> Files, string? Unreadable)> FilesOfAsync(Task<PlaylistLoad> reading)
    {
        var load = await reading.ConfigureAwait(false);
        return (load.Paths.ToList(), null);
    }

    private async Task<PlaylistLoad?> PlaylistResultAsync(Task opened, Task<PlaylistLoad> reading, PendingOpen pending)
    {
        await opened;
        if (_disposed || IsClosing || pending.Dropped) return null;

        var load = await reading;

        // Odczyt się udał, ale listy nie wstawiono, np. przez błąd urządzenia przy zatrzymaniu.
        if (pending.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);

        return load;
    }

    internal PlaylistLoad ReplaceWithPlaylist(PlaylistLoad load)
    {
        if (load.Paths.Count == 0) return load;

        var undo = Queue.Count > 0 ? SnapshotQueue("QueueUndoReplace") : null;

        _resume.Discard();
        _engine.ClearQueue();
        _engine.Enqueue(load.Paths);

        RebuildQueue();
        if (!IsClosing) ResetNowPlaying();

        _resume.Arm(0, 0, load.Paths.Count);
        SetUndo(undo);

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

        var saved = seconds;

        // Pozycja za końcem utworu (plik podmieniono na krótszy) zaczyna go od początku,
        // zamiast od razu przeskakiwać do następnego.
        var duration = _engine.Queue.ElementAtOrDefault(index)?.Duration ?? TimeSpan.Zero;
        if (seconds <= 1 || (duration > TimeSpan.Zero && seconds >= duration.TotalSeconds - 1)) seconds = 0;

        if (TryPlayback(() => _engine.PlayIndex(index, TimeSpan.FromSeconds(seconds))))
            IsPlaying = _engine.State == PlaybackState.Playing;
        else
            // Odtwarzanie nie ruszyło, więc pozycja czeka na ponowienie i trafia do zapisu sesji.
            _resume.Arm(index, saved, Queue.Count);

        return true;
    }

    // ---------- Odświeżanie ----------

    /// <summary>
    /// Wykonuje odświeżenie od razu, zamiast czekać na zegar. Wyłącznie dla narzędzia zrzutów.
    ///
    /// <para>Pułapka: narzędzie zrzutów pompuje dyspozytora wywołaniami <c>RunJobs</c> bez pętli
    /// komunikatów i zegar odświeżania potrafi wtedy nie ruszyć wcale.</para>
    /// </summary>
    public void RefreshForCapture() => Refresh();

    private void Refresh()
    {
        Raise(nameof(WaveLevel));

        var duration = _engine.Duration;
        var position = _engine.Position;
        var hours = duration.TotalHours >= 1;

        Total = duration > TimeSpan.Zero ? FormatTime(duration.TotalSeconds, hours) : "0:00";

        // Podczas przeciągania suwak należy do wskaźnika, inaczej odświeżanie wyrywałoby
        // uchwyt spod kursora.
        if (!_scrubbing)
        {
            Elapsed = FormatTime(position.TotalSeconds, hours);

            // Ustawienie przez pole omija setter, żeby odświeżanie nie wywołało przewijania.
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

        if (_radioMode) Radio.RefreshBadge();
        RefreshBitrateBadge();
        UpdateNextUp();
        CheckQueueRow();

        // Bez gry wartości stoją w miejscu: zegar zwalnia, a wraca do pełnego tempa przy
        // wznowieniu i przy przeciąganiu suwaka.
        _refresh.Interval = playing || _scrubbing ? RefreshInterval : IdleRefreshInterval;

        if (_isSceneOpen && DateTime.UtcNow - _lastBatteryCheck >= BatteryCheckInterval) RefreshBattery();

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
    /// Odświeża plakietkę zgodnie z bieżącą przepływnością, tylko dla plików o zmiennej
    /// przepływności.
    /// </summary>
    private void RefreshBitrateBadge()
    {
        if (_currentMetadata is not { IsVariableBitrate: true } metadata) return;

        var live = _engine.InstantaneousBitrate;
        if (live <= 0) return;

        // Kwantyzacja do 2 kbps, bo inaczej ostatnia cyfra migotałaby kilka razy na sekundę.
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
        // Numer ze zdarzenia powstał na innym wątku i mógł się zestarzeć; rozstrzyga stan silnika
        // teraz. Gdy nic nie jest bieżące, zdarzenie dotyczy utworu, który już nie gra.
        index = _engine.CurrentIndex;

        if (index < 0 || index >= Queue.Count) return;
        if (_engine.Queue.ElementAtOrDefault(index) is not { } entry) return;

        _keptSession = null;

        var row = Queue[index];
        if (!ReferenceEquals(row, _current))
        {
            if (_current is not null) _current.IsCurrent = false;
            _current = row;
            row.IsCurrent = true;
        }

        row.ClearTemporaryFailure();
        UpdateNextUp();
        MarkQueueRowStale();

        var metadata = entry.Metadata;
        var newTrack = !ReferenceEquals(entry, _shownEntry);

        if (newTrack || (metadata is not null && !ReferenceEquals(metadata, _currentMetadata)))
        {
            _shownEntry = entry;
            Raise(nameof(CurrentFilePath));
            RefreshSceneEnvelope();
            _currentMetadata = metadata;
            _shownBitrate = 0;

            Title = metadata?.Title ?? entry.Title;
            ArtistLine = BuildAlbumLine(entry);
            Artist = ArtistOf(metadata) ?? string.Empty;
            Album = metadata?.Album ?? string.Empty;
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
        CreateCoverLoader(path => DecodeCover(MetadataReader.ReadCover(path)));

    /// <summary>
    /// Odczyt okładek, który spóźniony wynik zwalnia w całości: obraz płyty i rozmytą okładkę.
    /// Testy podają własny odczyt, reguła zwalniania zostaje ta sama.
    /// </summary>
    internal static CoverLoader<DecodedCover> CreateCoverLoader(Func<string, DecodedCover?> read) =>
        new(read, cover => cover.Discard());

    /// <summary>Ostatni zamówiony odczyt okładki; testy czekają na jego koniec.</summary>
    internal Task CoverTask { get; private set; } = Task.CompletedTask;

    private void LoadCover(string path) => CoverTask = _covers.LoadAsync(path, ShowCover);

    /// <summary>
    /// Szerokość, do której dekodowana jest okładka. Płyta ma kilkaset pikseli średnicy także
    /// przy skalowaniu ekranu 2×; okładki osadzone w plikach miewają 3000 px i więcej.
    /// </summary>
    private const int CoverDecodeWidth = 640;

    /// <summary>
    /// Obraz do pokazania, próbka pikseli do palety i rozmyta wersja pod scenę; null, gdy okładki
    /// nie ma albo jest uszkodzona.
    /// </summary>
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

            Bitmap? backdrop = null;
            try
            {
                backdrop = CoverBlur.FromEncoded(bytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cewka] rozmycie okładki: {ex.Message}");
            }

            return new DecodedCover(display, pixels, backdrop);
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

    /// <summary>
    /// Piksele, z których liczą się plamy sceny: próbka okładki, przy jej braku piksele rozmytej
    /// okładki, a przy okładce domyślnej jej ciemna wersja zmniejszona do rozmycia.
    /// </summary>
    private byte[]? _scenePixels;

    /// <summary>Pokazuje zdekodowaną okładkę; testy podają ją wprost, bez pliku.</summary>
    internal void ShowCover(DecodedCover? cover)
    {
        // Duża okładka poprzedniej okładki jest nieaktualna: odpinana bez powiadomienia, jedno
        // powiadomienie o obrazie sceny wysyła ReplaceCover.
        _largeCovers?.Cancel();
        _largePath = null;
        DropLargeCover(delayed: true, raise: false);

        if (cover is not null)
        {
            _usingPlaceholderCover = false;
            _coverPixels = cover.Pixels;
            _scenePixels = cover.Pixels ?? BackdropPixels(cover.Backdrop);
            ReplaceCover(cover.Display, cover.Display, cover.Backdrop);
        }
        else
        {
            _usingPlaceholderCover = true;
            _coverPixels = null;
            ShowPlaceholder();
        }

        UpdateLargeCover();
    }

    /// <summary>
    /// Podmienia obrazy na płycie okna i sceny oraz rozmytą okładkę sceny, przelicza barwy i zwalnia
    /// poprzednie obrazy. Płyta sceny ma zwykle tę samą bitmapę co okno; zwalnia się ją raz.
    /// </summary>
    private void ReplaceCover(Bitmap? next, Bitmap? sceneCover, Bitmap? backdrop)
    {
        var previous = _cover;
        var previousScene = _sceneCover;
        var previousBackdrop = _sceneBackdrop;

        Cover = next;
        SceneCover = sceneCover;
        SceneBackdrop = backdrop;
        Raise(nameof(SceneCoverArt));
        RefreshPalettes();

        if (!ReferenceEquals(previous, next) && !ReferenceEquals(previous, sceneCover)) ReleaseSceneImage(previous);
        if (!ReferenceEquals(previousScene, previous) && !ReferenceEquals(previousScene, next)
            && !ReferenceEquals(previousScene, sceneCover)) ReleaseSceneImage(previousScene);
        if (!ReferenceEquals(previousBackdrop, backdrop)) previousBackdrop?.Dispose();
    }

    /// <summary>
    /// Zwalnia obraz zdjęty ze sceny. W wizualizacji Okładka poprzedni obraz jeszcze znika w przenikaniu
    /// i warstwa może go przerysować, więc zwolnienie jest odroczone jak przy dużej okładce.
    /// </summary>
    private void ReleaseSceneImage(Bitmap? image)
    {
        if (image is null) return;

        if (_isSceneOpen && !_disposed && SceneVisual == SceneVisual.Cover)
            DispatcherTimer.RunOnce(image.Dispose, TimeSpan.FromSeconds(2));
        else
            image.Dispose();
    }

    /// <summary>
    /// Piksele rozmytej okładki, gdy próbki do palety nie dało się pobrać; rozmycie nie zmienia
    /// barw dominujących, a bitmapa ma tylko kilka tysięcy pikseli.
    /// </summary>
    private static byte[]? BackdropPixels(Bitmap? backdrop)
    {
        if (backdrop is null) return null;

        try
        {
            return CoverPalette.ReadPixels(backdrop);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] piksele rozmytej okładki: {ex.Message}");
            return null;
        }
    }

    /// <summary>Okładka domyślna na płycie okna, jej ciemna wersja na płycie sceny i rozmyta pod sceną.</summary>
    private void ShowPlaceholder()
    {
        var cover = LoadPlaceholder();
        var sceneCover = ScenePlaceholderCover(cover);
        var backdrop = ScenePlaceholder(sceneCover, out _scenePixels);
        ReplaceCover(cover, sceneCover, backdrop);
    }

    /// <summary>
    /// Płyta i tło sceny od nowa przy tej samej okładce domyślnej okna, po otwarciu albo zamknięciu
    /// sceny. Okładka okna się nie zmienia, więc jego barwy i akcent zostają; liczą się tylko plamy sceny.
    /// </summary>
    private void ShowScenePlaceholder()
    {
        var previousScene = _sceneCover;
        var previousBackdrop = _sceneBackdrop;

        var sceneCover = ScenePlaceholderCover(_cover);
        var backdrop = ScenePlaceholder(sceneCover, out _scenePixels);
        SceneCover = sceneCover;
        SceneBackdrop = backdrop;
        Raise(nameof(SceneCoverArt));
        SceneBlobs = CoverPalette.SceneBlobs(_radioMode ? _radioPixels : _scenePixels);

        if (!ReferenceEquals(previousScene, _cover) && !ReferenceEquals(previousScene, sceneCover)) ReleaseSceneImage(previousScene);
        if (!ReferenceEquals(previousBackdrop, backdrop)) previousBackdrop?.Dispose();
    }

    /// <summary>
    /// Podmienia okładkę domyślną po zmianie palety; prawdziwa okładka z motywem się nie zmienia.
    /// </summary>
    public void OnThemeChanged()
    {
        if (_usingPlaceholderCover) ShowPlaceholder();
        else RefreshPalettes();
    }

    /// <summary>Barwy tła okna i sceny z bieżącej okładki.</summary>
    private void RefreshPalettes()
    {
        Palette = ExtractPalette(App.Theme.IsDark);
        SceneBlobs = CoverPalette.SceneBlobs(_radioMode ? _radioPixels : _scenePixels);
    }

    /// <summary>
    /// Pulls the background colours out of whatever is currently on the record, at the strength
    /// the user chose.
    /// </summary>
    private Color[] ExtractPalette(bool dark)
    {
        // Akcent liczony jest z palety pobranej w pełnej sile, a nie z tej, którą przyciemniło
        // ustawienie intensywności. Intensywność dotyczy tła; przełączniki i suwaki mają
        // pokazywać barwę okładki zawsze i w całości.
        ApplyAccent(VividPalette(dark), dark);

        return BackdropColours(dark);
    }

    /// <summary>Próbka pikseli logo bieżącej stacji; null, gdy logo nie ma.</summary>
    private byte[]? _radioPixels;

    private void SampleRadioLogo() => _radioPixels = Radio.StageLogo is { } logo ? CoverPalette.SampleImage(logo) : null;

    /// <summary>
    /// Barwy tła w trybie radia: z logo bieżącej stacji; stacja bez logo (albo z wyłączonym pobieraniem)
    /// ma paletę domyślną, nigdy barwy poprzedniej stacji ani okładki pliku.
    /// </summary>
    private Color[] RadioColours(bool dark, double saturation) =>
        _radioPixels is { } pixels ? CoverPalette.FromPixels(pixels, dark, saturation) : CoverPalette.Extract(null, dark, saturation);

    private Color[] VividPalette(bool dark) =>
        _radioMode ? RadioColours(dark, 1.0) :
        _coverPixels is { } pixels ? CoverPalette.FromPixels(pixels, dark, 1.0) : CoverPalette.Extract(Cover, dark, 1.0);

    /// <summary>Barwy plam tła dla motywu jasnego albo ciemnego, bez ruszania akcentu.</summary>
    private Color[] BackdropColours(bool dark)
    {
        var saturation = ColourPreferences.Saturation(_settings.Current.ColourIntensity);

        if (_radioMode) return RadioColours(dark, saturation);

        if (_coverPixels is { } pixels) return CoverPalette.FromPixels(pixels, dark, saturation);

        // Przy okładce domyślnej barwy pary są znane dokładnie, więc nie ma po co odczytywać ich
        // z narysowanego obrazka.
        if (_usingPlaceholderCover) return PlaceholderBackdrop(dark);

        return CoverPalette.Extract(Cover, dark, saturation);
    }

    /// <summary>
    /// Plamy tła dla okładki domyślnej: obie skrajne barwy pary po dwa razy, z nasyceniem
    /// skalowanym intensywnością barw.
    ///
    /// <para>Pułapka: analiza narysowanej spirali przez <see cref="CoverPalette"/> psuje barwy,
    /// bo cienkie kreski giną w ciemnym tle przegródek, a uśrednianie ciągnie je ku szarości.</para>
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
    /// Scena akcentu nie używa: jej jedyną barwą stanu włączonego jest biel.
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
        resources["FocusRingBrush"] = new SolidColorBrush(Contrast.FocusRing(accent, darkTheme));
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

        RefreshPalettes();
        Raise(nameof(BackdropMinimum));
        Raise(nameof(BackdropMaximum));
    }

    /// <summary>
    /// Stosuje nową parę barw okładki domyślnej; widać ją tylko przy pliku bez okładki, ale
    /// razem z nią zmienia się tło okna.
    /// </summary>
    public void SetPlaceholderPalette(PlaceholderPalette palette)
    {
        if (_settings.Current.PlaceholderPalette == palette) return;

        _settings.Current.PlaceholderPalette = palette;
        _settings.Touch();

        // Wybranie losowania ma dać skutek od razu, a nie dopiero przy następnym utworze.
        _placeholderDraw.Forget();

        if (_usingPlaceholderCover) ShowPlaceholder();
    }

    private readonly PlaceholderDraw _placeholderDraw = new();

    /// <summary>
    /// Okładka domyślna: cewka.
    ///
    /// <para>Pułapka: para barw idzie przez <see cref="PlaceholderDraw"/>, bo przy wyborze
    /// losowym ma być losowana raz na utwór, a ta metoda działa też po przewinięciu i zmianie
    /// motywu.</para>
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
    /// Okładka domyślna na płycie sceny. Scena jest zawsze ciemna, więc w jasnym motywie spirala
    /// rysuje się drugi raz, w wersji ciemnej, z tą samą parą barw co na płycie okna. Ten wariant
    /// powstaje tylko przy otwartej scenie: zamknięta nie ma w jasnym oknie ani płyty, ani tła.
    /// </summary>
    private Bitmap? ScenePlaceholderCover(Bitmap? cover)
    {
        if (App.Theme.IsDark && cover is not null) return cover;
        if (!_isSceneOpen) return null;

        try
        {
            return CoilCover.Render(_placeholderDraw.For(_settings.Current.PlaceholderPalette, _current?.Path), darkTheme: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] ciemna okładka domyślna: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rozmyta okładka domyślna pod sceną i piksele do barw plam, z tego samego obrazu co płyta
    /// sceny, więc scena wygląda tak samo w każdym motywie okna.
    /// </summary>
    private static Bitmap? ScenePlaceholder(Bitmap? sceneCover, out byte[]? pixels)
    {
        pixels = null;
        if (sceneCover is null) return null;

        try
        {
            pixels = CoverBlur.Rasterise(sceneCover, out var size);
            return CoverBlur.FromPixels(pixels, size);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] rozmycie okładki domyślnej: {ex.Message}");
            pixels = null;
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
        _largeCovers?.Cancel();
        DropLargeCover(delayed: false, raise: false);
        _nextUpCovers?.Cancel();
        _nextUpCover?.Dispose();
        _nextUpCover = null;
        Radio.Dispose();
        _engine.Dispose();
        _loudness.Measured -= OnLoudnessMeasured;
        _loudness.Dispose();
        _cover?.Dispose();
        if (!ReferenceEquals(_sceneCover, _cover)) _sceneCover?.Dispose();
        _sceneBackdrop?.Dispose();
    }
}
