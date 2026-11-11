using Cewka.App.Radio;
using Xunit;
using static Cewka.App.Tests.Radio.RadioTestData;

namespace Cewka.App.Tests.Radio;

public class RadioStateTests
{
    private static RadioStation S(string id) => Station(id, "Stacja " + id, "PL");

    [Fact]
    public void UlubioneDodajeNaKoniecBezPowtorzen()
    {
        var state = new RadioState();

        Assert.True(state.AddFavourite(S("a")));
        Assert.True(state.AddFavourite(S("b")));
        Assert.False(state.AddFavourite(S("a")));

        Assert.Equal(["a", "b"], state.Favourites.Select(f => f.Id));
        Assert.True(state.IsFavourite("a"));
        Assert.False(state.IsFavourite("z"));
    }

    [Fact]
    public void UlubioneZapisujePelnaKopiePrzezywajacaZmianeOryginalu()
    {
        var original = Station("a", "Stara nazwa", "PL");
        var state = new RadioState();
        state.AddFavourite(original);

        original.Name = "Zmieniona";
        original.Streams[0].Url = "https://zmieniony.example.com/";

        Assert.Equal("Stara nazwa", state.Favourites[0].Name);
        Assert.Equal("https://a.example.com/live", state.Favourites[0].Streams[0].Url);
    }

    [Fact]
    public void UsuniecieZwracaMiejsceIKopieDoCofniecia()
    {
        var state = new RadioState();
        foreach (var id in new[] { "a", "b", "c" })
            state.AddFavourite(S(id));

        var removed = state.RemoveFavourite("b");

        Assert.NotNull(removed);
        Assert.Equal(1, removed.Value.Index);
        Assert.Equal(["a", "c"], state.Favourites.Select(f => f.Id));

        Assert.True(state.RestoreFavourite(removed.Value.Index, removed.Value.Station));
        Assert.Equal(["a", "b", "c"], state.Favourites.Select(f => f.Id));
    }

    [Fact]
    public void UsuniecieNieobecnejStacjiNicNieRobi()
    {
        var state = new RadioState();
        state.AddFavourite(S("a"));

        Assert.Null(state.RemoveFavourite("z"));
        Assert.Single(state.Favourites);
    }

    [Fact]
    public void CofnieciePoSkroceniuListyWstawiaNaKoniecAIstniejacejNieDubluje()
    {
        var state = new RadioState();
        state.AddFavourite(S("a"));

        Assert.True(state.RestoreFavourite(5, S("b")));
        Assert.Equal(["a", "b"], state.Favourites.Select(f => f.Id));
        Assert.False(state.RestoreFavourite(0, S("a")));
    }

    [Fact]
    public void RecznaKolejnoscPrzesuwaStacje()
    {
        var state = new RadioState();
        foreach (var id in new[] { "a", "b", "c", "d" })
            state.AddFavourite(S(id));

        Assert.True(state.MoveFavourite(0, 2));
        Assert.Equal(["b", "c", "a", "d"], state.Favourites.Select(f => f.Id));

        Assert.True(state.MoveFavourite(3, 0));
        Assert.Equal(["d", "b", "c", "a"], state.Favourites.Select(f => f.Id));

        Assert.False(state.MoveFavourite(0, 4));
        Assert.False(state.MoveFavourite(-1, 0));
    }

    [Fact]
    public void OstatnioSluchaneMajaDziesiecPozycjiOdNajnowszejBezPowtorzen()
    {
        var state = new RadioState();
        for (var i = 0; i < 12; i++)
            state.PushRecent(S("s" + i));

        Assert.Equal(10, state.Recent.Count);
        Assert.Equal("s11", state.Recent[0].Id);
        Assert.Equal("s2", state.Recent[^1].Id);
        Assert.Equal("s11", state.LastStationId);

        state.PushRecent(S("s5"));

        Assert.Equal(10, state.Recent.Count);
        Assert.Equal(["s5", "s11"], state.Recent.Take(2).Select(r => r.Id));
        Assert.Single(state.Recent, r => r.Id == "s5");
        Assert.Equal("s5", state.LastStationId);
    }

    [Fact]
    public void CzyszczenieOstatnioSluchanychNieRuszaUlubionychAniOstatniejStacji()
    {
        var state = new RadioState();
        state.AddFavourite(S("a"));
        state.PushRecent(S("a"));

        state.ClearRecent();

        Assert.Empty(state.Recent);
        Assert.Single(state.Favourites);
        Assert.Equal("a", state.LastStationId);
    }

    [Fact]
    public void FindSzukaWUlubionychIOstatnich()
    {
        var state = new RadioState();
        state.AddFavourite(S("a"));
        state.PushRecent(S("b"));

        Assert.Equal("a", state.Find("a")?.Id);
        Assert.Equal("b", state.Find("b")?.Id);
        Assert.Null(state.Find("c"));
    }

    [Fact]
    public void AdresWpisanyReczniePozwalaNaSiecLokalnaAZaimportowanyNie()
    {
        var typed = RadioState.CreateCustom("Domowe", "http://192.168.1.10:8000/live", "pl", typed: true, logoUrl: "https://example.com/l.png");
        var imported = RadioState.CreateCustom("Z pliku", "http://192.168.1.10:8000/live", null, typed: false);

        Assert.True(typed.AllowLocalAddress);
        Assert.Equal(StationOrigin.Typed, typed.Origin);
        Assert.Equal("PL", typed.Country);
        Assert.Equal(["https://example.com/l.png"], typed.Logos);
        Assert.False(imported.AllowLocalAddress);
        Assert.Equal(StationOrigin.Imported, imported.Origin);
        Assert.True(typed.IsCustom && imported.IsCustom);
        Assert.Equal(typed.Id, imported.Id);
    }

    [Fact]
    public void StacjaBezNazwyDostajeAdresJakoNazwe()
    {
        Assert.Equal("http://x.example.com/live", RadioState.CreateCustom("  ", " http://x.example.com/live ", null, typed: true).Name);
    }

    [Fact]
    public void WpisanyAdresZListyBlokadJestOdrzucony()
    {
        var (station, verdict) = RadioState.TryCreateTyped("Rosyjska", "https://radio.example.ru/live");

        Assert.Null(station);
        Assert.Equal(AddressVerdict.Blocked, verdict);
    }

    [Fact]
    public void WpisanyAdresLokalnyJestDopuszczonyZFlagaAllowLocal()
    {
        var (station, verdict) = RadioState.TryCreateTyped("Domowe", "http://192.168.1.10:8000/live");

        Assert.Equal(AddressVerdict.Allowed, verdict);
        Assert.True(station!.ToLiveSource()!.AllowLocalAddress);
    }

    [Fact]
    public void WpisanaStacjaONazwieZListyBlokadJestOdrzucona()
    {
        var (station, verdict) = RadioState.TryCreateTyped("RT Deutsch", "https://stream.example.com/live");

        Assert.Null(station);
        Assert.Equal(AddressVerdict.Blocked, verdict);
    }

    [Fact]
    public void IdentyfikatorStacjiZAdresuJestStalyDlaTegoSamegoAdresu()
    {
        Assert.Equal(RadioState.CustomId("http://a/"), RadioState.CustomId(" http://a/ "));
        Assert.NotEqual(RadioState.CustomId("http://a/"), RadioState.CustomId("http://b/"));
        Assert.StartsWith("user-", RadioState.CustomId("http://a/"));
    }
}
