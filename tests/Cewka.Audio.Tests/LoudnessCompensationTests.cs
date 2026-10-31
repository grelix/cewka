using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>Kompensacja głośności przy zmianach ustawień w trakcie odtwarzania.</summary>
public class LoudnessCompensationTests
{
    private const int SampleRate = 48000;

    /// <summary>
    /// Ruch suwaka siły przy nieruchomej głośności musi zmienić podbicie. Głośność stoi,
    /// więc ubytek poziomu się nie zmienia, a mimo to nowa siła ma wejść.
    /// </summary>
    [Fact]
    public void StrengthChangeTakesEffectAtConstantVolume()
    {
        var loudness = Prepared(strength: 100, volumeDb: -20);
        RunSilence(loudness, buffers: 50, frames: 480);
        var before = loudness.CurrentBassBoostDb;

        loudness.Strength = 20;
        RunSilence(loudness, buffers: 100, frames: 480);
        var after = loudness.CurrentBassBoostDb;

        Assert.True(before > 6, $"brak podbicia przed zmianą ({before:F2} dB)");
        Assert.InRange(after, before * 0.2 - 0.05, before * 0.2 + 0.05);
    }

    /// <summary>Siła podniesiona z zera przy stałej głośności też musi zadziałać.</summary>
    [Fact]
    public void StrengthRaisedFromZeroTakesEffect()
    {
        var loudness = Prepared(strength: 0, volumeDb: -20);
        RunSilence(loudness, buffers: 20, frames: 480);
        Assert.Equal(0, loudness.CurrentBassBoostDb, precision: 6);

        loudness.Strength = 100;
        RunSilence(loudness, buffers: 100, frames: 480);

        Assert.InRange(loudness.CurrentBassBoostDb, 6.9, 7.1);   // 0,35 · 20 dB
    }

    /// <summary>
    /// Zmiana siły wchodzi płynnie: pierwszy bufor po kliknięciu w tor suwaka przenosi tylko
    /// ułamek różnicy, a nie całą w jednym kroku.
    /// </summary>
    [Fact]
    public void StrengthChangeIsSmoothed()
    {
        var loudness = Prepared(strength: 100, volumeDb: -20);
        RunSilence(loudness, buffers: 50, frames: 480);
        var before = loudness.CurrentBassBoostDb;

        loudness.Strength = 20;
        RunSilence(loudness, buffers: 1, frames: 480);
        var firstStep = before - loudness.CurrentBassBoostDb;

        Assert.InRange(firstStep, 0.01, (before * 0.8) * 0.3);
    }

    /// <summary>
    /// Czas dojścia do nowej wartości liczony w sekundach, a nie w buforach: przy okresie
    /// 256 i 2048 ramek po 100 ms podbicie przebywa taką samą część drogi.
    /// </summary>
    [Fact]
    public void SmoothingTimeDoesNotDependOnBufferSize()
    {
        double Progress(int frames)
        {
            var loudness = Prepared(strength: 100, volumeDb: 0);
            loudness.SetListeningLevel(-20, 0);
            RunSilence(loudness, buffers: (int)Math.Round(0.1024 * SampleRate / frames), frames: frames);
            return loudness.CurrentBassBoostDb / 7.0;
        }

        var small = Progress(256);
        var large = Progress(2048);

        Assert.InRange(small, 0.7, 0.95);
        Assert.InRange(large, small - 0.08, small + 0.08);
    }

    /// <summary>
    /// Wyłączenie w trakcie gry wygasza podbicie stopniowo, a po wygaszeniu sygnał przechodzi
    /// bez zmiany ani jednej próbki.
    /// </summary>
    [Fact]
    public void DisablingFadesTheBoostOutThenBypasses()
    {
        var loudness = Prepared(strength: 100, volumeDb: -20);
        var tone = Sine(50, 0.25, SampleRate, SampleRate);
        ProcessInBlocks(loudness, tone, 480);

        loudness.Enabled = false;
        var transition = Sine(50, 0.25, 4800, SampleRate, startFrame: SampleRate);
        var reference = (float[])transition.Clone();
        ProcessInBlocks(loudness, transition, 480);

        // Pierwszy bufor po wyłączeniu nadal niesie podbicie, więc nie było skoku do zera.
        Assert.True(Goertzel(transition, 0, 50, SampleRate, 0, 480) > 0.25 * 1.5);

        RunSilence(loudness, buffers: 100, frames: 480);
        var after = Sine(50, 0.25, 4800, SampleRate);
        var untouched = (float[])after.Clone();
        ProcessInBlocks(loudness, after, 480);

        Assert.Equal(untouched, after);
        Assert.NotEqual(reference, transition);
    }

