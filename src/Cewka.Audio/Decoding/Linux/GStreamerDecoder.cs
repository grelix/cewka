using System.Diagnostics;
using System.Runtime.Versioning;

namespace Cewka.Audio.Decoding.Linux;

/// <summary>
/// AAC, M4A i ALAC pod Linuksem przez GStreamer. Potok <c>filesrc → decodebin → audioconvert →
/// appsink</c> oddaje próbki zmiennoprzecinkowe w formacie pliku, a dopasowanie do urządzenia
/// robi wspólna klasa bazowa.
///
/// <para><c>sync=false</c> na odbiorniku jest konieczne, inaczej próbki przychodziłyby w tempie
/// odtwarzania, a dekoder ma je wyprzedzać.</para>
///
/// <para>Pułapka: błąd dekodera za kolejką w <c>decodebin</c> zatrzymuje potok bez EOS, więc
/// blokujące <c>gst_app_sink_pull_sample</c> czekałoby bez końca. Próbki pobierane są z limitem
/// czasu, a między próbami odczytywana jest szyna potoku.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class GStreamerDecoder : ManagedDecoderBase
{
    /// <summary>
    /// Nazwy elementów są potrzebne, żeby po zbudowaniu potoku odnaleźć źródło i odbiornik.
    /// Ścieżka pliku nie trafia do tego opisu — wstawiona w tekst wymagałaby cytowania zgodnego
    /// ze składnią GStreamera, a każdy plik z cudzysłowem w nazwie byłby wtedy usterką.
    /// </summary>
    private const string PipelineDescription =
        "filesrc name=source ! decodebin ! audioconvert ! audioresample ! " +
        "appsink name=sink sync=false max-buffers=16 caps=audio/x-raw,format=F32LE,layout=interleaved";

    /// <summary>Łączny czas oczekiwania na próbkę, po którym potok uznaje się za zablokowany.</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(10);

    private static readonly Lock StartupLock = new();
    private static bool _started;

    /// <summary>Chroni uchwyty przed zwolnieniem w trakcie odczytu z wątku dekodującego.</summary>
    private readonly Lock _gate = new();
    private volatile bool _closing;

    private nint _pipeline;
    private nint _sink;
    private nint _bus;

    private nint _sample;
    private Gst.MapInfo _map;
    private bool _mapped;
    private int _pendingFrames;
    private int _pendingOffset;
    private bool _endOfStream;
    private DecoderFailure _failure = DecoderFailure.DamagedFile;
    private (int Rate, int Channels)? _formatChange;

    public GStreamerDecoder(string path, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        EnsureStarted();

        _pipeline = Gst.ParseLaunch(PipelineDescription, out var error);
        if (_pipeline == nint.Zero)
        {
            DecoderFailures.Log($"GStreamer nie zbudował potoku: {Gst.TakeMessage(error)}");
            throw DecoderFailures.Create(DecoderFailure.SystemCodecsUnavailable);
        }

        if (error != nint.Zero) Gst.ErrorFree(error);

        try
        {
            Open(path, outputSampleRate, outputChannels, meter);
        }
        catch
        {
            // Obiekt nie powstał, więc nikt inny nie zatrzyma potoku i nie zamknie pliku.
            Dispose();
            throw;
        }
    }

    private void Open(string path, int outputSampleRate, int outputChannels, BitrateMeter? meter)
    {
        _bus = Gst.ElementGetBus(_pipeline);

        var source = Gst.BinGetByName(_pipeline, "source");
        if (source == nint.Zero) throw Fail("nie odnaleziono źródła w potoku", DecoderFailure.SystemCodecsUnavailable);

        try
        {
            Gst.ObjectSetString(source, "location", path, nint.Zero);
        }
        finally
        {
            Gst.ObjectUnref(source);
        }

        _sink = Gst.BinGetByName(_pipeline, "sink");
        if (_sink == nint.Zero) throw Fail("nie odnaleziono odbiornika w potoku", DecoderFailure.SystemCodecsUnavailable);

        // Stan wstrzymania wypełnia potok pierwszą próbką. Dopiero wtedy znany jest format
        // wyjściowy i długość nagrania.
        if (Gst.SetState(_pipeline, Gst.StatePaused) == Gst.ChangeFailure)
            throw FailFromBus("nie udało się uruchomić potoku");

        var state = Gst.GetState(_pipeline, out _, out _, Gst.TenSeconds);
        if (state == Gst.ChangeFailure)
            throw FailFromBus("nie udało się zdekodować pliku");

        if (state != Gst.ChangeSuccess && state != Gst.ChangeNoPreroll)
            throw Fail("potok nie przygotował się w ciągu 10 s", DecoderFailure.DamagedFile);

        Gst.SetState(_pipeline, Gst.StatePlaying);

        if (!PullSample()) throw Fail("potok nie zwrócił żadnych próbek", _failure);

        if (!TryReadFormat(_sample, out var sourceRate, out var sourceChannels))
            throw Fail("potok podał format bez częstotliwości lub liczby kanałów", DecoderFailure.DamagedFile);

        // audioconvert układa kanały według pozycji GStreamera, co dla 1-8 kanałów
        // odpowiada kolejności WAVE.
        Configure(sourceRate, sourceChannels, outputSampleRate, outputChannels, meter, ChannelLayouts.Wave(sourceChannels));

        // Pierwsza próbka zostaje pobrana, zanim znany jest format, więc liczbę ramek
        // trzeba policzyć ponownie, teraz gdy wiadomo, ile bajtów zajmuje jedna.
        CountPendingFrames();

        TryReadDuration();
    }

    /// <summary>
    /// Pyta potok o długość utworu. Tuż po starcie zapytanie potrafi zawieść (MP3 o zmiennej
    /// przepływności bez nagłówka Xing, kontener przed odczytem nagłówków), więc jest
    /// powtarzane przy kolejnych porcjach próbek.
    /// </summary>
    private void TryReadDuration()
    {
        if (TotalFrames > 0) return;

        if (!Gst.QueryDuration(_pipeline, Gst.FormatTime, out var nanoseconds) || nanoseconds <= 0) return;

        // Do ramek wyjściowych, bo w nich silnik liczy czas; SampleRate jest zerem tylko
        // przed pierwszym wywołaniem Configure.
        var rate = SampleRate > 0 ? SampleRate : SourceSampleRate;
        if (rate <= 0) return;

        TotalFrames = (long)Math.Round(nanoseconds / 1_000_000_000.0 * rate);
    }

    public override bool CanSeek => true;

    internal static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (_started) return;

            // Bez argumentów: program nie przekazuje GStreamerowi wiersza poleceń.
            Gst.Init(nint.Zero, nint.Zero);
            _started = true;
        }
    }

    /// <summary>Reads the negotiated rate and channel count of a sample.</summary>
    internal static bool TryReadFormat(nint sample, out int rate, out int channels)
    {
        rate = 0;
        channels = 0;

        var caps = Gst.SampleGetCaps(sample);
        if (caps == nint.Zero) return false;

        var structure = Gst.CapsGetStructure(caps, 0);
        if (structure == nint.Zero) return false;

        Gst.StructureGetInt(structure, "rate", out rate);
        Gst.StructureGetInt(structure, "channels", out channels);

        return rate > 0 && channels > 0;
    }

    protected override unsafe int ReadSource(Span<float> destination, int frames)
    {
        lock (_gate)
        {
            if (_closing) return 0;

            var produced = 0;

            while (produced < frames)
            {
                if (_formatChange is { } change)
                {
                    // Próbki w nowym formacie nie mogą trafić do bufora za próbkami w starym.
                    if (produced > 0) break;

                    _formatChange = null;
                    Reconfigure(change.Rate, change.Channels, ChannelLayouts.Wave(change.Channels));
                    CountPendingFrames();
                    return 0;
                }

                if (_pendingFrames == 0 && !PullSample()) break;
                if (_formatChange is not null) continue;

                var take = Math.Min(frames - produced, _pendingFrames);
                var samples = take * SourceChannels;

                var source = new ReadOnlySpan<float>(
                    (float*)_map.Data + _pendingOffset * SourceChannels, samples);

                source.CopyTo(destination.Slice(produced * SourceChannels, samples));

                _pendingOffset += take;
                _pendingFrames -= take;
                produced += take;

                if (_pendingFrames == 0) ReleaseSample();
            }

            return produced;
        }
    }

    /// <summary>
    /// Fetches the next buffer. Returns false at the end of the stream, which is how the base
    /// class learns the track has finished; an error on the bus or a stalled pipeline ends
    /// the stream the same way, so playback moves on instead of hanging.
    /// </summary>
    private bool PullSample()
    {
        ReleaseSample();

        if (_endOfStream) return false;

        var waiting = Stopwatch.StartNew();

        while (true)
        {
            if (_closing) return EndStream(null);

            _sample = Gst.AppSinkTryPullSample(_sink, Gst.TwoHundredMilliseconds);
            if (_sample != nint.Zero) break;

            if (Gst.AppSinkIsEos(_sink)) return EndStream(null);

            if (Gst.PopError(_bus) is { } error)
            {
                _failure = Classify(error.Domain, error.Code);
                return EndStream($"błąd potoku: {error.Message}");
            }

            if (waiting.Elapsed > StallLimit) return EndStream("potok nie oddał próbek w ciągu 10 s");
        }

        // Przed pierwszym Configure format nie jest jeszcze znany i nie ma z czym porównywać.
        if (SourceChannels > 0)
        {
            if (!TryReadFormat(_sample, out var rate, out var channels))
            {
                ReleaseSample();
                return EndStream("próbka bez częstotliwości lub liczby kanałów");
            }

            // Bufor jest już w nowym formacie; ReadSource przebuduje konwerter, zanim go przekaże.
            if (rate != SourceSampleRate || channels != SourceChannels) _formatChange = (rate, channels);
        }

        var buffer = Gst.SampleGetBuffer(_sample);
        if (buffer == nint.Zero || !Gst.BufferMap(buffer, out _map, Gst.MapRead))
        {
            ReleaseSample();
            _formatChange = null;
            return EndStream("nie udało się odczytać bufora próbek");
        }

        _mapped = true;
        _pendingOffset = 0;
        if (_formatChange is null) CountPendingFrames();
        TryReadDuration();

        return true;
    }

    private bool EndStream(string? reason)
    {
        if (reason is not null) DecoderFailures.Log($"GStreamer: {reason}");
        _endOfStream = true;
        return false;
    }

    /// <summary>
    /// Works out how many frames the mapped buffer holds. Before the format is known — which is
    /// the case for the very first sample, the one the format is read from — the answer is zero
    /// and the caller recounts once <see cref="ManagedDecoderBase.SourceChannels"/> is set.
    /// </summary>
    private void CountPendingFrames()
    {
        if (!_mapped || SourceChannels <= 0)
        {
            _pendingFrames = 0;
            return;
        }

        _pendingFrames = (int)(_map.Size / (nuint)(SourceChannels * sizeof(float)));
    }

    private void ReleaseSample()
    {
        if (_mapped)
        {
            var buffer = Gst.SampleGetBuffer(_sample);
            if (buffer != nint.Zero) Gst.BufferUnmap(buffer, ref _map);
            _mapped = false;
        }

        if (_sample != nint.Zero)
        {
            Gst.MiniObjectUnref(_sample);
            _sample = nint.Zero;
        }

        _pendingFrames = 0;
        _pendingOffset = 0;
    }

    protected override void SeekSource(long sourceFrame)
    {
        lock (_gate)
        {
            if (_closing) return;

            ReleaseSample();
            _endOfStream = false;

            var nanoseconds = (long)Math.Round(sourceFrame / (double)SourceSampleRate * 1_000_000_000.0);

            Gst.SeekSimple(_pipeline, Gst.FormatTime, Gst.SeekFlush | Gst.SeekKeyUnit, nanoseconds);

            // Przewijanie opróżnia potok; poczekanie na ponowne wypełnienie oszczędza pierwszemu
            // odczytowi po przewinięciu zwrócenia zera, które silnik wziąłby za koniec utworu.
            Gst.GetState(_pipeline, out _, out _, Gst.TenSeconds);
        }
    }

    /// <summary>Rozróżnia brak wtyczki i nierozpoznany typ od uszkodzonego pliku.</summary>
    private static DecoderFailure Classify(uint domain, int code)
    {
        if (domain == Gst.CoreErrorQuark())
            return code == Gst.CoreErrorMissingPlugin ? DecoderFailure.CodecMissing : DecoderFailure.DamagedFile;

        if (domain != Gst.StreamErrorQuark()) return DecoderFailure.DamagedFile;

        return code switch
        {
            Gst.StreamErrorCodecNotFound => DecoderFailure.CodecMissing,
            Gst.StreamErrorTypeNotFound => DecoderFailure.UnrecognisedFormat,
            _ => DecoderFailure.DamagedFile,
        };
    }

    private AudioException FailFromBus(string what)
    {
        if (Gst.PopError(_bus) is { } error)
            return Fail($"{what}: {error.Message}", Classify(error.Domain, error.Code));

        return Fail(what, DecoderFailure.DamagedFile);
    }

    private static AudioException Fail(string what, DecoderFailure reason)
    {
        DecoderFailures.Log($"GStreamer: {what}");
        return DecoderFailures.Create(reason);
    }

    protected override void DisposeCore()
    {
        // Wątek dekodujący wychodzi z oczekiwania na próbkę najpóźniej po jednym limicie czasu.
        _closing = true;

        lock (_gate)
        {
            ReleaseSample();

            if (_sink != nint.Zero)
            {
                Gst.ObjectUnref(_sink);
                _sink = nint.Zero;
            }

            if (_bus != nint.Zero)
            {
                Gst.ObjectUnref(_bus);
                _bus = nint.Zero;
            }

            if (_pipeline != nint.Zero)
            {
                Gst.SetState(_pipeline, Gst.StateNull);
                Gst.ObjectUnref(_pipeline);
                _pipeline = nint.Zero;
            }
        }
    }
}
