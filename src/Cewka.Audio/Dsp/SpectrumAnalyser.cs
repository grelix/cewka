namespace Cewka.Audio.Dsp;

/// <summary>
/// Zasila falę nad paskiem przewijania: każdą z trzech fal napędza energia jednego pasma
/// (niskie, środek, wysokie), a amplituda całości idzie za obwiednią sygnału.
/// <para>
/// Wyniki to zwykłe liczby czytane przez wątek interfejsu bez blokady; rozdarty odczyt pokaże
/// najwyżej klatkę z sąsiedniego okna analizy.
/// </para>
/// </summary>
public sealed class SpectrumAnalyser : IAudioProcessor
{
    /// <summary>
    /// Rozmiar okna przy 48 kHz, około 11 ms: dość krótko, żeby nadążyć za rytmem. Przy innym
    /// próbkowaniu okno rośnie lub maleje w potęgach dwójki, żeby szerokość prążka, a z nią podział
    /// na pasma, została mniej więcej ta sama.
    /// </summary>
    private const int ReferenceWindowSize = 512;
    private const int ReferenceSampleRate = 48000;

    /// <summary>Stała czasowa opadania wskaźników; wzrost jest natychmiastowy.</summary>
    private const double DecaySeconds = 0.12;

    private int _windowSize;
    private int _hopSize;
    private float[] _window = [];
    private float[] _real = [];
    private float[] _imaginary = [];
    private float[] _hann = [];

    private int _writeIndex;
    private int _framesSinceAnalysis;
    private int _framesSincePublish;
    private int _channels = 2;
    private int _sampleRate = 48000;

    private int _outputLatencyFrames;

    private float _level;
    private float _low;
    private float _mid;
    private float _high;

    public SpectrumAnalyser() => Allocate(ReferenceSampleRate);

