using System.Text.Json;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Wzmocnienie albumu w silniku: silnik podaje źródłu sąsiadów wpisu, więc dwa utwory jednego
/// albumu o różnej głośności grają z jednym wzmocnieniem i bez skoku poziomu na granicy.
/// </summary>
public sealed class AlbumGainEngineTests
{
    private const float Value = 0.5f;

    /// <summary>
    /// Otwarcie utworu w wątku dekodującym pozwala źródłu czytać znaczniki sąsiadów; odświeżenie
    /// po zmianie ustawienia, wołane z wątku interfejsu, nie pozwala.
    /// </summary>
    [Fact]
    public void OdswiezenieZabraniaCzytaniaZnacznikowAOtwarciePozwala()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, Value);
        var b = h.Track("b", 10, Value);
        var source = new RecordingGains();
        h.Engine.Graph.GainSource = source;

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));
        Assert.True(source.Calls.TryDequeue(out var opening), "otwarcie nie zapytało o wzmocnienie");
        Assert.True(opening.MayReadTags);
        Assert.Same(h.Engine.Queue[1], opening.Next);

        h.Engine.RefreshTrackGain();
        Assert.True(source.Calls.TryDequeue(out var refresh), "odświeżenie nie zapytało o wzmocnienie");
        Assert.False(refresh.MayReadTags);
        Assert.Same(h.Engine.Queue[1], refresh.Next);
    }

    private sealed class RecordingGains : ITrackGainSource
    {
        public System.Collections.Concurrent.ConcurrentQueue<TrackGainContext> Calls { get; } = new();

        public TrackGain? GetTrackGain(Cewka.Audio.Playback.QueueEntry entry) => GetTrackGain(entry, TrackGainContext.Alone);

        public TrackGain? GetTrackGain(Cewka.Audio.Playback.QueueEntry entry, TrackGainContext context)
        {
            Calls.Enqueue(context);
            return new TrackGain(0, 0);
        }
    }

    /// <summary>
    /// Utwory zmierzone na −12 i −24 LUFS: wzmocnienia utworów różnią się o 12 dB, a albumowe jest
    /// wspólne. Bez sąsiadów od silnika tryb „Automatyczny” dałby wzmocnienia utworów i skok.
    /// </summary>
    [Theory]
    [InlineData(NormalisationScope.Auto)]
    [InlineData(NormalisationScope.Album)]
    public void DwaUtworyJednegoAlbumuGrajaBezSkokuPoziomu(NormalisationScope scope)
    {
        var (left, start, length) = PlayAlbum(scope);

        var albumLufs = 10 * Math.Log10((Math.Pow(10, -1.2) + Math.Pow(10, -2.4)) / 2);
        var expected = Value * (float)Math.Pow(10, (-18 - albumLufs) / 20);

        // Od końca wyciszenia wejściowego do ostatniej ramki drugiego utworu jeden poziom, także na granicy.
        // Pierwsza ramka utworu jest wyciszona do zera, więc dźwięk zaczyna się od drugiej.
        var off = Enumerable.Range(start + 400, 2 * length - 401).Where(i => Math.Abs(left[i] - expected) > 1e-3f).ToList();
        Assert.True(off.Count == 0, $"{off.Count} ramek poza poziomem albumu, pierwsza {(off.Count > 0 ? off[0] - start : 0)}");
    }

    /// <summary>Ten sam album w trybie utworu: poziomy na granicy różnią się, więc test wyżej łapie skok.</summary>
    [Fact]
    public void WTrybieUtworuPoziomySieRoznia()
    {
        var (left, start, length) = PlayAlbum(NormalisationScope.Track);
        var first = left[start + length / 2];
        var second = left[start + length + length / 2];

        Assert.Equal(Value * (float)Math.Pow(10, -6.0 / 20), first, 3);
        Assert.True(second > first * 2, $"utwór cichszy nie został podbity: {first} → {second}");
    }

    /// <summary>Lewy kanał wyjścia dwóch utworów albumu granych jeden po drugim, początek dźwięku i długość utworu.</summary>
    private static (float[] Left, int Start, int Length) PlayAlbum(NormalisationScope scope)
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 1.0, Value);
        var b = h.Track("b", 1.0, Value);
        Tag(a, 1);
        Tag(b, 2);

        var directory = Path.GetDirectoryName(a)!;
        var cache = Path.Combine(directory, "loudness.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(new[] { Record(a, -12), Record(b, -24) }));

        using var loudness = new LoudnessService(cache, (_, _) => null) { TargetLufs = -18, Scope = scope };
        h.Engine.Graph.GainSource = loudness;

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Finished > 0);

        var left = h.Output.Samples.Where((_, i) => i % EngineHarness.Channels == 0).ToArray();
        var start = Array.FindIndex(left, s => s > 0);
        return (left, start, (int)EngineHarness.Frames(1.0));
    }

    private static void Tag(string path, int track)
    {
        var tags = new ATL.Track(path) { Album = "Album", Title = Path.GetFileNameWithoutExtension(path), TrackNumber = track };
        Assert.True(tags.Save(), "nie zapisano znaczników");
    }

    private static Dictionary<string, object> Record(string path, double lufs)
    {
        var info = new FileInfo(path);
        return new Dictionary<string, object>
        {
            ["path"] = path,
            ["length"] = info.Length,
            ["modifiedTicks"] = info.LastWriteTimeUtc.Ticks,
            ["integratedLufs"] = lufs,
            ["samplePeak"] = 0.5,
        };
    }
}
