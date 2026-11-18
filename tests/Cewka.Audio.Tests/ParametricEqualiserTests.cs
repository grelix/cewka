using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>
/// Korektor parametryczny: charakterystyka zgodna ze wzorem, przepust bez zmiany próbek
/// i zmiany w trakcie gry bez trzasku. Miarą trzasku jest największa różnica sąsiednich próbek
/// przy tonie ciągłym, porównana z największym krokiem samego sinusa.
/// </summary>
public class ParametricEqualiserTests
{
    private const int SampleRate = 48000;
    private const int Block = 480;

    /// <summary>Ton niski: jego naturalny krok jest mały, więc skok współczynników od razu wystaje.</summary>
    private const double ToneHz = 220;

    private const double ToneAmplitude = 0.25;

    /// <summary>Zestaw AutoEq dla Sennheiser HD 650.</summary>
    private static readonly ParametricBand[] Hd650 =
    [
        new(1, ParametricFilterType.LowShelf, 105, 6.4, 0.70),
        new(2, ParametricFilterType.Peak, 8800, 5.1, 1.42),
        new(3, ParametricFilterType.HighShelf, 10000, -2.1, 0.70),
        new(4, ParametricFilterType.Peak, 5332, -1.1, 5.75),
    ];

    private const double Hd650Preamp = -6.1;

    private static double NaturalStep(double frequency, double amplitude) =>
        1.2 * amplitude * 2 * Math.PI * frequency / SampleRate;

    // ================= charakterystyka =================

    /// <summary>
    /// Pomiar tonami zgadza się z charakterystyką liczoną ze wzorów Audio EQ Cookbook, zapisanych
    /// tu niezależnie od kodu toru, z dokładnością 0,1 dB.
    /// </summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Hd650PresetMatchesTheCookbookFormula(int sampleRate)
    {
        var equaliser = new ParametricEqualiser { Preamp = Hd650Preamp };
        equaliser.SetBands(Hd650);
        equaliser.Prepare(sampleRate, Channels);

        int[] frequencies = [30, 60, 105, 200, 500, 1000, 2000, 4000, 5332, 7000, 8800, 10000, 12000, 15000, 19000];

        foreach (var frequency in frequencies)
        {
            // Pół sekundy na ustalenie, potem okno o całkowitej liczbie okresów (całe sekundy).
            var tone = Sine(frequency, 0.1, sampleRate * 3 / 2, sampleRate);
            equaliser.Reset();
            ProcessInBlocks(equaliser, tone, Block);

            var measured = Db(Goertzel(tone, 0, frequency, sampleRate, skipFrames: sampleRate / 2) / 0.1);
            var expected = CookbookResponseDb(Hd650, Hd650Preamp, frequency, sampleRate);

            Assert.True(Math.Abs(measured - expected) <= 0.1,
                $"{sampleRate} Hz, ton {frequency} Hz: zmierzono {measured:F3} dB, wzór {expected:F3} dB");
        }
    }

    /// <summary>Charakterystyka dla wykresu jest tą samą, którą daje wzór.</summary>
    [Fact]
    public void ResponseForTheChartFollowsTheFormula()
    {
        double[] frequencies = [20, 105, 1000, 5332, 8800, 10000, 20000];
        var result = new double[frequencies.Length];

        ParametricEqualiser.Response(Hd650, Hd650Preamp, SampleRate, frequencies, result);

        for (var i = 0; i < frequencies.Length; i++)
            Assert.Equal(CookbookResponseDb(Hd650, Hd650Preamp, frequencies[i], SampleRate), result[i], 6);
    }

