namespace Cewka.Audio.Dsp;

/// <summary>
/// Widmo dla wizualizacji: FFT 4096 z oknem Hanna z sumy kanałów, zebrane w logarytmiczne pasma
/// 30 Hz–16 kHz. Liczone na wątku rysowania, na żądanie; po konstrukcji bez alokacji.
/// Nie jest bezpieczne wątkowo: jeden czytelnik.
/// </summary>
public sealed class BandSpectrum
{
    public const int FftSize = 4096;

    /// <summary>Liczba pasm: około 1/5,3 oktawy; zakres 30 Hz–16 kHz to 9,06 oktawy.</summary>
    public const int BandCount = 48;

    public const double LowFrequency = 30;
    public const double HighFrequency = 16000;

    private const int Bits = 12;

    private readonly float[] _left = new float[FftSize];
    private readonly float[] _right = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imaginary = new float[FftSize];
    private readonly float[] _hann = new float[FftSize];
    private readonly float[] _cos = new float[FftSize / 2];
    private readonly float[] _sin = new float[FftSize / 2];
    private readonly int[] _reverse = new int[FftSize];
    private readonly int[] _firstBin = new int[BandCount];
    private readonly int[] _lastBin = new int[BandCount];
    private readonly float[] _bands = new float[BandCount];
    private readonly float _amplitudeScale;

    private int _sampleRate;

    public BandSpectrum()
    {
        double windowSum = 0;
        for (var i = 0; i < FftSize; i++)
        {
            _hann[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (FftSize - 1)));
            windowSum += _hann[i];
        }

        // Sinus o amplitudzie A daje w prążku szczytowym wartość A.
        _amplitudeScale = (float)(2 / windowSum);

        for (var i = 0; i < FftSize / 2; i++)
        {
            var angle = -2 * Math.PI * i / FftSize;
            _cos[i] = (float)Math.Cos(angle);
            _sin[i] = (float)Math.Sin(angle);
        }

        for (var i = 0; i < FftSize; i++)
        {
            var reversed = 0;
            for (var b = 0; b < Bits; b++)
                if ((i & (1 << b)) != 0) reversed |= 1 << (Bits - 1 - b);
            _reverse[i] = reversed;
        }

        Prepare(48000);
    }

    /// <summary>Wyniki ostatniego obliczenia: amplituda 0 i więcej, 1 = sinus pełnej skali.</summary>
    public ReadOnlySpan<float> Bands => _bands;

    public int SampleRate => _sampleRate;

    /// <summary>Dolna krawędź pasma w hercach; indeks <see cref="BandCount"/> to górna krawędź ostatniego.</summary>
    public static double BandEdge(int index) =>
        LowFrequency * Math.Pow(HighFrequency / LowFrequency, index / (double)BandCount);

    /// <summary>Środek geometryczny pasma w hercach.</summary>
    public static double BandCenter(int index) =>
        LowFrequency * Math.Pow(HighFrequency / LowFrequency, (index + 0.5) / BandCount);

    /// <summary>Przypisuje pasmom prążki dla danego próbkowania. Wołane przy zmianie formatu.</summary>
    public void Prepare(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        var binHz = _sampleRate / (double)FftSize;
        var nyquistBin = FftSize / 2 - 1;

        for (var band = 0; band < BandCount; band++)
        {
            var first = (int)Math.Ceiling(BandEdge(band) / binHz);
            var last = (int)Math.Floor(BandEdge(band + 1) / binHz);

            // Pasmo węższe od prążka bierze prążek najbliższy środka.
            if (last < first) first = last = (int)Math.Round(BandCenter(band) / binHz);

            _firstBin[band] = Math.Clamp(first, 1, nyquistBin);
            _lastBin[band] = Math.Clamp(last, _firstBin[band], nyquistBin);
        }

        Array.Clear(_bands);
    }

    /// <summary>Liczy pasma z najnowszych próbek pierścienia, przesuniętych o <paramref name="delayFrames"/>.</summary>
    public ReadOnlySpan<float> Compute(SampleRing ring, int delayFrames)
    {
        ring.Read(_left, _right, delayFrames);
        return ComputeFromSamples();
    }

    /// <summary>Liczy pasma z gotowych <see cref="FftSize"/> próbek kanałów.</summary>
    public ReadOnlySpan<float> Compute(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        left[..FftSize].CopyTo(_left);
        right[..FftSize].CopyTo(_right);
        return ComputeFromSamples();
    }

    private ReadOnlySpan<float> ComputeFromSamples()
    {
        for (var i = 0; i < FftSize; i++)
        {
            var mono = 0.5f * (_left[i] + _right[i]);
            if (!float.IsFinite(mono)) mono = 0;
            _real[_reverse[i]] = mono * _hann[i];
            _imaginary[_reverse[i]] = 0;
        }

        Transform();

        for (var band = 0; band < BandCount; band++)
        {
            var peak = 0f;
            for (var bin = _firstBin[band]; bin <= _lastBin[band]; bin++)
            {
                var power = _real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin];
                if (power > peak) peak = power;
            }

            _bands[band] = MathF.Sqrt(peak) * _amplitudeScale;
        }

        return _bands;
    }

    /// <summary>Iteracyjne FFT radix-2 w miejscu; wejście już w kolejności bitowo odwróconej.</summary>
    private void Transform()
    {
        for (var length = 2; length <= FftSize; length <<= 1)
        {
            var half = length >> 1;
            var step = FftSize / length;

            for (var start = 0; start < FftSize; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wReal = _cos[k * step];
                    var wImaginary = _sin[k * step];
                    var even = start + k;
                    var odd = even + half;

                    var oddReal = _real[odd] * wReal - _imaginary[odd] * wImaginary;
                    var oddImaginary = _real[odd] * wImaginary + _imaginary[odd] * wReal;

                    _real[odd] = _real[even] - oddReal;
                    _imaginary[odd] = _imaginary[even] - oddImaginary;
                    _real[even] += oddReal;
                    _imaginary[even] += oddImaginary;
                }
            }
        }
    }
}
