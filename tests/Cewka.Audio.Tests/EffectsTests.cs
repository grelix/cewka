using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Zachowanie pięciu stopni dodanych do łańcucha: crossfeedu, kompensacji głośności, basu
/// wirtualnego, ograniczania dynamiki i poszerzenia bazy stereo.
///
/// <para>Każdy z nich ma miarę liczbową, więc sprawdzenie nie wymaga niczyjego ucha. Energię
/// na zadanej częstotliwości liczy algorytm Goertzela — pojedyncza pętla zamiast całej
/// transformaty, bo interesuje nas kilka wybranych częstotliwości, a nie widmo.</para>
/// </summary>
public class EffectsTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    // ================= crossfeed =================

    /// <summary>Wyłączony stopień nie ma prawa ruszyć ani jednej próbki.</summary>
    [Fact]
    public void DisabledCrossfeedIsBypassed()
    {
        var crossfeed = new Crossfeed { Enabled = false, Strength = 100 };
        crossfeed.Prepare(SampleRate, Channels);

        var original = Sine(1000, 0.5, 4096);
        var processed = (float[])original.Clone();
        crossfeed.Process(processed, 4096);

        Assert.Equal(original, processed);
    }

    /// <summary>
    /// Sedno crossfeedu: niskie tony przechodzą do drugiego ucha niemal w całości, wysokie
    /// prawie wcale. Gdyby przechodziły jednakowo, byłoby to zwykłe mieszanie kanałów
    /// i obraz stereo zapadłby się do środka.
    /// </summary>
    [Theory]
    [InlineData(100.0, 0.20, 0.45)]     // niskie: przenikanie duże
    [InlineData(10000.0, 0.0, 0.06)]    // wysokie: przenikanie znikome
    public void CrossfeedLeaksLowFrequenciesAndKeepsHighOnesSeparate(
        double frequency, double minimumRatio, double maximumRatio)
    {
        var crossfeed = new Crossfeed { Enabled = true, Strength = 100 };
        crossfeed.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var buffer = SineOnLeftOnly(frequency, 0.5, frames);
        crossfeed.Process(buffer, frames);

        // Pierwsza dziesiąta sekundy odpada: filtr musi się ustalić.
        var left = Goertzel(buffer, channel: 0, frequency, skipFrames: 4800);
        var right = Goertzel(buffer, channel: 1, frequency, skipFrames: 4800);

        Assert.InRange(right / left, minimumRatio, maximumRatio);
    }

    /// <summary>Przy jednym kanale nie ma czego mieszać — stopień musi się usunąć z drogi.</summary>
    [Fact]
    public void CrossfeedIsBypassedForMonoMaterial()
    {
        var crossfeed = new Crossfeed { Enabled = true, Strength = 100 };
        crossfeed.Prepare(SampleRate, channels: 1);

        var original = new float[4096];
        for (var i = 0; i < original.Length; i++)
            original[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));

        var processed = (float[])original.Clone();
        crossfeed.Process(processed, 4096);

        Assert.Equal(original, processed);
    }

    // ================= kompensacja głośności =================

    /// <summary>
    /// Przy poziomie odniesienia — suwak na maksimum, normalizacja bez wzmocnienia — kompensacja
    /// nie ma nic do roboty i musi być zupełnie przezroczysta.
    /// </summary>
    [Fact]
    public void LoudnessIsTransparentAtReferenceLevel()
    {
        var loudness = new Loudness { Enabled = true, Strength = 100 };
        loudness.Prepare(SampleRate, Channels);
        loudness.SetListeningLevel(volumeDb: 0, normalisationDb: 0);

        var original = Sine(50, 0.5, 4096);
        var processed = (float[])original.Clone();
        loudness.Process(processed, 4096);

        Assert.Equal(original, processed);
    }

    /// <summary>Ściszenie ma podnosić bas, i to tym mocniej, im ciszej gra muzyka.</summary>
    [Fact]
    public void LoudnessRaisesBassAsTheVolumeDrops()
    {
        var gentle = MeasureLoudnessBoost(volumeDb: -10, strength: 100);
        var strong = MeasureLoudnessBoost(volumeDb: -25, strength: 100);

        Assert.True(gentle > 1.0, $"przy −10 dB nie ma podbicia basu (zmierzono {gentle:F2} dB)");
        Assert.True(strong > gentle + 2.0,
            $"podbicie nie rośnie ze ściszaniem: {gentle:F2} dB przy −10, {strong:F2} dB przy −25");
    }

    /// <summary>
    /// Wzmocnienie, jakie nadała normalizacja, odejmuje się od ubytku. Ciche nagranie podbite
    /// o dziesięć decybeli gra głośniej, niż mówi suwak, i nie powinno dostać pełnej korekty.
    /// </summary>
    [Fact]
    public void LoudnessAccountsForTheNormalisationGain()
    {
        var withoutGain = MeasureLoudnessBoost(volumeDb: -20, strength: 100, normalisationDb: 0);
        var withGain = MeasureLoudnessBoost(volumeDb: -20, strength: 100, normalisationDb: 10);

        Assert.True(withGain < withoutGain - 1.0,
            $"normalizacja nie wpłynęła na korektę: {withoutGain:F2} dB bez niej, {withGain:F2} dB z nią");
    }

    /// <summary>Suwak siły skaluje wyliczone podbicie wprost proporcjonalnie.</summary>
    [Fact]
    public void LoudnessStrengthScalesTheBoost()
    {
        var full = MeasureLoudnessBoost(volumeDb: -20, strength: 100);
        var half = MeasureLoudnessBoost(volumeDb: -20, strength: 50);

        Assert.InRange(half, full * 0.4, full * 0.6);
    }

    /// <summary>
    /// Ograniczenie musi trzymać. Przy suwaku na kilku procentach nieograniczona korekta
    /// sięgnęłaby kilkunastu decybeli i zamieniłaby nagranie w dudnienie.
    /// </summary>
    [Fact]
    public void LoudnessNeverExceedsItsCap()
    {
        var extreme = MeasureLoudnessBoost(volumeDb: -40, strength: 100);
        Assert.True(extreme <= 10.5, $"podbicie przekroczyło ograniczenie: {extreme:F2} dB");
    }

    // ================= bas wirtualny =================

    [Fact]
    public void DisabledVirtualBassIsBypassed()
    {
        var bass = new VirtualBass { Enabled = false, Strength = 100 };
        bass.Prepare(SampleRate, Channels);

        var original = Sine(45, 0.5, 4096);
        var processed = (float[])original.Clone();
        bass.Process(processed, 4096);

        Assert.Equal(original, processed);
    }

    /// <summary>
    /// Sedno efektu: ton, którego mały głośnik nie wypromieniuje, ma pojawić się na wyjściu
    /// jako swoje harmoniczne. Wejście jest czystym sinusem, więc energii na 90 Hz nie ma
    /// tam skąd wziąć — jeśli się pojawia, wytworzył ją ten stopień.
    /// </summary>
    [Fact]
    public void VirtualBassCreatesHarmonicsOfTheFundamental()
    {
        var bass = new VirtualBass { Enabled = true, Strength = 100 };
        bass.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var buffer = Sine(45, 0.5, frames);

        var before = Goertzel(buffer, channel: 0, 90, skipFrames: 0);
        bass.Process(buffer, frames);
        var after = Goertzel(buffer, channel: 0, 90, skipFrames: 12000);

        Assert.True(before < 1e-4, $"sygnał wejściowy nie był czysty (90 Hz: {before:E2})");
        Assert.True(after > 0.01, $"nie powstały harmoniczne (90 Hz: {after:E2})");
    }

    /// <summary>Cisza na wejściu musi dać ciszę na wyjściu — inaczej stopień dudniłby bez przerwy.</summary>
    [Fact]
    public void VirtualBassIsSilentOnSilence()
    {
        var bass = new VirtualBass { Enabled = true, Strength = 100 };
        bass.Prepare(SampleRate, Channels);

        var buffer = new float[48000 * Channels];
        bass.Process(buffer, 48000);

        foreach (var sample in buffer) Assert.Equal(0f, sample, tolerance: 1e-7f);
    }

    /// <summary>Materiał leżący poza pasmem źródłowym i poza pasmem harmonicznych zostaje nietknięty.</summary>
    [Fact]
    public void VirtualBassLeavesTheMidrangeAlone()
    {
        var bass = new VirtualBass { Enabled = true, Strength = 100 };
        bass.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var buffer = Sine(1000, 0.5, frames);
        bass.Process(buffer, frames);

        Assert.InRange(Goertzel(buffer, channel: 0, 1000, skipFrames: 4800), 0.49, 0.51);
    }

    // ================= ograniczanie dynamiki =================

    [Fact]
    public void DisabledDynamicRangeIsBypassed()
    {
        var dynamics = new DynamicRange { Enabled = false, Strength = 100 };
        dynamics.Prepare(SampleRate, Channels);

        var original = Sine(440, 0.5, 4096);
        var processed = (float[])original.Clone();
        dynamics.Process(processed, 4096);

        Assert.Equal(original, processed);
    }

    /// <summary>
    /// Cała rzecz w tym, żeby ciche fragmenty stały się głośniejsze, a głośne cichsze. Jedno
    /// bez drugiego byłoby zwykłym pokrętłem głośności.
    /// </summary>
    [Fact]
    public void DynamicRangeLiftsQuietMaterialAndHoldsBackLoudMaterial()
    {
        var quietChange = MeasureLevelChangeDb(inputAmplitude: 0.01);
        var loudChange = MeasureLevelChangeDb(inputAmplitude: 0.5);

        Assert.True(quietChange > 8, $"ciche nie zostało podniesione ({quietChange:F2} dB)");
        Assert.True(loudChange < 0, $"głośne nie zostało przytrzymane ({loudChange:F2} dB)");
        Assert.True(quietChange - loudChange > 10,
            $"rozpiętość nie zawęziła się dość wyraźnie ({quietChange - loudChange:F2} dB)");
    }

    /// <summary>Wspólna redukcja dla kanałów: głośniejsza strona nie może przesuwać obrazu stereo.</summary>
    [Fact]
    public void DynamicRangeAppliesTheSameGainToBothChannels()
    {
        var dynamics = new DynamicRange { Enabled = true, Strength = 100 };
        dynamics.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var buffer = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(0.8 * Math.Sin(2 * Math.PI * 440 * frame / SampleRate));
            buffer[frame * Channels] = value;
            buffer[frame * Channels + 1] = value * 0.5f;
        }

        dynamics.Process(buffer, frames);

        for (var frame = frames / 2; frame < frames; frame++)
        {
            var left = buffer[frame * Channels];
            if (Math.Abs(left) < 0.05f) continue;

            Assert.InRange(buffer[frame * Channels + 1] / left, 0.49f, 0.51f);
        }
    }

    // ================= poszerzenie stereo =================

    [Fact]
    public void DisabledStereoWidthIsBypassed()
    {
        var width = new StereoWidth { Enabled = false, Strength = 100 };
        width.Prepare(SampleRate, Channels);

        var original = Stereo(440, 660, 0.4, 4096);
        var processed = (float[])original.Clone();
        width.Process(processed, 4096);

        Assert.Equal(original, processed);
    }

    /// <summary>
    /// Najważniejsza własność macierzy środek–boki: suma monofoniczna zostaje nienaruszona
    /// przy każdym ustawieniu. Poszerzanie opóźnieniem tego nie potrafi i potrafi wydrążyć
    /// nagranie odtworzone na jednym głośniku.
    /// </summary>
    [Fact]
    public void StereoWidthPreservesTheMonoSum()
    {
        var width = new StereoWidth { Enabled = true, Strength = 100 };
        width.Prepare(SampleRate, Channels);

        const int frames = 8192;
        var original = Stereo(440, 660, 0.4, frames);
        var processed = (float[])original.Clone();
        width.Process(processed, frames);

        for (var frame = 0; frame < frames; frame++)
        {
            var before = original[frame * Channels] + original[frame * Channels + 1];
            var after = processed[frame * Channels] + processed[frame * Channels + 1];
            Assert.Equal(before, after, tolerance: 1e-5f);
        }
    }

    /// <summary>Poszerzenie ma faktycznie podnosić zawartość boków.</summary>
    [Fact]
    public void StereoWidthIncreasesTheSideContent()
    {
        var width = new StereoWidth { Enabled = true, Strength = 100 };
        width.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var original = Stereo(1000, 1000, 0.4, frames, rightPhase: Math.PI);
        var processed = (float[])original.Clone();
        width.Process(processed, frames);

        var before = SideEnergy(original, frames, skipFrames: 4800);
        var after = SideEnergy(processed, frames, skipFrames: 4800);

        Assert.InRange(after / before, 1.6, 2.0);
    }

    // ================= pomocnicze =================

    /// <summary>
    /// Doprowadza kompensację do stanu ustalonego przy zadanym poziomie i zwraca podbicie
    /// zmierzone na 50 Hz, w decybelach.
    /// </summary>
    private static double MeasureLoudnessBoost(double volumeDb, double strength, double normalisationDb = 0)
    {
        var loudness = new Loudness { Enabled = true, Strength = strength };
        loudness.Prepare(SampleRate, Channels);
        loudness.SetListeningLevel(volumeDb, normalisationDb);

        // Ubytek dochodzi do celu przez wygładzanie liczone raz na bufor, więc trzeba przepuścić
        // kilkadziesiąt buforów, zanim pomiar cokolwiek znaczy.
        var warmUp = new float[480 * Channels];
        for (var i = 0; i < 120; i++)
        {
            Array.Clear(warmUp);
            loudness.Process(warmUp, 480);
        }

        const int frames = 48000;
        var buffer = Sine(50, 0.25, frames);
        loudness.Process(buffer, frames);

        var measured = Goertzel(buffer, channel: 0, 50, skipFrames: 12000);
        return 20 * Math.Log10(measured / 0.25);
    }

    /// <summary>Zmiana poziomu tonu ustalonego po przejściu przez kompresor, w decybelach.</summary>
    private static double MeasureLevelChangeDb(double inputAmplitude)
    {
        var dynamics = new DynamicRange { Enabled = true, Strength = 100 };
        dynamics.Prepare(SampleRate, Channels);

        const int frames = 96000;
        var buffer = Sine(440, inputAmplitude, frames);
        dynamics.Process(buffer, frames);

        // Druga połowa: obwiednia zdążyła już dojść do stanu ustalonego.
        var measured = Goertzel(buffer, channel: 0, 440, skipFrames: frames / 2);
        return 20 * Math.Log10(measured / inputAmplitude);
    }

    /// <summary>
    /// Amplituda składowej o zadanej częstotliwości, liczona algorytmem Goertzela. Tańszy
    /// od pełnej transformaty i wystarczający, gdy interesuje nas kilka wybranych miejsc widma.
    /// </summary>
    private static double Goertzel(float[] buffer, int channel, double frequency, int skipFrames)
    {
        var frames = buffer.Length / Channels;
        var count = frames - skipFrames;
        if (count <= 0) return 0;

        var omega = 2 * Math.PI * frequency / SampleRate;
        var cosine = Math.Cos(omega);
        var coefficient = 2 * cosine;

        double s1 = 0, s2 = 0;

        for (var frame = skipFrames; frame < frames; frame++)
        {
            var s0 = buffer[frame * Channels + channel] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        var real = s1 - s2 * cosine;
        var imaginary = s2 * Math.Sin(omega);

        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
    }

    private static double SideEnergy(float[] buffer, int frames, int skipFrames)
    {
        var sum = 0.0;
        for (var frame = skipFrames; frame < frames; frame++)
        {
            var side = (buffer[frame * Channels] - buffer[frame * Channels + 1]) * 0.5;
            sum += side * side;
        }

        return Math.Sqrt(sum / (frames - skipFrames));
    }

    private static float[] Sine(double frequency, double amplitude, int frames)
    {
        var buffer = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / SampleRate));
            buffer[frame * Channels] = value;
            buffer[frame * Channels + 1] = value;
        }

        return buffer;
    }

    private static float[] SineOnLeftOnly(double frequency, double amplitude, int frames)
    {
        var buffer = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
            buffer[frame * Channels] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / SampleRate));

        return buffer;
    }

    private static float[] Stereo(double leftHz, double rightHz, double amplitude, int frames,
        double rightPhase = 0)
    {
        var buffer = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            buffer[frame * Channels] =
                (float)(amplitude * Math.Sin(2 * Math.PI * leftHz * frame / SampleRate));
            buffer[frame * Channels + 1] =
                (float)(amplitude * Math.Sin(2 * Math.PI * rightHz * frame / SampleRate + rightPhase));
        }

        return buffer;
    }
}
