namespace Cewka.Audio.Dsp;

/// <summary>RMS i szczyt każdego kanału w oknie próbek; liniowo, 1 = pełna skala.</summary>
public readonly record struct ChannelLevels(float RmsLeft, float RmsRight, float PeakLeft, float PeakRight);

/// <summary>
/// Jedyne miejsce, z którego wizualizacje czytają dane dźwięku: przebieg L/P, RMS i szczyt kanałów,
/// widmo w pasmach logarytmicznych oraz obwiednia i trzy pasma z <see cref="SpectrumAnalyser"/>.
/// <para>
/// Odczyty próbek są przesunięte o opóźnienie wyjścia (<see cref="SpectrumAnalyser.OutputLatencyFrames"/>),
/// więc obraz odpowiada temu, co właśnie słychać, a nie temu, co dopiero trafia do urządzenia.
/// Odczyty woła wyłącznie wątek rysowania, jeden czytelnik; nie alokują.
/// W pauzie wątek dźwiękowy nie dopisuje próbek, więc dane stoją; scena w pauzie w ogóle nie czyta.
/// </para>
/// </summary>
public sealed class SceneAudio
{
    /// <summary>Największe okno pojedynczego odczytu przebiegu i poziomów.</summary>
    public const int MaxWindowFrames = 4096;

    private readonly SpectrumAnalyser _analyser;
    private readonly BandSpectrum _spectrum = new();
    private readonly float[] _left = new float[MaxWindowFrames];
    private readonly float[] _right = new float[MaxWindowFrames];

    public SceneAudio(SpectrumAnalyser analyser)
    {
        _analyser = analyser;
        _spectrum.Prepare(analyser.SampleRate);
    }

    /// <summary>Obwiednia całości, 0 do 1.</summary>
    public float Level => _analyser.Level;

    /// <summary>Energia poniżej 250 Hz, 0 do 1.</summary>
    public float LowBand => _analyser.LowBand;

    /// <summary>Energia od 250 Hz do 4 kHz, 0 do 1.</summary>
    public float MidBand => _analyser.MidBand;

    /// <summary>Energia powyżej 4 kHz, 0 do 1.</summary>
    public float HighBand => _analyser.HighBand;

    /// <summary>Próbkowanie, dla którego liczone są pasma.</summary>
    public int SampleRate => _analyser.SampleRate;

    public static int BandCount => BandSpectrum.BandCount;

    /// <summary>Środek pasma w hercach.</summary>
    public static double BandCenter(int index) => BandSpectrum.BandCenter(index);

    /// <summary>
    /// Kopiuje ostatnie <c>left.Length</c> ramek przebiegu (z uwzględnieniem opóźnienia wyjścia).
    /// False, gdy zapis wyprzedził odczyt; ramki sprzed początku nagrania są zerami.
    /// </summary>
    public bool ReadWaveform(Span<float> left, Span<float> right) =>
        _analyser.Ring.Read(left, right, _analyser.OutputLatencyFrames);

    /// <summary>RMS i szczyt kanałów z ostatnich <paramref name="windowFrames"/> ramek.</summary>
    public ChannelLevels ReadLevels(int windowFrames = 1024)
    {
        windowFrames = Math.Clamp(windowFrames, 1, MaxWindowFrames);
        var left = _left.AsSpan(0, windowFrames);
        var right = _right.AsSpan(0, windowFrames);
        if (!_analyser.Ring.Read(left, right, _analyser.OutputLatencyFrames)) return default;

        Measure(left, out var rmsLeft, out var peakLeft);
        Measure(right, out var rmsRight, out var peakRight);
        return new ChannelLevels(rmsLeft, rmsRight, peakLeft, peakRight);
    }

    /// <summary>
    /// Liczy widmo FFT 4096 w <see cref="BandCount"/> pasmach logarytmicznych 30 Hz–16 kHz.
    /// Wartość to amplituda liniowa (1 = sinus pełnej skali); cisza daje zera. Zwrócony zakres
    /// jest ważny do następnego wywołania.
    /// </summary>
    public ReadOnlySpan<float> ReadSpectrum()
    {
        if (_spectrum.SampleRate != _analyser.SampleRate) _spectrum.Prepare(_analyser.SampleRate);
        return _spectrum.Compute(_analyser.Ring, _analyser.OutputLatencyFrames);
    }

    private static void Measure(ReadOnlySpan<float> samples, out float rms, out float peak)
    {
        double sum = 0;
        var max = 0f;
        foreach (var sample in samples)
        {
            var value = float.IsFinite(sample) ? sample : 0;
            sum += (double)value * value;
            var magnitude = MathF.Abs(value);
            if (magnitude > max) max = magnitude;
        }

        rms = (float)Math.Sqrt(sum / samples.Length);
        peak = max;
    }
}
