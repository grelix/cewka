using System.Numerics;

namespace Cewka.Audio.Tests;

/// <summary>
/// Widmo amplitudowe do pomiarów konwersji: okno Blackmana-Harrisa siedmioczłonowe (listki
/// boczne poniżej −180 dB), więc składowe na poziomie −120 dB nie giną w przecieku tonu.
/// Amplitudy w skali sygnału: ton o amplitudzie A daje w swoim prążku około A.
/// </summary>
internal sealed class Spectrum
{
    private static readonly double[] Terms =
    [
        0.27105140069342, 0.43329793923448, 0.21812299954311, 0.06592544638803,
        0.01081174209837, 0.00077658482522, 0.00001388721735,
    ];

    /// <summary>Połowa szerokości listka głównego okna w prążkach, z zapasem.</summary>
    public const int MainLobeBins = 10;

    private readonly double[] _magnitudes;
    private readonly double[] _samples;
    private readonly double[] _window;
    private readonly double _windowSum;

    /// <param name="samples">Liczba próbek będąca potęgą dwójki.</param>
    public Spectrum(ReadOnlySpan<float> samples, int sampleRate)
    {
        var n = samples.Length;
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("Długość musi być potęgą dwójki.", nameof(samples));

        SampleRate = sampleRate;
        Length = n;

        _window = Window(n);
        _windowSum = _window.Sum();
        _samples = new double[n];
        for (var i = 0; i < n; i++) _samples[i] = samples[i];

        var data = new Complex[n];
        for (var i = 0; i < n; i++) data[i] = _samples[i] * _window[i];
        Fft(data);

        _magnitudes = new double[n / 2 + 1];
        for (var k = 0; k <= n / 2; k++) _magnitudes[k] = 2 * data[k].Magnitude / _windowSum;
    }

    public int SampleRate { get; }
    public int Length { get; }

    public double BinWidth => (double)SampleRate / Length;

    /// <summary>Amplituda składowej o dokładnie tej częstotliwości (DFT w jednym punkcie, z oknem).</summary>
    public double AmplitudeAt(double frequency)
    {
        double re = 0, im = 0;
        var omega = 2 * Math.PI * frequency / SampleRate;
        for (var i = 0; i < Length; i++)
        {
            var value = _samples[i] * _window[i];
            re += value * Math.Cos(omega * i);
            im -= value * Math.Sin(omega * i);
        }

        return 2 * Math.Sqrt(re * re + im * im) / _windowSum;
    }

    /// <summary>
    /// Największa składowa w paśmie [<paramref name="from"/>, <paramref name="to"/>] Hz poza listkiem
    /// głównym podanych częstotliwości. Zwraca amplitudę i częstotliwość prążka.
    /// </summary>
    public (double Amplitude, double Frequency) LargestComponent(double from, double to, params double[] excluded)
    {
        var first = Math.Max(1, (int)Math.Ceiling(from / BinWidth));
        var last = Math.Min(_magnitudes.Length - 1, (int)Math.Floor(to / BinWidth));

        var best = (0.0, 0.0);
        for (var k = first; k <= last; k++)
        {
            var skip = false;
            foreach (var f in excluded)
                if (Math.Abs(k - f / BinWidth) <= MainLobeBins) skip = true;
            if (skip) continue;

            if (_magnitudes[k] > best.Item1) best = (_magnitudes[k], k * BinWidth);
        }

        return best;
    }

    private static double[] Window(int n)
    {
        var window = new double[n];
        for (var i = 0; i < n; i++)
        {
            var value = 0.0;
            for (var k = 0; k < Terms.Length; k++)
                value += (k % 2 == 0 ? 1 : -1) * Terms[k] * Math.Cos(2 * Math.PI * k * i / n);
            window[i] = value;
        }

        return window;
    }

    private static void Fft(Complex[] data)
    {
        var n = data.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }

        for (var size = 2; size <= n; size <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
            for (var start = 0; start < n; start += size)
            {
                var w = Complex.One;
                for (var k = 0; k < size / 2; k++)
                {
                    var even = data[start + k];
                    var odd = data[start + k + size / 2] * w;
                    data[start + k] = even + odd;
                    data[start + k + size / 2] = even - odd;
                    w *= step;
                }
            }
        }
    }
}
