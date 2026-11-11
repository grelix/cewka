using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Migawka kolejności odtwarzania: tylko odczyt, zgodna z faktycznym przejściem.</summary>
public class PlaybackOrderTests
{
    private static string[] Tracks(EngineHarness h, int count, double seconds = 10) =>
        Enumerable.Range(0, count).Select(i => h.Track("o" + i, seconds, 0.05f * (i + 1))).ToArray();

    private static EngineHarness Started(int count, int start = 0, int seed = 7, bool shuffle = false,
        RepeatMode repeat = RepeatMode.None)
    {
        var h = new EngineHarness(seed: seed);
        h.Engine.Shuffle = shuffle;
        h.Engine.Repeat = repeat;
        h.Engine.SetQueue(Tracks(h, count), start);
        h.PumpUntil(() => h.Engine.CurrentIndex >= 0);
        return h;
    }

    /// <summary>Numer w kolejce wpisu, który po bieżącym pokazuje migawka; −1, gdy nic po nim nie ma.</summary>
    private static int FollowingInQueue(PlaybackEngine engine)
    {
        var snapshot = engine.PlaybackOrder;
        var next = snapshot.CurrentPosition + 1;
        return next < snapshot.Entries.Count ? engine.Queue.ToList().IndexOf(snapshot.Entries[next]) : -1;
    }

    private static void AssertPermutation(PlaybackEngine engine, PlaybackOrderSnapshot snapshot)
    {
        Assert.Equal(engine.Queue.Count, snapshot.Entries.Count);
        Assert.Equal(snapshot.Entries.Count, snapshot.Entries.Distinct().Count());
        Assert.Empty(snapshot.Entries.Except(engine.Queue));
    }

    /// <summary>Do następnego w kolejności, a przy losowaniu tak długo, aż bieżący stanie na końcu rundy.</summary>
    private static void ToEndOfRound(EngineHarness h)
    {
        for (var i = 0; i < 100; i++)
        {
            var snapshot = h.Engine.PlaybackOrder;
            if (snapshot.CurrentPosition == snapshot.Entries.Count - 1) return;

            var target = h.Engine.Queue.ToList().IndexOf(snapshot.Entries[snapshot.CurrentPosition + 1]);
            h.Engine.PlayIndex(target);
            h.PumpUntil(() => h.Engine.CurrentIndex == target);
        }

        Assert.Fail("Bieżący wpis nie doszedł do końca rundy.");
    }

    // ---------- Bez losowania ----------

    [Fact]
    public void BezLosowaniaToKolejnoscListy()
    {
        using var h = Started(4, start: 2);

        var snapshot = h.Engine.PlaybackOrder;

        Assert.Equal(h.Engine.Queue, snapshot.Entries);
        Assert.Equal(2, snapshot.CurrentPosition);
        Assert.Equal(h.Engine.NextIndex, FollowingInQueue(h.Engine));
    }

    [Fact]
    public void PrzestawienieWKolejceWidacBezLosowania()
    {
        using var h = Started(4);
        var version = h.Engine.PlaybackOrderVersion;

        h.Engine.Move(3, 1);

        Assert.Equal(h.Engine.Queue, h.Engine.PlaybackOrder.Entries);
        Assert.True(h.Engine.PlaybackOrderVersion > version);
    }

