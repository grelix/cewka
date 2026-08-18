namespace Cewka.Audio.Dsp;

/// <summary>
/// Feeds the waveform drawn above the seek bar.
/// <para>
/// The design shows three overlapping travelling waves. Rather than invent their motion,
/// each one is driven by the energy of one part of the spectrum — low, middle and high —
/// while the overall amplitude follows the signal envelope. The result moves with the music
/// instead of merely next to it.
/// </para>
/// <para>
/// Results are published as plain floats read by the interface thread. A torn read would
/// at worst show one frame from an adjacent analysis window, which nobody can see.
/// </para>
/// </summary>
public sealed class SpectrumAnalyser : IAudioProcessor
{
    /// <summary>Window size. At 48 kHz this is about 11 ms — fine enough to follow a beat.</summary>
    private const int WindowSize = 512;

    /// <summary>Analyse every this many frames; roughly 43 updates per second at 48 kHz.</summary>
    private const int HopSize = 1024;

    /// <summary>How quickly the published values fall back; rises are immediate.</summary>
    private const float Decay = 0.16f;

    private readonly float[] _window = new float[WindowSize];
    private readonly float[] _real = new float[WindowSize];
    private readonly float[] _imaginary = new float[WindowSize];
    private readonly float[] _hann = new float[WindowSize];

    private int _writeIndex;
    private int _framesSinceAnalysis;
    private int _channels = 2;
    private int _sampleRate = 48000;

    private float _level;
    private float _low;
    private float _mid;
    private float _high;

    public SpectrumAnalyser()
    {
        for (var i = 0; i < WindowSize; i++)
            _hann[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (WindowSize - 1)));
    }

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
        Reset();
    }

    public void Process(Span<float> buffer, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            // Mono sum: the display shows the piece, not the stereo image.
            var sum = 0f;
            var baseIndex = frame * _channels;
            for (var c = 0; c < _channels; c++) sum += buffer[baseIndex + c];

            _window[_writeIndex] = sum / _channels;
            _writeIndex = (_writeIndex + 1) % WindowSize;
        }

        _framesSinceAnalysis += frames;
        if (_framesSinceAnalysis < HopSize) return;

        _framesSinceAnalysis = 0;
        Analyse();
    }

    private void Analyse()
    {
        // Copy out in chronological order and apply the window.
        for (var i = 0; i < WindowSize; i++)
        {
            _real[i] = _window[(_writeIndex + i) % WindowSize] * _hann[i];
            _imaginary[i] = 0;
        }

        var rms = 0f;
        for (var i = 0; i < WindowSize; i++) rms += _real[i] * _real[i];
        rms = MathF.Sqrt(rms / WindowSize);

        Fft(_real, _imaginary);

        var binHz = _sampleRate / (float)WindowSize;
        float low = 0, mid = 0, high = 0;

        // Only the first half carries distinct information for a real input.
        for (var bin = 1; bin < WindowSize / 2; bin++)
        {
            var magnitude = MathF.Sqrt(_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]);
            var frequency = bin * binHz;

            if (frequency < 250) low += magnitude;
            else if (frequency < 4000) mid += magnitude;
            else high += magnitude;
        }

        var scale = 2f / WindowSize;
        Publish(ref _low, Normalise(low * scale));
        Publish(ref _mid, Normalise(mid * scale));
        Publish(ref _high, Normalise(high * scale));

        // Obwiednia jest hojna, żeby ciche fragmenty też pokazywały ruch — ale nie tak hojna,
        // jak była. Mnożnik cztery sprawiał, że przy zwykłej muzyce wartość stała przyklejona
        // do jedności i wykres fali prawie się nie ruszał, bo obcięcie zjadało całą zmienność.
        // Przy 2,6 poziom mieści się w zakresie i faktycznie podąża za muzyką.
        Publish(ref _level, Math.Clamp(rms * 2.6f, 0f, 1f));
    }

    private static float Normalise(float value) => Math.Clamp(MathF.Sqrt(value) * 1.6f, 0f, 1f);

    private static void Publish(ref float field, float value) =>
        field = value > field ? value : field + (value - field) * Decay;

    /// <summary>
    /// In-place iterative radix-2 FFT. Small and self-contained: pulling in a maths library
    /// for one 512-point transform per 20 ms would be a poor trade.
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
        Array.Clear(_window);
        _writeIndex = 0;
        _framesSinceAnalysis = 0;
        _level = _low = _mid = _high = 0;
    }
}
