using Cewka.Audio.Decoding;
using Cewka.Audio.Devices;
using Cewka.Audio.Dsp;

namespace Cewka.Audio.Playback;

/// <summary>
/// Drives playback: owns the device, the decode thread and the queue.
///
/// <para><b>Kształt rozwiązania.</b> Jeden wątek dekodujący napełnia bufor pierścieniowy,
/// a wywołanie zwrotne urządzenia wyłącznie z niego czyta i przepuszcza próbki przez tor
/// przetwarzania. Granice utworów zapisywane są jako znaczniki pozycji w tym samym buforze,
/// dzięki czemu przejście do kolejnego utworu nie wymaga żadnej przerwy — na tym polega
/// odtwarzanie bezprzerwowe. Wątek dekodujący wyprzedza odtwarzanie o pół sekundy, co
/// wystarcza, by zdążył otworzyć następny plik.</para>
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    /// <summary>Half a second of lookahead: enough to open the next file, short enough that seeking feels immediate.</summary>
    private const double RingSeconds = 0.5;

    /// <summary>Frames decoded per pass of the feeder loop.</summary>
    private const int DecodeChunkFrames = 2048;

    /// <summary>Fade applied around a flush so a seek does not click.</summary>
    private const double FadeMilliseconds = 6;

    private readonly object _sync = new();
    private readonly List<QueueEntry> _queue = [];
    private readonly Lock _boundaryLock = new();
    private readonly Queue<TrackBoundary> _boundaries = new();

    private AudioDevice? _device;
    private PcmRingBuffer? _ring;
    private Thread? _feeder;
    private volatile bool _feederRunning;

    private IAudioDecoder? _decoder;
    private QueueEntry? _playingEntry;
    private int _decodingIndex = -1;
    private float[] _decodeBuffer = [];

    // Uzgodnienie opróżnienia bufora między wątkiem sterującym, dekodującym i dźwiękowym.
    private volatile bool _seekPending;

    /// <summary>
    /// True from the moment the decode thread picks a seek up until the new boundary is in
    /// place. <see cref="_seekPending"/> alone is not enough: it is cleared as soon as the
    /// target is read, and opening or seeking a file takes long enough for the interface to
    /// read the old position in between and jump the indicator back.
    /// </summary>
    private volatile bool _seekInProgress;

    private volatile bool _producerParked;
    private volatile bool _flushRequested;
    private volatile bool _flushDone;
    private long _seekTargetFrame;
    private int _seekTargetIndex = -1;

    private int _fadeCounter;
    private int _fadeLength = 1;
    private bool _fadingIn;

    private TrackBoundary _current = new(0, -1, 0);
    private volatile bool _finished;

    /// <summary>Raised off the audio thread when playback moves to another queue entry.</summary>
    public event Action<int>? TrackChanged;

    /// <summary>Raised when the queue runs out and playback stops on its own.</summary>
    public event Action? QueueFinished;

    /// <summary>Raised when a track could not be opened; the argument is the queue index.</summary>
    public event Action<int, string>? TrackFailed;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;

    public RepeatMode Repeat { get; set; } = RepeatMode.None;

    public bool Shuffle { get; set; }

    /// <summary>Signal chain applied on the audio thread. Assigned once, before playback.</summary>
    public AudioGraph Graph { get; } = new();

    public IReadOnlyList<QueueEntry> Queue
    {
        get { lock (_sync) return _queue.ToArray(); }
    }

    public int CurrentIndex => _current.QueueIndex;

    /// <summary>
    /// Ponownie odczytuje wzmocnienie wyrównania dla trwającego utworu.
    ///
    /// <para>Potrzebne, gdy zmieni się samo ustawienie, a nie utwór: bez tego przestawienie
    /// poziomu docelowego albo wyłączenie wyrównania byłoby słyszalne dopiero od następnego
    /// utworu, choć zmiany dokonano właśnie po to, żeby porównać brzmienie teraz.</para>
    ///
    /// <para>Odczytywany jest utwór dekodowany, który przy przejściu bezprzerwowym może być
    /// już następnym w kolejce. W najgorszym razie wzmocnienie policzy się o jeden utwór za
    /// wcześnie i poprawi samo, gdy ten utwór faktycznie się zacznie.</para>
    /// </summary>
    public void RefreshTrackGain()
    {
        var entry = _playingEntry;
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

    /// <summary>
    /// Bitrate of the passage being decoded, in kilobits per second; 0 when not measured.
    /// Read from the decode thread's decoder, which is safe for a single integer.
    /// </summary>
    public int InstantaneousBitrate => _decoder?.InstantaneousBitrate ?? 0;

    /// <summary>Position within the current track, derived from frames actually delivered.</summary>
    public TimeSpan Position
    {
        get
        {
            // Miedzy zazadaniem przewiniecia a wykonaniem go przez watek dekodujacy bufor
            // opisuje jeszcze stare polozenie. Zwracanie go cofneloby wskaznik na ulamek
            // sekundy, wiec az do przelaczenia obowiazuje cel przewiniecia.
            if (_seekPending || _seekInProgress)
            {
                var target = Interlocked.Read(ref _seekTargetFrame);
                return TimeSpan.FromSeconds(target / (double)SampleRate);
            }

            var ring = _ring;
            if (ring is null || _current.QueueIndex < 0) return TimeSpan.Zero;

            var frames = ring.TotalRead - _current.AbsoluteFrame;
            if (frames < 0) frames = 0;

            // Do ramek odtworzonych od granicy dochodzi miejsce, w ktorym ta granica lezy
            // wewnatrz utworu - po przewinieciu jest to cel przewiniecia, nie zero.
            return TimeSpan.FromSeconds((frames + _current.StartFrame) / (double)SampleRate);
        }
    }

    public TimeSpan Duration => TimeSpan.FromSeconds(_current.TotalFrames / (double)Math.Max(1, SampleRate));

    // ================= konfiguracja =================

    /// <summary>
    /// Device the next <see cref="Initialise"/> should open; −1 asks for the system default.
    /// Set from the settings window before anything is playing.
    /// </summary>
    public int PreferredDeviceIndex { get; set; } = -1;

    /// <summary>
    /// Żądany rozmiar okresu w ramkach; zero zostawia wybór miniaudio. Wartość jest wyłącznie
    /// podpowiedzią — przyjęty rozmiar podaje <see cref="PeriodSizeInFrames"/>.
    /// </summary>
    public int PreferredPeriodSize { get; set; }

    /// <summary>Rozmiar okresu, jaki otwarte urządzenie faktycznie przyjęło; zero, gdy zamknięte.</summary>
    public int PeriodSizeInFrames => _device?.PeriodSizeInFrames ?? 0;

    /// <summary>Opens the output device and starts the decode thread. Idempotent.</summary>
    public void Initialise(int deviceIndex = int.MinValue)
    {
        if (deviceIndex == int.MinValue) deviceIndex = PreferredDeviceIndex;

        lock (_sync)
        {
            if (_device is not null) return;

            _device = new AudioDevice(0, 2, deviceIndex, PreferredPeriodSize);
            SampleRate = _device.SampleRate;
            Channels = _device.Channels;

            _ring = new PcmRingBuffer((int)(SampleRate * RingSeconds), Channels);
            _fadeLength = Math.Max(1, (int)(SampleRate * FadeMilliseconds / 1000));

            Graph.Prepare(SampleRate, Channels);

            _device.Render = Render;

            _feederRunning = true;
            _feeder = new Thread(FeederLoop)
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
    /// Moves playback to another output device without restarting the application.
    ///
    /// <para><b>Dlaczego pełna przebudowa, a nie podmiana samego urządzenia.</b> Nowe
    /// urządzenie może pracować z inną częstotliwością próbkowania i inną liczbą kanałów, a od
    /// nich zależy rozmiar bufora pierścieniowego, długość wyciszenia, współczynniki filtrów
    /// korektora i pozycja liczona w ramkach. Podmiana samego uchwytu zostawiłaby te wartości
    /// policzone dla poprzedniego urządzenia, więc tor jest budowany od nowa. Odtwarzany utwór
    /// wraca na swoje miejsce razem z pozycją, a wstrzymany pozostaje wstrzymany.</para>
    /// </summary>
    public void SwitchDevice(int deviceIndex)
    {
        PreferredDeviceIndex = deviceIndex;
        Reopen();
    }

    /// <summary>
    /// Otwiera urządzenie na nowo z bieżącymi ustawieniami, zachowując utwór i pozycję.
    /// Rozmiar okresu podaje się przy tworzeniu urządzenia i nie da się go zmienić później,
    /// więc zmiana opóźnienia przechodzi tą samą drogą co zmiana urządzenia.
    /// </summary>
    public void Reopen()
    {
        // Nic jeszcze nie zostało otwarte: wystarczy zapamiętać wybór.
        if (_device is null) return;

        var wasPlaying = State == PlaybackState.Playing;
        var index = _current.QueueIndex;
        var position = Position;

        TearDown();
        Initialise();

        if (index < 0) return;

        PlayIndex(index);
        if (position > TimeSpan.FromSeconds(0.5)) SeekTo(position);
        if (!wasPlaying) Pause();
    }

    /// <summary>Stops the decode thread and closes the device, leaving the queue intact.</summary>
    private void TearDown()
    {
        _feederRunning = false;
        _feeder?.Join(1000);
        _feeder = null;

        lock (_sync)
        {
            _device?.Dispose();
            _device = null;
            State = PlaybackState.Stopped;
            _seekTargetIndex = -1;
        }

        CloseDecoder();

        _ring = null;
        _playingEntry = null;
        _decodingIndex = -1;
        _current = new TrackBoundary(0, -1, 0);

        // Znaczniki granic opisują pozycje w buforze, którego już nie ma.
        lock (_boundaryLock) _boundaries.Clear();

        _seekPending = false;
        _seekInProgress = false;
        _producerParked = false;
        _flushRequested = false;
        _flushDone = false;
        _finished = false;
    }

    // ================= kolejka =================

    /// <summary>Replaces the queue and begins at <paramref name="startIndex"/>.</summary>
    public void SetQueue(IEnumerable<string> paths, int startIndex = 0)
    {
        lock (_sync)
        {
            _queue.Clear();
            foreach (var path in paths) _queue.Add(new QueueEntry { Path = path });
        }

        if (_queue.Count > 0) PlayIndex(Math.Clamp(startIndex, 0, _queue.Count - 1));
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        lock (_sync)
        {
            foreach (var path in paths) _queue.Add(new QueueEntry { Path = path });
        }
    }

    /// <summary>
    /// Removes an entry. Removing the track that is playing stops it; removing any other
    /// leaves playback untouched, which is what makes tidying the queue mid-listen safe.
    /// </summary>
    public void RemoveAt(int index)
    {
        var wasPlaying = false;

        lock (_sync)
        {
            if (index < 0 || index >= _queue.Count) return;

            wasPlaying = ReferenceEquals(_queue[index], _playingEntry);
            _queue.RemoveAt(index);
        }

        if (wasPlaying)
        {
            _playingEntry = null;
            Stop();
        }
        else
        {
            ReindexAfterMutation();
        }
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
        }

        ReindexAfterMutation();
    }

    /// <summary>
    /// Re-derives the position of the playing track from the entry itself rather than from
    /// its old index. Any arithmetic on indices would have to account for every way the list
    /// can be edited; asking the list where the object now sits cannot go wrong.
    /// </summary>
    private void ReindexAfterMutation()
    {
        int index;
        lock (_sync)
        {
            index = _playingEntry is null ? -1 : _queue.IndexOf(_playingEntry);
        }

        if (index < 0) return;

        _decodingIndex = index;
        _current = _current with { QueueIndex = index };

        ThreadPool.UnsafeQueueUserWorkItem(_ => TrackChanged?.Invoke(index), null);
    }

    /// <summary>Paths in queue order, for saving between runs.</summary>
    public IReadOnlyList<string> QueuePaths
    {
        get { lock (_sync) return _queue.Select(entry => entry.Path).ToArray(); }
    }

    public void ClearQueue()
    {
        Stop();
        lock (_sync) _queue.Clear();
    }

    // ================= sterowanie =================

    public void PlayIndex(int index)
    {
        Initialise();

        lock (_sync)
        {
            if (index < 0 || index >= _queue.Count) return;
            _seekTargetIndex = index;
            Interlocked.Exchange(ref _seekTargetFrame, 0);
            _finished = false;
        }

        RequestFlush();
        Play();
    }

    public void Play()
    {
        Initialise();

        lock (_sync)
        {
            if (_queue.Count == 0) return;

            // Nic jeszcze nie gra i nic nie jest zaplanowane: zacznij od poczatku.
            if (_current.QueueIndex < 0 && _seekTargetIndex < 0)
            {
                _seekTargetIndex = 0;
                Interlocked.Exchange(ref _seekTargetFrame, 0);
                RequestFlush();
            }

            _device?.Start();
            State = PlaybackState.Playing;
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (State != PlaybackState.Playing) return;
            _device?.Stop();
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
            _device?.Stop();
            State = PlaybackState.Stopped;
            _seekTargetIndex = -1;

            // Bez wyzerowania celu zatrzymanie odtwarzania pokazywaloby przez chwile
            // polozenie z ostatniego przewiniecia - Position czyta go, dopoki trwa uzgodnienie.
            Interlocked.Exchange(ref _seekTargetFrame, 0);
        }

        RequestFlush();
        _current = new TrackBoundary(0, -1, 0);
    }

    public void Next() => Step(1);

    /// <summary>Rewinds first; only jumps back when already near the start of the track.</summary>
    public void Previous()
    {
        if (Position > TimeSpan.FromSeconds(3))
        {
            SeekTo(TimeSpan.Zero);
            return;
        }

        Step(-1);
    }

    private void Step(int delta)
    {
        int target;
        lock (_sync)
        {
            if (_queue.Count == 0) return;

            var from = _current.QueueIndex >= 0 ? _current.QueueIndex : 0;

            if (Shuffle && _queue.Count > 1)
            {
                target = PickRandomOther(from);
            }
            else
            {
                target = from + delta;
                if (target < 0) target = _queue.Count - 1;
                if (target >= _queue.Count) target = 0;
            }
        }

        PlayIndex(target);
    }

    /// <summary>
    /// Picks a different entry at random. Repeating the track that is already playing would
    /// read as a broken button, so the current index is excluded rather than merely unlikely.
    /// </summary>
    private int PickRandomOther(int current)
    {
        var choice = Random.Shared.Next(_queue.Count - 1);
        return choice >= current ? choice + 1 : choice;
    }

    public void SeekTo(TimeSpan position)
    {
        lock (_sync)
        {
            if (_current.QueueIndex < 0) return;

            _seekTargetIndex = _current.QueueIndex;
            Interlocked.Exchange(
                ref _seekTargetFrame, Math.Max(0, (long)(position.TotalSeconds * SampleRate)));
        }

        RequestFlush();
    }

    private void RequestFlush()
    {
        _seekPending = true;

        // Gdy urzadzenie nie gra, wywolanie zwrotne nigdy nie nastapi, wiec czyszczenie
        // musi wykonac ktos inny - wtedy jest to bezpieczne, bo nikt nie czyta bufora.
        if (_device is { IsRunning: false })
        {
            _ring?.DiscardAll();
            _flushDone = true;
        }
    }

    // ================= wątek dźwiękowy =================

    private void Render(Span<float> buffer)
    {
        var ring = _ring;
        if (ring is null) { buffer.Clear(); return; }

        var frames = buffer.Length / Channels;

        if (_flushRequested)
        {
            // Wyciszenie tego, co jeszcze w buforze, zeby przejscie nie trzasnelo.
            var faded = ring.Read(buffer, frames);
            ApplyFadeOut(buffer, faded);
            if (faded < frames) buffer[(faded * Channels)..].Clear();

            ring.DiscardAll();
            _flushRequested = false;
            _flushDone = true;
            _fadingIn = true;
            _fadeCounter = 0;
            return;
        }

        var delivered = ring.Read(buffer, frames);

        if (delivered < frames)
        {
            // Niedobór: albo dekoder nie nadąża, albo kolejka się skończyła.
            buffer[(delivered * Channels)..].Clear();
        }

        AdvanceBoundaries(ring.TotalRead);

        if (_fadingIn) ApplyFadeIn(buffer, delivered);

        Graph.Process(buffer, frames);
    }

    private void ApplyFadeOut(Span<float> buffer, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var gain = 1f - Math.Min(1f, i / (float)_fadeLength);
            for (var c = 0; c < Channels; c++) buffer[i * Channels + c] *= gain;
        }
    }

    private void ApplyFadeIn(Span<float> buffer, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            if (_fadeCounter >= _fadeLength) { _fadingIn = false; break; }

            var gain = _fadeCounter / (float)_fadeLength;
            for (var c = 0; c < Channels; c++) buffer[i * Channels + c] *= gain;
            _fadeCounter++;
        }
    }

    /// <summary>Moves the "current track" marker once playback has crossed a boundary.</summary>
    private void AdvanceBoundaries(long framesRead)
    {
        while (true)
        {
            TrackBoundary next;
            lock (_boundaryLock)
            {
                if (_boundaries.Count == 0) return;

                next = _boundaries.Peek();
                if (framesRead < next.AbsoluteFrame) return;

                _boundaries.Dequeue();
            }

            _current = next;

            // Zdarzenia nigdy z wątku dźwiękowego.
            var index = next.QueueIndex;
            ThreadPool.UnsafeQueueUserWorkItem(_ => TrackChanged?.Invoke(index), null);
        }
    }

    // ================= wątek dekodujący =================

    private void FeederLoop()
    {
        while (_feederRunning)
        {
            try
            {
                if (_seekPending) { HandleSeek(); continue; }

                var ring = _ring;
                if (ring is null) { Thread.Sleep(5); continue; }

                if (_decoder is null && !OpenNextTrack()) { Thread.Sleep(10); continue; }
                if (ring.SpaceAvailable < DecodeChunkFrames) { Thread.Sleep(3); continue; }

                PumpOneChunk(ring);
            }
            catch (Exception ex)
            {
                // Wątek dekodujący nie może zginąć — to zatrzymałoby odtwarzanie na zawsze.
                Console.Error.WriteLine($"[cewka] błąd wątku dekodującego: {ex.Message}");
                CloseDecoder();
                Thread.Sleep(50);
            }
        }
    }

    private void HandleSeek()
    {
        _seekInProgress = true;

        try
        {
            SeekNow();
        }
        finally
        {
            _seekInProgress = false;
        }
    }

    private void SeekNow()
    {
        // 1. Przestań pisać i poczekaj, aż wątek dźwiękowy opróżni bufor.
        _producerParked = true;
        _flushDone = false;
        _flushRequested = true;

        var waited = 0;
        while (!_flushDone && waited < 500)
        {
            if (_device is { IsRunning: false }) { _ring?.DiscardAll(); break; }
            Thread.Sleep(1);
            waited++;
        }

        _flushRequested = false;

        // 2. Ustaw pozycję.
        int index;
        long frame;
        lock (_sync)
        {
            index = _seekTargetIndex;
            frame = _seekTargetFrame;
            _seekPending = false;
        }

        lock (_boundaryLock) _boundaries.Clear();

        if (index < 0)
        {
            CloseDecoder();
            _current = new TrackBoundary(_ring?.TotalWritten ?? 0, -1, 0);
            _producerParked = false;
            return;
        }

        // Numer pozycji nie jest tożsamością utworu.
        //
        // Zastąpienie kolejki nowym plikiem wstawia go pod numer 0 — czyli ten sam, pod którym
        // stał utwór właśnie odtwarzany. Sam numer się wtedy nie zmienia, więc dekoder zostawał
        // otwarty na poprzednim pliku i był tylko przewijany na początek: stary utwór grał od
        // nowa, choć kolejka pokazywała już nowy. Rozstrzyga porównanie z wpisem, który dekoder
        // faktycznie otworzył — tak samo jak w RemoveAt, gdzie ta sama wątpliwość wraca.
        if (index != _decodingIndex || !ReferenceEquals(_playingEntry, EntryAt(index)))
        {
            CloseDecoder();
            _decodingIndex = index;
        }

        if (_decoder is null && !OpenTrack(index)) { _producerParked = false; return; }

        _decoder!.Seek(frame);

        var boundary = new TrackBoundary(_ring?.TotalWritten ?? 0, index, _decoder.TotalFrames, frame);
        _current = boundary;
        ThreadPool.UnsafeQueueUserWorkItem(_ => TrackChanged?.Invoke(index), null);

        _producerParked = false;
    }

    private void PumpOneChunk(PcmRingBuffer ring)
    {
        if (_producerParked || _decoder is null) { Thread.Sleep(2); return; }

        var needed = DecodeChunkFrames * Channels;
        if (_decodeBuffer.Length < needed) _decodeBuffer = new float[needed];

        var frames = _decoder.Read(_decodeBuffer.AsSpan(0, needed));

        if (frames == 0)
        {
            AdvanceToNextTrack();
            return;
        }

        var written = 0;
        while (written < frames && _feederRunning && !_seekPending)
        {
            var accepted = ring.Write(
                _decodeBuffer.AsSpan(written * Channels, (frames - written) * Channels),
                frames - written);

            if (accepted == 0) { Thread.Sleep(2); continue; }
            written += accepted;
        }
    }

    /// <summary>
    /// The current file has run out. Opening the next one here — while the ring still holds
    /// half a second of audio — is what makes the change of track inaudible.
    /// </summary>
    private void AdvanceToNextTrack()
    {
        CloseDecoder();

        int next;
        lock (_sync)
        {
            if (_queue.Count == 0) { Finish(); return; }

            if (Repeat == RepeatMode.Track)
            {
                next = _decodingIndex;
            }
            else if (Shuffle && _queue.Count > 1)
            {
                // W trybie losowym kolejka nie ma konca, wiec nie ma tez czego zapetlac.
                next = PickRandomOther(_decodingIndex);
            }
            else
            {
                next = _decodingIndex + 1;

                if (next >= _queue.Count)
                {
                    if (Repeat != RepeatMode.Queue) { Finish(); return; }
                    next = 0;
                }
            }
        }

        _decodingIndex = next;
        OpenTrack(next);
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;

        _decodingIndex = -1;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            Pause();
            QueueFinished?.Invoke();
        }, null);
    }

    private bool OpenNextTrack()
    {
        int index;
        lock (_sync)
        {
            if (_queue.Count == 0) return false;
            index = _decodingIndex >= 0 ? _decodingIndex : 0;
            if (index >= _queue.Count) return false;
        }

        _decodingIndex = index;
        return OpenTrack(index);
    }

    /// <summary>
    /// Opens a queue entry and records where its audio begins in the ring. Entries that
    /// cannot be decoded are marked and skipped rather than stopping playback.
    /// </summary>
    private bool OpenTrack(int index)
    {
        QueueEntry entry;
        lock (_sync)
        {
            if (index < 0 || index >= _queue.Count) return false;
            entry = _queue[index];
        }

        try
        {
            entry.EnsureMetadata();

            if (entry.IsUnsupported)
            {
                ReportFailure(index, entry.UnsupportedReason ?? "Format nieobsługiwany.");
                return SkipToFollowing(index);
            }

            _decoder = DecoderFactory.Open(entry.Path, SampleRate, Channels);
            _decodingIndex = index;
            _playingEntry = entry;

            lock (_boundaryLock)
            {
                _boundaries.Enqueue(new TrackBoundary(
                    _ring?.TotalWritten ?? 0, index, _decoder.TotalFrames));
            }

            Graph.OnTrackChanged(entry);
            return true;
        }
        catch (Exception ex)
        {
            entry.IsUnsupported = true;
            entry.UnsupportedReason = ex is AudioException ? ex.Message : $"Nie udało się otworzyć pliku — {ex.Message}";
            ReportFailure(index, entry.UnsupportedReason);
            return SkipToFollowing(index);
        }
    }

    private bool SkipToFollowing(int index)
    {
        int count;
        lock (_sync) count = _queue.Count;

        if (index + 1 >= count) { Finish(); return false; }

        _decodingIndex = index + 1;
        return OpenTrack(index + 1);
    }

    private void ReportFailure(int index, string reason) =>
        ThreadPool.UnsafeQueueUserWorkItem(_ => TrackFailed?.Invoke(index, reason), null);

    /// <summary>Wpis stojący pod danym numerem, albo <c>null</c>, gdy numer wychodzi poza kolejkę.</summary>
    private QueueEntry? EntryAt(int index)
    {
        lock (_sync) return index >= 0 && index < _queue.Count ? _queue[index] : null;
    }

    private void CloseDecoder()
    {
        _decoder?.Dispose();
        _decoder = null;
    }

    public void Dispose() => TearDown();

    /// <summary>
    /// Marks the frame in the ring at which a queue entry's audio begins.
    /// <para>
    /// <paramref name="StartFrame"/> says where inside the track that audio starts. It is zero
    /// for a track played from its beginning and equal to the seek target after a seek —
    /// without it the position would be counted from the moment of the seek, so the display
    /// would return to 0:00 while the music carried on from the middle.
    /// </para>
    /// </summary>
    private readonly record struct TrackBoundary(
        long AbsoluteFrame, int QueueIndex, long TotalFrames, long StartFrame = 0);
}
