using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Długość utworu podaje dekoder na podstawie pliku. Interfejs czyta ją co chwilę jako czas,
/// więc wartość spoza rozsądku nie może dojść do zamiany na <see cref="TimeSpan"/>.
/// </summary>
public sealed class TrackLengthTests
{
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L << 60)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void NiedorzecznaDlugoscZDekoderaJestNieznana(long totalFrames)
    {
        using var h = new EngineHarness();
        h.Engine.SetQueue([h.Track("a", 1, 0.1f,
            _ => new SyntheticDecoder(totalFrames, 0.1f, EngineHarness.Channels, EngineHarness.Rate))]);

        h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.CurrentIndex == 0);
        h.PumpPeriods(4);

        Assert.Equal(TimeSpan.Zero, h.Engine.Duration);
        Assert.True(h.Engine.Position >= TimeSpan.Zero);
    }

    [Theory]
    [InlineData(48000L, 48000, 48000L)]
    [InlineData(0L, 48000, 0L)]
    [InlineData(-1L, 48000, 0L)]
    [InlineData(30L * 24 * 3600 * 48000, 48000, 30L * 24 * 3600 * 48000)]
    [InlineData(31L * 24 * 3600 * 48000, 48000, 0L)]
    [InlineData(long.MaxValue, 0, 0L)]
    public void RozsadnaDlugoscZostajeBezZmian(long frames, int rate, long expected)
    {
        Assert.Equal(expected, PlaybackEngine.PlausibleLength(frames, rate));
    }
}
