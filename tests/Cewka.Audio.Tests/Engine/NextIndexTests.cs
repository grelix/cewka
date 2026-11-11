using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Odczyt numeru następnego wpisu: ta sama logika co faktyczne przejście.</summary>
public class NextIndexTests
{
    private static string[] Tracks(EngineHarness h, int count) =>
        Enumerable.Range(0, count).Select(i => h.Track("n" + i, 10, 0.1f * (i + 1))).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OdczytNaKoncuRundyZBlednymiWpisamiNieZmieniaKolejnosci(int seed)
    {
        using var h = new EngineHarness(seed: seed);
        h.Engine.Shuffle = true;
        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue(Tracks(h, 6), startIndex: 0);

        // Stan silnika, a nie zgłoszenie zmiany utworu: zgłoszenie idzie przez pulę wątków, a pompowanie
        // w tym czasie przesuwało odtwarzanie dalej, aż bieżący wpis bywał ostatnim w rundzie.
        h.PumpUntil(() => h.Engine.CurrentIndex == 0 && h.Engine.PlaybackOrder.CurrentPosition >= 0);

        // Wszystko za bieżącym wpisem jest błędne, więc następny pochodzi już z kolejnej rundy.
        var before = h.Engine.PlaybackOrder;
        var position = before.CurrentPosition;
        Assert.True(position >= 0 && position + 2 < before.Entries.Count,
            $"bieżący wpis na miejscu {position} z {before.Entries.Count}, numer {h.Engine.CurrentIndex}");
        foreach (var entry in before.Entries.Skip(position + 2)) entry.OpenFailure = TrackFailureReason.DamagedFile;
        var current = before.Entries[position + 1];
        h.Engine.PlayIndex(h.Engine.Queue.ToList().IndexOf(current));
        h.PumpUntil(() => h.Engine.CurrentIndex == h.Engine.Queue.ToList().IndexOf(current));

        var order = h.Engine.PlaybackOrder.Entries;
        var version = h.Engine.PlaybackOrderVersion;
        var predicted = h.Engine.NextIndex;

        for (var i = 0; i < 5; i++) Assert.Equal(predicted, h.Engine.NextIndex);
        Assert.Equal(version, h.Engine.PlaybackOrderVersion);
        Assert.Equal(order, h.Engine.PlaybackOrder.Entries);
    }

    [Fact]
    public void KolejnoscZwyklaDajeNastepnyWpis()
    {
        using var h = new EngineHarness();
        h.Engine.SetQueue(Tracks(h, 3), startIndex: 1);

        // Zgłoszenie zmiany utworu idzie przez pulę wątków: pod obciążeniem pompowanie, szybsze od czasu
        // rzeczywistego, przegrałoby cały utwór, zanim dotrze. Warunek sprawdzany co okres tego nie przegapi.
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.NextIndex == 2);
    }

    [Fact]
    public void NaOstatnimWpisieBezPowtarzaniaNiemaNastepnego()
    {
        using var h = new EngineHarness();
        h.Engine.SetQueue(Tracks(h, 3), startIndex: 2);
        h.PumpUntil(() => h.TrackChanges.Contains(2));

        Assert.Equal(-1, h.Engine.NextIndex);
    }

    [Fact]
    public void PowtarzanieKolejkiNaOstatnimDajePierwszy()
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue(Tracks(h, 3), startIndex: 2);
        h.PumpUntil(() => h.TrackChanges.Contains(2));

        h.PumpUntil(() => h.Engine.NextIndex == 0);
    }

    [Fact]
    public void PowtarzanieUtworuIJedynegoWpisuNieMaNastepnego()
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = RepeatMode.Track;
        h.Engine.SetQueue(Tracks(h, 3), startIndex: 0);
        h.PumpUntil(() => h.TrackChanges.Contains(0));
        Assert.Equal(-1, h.Engine.NextIndex);

        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue(Tracks(h, 1));
        h.PumpUntil(() => h.TrackChanges.Count >= 2);
        Assert.Equal(-1, h.Engine.NextIndex);
    }

    [Fact]
    public void PrzyLosowaniuOdczytZgadzaSieZFaktycznymPrzejsciem()
    {
        using var h = new EngineHarness(seed: 7);
        h.Engine.Shuffle = true;
        h.Engine.SetQueue(Tracks(h, 6), startIndex: 3);
        h.PumpUntil(() => h.Engine.CurrentIndex == 3 && h.Engine.NextIndex >= 0);

        var predicted = h.Engine.NextIndex;
        Assert.InRange(predicted, 0, 5);
        Assert.NotEqual(3, predicted);
        Assert.Equal(predicted, h.Engine.NextIndex);

        h.Engine.Next();
        h.PumpUntil(() => h.Engine.CurrentIndex != 3);
        Assert.Equal(predicted, h.Engine.CurrentIndex);
    }

    [Fact]
    public void ZrodloNaZywoNieMaNastepnego()
    {
        using var rig = new LiveRig();
        rig.Engine.Repeat = RepeatMode.Queue;
        rig.Engine.PlayLive(LiveRig.Station());

        Assert.Equal(-1, rig.Engine.NextIndex);
    }

    [Fact]
    public void BezOdtwarzaniaNiemaNastepnego()
    {
        using var h = new EngineHarness();

        Assert.Equal(-1, h.Engine.NextIndex);
    }
}
