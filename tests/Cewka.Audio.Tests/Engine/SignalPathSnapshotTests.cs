using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Migawka toru: opisuje utwór słyszany, a nie ten, którego dekoder już czeka w buforze, wraz z formatem
/// źródła, wzmocnieniem wyrównania, które naprawdę działa, i parametrami urządzenia.
/// </summary>
public sealed class SignalPathSnapshotTests
{
    /// <summary>Stałe wzmocnienia według nazwy pliku; wartość zmienia się w trakcie testu.</summary>
    private sealed class FixedGains : ITrackGainSource
    {
        public Dictionary<string, TrackGain> Gains { get; } = new(StringComparer.Ordinal);

        public TrackGain? GetTrackGain(QueueEntry entry) =>
            Gains.TryGetValue(Path.GetFileName(entry.Path), out var gain) ? gain : null;
    }

    private static SyntheticDecoder Source(SyntheticDecoder decoder, int rate, int channels) =>
        new(decoder.TotalFrames, 0.1f, decoder.Channels, decoder.SampleRate) { SourceSampleRate = rate, SourceChannels = channels };

    [Fact]
    public void PrzedPierwszymOdtworzeniemNicNieGraIUrzadzenieJestZamkniete()
    {
        using var h = new EngineHarness();

        var path = h.Engine.SignalPath;

        Assert.Null(path.Entry);
        Assert.False(path.DeviceOpen);
        Assert.False(path.Resamples);
        Assert.False(path.RemapsChannels);
        Assert.Null(path.Gain);
    }

    [Fact]
    public void PlikMonoZ441KHzNaUrzadzeniuStereo48KHzMaKonwersjeIUkladKanalow()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 2.0, 0.1f, d => Source(d, 44100, 1));
        h.Engine.SetQueue([a]);
        h.Engine.Play();
        h.PumpUntil(() => h.Engine.SignalPath.Entry is not null);

        var path = h.Engine.SignalPath;

        Assert.Equal(a, path.Entry!.Path);
        Assert.False(path.IsLive);
        Assert.Equal(44100, path.SourceSampleRate);
        Assert.Equal(1, path.SourceChannels);
        Assert.True(path.DeviceOpen);
        Assert.Equal("Atrapa", path.DeviceName);
        Assert.Equal(EngineHarness.Rate, path.DeviceSampleRate);
        Assert.Equal(EngineHarness.Channels, path.DeviceChannels);
        Assert.True(path.Resamples);
        Assert.True(path.RemapsChannels);
        Assert.True(Enum.IsDefined(path.Resampling));
    }

    [Fact]
    public void PlikWFormacieUrzadzeniaGraBezKonwersji()
    {
        using var h = new EngineHarness();
        h.Engine.SetQueue([h.Track("a", 2.0, 0.1f)]);
        h.Engine.Play();
        h.PumpUntil(() => h.Engine.SignalPath.Entry is not null);

        var path = h.Engine.SignalPath;

        Assert.Equal(EngineHarness.Rate, path.SourceSampleRate);
        Assert.False(path.Resamples);
        Assert.False(path.RemapsChannels);
    }

    /// <summary>
    /// Dekoder następnego utworu otwiera się, zanim dźwięk poprzedniego wybrzmi. Migawka zostaje przy
    /// formacie słyszanego utworu do chwili, w której granica dociera do wyjścia.
    /// </summary>
    [Fact]
    public void FormatZmieniaSieDopieroNaGranicySlyszanegoUtworu()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 1.0, 0.1f, d => Source(d, 44100, 2));
        var b = h.Track("b", 1.0, 0.2f, d => Source(d, 96000, 2));
        h.Engine.SetQueue([a, b]);
        h.Engine.Play();

        h.PumpUntil(() => h.Opened.Contains("b.wav"));
        var before = h.Engine.SignalPath;
        Assert.Equal(a, before.Entry?.Path);
        Assert.Equal(44100, before.SourceSampleRate);

        h.PumpUntil(() => h.Engine.SignalPath.Entry?.Path == b);
        Assert.Equal(96000, h.Engine.SignalPath.SourceSampleRate);
    }

    [Fact]
    public void WzmocnienieWyrownaniaZRodzajemIJegoBrakPoWylaczeniu()
    {
        using var h = new EngineHarness();
        var gains = new FixedGains();
        gains.Gains["a.wav"] = new TrackGain(-4.5, -14, TrackGainKind.Album);
        h.Engine.Graph.GainSource = gains;

        h.Engine.SetQueue([h.Track("a", 3.0, 0.1f)]);
        h.Engine.Play();
        h.PumpUntil(() => h.Engine.SignalPath.Entry is not null);

        Assert.Equal(new TrackGain(-4.5, -14, TrackGainKind.Album), h.Engine.SignalPath.Gain);

        // Zmiana ustawienia odczytuje wzmocnienie od nowa; migawka podaje nowe, a nie to z otwarcia pliku.
        gains.Gains["a.wav"] = new TrackGain(2.0, -20, TrackGainKind.Track);
        h.Engine.RefreshTrackGain();
        Assert.Equal(new TrackGain(2.0, -20, TrackGainKind.Track), h.Engine.SignalPath.Gain);

        h.Engine.Graph.NormalisationEnabled = false;
        Assert.Null(h.Engine.SignalPath.Gain);
    }

    [Fact]
    public void StacjaPodajeFormatZDekoderaIOznaczenieStrumienia()
    {
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder { SourceChannels = 1 }.Feed(long.MaxValue / 2) };

        rig.Engine.PlayLive(LiveRig.Station());
        rig.Harness.PumpUntil(() => rig.Engine.SignalPath.Entry is not null);

        var path = rig.Engine.SignalPath;
        Assert.True(path.IsLive);
        Assert.Equal(EngineHarness.Rate, path.SourceSampleRate);
        Assert.Equal(1, path.SourceChannels);
        Assert.True(path.RemapsChannels);
        Assert.False(path.Resamples);
    }
}
