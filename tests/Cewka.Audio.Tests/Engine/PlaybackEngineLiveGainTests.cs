using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Głośność stacji w silniku: pomiar na wątku dekodującym, wzmocnienie w torze.</summary>
public class PlaybackEngineLiveGainTests
{
    private const float A = 0.25f;

    private static ScriptedLiveDecoder Sine(double levelDbfs, long frames = long.MaxValue / 2)
    {
        var amplitude = Math.Pow(10, levelDbfs / 20);
        return new ScriptedLiveDecoder(signal: frame =>
            (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * frame / EngineHarness.Rate))).Feed(frames);
    }

    private static LiveRig QuietRig(double levelDbfs = -30) => new() { NextDecoder = () => Sine(levelDbfs) };

    /// <summary>
    /// Stacja z dźwiękiem krótszym niż pierwszy pomiar. Dekoder wyprzedza wyjście, więc przy obciążeniu
    /// bez tego limitu pomiar mógłby podnieść wzmocnienie, zanim test je odczyta.
    /// </summary>
    private static LiveRig ShortStationRig() =>
        new() { NextDecoder = () => Sine(-30, EngineHarness.Frames(6)) };

    private static double Gain(LiveRig rig) => rig.Engine.Graph.Normalisation.TargetDecibels;

    [Fact]
    public void QuietStationComesUpAfterTenSecondsAndSlowly()
    {
        using var rig = ShortStationRig();
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position > EngineHarness.Frames(5));
        Assert.Equal(0, Gain(rig), precision: 3);

        rig.Decoder.Feed(long.MaxValue / 2);
        h.PumpUntil(() => Gain(rig) > 0.1, 30000);
        Assert.True(rig.Decoder.Position >= EngineHarness.Frames(10));
        Assert.InRange(Gain(rig), 0.4, 0.6);

        h.PumpUntil(() => Gain(rig) > 1.4, 30000);
        Assert.True(rig.Decoder.Position >= EngineHarness.Frames(14));
    }

    [Fact]
    public void WithNormalisationOffTheStationStaysAtUnityButIsStillMeasured()
    {
        using var rig = QuietRig();
        var h = rig.Harness;
        rig.Engine.Graph.NormalisationEnabled = false;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position > EngineHarness.Frames(13), 30000);
        Assert.Equal(0, Gain(rig), precision: 3);

        rig.Engine.Graph.NormalisationEnabled = true;
        rig.Engine.RefreshTrackGain();

        Assert.True(Gain(rig) > 0.4);
    }

    /// <summary>Źródło wzmocnień plików nic nie wie o stacji, nawet gdy nazwa wpisu pasuje.</summary>
    [Fact]
    public void RefreshUsesTheStationGainNotTheFileSource()
    {
        using var rig = ShortStationRig();
        var h = rig.Harness;
        rig.Engine.Graph.GainSource = new FixedGains(new Dictionary<string, double> { ["s1"] = -9 });

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        Assert.Equal(0, Gain(rig), precision: 3);

        rig.Engine.RefreshTrackGain();

        Assert.Equal(0, Gain(rig), precision: 3);
    }

    /// <summary>Powrót do stacji startuje od znanego poziomu, bez ponownego dochodzenia.</summary>
    [Fact]
    public void ReturningToAStationStartsFromItsKnownLevel()
    {
        using var rig = QuietRig();
        var h = rig.Harness;
        var station = LiveRig.Station("glosna");

        rig.Engine.PlayLive(station);
        h.PumpUntil(() => Gain(rig) > 1.4, 30000);
        var known = Gain(rig);

        rig.Engine.PlayLive(LiveRig.Station("inna"));
        h.WaitForSeekToBeTaken();
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        h.PumpPeriods(2);
        Assert.Equal(0, Gain(rig), precision: 3);

        rig.Engine.PlayLive(station);
        h.WaitForSeekToBeTaken();
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        h.PumpPeriods(2);
        Assert.Equal(known, Gain(rig), precision: 1);
    }

    /// <summary>Pauza to nowe połączenie, ale ta sama stacja: pomiar idzie dalej, bez 10 s przerwy.</summary>
    [Fact]
    public void ReconnectingKeepsTheMeasurement()
    {
        using var rig = QuietRig();
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => Gain(rig) > 0.9, 30000);
        var before = Gain(rig);

        rig.Engine.Pause();
        rig.Engine.Play();
        h.PumpUntil(() => rig.Decoders.Count == 2);
        var second = rig.Decoder;
        h.PumpUntil(() => Gain(rig) > before + 0.1, 30000);

        Assert.True(second.Position < EngineHarness.Frames(5), "pomiar zaczął się od nowa");
    }

    [Fact]
    public void TargetComesFromTheGraph()
    {
        using var rig = QuietRig();
        var h = rig.Harness;
        rig.Engine.Graph.LiveTargetLufs = -30;
        Assert.Equal(-30, rig.Engine.Graph.LiveTargetLufs);

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position > EngineHarness.Frames(16), 30000);

        Assert.Equal(0, Gain(rig), precision: 3);
    }

    [Fact]
    public void FileAfterTheStationGetsItsOwnGainAndTheStationStartsFromUnity()
    {
        using var rig = ShortStationRig();
        var h = rig.Harness;
        rig.Engine.Graph.GainSource = new FixedGains(new Dictionary<string, double> { ["a"] = -6 });
        h.Engine.SetQueue([h.Track("a", 60, A)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        h.PumpPeriods(2);
        Assert.Equal(-6, Gain(rig), precision: 1);

        rig.Engine.PlayLive(LiveRig.Station());
        h.WaitForSeekToBeTaken();
        h.PumpUntil(() => rig.Started > 0);
        Assert.Equal(0, Gain(rig), precision: 3);

        rig.Engine.StopLive();
        rig.Engine.Play();
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        h.PumpPeriods(2);
        Assert.Equal(-6, Gain(rig), precision: 1);
    }

    /// <summary>Pomiar nie patrzy na wzmocnienie: próbki idą do niego sprzed toru.</summary>
    [Fact]
    public void MeasurementDoesNotDependOnTheVolumeSlider()
    {
        using var rig = QuietRig();
        var h = rig.Harness;
        rig.Engine.Graph.Volume.TargetDecibels = -40;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => Gain(rig) > 0.4, 30000);

        Assert.True(rig.Decoder.Position >= EngineHarness.Frames(10));
    }
}