    private void Allocate(int sampleRate)
    {
        var octaves = Math.Round(Math.Log2(sampleRate / (double)ReferenceSampleRate));
        var size = Math.Clamp((int)(ReferenceWindowSize * Math.Pow(2, octaves)), 256, 4096);

        // Analiza co dwa okna: przy 48 kHz około 47 razy na sekundę.
        _hopSize = 2 * size;

        if (size == _windowSize) return;

        _windowSize = size;
        _window = new float[size];
        _real = new float[size];
        _imaginary = new float[size];
        _hann = new float[size];
        for (var i = 0; i < size; i++)
            _hann[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (size - 1)));
    }

    /// <summary>Pierścień próbek L/P po korektorze, czytany przez <see cref="SceneAudio"/>.</summary>
    public SampleRing Ring { get; } = new();

    /// <summary>
    /// Szacowane opóźnienie między zapisem do pierścienia a dźwiękiem z głośnika, w ramkach.
    /// Ustawia silnik po otwarciu urządzenia; odczyty cofają się o tyle, żeby obraz nie wyprzedzał dźwięku.
    /// </summary>
    public int OutputLatencyFrames
    {
        get => Volatile.Read(ref _outputLatencyFrames);
        set => Volatile.Write(ref _outputLatencyFrames, Math.Max(0, value));
    }

    public int SampleRate => _sampleRate;

    /// <summary>Overall envelope, 0 to 1.</summary>
    public float Level => _level;

    /// <summary>Energy below 250 Hz, 0 to 1.</summary>
    public float LowBand => _low;

    /// <summary>Energy between 250 Hz and 4 kHz, 0 to 1.</summary>
    public float MidBand => _mid;

    /// <summary>Energy above 4 kHz, 0 to 1.</summary>
    public float HighBand => _high;

    public void Prepare(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        Allocate(sampleRate);
        Reset();
    }

    public void Process(Span<float> buffer, int frames)
    {
        Ring.Write(buffer, frames, _channels);

        for (var frame = 0; frame < frames; frame++)
        {
            // Mono sum: the display shows the piece, not the stereo image.
            var sum = 0f;
            var baseIndex = frame * _channels;
            for (var c = 0; c < _channels; c++) sum += buffer[baseIndex + c];

            _window[_writeIndex] = sum / _channels;
            _writeIndex = (_writeIndex + 1) % _windowSize;
        }

        _framesSinceAnalysis += frames;
        _framesSincePublish += frames;
        if (_framesSinceAnalysis < _hopSize) return;

        // Reszta ponad krok przechodzi na następną analizę, więc ich średnia częstość nie zależy
        // od rozmiaru bufora. Opadanie liczone jest z czasu, który faktycznie upłynął.
        _framesSinceAnalysis %= _hopSize;
        var decay = (float)(1 - Math.Exp(-_framesSincePublish / (_sampleRate * DecaySeconds)));
        _framesSincePublish = 0;
        Analyse(decay);
    }

    private void Analyse(float decay)
    {
        var size = _windowSize;

        // Copy out in chronological order and apply the window.
        for (var i = 0; i < size; i++)
        {
            _real[i] = _window[(_writeIndex + i) % size] * _hann[i];
            _imaginary[i] = 0;
        }

        var rms = 0f;
        for (var i = 0; i < size; i++) rms += _real[i] * _real[i];
        rms = MathF.Sqrt(rms / size);

        Fft(_real, _imaginary);

        var binHz = _sampleRate / (float)size;
        float low = 0, mid = 0, high = 0;

        // Only the first half carries distinct information for a real input.
        for (var bin = 1; bin < size / 2; bin++)
        {
            var magnitude = MathF.Sqrt(_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]);
            var frequency = bin * binHz;

            if (frequency < 250) low += magnitude;
            else if (frequency < 4000) mid += magnitude;
            else high += magnitude;
        }

        var scale = 2f / size;
        Publish(ref _low, Normalise(low * scale), decay);
        Publish(ref _mid, Normalise(mid * scale), decay);
        Publish(ref _high, Normalise(high * scale), decay);

        // Obwiednia jest hojna, żeby ciche fragmenty też pokazywały ruch. Przy większym mnożniku
        // zwykła muzyka przyklejałaby wartość do jedności i obcięcie zjadałoby całą zmienność.
        Publish(ref _level, Math.Clamp(rms * 2.6f, 0f, 1f), decay);
    }

    private static float Normalise(float value) => Math.Clamp(MathF.Sqrt(value) * 1.6f, 0f, 1f);

    private static void Publish(ref float field, float value, float decay)
    {
        // NaN raz zapisany zostałby na zawsze: każde porównanie z nim jest fałszywe.
        if (float.IsNaN(value) || float.IsNaN(field))
        {
            field = 0;
            return;
        }

        field = value > field ? value : field + (value - field) * decay;

        // W ciszy wartość opadałaby w liczby subnormalne.
        if (field < 1e-6f) field = 0;
    }

    /// <summary>
    /// In-place iterative radix-2 FFT. Small and self-contained: pulling in a maths library
    /// for one short transform per 20 ms would be a poor trade.
    /// </summary>
    private static void Fft(float[] real, float[] imaginary)
    {
        var n = real.Length;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * MathF.PI / length;
            var wReal = MathF.Cos(angle);
            var wImaginary = MathF.Sin(angle);

            for (var start = 0; start < n; start += length)
            {
                float currentReal = 1, currentImaginary = 0;

                for (var k = 0; k < length / 2; k++)
                {
                    var evenIndex = start + k;
                    var oddIndex = evenIndex + length / 2;

                    var oddReal = real[oddIndex] * currentReal - imaginary[oddIndex] * currentImaginary;
                    var oddImaginary = real[oddIndex] * currentImaginary + imaginary[oddIndex] * currentReal;

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImaginary;

                    var nextReal = currentReal * wReal - currentImaginary * wImaginary;
                    currentImaginary = currentReal * wImaginary + currentImaginary * wReal;
                    currentReal = nextReal;
                }
            }
        }
    }

    public void Reset()
    {
        Ring.Clear();
        Array.Clear(_window);
        _writeIndex = 0;
        _framesSinceAnalysis = 0;
        _framesSincePublish = 0;
        _level = _low = _mid = _high = 0;
    }
}