    /// <summary>Wyłączone pasmo nie liczy się ani w torze, ani w charakterystyce.</summary>
    [Fact]
    public void DisabledBandIsLeftOut()
    {
        ParametricBand[] bands = [new(1, ParametricFilterType.Peak, 1000, 12, 1, Enabled: false)];
        var result = new double[1];
        ParametricEqualiser.Response(bands, 0, SampleRate, [1000.0], result);
        Assert.Equal(0, result[0], 9);

        var equaliser = new ParametricEqualiser();
        equaliser.SetBands(bands);
        equaliser.Prepare(SampleRate, Channels);

        var original = Sine(1000, 0.25, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(equaliser, processed, Block);
        Assert.Equal(original, processed);
    }

    /// <summary>Wartości spoza zakresów są przycinane, a pasm nie ma więcej niż 20.</summary>
    [Fact]
    public void BandsAreClampedAndLimited()
    {
        var equaliser = new ParametricEqualiser { Preamp = -40 };
        equaliser.SetBands(Enumerable.Range(1, 25)
            .Select(i => new ParametricBand(i, ParametricFilterType.Peak, 5, 99, 100)));

        Assert.Equal(-12, equaliser.Preamp);
        Assert.Equal(ParametricEqualiser.MaxBands, equaliser.Bands.Count);
        Assert.All(equaliser.Bands, band =>
        {
            Assert.Equal(20, band.Frequency);
            Assert.Equal(24, band.Gain);
            Assert.Equal(20, band.Q);
        });

        equaliser.SetBands([new ParametricBand(1, (ParametricFilterType)7, double.NaN, double.PositiveInfinity, 0)]);
        var band = equaliser.Bands[0];
        Assert.Equal(ParametricFilterType.Peak, band.Type);
        Assert.Equal(1000, band.Frequency);
        Assert.Equal(0, band.Gain);
        Assert.Equal(0.1, band.Q);

        Assert.Throws<ArgumentException>(() => equaliser.SetBands(
            [new ParametricBand(3, ParametricFilterType.Peak, 100, 1, 1), new ParametricBand(3, ParametricFilterType.Peak, 200, 1, 1)]));
    }

    /// <summary>
    /// Półka górna przy częstotliwości ponad 0,92 częstotliwości Nyquista nie przechodzi w przepust,
    /// tylko zatrzymuje się na granicy: przesuwanie pasma w górę nie daje przeskoku.
    /// </summary>
    [Fact]
    public void HighShelfStopsBelowNyquistInsteadOfVanishing()
    {
        ParametricBand[] bands = [new(1, ParametricFilterType.HighShelf, 20000, 12, 0.7)];
        var result = new double[1];
        ParametricEqualiser.Response(bands, 0, 44100, [21000.0], result);
        Assert.True(result[0] > 6, $"{result[0]:F2} dB");
    }

    // ================= przepust =================

    /// <summary>Etap płaski (pasma na 0 dB, przedwzmacniacz 0 dB) nie zmienia żadnej próbki.</summary>
    [Fact]
    public void FlatStagePassesSamplesUntouched()
    {
        var equaliser = new ParametricEqualiser();
        equaliser.SetBands([new ParametricBand(1, ParametricFilterType.LowShelf, 100, 0, 0.7)]);
        equaliser.Prepare(SampleRate, Channels);

        var original = Sine(1000, 0.25, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(equaliser, processed, Block);
        Assert.Equal(original, processed);
    }

    /// <summary>Wyłączenie w trakcie gry wygasza pasma płynnie, a potem etap przepuszcza próbki bez zmiany.</summary>
    [Fact]
    public void DisablingFadesOutThenBypasses()
    {
        var equaliser = new ParametricEqualiser { Preamp = -6 };
        equaliser.SetBands([new ParametricBand(1, ParametricFilterType.Peak, ToneHz, 12, 2)]);
        equaliser.Prepare(SampleRate, Channels);

        var tone = Sine(ToneHz, ToneAmplitude, 2 * SampleRate, SampleRate);
        ProcessRange(equaliser, tone, 0, SampleRate);
        equaliser.Enabled = false;
        ProcessRange(equaliser, tone, SampleRate, 2 * SampleRate);

        AssertNoClick(tone);

        var original = Sine(ToneHz, ToneAmplitude, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(equaliser, processed, Block);
        Assert.Equal(original, processed);
    }

    /// <summary>Usunięte pasmo wygasa, a gdy nic nie zostało, etap znowu przepuszcza próbki bez zmiany.</summary>
    [Fact]
    public void RemovedBandFadesOutThenBypasses()
    {
        var equaliser = new ParametricEqualiser();
        equaliser.SetBands([new ParametricBand(1, ParametricFilterType.LowShelf, 400, -9, 0.7)]);
        equaliser.Prepare(SampleRate, Channels);

        var tone = Sine(ToneHz, ToneAmplitude, 2 * SampleRate, SampleRate);
        ProcessRange(equaliser, tone, 0, SampleRate);
        equaliser.SetBands([]);
        ProcessRange(equaliser, tone, SampleRate, 2 * SampleRate);

        AssertNoClick(tone);

        var original = Sine(ToneHz, ToneAmplitude, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(equaliser, processed, Block);
        Assert.Equal(original, processed);
    }

    // ================= zmiany bez trzasku =================

    public static TheoryData<string> Changes => new()
    {
        "dodanie pasma", "częstotliwość", "wzmocnienie", "dobroć", "rodzaj", "wyłącznik pasma",
        "usunięcie pasma", "liczba pasm", "wymiana wszystkich pasm", "przedwzmacniacz",
    };

    /// <summary>
    /// Każda zmiana konfiguracji w trakcie tonu ciągłego: przebieg nie ma skoku większego niż
    /// naturalny krok tonu o największej amplitudzie, jaką etap mógł dać.
    /// </summary>
    [Theory]
    [MemberData(nameof(Changes))]
    public void ChangeDuringPlaybackHasNoClick(string change)
    {
        ParametricBand[] before =
        [
            new(1, ParametricFilterType.Peak, ToneHz, 12, 4),
            new(2, ParametricFilterType.LowShelf, 150, -6, 0.7),
        ];

        ParametricBand[] after = change switch
        {
            "dodanie pasma" => [.. before, new(3, ParametricFilterType.HighShelf, 200, 12, 2)],
            "częstotliwość" => [before[0] with { Frequency = 2400 }, before[1]],
            "wzmocnienie" => [before[0] with { Gain = -18 }, before[1]],
            "dobroć" => [before[0] with { Q = 0.3 }, before[1]],
            "rodzaj" => [before[0] with { Type = ParametricFilterType.HighShelf }, before[1]],
            "wyłącznik pasma" => [before[0] with { Enabled = false }, before[1]],
            "usunięcie pasma" => [before[1]],
            "liczba pasm" => [.. Enumerable.Range(10, 20).Select(i =>
                new ParametricBand(i, ParametricFilterType.Peak, 100 + 20 * i, i % 2 == 0 ? 6 : -6, 3))],
            "wymiana wszystkich pasm" => [
                new(7, ParametricFilterType.HighShelf, 180, 10, 1),
                new(8, ParametricFilterType.Peak, 300, -12, 8)],
            _ => before,
        };

        var equaliser = new ParametricEqualiser();
        equaliser.SetBands(before);
        equaliser.Prepare(SampleRate, Channels);

        var tone = Sine(ToneHz, ToneAmplitude, 3 * SampleRate, SampleRate);
        ProcessRange(equaliser, tone, 0, SampleRate);
        equaliser.SetBands(after);
        if (change == "przedwzmacniacz") equaliser.Preamp = 12;
        ProcessRange(equaliser, tone, SampleRate, 3 * SampleRate);

        AssertNoClick(tone);
        Assert.True(AllFinite(tone));
    }

    /// <summary>
    /// Przełączenie trybu korektora w torze: graficzny wygasa, parametryczny narasta, bez trzasku,
    /// a po przełączeniu gra wyłącznie parametryczny.
    /// </summary>
    [Fact]
    public void ModeSwitchInTheGraphHasNoClick()
    {
        var graph = new AudioGraph();
        graph.Limiter.Enabled = false;
        graph.Equaliser.SetGain(3, 12);   // 250 Hz
        graph.Parametric.SetBands([new ParametricBand(1, ParametricFilterType.Peak, 1000, 12, 1)]);
        graph.Prepare(SampleRate, Channels);

        var tone = Sine(ToneHz, ToneAmplitude, 3 * SampleRate, SampleRate);
        ProcessGraphRange(graph, tone, 0, SampleRate);
        graph.Equaliser.Enabled = false;
        graph.Parametric.Enabled = true;
        ProcessGraphRange(graph, tone, SampleRate, 3 * SampleRate);

        AssertNoClick(tone);

        // Na końcu sam parametryczny: 220 Hz leży na zboczu dzwonu 1 kHz, daleko od 250 Hz graficznego.
        var expected = CookbookResponseDb(graph.Parametric.Bands.ToArray(), 0, ToneHz, SampleRate);
        var measured = Db(Goertzel(tone, 0, ToneHz, SampleRate, skipFrames: 2 * SampleRate) / ToneAmplitude);
        Assert.True(Math.Abs(measured - expected) < 0.1, $"zmierzono {measured:F2} dB, wzór {expected:F2} dB");
    }

    /// <summary>Wątek dźwiękowy niczego nie przydziela, także w buforze, w którym przychodzi nowa konfiguracja.</summary>
    [Fact]
    public void ProcessDoesNotAllocate()
    {
        var equaliser = new ParametricEqualiser();
        equaliser.SetBands(Hd650);
        equaliser.Prepare(SampleRate, Channels);

        var buffer = Sine(ToneHz, ToneAmplitude, Block, SampleRate);
        equaliser.Process(buffer, Block);

        ParametricBand[] other = [.. Hd650.Skip(1), new ParametricBand(9, ParametricFilterType.Peak, 300, 3, 1)];
        var flip = false;

        // Przed każdą próbą nowa konfiguracja (na zmianę dwa zestawy), więc mierzony jest bufor ze zmianą.
        var allocated = Allocations.Smallest(5,
            () =>
            {
                flip = !flip;
                equaliser.SetBands(flip ? other : Hd650);
                equaliser.Preamp = flip ? -3 : 0;
            },
            () =>
            {
                for (var i = 0; i < 50; i++) equaliser.Process(buffer, Block);
            });

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// Konfiguracja zmieniana bez przerwy z innego wątku w trakcie przetwarzania: wynik skończony,
    /// bez wyjątku i bez zawieszenia.
    /// </summary>
    [Fact]
    public async Task ConfigurationChangesFromAnotherThreadAreSafe()
    {
        var equaliser = new ParametricEqualiser();
        equaliser.Prepare(SampleRate, Channels);

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var random = new Random(1234);
            var id = 0;
            while (!stop.IsCancellationRequested)
            {
                var count = random.Next(0, 21);
                equaliser.SetBands(Enumerable.Range(0, count).Select(_ => new ParametricBand(
                    ++id, (ParametricFilterType)random.Next(3), random.Next(20, 20000), random.Next(-24, 25), 0.1 + random.NextDouble() * 19.9,
                    random.Next(4) > 0)));
                equaliser.Preamp = random.Next(-12, 13);
                equaliser.Enabled = random.Next(5) > 0;
            }
        });

        var tone = Sine(ToneHz, ToneAmplitude, 4 * SampleRate, SampleRate);
        ProcessInBlocks(equaliser, tone, Block);
        stop.Cancel();
        await writer;

        Assert.True(AllFinite(tone));
    }

    // ---------- pomocnicze ----------

    /// <summary>
    /// Granicą jest naturalny krok tonu o największej amplitudzie, jaka wystąpiła w wyniku. Trzask,
    /// czyli zamiana współczynników przy niepasującym stanie filtru, daje skok rzędu samej amplitudy,
    /// a naturalny krok tonu 220 Hz to około 3,5% amplitudy.
    /// </summary>
    private static void AssertNoClick(float[] tone)
    {
        // Amplituda w stanach ustalonych: przed zmianą (pół sekundy przed nią) i na końcu.
        var frames = tone.Length / Channels;
        var before = Peak(tone[(SampleRate / 2 * Channels)..(SampleRate * Channels)]);
        var after = Peak(tone, skipFrames: frames - SampleRate / 2);
        var limit = NaturalStep(ToneHz, Math.Max(before, after));
        var step = LargestStep(tone, 0, fromFrame: 4800);
        Assert.True(step < limit, $"skok {step:F4} przy granicy {limit:F4}");
    }

    private static void ProcessGraphRange(AudioGraph graph, float[] buffer, int fromFrame, int toFrame)
    {
        for (var start = fromFrame; start < toFrame; start += Block)
        {
            var count = Math.Min(Block, toFrame - start);
            graph.Process(buffer.AsSpan(start * Channels, count * Channels), count);
        }
    }

    /// <summary>Charakterystyka ze wzorów Audio EQ Cookbook, niezależna od kodu toru.</summary>
    private static double CookbookResponseDb(ParametricBand[] bands, double preamp, double frequency, double sampleRate)
    {
        var total = preamp;
        foreach (var band in bands)
        {
            if (!band.Enabled) continue;

            var a = Math.Pow(10, band.Gain / 40);
            var w0 = 2 * Math.PI * Math.Min(band.Frequency, sampleRate / 2 * 0.92) / sampleRate;
            var alpha = Math.Sin(w0) / (2 * band.Q);
            var c = Math.Cos(w0);
            var s = 2 * Math.Sqrt(a) * alpha;

            double b0, b1, b2, a0, a1, a2;
            switch (band.Type)
            {
                case ParametricFilterType.Peak:
                    (b0, b1, b2) = (1 + alpha * a, -2 * c, 1 - alpha * a);
                    (a0, a1, a2) = (1 + alpha / a, -2 * c, 1 - alpha / a);
                    break;
                case ParametricFilterType.LowShelf:
                    (b0, b1, b2) = (a * ((a + 1) - (a - 1) * c + s), 2 * a * ((a - 1) - (a + 1) * c), a * ((a + 1) - (a - 1) * c - s));
                    (a0, a1, a2) = ((a + 1) + (a - 1) * c + s, -2 * ((a - 1) + (a + 1) * c), (a + 1) + (a - 1) * c - s);
                    break;
                default:
                    (b0, b1, b2) = (a * ((a + 1) + (a - 1) * c + s), -2 * a * ((a - 1) + (a + 1) * c), a * ((a + 1) + (a - 1) * c - s));
                    (a0, a1, a2) = ((a + 1) - (a - 1) * c + s, 2 * ((a - 1) - (a + 1) * c), (a + 1) - (a - 1) * c - s);
                    break;
            }

            var w = 2 * Math.PI * frequency / sampleRate;
            var z1 = new System.Numerics.Complex(Math.Cos(-w), Math.Sin(-w));
            var z2 = z1 * z1;
            var h = (b0 + b1 * z1 + b2 * z2) / (a0 + a1 * z1 + a2 * z2);
            total += 20 * Math.Log10(h.Magnitude);
        }

        return total;
    }
}