    /// <summary>
    /// Ubytek liczy się z poziomu, na jakim program naprawdę gra. Dwa utwory o różnym
    /// wzmocnieniu normalizacji grają po wyrównaniu tak samo głośno, więc dostają tę samą korektę.
    /// </summary>
    [Fact]
    public void CompensationIgnoresTheNormalisationGainItself()
    {
        var graph = new AudioGraph { GainSource = new FixedGains(("loud.flac", -9, 0), ("quiet.flac", 9, 0)) };
        graph.Loudness.Enabled = true;
        graph.Loudness.Strength = 100;
        graph.Volume.TargetDecibels = -20;
        graph.Prepare(SampleRate, Channels);

        graph.OnTrackChanged(new() { Path = "loud.flac" });
        RunGraph(graph, buffers: 100);
        var loud = graph.Loudness.CurrentBassBoostDb;

        graph.OnTrackChanged(new() { Path = "quiet.flac" });
        RunGraph(graph, buffers: 100);
        var quiet = graph.Loudness.CurrentBassBoostDb;

        Assert.Equal(7.0, loud, precision: 2);
        Assert.Equal(loud, quiet, precision: 2);
    }

    /// <summary>
    /// Wyższy poziom docelowy normalizacji podnosi poziom odsłuchu, więc zmniejsza ubytek
    /// o różnicę poziomów: cel −14 LUFS to o 4 dB mniej korekty niż cel −18 LUFS.
    /// </summary>
    [Fact]
    public void HigherNormalisationTargetReducesTheDeficit()
    {
        var graph = new AudioGraph { GainSource = new FixedGains(("a.flac", 2, 4)) };
        graph.Loudness.Enabled = true;
        graph.Loudness.Strength = 100;
        graph.Volume.TargetDecibels = -20;
        graph.Prepare(SampleRate, Channels);

        graph.OnTrackChanged(new() { Path = "a.flac" });
        RunGraph(graph, buffers: 100);

        Assert.Equal(0.35 * 16, graph.Loudness.CurrentBassBoostDb, precision: 2);
    }

    /// <summary>Utwór jeszcze niezmierzony gra bez wyrównania, więc liczy się sam suwak.</summary>
    [Fact]
    public void UnmeasuredTrackUsesTheSliderAlone()
    {
        var graph = new AudioGraph { GainSource = new FixedGains() };
        graph.Loudness.Enabled = true;
        graph.Loudness.Strength = 100;
        graph.Volume.TargetDecibels = -20;
        graph.Prepare(SampleRate, Channels);

        graph.OnTrackChanged(new() { Path = "unknown.flac" });
        RunGraph(graph, buffers: 100);

        Assert.Equal(0, graph.Normalisation.TargetDecibels, precision: 6);
        Assert.Equal(7.0, graph.Loudness.CurrentBassBoostDb, precision: 2);
    }

    // ---------- pomocnicze ----------

    private static Loudness Prepared(double strength, double volumeDb)
    {
        var loudness = new Loudness { Enabled = true, Strength = strength };
        loudness.SetListeningLevel(volumeDb, 0);
        loudness.Prepare(SampleRate, Channels);
        return loudness;
    }

    private static void RunSilence(Loudness loudness, int buffers, int frames)
    {
        var buffer = new float[frames * Channels];
        for (var i = 0; i < buffers; i++)
        {
            Array.Clear(buffer);
            loudness.Process(buffer, frames);
        }
    }

    private static void RunGraph(AudioGraph graph, int buffers)
    {
        var buffer = new float[480 * Channels];
        for (var i = 0; i < buffers; i++)
        {
            Array.Clear(buffer);
            graph.Process(buffer, 480);
        }
    }

    /// <summary>Źródło wzmocnień o z góry znanych wartościach: (ścieżka, wzmocnienie, poziom programu).</summary>
    private sealed class FixedGains(params (string Path, double Gain, double Level)[] gains) : ITrackGainSource
    {
        public TrackGain? GetTrackGain(Cewka.Audio.Playback.QueueEntry entry)
        {
            foreach (var (path, gain, level) in gains)
                if (path == entry.Path) return new TrackGain(gain, level);
            return null;
        }
    }
}
