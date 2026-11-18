using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Plik, który się otwiera, ale nie daje dźwięku (sam nagłówek, urwane pobieranie) albo psuje się
/// przy pierwszym odczycie, przy powtarzaniu nie może zapętlić wątku dekodującego: ten otwierałby
/// go w kółko bez przerwy, zajmując rdzeń procesora i zasypując interfejs zgłoszeniami.
/// </summary>
public sealed class EmptyTrackLoopTests
{
    [Theory]
    [InlineData(RepeatMode.Queue)]
    [InlineData(RepeatMode.Track)]
    public void PustyPlikPrzyPowtarzaniuNieJestOtwieranyWKolko(RepeatMode repeat)
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = repeat;
        h.Engine.SetQueue([h.Track("pusty", 0, 0.1f)]);

        Thread.Sleep(400);
        h.PumpPeriods(20);
        Thread.Sleep(400);

        Assert.True(h.Opened.Count <= 3, $"plik otwarto {h.Opened.Count} razy");
        Assert.True(h.TrackChanges.Count <= 3, $"zgłoszeń zmiany utworu: {h.TrackChanges.Count}");
    }

    [Fact]
    public void PlikPsujacySiePrzyPierwszymOdczycieNieJestOtwieranyWKolkoPrzyPowtarzaniuKolejki()
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue([h.Track("zepsuty", 3, 0.1f,
            _ => new SyntheticDecoder(EngineHarness.Frames(3), 0.1f, EngineHarness.Channels, EngineHarness.Rate) { ThrowAtFrame = 0 })]);

        Thread.Sleep(400);
        h.PumpPeriods(20);
        Thread.Sleep(400);

        Assert.True(h.Opened.Count <= 3, $"plik otwarto {h.Opened.Count} razy");
        Assert.True(h.Failures.Count <= 3, $"zgłoszeń porażki: {h.Failures.Count}");
    }

    /// <summary>Koło samych pustych plików kończy kolejkę jak zwykły koniec, a nie zostawia stanu grania bez dźwięku.</summary>
    [Fact]
    public void KoloPustychPlikowKonczyKolejke()
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue([h.Track("pusty1", 0, 0.1f), h.Track("pusty2", 0, 0.2f)]);

        h.PumpUntil(() => h.Finished > 0);

        Assert.Equal(PlaybackState.Paused, h.Engine.State);
        Assert.True(h.Opened.Count <= 4, $"pliki otwarto {h.Opened.Count} razy");
    }

    /// <summary>Pusty plik między grającymi jest pomijany, a powtarzanie kolejki działa dalej.</summary>
    [Fact]
    public void PustyPlikMiedzyGrajacymiNiePrzerywaPowtarzaniaKolejki()
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue([h.Track("gra", 0.3, 0.1f), h.Track("pusty", 0, 0.2f)]);

        // Trzy pełne koła: grający plik otwiera się za każdym razem od nowa.
        h.PumpUntil(() => h.Opened.Count(name => name == "gra.wav") >= 4);

        Assert.Equal(0, h.Finished);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    /// <summary>
    /// Utwór przewinięty pod sam koniec nie daje już dźwięku, ale pusty nie jest: powtarzanie utworu
    /// zaczyna go od początku.
    /// </summary>
    [Fact]
    public void PrzewiniecieNaKoniecPrzyPowtarzaniuUtworuZaczynaGoOdNowa()
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = RepeatMode.Track;
        h.Engine.SetQueue([h.Track("a", 2, 0.1f)]);
        h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.Position.TotalSeconds > 0.1);

        h.Engine.SeekTo(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => !h.Engine.IsSeeking);
        h.PumpUntil(() => h.Opened.Count >= 2 && h.Engine.Position.TotalSeconds is > 0.2 and < 1.5);

        Assert.Equal(0, h.Finished);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    /// <summary>
    /// Pusty plik obok pliku, którego nie da się otworzyć: do pustego dochodzi się wtedy po nieudanym
    /// otwarciu sąsiada, więc koło musi zamknąć się także na tej drodze, a nie tylko po końcu utworu.
    /// </summary>
    [Fact]
    public void PustyPlikObokNieotwieralnegoNieKreciSieWKolkoPrzyPowtarzaniuKolejki()
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = RepeatMode.Queue;
        var missing = Path.Combine(Path.GetTempPath(), "cewka-brak-" + Guid.NewGuid().ToString("N") + ".wav");
        h.Engine.SetQueue([h.Track("pusty", 0, 0.1f), missing]);

        h.PumpUntil(() => h.Finished > 0);
        Thread.Sleep(300);

        Assert.Equal(PlaybackState.Paused, h.Engine.State);
        Assert.True(h.Opened.Count <= 4, $"pliki otwarto {h.Opened.Count} razy");
        Assert.True(h.Failures.Count <= 3, $"zgłoszeń porażki: {h.Failures.Count}");
    }

    /// <summary>
    /// W kolejności losowej pusty wpis wraca na początku następnej rundy, zanim przyjdzie kolej na
    /// grający. Kolejka z choć jednym grającym utworem nie może się wtedy skończyć.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1234)]
    [InlineData(99)]
    public void LosowaKolejkaZGrajacymUtworemNieKonczySieNaPustych(int seed)
    {
        using var h = new EngineHarness(seed: seed);
        h.Engine.Shuffle = true;
        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue([h.Track("gra", 0.2, 0.1f), h.Track("pusty1", 0, 0.2f), h.Track("pusty2", 0, 0.3f)]);

        // Sześć rund: przy każdym ziarnie któraś zaczyna się od pustego wpisu.
        h.PumpUntil(() => h.Opened.Count(name => name == "gra.wav") >= 7);

        Assert.Equal(0, h.Finished);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    /// <summary>Powtarzanie pustego utworu kończy odtwarzanie także wtedy, gdy w kolejce są inne, grające.</summary>
    [Fact]
    public void PowtarzaniePustegoUtworuKonczyOdtwarzanieChocKolejkaMaInne()
    {
        using var h = new EngineHarness();
        h.Engine.Repeat = RepeatMode.Track;
        h.Engine.SetQueue([h.Track("pusty", 0, 0.1f), h.Track("gra", 1, 0.2f)]);

        h.PumpUntil(() => h.Finished > 0);
        Thread.Sleep(200);

        Assert.True(h.Opened.Count <= 3, $"pliki otwarto {h.Opened.Count} razy");
        Assert.DoesNotContain("gra.wav", h.Opened);
    }
}
