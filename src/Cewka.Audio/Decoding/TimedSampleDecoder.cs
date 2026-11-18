using System.Globalization;

namespace Cewka.Audio.Decoding;

/// <summary>Porcja próbek źródła: czas pierwszej (100 ns na osi czasu źródła), format i liczba ramek.</summary>
internal readonly record struct TimedBlock(long Ticks, int Rate, int Channels, uint Mask, int Frames);

/// <summary>
/// Źródło próbek zmiennoprzecinkowych z czasami, w częstotliwości pliku. Tak oddaje dźwięk
/// czytnik Media Foundation; osobny interfejs pozwala sprawdzić przycinanie bez systemu.
/// </summary>
internal interface ITimedSampleSource : IDisposable
{
    int Rate { get; }
    int Channels { get; }
    uint Mask { get; }

    /// <summary>Długość według systemu, null gdy nieznana.</summary>
    TimeSpan? Duration { get; }

    /// <summary>Próbki ostatniej porcji z <see cref="Next"/>, ważne do następnego wywołania Next albo Seek.</summary>
    ReadOnlySpan<float> Samples { get; }

    /// <summary>Następna porcja; fałsz na końcu strumienia albo po błędzie.</summary>
    bool Next(out TimedBlock block);

    /// <summary>Przewija do czasu w jednostkach 100 ns na osi czasu źródła.</summary>
    void Seek(long ticks);
}

/// <summary>
/// Dekoder nad źródłem próbek z czasami. Przy pliku MP4 z opisem rozbiegu (<see cref="Mp4Gapless"/>)
/// zostawia tylko nagranie: ramki od opóźnienia kodera do końca nagrania, bez rozbiegu
/// i dopełnienia ostatniej ramki, które między utworami albumu grałyby jako cisza.
///
/// <para>Miejsce próbki w pliku to jej czas plus przesunięcie osi. Media Foundation w części
/// wydań Windows liczy czas od początku ścieżki, w innych od początku edycji z listy edycji,
/// czyli za rozbiegiem. Który układ obowiązuje, mówi koniec osi czasu systemu, porównany
/// przy otwarciu z sumą czasów próbek pliku (<see cref="ProbeEnd"/>). Gdy cokolwiek się nie
/// zgadza, dekoder nie przycina niczego: zbędny rozbieg jest mniejszą szkodą niż utracony
/// początek nagrania.</para>
/// </summary>
internal class TimedSampleDecoder : ManagedDecoderBase
{
    /// <summary>
    /// Rozbieg dekodera przed celem przewinięcia: ramka AAC zależy od poprzedniej, więc dekodowanie
    /// zaczyna się wcześniej, a nadmiar odpada. Dwie ramki HE-AAC z zapasem.
    /// </summary>
    private const int SeekPreRollFrames = 4096;

    /// <summary>Sonda czyta od tylu sekund przed spodziewanym końcem osi.</summary>
    private const double ProbeLeadSeconds = 0.5;

    /// <summary>Sonda, która zdekodowała tyle dźwięku i nie doszła do końca, nie daje wyniku.</summary>
    private const double ProbeLimitSeconds = 10;

    private enum Timeline
    {
        /// <summary>Bez przycinania, jak przed odczytem opisu rozbiegu.</summary>
        Untrimmed,

        /// <summary>Czas liczony od początku ścieżki.</summary>
        Absolute,

        /// <summary>Czas liczony od początku edycji, za rozbiegiem.</summary>
        Edited,
    }

    private readonly ITimedSampleSource _source;
    private readonly Mp4Gapless? _gapless;

    private Timeline _timeline;

    /// <summary>Sekundy do dodania do czasu źródła, żeby dostać miejsce w pliku.</summary>
    private double _offset;

    /// <summary>Pierwsza chwila pliku, która gra: początek nagrania albo cel przewinięcia.</summary>
    private double _keepFrom;

    /// <summary>Koniec poprzedniej porcji w pliku; porcja zaczynająca się wcześniej znaczy, że czasy się cofają.</summary>
    private double? _previousEnd;

    private int _blockOffset;
    private int _blockFrames;
    private bool _endOfStream;
    private uint _mask;
    private (int Rate, int Channels, uint Mask)? _formatChange;

    private double? _probeEnd;
    private string _decision = "";
    private readonly List<long> _firstTicks = [];
    private bool _seeked;
    private long _systemFrames;

