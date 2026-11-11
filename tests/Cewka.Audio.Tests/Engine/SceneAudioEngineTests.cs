using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Silnik zasila dane sceny dla plików i dla stacji, a w pauzie przestaje je zmieniać.</summary>
public class SceneAudioEngineTests
{
    [Fact]
    public void FilePlaybackFeedsTheSceneData()
    {
        using var h = new EngineHarness();
        h.Engine.SetQueue([h.Track("a", 3, 0.25f)]);

        h.PumpUntil(() => h.Engine.Graph.Scene.ReadLevels(256).PeakLeft > 0);

        var levels = h.Engine.Graph.Scene.ReadLevels(256);
        Assert.Equal(0.25f, levels.PeakLeft, 3);
        Assert.Equal(2 * h.Output.PeriodSizeInFrames, h.Engine.Graph.Analyser.OutputLatencyFrames);
    }

    [Fact]
    public void LiveStationFeedsTheSceneData()
    {
        using var rig = new LiveRig();

        rig.Engine.PlayLive(LiveRig.Station());
        rig.Harness.PumpUntil(() => rig.Started > 0);
        rig.Harness.PumpPeriods(20);

        var levels = rig.Engine.Graph.Scene.ReadLevels(256);
        Assert.True(levels.PeakLeft > 0 || levels.PeakRight > 0);
    }

    [Fact]
    public void PausedPlaybackDoesNotMoveTheRing()
    {
        using var h = new EngineHarness();
        h.Engine.SetQueue([h.Track("a", 5, 0.25f)]);
        h.PumpPeriods(20);

        h.Engine.Pause();
        h.PumpPeriods(60);
        var settled = h.Engine.Graph.Analyser.Ring.Written;

        h.PumpPeriods(20);
        Assert.Equal(settled, h.Engine.Graph.Analyser.Ring.Written);
    }
}
