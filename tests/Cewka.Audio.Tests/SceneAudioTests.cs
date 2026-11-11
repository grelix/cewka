using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>Dane dźwięku dla sceny: pierścień próbek, poziomy kanałów, widmo w pasmach.</summary>
public class SceneAudioTests
{
    private static (SpectrumAnalyser Analyser, SceneAudio Scene) Rig(int sampleRate)
    {
        var analyser = new SpectrumAnalyser();
        analyser.Prepare(sampleRate, Channels);
        return (analyser, new SceneAudio(analyser));
    }

    private static float[] Stereo(int frames, Func<int, float> left, Func<int, float> right)
    {
        var buffer = new float[frames * Channels];
        for (var i = 0; i < frames; i++)
        {
            buffer[i * Channels] = left(i);
            buffer[i * Channels + 1] = right(i);
        }

        return buffer;
    }

    [Fact]
    public void ChannelsStaySeparateInTheRing()
    {
        var (analyser, scene) = Rig(48000);
        ProcessInBlocks(analyser, Stereo(4800, i => 0.5f * MathF.Sin(i * 0.1f), _ => 0f), 480);

        var left = new float[1024];
        var right = new float[1024];
        Assert.True(scene.ReadWaveform(left, right));

        Assert.InRange(left.Max(), 0.45f, 0.5f);
        Assert.All(right, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void LevelsHaveKnownValuesPerChannel()
    {
        var (analyser, scene) = Rig(48000);
        ProcessInBlocks(analyser, Stereo(4800, _ => 0.5f, i => i % 2 == 0 ? 0.25f : -0.25f), 480);

        var levels = scene.ReadLevels(1024);

        Assert.Equal(0.5f, levels.RmsLeft, 4);
        Assert.Equal(0.5f, levels.PeakLeft, 4);
        Assert.Equal(0.25f, levels.RmsRight, 4);
        Assert.Equal(0.25f, levels.PeakRight, 4);
    }

    [Fact]
    public void SineRmsAndPeakMatchTheAmplitude()
    {
        var (analyser, scene) = Rig(48000);
        ProcessInBlocks(analyser, Sine(1000, 0.8, 48000, 48000), 480);

        var levels = scene.ReadLevels(4096);

        Assert.InRange(levels.PeakLeft, 0.79f, 0.8f);
        Assert.InRange(levels.RmsLeft, 0.8f / MathF.Sqrt(2) - 0.01f, 0.8f / MathF.Sqrt(2) + 0.01f);
    }

    [Fact]
    public void SilenceGivesZeroWithoutNaN()
    {
        var (analyser, scene) = Rig(48000);
        ProcessInBlocks(analyser, new float[48000 * Channels], 480);

        var levels = scene.ReadLevels(2048);
        var spectrum = scene.ReadSpectrum();

        Assert.Equal(default, levels);
        Assert.Equal(48, spectrum.Length);
        foreach (var band in spectrum) Assert.Equal(0f, band);
    }

    [Fact]
    public void ReadingBeforeAnySoundGivesZeros()
    {
        var (_, scene) = Rig(48000);

        Assert.Equal(default, scene.ReadLevels());
        foreach (var band in scene.ReadSpectrum()) Assert.Equal(0f, band);
    }

    [Theory]
    [InlineData(44100, 20)]
    [InlineData(48000, 20)]
    [InlineData(44100, 30)]
    [InlineData(48000, 30)]
    [InlineData(44100, 40)]
    [InlineData(48000, 40)]
    [InlineData(44100, 47)]
    [InlineData(48000, 47)]
    public void SinePeaksInTheBandOfItsFrequency(int sampleRate, int band)
    {
        var (analyser, scene) = Rig(sampleRate);
        const double amplitude = 0.5;
        ProcessInBlocks(analyser, Sine(SceneAudio.BandCenter(band), amplitude, sampleRate / 2, sampleRate), 512);

        var spectrum = scene.ReadSpectrum();
        var strongest = 0;
        for (var i = 1; i < spectrum.Length; i++)
            if (spectrum[i] > spectrum[strongest]) strongest = i;

        Assert.Equal(band, strongest);
        Assert.InRange(spectrum[band], (float)(amplitude * 0.8), (float)(amplitude * 1.05));
        Assert.True(spectrum[band - 4] < amplitude * 0.15, $"pasmo {band - 4}: {spectrum[band - 4]}");
        Assert.True(spectrum[Math.Min(47, band + 4)] < amplitude * 0.15 || band + 4 > 47);
    }

    [Fact]
    public void BandsCoverTheRequestedRangeInLogarithmicSteps()
    {
        Assert.Equal(48, SceneAudio.BandCount);
        Assert.Equal(30, BandSpectrum.BandEdge(0), 6);
        Assert.Equal(16000, BandSpectrum.BandEdge(48), 3);

        var ratio = BandSpectrum.BandEdge(1) / BandSpectrum.BandEdge(0);
        for (var i = 1; i < 48; i++)
            Assert.Equal(ratio, BandSpectrum.BandEdge(i + 1) / BandSpectrum.BandEdge(i), 9);
    }

    [Fact]
    public void ReadsAreDelayedByTheOutputLatency()
    {
        var (analyser, scene) = Rig(48000);
        analyser.OutputLatencyFrames = 200;
        ProcessInBlocks(analyser, Stereo(1000, i => i + 1, i => -(i + 1)), 250);

        var left = new float[100];
        var right = new float[100];
        scene.ReadWaveform(left, right);

        // Ostatnia ramka zapisana to 1000; po cofnięciu o 200 ramek ostatnia odczytana to 800.
        Assert.Equal(800f, left[^1]);
        Assert.Equal(701f, left[0]);
        Assert.Equal(-800f, right[^1]);
    }

    [Fact]
    public void ResetClearsTheRing()
    {
        var (analyser, scene) = Rig(48000);
        ProcessInBlocks(analyser, Stereo(4800, _ => 0.5f, _ => 0.5f), 480);
        analyser.Reset();

        Assert.Equal(default, scene.ReadLevels(1024));
    }

    /// <summary>Piszący wątek dźwiękowy i czytający wątek rysowania nigdy nie widzą rozdartego okna.</summary>
    [Fact]
    public async Task ConcurrentReadsAreConsistent()
    {
        var ring = new SampleRing();
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            var block = new float[256 * Channels];
            long frame = 0;
            // Wartości są liczbami całkowitymi w float, więc muszą zostać poniżej 2^24.
            while (!stop.IsCancellationRequested && frame < 15_000_000)
            {
                Thread.SpinWait(3000);
                for (var i = 0; i < 256; i++)
                {
                    frame++;
                    block[i * Channels] = frame;
                    block[i * Channels + 1] = -frame;
                }

                ring.Write(block, 256, Channels);
            }
        });

        var left = new float[2048];
        var right = new float[2048];
        var valid = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // Co najmniej 400 ms sprawdzania, a pod obciążeniem czas na to, żeby wątek zapisu ruszył.
        while (watch.ElapsedMilliseconds < 400 || (valid <= 50 && watch.ElapsedMilliseconds < 15_000))
        {
            if (!ring.Read(left, right, 500)) continue;
            if (left[0] == 0) continue;

            valid++;
            for (var i = 1; i < left.Length; i++)
            {
                Assert.Equal(left[i - 1] + 1, left[i]);
                Assert.Equal(-left[i], right[i]);
            }
        }

        stop.Cancel();
        await writer;
        Assert.True(valid > 50, $"za mało poprawnych odczytów: {valid}");
    }

    [Fact]
    public void FrameReadsDoNotAllocate()
    {
        var (analyser, scene) = Rig(48000);
        ProcessInBlocks(analyser, Sine(440, 0.5, 48000, 48000), 480);

        var left = new float[1024];
        var right = new float[1024];
        for (var i = 0; i < 100; i++) Frame(scene, left, right);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++) Frame(scene, left, right);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void WritingFromTheAudioThreadDoesNotAllocate()
    {
        var (analyser, _) = Rig(48000);
        var block = Sine(440, 0.5, 480, 48000);
        for (var i = 0; i < 100; i++) analyser.Process(block, 480);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++) analyser.Process(block, 480);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Frame(SceneAudio scene, float[] left, float[] right)
    {
        scene.ReadWaveform(left, right);
        scene.ReadLevels(1024);
        scene.ReadSpectrum();
    }
}
