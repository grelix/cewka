using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cewka.Audio.Dsp;

/// <summary>
/// Strumieniowa zmiana częstotliwości próbkowania wielofazowym filtrem FIR z okienkowanego
/// sinc (okno Kaisera). Próbki przeplatane float, dowolna liczba kanałów.
/// <para>
/// Ramka wyjściowa n leży w chwili n·we/wy ramek wejścia, bez opóźnienia: filtr patrzy w przód
/// o połowę swojej długości, a przed pierwszą ramką stoją zera. Końcówkę, która czeka na te
/// przyszłe ramki, wydaje <see cref="Drain"/>; bez niego stan może przejąć następny utwór
/// i ciągnąć ten sam strumień dalej.
/// </para>
/// </summary>
internal sealed class SincResampler
{
    /// <summary>Granica częstotliwości z nagłówka pliku; wyżej to uszkodzone dane, nie dźwięk.</summary>
    public const int MaxRate = 1_536_000;

    /// <summary>Największe zmniejszenie częstotliwości; długość filtru rośnie z nim liniowo.</summary>
    public const int MaxDownsamplingRatio = 256;

    private readonly FilterBank _bank;
    private readonly int _channels;
    private readonly float[][] _planes;
    private readonly int _capacity;
    private readonly int _stepWhole;
    private readonly int _stepRemainder;

    private int _base;
    private int _phase;
    private int _end;
    private bool _draining;
    private int _realEnd;
    private int _zerosLeft;

    public SincResampler(int channels, int inputRate, int outputRate, ResamplingLevel level)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _bank = FilterBank.For(inputRate, outputRate, level);
        _channels = channels;

        _stepWhole = _bank.Down / _bank.Up;
        _stepRemainder = _bank.Down % _bank.Up;

        // Zapas na porcję wejścia ponad to, co filtr musi pamiętać.
        _capacity = 2 * _bank.Taps + 4096;
        _planes = new float[channels][];
        for (var c = 0; c < channels; c++) _planes[c] = new float[_capacity];

