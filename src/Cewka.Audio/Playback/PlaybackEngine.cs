using System.Diagnostics;
using Cewka.Audio.Decoding;
using Cewka.Audio.Devices;
using Cewka.Audio.Dsp;

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

    /// <summary>
    /// Cisza oddawana po wyciszeniu, zanim wolno zatrzymać urządzenie. Wyrenderowane próbki
    /// czekają jeszcze w buforze sprzętowym, a zatrzymanie go ucina — z trzaskiem.
    /// </summary>
    private const int DrainMilliseconds = 40;

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

    /// <summary>Rośnie z każdym żądaniem przewinięcia; nieaktualny sygnał końca kolejki go nie zgadza.</summary>
    private int _generation;

    /// <summary>Kolejka dobiegła końca; następne Play zaczyna od początku.</summary>
    private bool _ended;

    // Stan wątku dźwiękowego. Pola volatile ustawiają inne wątki, resztę zmienia tylko on.
    private volatile bool _pauseFadeRequested;
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

    public PlaybackEngine()
        : this(static (name, period) => AudioDevice.Open(name, 0, 2, period))
    {
    }

    /// <summary>Dla testów: wyjście dźwięku i źródło losowości podane z zewnątrz.</summary>
    internal PlaybackEngine(Func<string?, int, IAudioOutput> openOutput, Random? random = null)
    {
        _openOutput = openOutput;
        _shuffleOrder = new ShuffleOrder(random);
    }

    /// <summary>Otwieranie dekodera; testy podstawiają tu dekoder syntetyczny.</summary>
    internal Func<string, int, int, IAudioDecoder> OpenDecoder { get; set; } = DecoderFactory.Open;

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

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;

    public RepeatMode Repeat { get; set; } = RepeatMode.None;

    /// <summary>
    /// Odtwarzanie losowe. Włączenie układa nową kolejność z bieżącym utworem na początku.
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
                if (value) _shuffleOrder.Reset(_queue, AudibleOrPendingEntry());
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
    /// Ponownie odczytuje wzmocnienie wyrównania dla słyszanego utworu. Potrzebne, gdy zmieni
    /// się samo ustawienie: bez tego zmiana byłaby słyszalna dopiero od następnego utworu.
    /// </summary>
    public void RefreshTrackGain()
    {
        var entry = _current.Entry;
        if (entry is not null) Graph.OnTrackChanged(entry);
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

        lock (_sync)
        {
            if (expected is not null && !ReferenceEquals(expected, _device)) return null;

            feeder = _feeder;
            session = _session;
            device = _device;

            var entry = AudibleOrPendingEntry();
            closed = new ClosedState(State == PlaybackState.Playing && !_ended);
            var frame = FramesFrom(Position);
            var ended = _ended;

            _feeder = null;
            _session = null;
            _device = null;

            // Odłączenie jeszcze pod blokadą: sterownik może sam uruchomić utracone urządzenie,
            // a Initialise nowej sesji przygotowuje ten sam tor. Stop czeka na trwający okres.
            if (device is not null)
            {
                device.Render = null;
                try { device.Stop(); } catch (AudioException) { }
            }

            State = PlaybackState.Stopped;
            _seekTargetEntry = null;
            _seekPending = false;
            _ended = false;
            _pauseFadeRequested = false;
            _current = TrackBoundary.None;

            if (keepPlace)
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
        feeder?.Join(1000);
        device?.Dispose();

        return closed;
    }

    private readonly record struct ClosedState(bool WasPlaying);

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
            _queue.Clear();
            foreach (var path in paths) _queue.Add(new QueueEntry { Path = path });
            count = _queue.Count;

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
            _queue.AddRange(added);
            _shuffleOrder.Insert(added, AudibleOrPendingEntry());

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
            _queue.RemoveAt(index);
            _shuffleOrder.Remove(entry);

            wasPlaying = ReferenceEquals(entry, AudibleOrPendingEntry());
            if (!wasPlaying) RevalidatePrefetchLocked();
        }

        if (wasPlaying) Stop();
        else NotifyCurrentIndex();
    }

    /// <summary>Reorders the queue, used by dragging a row to a new position.</summary>
    public void Move(int from, int to)
    {
        lock (_sync)
        {
            if (from < 0 || from >= _queue.Count) return;
            if (to < 0 || to >= _queue.Count || from == to) return;

            var entry = _queue[from];
            _queue.RemoveAt(from);
            _queue.Insert(to, entry);

            RevalidatePrefetchLocked();
        }

        NotifyCurrentIndex();
    }

    /// <summary>
    /// W ostatnim pół sekundy utworu dekoder ma już otwarty następny albo zapisał koniec
    /// kolejki. Gdy zmiana kolejki sprawia, że następnym jest inny wpis, bufor kończy się
    /// niewłaściwie. Gdy następny jest już otwarty, przewinięcie słyszanego utworu w miejscu
    /// opróżnia bufor i każe dekoderowi wybrać następny od nowa; za zapisanym końcem kolejki
    /// dekoder dopisuje nowy wpis bez przerwy.
    /// </summary>
    private void RevalidatePrefetchLocked()
    {
        var heard = _current.Entry;
        var session = _session;
        var decoding = session?.Entry;

        // W trakcie przewinięcia słyszany wpis jest nieaktualny, a dekoder otwiera właśnie cel.
        if (IsSeeking || _ended || heard is null || session is null || ReferenceEquals(heard, decoding)) return;

        // Następnik liczy się od wpisu zapisanego w buforze tuż przed dekodowanym albo przed
        // znacznikiem końca; krótki utwór bywa zapisany w całości, zanim zagra.
        var previous = heard;
        lock (session.BoundaryLock)
        {
            var written = session.Boundaries.Where(boundary => !boundary.IsEnd).Select(boundary => boundary.Entry).ToList();
            if (written.Count > 0 && ReferenceEquals(written[^1], decoding)) written.RemoveAt(written.Count - 1);
            if (written.Count > 0) previous = written[^1]!;
        }

        var expected = Repeat == RepeatMode.Track ? previous : FollowingEntryLocked(previous, userInitiated: false);

        if (decoding is null)
        {
            // Koniec kolejki bywa zapisany po wpisach, których nie dało się otworzyć; dekoder
            // już je zgłosił i pominął.
            var left = _queue.Count;
            while (expected is { IsUnsupported: true } or { OpenFailure: not null })
                expected = left-- > 0 ? FollowingEntryLocked(expected, userInitiated: false) : null;

            Volatile.Write(ref session.ContinueWith, expected);
            return;
        }

        if (ReferenceEquals(expected, decoding)) return;

        RequestSeekLocked(heard, FramesFrom(Position));
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
            Stop();
            _queue.Clear();
            _shuffleOrder.Clear();
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

        lock (_sync)
        {
            if (index < 0 || index >= _queue.Count) return;

            var entry = _queue[index];
            if (_shuffle) _shuffleOrder.MoveAfter(entry, AudibleOrPendingEntry());

            RequestSeekLocked(entry, FramesFrom(start));
            PlayLocked();
        }
    }

    /// <summary>
    /// Wznawia albo zaczyna od początku. Po utracie urządzenia otwiera wyjście od nowa; utwór
    /// i pozycja czekają wtedy jako zaplanowane przewinięcie.
    /// </summary>
    public void Play()
    {
        Initialise();

        lock (_sync) PlayLocked();
    }

    private void PlayLocked()
    {
        if (_queue.Count == 0 || _device is null) return;

        // Nic nie gra i nic nie jest zaplanowane albo kolejka się skończyła: od początku.
        if (_ended || AudibleOrPendingEntry() is null)
        {
            var first = FirstEntryLocked();
            if (first is not null) RequestSeekLocked(first, 0);
        }

        if (State != PlaybackState.Playing) _startFadeIn = true;

        _device.Start();
        State = PlaybackState.Playing;
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (State != PlaybackState.Playing) return;

            FadeOutAndStopLocked();
            State = PlaybackState.Paused;
        }
    }

    public void TogglePlay()
    {
        if (State == PlaybackState.Playing) Pause();
        else Play();
    }

    public void Stop()
    {
        lock (_sync)
        {
            FadeOutAndStopLocked();
            State = PlaybackState.Stopped;
            _current = TrackBoundary.None;

            // Bez urządzenia nie ma wątku, który wykonałby przewinięcie; wystarczy zapomnieć cel.
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
    }

    public void Next() => Step(forward: true);

    /// <summary>Rewinds first; only jumps back when already near the start of the track.</summary>
    public void Previous()
    {
        if (Position > TimeSpan.FromSeconds(3))
        {
            SeekTo(TimeSpan.Zero);
            return;
        }

        Step(forward: false);
    }

    private void Step(bool forward)
    {
        Initialise();

        lock (_sync)
        {
            if (_queue.Count == 0) return;

            var from = AudibleOrPendingEntry();

            QueueEntry? target;
            if (from is null) target = FirstEntryLocked();
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
            // Przewinięcie zaraz po PlayIndex dotyczy wpisu, który dopiero zacznie grać.
            var entry = AudibleOrPendingEntry();
            if (entry is null) return;

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
        _seekTargetEntry = entry;
        Interlocked.Exchange(ref _seekTargetFrame, frame);
        _ended = false;
        _generation++;
        _seekPending = true;
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

        var wrap = userInitiated || Repeat == RepeatMode.Queue;

        if (_shuffle && _queue.Count > 1) return _shuffleOrder.Next(from, wrap, _queue);

        var index = _queue.IndexOf(from);
        var next = index + 1;
        if (next < _queue.Count) return _queue[next];

        return wrap ? _queue[0] : null;
    }

    private QueueEntry? PrecedingEntryLocked(QueueEntry from)
    {
        if (_shuffle && _queue.Count > 1)
            return _shuffleOrder.Previous(from, Repeat == RepeatMode.Queue, _queue);

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

        if (trackBoundaries) while (TryCrossBoundary(session, ring.TotalRead)) { }
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
            if (session.Boundaries.Count == 0) return false;

            next = session.Boundaries.Peek();
            if (framesRead < next.AbsoluteFrame) return false;

            session.Boundaries.Dequeue();
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

        _current = next;
        Graph.ApplyTrackGain(next.Gain, snap: false);
        Volatile.Write(ref session.TrackChangeSignal, 1);
        return true;
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

                    PumpOneChunk(session);
                }
                catch (Exception ex)
                {
                    // Wątek dekodujący nie może zginąć — to zatrzymałoby odtwarzanie na zawsze.
                    // Błąd w środku utworu kończy ten utwór: ponowne otwarcie zaczęłoby go od
                    // początku i przy błędzie powtarzalnym grało w kółko ten sam fragment.
                    Console.Error.WriteLine($"[cewka] błąd wątku dekodującego: {ex.Message}");

                    var failed = session.Entry;
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

        lock (_sync)
        {
            if (_session != session) return;

            entry = _seekTargetEntry;
            frame = Interlocked.Read(ref _seekTargetFrame);
            generation = _generation;
            _seekPending = false;
            session.ContinueWith = null;

            if (entry is not null && !_queue.Contains(entry)) entry = null;

            // Jawne polecenie: plik, którego wcześniej nie dało się otworzyć, dostaje nową szansę.
            if (entry is not null) entry.OpenFailure = null;
        }

        lock (session.BoundaryLock) session.Boundaries.Clear();

        if (entry is null)
        {
            CloseDecoder(session);
            session.Entry = null;
            SetCurrent(session, TrackBoundary.None);
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

    /// <summary>
    /// Przestawia bieżący utwór, o ile ta sesja nadal jest aktualna, i kończy przewinięcie:
    /// od tej chwili wpis i pozycję opisuje <see cref="_current"/>, nie cel.
    /// </summary>
    private bool SetCurrent(FeederSession session, TrackBoundary boundary)
    {
        lock (_sync)
        {
            if (_session != session) return false;
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
                next = Repeat == RepeatMode.Track ? finished : FollowingEntryLocked(finished, userInitiated: false);
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

        /// <summary>Wpis, który ma zagrać za zapisanym już końcem kolejki; ustawiany pod blokadą kolejki.</summary>
        public QueueEntry? ContinueWith;

        public float[] Buffer = [];
    }

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
