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
/// <para>Media Foundation różnych wydań Windows podaje czas próbek pliku z listą edycji
/// inaczej: od początku ścieżki (z rozbiegiem na początku), od początku edycji z pominiętym
/// rozbiegiem albo od początku edycji z rozbiegiem oddanym z czasem zero. Układ rozpoznawany
/// jest przy otwarciu z zachowania systemu: koniec osi czasu (<see cref="ProbeEnd"/>) mówi,
/// od czego liczony jest czas, a pierwsza porcja o czasie dodatnim (<see cref="ProbeStart"/>),
/// czy przed nią przyszedł rozbieg. W trakcie każda porcja z czasem jest sprawdzana z licznikiem
/// ramek. Gdy cokolwiek się nie zgadza, dekoder przestaje przycinać: zbędny rozbieg jest
/// mniejszą szkodą niż utracony początek nagrania.</para>
/// </summary>
internal class TimedSampleDecoder : ManagedDecoderBase
{
    /// <summary>
    /// Rozbieg dekodera przed celem przewinięcia: ramka AAC zależy od poprzedniej, więc dekodowanie
    /// zaczyna się wcześniej, a nadmiar odpada. Dwie ramki HE-AAC z zapasem.
    /// </summary>
    private const int SeekPreRollFrames = 4096;

    /// <summary>Sonda końca czyta od tylu sekund przed spodziewanym końcem osi.</summary>
    private const double ProbeLeadSeconds = 0.5;

    /// <summary>Sonda, która zdekodowała tyle dźwięku i nie znalazła tego, czego szuka, nie daje wyniku.</summary>
    private const double ProbeLimitSeconds = 10;

    private enum Timeline
    {
        /// <summary>Bez przycinania, jak przed odczytem opisu rozbiegu.</summary>
        Untrimmed,

        /// <summary>Miejsce porcji w pliku to jej czas plus przesunięcie osi.</summary>
        Stated,

        /// <summary>
        /// Czas od początku edycji, a rozbieg przychodzi z czasem zero albo ujemnym: miejsce
        /// porcji bez czasu dodatniego wynika z licznika ramek od początku strumienia.
        /// </summary>
        Counted,
    }

    private readonly ITimedSampleSource _source;
    private readonly Mp4Gapless? _gapless;

    private Timeline _timeline;

    /// <summary>Sekundy do dodania do czasu źródła, żeby dostać miejsce w pliku.</summary>
    private double _offset;

    /// <summary>Największa rozbieżność czasu porcji z licznikiem: ćwierć ramki kodera.</summary>
    private double _tolerance;

    /// <summary>Pierwsza chwila pliku, która gra: początek nagrania albo cel przewinięcia.</summary>
    private double _keepFrom;

    /// <summary>Miejsce w pliku (s), w którym powinna zacząć się następna porcja; null po otwarciu i przewinięciu.</summary>
    private double? _nextPosition;

    private int _blockOffset;
    private int _blockFrames;
    private bool _endOfStream;
    private uint _mask;
    private (int Rate, int Channels, uint Mask)? _formatChange;

