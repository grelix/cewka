using Cewka.Audio.Decoding;
using Cewka.Audio.Dsp;

namespace Cewka.Audio.Playback;

/// <summary>
/// Przenikanie utworów: przy samoczynnym przejściu koniec bieżącego utworu nakłada się na początek
/// następnego.
///
/// <para>Nakładka powstaje w wątku dekodującym z dwóch dekoderów naraz i trafia do bufora jako zwykły
/// dźwięk, więc wątek dźwiękowy niczego nie miesza. Granica następnego utworu stoi na początku nakładki:
/// od niej następny jest bieżącym, a jego pozycja liczy się od zera. Wzmocnienie wyrównania nakłada wątek
/// dźwiękowy dla bieżącego utworu, dlatego końcówka poprzedniego jest skalowana stosunkiem obu wzmocnień.</para>
///
/// <para>Przewinięcie, zmiana utworu i każda zmiana kolejki idą drogą przewinięcia, która porzuca
/// wyciszany utwór; po nim nakładka już nie wraca.</para>
/// </summary>
public sealed partial class PlaybackEngine
{
    private long _crossfadeTicks;
    private volatile bool _albumsWithoutCrossfade = true;

    /// <summary>
    /// Czas przenikania przy samoczynnym przejściu do następnego pliku; zero wyłącza. Zmiana działa od
    /// następnego przejścia: trwająca nakładka kończy się z czasem, z jakim się zaczęła.
    /// </summary>
    public TimeSpan Crossfade
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _crossfadeTicks));
        set => Interlocked.Exchange(ref _crossfadeTicks, Math.Max(0, value.Ticks));
    }

    /// <summary>Następca na albumie (także kolejna ścieżka arkusza) przychodzi bez przerwy i bez przenikania.</summary>
    public bool AlbumsWithoutCrossfade
    {
        get => _albumsWithoutCrossfade;
        set => _albumsWithoutCrossfade = value;
    }

    private long OrderVersionLocked() => _orderVersion + _shuffleOrder.Version;

    /// <summary>
    /// Najdłuższa nakładka dla utworu: wybrany czas, najwyżej połowa utworu. Zero, gdy przenikanie
    /// jest wyłączone albo długość utworu nieznana.
    /// </summary>
    private long CrossfadeWindow(IAudioDecoder decoder)
    {
        var span = (long)(Crossfade.TotalSeconds * SampleRate);
        if (span <= 0) return 0;

        var total = PlausibleLength(decoder.TotalFrames, SampleRate);
        return total <= 0 ? 0 : Math.Min(span, total / 2);
    }

    /// <summary>Ramki do miejsca, w którym może zacząć się przenikanie; zero, gdy już tam jest; −1, gdy go nie będzie.</summary>
    private long CrossfadeLead(IAudioDecoder decoder)
    {
        var window = CrossfadeWindow(decoder);
        if (window <= 0) return -1;

        var remaining = PlausibleLength(decoder.TotalFrames, SampleRate) - decoder.Position;
        return Math.Max(0, remaining - window);
    }

    /// <summary>
    /// Otwiera następny utwór obok dekodowanego, tą samą drogą co przejście bez przerwy: z pomijaniem
    /// wpisów nie do otwarcia i bez otwierania koła wpisów bez dźwięku. Bez przenikania zostaje koniec
    /// kolejki, wpis grany spoza kolejki i następca na albumie; wtedy utwór gra do końca jak dotąd.
    /// Rozstrzygnięcie obowiązuje do zmiany kolejki, kolejności albo trybu powtarzania.
    /// </summary>
    /// <returns>True, gdy nakładka czeka już w <see cref="FeederSession.Overlap"/>.</returns>
    private bool TryBeginCrossfade(FeederSession session)
    {
        var outgoing = session.Decoder!;
        var finished = session.Entry;
        if (finished is null || finished.IsLive || _seekPending) return false;

        QueueEntry? candidate = null;
        long version;
        var stale = false;

        lock (_sync)
        {
            if (session.Verdict != CrossfadeVerdict.None && session.VerdictVersion == OrderVersionLocked()) return false;

            if (_queue.Contains(finished))
                candidate = _repeat == RepeatMode.Track ? finished : FollowingEntryLocked(finished, userInitiated: false);

            version = OrderVersionLocked();
        }

        if (candidate is null) return Decline(session, CrossfadeVerdict.Declined, version, stale);

        // Utwór, który już coś zagrał, kończy serię wpisów bez dźwięku, jak przy zwykłym końcu.
        var outgoingProduced = session.ProducedSound;
        if (outgoingProduced) session.Barren.Clear();

        int attempts;
        lock (_sync) attempts = _queue.Count;

        var opened = false;
        try
        {
            for (; candidate is not null && attempts > 0 && !session.Cancelled && !_seekPending; attempts--)
            {
                if (_albumsWithoutCrossfade && IsAlbumSuccessor(finished, candidate))
                    return Decline(session, CrossfadeVerdict.Declined, version, stale);

                lock (_sync)
                {
                    if (!_queue.Contains(candidate) || ClosesBarrenCircleLocked(session, candidate)) break;
                    session.Entry = candidate;
                }

                session.Decoder = null;
                if (TryOpen(session, candidate, out var failure))
                {
                    opened = true;
                    if (!outgoingProduced) session.Barren.Add(finished);
                    StartOverlap(session, outgoing, finished, candidate);
                    return true;
                }

                session.Decoder = outgoing;
                session.ProducedSound = outgoingProduced;
                lock (_sync) session.Entry = finished;

                ReportFailure(session, candidate, failure);
                session.Barren.Add(candidate);

                lock (_sync)
                {
                    stale |= OrderVersionLocked() != version;
                    candidate = _queue.Contains(candidate) ? FollowingEntryLocked(candidate, userInitiated: false) : null;
                    version = OrderVersionLocked();
                }
            }
        }
        finally
        {
            if (!opened && session.Decoder is null)
            {
                session.Decoder = outgoing;
                lock (_sync) session.Entry = finished;
            }
        }

        if (session.Cancelled || _seekPending) return false;

        // Nic się nie otworzyło: po tym utworze kolejka się kończy, jak przy przejściu bez przerwy.
        return Decline(session, CrossfadeVerdict.Exhausted, version, stale);
    }

    /// <summary>
    /// Zapisuje odmowę przenikania. Zmiana kolejki w trakcie rozstrzygania (inna niż wywołana przez
    /// samo wyznaczanie następnego) unieważnia ją od razu.
    /// </summary>
    private bool Decline(FeederSession session, CrossfadeVerdict verdict, long version, bool stale)
    {
        lock (_sync) session.VerdictVersion = stale || OrderVersionLocked() != version ? -1 : version;
        session.Verdict = verdict;
        return false;
    }

    /// <summary>Następca na albumie; ten sam wpis (powtarzanie utworu) nim nie jest.</summary>
    private static bool IsAlbumSuccessor(QueueEntry previous, QueueEntry next)
    {
        if (ReferenceEquals(previous, next)) return false;

        try
        {
            next.EnsureMetadata();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Błąd odczytu zgłosi otwarcie pliku; bez znaczników wpis nie należy do albumu.
            return false;
        }

        return AlbumIdentity.IsSuccessor(previous, next);
    }

    /// <summary>
    /// Długość nakładki: wybrany czas, najwyżej połowa krótszego z utworów i reszta wyciszanego.
    /// Do jej początku wyciszany gra sam.
    /// </summary>
    private void StartOverlap(FeederSession session, IAudioDecoder outgoing, QueueEntry outgoingEntry, QueueEntry incomingEntry)
    {
        // Sesja przejmuje wyciszany dekoder, zanim cokolwiek może rzucić (źródło wzmocnienia, dekodery):
        // wyjątek idzie wtedy zwykłą drogą błędu pętli dekodującej, która zamyka oba dekodery, i jej
        // zachowanie się nie zmienia.
        var overlap = new CrossfadeOverlap(outgoing, outgoingEntry, session.EntryGain, incomingEntry);
        session.Overlap = overlap;

        var incoming = session.Decoder!;
        var incomingLength = PlausibleLength(incoming.TotalFrames, SampleRate);
        var remaining = Math.Max(0, PlausibleLength(outgoing.TotalFrames, SampleRate) - outgoing.Position);

        var length = Math.Min(CrossfadeWindow(outgoing), remaining);
        if (incomingLength > 0) length = Math.Min(length, incomingLength / 2);

        // Wyciszany utwór wypłukuje końcówkę konwersji sam, jak przy zwykłym końcu.
        if (outgoing is IContinuousDecoder continuous) continuous.HoldTail = false;

        overlap.IncomingGain = GainFor(incomingEntry);
        overlap.Solo = remaining - length;
        overlap.Length = Math.Max(0, length);
        overlap.IncomingLength = incomingLength;
    }

    /// <summary>
    /// Jedna porcja przenikania: najpierw reszta wyciszanego utworu przed nakładką, potem suma obu
    /// utworów z krzywymi o stałej mocy (cos dla wyciszanego, sin dla narastającego).
    /// </summary>
    private void PumpOverlap(FeederSession session)
    {
        var overlap = session.Overlap!;
        var channels = Channels;
        var needed = DecodeChunkFrames * channels;
        if (session.Buffer.Length < needed) session.Buffer = new float[needed];
        if (session.FadeBuffer.Length < needed) session.FadeBuffer = new float[needed];

        if (!overlap.Begun)
        {
            if (overlap.Solo > 0 || overlap.Length == 0)
            {
                // Bez nakładki (utwór za krótki) wyciszany gra do swojego końca, a następny po nim bez przerwy.
                var want = overlap.Solo > 0 ? (int)Math.Min(DecodeChunkFrames, overlap.Solo) : DecodeChunkFrames;
                var solo = ReadOutgoing(overlap, session.Buffer.AsSpan(0, want * channels));
                if (solo == 0)
                {
                    // Utwór skończył się przed nakładką: następny zaczyna się zaraz po nim.
                    JoinAfterOutgoing(session, overlap);
                    return;
                }

                var produced = session.ProducedSound;
                WriteToRing(session, solo);
                session.ProducedSound = produced;
                overlap.Solo -= solo;
                return;
            }

            BeginOverlap(session, overlap);
        }

        var step = (int)Math.Min(DecodeChunkFrames, overlap.Length - overlap.Done);
        var mixed = session.Buffer.AsSpan(0, step * channels);
        var got = ReadFully(session.Decoder!, mixed);
        mixed[(got * channels)..].Clear();

        var fading = session.FadeBuffer.AsSpan(0, step * channels);
        var left = ReadOutgoing(overlap, fading);
        fading[(left * channels)..].Clear();

        // Oba utwory skończyły się przed końcem nakładki; cisza nie ma po co trafiać do bufora.
        if (got == 0 && left == 0)
        {
            DropOverlap(session);
            return;
        }

        // Stosunek dochodzi w obrębie porcji od poprzedniej wartości do bieżącej, bez skoku próbki.
        var from = overlap.Ratio;
        var to = CurrentRatio(overlap);
        overlap.Ratio = to;

        // Gdy wyciszany skończy się wcześniej, następny narasta dalej sam po swojej krzywej.
        for (var i = 0; i < step; i++)
        {
            var angle = (overlap.Done + i) * (Math.PI / 2) / overlap.Length;
            var ratio = from + (to - from) * (i + 1) / step;
            var fadeIn = (float)Math.Sin(angle);
            var fadeOut = (float)(Math.Cos(angle) * ratio);

            for (var c = 0; c < channels; c++)
            {
                var k = i * channels + c;
                mixed[k] = mixed[k] * fadeIn + fading[k] * fadeOut;
            }
        }

        // Dźwięk w nakładce należy do następnego utworu tylko wtedy, gdy on sam coś oddał.
        var before = session.ProducedSound;
        WriteToRing(session, step);
        session.ProducedSound = before || got > 0;

        overlap.Done += step;

        // Koniec nakładki: reszta wyciszanego, jeśli była dłuższy, niż podawał, przepada.
        if (overlap.Done >= overlap.Length) DropOverlap(session);
    }

    /// <summary>Granica następnego utworu na początku nakładki; wzmocnienie ustawia się na niej od razu.</summary>
    private void BeginOverlap(FeederSession session, CrossfadeOverlap overlap)
    {
        overlap.Begun = true;

        overlap.OutgoingApplied = AppliedGain(overlap.OutgoingEntry, overlap.OutgoingGain);
        overlap.IncomingApplied = overlap.IncomingGain;
        overlap.SeenRefresh = _refreshedGain;
        overlap.Ratio = CurrentRatio(overlap);

        session.EntryGain = overlap.IncomingGain;
        var boundary = new TrackBoundary(
            session.Ring.TotalWritten, overlap.IncomingEntry, overlap.IncomingLength, 0, overlap.IncomingGain, Overlap: true);

        lock (session.BoundaryLock) session.Boundaries.Enqueue(boundary);
    }

    /// <summary>Przejście bez nakładki, gdy wyciszany utwór skończył się, zanim się zaczęła.</summary>
    private void JoinAfterOutgoing(FeederSession session, CrossfadeOverlap overlap)
    {
        DropOverlap(session);

        session.EntryGain = overlap.IncomingGain;
        var boundary = new TrackBoundary(
            session.Ring.TotalWritten, overlap.IncomingEntry, overlap.IncomingLength, 0, overlap.IncomingGain);

        lock (session.BoundaryLock) session.Boundaries.Enqueue(boundary);
    }

    /// <summary>Wzmocnienie, z jakim tor gra wpis: odczytane od nowa po zmianie ustawień albo zapisane w granicy.</summary>
    private TrackGain? AppliedGain(QueueEntry entry, TrackGain? recorded) =>
        _refreshedGain is { } refreshed && ReferenceEquals(refreshed.Entry, entry) && ReferenceEquals(_current.Entry, entry)
            ? refreshed.Gain
            : recorded;

    /// <summary>
    /// Stosunek wzmocnienia, z jakim ma grać wyciszany utwór, do wzmocnienia, z jakim tor gra teraz
    /// narastający; jeden przy wyłączonym wyrównaniu. Odświeżenie wzmocnienia narastającego (zmiana
    /// ustawień albo koniec pomiaru) odczytuje od nowa także wyciszany, żeby oba wynikały z tych samych
    /// ustawień. Próbki nakładki, które w chwili zmiany leżą już w buforze, zostają ze starym stosunkiem.
    /// </summary>
    private float CurrentRatio(CrossfadeOverlap overlap)
    {
        var refreshed = _refreshedGain;
        if (refreshed is not null && !ReferenceEquals(refreshed, overlap.SeenRefresh)
            && ReferenceEquals(refreshed.Entry, overlap.IncomingEntry))
        {
            overlap.IncomingApplied = refreshed.Gain;
            overlap.OutgoingApplied = GainFor(overlap.OutgoingEntry);
        }

        overlap.SeenRefresh = refreshed;

        if (!Graph.NormalisationEnabled) return 1;

        var outgoing = overlap.OutgoingApplied?.GainDecibels ?? 0;
        var incoming = overlap.IncomingApplied?.GainDecibels ?? 0;
        return (float)(Linear(outgoing) / Linear(incoming));
    }

    /// <summary>Tak samo jak <see cref="GainStage.TargetDecibels"/>, razem z granicami.</summary>
    private static double Linear(double decibels) => Math.Pow(10, Math.Clamp(decibels, -60, 18) / 20);

    /// <summary>
    /// Czyta do pełna albo do końca utworu. W nakładce niepełny odczyt dałby dołek w jednym
    /// z utworów, a nie tylko później gotowe dane.
    /// </summary>
    private int ReadFully(IAudioDecoder decoder, Span<float> destination)
    {
        var channels = Channels;
        var wanted = destination.Length / channels;
        var got = 0;

        while (got < wanted)
        {
            var frames = decoder.Read(destination[(got * channels)..]);
            if (frames <= 0) break;
            got += frames;
        }

        return got;
    }

    /// <summary>
    /// Odczyt wyciszanego utworu; po jego końcu albo błędzie dekoder jest zamykany, a utwór milknie.
    /// Błąd końcówki nie przerywa następnego utworu, który już jest bieżący.
    /// </summary>
    private int ReadOutgoing(CrossfadeOverlap overlap, Span<float> destination)
    {
        var decoder = overlap.Outgoing;
        if (decoder is null) return 0;

        int got;
        try
        {
            got = ReadFully(decoder, destination);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[cewka] błąd końcówki utworu w przenikaniu: {ex.Message}");
            got = 0;
        }

        if (got * Channels < destination.Length)
        {
            decoder.Dispose();
            overlap.Outgoing = null;
        }

        return got;
    }

    private static void DropOverlap(FeederSession session)
    {
        var overlap = session.Overlap;
        if (overlap is null) return;

        session.Overlap = null;
        overlap.Outgoing?.Dispose();
        overlap.Outgoing = null;
    }

    /// <summary>Co wyszło z próby przenikania dla dekodowanego utworu.</summary>
    private enum CrossfadeVerdict
    {
        /// <summary>Jeszcze nie rozstrzygano.</summary>
        None,

        /// <summary>Bez przenikania; utwór gra do końca, a następny po nim jak dotąd.</summary>
        Declined,

        /// <summary>Żadnego następnego nie dało się otworzyć; po utworze kolejka się kończy.</summary>
        Exhausted,
    }

    /// <summary>Stan nakładki; tylko wątek dekodujący.</summary>
    private sealed class CrossfadeOverlap(
        IAudioDecoder outgoing, QueueEntry outgoingEntry, TrackGain? outgoingGain, QueueEntry incomingEntry)
    {
        /// <summary>Dekoder wyciszanego utworu; null, gdy już się skończył.</summary>
        public IAudioDecoder? Outgoing = outgoing;

        public QueueEntry OutgoingEntry { get; } = outgoingEntry;
        public TrackGain? OutgoingGain { get; } = outgoingGain;
        public QueueEntry IncomingEntry { get; } = incomingEntry;
        public TrackGain? IncomingGain { get; set; }

        /// <summary>Ramki wyciszanego utworu do początku nakładki.</summary>
        public long Solo;

        /// <summary>Długość nakładki w ramkach; zero, gdy któryś utwór jest na nią za krótki.</summary>
        public long Length;

        public long Done;
        public long IncomingLength;

        /// <summary>Granica następnego utworu jest już w buforze.</summary>
        public bool Begun;

        /// <summary>Stosunek wzmocnień wyrównania wyciszanego do narastającego, liniowo, na końcu ostatniej porcji.</summary>
        public float Ratio = 1;

        /// <summary>Wzmocnienie, z jakim ma grać wyciszany utwór.</summary>
        public TrackGain? OutgoingApplied;

        /// <summary>Wzmocnienie, z jakim tor gra narastający utwór.</summary>
        public TrackGain? IncomingApplied;

        /// <summary>Ostatnio widziane odświeżenie wzmocnienia; nowe obiekty oznaczają zmianę ustawień.</summary>
        public object? SeenRefresh;
    }
}