    protected TimedSampleDecoder(
        ITimedSampleSource source, Mp4Gapless? gapless, int outputSampleRate, int outputChannels, BitrateMeter? meter)
    {
        _source = source;
        _gapless = gapless;

        try
        {
            _mask = source.Mask;
            Configure(source.Rate, source.Channels, outputSampleRate, outputChannels, meter,
                ChannelLayouts.FromWaveMask(source.Mask, source.Channels));

            Decide();

            if (_timeline != Timeline.Untrimmed && gapless is { } known)
            {
                _keepFrom = known.StartSeconds;
                TotalFrames = (long)Math.Round(known.LengthSeconds * outputSampleRate);
            }
            else
            {
                TotalFrames = source.Duration is { } duration && duration > TimeSpan.Zero
                    ? (long)Math.Round(duration.TotalSeconds * outputSampleRate)
                    : 0;
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public override bool CanSeek => true;

    /// <summary>
    /// Surowe liczby rozpoznania osi czasu: wynik sondy, decyzja, czasy pierwszych porcji
    /// i liczba ramek oddanych przez system. Do komunikatów testów i dziennika.
    /// </summary>
    internal string TimelineReport
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var g = _gapless;
            var file = g is { } x
                ? string.Create(inv, $"skala {x.Timescale}, rozbieg {x.Delay}, nagranie {x.Length}, źródło {x.Source}, " +
                    $"suma stts {x.Samples}, próbek {x.SampleCount}, ramka {x.FrameDuration}, edycja od {x.EditDelay}")
                : "brak opisu";
            var probe = _probeEnd is { } end && g is { } y
                ? string.Create(inv, $"{end * y.Timescale:F1} jedn. ścieżki")
                : "nie było";
            return string.Create(inv,
                $"plik: {file}; sonda końca osi: {probe}; decyzja: {_decision}; przesunięcie {_offset * (g?.Timescale ?? 0):F0}; " +
                $"czasy pierwszych porcji (100 ns): [{string.Join(", ", _firstTicks)}]; ramek od systemu: {_systemFrames}");
        }
    }

    private void Decide()
    {
        if (_gapless is not { } g)
        {
            Choose(Timeline.Untrimmed, 0, "brak opisu rozbiegu");
            return;
        }

        // Bez edycji przesuniętej za początek oba układy czasu są tym samym.
        if (g.EditDelay <= 0)
        {
            Choose(Timeline.Absolute, 0, "brak edycji, czas od początku ścieżki");
            return;
        }

        _probeEnd = ProbeEnd(g);
        if (_probeEnd is not { } probe)
        {
            Choose(Timeline.Untrimmed, 0, "sonda bez wyniku");
            return;
        }

        // Koniec osi w jednostkach ścieżki: system kończy na sumie czasów próbek albo na pełnej
        // ostatniej ramce, a przy czasie po edycji obie wartości są mniejsze o początek edycji.
        var end = probe * g.Timescale;
        var frame = Math.Max(g.FrameDuration, 1);
        var tolerance = frame / 4.0;
        double[] whole = [g.Samples, g.SampleCount * (double)frame];

        var absolute = whole.Any(candidate => Math.Abs(end - candidate) <= tolerance);
        var edited = whole.Any(candidate => Math.Abs(end - (candidate - g.EditDelay)) <= tolerance);

        if (absolute && !edited) Choose(Timeline.Absolute, 0, "czas od początku ścieżki");
        else if (edited && !absolute) Choose(Timeline.Edited, g.EditDelay / (double)g.Timescale, "czas od początku edycji");
        else Choose(Timeline.Untrimmed, 0, absolute ? "sonda pasuje do obu układów" : "sonda nie pasuje do żadnego układu");
    }

    private void Choose(Timeline timeline, double offset, string decision)
    {
        _timeline = timeline;
        _offset = offset;
        _decision = decision;

        if (timeline == Timeline.Untrimmed && _gapless is not null)
            DecoderFailures.Log($"Dekoder systemu: plik gra bez przycięcia rozbiegu ({decision}).");
    }

    /// <summary>
    /// Koniec osi czasu systemu w sekundach: przewinięcie tuż przed spodziewany koniec, odczyt
    /// do końca strumienia, największy czas porcji z jej długością, powrót na początek.
    /// </summary>
    private double? ProbeEnd(Mp4Gapless g)
    {
        var earliest = (g.Samples - g.EditDelay) / (double)g.Timescale - ProbeLeadSeconds;
        _source.Seek((long)(Math.Max(0, earliest) * TimeSpan.TicksPerSecond));

        var end = double.NegativeInfinity;
        var decoded = 0.0;
        var finished = false;

        while (decoded < ProbeLimitSeconds)
        {
            if (!_source.Next(out var block))
            {
                finished = true;
                break;
            }

            if (block.Rate <= 0) continue;

            var length = block.Frames / (double)block.Rate;
            decoded += length;
            end = Math.Max(end, block.Ticks / (double)TimeSpan.TicksPerSecond + length);
        }

        _source.Seek(0);
        return finished && double.IsFinite(end) ? end : null;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from the source blocks; a block in a new format
    /// waits until the converter has been rebuilt.
    /// </summary>
    protected override int ReadSource(Span<float> destination, int frames)
    {
        var written = 0;

        while (written < frames)
        {
            if (_formatChange is { } change)
            {
                // Próbki w nowym formacie nie mogą trafić do bufora za próbkami w starym.
                if (written > 0) break;

                _formatChange = null;
                _mask = change.Mask;
                Reconfigure(change.Rate, change.Channels, ChannelLayouts.FromWaveMask(change.Mask, change.Channels));
                return 0;
            }

            if (_blockFrames == 0)
            {
                if (_endOfStream || !Pull()) break;
                continue;
            }

            var take = Math.Min(frames - written, _blockFrames);
            var channels = SourceChannels;

            _source.Samples.Slice(_blockOffset * channels, take * channels)
                .CopyTo(destination[(written * channels)..]);

            written += take;
            _blockOffset += take;
            _blockFrames -= take;
        }

        return written;
    }

    private bool Pull()
    {
        if (!_source.Next(out var block))
        {
            _endOfStream = true;
            return false;
        }

        _systemFrames += block.Frames;
        if (!_seeked && _firstTicks.Count < 3) _firstTicks.Add(block.Ticks);

        // Ta porcja jest już w nowym formacie; ReadSource przebuduje konwerter, zanim ją przekaże.
        if (block.Rate != SourceSampleRate || block.Channels != SourceChannels || block.Mask != _mask)
            _formatChange = (block.Rate, block.Channels, block.Mask);

        _blockOffset = 0;
        _blockFrames = block.Frames;

        return _timeline == Timeline.Untrimmed || Trim(block);
    }

    /// <summary>
    /// Zostawia z porcji ramki od <see cref="_keepFrom"/> do końca nagrania. Fałsz oznacza
    /// koniec nagrania.
    /// </summary>
    private bool Trim(TimedBlock block)
    {
        var rate = block.Rate;
        if (rate <= 0 || _gapless is not { } g) return true;

        var start = block.Ticks / (double)TimeSpan.TicksPerSecond + _offset;

        // Porcja zaczynająca się przed końcem poprzedniej: czas nie opisuje miejsca w pliku
        // (np. rozbieg z czasem przyciętym do zera). Dalej bez przycinania, nic nie ginie.
        if (_previousEnd is { } previous && start < previous - 2.0 / rate)
        {
            Choose(Timeline.Untrimmed, 0, "czasy porcji się cofają");
            return true;
        }

        _previousEnd = start + block.Frames / (double)rate;

        var first = (long)Math.Round(start * rate);
        var from = (long)Math.Round(_keepFrom * rate);
        var until = (long)Math.Round(g.EndSeconds * rate);

        if (first >= until)
        {
            _blockFrames = 0;
            _endOfStream = true;
            return false;
        }

        var skip = Math.Clamp(from - first, 0, block.Frames);
        var keep = Math.Clamp(until - first, 0, block.Frames) - skip;

        _blockOffset = (int)skip;
        _blockFrames = (int)Math.Max(keep, 0);
        return true;
    }

    protected override void SeekSource(long sourceFrame)
    {
        _blockOffset = 0;
        _blockFrames = 0;
        _endOfStream = false;
        _previousEnd = null;
        _seeked = true;

        var seconds = sourceFrame / (double)SourceSampleRate;

        // Źródło staje na początku ramki przed celem; resztę do celu odrzuca Trim.
        if (_timeline != Timeline.Untrimmed && _gapless is { } g)
        {
            _keepFrom = g.StartSeconds + seconds;
            seconds = Math.Max(0, _keepFrom - _offset - SeekPreRollFrames / (double)SourceSampleRate);
        }

        _source.Seek((long)(seconds * TimeSpan.TicksPerSecond));
    }

    protected override void DisposeCore() => _source.Dispose();
}
