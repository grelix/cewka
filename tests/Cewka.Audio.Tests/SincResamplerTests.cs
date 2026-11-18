using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Konwersja częstotliwości: płaskość pasma i składowe obce mierzone widmem, położenie impulsu,
/// długość po wypłukaniu końcówki i niezależność od podziału na porcje.
/// </summary>
public sealed class SincResamplerTests
{
    private const double Amplitude = 0.5;
    private const int MeasuredFrames = 65536;

    public static TheoryData<int, int, ResamplingLevel> Pairs()
    {
        var data = new TheoryData<int, int, ResamplingLevel>();
        foreach (var level in Enum.GetValues<ResamplingLevel>())
            foreach (var (from, to) in MeasuredPairs) data.Add(from, to, level);
        return data;
    }

    internal static readonly (int From, int To)[] MeasuredPairs =
        [(44100, 48000), (48000, 44100), (96000, 44100), (192000, 48000), (22050, 48000)];

    /// <summary>Wymagania poziomu: pasmo płaskie do częstotliwości z tolerancją i granica składowych obcych.</summary>
    internal static (double FlatTo, double FlatDb, double SpuriousDb) Criteria(ResamplingLevel level) => level switch
    {
        ResamplingLevel.Economy => (16000, 0.2, -70),
        ResamplingLevel.Standard => (20000, 0.05, -95),
        _ => (20000, 0.02, -120),
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void PassbandIsFlatAndForeignComponentsStayBelowTheLimit(int from, int to, ResamplingLevel level)
    {
        var (flatTo, flatDb, spuriousDb) = Criteria(level);
        var result = Measure(from, to, level);

        Assert.True(result.WorstFlatnessDb <= flatDb,
            $"{from}→{to} {level}: odchyłka pasma {result.WorstFlatnessDb:F4} dB przy {result.FlatnessAt} Hz (do {flatTo} Hz)");
        Assert.True(result.WorstSpuriousDb <= spuriousDb,
            $"{from}→{to} {level}: składowa obca {result.WorstSpuriousDb:F1} dB przy {result.SpuriousAt:F0} Hz (ton {result.SpuriousTone} Hz)");
    }

    /// <summary>Wynik pomiaru jednej pary na jednym poziomie: najgorsze wartości ze wszystkich tonów.</summary>
    internal sealed record Measurement(
        double WorstFlatnessDb, double FlatnessAt, double WorstSpuriousDb, double SpuriousAt, double SpuriousTone);

    internal static Measurement Measure(int from, int to, ResamplingLevel level)
    {
        var (flatTo, _, _) = Criteria(level);
        var passFraction = level == ResamplingLevel.Economy ? 16.0 / 44.1 : 20.0 / 44.1;
        var flatLimit = Math.Min(flatTo, passFraction * Math.Min(from, to) + 1);

        var tones = new List<double> { 1000, 5000, 10000, 15000, 19000 };
        tones.RemoveAll(f => f >= 0.475 * from);

        // Przy zmniejszaniu ton ponad połową wyjścia: jego odbicie wpadłoby w pasmo słyszalne.
        if (from > to) tones.Add(Math.Min(to - 10000, 0.475 * from));

        double worstFlat = 0, flatAt = 0, worstSpur = double.NegativeInfinity, spurAt = 0, spurTone = 0;
        var band = Math.Min(20000, to / 2.0);

        foreach (var tone in tones)
        {
            var spectrum = new Spectrum(ResampledTone(tone, from, to, level), to);
            var audible = tone < to / 2.0;

            if (audible && tone <= flatLimit)
            {
                var deviation = Math.Abs(Signals.Db(spectrum.AmplitudeAt(tone) / Amplitude));
                if (deviation > worstFlat) (worstFlat, flatAt) = (deviation, tone);
            }

            var (largest, at) = audible
                ? spectrum.LargestComponent(0, band, tone)
                : spectrum.LargestComponent(0, band);
            var spurious = Signals.Db(Math.Max(largest, 1e-12) / Amplitude);
            if (spurious > worstSpur) (worstSpur, spurAt, spurTone) = (spurious, at, tone);
        }

        return new Measurement(worstFlat, flatAt, worstSpur, spurAt, spurTone);
    }

    /// <summary>Wycinek wyjścia z dala od początku, o długości widma.</summary>
    private static float[] ResampledTone(double frequency, int from, int to, ResamplingLevel level)
    {
        var skip = 4096;
        var inputFrames = (int)((skip + MeasuredFrames + 8192L) * from / to) + 1;

        var input = new float[inputFrames];
        for (var i = 0; i < inputFrames; i++) input[i] = (float)(Amplitude * Math.Sin(2 * Math.PI * frequency * i / from));

        var output = Run(new SincResampler(1, from, to, level), input, 1, 4093);
        return output.AsSpan(skip, MeasuredFrames).ToArray();
    }

    /// <summary>Przepuszcza całe wejście porcjami i wypłukuje końcówkę.</summary>
    internal static float[] Run(SincResampler resampler, float[] input, int channels, int chunk, int outputChunk = 3001,
        Random? random = null)
    {
        var frames = input.Length / channels;
        var result = new List<float>(frames * 2);
        var buffer = new float[Math.Max(outputChunk, 1) * channels];
        var offset = 0;

        while (offset < frames)
        {
            var take = Math.Min(random?.Next(1, chunk + 1) ?? chunk, frames - offset);
            var space = random?.Next(1, outputChunk + 1) ?? outputChunk;

            var produced = resampler.Process(input.AsSpan(offset * channels, take * channels), take,
                buffer.AsSpan(0, space * channels), out var used);
            result.AddRange(buffer.AsSpan(0, produced * channels));
            offset += used;
        }

        int drained;
        while ((drained = resampler.Drain(buffer.AsSpan(0, (random?.Next(1, outputChunk + 1) ?? outputChunk) * channels))) > 0)
            result.AddRange(buffer.AsSpan(0, drained * channels));

        return result.ToArray();
    }

    // ---------- położenie i długość ----------

    [Theory]
    [MemberData(nameof(Pairs))]
    public void ImpulseLandsOnItsOwnTime(int from, int to, ResamplingLevel level)
    {
        foreach (var at in new[] { 0, 1, 777, 5003 })
        {
            var input = new float[6000];
            input[at] = 1f;

            var output = Run(new SincResampler(1, from, to, level), input, 1, 512);

            var peak = 0;
            for (var i = 1; i < output.Length; i++)
                if (Math.Abs(output[i]) > Math.Abs(output[peak])) peak = i;

            var expected = (double)at * to / from;
            Assert.True(Math.Abs(peak - expected) <= 1, $"{from}→{to} {level}: impuls z {at} w {peak}, oczekiwano {expected:F2}");
        }
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(96000, 44100)]
    [InlineData(192000, 48000)]
    [InlineData(22050, 48000)]
    [InlineData(8000, 44100)]
    [InlineData(44056, 48000)]
    public void DrainedLengthMatchesTheRatio(int from, int to)
    {
        foreach (var level in Enum.GetValues<ResamplingLevel>())
        {
            foreach (var frames in new[] { 0, 1, 2, 99, 4410, 44101, 100003 })
            {
                var output = Run(new SincResampler(2, from, to, level), new float[frames * 2], 2, 1000);
                var expected = Math.Round((double)frames * to / from);

                Assert.InRange(output.Length / 2, expected - 1, expected + 1);
                Assert.Equal(SincResampler.OutputFrames(frames, from, to), output.Length / 2);
            }
        }
    }

    /// <summary>Porcje wejścia i wyjścia losowej długości dają ten sam wynik co stałe, co do bitu.</summary>
    [Theory]
    [InlineData(44100, 48000, 11)]
    [InlineData(96000, 44100, 12)]
    [InlineData(44056, 48000, 13)]
    [InlineData(48000, 22050, 14)]
    public void ResultDoesNotDependOnHowTheInputIsSplit(int from, int to, int seed)
    {
        var random = new Random(seed);
        var input = new float[30011 * 2];
        for (var i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 2 - 1);

        foreach (var level in Enum.GetValues<ResamplingLevel>())
        {
            var whole = Run(new SincResampler(2, from, to, level), input, 2, input.Length / 2, 100000);
            var split = Run(new SincResampler(2, from, to, level), input, 2, 700, 333, new Random(seed));

            Assert.Equal(whole, split);
        }
    }

    // ---------- tablica faz z interpolacją ----------

    /// <summary>
    /// 44 056 Hz (dawne nagrania wideo NTSC) do 48 kHz ma 6000 faz: współczynniki pochodzą z tablicy
    /// z interpolacją, a wymagania poziomów obowiązują tak samo.
    /// </summary>
    [Theory]
    [InlineData(44056, 48000)]
    [InlineData(48000, 44056)]
    public void InterpolatedPhasesMeetTheSameCriteria(int from, int to)
    {
        foreach (var level in Enum.GetValues<ResamplingLevel>())
        {
            Assert.True(new SincResampler(1, from, to, level).InterpolatesPhases);

            var (_, flatDb, spuriousDb) = Criteria(level);
            var result = Measure(from, to, level);

            Assert.True(result.WorstFlatnessDb <= flatDb, $"{level}: odchyłka {result.WorstFlatnessDb:F4} dB przy {result.FlatnessAt} Hz");
            Assert.True(result.WorstSpuriousDb <= spuriousDb,
                $"{level}: składowa {result.WorstSpuriousDb:F1} dB przy {result.SpuriousAt:F0} Hz (ton {result.SpuriousTone} Hz)");
        }
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(96000, 44100)]
    [InlineData(176400, 48000)]
    [InlineData(88200, 48000)]
    [InlineData(48000, 192000)]
    public void CommonRatesUseExactPhases(int from, int to)
    {
        foreach (var level in Enum.GetValues<ResamplingLevel>())
            Assert.False(new SincResampler(1, from, to, level).InterpolatesPhases);
    }

    // ---------- kanały, stan, pamięć ----------

    [Fact]
    public void ChannelsDoNotLeakIntoEachOther()
    {
        const int channels = 6;
        var input = TestAudioFiles.ToneInChannel(channels, 4, 20000, 44100, 3000);

        var output = Run(new SincResampler(channels, 44100, 48000, ResamplingLevel.High), input, channels, 1000);

        for (var i = 0; i < output.Length; i++)
            if (i % channels != 4) Assert.Equal(0f, output[i]);

        Assert.Contains(output, sample => Math.Abs(sample) > 0.4f);
    }

    [Fact]
    public void ResetStartsOverAsANewStream()
    {
        var random = new Random(5);
        var input = new float[5000 * 2];
        for (var i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() - 0.5);

        var resampler = new SincResampler(2, 44100, 48000, ResamplingLevel.Standard);
        Run(resampler, input.Reverse().ToArray(), 2, 999);

        resampler.Reset();
        var again = Run(resampler, input, 2, 999);
        var fresh = Run(new SincResampler(2, 44100, 48000, ResamplingLevel.Standard), input, 2, 999);

        Assert.Equal(fresh, again);
    }

    [Fact]
    public void ConstantSignalPassesUnchanged()
    {
        var input = new float[20000];
        Array.Fill(input, 0.75f);

        var output = Run(new SincResampler(1, 96000, 44100, ResamplingLevel.Standard), input, 1, 1000);

        // Z dala od brzegów, gdzie filtr widzi ciszę przed pierwszą i po ostatniej ramce.
        for (var i = 200; i < output.Length - 200; i++) Assert.Equal(0.75f, output[i], 1e-5f);
    }

    [Fact]
    public void ProcessDoesNotAllocate()
    {
        var resampler = new SincResampler(2, 44100, 48000, ResamplingLevel.High);
        var input = new float[1024 * 2];
        var output = new float[2048 * 2];

        resampler.Process(input, 1024, output, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++) resampler.Process(input, 1024, output, out _);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    /// <summary>Tablice współczynników są wspólne, także gdy wiele wątków tworzy konwersję naraz.</summary>
    [Fact]
    public void CoefficientTablesAreShared()
    {
        var banks = new SincResampler.FilterBank[16];
        Parallel.For(0, banks.Length, i => banks[i] = SincResampler.FilterBank.For(32000, 44100, ResamplingLevel.High));

        Assert.All(banks, bank => Assert.Same(banks[0], bank));
        Assert.NotSame(banks[0], SincResampler.FilterBank.For(32000, 44100, ResamplingLevel.Standard));
    }

    [Theory]
    [InlineData(0, 48000)]
    [InlineData(-44100, 48000)]
    [InlineData(44100, 0)]
    [InlineData(2_000_000, 48000)]
    [InlineData(1_536_000, 4000)]
    public void ImplausibleRatesAreRejected(int from, int to) =>
        Assert.Throws<AudioException>(() => new SincResampler(2, from, to, ResamplingLevel.Standard));

    /// <summary>Pomocnik widma sam nie wnosi składowych: czysty ton w podwójnej precyzji daje tło poniżej −150 dB.</summary>
    [Fact]
    public void SpectrumHelperHasAFloorFarBelowTheCriteria()
    {
        var samples = new float[MeasuredFrames];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(Amplitude * Math.Sin(2 * Math.PI * 1234.5 * i / 48000));

        var spectrum = new Spectrum(samples, 48000);

        Assert.Equal(Amplitude, spectrum.AmplitudeAt(1234.5), 1e-6);
        Assert.True(Signals.Db(spectrum.LargestComponent(0, 20000, 1234.5).Amplitude / Amplitude) < -140);
    }
}