        Reset();
    }

    public int Channels => _channels;
    public int InputRate => _bank.InputRate;
    public int OutputRate => _bank.OutputRate;
    public ResamplingLevel Level => _bank.Level;

    /// <summary>Liczba współczynników na ramkę wyjścia (po wyrównaniu do wektora).</summary>
    public int Taps => _bank.Taps;

    /// <summary>Czy fazy pochodzą z tablicy z interpolacją zamiast dokładnego wyliczenia.</summary>
    public bool InterpolatesPhases => _bank.Interpolated;

    /// <summary>
    /// O ile ramek wejścia filtr wybiega w przód. Tyle ostatnich ramek utworu czeka na
    /// <see cref="Drain"/> albo na następny utwór.
    /// </summary>
    public int Lookahead => _bank.Lookahead;

    /// <summary>Długość wyjścia dla <paramref name="inputFrames"/> ramek wejścia po wypłukaniu końcówki.</summary>
    public static long OutputFrames(long inputFrames, int inputRate, int outputRate)
    {
        if (inputFrames <= 0) return 0;
        var divisor = Gcd(inputRate, outputRate);
        var up = outputRate / divisor;
        var down = inputRate / divisor;

        // ceil(n·up/down) bez przepełnienia dla długości z nagłówka.
        var whole = inputFrames / down;
        var rest = inputFrames % down;
        return whole * up + (rest * up + down - 1) / down;
    }

    /// <summary>Zeruje historię filtru i fazę, jak przed pierwszą ramką strumienia.</summary>
    public void Reset()
    {
        foreach (var plane in _planes) Array.Clear(plane, 0, _bank.History);
        _base = _bank.History;
        _end = _bank.History;
        _phase = 0;
        _draining = false;
        _realEnd = 0;
        _zerosLeft = 0;
    }

    /// <summary>Ile ramek wejścia przyjmie teraz <see cref="Process"/>, licząc przesunięcie bufora.</summary>
    public int AcceptableFrames => _draining ? 0 : _capacity - (_end - Discardable);

    /// <summary>Ile ramek wejścia jeszcze brakuje do wydania <paramref name="outputFrames"/> ramek.</summary>
    public long RequiredInput(long outputFrames)
    {
        if (outputFrames <= 0) return 0;

        var advance = ((outputFrames - 1) * _bank.Down + _phase) / _bank.Up;
        var lastBase = _base + advance;
        return Math.Max(0, lastBase + _bank.Lookahead + 1 - _end);
    }

    /// <summary>
    /// Przyjmuje tyle wejścia, ile się zmieści, i wydaje tyle wyjścia, ile pozwala bufor.
    /// Wynik nie zależy od podziału wejścia na porcje.
    /// </summary>
    /// <returns>Liczba ramek zapisanych w <paramref name="output"/>.</returns>
    public int Process(ReadOnlySpan<float> input, int inputFrames, Span<float> output, out int inputUsed)
    {
        if (_draining)
        {
            if (inputFrames > 0) throw new InvalidOperationException("Po wypłukaniu końcówki strumień się nie wydłuża.");

            // Końcówkę wydaje wyłącznie Drain.
            inputUsed = 0;
            return 0;
        }

        var maxOut = output.Length / _channels;
        var produced = 0;
        inputUsed = 0;

        while (true)
        {
            produced += Emit(output, produced, maxOut);
            if (inputUsed == inputFrames || produced == maxOut) break;

            var accepted = Append(input, inputUsed, inputFrames - inputUsed);
            if (accepted == 0) break;
            inputUsed += accepted;
        }

        return produced;
    }

    /// <summary>
    /// Wydaje końcówkę strumienia: ramki, które czekały na przyszłe wejście, liczone tak,
    /// jakby po ostatniej ramce była cisza. Zero oznacza koniec.
    /// </summary>
    public int Drain(Span<float> output)
    {
        if (!_draining)
        {
            _draining = true;
            _realEnd = _end;
            _zerosLeft = _bank.Lookahead;
        }

        var maxOut = output.Length / _channels;
        var produced = 0;

        while (true)
        {
            produced += Emit(output, produced, maxOut);
            if (produced == maxOut || _base >= _realEnd || _zerosLeft == 0) break;

            Compact();
            var zeros = Math.Min(_zerosLeft, _capacity - _end);
            if (zeros == 0) break;

            foreach (var plane in _planes) Array.Clear(plane, _end, zeros);
            _end += zeros;
            _zerosLeft -= zeros;
        }

        return produced;
    }

    private int Discardable => Math.Clamp(_base - _bank.History, 0, _end);

    private void Compact()
    {
        var shift = Discardable;
        if (shift == 0) return;

        var kept = _end - shift;
        foreach (var plane in _planes) Array.Copy(plane, shift, plane, 0, kept);

        _base -= shift;
        _end -= shift;
        _realEnd -= shift;
    }

    private int Append(ReadOnlySpan<float> input, int offset, int frames)
    {
        if (_capacity - _end < frames) Compact();

        var take = Math.Min(frames, _capacity - _end);
        if (take <= 0) return 0;

        var source = input.Slice(offset * _channels, take * _channels);

        if (_channels == 2)
        {
            var left = _planes[0].AsSpan(_end, take);
            var right = _planes[1].AsSpan(_end, take);
            for (var i = 0; i < take; i++)
            {
                left[i] = source[2 * i];
                right[i] = source[2 * i + 1];
            }
        }
        else
        {
            for (var c = 0; c < _channels; c++)
            {
                var plane = _planes[c].AsSpan(_end, take);
                for (var i = 0; i < take; i++) plane[i] = source[i * _channels + c];
            }
        }

        _end += take;
        return take;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Emit(Span<float> output, int offset, int maxOut)
    {
        var bank = _bank;
        var taps = bank.Taps;
        var history = bank.History;
        var lookahead = bank.Lookahead;
        var limit = _draining ? _realEnd : int.MaxValue;

        ref var coefficients = ref MemoryMarshal.GetArrayDataReference(bank.Coefficients);
        ref var deltas = ref bank.Interpolated
            ? ref MemoryMarshal.GetArrayDataReference(bank.Deltas!)
            : ref coefficients;

        var produced = 0;

        while (offset + produced < maxOut && _base + lookahead < _end && _base < limit)
        {
            var first = _base - history;
            var at = (offset + produced) * _channels;

            if (!bank.Interpolated)
            {
                ref var row = ref Unsafe.Add(ref coefficients, _phase * taps);
                for (var c = 0; c < _channels; c++)
                {
                    ref var samples = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_planes[c]), first);
                    output[at + c] = Dot(ref row, ref samples, taps);
                }
            }
            else
            {
                var position = (double)_phase * bank.Rows / bank.Up;
                var index = (int)position;
                var fraction = (float)(position - index);

                ref var row = ref Unsafe.Add(ref coefficients, index * taps);
                ref var delta = ref Unsafe.Add(ref deltas, index * taps);
                for (var c = 0; c < _channels; c++)
                {
                    ref var samples = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_planes[c]), first);
                    output[at + c] = DotInterpolated(ref row, ref delta, fraction, ref samples, taps);
                }
            }

            _base += _stepWhole;
            _phase += _stepRemainder;
            if (_phase >= bank.Up)
            {
                _phase -= bank.Up;
                _base++;
            }

            produced++;
        }

        return produced;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Load(ref float source, int index) =>
        Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref source, index)));

    /// <summary>Iloczyn skalarny; <paramref name="count"/> jest wielokrotnością długości wektora.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dot(ref float coefficients, ref float samples, int count)
    {
        var width = Vector<float>.Count;
        var first = Vector<float>.Zero;
        var second = Vector<float>.Zero;
        var i = 0;

        for (; i + 2 * width <= count; i += 2 * width)
        {
            first += Load(ref coefficients, i) * Load(ref samples, i);
            second += Load(ref coefficients, i + width) * Load(ref samples, i + width);
        }

        for (; i < count; i += width) first += Load(ref coefficients, i) * Load(ref samples, i);

        return Vector.Sum(first + second);
    }

    /// <summary>Współczynniki między dwoma wierszami tablicy faz: wiersz plus ułamek różnicy.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotInterpolated(ref float row, ref float delta, float fraction, ref float samples, int count)
    {
        var width = Vector<float>.Count;
        var direct = Vector<float>.Zero;
        var slope = Vector<float>.Zero;

        for (var i = 0; i < count; i += width)
        {
            var x = Load(ref samples, i);
            direct += Load(ref row, i) * x;
            slope += Load(ref delta, i) * x;
        }

        return Vector.Sum(direct) + fraction * Vector.Sum(slope);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    /// <summary>
    /// Współczynniki filtru dla pary częstotliwości i poziomu. Niezmienne po zbudowaniu,
    /// więc wspólne dla wszystkich egzemplarzy przez pamięć podręczną.
    /// </summary>
    internal sealed class FilterBank
    {
        /// <summary>Do tylu faz współczynniki liczone są dokładnie dla każdej z nich.</summary>
        private const int MaxExactPhases = 1280;

        /// <summary>Granica rozmiaru tablicy jednej pary (w liczbach float), żeby plik nie decydował o pamięci.</summary>
        private const int MaxTableSize = 1 << 20;

        /// <summary>
        /// Granica łącznego rozmiaru zapamiętanych tablic (w liczbach float, 64 MB). Pliki o dziwnych
        /// częstotliwościach nie mogą jej przesunąć; tablice ponad nią budowane są bez zapamiętywania.
        /// </summary>
        private const long MaxCachedSize = 16L << 20;

        private static readonly Dictionary<(int, int, ResamplingLevel), FilterBank> Cache = new();
        private static readonly Lock CacheLock = new();
        private static long _cachedSize;

        private FilterBank(int inputRate, int outputRate, ResamplingLevel level)
        {
            InputRate = inputRate;
            OutputRate = outputRate;
            Level = level;

            var divisor = Gcd(inputRate, outputRate);
            Up = outputRate / divisor;
            Down = inputRate / divisor;

            var design = Design.For(level);

            // Filtr liczony w częstotliwości niższej ze stron: przy zmniejszaniu skaluje się
            // do wyjścia, przy zwiększaniu do wejścia. Czas w ramkach wejścia.
            var scale = Math.Min(1.0, (double)outputRate / inputRate);
            var halfWidth = design.HalfWidth / scale;
            var side = (int)Math.Ceiling(halfWidth);

            var width = Vector<float>.Count;
            History = side - 1;
            Taps = (2 * side + width - 1) / width * width;

            Interpolated = Up > MaxExactPhases || (long)Up * Taps > MaxTableSize;
            Rows = Interpolated
                ? Math.Max(64, Math.Min(design.InterpolatedPhases, MaxTableSize / Taps))
                : Up;

            var kernel = new Kernel(scale, design.Cutoff, design.HalfWidth, design.Beta);

            if (!Interpolated)
            {
                Coefficients = new float[Up * Taps];
                var row = new double[Taps];
                for (var p = 0; p < Up; p++)
                {
                    FillRow(kernel, (double)p / Up, row);
                    for (var j = 0; j < Taps; j++) Coefficients[p * Taps + j] = (float)row[j];
                }
            }
            else
            {
                Coefficients = new float[Rows * Taps];
                Deltas = new float[Rows * Taps];

                var current = new double[Taps];
                var next = new double[Taps];
                FillRow(kernel, 0, current);

                for (var r = 0; r < Rows; r++)
                {
                    FillRow(kernel, (double)(r + 1) / Rows, next);
                    for (var j = 0; j < Taps; j++)
                    {
                        Coefficients[r * Taps + j] = (float)current[j];
                        Deltas[r * Taps + j] = (float)(next[j] - current[j]);
                    }

                    (current, next) = (next, current);
                }
            }
        }

        public int InputRate { get; }
        public int OutputRate { get; }
        public ResamplingLevel Level { get; }
        public int Up { get; }
        public int Down { get; }
        public int Taps { get; }

        /// <summary>Ramki wejścia przed ramką bazową, które filtr jeszcze czyta.</summary>
        public int History { get; }

        /// <summary>Ramki wejścia za ramką bazową, które filtr już czyta.</summary>
        public int Lookahead => Taps - History - 1;

        public bool Interpolated { get; }
        public int Rows { get; }
        public float[] Coefficients { get; }
        public float[]? Deltas { get; }

        public static FilterBank For(int inputRate, int outputRate, ResamplingLevel level)
        {
            if (inputRate is <= 0 or > MaxRate || outputRate is <= 0 or > MaxRate)
                throw new AudioException($"Nieobsługiwana częstotliwość próbkowania: {inputRate} Hz → {outputRate} Hz.");
            if (inputRate > (long)outputRate * MaxDownsamplingRatio)
                throw new AudioException($"Częstotliwość próbkowania {inputRate} Hz jest zbyt wysoka dla wyjścia {outputRate} Hz.");
            if (!Enum.IsDefined(level)) level = ResamplingLevel.Standard;

            var key = (inputRate, outputRate, level);
            lock (CacheLock)
                if (Cache.TryGetValue(key, out var cached)) return cached;

            // Budowa poza blokadą: wątek odtwarzania nie czeka na tablicę dla analizy w tle.
            // Dwa wątki mogą zbudować tę samą; obaj dostają tę, która trafiła do pamięci.
            var bank = new FilterBank(inputRate, outputRate, level);

            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                if (_cachedSize + bank.Size > MaxCachedSize) return bank;

                Cache[key] = bank;
                _cachedSize += bank.Size;
                return bank;
            }
        }

        private long Size => Coefficients.LongLength + (Deltas?.LongLength ?? 0);

        /// <summary>
        /// Współczynniki dla ułamka położenia <paramref name="fraction"/>: tap j czyta ramkę
        /// bazową − <see cref="History"/> + j. Wiersz sumuje się do jedności, więc składowa
        /// stała przechodzi bez zmian w każdej fazie.
        /// </summary>
        private void FillRow(Kernel kernel, double fraction, double[] row)
        {
            var sum = 0.0;
            for (var j = 0; j < Taps; j++)
            {
                row[j] = kernel.At(fraction + History - j);
                sum += row[j];
            }

            if (sum != 0)
                for (var j = 0; j < Taps; j++) row[j] /= sum;
        }
    }

    /// <summary>Okienkowany sinc w czasie ramek wejścia.</summary>
    private readonly struct Kernel(double scale, double cutoff, double halfWidth, double beta)
    {
        private readonly double _normaliser = 1 / BesselI0(beta);

        public double At(double time)
        {
            // Czas w ramkach częstotliwości niższej ze stron.
            var t = time * scale;
            var u = t / halfWidth;
            if (u <= -1 || u >= 1) return 0;

            var x = 2 * cutoff * t;
            var sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var window = BesselI0(beta * Math.Sqrt(1 - u * u)) * _normaliser;
            return scale * 2 * cutoff * sinc * window;
        }

        private static double BesselI0(double x)
        {
            var sum = 1.0;
            var term = 1.0;
            var half = x / 2;

            for (var k = 1; k < 200; k++)
            {
                term *= half / k * (half / k);
                sum += term;
                if (term < sum * 1e-17) break;
            }

            return sum;
        }
    }

    /// <summary>
    /// Parametry filtru w ułamkach częstotliwości niższej ze stron. Pasmo przepustowe 20/44,1
    /// sięga 20 kHz przy 44,1 kHz; zaporowe zaczyna się tam, skąd składowe odbite trafiłyby
    /// z powrotem poniżej 20 kHz (44,1 − 20 = 24,1 kHz).
    /// </summary>
    private readonly record struct Design(double Pass, double Stop, double Attenuation, int InterpolatedPhases)
    {
        private const double Audible = 20.0 / 44.1;

        public static Design For(ResamplingLevel level) => level switch
        {
            ResamplingLevel.Economy => new Design(16.0 / 44.1, 1 - Audible, 80, 256),
            ResamplingLevel.High => new Design(Audible, 1 - Audible, 135, 4096),
            _ => new Design(Audible, 1 - Audible, 105, 1024),
        };

        public double Cutoff => (Pass + Stop) / 2;

        /// <summary>Połowa długości filtru w ramkach częstotliwości niższej, ze wzoru Kaisera.</summary>
        public double HalfWidth => (Attenuation - 7.95) / (14.36 * (Stop - Pass)) / 2;

        public double Beta => 0.1102 * (Attenuation - 8.7);
    }
}
