using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>
/// Zmiany ustawień i włączanie stopni w trakcie odtwarzania. Skok mnożnika albo współczynników
/// w jednej próbce to skok w przebiegu, czyli trzask; miarą jest największa różnica między
/// sąsiednimi próbkami wyniku.
/// </summary>
public class ParameterSmoothingTests
{
    private const int SampleRate = 48000;
    private const int Block = 480;

    /// <summary>Największy krok sinusa o danej częstotliwości i amplitudzie, z zapasem 20%.</summary>
    private static double NaturalStep(double frequency, double amplitude) =>
        1.2 * amplitude * 2 * Math.PI * frequency / SampleRate;

    // ================= ograniczanie dynamiki =================

    /// <summary>
    /// Włączenie „trybu nocnego” na 100% w trakcie głośnej muzyki nie może wypchnąć wzmocnienia
    /// o kilkanaście decybeli w jednej próbce.
    /// </summary>
    [Fact]
    public void DynamicRangeSwitchesOnAndOffWithoutAStep()
    {
        var dynamics = new DynamicRange { Enabled = false, Strength = 100 };
        dynamics.Prepare(SampleRate, Channels);

        var tone = Sine(100, 0.5, 3 * SampleRate, SampleRate);

        ProcessRange(dynamics, tone, 0, SampleRate);
        dynamics.Enabled = true;
        ProcessRange(dynamics, tone, SampleRate, 2 * SampleRate);
        dynamics.Enabled = false;
        ProcessRange(dynamics, tone, 2 * SampleRate, 3 * SampleRate);

        Assert.True(LargestStep(tone, 0) < NaturalStep(100, 0.5) * 2, $"skok {LargestStep(tone, 0):F4}");
    }