    private double? _probeEnd;
    private (double Before, double Time)? _probeStart;
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
    /// Surowe liczby rozpoznania osi czasu: sondy końca i początku, decyzja, czasy pierwszych
    /// porcji i liczba ramek oddanych przez system. Do komunikatów testów i dziennika.
    /// </summary>
    internal string TimelineReport
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var g = _gapless;
            var scale = g?.Timescale ?? 0;
            var file = g is { } x
                ? string.Create(inv, $"skala {x.Timescale}, rozbieg {x.Delay}, nagranie {x.Length}, źródło {x.Source}, " +
                    $"suma stts {x.Samples}, próbek {x.SampleCount}, ramka {x.FrameDuration}, edycja od {x.EditDelay}")
                : "brak opisu";
            var end = _probeEnd is { } e ? string.Create(inv, $"{e * scale:F1} jedn. ścieżki") : "nie było";
            var start = _probeStart is { } s
                ? string.Create(inv, $"przed pierwszą porcją z czasem dodatnim C = {s.Before * scale:F1}, jej czas t = {s.Time * scale:F1}")
                : "nie było";
            return string.Create(inv,
                $"plik: {file}; sonda końca osi: {end}; sonda początku: {start}; decyzja: {_decision}; " +
                $"przesunięcie {_offset * scale:F0}; czasy pierwszych porcji (100 ns): [{string.Join(", ", _firstTicks)}]; " +
                $"ramek od systemu: {_systemFrames}");
        }
    }

    private void Decide()
    {
        if (_gapless is not { } g)
        {
            Choose(Timeline.Untrimmed, 0, "brak opisu rozbiegu");
            return;
        }

        _tolerance = Math.Max(g.FrameDuration, 1) / 4.0 / g.Timescale;

        // Bez edycji przesuniętej za początek oba układy czasu są tym samym.
        if (g.EditDelay <= 0)
        {
            Choose(Timeline.Stated, 0, "brak edycji, czas od początku ścieżki");
            return;
        }

        _probeEnd = ProbeEnd(g);
        _probeStart = ProbeStart();

        if (_probeEnd is not { } probe || _probeStart is not { } first)
        {
            Choose(Timeline.Untrimmed, 0, "sonda bez wyniku");
            return;
        }

        // Koniec osi w jednostkach ścieżki: system kończy na sumie czasów próbek albo na pełnej
        // ostatniej ramce, a przy czasie po edycji obie wartości są mniejsze o początek edycji.
        var end = probe * g.Timescale;
        var frame = Math.Max(g.FrameDuration, 1);
        double[] whole = [g.Samples, g.SampleCount * (double)frame];

        var absolute = whole.Any(candidate => Math.Abs(end - candidate) <= frame / 4.0);
        var edited = whole.Any(candidate => Math.Abs(end - (candidate - g.EditDelay)) <= frame / 4.0);

        var edit = g.EditDelay / (double)g.Timescale;
        var timeMatches = Math.Abs(first.Time - first.Before) <= _tolerance;
        var primingDelivered = Math.Abs(first.Time + edit - first.Before) <= _tolerance;

        if (absolute && !edited)
        {
            if (timeMatches) Choose(Timeline.Stated, 0, "czas od początku ścieżki");
            else Choose(Timeline.Untrimmed, 0, "czas od początku ścieżki, ale początek nie zgadza się z licznikiem");
        }
        else if (edited && !absolute)
        {
            if (primingDelivered && !timeMatches) Choose(Timeline.Counted, edit, "czas od początku edycji, rozbieg oddawany");
            else if (timeMatches && !primingDelivered) Choose(Timeline.Stated, edit, "czas od początku edycji, rozbieg pominięty");
            else Choose(Timeline.Untrimmed, 0, "czas od początku edycji, ale początek nie pasuje do żadnego przypadku");
        }
        else
        {
            Choose(Timeline.Untrimmed, 0, absolute ? "sonda końca pasuje do obu układów" : "sonda końca nie pasuje do żadnego układu");
        }
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
    /// Od początku strumienia do pierwszej porcji o czasie dodatnim: ile dźwięku (s) przyszło
    /// przed nią i jaki ma czas. System, który oddaje rozbieg z czasem zero albo ujemnym, oddaje
    /// przed nią rozbieg i jeszcze czas porcji; system, który go pomija albo liczy czas od
    /// początku ścieżki, oddaje przed nią dokładnie tyle, ile mówi jej czas.
    /// </summary>
    private (double Before, double Time)? ProbeStart()
    {
        var before = 0.0;
        (double, double)? result = null;

        while (before < ProbeLimitSeconds && _source.Next(out var block))
        {
            if (block.Rate <= 0) continue;

            if (block.Ticks > 0)
            {
                result = (before, block.Ticks / (double)TimeSpan.TicksPerSecond);
                break;
            }

            before += block.Frames / (double)block.Rate;
        }

        _source.Seek(0);
        return result;
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

        var time = block.Ticks / (double)TimeSpan.TicksPerSecond;

        // Czas zero albo ujemny przy rozbiegu oddawanym nie mówi, gdzie porcja leży.
        double? stated = _timeline == Timeline.Stated || block.Ticks > 0 ? time + _offset : null;

        // Pierwsza porcja po otwarciu albo przewinięciu bez czasu: początek strumienia.
        var position = _nextPosition ?? stated ?? 0;

        if (_nextPosition is { } expected && stated is { } at && Math.Abs(at - expected) > _tolerance)
        {
            Choose(Timeline.Untrimmed, 0, string.Create(CultureInfo.InvariantCulture,
                $"czas porcji {at * g.Timescale:F1} nie zgadza się z licznikiem {expected * g.Timescale:F1}"));
            return true;
        }

        _nextPosition = position + block.Frames / (double)rate;

        var first = (long)Math.Round(position * rate);
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
        _nextPosition = null;
        _seeked = true;

        var seconds = sourceFrame / (double)SourceSampleRate;

        // Źródło staje na początku ramki przed celem; resztę do celu odrzuca Trim.
        if (_timeline != Timeline.Untrimmed && _gapless is { } g)
        {
            _keepFrom = g.StartSeconds + seconds;
            seconds = Math.Max(0, _keepFrom - _offset - SeekPreRollFrames / (double)SourceSampleRate);

            // Przy rozbiegu oddawanym z czasem zero porcja trafiona tuż za początkiem też może
            // mieć czas zero, a jej miejsca nie widać. Przewinięcie na sam początek sprawdziła
            // sonda początku, więc cel blisko początku zamienia się na początek.
            if (_timeline == Timeline.Counted && seconds < 2.0 * Math.Max(g.FrameDuration, 1) / g.Timescale)
                seconds = 0;
        }

        _source.Seek((long)(seconds * TimeSpan.TicksPerSecond));
    }

    protected override void DisposeCore() => _source.Dispose();
}
