using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cewka.Audio.Decoding.Linux;

/// <summary>
/// AAC, M4A and ALAC on Linux, decoded by whatever GStreamer has installed.
///
/// <para><b>Kształt rozwiązania.</b> Potok <c>filesrc → decodebin → audioconvert → appsink</c>
/// zostawia dobór dekodera GStreamerowi, a program odbiera gotowe próbki zmiennoprzecinkowe.
/// Format wyjściowy nie jest narzucany poza samym typem próbki: częstotliwość i liczba kanałów
/// pozostają takie jak w pliku, a dopasowanie do urządzenia robi ta sama klasa bazowa, co dla
/// Vorbisa, Opusa i Media Foundation. Dzięki temu wszystkie ścieżki brzmią tak samo, zamiast
/// zależeć od jakości resamplera akurat zainstalowanej wtyczki.</para>
///
/// <para><c>sync=false</c> na odbiorniku jest tu istotne: bez tego GStreamer wydawałby próbki
/// w tempie odtwarzania, a dekoder ma wyprzedzać odtwarzanie o pół sekundy, nie nadążać za nim.</para>
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

    private static readonly Lock StartupLock = new();
    private static bool _started;

    private nint _pipeline;
    private nint _sink;

    private nint _sample;
    private Gst.MapInfo _map;
    private bool _mapped;
    private int _pendingFrames;
    private int _pendingOffset;
    private bool _endOfStream;

    public GStreamerDecoder(string path, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        EnsureStarted();

        _pipeline = Gst.ParseLaunch(PipelineDescription, out var error);
        if (_pipeline == nint.Zero)
            throw new AudioException($"GStreamer nie zbudował potoku: {Gst.TakeMessage(error)}.");

        if (error != nint.Zero) Gst.ErrorFree(error);

        var source = Gst.BinGetByName(_pipeline, "source");
        if (source == nint.Zero) throw Fail("nie odnaleziono źródła w potoku");

        try
        {
            Gst.ObjectSetString(source, "location", path, nint.Zero);
        }
        finally
        {
            Gst.ObjectUnref(source);
        }

        _sink = Gst.BinGetByName(_pipeline, "sink");
        if (_sink == nint.Zero) throw Fail("nie odnaleziono odbiornika w potoku");

        // Stan wstrzymania wypełnia potok pierwszą próbką. Dopiero wtedy znany jest format
        // wyjściowy i długość nagrania - przed uzgodnieniem nie ma o co pytać.
        if (Gst.SetState(_pipeline, Gst.StatePaused) == Gst.ChangeFailure)
            throw Fail("nie udało się uruchomić potoku");

        if (Gst.GetState(_pipeline, out _, out _, Gst.TenSeconds) == Gst.ChangeFailure)
            throw Fail("nie udało się zdekodować pliku; najczęstszy powód to brak wtyczki " +
                       "dekodującej — dla formatów AAC, M4A i ALAC dostarcza ją pakiet gstreamer1.0-libav");

        Gst.SetState(_pipeline, Gst.StatePlaying);

        if (!PullSample()) throw Fail("potok nie zwrócił żadnych próbek");

        ReadFormat(out var sourceRate, out var sourceChannels);
        Configure(sourceRate, sourceChannels, outputSampleRate, outputChannels, meter);

        // Pierwsza próbka zostaje pobrana, zanim znany jest format, więc liczbę ramek
        // trzeba policzyć ponownie — teraz, gdy wiadomo, ile bajtów zajmuje jedna.
        CountPendingFrames();

        TryReadDuration();
    }

    /// <summary>
    /// Asks the pipeline how long the track is.
    ///
    /// <para>Zapytanie potrafi zawieść tuż po uruchomieniu potoku: przy pliku MP3 o zmiennej
    /// przepływności bez nagłówka Xing długość znana jest dopiero po przeskanowaniu materiału,
    /// a przy strumieniu z kontenera — po odczytaniu jego nagłówków. Dlatego pytanie powtarzane
    /// jest przy kolejnych porcjach próbek, dopóki nie przyniesie odpowiedzi.</para>
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

    private static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (_started) return;

            // Bez argumentów: program nie przekazuje GStreamerowi wiersza poleceń.
            Gst.Init(nint.Zero, nint.Zero);
            _started = true;
        }
    }

    /// <summary>Reads the negotiated rate and channel count from the sample already in hand.</summary>
    private void ReadFormat(out int rate, out int channels)
    {
        rate = 0;
        channels = 0;

        var caps = Gst.SampleGetCaps(_sample);
        if (caps == nint.Zero) throw Fail("potok nie podał formatu próbek");

        var structure = Gst.CapsGetStructure(caps, 0);
        if (structure == nint.Zero) throw Fail("potok nie podał formatu próbek");

        Gst.StructureGetInt(structure, "rate", out rate);
        Gst.StructureGetInt(structure, "channels", out channels);

        if (rate <= 0 || channels <= 0) throw Fail("potok podał format bez częstotliwości lub liczby kanałów");
    }

    protected override unsafe int ReadSource(Span<float> destination, int frames)
    {
        var produced = 0;

        while (produced < frames)
        {
            if (_pendingFrames == 0 && !PullSample()) break;

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

    /// <summary>
    /// Fetches the next buffer. Returns false at the end of the stream, which is how the base
    /// class learns the track has finished.
    /// </summary>
    private bool PullSample()
    {
        ReleaseSample();

        if (_endOfStream) return false;

        _sample = Gst.AppSinkPullSample(_sink);
        if (_sample == nint.Zero)
        {
            _endOfStream = true;
            return false;
        }

        var buffer = Gst.SampleGetBuffer(_sample);
        if (buffer == nint.Zero || !Gst.BufferMap(buffer, out _map, Gst.MapRead))
        {
            ReleaseSample();
            _endOfStream = true;
            return false;
        }

        _mapped = true;
        _pendingOffset = 0;
        CountPendingFrames();
        TryReadDuration();

        return true;
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
        ReleaseSample();
        _endOfStream = false;

        var nanoseconds = (long)Math.Round(sourceFrame / (double)SourceSampleRate * 1_000_000_000.0);

        Gst.SeekSimple(_pipeline, Gst.FormatTime, Gst.SeekFlush | Gst.SeekKeyUnit, nanoseconds);

        // Przewijanie opróżnia potok; poczekanie na ponowne wypełnienie oszczędza pierwszemu
        // odczytowi po przewinięciu zwrócenia zera, które silnik wziąłby za koniec utworu.
        Gst.GetState(_pipeline, out _, out _, Gst.TenSeconds);
    }

    private AudioException Fail(string what) => new($"GStreamer: {what}.");

    protected override void DisposeCore()
    {
        ReleaseSample();

        if (_sink != nint.Zero)
        {
            Gst.ObjectUnref(_sink);
            _sink = nint.Zero;
        }

        if (_pipeline != nint.Zero)
        {
            Gst.SetState(_pipeline, Gst.StateNull);
            Gst.ObjectUnref(_pipeline);
            _pipeline = nint.Zero;
        }
    }
}
