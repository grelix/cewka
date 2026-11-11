using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Miękka pauza: wyciszenie w zadanym czasie zamiast cięcia, zatrzymanie wyjścia dopiero po nim
/// i podgłośnienie przy wznowieniu. Miarą trzasku jest największa różnica sąsiednich próbek.
/// </summary>
public class SoftPauseTests
{
    private const float A = 0.25f;
    private const float B = 0.5f;

    private static readonly TimeSpan Fade = TimeSpan.FromSeconds(0.2);

    private static float[] Left(float[] samples) => samples.Where((_, i) => i % EngineHarness.Channels == 0).ToArray();

    private static float LargestStep(float[] left) => left.Zip(left.Skip(1), (x, y) => Math.Abs(y - x)).Max();

    /// <summary>Utwór gra, a bufor jest pełny: szybkie pompowanie w czasie wyciszenia nie trafi na niedobór.</summary>
    private static void PlayWithFullBuffer(EngineHarness h, params string[] paths)
    {
        h.Engine.SetQueue(paths);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.3));
        EngineHarness.WaitUntil(() => h.Engine.BufferedFrames > EngineHarness.Frames(0.4));
    }

    /// <summary>Pauza do końca: wyciszenie w tle i zatrzymanie wyjścia.</summary>
    private static void PauseCompletely(EngineHarness h)
    {
        h.Output.StartAutoPump(independentOfSystemClock: true);
        h.Engine.Pause();
        EngineHarness.WaitUntil(() => !h.Engine.IsSoftPausing);
        h.Output.StopAutoPump();
    }

    [Fact]
    public void MiekkaPauzaWyciszaStopniowoIZatrzymujeWyjsciePoWyciszeniu()
    {
        using var h = new EngineHarness(periodFrames: 128);
        h.Engine.SoftPause = Fade;
        PlayWithFullBuffer(h, h.Track("a", 10, A));

        h.Output.ClearSamples();
        h.Engine.Pause();

        // Stan jest pauzą od razu; wątek interfejsu nie czeka na wyciszenie.
        Assert.Equal(PlaybackState.Paused, h.Engine.State);
        Assert.True(h.Engine.IsSoftPausing);

        // Rampa wyciszenia liczy się w ramkach, a zatrzymanie wyjścia w czasie zegarowym. Ramki rampy
        // pompuje test sam, zanim ten czas minie: wątek naśladujący urządzenie nie nadąża za zegarem
        // na obciążonej maszynie i wyjście stawało, zanim rampa zeszła do zera.
        h.PumpPeriods((int)(EngineHarness.Frames(0.25) / 128) + 1);
        h.Output.StartAutoPump(independentOfSystemClock: true);
        EngineHarness.WaitUntil(() => !h.Engine.IsSoftPausing);
        h.Output.StopAutoPump();

        Assert.False(h.Output.IsRunning);
        var left = Left(h.Output.Samples);
        var falling = left.Count(s => s > 0.01f && s < A - 0.01f);
        Assert.True(falling >= EngineHarness.Frames(0.15), $"ramek opadania: {falling}");
        Assert.True(LargestStep(left) < 0.005f, $"skok przy miękkiej pauzie {LargestStep(left)}");
        Assert.Equal(0f, left[^1]);
    }

    [Fact]
    public void WznowienieWTrakcieWyciszaniaWracaBezSkokuIZostajeGrajace()
    {
        using var h = new EngineHarness(periodFrames: 128);
        h.Engine.SoftPause = TimeSpan.FromSeconds(0.4);
        PlayWithFullBuffer(h, h.Track("a", 10, A));

        h.Output.ClearSamples();
        h.Engine.Pause();
        h.PumpPeriods(40);
        h.Engine.Play();

        Assert.False(h.Engine.IsSoftPausing);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);

        h.PumpPeriods(200);
        var left = Left(h.Output.Samples);
        Assert.True(left.Min() > 0.1f, $"dźwięk zszedł do {left.Min()}");
        Assert.True(LargestStep(left) < 0.005f, $"skok przy wznowieniu {LargestStep(left)}");
        Assert.Equal(A, left[^1], 1e-5f);

        // Zatrzymanie zaplanowane przez przerwaną pauzę nie może zatrzymać grania.
        Thread.Sleep(Fade * 3 + TimeSpan.FromMilliseconds(200));
        Assert.True(h.Output.IsRunning);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    [Fact]
    public void WznowieniePoMiekkiejPauziePodglasniaStopniowo()
    {
        using var h = new EngineHarness(periodFrames: 128);
        h.Engine.SoftPause = Fade;
        PlayWithFullBuffer(h, h.Track("a", 10, A));
        PauseCompletely(h);

        h.Output.ClearSamples();
        h.Engine.Play();
        h.PumpPeriods(150);

        var left = Left(h.Output.Samples);
        Assert.True(left[0] < 0.01f);
        var rising = left.Count(s => s > 0.01f && s < A - 0.01f);
        Assert.True(rising >= EngineHarness.Frames(0.15), $"ramek narastania: {rising}");
        Assert.True(LargestStep(left) < 0.005f, $"skok przy wznowieniu {LargestStep(left)}");
        Assert.Equal(A, left[^1], 1e-5f);
    }

    [Fact]
    public void NowyWyborPoMiekkiejPauzieGraOdRazuPelnymPoziomem()
    {
        using var h = new EngineHarness(periodFrames: 128);
        h.Engine.SoftPause = Fade;
        var a = h.Track("a", 10, A);
        var b = h.Track("b", 10, B);
        PlayWithFullBuffer(h, a, b);
        PauseCompletely(h);

        h.Output.ClearSamples();
        h.Engine.PlayIndex(1);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && !h.Engine.IsSeeking);
        h.PumpPeriods(40);

        var left = Left(h.Output.Samples);
        var between = left.Count(s => s > 0.01f && s < B - 0.01f);
        Assert.True(between < EngineHarness.Frames(0.03), $"ramek narastania: {between}");
        Assert.Equal(B, left[^1], 1e-5f);
    }

    [Fact]
    public void BezMiekkiejPauzyPauzaZatrzymujeWyjscieOdRazu()
    {
        using var h = new EngineHarness(periodFrames: 128);
        PlayWithFullBuffer(h, h.Track("a", 10, A));

        h.Output.StartAutoPump();
        h.Engine.Pause();
        h.Output.StopAutoPump();

        Assert.False(h.Engine.IsSoftPausing);
        Assert.False(h.Output.IsRunning);
    }

    [Fact]
    public void ZatrzymanieWTrakcieWyciszaniaKonczyPauzeAOdtworzenieGraPelnymPoziomem()
    {
        using var h = new EngineHarness(periodFrames: 128);
        h.Engine.SoftPause = Fade;
        PlayWithFullBuffer(h, h.Track("a", 10, A));

        h.Engine.Pause();
        h.PumpPeriods(20);
        h.Output.StartAutoPump();
        h.Engine.Stop();
        h.Output.StopAutoPump();
        Assert.Equal(PlaybackState.Stopped, h.Engine.State);

        h.Output.ClearSamples();
        h.Engine.Play();
        h.PumpPeriods(60);

        var left = Left(h.Output.Samples);
        var between = left.Count(s => s > 0.01f && s < A - 0.01f);
        Assert.True(between < EngineHarness.Frames(0.03), $"ramek narastania: {between}");
        Assert.Equal(A, left[^1], 1e-5f);
    }
}