    /// <summary>Po wygaszeniu wyłączony stopień przepuszcza sygnał bez zmiany ani jednej próbki.</summary>
    [Fact]
    public void DynamicRangeBypassesOnceFadedOut()
    {
        var dynamics = new DynamicRange { Enabled = true, Strength = 100 };
        dynamics.Prepare(SampleRate, Channels);
        ProcessInBlocks(dynamics, Sine(100, 0.5, SampleRate, SampleRate), Block);

        dynamics.Enabled = false;
        ProcessInBlocks(dynamics, Sine(100, 0.5, SampleRate, SampleRate), Block);

        var original = Sine(100, 0.5, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(dynamics, processed, Block);
        Assert.Equal(original, processed);
    }

    /// <summary>Zmiana siły na cichym materiale przesuwa wzmocnienie wyrównawcze płynnie.</summary>
    [Fact]
    public void DynamicRangeStrengthChangeIsSmooth()
    {
        var dynamics = new DynamicRange { Enabled = true, Strength = 10 };
        dynamics.Prepare(SampleRate, Channels);

        // Przebieg stały: każda zmiana wyniku jest zmianą wzmocnienia.
        var buffer = new float[2 * SampleRate * Channels];
        Array.Fill(buffer, 0.01f);

        ProcessRange(dynamics, buffer, 0, SampleRate);
        dynamics.Strength = 100;
        ProcessRange(dynamics, buffer, SampleRate, 2 * SampleRate);

        Assert.True(LargestStep(buffer, 0, fromFrame: 4800) < 0.002, $"skok wzmocnienia {LargestStep(buffer, 0, 4800):F4}");
        Assert.True(buffer[^1] > 0.03f, "wzmocnienie wyrównawcze nie weszło");
    }

    // ================= poszerzenie stereo =================

    [Fact]
    public void StereoWidthSwitchesOnWithoutAStep()
    {
        var width = new StereoWidth { Enabled = false, Strength = 100 };
        width.Prepare(SampleRate, Channels);

        // Przełączenie w szczycie sinusa, gdzie skok mnożnika jest największy.
        var signal = SideOnly(1000, 0.4, 2 * SampleRate, SampleRate);
        ProcessRange(width, signal, 0, SampleRate + 12);
        width.Enabled = true;
        ProcessRange(width, signal, SampleRate + 12, 2 * SampleRate);

        Assert.True(LargestStep(signal, 0) < NaturalStep(1000, 0.4 * 1.8), $"skok {LargestStep(signal, 0):F4}");
        Assert.InRange(Goertzel(signal, 0, 1000, SampleRate, SampleRate + 9600), 0.4 * 1.6, 0.4 * 1.85);
    }

    /// <summary>
    /// Bas w bokach zostaje prawie nietknięty, a przejście ku pełnej szerokości jest monotoniczne:
    /// bez garbu przy częstotliwości podziału i bez dołka poniżej niej.
    /// </summary>
    [Theory]
    [InlineData(100.0)]
    [InlineData(50.0)]
    public void StereoWidthLeavesTheBassWidthAlone(double strength)
    {
        double[] frequencies = [40, 63, 100, 160, 250, 400, 630, 1000, 2000, 5000];
        var gains = frequencies.Select(f => SideGainDb(f, strength)).ToArray();
        var full = Db(1 + 0.8 * strength / 100);

        Assert.True(gains[0] < 0.1, $"40 Hz: {gains[0]:F2} dB");
        Assert.True(gains[2] < 0.8, $"100 Hz: {gains[2]:F2} dB");
        Assert.True(gains[^1] > full - 0.3, $"5 kHz: {gains[^1]:F2} dB wobec {full:F2} dB");

        for (var i = 0; i < gains.Length; i++)
        {
            Assert.True(gains[i] > -0.05, $"dołek przy {frequencies[i]} Hz: {gains[i]:F2} dB");
            Assert.True(gains[i] < full + 0.05, $"garb przy {frequencies[i]} Hz: {gains[i]:F2} dB");
            if (i > 0) Assert.True(gains[i] >= gains[i - 1] - 0.02,
                $"spadek między {frequencies[i - 1]} a {frequencies[i]} Hz: {gains[i - 1]:F2} → {gains[i]:F2} dB");
        }
    }

    // ================= crossfeed =================

    /// <summary>
    /// Materiał wspólny obu kanałom, jak wokal na środku, nie ma różnicy kanałów, więc
    /// przechodzi przez crossfeed bez zmiany: nie traci ani góry, ani poziomu.
    /// </summary>
    [Fact]
    public void CrossfeedLeavesCentredMaterialUntouched()
    {
        var crossfeed = new Crossfeed { Enabled = true, Strength = 100 };
        crossfeed.Prepare(SampleRate, Channels);

        var original = Sine(5000, 0.5, 9600, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(crossfeed, processed, Block);

        for (var i = 0; i < original.Length; i++) Assert.Equal(original[i], processed[i], tolerance: 1e-6f);
    }

    /// <summary>
    /// Pełna siła to najmocniejsza nastawa bs2b: różnica poziomów w basie między drogą
    /// bezpośrednią a skrośną około 4,5 dB. Słabsze ustawienie daje większą różnicę.
    /// </summary>
    [Fact]
    public void CrossfeedStrengthMapsOntoTheBs2bRange()
    {
        var strong = BassSeparationDb(100);
        var medium = BassSeparationDb(50);
        var weak = BassSeparationDb(10);

        Assert.InRange(strong, 4.0, 5.2);
        Assert.InRange(medium, 9.0, 10.8);
        Assert.True(weak > medium + 3, $"10%: {weak:F2} dB, 50%: {medium:F2} dB");
    }

    [Fact]
    public void CrossfeedSwitchesOnAndOffWithoutAStep()
    {
        var crossfeed = new Crossfeed { Enabled = false, Strength = 100 };
        crossfeed.Prepare(SampleRate, Channels);

        var signal = SideOnly(100, 0.5, 3 * SampleRate, SampleRate);
        ProcessRange(crossfeed, signal, 0, SampleRate);
        crossfeed.Enabled = true;
        ProcessRange(crossfeed, signal, SampleRate, 2 * SampleRate);
        crossfeed.Enabled = false;
        ProcessRange(crossfeed, signal, 2 * SampleRate, 3 * SampleRate);

        Assert.True(LargestStep(signal, 0) < NaturalStep(100, 0.5), $"skok {LargestStep(signal, 0):F4}");
    }

    // ================= bas wirtualny =================

    /// <summary>
    /// Wyłączenie w chwili, gdy domieszka harmonicznych jest największa, nie może jej uciąć
    /// w jednej próbce. Miarą jest sama domieszka: wynik minus wejście.
    /// </summary>
    [Fact]
    public void VirtualBassSwitchesOffWithoutAStep()
    {
        var original = Sine(45, 0.5, 3 * SampleRate, SampleRate);

        // Przebieg domieszki przy stopniu włączonym bez przerwy wskazuje chwilę jej szczytu.
        var probe = new VirtualBass { Enabled = true, Strength = 100 };
        probe.Prepare(SampleRate, Channels);
        var reference = (float[])original.Clone();
        ProcessRange(probe, reference, 0, 3 * SampleRate);

        var switchFrame = 2 * SampleRate;
        for (var frame = 2 * SampleRate; frame < 2 * SampleRate + 1067; frame++)
            if (Math.Abs(reference[frame * Channels] - original[frame * Channels]) >
                Math.Abs(reference[switchFrame * Channels] - original[switchFrame * Channels])) switchFrame = frame;

        var bass = new VirtualBass { Enabled = true, Strength = 100 };
        bass.Prepare(SampleRate, Channels);
        var tone = (float[])original.Clone();
        ProcessRange(bass, tone, 0, switchFrame);
        bass.Enabled = false;
        ProcessRange(bass, tone, switchFrame, 3 * SampleRate);

        var added = new float[tone.Length];
        for (var i = 0; i < tone.Length; i++) added[i] = tone[i] - original[i];

        var steady = LargestStep(added[..((switchFrame - 1) * Channels)], 0, fromFrame: SampleRate);
        Assert.True(LargestStep(added, 0, fromFrame: SampleRate) <= steady * 1.5,
            $"skok domieszki {LargestStep(added, 0, SampleRate):F4} wobec {steady:F4} w stanie ustalonym");
    }
    // ================= korektor =================

    /// <summary>
    /// Wyłączenie korektora w trakcie gry sprowadza pasma do zera płynnie, a po wygaszeniu
    /// sygnał przechodzi bez zmiany ani jednej próbki.
    /// </summary>
    [Fact]
    public void EqualiserFadesOutWhenDisabledThenBypasses()
    {
        var equaliser = new Equaliser();
        equaliser.SetGain(5, 12);
        equaliser.Preamp = -6;
        equaliser.Prepare(SampleRate, Channels);

        var tone = Sine(1000, 0.25, 2 * SampleRate, SampleRate);
        ProcessRange(equaliser, tone, 0, SampleRate + 12);
        equaliser.Enabled = false;
        ProcessRange(equaliser, tone, SampleRate + 12, 2 * SampleRate);

        Assert.True(LargestStep(tone, 0, fromFrame: 4800) < NaturalStep(1000, 0.25 * 2),
            $"skok {LargestStep(tone, 0, 4800):F4}");

        var original = Sine(1000, 0.25, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(equaliser, processed, Block);
        Assert.Equal(original, processed);
    }

    /// <summary>Przedwzmacniacz przestawiony o 12 dB nie skacze na granicy bufora.</summary>
    [Fact]
    public void PreampChangeHasNoStepAtTheBufferBoundary()
    {
        var equaliser = new Equaliser();
        equaliser.Prepare(SampleRate, Channels);

        var buffer = new float[SampleRate * Channels];
        Array.Fill(buffer, 0.5f);
        ProcessRange(equaliser, buffer, 0, 4800);
        equaliser.Preamp = -12;
        ProcessRange(equaliser, buffer, 4800, SampleRate);

        Assert.True(LargestStep(buffer, 0) < 0.005, $"skok {LargestStep(buffer, 0):F4}");
        Assert.InRange(buffer[^1], 0.5f * 0.24f, 0.5f * 0.26f);
    }

    /// <summary>Czas wygładzania liczony w sekundach: okres 256 i 2048 ramek po 100 ms daje to samo.</summary>
    [Fact]
    public void EqualiserSmoothingDoesNotDependOnBufferSize()
    {
        double Progress(int frames)
        {
            var equaliser = new Equaliser();
            equaliser.Prepare(SampleRate, Channels);
            equaliser.Preamp = -12;

            var count = (int)Math.Round(0.1024 * SampleRate / frames) * frames;
            var buffer = new float[count * Channels];
            Array.Fill(buffer, 1f);
            ProcessInBlocks(equaliser, buffer, frames);
            return Db(buffer[^1]) / -12;
        }

        var small = Progress(256);
        var large = Progress(2048);

        Assert.InRange(small, 0.75, 0.95);
        Assert.InRange(large, small - 0.06, small + 0.06);
    }

    /// <summary>
    /// Dokumentuje rzeczywiste zachowanie: pasma oktawowe zachodzą na siebie, więc przy
    /// wszystkich suwakach na +6 dB środek pasma wewnętrznego dostaje około 8,8 dB.
    /// </summary>
    [Fact]
    public void AdjacentBandsOverlap()
    {
        var equaliser = new Equaliser();
        for (var band = 0; band < Equaliser.Frequencies.Length; band++) equaliser.SetGain(band, 6);
        equaliser.Prepare(SampleRate, Channels);

        var buffer = Sine(1000, 0.05, SampleRate, SampleRate);
        ProcessInBlocks(equaliser, buffer, Block);

        Assert.InRange(Db(Goertzel(buffer, 0, 1000, SampleRate, 9600) / 0.05), 8.3, 9.3);
    }

    // ================= wzmocnienie =================

    /// <summary>
    /// Po powrocie do jedności wzmocnienie dochodzi do celu dokładnie, więc stopień znowu
    /// przepuszcza próbki bez mnożenia.
    /// </summary>
    [Fact]
    public void GainStageSettlesExactlyOnUnity()
    {
        var gain = new GainStage(smoothingSeconds: 0.02);
        gain.Prepare(SampleRate, Channels);
        gain.Target = 0.5f;
        ProcessInBlocks(gain, new float[SampleRate * Channels], Block);
        gain.Target = 1f;
        ProcessInBlocks(gain, Sine(440, 0.5, SampleRate, SampleRate), Block);

        var original = Sine(440, 0.5, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(gain, processed, Block);
        Assert.Equal(original, processed);
    }

    /// <summary>Głośność zero daje dokładne zero, a nie mnożenie przez liczbę subnormalną.</summary>
    [Fact]
    public void GainStageAtZeroOutputsExactSilence()
    {
        var gain = new GainStage(smoothingSeconds: 0.02);
        gain.Prepare(SampleRate, Channels);
        gain.Target = 0f;
        ProcessInBlocks(gain, Sine(440, 0.5, 2 * SampleRate, SampleRate), Block);

        var processed = Sine(440, 0.5, 4800, SampleRate);
        ProcessInBlocks(gain, processed, Block);
        Assert.All(processed, sample => Assert.Equal(0f, sample));
    }

    // ---------- pomocnicze ----------

    /// <summary>Wzmocnienie sygnału boku na zadanej częstotliwości, w decybelach.</summary>
    private static double SideGainDb(double frequency, double strength)
    {
        var width = new StereoWidth { Enabled = true, Strength = strength };
        width.Prepare(SampleRate, Channels);

        var buffer = SideOnly(frequency, 0.25, 2 * SampleRate, SampleRate);
        ProcessInBlocks(width, buffer, Block);
        return Db(Goertzel(buffer, 0, frequency, SampleRate, SampleRate) / 0.25);
    }

    /// <summary>Różnica poziomów 60 Hz między kanałem z sygnałem a przeciwnym, w decybelach.</summary>
    private static double BassSeparationDb(double strength)
    {
        var crossfeed = new Crossfeed { Enabled = true, Strength = strength };
        crossfeed.Prepare(SampleRate, Channels);

        var buffer = new float[SampleRate * Channels];
        for (var frame = 0; frame < SampleRate; frame++)
            buffer[frame * Channels] = (float)(0.5 * Math.Sin(2 * Math.PI * 60 * frame / SampleRate));
        ProcessInBlocks(crossfeed, buffer, Block);

        var direct = Goertzel(buffer, 0, 60, SampleRate, 9600);
        var cross = Goertzel(buffer, 1, 60, SampleRate, 9600);
        return Db(direct / cross);
    }
}