    [Fact]
    public void PustaKolejkaDajePustaMigawke()
    {
        using var h = new EngineHarness();

        var snapshot = h.Engine.PlaybackOrder;

        Assert.Empty(snapshot.Entries);
        Assert.Equal(-1, snapshot.CurrentPosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JedenWpisStoiNaPierwszymMiejscuBezNastepnego(bool shuffle)
    {
        using var h = Started(1, shuffle: shuffle);

        var snapshot = h.Engine.PlaybackOrder;

        Assert.Single(snapshot.Entries);
        Assert.Equal(0, snapshot.CurrentPosition);
        Assert.Equal(-1, h.Engine.NextIndex);
    }

    [Fact]
    public void ZrodloNaZywoNieJestBiezacymWpisemKolejki()
    {
        using var rig = new LiveRig();
        rig.Engine.SetQueue(Tracks(rig.Harness, 2));
        rig.Engine.PlayLive(LiveRig.Station());

        Assert.Equal(-1, rig.Engine.PlaybackOrder.CurrentPosition);
    }

    // ---------- Losowanie ----------

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(23)]
    public void PrzyLosowaniuNastepnyWMigawceToFaktyczneNastepnePrzejscie(int seed)
    {
        using var h = Started(6, start: 3, seed: seed, shuffle: true);

        // Bez powtarzania ostatni z rundy nie ma następnego; nic nie zawija, więc wyjątku końca rundy tu nie ma.
        for (var step = 0; step < 6; step++)
        {
            var snapshot = h.Engine.PlaybackOrder;
            AssertPermutation(h.Engine, snapshot);

            var predicted = h.Engine.NextIndex;
            Assert.Equal(predicted, FollowingInQueue(h.Engine));
            if (predicted < 0) return;

            h.Engine.Next();
            h.PumpUntil(() => h.Engine.CurrentIndex == predicted);
            Assert.Equal(snapshot.CurrentPosition + 1, h.Engine.PlaybackOrder.CurrentPosition);
        }
    }

    [Fact]
    public void OdczytNieLosujeINieZmieniaKolejnosci()
    {
        using var reading = Started(8, seed: 11, shuffle: true, repeat: RepeatMode.Queue);
        using var quiet = Started(8, seed: 11, shuffle: true, repeat: RepeatMode.Queue);

        var version = reading.Engine.PlaybackOrderVersion;
        var first = reading.Engine.PlaybackOrder.Entries.ToArray();

        for (var i = 0; i < 50; i++) Assert.Equal(first, reading.Engine.PlaybackOrder.Entries);

        Assert.Equal(version, reading.Engine.PlaybackOrderVersion);

        // Generator losowości silnika stoi w miejscu: wyłączenie i włączenie losowania układa to samo.
        reading.Engine.Shuffle = false;
        reading.Engine.Shuffle = true;
        quiet.Engine.Shuffle = false;
        quiet.Engine.Shuffle = true;

        Assert.Equal(
            quiet.Engine.PlaybackOrder.Entries.Select(entry => Path.GetFileName(entry.Path)),
            reading.Engine.PlaybackOrder.Entries.Select(entry => Path.GetFileName(entry.Path)));
    }

    [Fact]
    public void MigawkaJestWlasnaKopiaNiezaleznaOdPozniejszychZmian()
    {
        using var h = Started(4, shuffle: true);

        var snapshot = h.Engine.PlaybackOrder;
        var copy = snapshot.Entries.ToArray();

        h.Engine.RemoveAt(h.Engine.Queue.ToList().IndexOf(copy[^1]));
        h.Engine.Shuffle = false;

        Assert.Equal(copy, snapshot.Entries);
        Assert.Equal(3, h.Engine.PlaybackOrder.Entries.Count);
    }

    [Fact]
    public void WersjaRosnieZKazdaZmianaKolejnosciIKolejki()
    {
        using var h = Started(5, shuffle: true);
        var version = h.Engine.PlaybackOrderVersion;

        void Changed()
        {
            Assert.True(h.Engine.PlaybackOrderVersion > version);
            version = h.Engine.PlaybackOrderVersion;
        }

        h.Engine.Enqueue([h.Track("dodany", 10, 0.2f)]);
        Changed();
        h.Engine.Move(0, 1);
        Changed();
        h.Engine.RemoveAt(h.Engine.Queue.Count - 1);
        Changed();
        h.Engine.Shuffle = false;
        Changed();
        h.Engine.Repeat = RepeatMode.Queue;
        Changed();
        h.Engine.ClearQueue();
        Changed();
    }

    // ---------- Koniec rundy przy powtarzaniu z losowaniem ----------

    [Fact]
    public void KoniecRundyPokazujeBiezacaKolejnoscBezUstalaniaNastepnej()
    {
        using var h = Started(5, seed: 3, shuffle: true, repeat: RepeatMode.Queue);
        ToEndOfRound(h);

        var version = h.Engine.PlaybackOrderVersion;
        var snapshot = h.Engine.PlaybackOrder;

        Assert.Equal(snapshot.Entries.Count - 1, snapshot.CurrentPosition);
        Assert.Equal(snapshot.Entries, h.Engine.PlaybackOrder.Entries);
        Assert.Equal(version, h.Engine.PlaybackOrderVersion);
    }

    [Fact]
    public void OdczytNastepnegoNaKoncuRundyNiczegoNiePrzesuwaAPrzejscieSieZgadza()
    {
        using var h = Started(5, seed: 3, shuffle: true, repeat: RepeatMode.Queue);
        ToEndOfRound(h);

        var version = h.Engine.PlaybackOrderVersion;
        var order = h.Engine.PlaybackOrder.Entries;
        var predicted = h.Engine.NextIndex;

        Assert.Equal(version, h.Engine.PlaybackOrderVersion);
        Assert.Equal(order, h.Engine.PlaybackOrder.Entries);
        Assert.Equal(predicted, h.Engine.NextIndex);

        h.Engine.Next();
        h.PumpUntil(() => h.Engine.CurrentIndex == predicted);
        Assert.Equal(0, h.Engine.PlaybackOrder.CurrentPosition);
    }

    [Fact]
    public void WylaczeniePowtarzaniaPoUstaleniuRundyKonczyKolejnoscNaBiezacym()
    {
        using var h = Started(5, seed: 3, shuffle: true, repeat: RepeatMode.Queue);
        ToEndOfRound(h);
        Assert.True(h.Engine.NextIndex >= 0);

        h.Engine.Repeat = RepeatMode.None;

        var snapshot = h.Engine.PlaybackOrder;
        AssertPermutation(h.Engine, snapshot);
        Assert.Equal(snapshot.Entries.Count - 1, snapshot.CurrentPosition);
        Assert.Equal(-1, h.Engine.NextIndex);
    }

    // ---------- Współbieżność ----------

    [Fact]
    public async Task OdczytRownoleglyZPrzejsciamiIZmianamiKolejkiJestSpojny()
    {
        using var h = Started(30, seed: 5, shuffle: true, repeat: RepeatMode.Queue);
        using var stop = new CancellationTokenSource();
        var reads = 0;
        Exception? failure = null;

        var readers = Enumerable.Range(0, 3).Select(reader => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var snapshot = h.Engine.PlaybackOrder;
                    Assert.Equal(snapshot.Entries.Count, snapshot.Entries.Distinct().Count());
                    Assert.InRange(snapshot.CurrentPosition, -1, snapshot.Entries.Count - 1);
                    _ = h.Engine.PlaybackOrderVersion;
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failure, ex);
            }
        })).ToArray();

        var extra = h.Track("rownolegly", 10, 0.3f);

        for (var i = 0; i < 200 && Volatile.Read(ref failure) is null; i++)
        {
            switch (i % 5)
            {
                case 0: h.Engine.Next(); break;
                case 1: h.Engine.Enqueue([extra]); break;
                case 2: h.Engine.Move(0, h.Engine.Queue.Count - 1); break;
                case 3: h.Engine.RemoveAt(h.Engine.Queue.Count - 1); break;
                case 4: _ = h.Engine.NextIndex; break;
            }

            h.PumpPeriods(1);
        }

        // Pętla bywa krótsza niż start wątków z puli: każdy odczyt ma się odbyć, zanim skończy się test.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) > 0, 10000));

        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Null(Volatile.Read(ref failure));
        Assert.True(Volatile.Read(ref reads) > 0);
        AssertPermutation(h.Engine, h.Engine.PlaybackOrder);
    }
}
