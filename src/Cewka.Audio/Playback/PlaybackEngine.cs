using System.Diagnostics;
using Cewka.Audio.Decoding;
using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Devices;
using Cewka.Audio.Dsp;
using Cewka.Audio.Streaming;
using StreamState = Cewka.Audio.Streaming.LiveState;

namespace Cewka.Audio.Playback;

/// <summary>
/// Steruje odtwarzaniem: ma urządzenie, wątek dekodujący i kolejkę.
///
/// <para>Wątek dekodujący wyprzedza odtwarzanie o pół sekundy i napełnia bufor pierścieniowy,
/// z którego czyta wywołanie zwrotne urządzenia. Granice utworów są znacznikami pozycji w tym
/// samym buforze, więc przejście do następnego utworu jest bez przerwy.</para>
///
/// <para>Pułapka: wątek dźwiękowy nie alokuje, nie czeka na blokady i nie zgłasza zdarzeń;
/// przejście przez granicę sygnalizuje flagą, którą odbiera wątek dekodujący.</para>
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    /// <summary>Half a second of lookahead: enough to open the next file, short enough that seeking feels immediate.</summary>
    private const double RingSeconds = 0.5;

    /// <summary>Frames decoded per pass of the feeder loop.</summary>
    private const int DecodeChunkFrames = 2048;

    /// <summary>Wyciszenie przy przewinięciu, pauzie i zatrzymaniu, żeby nie trzasnęło.</summary>
    private const double FadeMilliseconds = 6;

    /// <summary>
    /// Cisza przepuszczana przez tor po wyciszeniu. Musi przekraczać opóźnienie limitera
    /// (5 ms), inaczej jego linia opóźniająca wypuściłaby stary dźwięk po wyciszeniu.
    /// </summary>
    private const double TailMilliseconds = 8;

    /// <summary>Najdłuższe czekanie na opróżnienie bufora przez wątek dźwiękowy.</summary>
    private const int FlushTimeoutMilliseconds = 500;

    /// <summary>Najdłuższe czekanie na wyciszenie przed pauzą; woła je wątek interfejsu.</summary>
    private const int PauseFadeTimeoutMilliseconds = 250;

    /// <summary>Powrót do pełnego poziomu przerwanej miękkiej pauzy przy nowym wyborze: bez skoku, ale od razu.</summary>
    private const double ShortRampSeconds = 0.02;

    /// <summary>Zapas na ostatni okres urządzenia po miękkim wyciszeniu, zanim pauza zatrzyma wyjście.</summary>
    private static readonly TimeSpan SoftPauseMargin = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// Cisza oddawana po wyciszeniu, zanim wolno zatrzymać urządzenie. Wyrenderowane próbki
    /// czekają jeszcze w buforze sprzętowym, a zatrzymanie go ucina — z trzaskiem.
    /// </summary>
    private const int DrainMilliseconds = 40;

    /// <summary>Przerwa, gdy stacja chwilowo nie ma danych; zatrzymanie nie może na nią długo czekać.</summary>
    private const int LiveWaitMilliseconds = 5;

    private int _drainLength = 1;
    private int _pauseDrainPosition;
    private bool _pauseDraining;

    private readonly object _sync = new();
    private readonly List<QueueEntry> _queue = [];
    private readonly ShuffleOrder _shuffleOrder;
    private readonly Func<string?, int, IAudioOutput> _openOutput;

    private IAudioOutput? _device;
    private FeederSession? _session;
    private Thread? _feeder;
    private bool _shuffle;

    // Uzgodnienie przewinięcia między wątkiem sterującym, dekodującym i dźwiękowym. Cel
    // zostaje ważny także w trakcie wykonywania (FeederSession.SeekInProgress), aż do SetCurrent.
    private volatile bool _seekPending;
    private QueueEntry? _seekTargetEntry;
    private long _seekTargetFrame;

    /// <summary>Przewinięcie do niczego kończy kolejkę, a nie tylko zatrzymuje; pod blokadą kolejki.</summary>
    private bool _endAfterSeek;

    /// <summary>
    /// Generacja, w której wątek dekodujący przejął zlecony koniec kolejki, a zgłoszenie końca
    /// jeszcze nie przyszło; pod blokadą kolejki.
    /// </summary>
    private int? _endQueuedGeneration;

    /// <summary>Rośnie z każdym żądaniem przewinięcia; nieaktualny sygnał końca kolejki go nie zgadza.</summary>
    private int _generation;

    /// <summary>Kolejka dobiegła końca; następne Play zaczyna od początku.</summary>
    private bool _ended;

    // Stan wątku dźwiękowego. Pola volatile ustawiają inne wątki, resztę zmienia tylko on.
    private volatile bool _pauseFadeRequested;

    /// <summary>Miękka pauza wycisza dźwięk, a urządzenie zatrzyma się po wyciszeniu.</summary>
    private bool _softPausePending;
    private int _softPauseGeneration;
    private volatile bool _pauseFadeDone;
    private volatile bool _startFadeIn;
    private volatile bool _resetChainPending;
    private volatile bool _snapGainPending;
    // Wzmocnienie dla przeskoku, opakowane: para wartości ma dotrzeć do wątku dźwiękowego w całości.
    private object? _pendingGain;
    private bool _fadeOutActive;
    private int _fadeOutPosition;
    private int _fadeLength = 1;
    private int _tailLength = 1;
    private int _fadeCounter;
    private bool _fadingIn;

    private volatile TrackBoundary _current = TrackBoundary.None;

    // Stacja: pod blokadą kolejki. Wybrana zostaje także w pauzie i po błędzie, a wpis i sesja
    // istnieją tylko w czasie połączenia.
    private LiveSource? _liveSource;
    private QueueEntry? _liveEntry;
    private StreamState _liveRestState = StreamState.Stopped;
    private StreamState? _liveReportedState;
    private string? _liveTitle;

    // Zdarzenia stacji: jedna kolejka, żeby wolny odbiorca nie zamieniał ich kolejności. Epoka to
    // jedno połączenie; zgłoszenie z minionej przepada, gdy przyjdzie na nie kolej.
    private readonly Queue<(int Epoch, Action Raise)> _liveEvents = new();
    private bool _liveEventsRunning;
    private int _liveEpoch;

    /// <summary>
    /// Miejsce pliku sprzed stacji; po wyłączeniu stacji wraca jak w wieży. Wpis usunięty z kolejki
    /// nie jest miejscem powrotu, dopóki cofnięcie usunięcia go nie przywróci.
    /// </summary>
    private LiveBookmark? _bookmark;

    // Głośność stacji: pod blokadą kolejki. Znany poziom żyje tylko w pamięci, do końca działania.
    private readonly Dictionary<string, TrackGain> _liveGains = new(StringComparer.Ordinal);
    private TrackGain? _liveGain;
    private LiveLoudnessTracker? _liveTracker;
    private string? _liveTrackerStation;
    private int _liveTrackerRate;
    private int _liveTrackerChannels;

    public PlaybackEngine()
        : this(static (name, period) => AudioDevice.Open(name, 0, 2, period))
    {
    }

    /// <summary>Dla testów: wyjście dźwięku i źródło losowości podane z zewnątrz.</summary>
    internal PlaybackEngine(Func<string?, int, IAudioOutput> openOutput, Random? random = null)
    {
        _openOutput = openOutput;
        _shuffleOrder = new ShuffleOrder(random);
        OpenLiveSession = OpenDefaultLiveSession;
    }

    /// <summary>Otwieranie dekodera; testy podstawiają tu dekoder syntetyczny.</summary>
    internal Func<string, int, int, IAudioDecoder> OpenDecoder { get; set; } = DecoderFactory.Open;

    /// <summary>Otwieranie sesji stacji; testy podstawiają atrapę.</summary>
    internal Func<LiveSource, ILiveSession> OpenLiveSession { get; set; }

    /// <summary>
    /// Blokada adresów stacji, sprawdzana dla adresu początkowego i każdego przekierowania; zwraca
    /// przyczynę odmowy albo null. Ustawia aplikacja (lista blokad); ważna dla następnych otwarć.
    /// </summary>
    public Func<Uri, LiveFailureReason?>? LiveUrlGuard { get; set; }

    private ILiveSession OpenDefaultLiveSession(LiveSource source) =>
        new LiveSession(source, LiveUrlGuard is { } guard ? new LiveOptions { UrlGuard = guard } : new LiveOptions());

    /// <summary>Otwieranie dekodera stacji; testy podstawiają atrapę.</summary>
    internal Func<ILiveByteSource, int, int, ILiveDecoder> OpenLiveDecoder { get; set; } =
        static (bytes, rate, channels) => new LiveStreamDecoder(bytes, rate, channels);

    /// <summary>Ramki czekające w buforze; testy dozują nim tempo wątku dźwiękowego.</summary>
    internal int BufferedFrames => _session?.Ring.FramesAvailable ?? 0;

    /// <summary>Dekoder zapisał koniec kolejki, a odtwarzanie jeszcze go nie minęło.</summary>
    internal bool EndOfQueueBuffered
    {
        get
        {
            var session = _session;
            if (session is null) return false;
            lock (session.BoundaryLock) return session.Boundaries.Any(boundary => boundary.IsEnd);
        }
    }

    /// <summary>True, dopóki zlecone przewinięcie nie zostało wykonane.</summary>
    internal bool IsSeeking => _seekPending || SeekInProgress;

    /// <summary>Wątek dekodujący odebrał przewinięcie i czeka na wyciszenie bufora.</summary>
    internal bool FlushingForSeek => _session?.FlushRequested ?? false;

    /// <summary>
    /// Wątek dekodujący odebrał przewinięcie, ale nowe położenie jeszcze nie obowiązuje.
    /// Otwarcie pliku z wolnego dysku trwa na tyle długo, że interfejs zdążyłby w tym czasie
    /// odczytać stare położenie i cofnąć wskaźnik.
    /// </summary>
    private bool SeekInProgress => _session?.SeekInProgress ?? false;

    /// <summary>Raised off the audio thread when playback moves to another queue entry.</summary>
    public event Action<int>? TrackChanged;

    /// <summary>Raised when the queue runs out and playback stops on its own.</summary>
    public event Action? QueueFinished;

    /// <summary>Raised when a track could not be opened or played; the first argument is the queue index.</summary>
    public event Action<int, TrackFailure>? TrackFailed;

    /// <summary>
    /// Urządzenie wyjściowe przestało pracować samo, na przykład odłączono słuchawki.
    /// Odtwarzanie przechodzi wtedy w pauzę; następne Play otwiera urządzenie od nowa.
    /// Zgłaszane z puli wątków.
    /// </summary>
    public event Action? DeviceLost;

    /// <summary>Pierwszy dźwięk stacji minął wyjście. Zgłaszane z puli wątków po kolei, jak pozostałe zdarzenia stacji.</summary>
    public event Action<LiveSource>? LiveStarted;

    public event Action<LiveSource, LiveState>? LiveStateChanged;

    /// <summary>Tytuł obowiązuje od dźwięku, który właśnie minął wyjście.</summary>
    public event Action<LiveSource, string?>? LiveTitleChanged;

    /// <summary>Stacja przestała grać z przyczyny podanej kodem; zgłaszane, gdy bufor wybrzmiał.</summary>
    public event Action<LiveSource, LiveFailure>? LiveFailed;

    public event Action<LiveSource, LiveStreamInfo>? LiveInfoChanged;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;

    /// <summary>Stacja wybrana, także w pauzie i po błędzie.</summary>
    public bool IsLive
    {
        get { lock (_sync) return _liveSource is not null; }
    }

    public LiveSource? CurrentLive
    {
        get { lock (_sync) return _liveSource; }
    }

    /// <summary>
    /// Miejsce pliku, do którego wraca się po stacji (numer w kolejce i pozycja w sekundach); null, gdy nie gra
    /// stacja albo plik nie zaczął grać. W czasie stacji tylko to jest pozycją pliku, bo <see cref="Position"/>
    /// opisuje strumień.
    /// </summary>
    public (int Index, double Seconds)? FilePlaceBehindLive
    {
        get
        {
            lock (_sync)
            {
                if (_liveSource is null || _bookmark is not { } place) return null;
                var index = _queue.IndexOf(place.Entry);
                return index < 0 ? null : (index, place.Position.TotalSeconds);
            }
        }
    }

    /// <summary>Ostatni zgłoszony stan: porażka dopiero po wybrzmieniu bufora. Null, gdy nie wybrano stacji.</summary>
    public LiveState? LiveState
    {
        get
        {
            lock (_sync)
            {
                if (_liveSource is null) return null;
                return _liveEntry is null ? _liveRestState : _liveReportedState ?? StreamState.Connecting;
            }
        }
    }

    /// <summary>Tytuł tego, co słychać; null, gdy stacja go nie podaje.</summary>
    public string? LiveTitle
    {
        get { lock (_sync) return _liveTitle; }
    }

    public LiveStreamInfo? LiveInfo
    {
        get { lock (_sync) return _liveEntry?.Live?.Session.Info; }
    }

    /// <summary>Kodek z dekodera, nie z nagłówków; przepływność podaje <see cref="InstantaneousBitrate"/>.</summary>
    public LiveCodec LiveCodec => _session?.Decoder is ILiveDecoder decoder ? decoder.Codec : LiveCodec.Unknown;

    /// <summary>
    /// Tryb powtarzania. Zmiana w ostatnim pół sekundy utworu sprawdza następny, który dekoder
    /// już otworzył, tak jak zmiana kolejki.
    /// </summary>
    public RepeatMode Repeat
    {
        get { lock (_sync) return _repeat; }
        set
        {
            lock (_sync)
            {
                if (_repeat == value) return;
                _repeat = value;
                _orderVersion++;
                RevalidatePrefetchLocked();
            }
        }
    }

    private RepeatMode _repeat = RepeatMode.None;

    /// <summary>
    /// Odtwarzanie losowe. Włączenie układa nową kolejność z bieżącym utworem na początku.
    /// Następny otwarty już przez dekoder jest sprawdzany jak przy zmianie kolejki.
    /// </summary>
    public bool Shuffle
    {
        get { lock (_sync) return _shuffle; }
        set
        {
            lock (_sync)
            {
                if (_shuffle == value) return;
                _shuffle = value;
                _orderVersion++;
                if (value) _shuffleOrder.Reset(_queue, OrderAnchorLocked());
                RevalidatePrefetchLocked();
            }
        }
    }

    /// <summary>Signal chain applied on the audio thread. Assigned once, before playback.</summary>
    public AudioGraph Graph { get; } = new();

    public IReadOnlyList<QueueEntry> Queue
    {
        get { lock (_sync) return _queue.ToArray(); }
    }

    /// <summary>
    /// Numer wpisu, który gra albo zaraz zagra po zleconym przewinięciu; −1, gdy nic.
    /// Wyznaczany z tożsamości wpisu przy odczycie, więc zmiany kolejki go nie rozjeżdżają.
    /// </summary>
    public int CurrentIndex
    {
        get
        {
            lock (_sync)
            {
                var entry = AudibleOrPendingEntry();
                return entry is null ? -1 : _queue.IndexOf(entry);
            }
        }
    }

    /// <summary>
    /// Numer wpisu, który zagra po bieżącym samoczynnie, liczony tak samo jak faktyczne przejście
    /// (kolejność losowa, powtarzanie kolejki, pomijanie wpisów nie do otwarcia). −1, gdy nic nie
    /// zagra: koniec kolejki, powtarzanie utworu albo jedyny wpis, źródło na żywo, koniec odtwarzania.
    /// Sam odczyt niczego nie przesuwa: na końcu rundy losowania tylko planuje następną.
    /// </summary>
    public int NextIndex
    {
        get
        {
            lock (_sync)
            {
                var current = AudibleOrPendingEntry();
                if (current is null || current.IsLive || _liveSource is not null || _ended
                    || _repeat == RepeatMode.Track || !_queue.Contains(current))
                    return -1;

                var next = _shuffle && _queue.Count > 1
                    ? _shuffleOrder.Preview(current, _repeat == RepeatMode.Queue, _queue,
                        entry => entry is not ({ IsUnsupported: true } or { OpenFailure: not null }))
                    : NextPlayableLocked(current);
                return next is null || ReferenceEquals(next, current) ? -1 : _queue.IndexOf(next);
            }
        }
    }

    /// <summary>
    /// Kolejność odtwarzania do pokazania: przy losowaniu permutacja, inaczej kolejka, razem z miejscem
    /// bieżącego wpisu, odczytane naraz. Tylko odczyt: bez losowania i bez zmiany kolejności, więc
    /// na końcu rundy przy powtarzaniu kolejki pokazuje bieżącą rundę, dopóki przejście nie zacznie
    /// następnej. Poza tym wpis po bieżącym jest tym, który zagra;
    /// wpisy nie do otwarcia zostają na swoich miejscach, choć silnik je pomija.
    /// </summary>
    public PlaybackOrderSnapshot PlaybackOrder
    {
        get
        {
            lock (_sync)
            {
                var current = _liveSource is null ? AudibleOrPendingEntry() : null;
                var entries = _shuffle && _queue.Count > 1 && _shuffleOrder.Order.Count == _queue.Count
                    ? _shuffleOrder.Snapshot(current, _repeat == RepeatMode.Queue)
                    : _queue.ToArray();

                return new PlaybackOrderSnapshot(entries, current is null ? -1 : Array.IndexOf(entries, current),
                    _orderVersion + _shuffleOrder.Version);
            }
        }
    }

    /// <summary>Rośnie przy każdej zmianie kolejki, kolejności i trybów; tani odczyt do sprawdzania migawki.</summary>
    public long PlaybackOrderVersion
    {
        get { lock (_sync) return _orderVersion + _shuffleOrder.Version; }
    }

    private long _orderVersion;

    /// <summary>
    /// Ponownie odczytuje wzmocnienie wyrównania dla słyszanego utworu. Potrzebne, gdy zmieni
    /// się samo ustawienie: bez tego zmiana byłaby słyszalna dopiero od następnego utworu.
    /// </summary>
    public void RefreshTrackGain()
    {
        var entry = _current.Entry;
        if (entry is null) return;

        if (entry.IsLive)
        {
            TrackGain? gain;
            lock (_sync) gain = _liveGain;
            Graph.ApplyTrackGain(gain, snap: false);
            return;
        }

        Graph.OnTrackChanged(entry);
    }

    public int SampleRate { get; private set; } = 48000;

    public int Channels { get; private set; } = 2;

    public string DeviceName => _device?.Name ?? "—";

    /// <summary>
    /// False until something is played. The device is opened on first use rather than at
    /// start-up, so that a player sitting idle does not hold the sound card.
    /// </summary>
    public bool IsDeviceOpen => _device is not null;

    /// <summary>True, gdy wybranego urządzenia nie znaleziono i gra urządzenie domyślne.</summary>
    public bool UsingFallbackDevice => _device?.IsFallback ?? false;

    /// <summary>
    /// Bitrate of the passage being decoded, in kilobits per second; 0 when not measured.
    /// Read from the decode thread's decoder, which is safe for a single integer.
    /// </summary>
    public int InstantaneousBitrate => _session?.Decoder?.InstantaneousBitrate ?? 0;

    /// <summary>Position within the current track, derived from frames actually delivered.</summary>
    public TimeSpan Position
    {
        get
        {
            // Między zażądaniem przewinięcia a jego wykonaniem bufor opisuje jeszcze stare
            // położenie; zwracanie go cofnęłoby wskaźnik na ułamek sekundy.
            if (IsSeeking)
            {
                var target = Interlocked.Read(ref _seekTargetFrame);
                return TimeSpan.FromSeconds(target / (double)SampleRate);
            }

            var current = _current;
            var ring = _session?.Ring;
            if (ring is null || current.Entry is null) return TimeSpan.Zero;

            var frames = ring.TotalRead - current.AbsoluteFrame;
            if (frames < 0) frames = 0;

            // Granica leży w utworze tam, gdzie zaczęło się odtwarzanie: po przewinięciu
            // w celu przewinięcia, nie na początku.
            return TimeSpan.FromSeconds((frames + current.StartFrame) / (double)SampleRate);
        }
    }

    public TimeSpan Duration => TimeSpan.FromSeconds(_current.TotalFrames / (double)Math.Max(1, SampleRate));

    // ================= konfiguracja =================

    /// <summary>
    /// Nazwa urządzenia, które otworzy następne <see cref="Initialise"/>; null oznacza
    /// domyślne. Numer na liście wyznacza się z nazwy dopiero przy otwarciu.
    /// </summary>
    public string? PreferredDeviceName { get; set; }

    /// <summary>
    /// Żądany rozmiar okresu w ramkach; zero zostawia wybór miniaudio. Wartość jest wyłącznie
    /// podpowiedzią — przyjęty rozmiar podaje <see cref="PeriodSizeInFrames"/>.
    /// </summary>
    public int PreferredPeriodSize { get; set; }

    /// <summary>Rozmiar okresu, jaki otwarte urządzenie faktycznie przyjęło; zero, gdy zamknięte.</summary>
    public int PeriodSizeInFrames => _device?.PeriodSizeInFrames ?? 0;

    /// <summary>Opens the output device and starts the decode thread. Idempotent.</summary>
    public void Initialise()
    {
        lock (_sync)
        {
            if (_device is not null) return;

            var device = _openOutput(PreferredDeviceName, PreferredPeriodSize);

            // Cel przewinięcia zaplanowany przy poprzednim urządzeniu jest w jego ramkach.
            if (_seekPending && device.SampleRate != SampleRate)
            {
                var frame = Interlocked.Read(ref _seekTargetFrame);
                Interlocked.Exchange(ref _seekTargetFrame, (long)(frame * (double)device.SampleRate / SampleRate));
            }

            SampleRate = device.SampleRate;
            Channels = device.Channels;

            var session = new FeederSession(new PcmRingBuffer((int)(SampleRate * RingSeconds), Channels));

            _fadeLength = Math.Max(1, (int)(SampleRate * FadeMilliseconds / 1000));
            _tailLength = Math.Max(1, (int)(SampleRate * TailMilliseconds / 1000));
            _drainLength = Math.Max(device.PeriodSizeInFrames * 3, SampleRate * DrainMilliseconds / 1000);
            ResetAudioThreadState();

            Graph.Prepare(SampleRate, Channels);

            // Dwa okresy: ten, który właśnie gra, i ten w kolejce urządzenia.
            Graph.Analyser.OutputLatencyFrames = device.PeriodSizeInFrames * 2;

            device.Lost += () => OnDeviceLost(device);
            device.Render = buffer => Render(session, buffer);

            _device = device;
            _session = session;

            _feeder = new Thread(() => FeederLoop(session))
            {
                IsBackground = true,
                Name = "Cewka.Decode",
                // Above normal, but not realtime: starving the rest of the process to
                // decode audio would be a poor trade.
                Priority = ThreadPriority.AboveNormal,
            };
            _feeder.Start();
        }
    }

    /// <summary>
    /// Przenosi odtwarzanie na inne urządzenie. Tor buduje się od nowa, bo od częstotliwości
    /// i liczby kanałów zależą bufor, filtry i pozycja w ramkach; utwór wraca na swoje miejsce,
    /// a wstrzymany pozostaje wstrzymany.
    /// </summary>
    public void SwitchDevice(string? deviceName)
    {
        PreferredDeviceName = string.IsNullOrEmpty(deviceName) ? null : deviceName;
        Reopen();
    }

    /// <summary>
    /// Otwiera urządzenie na nowo z bieżącymi ustawieniami, zachowując utwór i pozycję.
    /// Rozmiar okresu podaje się przy tworzeniu urządzenia i nie da się go zmienić później,
    /// więc zmiana opóźnienia przechodzi tą samą drogą co zmiana urządzenia.
    /// </summary>
    public void Reopen()
    {
        lock (_sync)
        {
            // Nic jeszcze nie zostało otwarte: wystarczy zapamiętać wybór.
            if (_device is null) return;
        }

        var closed = TearDown(keepPlace: true);
        Initialise();

        if (closed is { WasPlaying: true }) Play();
    }

    /// <summary>
    /// Zatrzymuje wątek dekodujący i zamyka urządzenie, zostawiając kolejkę.
    /// Z <paramref name="keepPlace"/> utwór i pozycja zostają jako przewinięcie, które wykona
    /// następna sesja, a stan przechodzi w pauzę; koniec kolejki zostaje końcem.
    ///
    /// <para>Pułapka: wątek dekodujący może utknąć w odczycie pliku z dysku sieciowego dłużej,
    /// niż trwa czekanie. Wtedy nie wolno zamknąć jego dekodera z zewnątrz, bo zwolniłoby to
    /// pamięć w trakcie odczytu natywnego; stary wątek dostaje sygnał końca i sam zamyka swój
    /// dekoder, a nowa sesja ma własny bufor, granice i flagi.</para>
    /// </summary>
    /// <param name="expected">Zamyka tylko wtedy, gdy bieżącym urządzeniem jest to.</param>
    /// <returns>Stan sprzed zamknięcia; null, gdy nic nie zamknięto.</returns>
    private ClosedState? TearDown(bool keepPlace, IAudioOutput? expected = null)
    {
        Thread? feeder;
        FeederSession? session;
        IAudioOutput? device;
        ClosedState closed;
        bool finishes;
        ILiveSession? liveSession = null;

        lock (_sync)
        {
            if (expected is not null && !ReferenceEquals(expected, _device)) return null;

            feeder = _feeder;
            session = _session;
            device = _device;

            // Odłączenie jeszcze pod blokadą: sterownik może sam uruchomić utracone urządzenie,
            // a Initialise nowej sesji przygotowuje ten sam tor. Stop czeka na trwający okres,
            // więc dalej słyszany wpis, pozycja i poprzedni wpis już się nie zmieniają.
            if (device is not null)
            {
                device.Render = null;
                try { device.Stop(); } catch (AudioException) { }
            }

            var entry = AudibleOrPendingEntry();
            var frame = FramesFrom(Position);

            // Zlecony koniec kolejki, którego wątek dekodujący jeszcze nie wykonał albo nie zgłosił,
            // też jest końcem.
            var ended = _ended || _seekPending && _endAfterSeek || _endQueuedGeneration == _generation;

            // Słyszany wpis usunięty z kolejki gra jeszcze z bufora: miejscem jest wpis po kotwicy,
            // od początku, a gdy po niej nic nie ma, koniec kolejki (jak w RestartAfterHeardLocked).
            if (entry is { IsLive: false } && !_queue.Contains(entry)
                && session?.PreviousHeard is { } anchor && _queue.Contains(anchor))
            {
                entry = NextPlayableLocked(anchor, afterRemoved: true);
                frame = 0;
                ended |= entry is null;
            }

            closed = new ClosedState(State == PlaybackState.Playing && !ended);

            // Koniec kolejki ustalony dopiero tutaj zgłasza się raz, poza blokadą; zgłoszenie starej
            // sesji, które mogło już czekać w puli wątków, traci ważność razem z jej generacją.
            finishes = keepPlace && ended && !_ended;
            if (finishes) _generation++;

            _feeder = null;
            _session = null;
            _device = null;

            State = PlaybackState.Stopped;
            _seekTargetEntry = null;
            _seekPending = false;
            _endAfterSeek = false;
            _endQueuedGeneration = null;
            _ended = false;
            _pauseFadeRequested = false;
            _current = TrackBoundary.None;

            if (!keepPlace)
            {
                liveSession = ForgetLiveLocked();
            }
            else if (_liveSource is not null)
            {
                // Stacja nie ma miejsca do zachowania: następne Play łączy od nowa.
                liveSession = DisconnectLiveLocked();
                State = PlaybackState.Paused;
            }
            else
            {
                if (ended)
                {
                    _ended = true;
                    State = PlaybackState.Paused;
                }
                else if (entry is not null && _queue.Contains(entry))
                {
                    RequestSeekLocked(entry, frame);
                    State = PlaybackState.Paused;
                }
            }
        }

        if (session is not null) session.Cancelled = true;
        liveSession?.Stop();
        feeder?.Join(1000);
        device?.Dispose();

        if (finishes) ThreadPool.UnsafeQueueUserWorkItem(_ => QueueFinished?.Invoke(), null);

        return closed;
    }

    private readonly record struct ClosedState(bool WasPlaying);

    private readonly record struct LiveBookmark(QueueEntry Entry, TimeSpan Position);

    private void ResetAudioThreadState()
    {
        _pauseFadeDone = false;
        _startFadeIn = false;
        _resetChainPending = false;
        _snapGainPending = false;
        _fadeOutActive = false;
        _fadeOutPosition = 0;
        _pauseDraining = false;
        _pauseDrainPosition = 0;
        _fadeCounter = 0;
        _fadingIn = false;
    }

    // ================= kolejka =================

    /// <summary>Replaces the queue and begins at <paramref name="startIndex"/>.</summary>
    public void SetQueue(IEnumerable<string> paths, int startIndex = 0)
    {
        int count;
        lock (_sync)
        {
            _orderVersion++;
            _queue.Clear();
            foreach (var path in paths) _queue.Add(new QueueEntry { Path = path });
            count = _queue.Count;
            _bookmark = null;

            var first = count > 0 ? _queue[Math.Clamp(startIndex, 0, count - 1)] : null;
            _shuffleOrder.Reset(_queue, first);
        }

        if (count > 0) PlayIndex(Math.Clamp(startIndex, 0, count - 1));
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        lock (_sync)
        {
            var added = paths.Select(path => new QueueEntry { Path = path }).ToList();
            _orderVersion++;
            _queue.AddRange(added);
            _shuffleOrder.Insert(added, OrderAnchorLocked());

            RevalidatePrefetchLocked();
        }
    }

    /// <summary>
    /// Removes an entry. Removing the track that is playing stops it; removing any other
    /// leaves playback untouched, which is what makes tidying the queue mid-listen safe.
    /// </summary>
    public void RemoveAt(int index)
    {
        bool wasPlaying;

        lock (_sync)
        {
            if (index < 0 || index >= _queue.Count) return;

            var entry = _queue[index];
            _orderVersion++;
            _queue.RemoveAt(index);
            _shuffleOrder.Remove(entry);

            wasPlaying = ReferenceEquals(entry, AudibleOrPendingEntry());
            AfterRemovalChecked?.Invoke();
            if (!wasPlaying) RevalidatePrefetchLocked();
        }

        if (wasPlaying) Stop();
        else NotifyCurrentIndex();
    }

    /// <summary>
    /// Wstawia z powrotem wpis usunięty przez <see cref="RemoveAt"/>, czyli cofa usunięcie. To ten
    /// sam wpis, więc gdy jeszcze gra z bufora, odtwarzanie rozpoznaje go i nie zaczyna od nowa.
    /// </summary>
    public void Restore(QueueEntry entry, int index)
    {
        lock (_sync)
        {
            if (_queue.Contains(entry)) return;

            var anchor = OrderAnchorLocked();
            _orderVersion++;
            _queue.Insert(Math.Clamp(index, 0, _queue.Count), entry);
            _shuffleOrder.Insert([entry], anchor);

            // Słyszany wpis wraca tam, skąd go usunięto: tuż za kotwicą.
            if (ReferenceEquals(entry, _current.Entry)) _shuffleOrder.MoveAfter(entry, anchor);

            RevalidatePrefetchLocked();
        }

        NotifyCurrentIndex();
    }

    /// <summary>
    /// Wyłącznie dla testów: wołane w <see cref="RemoveAt"/> pod blokadą kolejki, po sprawdzeniu,
    /// czy usuwany wpis gra, np. żeby wątek dźwięku przekroczył w tej chwili granicę.
    /// </summary>
    internal Action? AfterRemovalChecked;

    /// <summary>Reorders the queue, used by dragging a row to a new position.</summary>
    public void Move(int from, int to)
    {
        lock (_sync)
        {
            if (from < 0 || from >= _queue.Count) return;
            if (to < 0 || to >= _queue.Count || from == to) return;

            var entry = _queue[from];
            _orderVersion++;
            _queue.RemoveAt(from);
            _queue.Insert(to, entry);

            RevalidatePrefetchLocked();
        }

        NotifyCurrentIndex();
    }

    /// <summary>
    /// W ostatnim pół sekundy utworu dekoder ma już otwarty następny albo zapisał koniec
    /// kolejki, a krótkie utwory bywają zapisane w całości, zanim zagrają. Zmiana kolejki
    /// sprawdza cały łańcuch od słyszanego wpisu przez zapisane do dekodowanego. Przy pierwszej
    /// niezgodności przewinięcie słyszanego utworu w miejscu opróżnia bufor i każe dekoderowi
    /// wybrać następny od nowa; za zapisanym końcem kolejki dekoder dopisuje nowy wpis bez przerwy.
    /// </summary>
    private void RevalidatePrefetchLocked()
    {
        var session = _session;

        // W trakcie przewinięcia słyszany wpis jest nieaktualny, a dekoder otwiera właśnie cel.
        // Stacja nie ma następnika, a plik czeka do jej końca.
        if (IsSeeking || _ended || session is null || _liveSource is not null) return;

        // Wątek dźwięku przekracza granice bez blokady kolejki, ale pod blokadą granic. Słyszany
        // wpis, poprzedni i zapisane granice pochodzą z jednej migawki pod tą blokadą; gdy granica
        // przesunie się przed zleceniem przewinięcia, sprawdzenie zaczyna się od nowa.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            TrackBoundary current;
            QueueEntry? previousHeard;
            List<QueueEntry> written;

            // Wątek dźwięku czeka na tę blokadę, więc pod nią tylko kopia.
            lock (session.BoundaryLock)
            {
                current = _current;
                previousHeard = session.PreviousHeard;
                written = session.Boundaries.Where(boundary => !boundary.IsEnd).Select(boundary => boundary.Entry!).ToList();
            }

            if (current.Entry is null || RevalidateFromLocked(current, previousHeard, written, session)) return;
        }
    }

    /// <returns>Fałsz, gdy słyszany wpis zmienił się w trakcie i trzeba sprawdzić od nowa.</returns>
    private bool RevalidateFromLocked(
        TrackBoundary current, QueueEntry? previousHeard, List<QueueEntry> written, FeederSession session)
    {
        var heard = current.Entry!;
        var decoding = session.Entry;
        var heardQueued = _queue.Contains(heard);

        // Granica dekodowanego jest ostatnia. Bez niej dekoder jest jeszcze w słyszanym utworze,
        // chyba że właśnie otwiera następny. Słyszany usunięty z kolejki nie ma dokąd grać dalej.
        if (decoding is not null)
        {
            if (written.Count > 0 && ReferenceEquals(written[^1], decoding)) written.RemoveAt(written.Count - 1);
            else if (written.Count == 0 && ReferenceEquals(decoding, heard) && heardQueued) return true;
        }

        // Krótki wpis usunięty po zapisaniu w buforze gra dalej; następnik liczy się wtedy od
        // utworu przed nim. Gdy i tego nie ma w kolejce, następnik jest nieznany i bufor zostaje.
        var anchor = heardQueued ? heard : previousHeard;
        if (anchor is not null && !_queue.Contains(anchor)) anchor = null;

        // Powtarzanie utworu nie dotyczy usuniętego słyszanego: za nim gra wpis po kotwicy.
        var afterRemoved = !heardQueued;
        var previous = anchor;

        foreach (var entry in written)
        {
            // Usunięte z kolejki grają z bufora, ale nie wyznaczają następnika.
            if (!_queue.Contains(entry)) continue;

            if (previous is not null && !ReferenceEquals(NextPlayableLocked(previous, afterRemoved), entry))
                return RestartAfterHeardLocked(current, anchor, session);

            previous = entry;
            afterRemoved = false;
        }

        var expected = previous is null ? null : NextPlayableLocked(previous, afterRemoved);

        if (decoding is null)
        {
            lock (session.BoundaryLock)
            {
                if (!ReferenceEquals(_current, current)) return false;

                Volatile.Write(ref session.ContinueWith, expected);
                return true;
            }
        }

        if (previous is null || ReferenceEquals(expected, decoding)) return true;

        return RestartAfterHeardLocked(current, anchor, session);
    }

    /// <summary>
    /// Wpis, który dekoder otworzy po <paramref name="from"/>. Wpisy, których nie dało się
    /// otworzyć, dekoder już zgłosił i pominął.
    /// </summary>
    /// <param name="afterRemoved">
    /// Następnik usuniętego słyszanego, liczony od kotwicy: powtarzanie utworu go nie dotyczy.
    /// </param>
    private QueueEntry? NextPlayableLocked(QueueEntry from, bool afterRemoved = false)
    {
        var next = _repeat == RepeatMode.Track && !afterRemoved ? from : FollowingEntryLocked(from, userInitiated: false);

        var left = _queue.Count;
        while (next is { IsUnsupported: true } or { OpenFailure: not null })
            next = left-- > 0 ? FollowingEntryLocked(next, userInitiated: false) : null;

        return next;
    }

    /// <summary>
    /// Przewija słyszany utwór w miejscu, żeby dekoder wybrał następny od nowa. Słyszanego wpisu
    /// usuniętego z kolejki przewinąć się nie da, więc gra od razu wpis, który ma przyjść po
    /// kotwicy, a gdy po niej nic nie ma, kolejka kończy się od razu: reszta usuniętego utworu to
    /// najwyżej pół sekundy, a wszystko za nim jest jeszcze tylko w buforze. Bez kotwicy
    /// następnik jest nieznany i bufor zostaje.
    /// </summary>
    /// <returns>Fałsz, gdy słyszany wpis zmienił się w trakcie i nic nie zlecono.</returns>
    private bool RestartAfterHeardLocked(TrackBoundary current, QueueEntry? anchor, FeederSession session)
    {
        // Sprawdzenie i zlecenie pod blokadą granic: wątek dźwięku nie przekroczy granicy między
        // nimi, a po zleceniu przewinięcia nie przekracza ich wcale.
        lock (session.BoundaryLock)
        {
            if (!ReferenceEquals(_current, current)) return false;

            var heard = current.Entry!;
            if (_queue.Contains(heard))
            {
                // Pozycja z tej samej migawki co wpis, nie z bieżącej granicy.
                var frame = Math.Max(0, session.Ring.TotalRead - current.AbsoluteFrame) + current.StartFrame;
                RequestSeekLocked(heard, frame);
            }
            else if (anchor is null)
            {
                Volatile.Write(ref session.ContinueWith, null);
            }
            else if (NextPlayableLocked(anchor, afterRemoved: true) is { } next)
            {
                RequestSeekLocked(next, 0);
            }
            else
            {
                RequestSeekLocked(null, 0);
                _endAfterSeek = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Wpis, od którego liczy się kolejność losowa: słyszany albo, gdy usunięto go z kolejki,
    /// a gra jeszcze z bufora, kotwica, czyli utwór słyszany przed nim. Null, gdy żadnego z nich
    /// nie ma w kolejce.
    /// </summary>
    private QueueEntry? OrderAnchorLocked()
    {
        if (_liveSource is not null)
            return _bookmark is { } place && _queue.Contains(place.Entry) ? place.Entry : null;

        var entry = AudibleOrPendingEntry();
        if (entry is null || _queue.Contains(entry)) return entry;

        var previous = _session?.PreviousHeard;
        return previous is not null && _queue.Contains(previous) ? previous : null;
    }

    /// <summary>Po zmianie kolejki numer grającego wpisu mógł się zmienić; interfejs dostaje nowy.</summary>
    private void NotifyCurrentIndex()
    {
        var index = CurrentIndex;
        if (index >= 0) ThreadPool.UnsafeQueueUserWorkItem(_ => TrackChanged?.Invoke(index), null);
    }

    /// <summary>Paths in queue order, for saving between runs.</summary>
    public IReadOnlyList<string> QueuePaths
    {
        get { lock (_sync) return _queue.Select(entry => entry.Path).ToArray(); }
    }

    public void ClearQueue()
    {
        lock (_sync)
        {
            // Stacja gra niezależnie od kolejki; znika tylko miejsce powrotu.
            if (_liveSource is null) Stop();
            _orderVersion++;
            _queue.Clear();
            _shuffleOrder.Clear();
            _bookmark = null;
        }
    }

    // ================= sterowanie =================

    public void PlayIndex(int index) => PlayIndex(index, TimeSpan.Zero);

    /// <summary>
    /// Zaczyna wpis od podanego miejsca. Cel przewinięcia ustawia się razem z wpisem, w jednej
    /// sekcji krytycznej: osobne SeekTo zaraz po PlayIndex trafiałoby jeszcze w poprzedni stan.
    /// </summary>
    public void PlayIndex(int index, TimeSpan start)
    {
        Initialise();

        ILiveSession? ended = null;
        try
        {
            lock (_sync)
            {
                if (index < 0 || index >= _queue.Count) return;

                var entry = _queue[index];
                if (_shuffle) _shuffleOrder.MoveAfter(entry, OrderAnchorLocked());

                var station = _liveSource;
                ended = ForgetLiveLocked();
                RaiseLiveStopped(station);
                RequestSeekLocked(entry, FramesFrom(start));
                PlayLocked();
            }
        }
        finally
        {
            ended?.Stop();
        }
    }

    /// <summary>
    /// Czas płynnego wyciszenia przy pauzie i podgłośnienia przy wznowieniu plików; zero to
    /// pauza jak dotąd, z wyciszeniem tylko przeciw trzaskom. Stacji nie dotyczy: pauza je
    /// rozłącza, a wznowienie łączy od nowa.
    /// </summary>
    public TimeSpan SoftPause { get; set; }

    /// <summary>Miękka pauza wycisza jeszcze dźwięk i nie zatrzymała wyjścia.</summary>
    public bool IsSoftPausing
    {
        get
        {
            lock (_sync) return _softPausePending;
        }
    }

    /// <summary>
    /// Wznawia albo zaczyna od początku. Po utracie urządzenia otwiera wyjście od nowa; utwór
    /// i pozycja czekają wtedy jako zaplanowane przewinięcie.
    /// </summary>
    public void Play()
    {
        Initialise();

        lock (_sync) PlayLocked(resume: true);
    }

    /// <param name="resume">„Odtwórz” bez nowego wyboru; tylko ono podgłaśnia płynnie po miękkiej pauzie.</param>
    private void PlayLocked(bool resume = false)
    {
        if (_device is null || _liveSource is null && _queue.Count == 0) return;

        // Wznowienie pliku po pauzie podgłaśnia płynnie; nowy wybór gra od razu pełnym poziomem.
        // Miękka pauza w toku nie zatrzymała jeszcze wyjścia: dźwięk wraca od poziomu, do którego
        // zdążył się wyciszyć, krótką rampą, żeby nie skoczył.
        var soft = resume && SoftPause > TimeSpan.Zero && State == PlaybackState.Paused && _liveSource is null;
        var fadingOut = _softPausePending && _device.IsRunning;
        CancelSoftPauseLocked();
        Graph.PauseFade.FadeTo(1, soft ? SoftPause.TotalSeconds : fadingOut ? ShortRampSeconds : 0);

        if (_liveSource is { } station)
        {
            // Pauza, błąd i zmiana urządzenia rozłączają; wznowienie łączy od nowa.
            if (_liveEntry is null) AttachLiveLocked(station, OpenLiveSession(station));
        }
        else if (_ended || AudibleOrPendingEntry() is null)
        {
            // Nic nie gra i nic nie jest zaplanowane albo kolejka się skończyła: od początku.
            var first = FirstEntryLocked();
            if (first is not null) RequestSeekLocked(first, 0);
        }

        // Urządzenie pracujące po miękkiej pauzie gra bez przerwy: krótkie narastanie dałoby dołek.
        if (State != PlaybackState.Playing && !fadingOut) _startFadeIn = true;

        // Stojące urządzenie nie ma czego wyciszać, a wątek dekodujący może nie zdążyć przed pierwszym
        // okresem: bez opróżnienia słychać by było stary dźwięk z pierścienia.
        if (_seekPending && !_device.IsRunning && _session is { } session)
        {
            session.Ring.DiscardAll();
            _resetChainPending = true;
        }

        try
        {
            _device.Start();
        }
        catch (AudioException) when (_liveEntry is not null)
        {
            // Nikt by go nie słuchał; stacja zostaje wybrana, a Play spróbuje od nowa.
            DisconnectLiveLocked()?.Stop();
            State = PlaybackState.Paused;
            throw;
        }

        State = PlaybackState.Playing;
    }

    /// <summary>W czasie stacji rozłącza: wznowienie łączy od bieżącego miejsca transmisji.</summary>
    public void Pause()
    {
        ILiveSession? disconnected = null;

        lock (_sync)
        {
            if (State != PlaybackState.Playing) return;

            if (_liveSource is null && SoftPause > TimeSpan.Zero && _device is { IsRunning: true })
            {
                BeginSoftPauseLocked();
            }
            else if (_liveSource is null)
            {
                FadeOutAndStopLocked();
            }
            else
            {
                FadeOutAndStopForLiveLocked();
                disconnected = DisconnectLiveLocked();
            }

            State = PlaybackState.Paused;
        }

        disconnected?.Stop();
    }

    /// <summary>
    /// Włącza stację w miejsce tego, co gra; kolejka zostaje. Start idzie drogą przewinięcia,
    /// więc obejmuje go to samo wyciszenie.
    /// </summary>
    /// <exception cref="AudioException">Nie da się otworzyć urządzenia.</exception>
    public void PlayLive(LiveSource source)
    {
        ILiveSession? previous = null;
        try
        {
            lock (_sync)
            {
                // Pod blokadą: utrata urządzenia nie wejdzie między otwarcie a utworzenie sesji.
                Initialise();

                var session = OpenLiveSession(source);
                previous = _liveEntry?.Live?.Session;

                if (_liveSource is null) SaveBookmarkLocked();

                _liveSource = source;
                AttachLiveLocked(source, session);
                PlayLocked();
            }
        }
        finally
        {
            previous?.Stop();
        }
    }

    public void StopLive()
    {
        ILiveSession? stopped;
        lock (_sync)
        {
            if (_liveSource is null) return;
            stopped = EndLiveLocked();
        }

        stopped?.Stop();
    }

    /// <summary>Koniec kolejki to nie miejsce do powrotu: następne Play i tak zacznie od początku.</summary>
    private void SaveBookmarkLocked()
    {
        var entry = AudibleOrPendingEntry();
        _bookmark = entry is not null && !_ended && _queue.Contains(entry) ? new LiveBookmark(entry, Position) : null;
    }

    /// <summary>
    /// Kończy stację i wraca do zakładki jako przewinięcia w pauzie; czy grać dalej, wie wołający.
    /// Sesję zatrzymuje wołający, już poza blokadą.
    /// </summary>
    private ILiveSession? EndLiveLocked()
    {
        var bookmark = _bookmark;

        FadeOutAndStopForLiveLocked();

        var source = _liveSource;
        var session = ForgetLiveLocked();
        RaiseLiveStopped(source);
        _current = TrackBoundary.None;

        if (bookmark is { } place && _queue.Contains(place.Entry))
        {
            RequestSeekLocked(place.Entry, FramesFrom(place.Position));
            State = PlaybackState.Paused;
            return session;
        }

        State = PlaybackState.Stopped;
        ForgetPlaceLocked();

        return session;
    }

    /// <summary>Połączenie się kończy, a stacja zostaje wybrana i czeka na Play. Sesję zatrzymuje wołający.</summary>
    private ILiveSession? DisconnectLiveLocked()
    {
        RequestSeekLocked(null, 0);

        // Już rozłączona: stan spoczynku, także po porażce, zostaje i nie ma czego zgłaszać.
        if (_liveEntry is null) return null;

        var session = _liveEntry.Live?.Session;

        _liveEntry = null;
        _liveTitle = null;
        _liveRestState = StreamState.Stopped;
        _liveEpoch++;
        RaiseLiveStopped(_liveSource);

        return session;
    }

    private void RaiseLiveStopped(LiveSource? source)
    {
        if (source is not null) PostLiveEventLocked(() => LiveStateChanged?.Invoke(source, StreamState.Stopped));
    }

    private void PostLiveEventLocked(Action raise)
    {
        _liveEvents.Enqueue((_liveEpoch, raise));
        if (_liveEventsRunning) return;

        _liveEventsRunning = true;
        ThreadPool.UnsafeQueueUserWorkItem(_ => DeliverLiveEvents(), null);
    }

    /// <summary>Jeden wątek naraz; ważność sprawdza się tuż przed wywołaniem odbiorcy.</summary>
    private void DeliverLiveEvents()
    {
        while (true)
        {
            Action raise;
            lock (_sync)
            {
                if (!_liveEvents.TryDequeue(out var next))
                {
                    _liveEventsRunning = false;
                    return;
                }

                if (next.Epoch != _liveEpoch) continue;
                raise = next.Raise;
            }

            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cewka] błąd odbiorcy zdarzenia stacji: {ex.Message}");
            }
        }
    }

    private ILiveSession? ForgetLiveLocked()
    {
        var session = _liveEntry?.Live?.Session;

        _liveSource = null;
        _liveEntry = null;
        _liveTitle = null;
        _bookmark = null;
        _liveEpoch++;

        return session;
    }

    /// <summary>Nowe połączenie: wpis powstaje za każdym razem, a zgłoszenia starego go nie miną.</summary>
    private void AttachLiveLocked(LiveSource source, ILiveSession session)
    {
        var entry = new QueueEntry { Path = source.StationId, Live = new LiveBinding(source, session) };
        _liveEntry = entry;
        _liveTitle = null;
        _liveReportedState = null;
        _liveEpoch++;

        session.StateChanged += state => OnLiveSessionState(entry, state);
        session.InfoChanged += info => OnLiveSessionInfo(entry, info);

        RequestSeekLocked(entry, 0);
        session.Start();
    }

    private void OnLiveSessionState(QueueEntry entry, StreamState state)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(entry, _liveEntry)) return;

            // Porażka sesji to dopiero zapowiedź: bufor gra dalej, a stan końcowy zgłasza FinishLive.
            if (state == StreamState.Failed || state == _liveReportedState) return;

            _liveReportedState = state;
            PostLiveEventLocked(() => LiveStateChanged?.Invoke(entry.Live!.Source, state));
        }
    }

    private void OnLiveSessionInfo(QueueEntry entry, LiveStreamInfo info)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(entry, _liveEntry)) return;

            PostLiveEventLocked(() => LiveInfoChanged?.Invoke(entry.Live!.Source, info));
        }
    }

    public void TogglePlay()
    {
        if (State == PlaybackState.Playing) Pause();
        else Play();
    }

    public void Stop()
    {
        ILiveSession? ended = null;

        lock (_sync)
        {
            if (_liveSource is not null)
            {
                ended = EndLiveLocked();
            }
            else
            {
                FadeOutAndStopLocked();
                State = PlaybackState.Stopped;
                _current = TrackBoundary.None;
                ForgetPlaceLocked();
            }
        }

        ended?.Stop();
    }

    /// <summary>Bez urządzenia nie ma wątku, który wykonałby przewinięcie; wystarczy zapomnieć cel.</summary>
    private void ForgetPlaceLocked()
    {
        if (_device is not null)
        {
            RequestSeekLocked(null, 0);
        }
        else
        {
            _seekPending = false;
            _seekTargetEntry = null;
            _ended = false;
            _generation++;
        }
    }

    /// <summary>W czasie stacji nic: stację zmienia <see cref="PlayLive"/>.</summary>
    public void Next() => Step(forward: true);

    /// <summary>Rewinds first; only jumps back when already near the start of the track.</summary>
    public void Previous()
    {
        // Pod blokadą: stacja włączona po sprawdzeniu oddałaby miejsce utworowi z kolejki.
        lock (_sync)
        {
            if (_liveSource is not null) return;

            if (Position > TimeSpan.FromSeconds(3))
            {
                SeekTo(TimeSpan.Zero);
                return;
            }

            Step(forward: false);
        }
    }

    private void Step(bool forward)
    {
        lock (_sync)
        {
            if (_liveSource is not null) return;

            Initialise();

            if (_queue.Count == 0) return;

            var from = AudibleOrPendingEntry();

            QueueEntry? target;
            if (from is not null && !_queue.Contains(from))
            {
                // Usunięty słyszany gra z bufora: kolejność liczy się od kotwicy, utworu przed nim.
                var anchor = OrderAnchorLocked();
                if (anchor is null) target = FirstEntryLocked();
                else target = forward ? FollowingEntryLocked(anchor, userInitiated: true) : anchor;
            }
            else if (from is null) target = FirstEntryLocked();
            else if (forward) target = FollowingEntryLocked(from, userInitiated: true);
            else target = PrecedingEntryLocked(from) ?? from;

            if (target is null) return;

            RequestSeekLocked(target, 0);
            PlayLocked();
        }
    }

    public void SeekTo(TimeSpan position)
    {
        lock (_sync)
        {
            if (_liveSource is not null) return;

            // Przewinięcie zaraz po PlayIndex dotyczy wpisu, który dopiero zacznie grać. Usuniętego
            // słyszanego przewinąć się nie da, a zostało z niego najwyżej pół sekundy.
            var entry = AudibleOrPendingEntry();
            if (entry is null || !_queue.Contains(entry)) return;

            RequestSeekLocked(entry, FramesFrom(position));
        }
    }

    /// <summary>
    /// Wpis słyszany albo, gdy przewinięcie czeka lub trwa, ten, do którego ono prowadzi.
    /// Przewinięcie trwa także w czasie otwierania pliku, które z wolnego dysku zajmuje chwilę.
    /// </summary>
    private QueueEntry? AudibleOrPendingEntry() => IsSeeking ? _seekTargetEntry : _current.Entry;

    private long FramesFrom(TimeSpan position) => Math.Max(0, (long)(position.TotalSeconds * SampleRate));

    private void RequestSeekLocked(QueueEntry? entry, long frame)
    {
        _endAfterSeek = false;
        _endQueuedGeneration = null;
        _seekTargetEntry = entry;
        Interlocked.Exchange(ref _seekTargetFrame, frame);
        _ended = false;
        _generation++;
        _seekPending = true;
    }

    /// <summary>
    /// Miękka pauza: tor wycisza się w czasie <see cref="SoftPause"/>, a wyjście zatrzymuje się
    /// w tle po wyciszeniu. Wątek interfejsu nie czeka, a stan od razu jest pauzą.
    /// </summary>
    private void BeginSoftPauseLocked()
    {
        var duration = SoftPause;
        Graph.PauseFade.FadeTo(0, duration.TotalSeconds);

        _softPausePending = true;
        var generation = ++_softPauseGeneration;
        _ = Task.Delay(duration + SoftPauseMargin).ContinueWith(_ => FinishSoftPause(generation), TaskScheduler.Default);
    }

    /// <summary>Zatrzymuje wyjście po miękkim wyciszeniu, o ile w międzyczasie nic go nie wznowiło.</summary>
    private void FinishSoftPause(int generation)
    {
        try
        {
            lock (_sync)
            {
                if (!_softPausePending || generation != _softPauseGeneration) return;

                _softPausePending = false;
                if (State == PlaybackState.Paused) FadeOutAndStopLocked();
            }
        }
        catch (AudioException ex)
        {
            Console.Error.WriteLine($"[cewka] nie zatrzymano wyjścia po pauzie: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Silnik zamknięty w trakcie wyciszania: wyjścia już nie ma.
        }
    }

    private void CancelSoftPauseLocked()
    {
        if (!_softPausePending) return;

        _softPausePending = false;
        _softPauseGeneration++;
    }

    /// <summary>
    /// Wycisza wyjście i dopiero wtedy zatrzymuje urządzenie. Zatrzymanie w środku przebiegu
    /// daje trzask, a przy wznowieniu drugi. Czekanie ma limit, bo woła je wątek interfejsu.
    /// </summary>
    private void FadeOutAndStopLocked()
    {
        var device = _device;
        if (device is null) return;

        if (device.IsRunning)
        {
            _pauseFadeDone = false;
            _pauseDraining = false;
            _pauseFadeRequested = true;

            var clock = Stopwatch.StartNew();
            while (!_pauseFadeDone && device.IsRunning && clock.ElapsedMilliseconds < PauseFadeTimeoutMilliseconds)
                Thread.Sleep(1);
        }

        try
        {
            device.Stop();
        }
        finally
        {
            _pauseFadeRequested = false;
        }
    }

    /// <summary>Błąd zatrzymania wyjścia nie może przerwać rozłączania: połączenie nie miałoby już właściciela.</summary>
    private void FadeOutAndStopForLiveLocked()
    {
        try
        {
            FadeOutAndStopLocked();
        }
        catch (AudioException ex)
        {
            Console.Error.WriteLine($"[cewka] nie zatrzymano wyjścia: {ex.Message}");
        }
    }

    /// <summary>
    /// Potwierdzona utrata urządzenia. Wyjście zamyka się od razu: sterownik potrafi uruchomić
    /// strumień sam po ponownym podłączeniu sprzętu, a wtedy grałby przy stanie pauzy.
    /// Zgłoszenie od urządzenia, które zdążyło zostać zastąpione, nie dotyczy bieżącego.
    /// </summary>
    private void OnDeviceLost(IAudioOutput device)
    {
        if (TearDown(keepPlace: true, expected: device) is null) return;

        DeviceLost?.Invoke();
    }

    // ================= kolejność =================

    private QueueEntry? FirstEntryLocked()
    {
        if (_queue.Count == 0) return null;
        if (!_shuffle) return _queue[0];

        _shuffleOrder.Reset(_queue, null);
        return _shuffleOrder.Order[0];
    }

    /// <summary>
    /// Wpis po <paramref name="from"/>. Przy przejściu samoczynnym koniec kolejki daje null,
    /// chyba że powtarzana jest cała kolejka; polecenie „Następny” zawsze zawija.
    /// </summary>
    private QueueEntry? FollowingEntryLocked(QueueEntry from, bool userInitiated)
    {
        if (_queue.Count == 0) return null;

        var wrap = userInitiated || _repeat == RepeatMode.Queue;

        if (_shuffle && _queue.Count > 1) return _shuffleOrder.Next(from, wrap, _queue);

        var index = _queue.IndexOf(from);
        var next = index + 1;
        if (next < _queue.Count) return _queue[next];

        return wrap ? _queue[0] : null;
    }

    private QueueEntry? PrecedingEntryLocked(QueueEntry from)
    {
        if (_shuffle && _queue.Count > 1)
            return _shuffleOrder.Previous(from, _repeat == RepeatMode.Queue, _queue);

        var index = _queue.IndexOf(from);
        if (index < 0) return null;
        return index > 0 ? _queue[index - 1] : _queue[^1];
    }

    // ================= wątek dźwiękowy =================

    private void Render(FeederSession session, Span<float> buffer)
    {
        var ring = session.Ring;
        var channels = Channels;
        var frames = buffer.Length / channels;

        if (_resetChainPending)
        {
            _resetChainPending = false;
            ResetChain();
            _fadingIn = true;
            _fadeCounter = 0;
        }

        if (_startFadeIn)
        {
            _startFadeIn = false;
            _fadingIn = true;
            _fadeCounter = 0;
        }

        if (_snapGainPending)
        {
            _snapGainPending = false;
            Graph.ApplyTrackGain(_pendingGain is TrackGain gain ? gain : null, snap: true);
        }

        if (session.FlushRequested && !session.FlushDone)
        {
            if (RenderFadeOut(ring, buffer, frames))
            {
                ring.DiscardAll();
                ResetChain();
                session.FlushDone = true;
            }

            return;
        }

        if (_pauseFadeRequested)
        {
            if (_pauseFadeDone)
            {
                buffer.Clear();
            }
            else if (_pauseDraining)
            {
                buffer.Clear();
                _pauseDrainPosition += frames;
                if (_pauseDrainPosition >= _drainLength) _pauseFadeDone = true;
            }
            else if (RenderFadeOut(ring, buffer, frames))
            {
                ResetChain();
                _pauseDraining = true;
                _pauseDrainPosition = 0;
            }

            return;
        }

        // Wyciszenie przerwane przed końcem, na przykład pauza po upływie limitu czasu.
        _fadeOutActive = false;
        _pauseDraining = false;

        // Granice są nieaktualne od żądania przewinięcia do jego wykonania; ich przekroczenie
        // zgłosiłoby na chwilę utwór, który już nie zagra.
        var trackBoundaries = !(_seekPending || session.SeekInProgress);

        var done = 0;
        while (done < frames)
        {
            var want = frames - done;

            if (trackBoundaries)
            {
                while (TryCrossBoundary(session, ring.TotalRead)) { }

                // Czytanie kończy się na granicy, żeby wzmocnienie nowego utworu objęło
                // dokładnie jego próbki, a nie końcówkę poprzedniego.
                var ahead = NextBoundaryFrame(session) - ring.TotalRead;
                if (ahead > 0 && ahead < want) want = (int)ahead;
            }

            var got = ring.Read(buffer[(done * channels)..], want);
            if (got == 0) break;

            var segment = buffer.Slice(done * channels, got * channels);
            if (_fadingIn) ApplyFadeIn(segment, got);
            Graph.Process(segment, got);

            done += got;
        }

        if (done < frames)
        {
            // Niedobór: dekoder nie nadąża albo kolejka się skończyła. Cisza i tak przechodzi
            // przez tor, żeby linie opóźniające wybrzmiały.
            var rest = buffer[(done * channels)..];
            rest.Clear();
            Graph.Process(rest, frames - done);
        }

        if (trackBoundaries)
        {
            while (TryCrossBoundary(session, ring.TotalRead)) { }
            TryCrossLiveMark(session, ring.TotalRead);
        }
    }

    /// <summary>
    /// Jeden okres wyciszenia: rampa na wejściu toru, potem cisza przepuszczana przez tor,
    /// aż wybrzmi opóźnienie limitera. Rampa idzie przed torem, więc obejmuje go głośność
    /// i normalizacja. Zwraca true, gdy wyciszenie się skończyło.
    /// </summary>
    private bool RenderFadeOut(PcmRingBuffer ring, Span<float> buffer, int frames)
    {
        if (!_fadeOutActive)
        {
            _fadeOutActive = true;
            _fadeOutPosition = 0;
        }

        var channels = Channels;
        var rampLeft = Math.Max(0, _fadeLength - _fadeOutPosition);
        var read = rampLeft > 0 ? ring.Read(buffer, Math.Min(frames, rampLeft)) : 0;

        for (var i = 0; i < read; i++)
        {
            var gain = 1f - Math.Min(1f, (_fadeOutPosition + i + 1) / (float)_fadeLength);
            for (var c = 0; c < channels; c++) buffer[i * channels + c] *= gain;
        }

        buffer[(read * channels)..].Clear();
        Graph.Process(buffer, frames);

        _fadeOutPosition += frames;
        if (_fadeOutPosition < _fadeLength + _tailLength) return false;

        _fadeOutActive = false;
        _fadingIn = true;
        _fadeCounter = 0;
        return true;
    }

    /// <summary>
    /// Czyści stan stopni toru. Po wyciszeniu w liniach opóźniających i filtrach zostaje
    /// stary dźwięk, który nowy materiał wypuściłby jako trzask.
    /// </summary>
    private void ResetChain() => Graph.Reset();

    private void ApplyFadeIn(Span<float> buffer, int frames)
    {
        var channels = Channels;
        for (var i = 0; i < frames; i++)
        {
            if (_fadeCounter >= _fadeLength) { _fadingIn = false; break; }

            var gain = _fadeCounter / (float)_fadeLength;
            for (var c = 0; c < channels; c++) buffer[i * channels + c] *= gain;
            _fadeCounter++;
        }
    }

    /// <summary>
    /// Przekracza najbliższą granicę, jeśli odtwarzanie ją minęło. Blokada tylko próbowana:
    /// zajęta oznacza, że granica przesunie się w następnym okresie.
    /// </summary>
    private bool TryCrossBoundary(FeederSession session, long framesRead)
    {
        TrackBoundary next;

        if (!session.BoundaryLock.TryEnter()) return false;
        try
        {
            // Przewinięcie zlecone w trakcie okresu: granice za nim i tak znikną, a ich przekroczenie
            // zgłosiłoby na chwilę utwór, który nie zagra.
            if (_seekPending || session.SeekInProgress) return false;
            if (session.Boundaries.Count == 0) return false;

            next = session.Boundaries.Peek();
            if (framesRead < next.AbsoluteFrame) return false;

            session.Boundaries.Dequeue();

            // Słyszany wpis zmienia się pod tą samą blokadą, pod którą sprawdzanie kolejki robi
            // migawkę, żeby migawka nie łączyła starego słyszanego z nowymi granicami.
            if (!next.IsEnd)
            {
                session.PreviousHeard = _current.Entry;
                _current = next;
            }
        }
        finally
        {
            session.BoundaryLock.Exit();
        }

        if (next.IsEnd)
        {
            Volatile.Write(ref session.EndGeneration, next.Generation);
            Volatile.Write(ref session.EndSignal, 1);
            return true;
        }

        Graph.ApplyTrackGain(next.Gain, snap: false);
        Volatile.Write(ref session.TrackChangeSignal, 1);
        return true;
    }

    /// <summary>
    /// Przekazuje wątkowi dekodującemu znacznik stacji, jeśli odtwarzanie go minęło. Jedno miejsce
    /// na znacznik wystarcza: następny poczeka okres, a tytuły przychodzą rzadko.
    /// </summary>
    private static void TryCrossLiveMark(FeederSession session, long framesRead)
    {
        if (Volatile.Read(ref session.CrossedLiveMark) is not null) return;
        if (!session.BoundaryLock.TryEnter()) return;
        try
        {
            if (session.LiveMarks.Count == 0 || framesRead < session.LiveMarks.Peek().AbsoluteFrame) return;

            Volatile.Write(ref session.CrossedLiveMark, session.LiveMarks.Dequeue());
        }
        finally
        {
            session.BoundaryLock.Exit();
        }
    }

    private static long NextBoundaryFrame(FeederSession session)
    {
        if (!session.BoundaryLock.TryEnter()) return long.MaxValue;
        try
        {
            return session.Boundaries.Count == 0 ? long.MaxValue : session.Boundaries.Peek().AbsoluteFrame;
        }
        finally
        {
            session.BoundaryLock.Exit();
        }
    }

    // ================= wątek dekodujący =================

    private void FeederLoop(FeederSession session)
    {
        try
        {
            while (!session.Cancelled)
            {
                try
                {
                    DeliverSignals(session);

                    if (_seekPending) { HandleSeek(session); continue; }

                    if (session.Decoder is null)
                    {
                        if (!ContinueAfterEnd(session)) Thread.Sleep(10);
                        continue;
                    }
                    if (session.Ring.SpaceAvailable < DecodeChunkFrames) { Thread.Sleep(3); continue; }

                    if (session.Decoder is ILiveDecoder live) PumpLive(session, live);
                    else PumpOneChunk(session);
                }
                catch (Exception ex)
                {
                    // Wątek dekodujący nie może zginąć — to zatrzymałoby odtwarzanie na zawsze.
                    // Błąd w środku utworu kończy ten utwór: ponowne otwarcie zaczęłoby go od
                    // początku i przy błędzie powtarzalnym grało w kółko ten sam fragment.
                    Console.Error.WriteLine($"[cewka] błąd wątku dekodującego: {ex.Message}");

                    var failed = session.Entry;

                    // Stacja nie ma następnego utworu ani numeru w kolejce do zgłoszenia.
                    if (failed is { IsLive: true } && !session.Cancelled)
                    {
                        EndLive(session, new LiveFailure(LiveFailureReason.UndecodableData, Detail: ex.Message));
                        continue;
                    }

                    CloseDecoder(session);

                    if (failed is not null && !session.Cancelled)
                    {
                        ReportFailure(session, failed, new TrackFailure(TrackFailureReason.PlaybackInterrupted, ex.Message));

                        QueueEntry? next;
                        lock (_sync) next = _queue.Contains(failed) ? FollowingEntryLocked(failed, userInitiated: false) : null;
                        OpenFirstPlayable(session, next, gapless: true);
                    }
                    else
                    {
                        Thread.Sleep(50);
                    }
                }
            }
        }
        finally
        {
            // Dekoder zamyka zawsze wątek, który go używa.
            CloseDecoder(session);
        }
    }

    /// <summary>Zdarzenia sygnalizowane przez wątek dźwiękowy, zgłaszane już stąd.</summary>
    private void DeliverSignals(FeederSession session)
    {
        if (Interlocked.Exchange(ref session.TrackChangeSignal, 0) != 0)
        {
            var entry = _current.Entry;
            if (entry is not null) RaiseTrackChanged(session, entry);
        }

        if (Interlocked.Exchange(ref session.EndSignal, 0) != 0)
        {
            var generation = Volatile.Read(ref session.EndGeneration);
            ThreadPool.UnsafeQueueUserWorkItem(_ => FinishPlayback(generation), null);
        }

        if (Interlocked.Exchange(ref session.CrossedLiveMark, null) is { } mark) DeliverLiveMark(mark);
    }

    /// <summary>Znacznik stacji minął wyjście. Znacznik połączenia, które już się skończyło, przepada.</summary>
    private void DeliverLiveMark(LiveMark mark)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(mark.Entry, _liveEntry)) return;

            var source = mark.Entry.Live!.Source;
            switch (mark.Kind)
            {
                case LiveMarkKind.Started:
                    PostLiveEventLocked(() => LiveStarted?.Invoke(source));
                    break;
                case LiveMarkKind.Title:
                    _liveTitle = mark.Title;
                    PostLiveEventLocked(() => LiveTitleChanged?.Invoke(source, mark.Title));
                    break;
                default:
                    ThreadPool.UnsafeQueueUserWorkItem(_ => FinishLive(mark.Entry, mark.Failure), null);
                    break;
            }
        }
    }

    /// <summary>
    /// Koniec stacji dotarł do wyjścia. Pauza dopiero teraz, gdy bufor wybrzmiał. Kolejka nie
    /// zaczyna grać sama, a stacja zostaje wybrana: Play to ponowna próba.
    /// </summary>
    private void FinishLive(QueueEntry entry, LiveFailure? failure)
    {
        var binding = entry.Live!;

        lock (_sync)
        {
            if (!ReferenceEquals(entry, _liveEntry)) return;

            FadeOutAndStopForLiveLocked();
            State = PlaybackState.Paused;

            var reported = _liveReportedState;
            var final = failure is null ? StreamState.Stopped : StreamState.Failed;

            _liveEntry = null;
            _liveTitle = null;
            _liveRestState = final;
            _liveEpoch++;
            RequestSeekLocked(null, 0);

            if (reported != final) PostLiveEventLocked(() => LiveStateChanged?.Invoke(binding.Source, final));
            if (failure is { } reason) PostLiveEventLocked(() => LiveFailed?.Invoke(binding.Source, reason));
        }

        binding.Session.Stop();
    }

    /// <summary>
    /// Koniec kolejki dotarł do wyjścia. Pauza dopiero teraz, gdy bufor wybrzmiał: zlecona,
    /// gdy dekoder doszedł do końca pliku, ucinałaby ostatnie pół sekundy.
    /// </summary>
    private void FinishPlayback(int generation)
    {
        lock (_sync)
        {
            if (generation != _generation || _seekPending) return;

            _ended = true;
            _endQueuedGeneration = null;
            if (State == PlaybackState.Playing)
            {
                FadeOutAndStopLocked();
                State = PlaybackState.Paused;
            }
        }

        QueueFinished?.Invoke();
    }

    private void HandleSeek(FeederSession session)
    {
        session.SeekInProgress = true;

        try
        {
            SeekNow(session);
        }
        finally
        {
            session.SeekInProgress = false;
        }
    }

    private void SeekNow(FeederSession session)
    {
        // 1. Wyciszenie i opróżnienie bufora.
        WaitForFlush(session);

        // 2. Odczyt celu.
        QueueEntry? entry;
        long frame;
        int generation;
        bool endQueue;

        lock (_sync)
        {
            if (_session != session) return;

            entry = _seekTargetEntry;
            frame = Interlocked.Read(ref _seekTargetFrame);
            generation = _generation;
            endQueue = _endAfterSeek;
            _endAfterSeek = false;
            _seekPending = false;
            session.ContinueWith = null;

            // Cel usunięty z kolejki w międzyczasie: nie ma czego grać, więc kolejka się kończy,
            // zamiast zostać w stanie odtwarzania bez dźwięku. Stacji w kolejce nigdy nie ma.
            if (entry is { IsLive: false } && !_queue.Contains(entry))
            {
                entry = null;
                endQueue = true;
            }

            // Koniec w toku: zmiana urządzenia przed zgłoszeniem końca nadal ma go za koniec.
            if (endQueue) _endQueuedGeneration = generation;

            // Jawne polecenie: plik, którego wcześniej nie dało się otworzyć, dostaje nową szansę.
            if (entry is not null) entry.OpenFailure = null;
        }

        lock (session.BoundaryLock)
        {
            session.Boundaries.Clear();
            session.LiveMarks.Clear();
        }

        if (entry is null)
        {
            CloseDecoder(session);
            session.Entry = null;
            if (SetCurrent(session, TrackBoundary.None) && endQueue) EndOfQueue(session, gapless: false, generation);
            return;
        }

        if (entry.IsLive)
        {
            OpenLive(session, entry);
            return;
        }

        // Numer pozycji nie jest tożsamością utworu: rozstrzyga wpis, który dekoder
        // faktycznie otworzył. W ostatnim pół sekundy utworu to już następny.
        if (!ReferenceEquals(entry, session.Entry) || session.Decoder is null) CloseDecoder(session);

        var opened = session.Decoder is not null ? entry : OpenFirstPlayable(session, entry, gapless: false, generation);
        if (opened is null)
        {
            SetCurrent(session, TrackBoundary.None);
            return;
        }

        var decoder = session.Decoder!;
        long start = 0;

        if (ReferenceEquals(opened, entry) && frame > 0)
        {
            decoder.Seek(frame);

            // Dekoder, którego przewinięcie się nie udało, stoi gdzie indziej niż cel;
            // pozycja ma opisywać to, co faktycznie zagra.
            start = Math.Max(0, decoder.Position);
        }
        else if (decoder.Position != 0)
        {
            decoder.Seek(0);
            start = Math.Max(0, decoder.Position);
        }

        var gain = Graph.TrackGainFor(opened);
        var boundary = new TrackBoundary(session.Ring.TotalWritten, opened, decoder.TotalFrames, start, gain);

        if (!SetCurrent(session, boundary)) return;

        _pendingGain = gain;
        _snapGainPending = true;

        RaiseTrackChanged(session, opened);
    }

    /// <summary>Dekoder stacji w miejsce tego, co grało; pozycja to odtąd czas słuchania.</summary>
    private void OpenLive(FeederSession session, QueueEntry entry)
    {
        CloseDecoder(session);

        lock (_sync) session.Entry = entry;

        LiveFailure? failure = null;
        try
        {
            session.Decoder = OpenLiveDecoder(entry.Live!.Session.Bytes, SampleRate, Channels);
            session.LiveStartPending = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[cewka] nie otwarto dekodera stacji: {ex.Message}");
            failure = new LiveFailure(LiveFailureReason.Unspecified, Detail: ex.Message);
        }

        var gain = StartLiveLoudness(session, entry.Live!.Source);

        if (!SetCurrent(session, new TrackBoundary(session.Ring.TotalWritten, entry, 0, 0, gain))) return;

        _pendingGain = gain;
        _snapGainPending = true;

        if (failure is not null) EndLive(session, failure);
    }

    /// <summary>
    /// Powrót do stacji startuje od znanego poziomu, a do tej, która gra dalej po ponownym
    /// połączeniu, także z dotychczasowym pomiarem. Nieznana gra bez korekty.
    /// </summary>
    /// <returns>Wzmocnienie na początek; null, gdy stacja jeszcze nieznana.</returns>
    private TrackGain? StartLiveLoudness(FeederSession session, LiveSource source)
    {
        lock (_sync)
        {
            _liveGain = _liveGains.TryGetValue(source.StationId, out var known) ? known : null;

            if (_liveTracker is null || _liveTrackerStation != source.StationId
                || _liveTrackerRate != SampleRate || _liveTrackerChannels != Channels)
            {
                _liveTracker = new LiveLoudnessTracker(SampleRate, Channels, _liveGain?.GainDecibels ?? 0);
                _liveTrackerStation = source.StationId;
                _liveTrackerRate = SampleRate;
                _liveTrackerChannels = Channels;
            }

            session.LiveLoudness = _liveTracker;
            return _liveGain;
        }
    }

    /// <summary>Próbki sprzed toru, więc suwak głośności ani samo wyrównanie nie zmieniają pomiaru.</summary>
    private void FeedLiveLoudness(FeederSession session, int frames)
    {
        var tracker = session.LiveLoudness;
        if (tracker is null) return;

        tracker.Add(session.Buffer, frames);
        if (!tracker.TryUpdate(Graph.LiveTargetLufs, out var gain)) return;

        lock (_sync)
        {
            if (session.Entry is not { } entry || !ReferenceEquals(entry, _liveEntry)) return;

            _liveGain = gain;
            _liveGains[entry.Live!.Source.StationId] = gain;
        }

        Graph.ApplyTrackGain(gain, snap: false);
    }

    /// <summary>Koniec idzie do bufora jako znacznik: to, co już zdekodowane, ma wybrzmieć.</summary>
    private static void EndLive(FeederSession session, LiveFailure? failure)
    {
        var entry = session.Entry;

        CloseDecoder(session);
        session.Entry = null;

        if (entry is null) return;

        var mark = new LiveMark(session.Ring.TotalWritten, LiveMarkKind.Ended, entry, Failure: failure);
        lock (session.BoundaryLock) session.LiveMarks.Enqueue(mark);
    }

    /// <summary>
    /// Przestawia bieżący utwór, o ile ta sesja nadal jest aktualna, i kończy przewinięcie:
    /// od tej chwili wpis i pozycję opisuje <see cref="_current"/>, nie cel.
    /// </summary>
    private bool SetCurrent(FeederSession session, TrackBoundary boundary)
    {
        lock (_sync)
        {
            if (_session != session) return false;
            session.PreviousHeard = null;
            _current = boundary;
            session.SeekInProgress = false;
            return true;
        }
    }

    /// <summary>
    /// Czeka, aż wątek dźwiękowy wyciszy i opróżni bufor. Gdy urządzenie stoi, opróżnia go
    /// sam, pod tą samą blokadą, pod którą urządzenie się uruchamia, żeby dwóch czytelników
    /// nie przesunęło wskaźnika odczytu naraz.
    /// </summary>
    private void WaitForFlush(FeederSession session)
    {
        session.FlushDone = false;
        session.FlushRequested = true;

        var clock = Stopwatch.StartNew();
        while (!session.FlushDone && !session.Cancelled)
        {
            lock (_sync)
            {
                if (_device is not { IsRunning: true } || clock.ElapsedMilliseconds > FlushTimeoutMilliseconds)
                {
                    // Po przekroczeniu czasu urządzenie nie woła już wątku dźwiękowego.
                    session.Ring.DiscardAll();
                    _resetChainPending = true;
                    break;
                }
            }

            Thread.Sleep(1);
        }

        session.FlushRequested = false;
    }

    private void PumpOneChunk(FeederSession session)
    {
        var decoder = session.Decoder!;
        var needed = DecodeChunkFrames * Channels;
        if (session.Buffer.Length < needed) session.Buffer = new float[needed];

        var frames = decoder.Read(session.Buffer.AsSpan(0, needed));

        if (frames == 0)
        {
            AdvanceToNextTrack(session);
            return;
        }

        WriteToRing(session, frames);
    }

    private void PumpLive(FeederSession session, ILiveDecoder decoder)
    {
        var needed = DecodeChunkFrames * Channels;
        if (session.Buffer.Length < needed) session.Buffer = new float[needed];

        var status = decoder.ReadLive(session.Buffer.AsSpan(0, needed), out var frames);

        if (status == LiveReadStatus.Waiting)
        {
            Thread.Sleep(LiveWaitMilliseconds);
            return;
        }

        if (status == LiveReadStatus.Ended)
        {
            EndLive(session, decoder.EndFailure);
            return;
        }

        EnqueueLiveMarks(session, decoder, frames);
        WriteToRing(session, frames);
        FeedLiveLoudness(session, frames);
    }

    /// <summary>Znaczniki idą do bufora razem z dźwiękiem, żeby zgłosić je dopiero, gdy zagra.</summary>
    private static void EnqueueLiveMarks(FeederSession session, ILiveDecoder decoder, int frames)
    {
        var entry = session.Entry;
        if (entry is null) return;

        var ringFrame = session.Ring.TotalWritten;
        var decoderFrame = decoder.Position - frames;

        lock (session.BoundaryLock)
        {
            if (session.LiveStartPending)
            {
                session.LiveStartPending = false;
                session.LiveMarks.Enqueue(new LiveMark(ringFrame + 1, LiveMarkKind.Started, entry));
            }

            while (decoder.TryTakeMarker(out var marker, out var at))
            {
                if (marker is not StreamTitle title) continue;

                var frame = ringFrame + Math.Clamp(at - decoderFrame, 0, frames);
                session.LiveMarks.Enqueue(new LiveMark(frame, LiveMarkKind.Title, entry, title.Title));
            }
        }
    }

    private void WriteToRing(FeederSession session, int frames)
    {
        var written = 0;
        while (written < frames && !session.Cancelled && !_seekPending)
        {
            var accepted = session.Ring.Write(
                session.Buffer.AsSpan(written * Channels, (frames - written) * Channels),
                frames - written);

            if (accepted == 0) { Thread.Sleep(2); continue; }
            written += accepted;
        }
    }

    /// <summary>
    /// The current file has run out. Opening the next one here — while the ring still holds
    /// half a second of audio — is what makes the change of track inaudible.
    /// </summary>
    private void AdvanceToNextTrack(FeederSession session)
    {
        var finished = session.Entry;
        CloseDecoder(session);

        QueueEntry? next = null;
        lock (_sync)
        {
            if (finished is not null && _queue.Contains(finished))
                next = _repeat == RepeatMode.Track ? finished : FollowingEntryLocked(finished, userInitiated: false);
        }

        OpenFirstPlayable(session, next, gapless: true);
    }

    /// <summary>
    /// Otwiera <paramref name="candidate"/> albo, gdy się nie da, kolejne wpisy w porządku
    /// odtwarzania, najwyżej tyle razy, ile wpisów ma kolejka. Przy przejściu bez przerwy
    /// zapisuje granicę nowego utworu; gdy nic nie da się otworzyć, znacznik końca kolejki.
    /// </summary>
    private QueueEntry? OpenFirstPlayable(FeederSession session, QueueEntry? candidate, bool gapless, int? generation = null)
    {
        int attempts;
        lock (_sync) attempts = _queue.Count;

        for (; candidate is not null && attempts > 0 && !session.Cancelled; attempts--)
        {
            lock (_sync)
            {
                if (!_queue.Contains(candidate)) break;
                session.Entry = candidate;
            }

            if (TryOpen(session, candidate, out var failure))
            {
                if (gapless)
                {
                    var boundary = new TrackBoundary(
                        session.Ring.TotalWritten, candidate, session.Decoder!.TotalFrames, 0,
                        Graph.TrackGainFor(candidate));

                    lock (session.BoundaryLock) session.Boundaries.Enqueue(boundary);
                }

                return candidate;
            }

            ReportFailure(session, candidate, failure);

            // Powtarzanie utworu nie dotyczy pliku, którego nie da się otworzyć.
            lock (_sync) candidate = _queue.Contains(candidate) ? FollowingEntryLocked(candidate, userInitiated: false) : null;
        }

        session.Entry = null;
        EndOfQueue(session, gapless, generation ?? Volatile.Read(ref _generation));
        return null;
    }

    /// <summary>
    /// Nic więcej do dekodowania. Przy przejściu bez przerwy znacznik końca idzie do bufora
    /// i pauza nastąpi, gdy odtwarzanie go minie; inaczej od razu.
    /// </summary>
    private void EndOfQueue(FeederSession session, bool gapless, int generation)
    {
        if (session.Cancelled) return;

        if (gapless)
        {
            var marker = new TrackBoundary(session.Ring.TotalWritten, null, 0, IsEnd: true, Generation: generation);
            lock (session.BoundaryLock) session.Boundaries.Enqueue(marker);
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ => FinishPlayback(generation), null);
    }

    /// <summary>
    /// Po zapisaniu końca kolejki pojawił się następny wpis. Dopóki odtwarzanie nie minęło
    /// znacznika końca, znacznik się wycofuje, a nowy utwór dopisuje do bufora bez przerwy.
    /// </summary>
    private bool ContinueAfterEnd(FeederSession session)
    {
        var next = Interlocked.Exchange(ref session.ContinueWith, null);
        if (next is null) return false;

        lock (session.BoundaryLock)
        {
            // Znacznik końca jest ostatnią granicą, bo po nim dekoder niczego nie zapisał.
            var boundaries = session.Boundaries;
            if (boundaries.Count == 0 || !boundaries.Last().IsEnd) return true;

            for (var kept = boundaries.Count - 1; kept > 0; kept--) boundaries.Enqueue(boundaries.Dequeue());
            boundaries.Dequeue();
        }

        OpenFirstPlayable(session, next, gapless: true);
        return true;
    }

    private bool TryOpen(FeederSession session, QueueEntry entry, out TrackFailure failure)
    {
        try
        {
            entry.EnsureMetadata();

            if (entry.IsUnsupported)
            {
                failure = new TrackFailure(TrackFailureReason.UnsupportedFormat, entry.UnsupportedReason);
                return false;
            }

            session.Decoder = OpenDecoder(entry.Path, SampleRate, Channels);
            entry.OpenFailure = null;
            failure = default;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => TrackFailureReason.FileMissing,
                UnauthorizedAccessException => TrackFailureReason.AccessDenied,
                IOException => TrackFailureReason.FileUnavailable,
                _ => TrackFailureReason.DamagedFile,
            };

            Console.Error.WriteLine($"[cewka] nie otwarto pliku {entry.Path}: {ex.Message}");

            entry.OpenFailure = reason;
            failure = new TrackFailure(reason, ex.Message);
            return false;
        }
    }

    private void RaiseTrackChanged(FeederSession session, QueueEntry entry)
    {
        if (session.Cancelled) return;

        int index;
        lock (_sync) index = _queue.IndexOf(entry);

        if (index >= 0) ThreadPool.UnsafeQueueUserWorkItem(_ => TrackChanged?.Invoke(index), null);
    }

    private void ReportFailure(FeederSession session, QueueEntry entry, TrackFailure failure)
    {
        if (session.Cancelled) return;

        int index;
        lock (_sync) index = _queue.IndexOf(entry);

        if (index >= 0) ThreadPool.UnsafeQueueUserWorkItem(_ => TrackFailed?.Invoke(index, failure), null);
    }

    private static void CloseDecoder(FeederSession session)
    {
        session.Decoder?.Dispose();
        session.Decoder = null;
    }

    public void Dispose() => TearDown(keepPlace: false);

    /// <summary>
    /// Stan jednego wątku dekodującego: jego bufor, granice, dekoder i flagi uzgodnień
    /// z wątkiem dźwiękowym. Wątek, który nie zdążył się zakończyć przy zamykaniu urządzenia,
    /// pracuje dalej wyłącznie na własnej sesji i nie dotyka nowej.
    /// </summary>
    private sealed class FeederSession(PcmRingBuffer ring)
    {
        public readonly PcmRingBuffer Ring = ring;
        public readonly Lock BoundaryLock = new();
        public readonly Queue<TrackBoundary> Boundaries = new();
        public volatile bool Cancelled;

        public volatile bool SeekInProgress;
        public volatile bool FlushRequested;
        public volatile bool FlushDone;

        // Sygnały z wątku dźwiękowego odbierane przez wątek dekodujący.
        public int TrackChangeSignal;
        public int EndSignal;
        public int EndGeneration;

        public IAudioDecoder? Decoder;

        /// <summary>Wpis otwarty albo właśnie otwierany przez dekoder; zapisywany pod blokadą kolejki.</summary>
        public volatile QueueEntry? Entry;

        /// <summary>Wpis słyszany przed bieżącym; ustawia go wątek dźwiękowy przy przekroczeniu granicy.</summary>
        public volatile QueueEntry? PreviousHeard;

        /// <summary>Wpis, który ma zagrać za zapisanym już końcem kolejki; ustawiany pod blokadą kolejki.</summary>
        public QueueEntry? ContinueWith;

        public float[] Buffer = [];

        /// <summary>Znaczniki stacji w pozycjach bufora, pod <see cref="BoundaryLock"/>.</summary>
        public readonly Queue<LiveMark> LiveMarks = new();

        /// <summary>Znacznik, który minął wyjście; wątek dźwiękowy odkłada, wątek dekodujący odbiera.</summary>
        public LiveMark? CrossedLiveMark;

        public bool LiveStartPending;

        /// <summary>Używa go wyłącznie wątek dekodujący.</summary>
        public LiveLoudnessTracker? LiveLoudness;
    }

    private enum LiveMarkKind { Started, Title, Ended }

    /// <summary>Zdarzenie stacji w miejscu bufora; zgłaszane, gdy odtwarzanie je minie.</summary>
    private sealed record LiveMark(
        long AbsoluteFrame, LiveMarkKind Kind, QueueEntry Entry, string? Title = null, LiveFailure? Failure = null);

    /// <summary>
    /// Marks the frame in the ring at which a queue entry's audio begins.
    /// <para>
    /// <paramref name="StartFrame"/> says where inside the track that audio starts: zero for a
    /// track played from its beginning, the seek target after a seek. <paramref name="Gain"/>
    /// is the track's normalisation gain with its programme level, applied by the audio thread
    /// when the boundary is crossed. An end marker has no entry and closes the queue.
    /// </para>
    /// </summary>
    private sealed record TrackBoundary(
        long AbsoluteFrame,
        QueueEntry? Entry,
        long TotalFrames,
        long StartFrame = 0,
        TrackGain? Gain = null,
        bool IsEnd = false,
        int Generation = 0)
    {
        public static readonly TrackBoundary None = new(0, null, 0);
    }
}
