using Cewka.App.Radio;
using Cewka.Audio.Streaming;
using Xunit;
using static Cewka.App.Tests.Radio.RadioTestData;

namespace Cewka.App.Tests.Radio;

public class RadioCatalogTests
{
    private static RadioCatalog Small() => new(
    [
        Station("pl-lokalne", "Alfa Lokalne", "PL", "local", city: "Łódź", cityLatin: "Lodz"),
        Station("pl-ogolne-b", "Beta FM", "PL", "national"),
        Station("pl-ogolne-a", "Ącki Radio", "PL", "online"),
        Station("pl-regionalne", "Aaa Regionalne", "PL", "regional"),
        Station("cz-1", "Evropa 2", "CZ", categories: [RadioCategories.Rock, RadioCategories.Hits]),
        Station("ua-1", "Хіт FM", "UA", languages: ["uk"]),
        Station("ro-1", "Kiss FM", "RO"),
        Station("es-1", "Kiss FM", "ES"),
        Station("gb-1", "BBC Radio 1", "GB"),
        Station("us-1", "WNYC", "US"),
    ]);

    [Fact]
    public void WKrajuNajpierwOgolnokrajoweIInternetoweUGrupyAlfabetycznie()
    {
        var ids = Small().InCountry("PL").Select(s => s.Id);

        Assert.Equal(["pl-ogolne-a", "pl-ogolne-b", "pl-regionalne", "pl-lokalne"], ids);
    }

    /// <summary>W kraju najpierw stacje z logo, potem bez; w obu grupach zasięg i alfabet jak dotąd.</summary>
    [Fact]
    public void WKrajuNajpierwStacjeZLogoPotemBezWObuGrupachZasiegIAlfabet()
    {
        static RadioStation WithLogo(RadioStation station, string url)
        {
            station.Logos = [url];
            return station;
        }

        var catalog = new RadioCatalog(
        [
            Station("pl-lokalne", "Alfa Lokalne", "PL", "local"),
            WithLogo(Station("pl-lokalne-logo", "Zeta Lokalne", "PL", "local"), "https://zeta.example.com/logo.png"),
            Station("pl-ogolne", "Beta FM", "PL", "national"),
            WithLogo(Station("pl-ogolne-logo", "Gamma FM", "PL", "national"), "https://gamma.example.com/logo.png"),
            WithLogo(Station("pl-http", "Aaa FM", "PL", "national"), "http://aaa.example.com/logo.png"),
            Station("cz-1", "Evropa 2", "CZ"),
        ]);

        // Adres bez HTTPS nie jest pobierany, więc stacja z nim liczy się jako bez logo.
        Assert.Equal(["pl-ogolne-logo", "pl-lokalne-logo", "pl-http", "pl-ogolne", "pl-lokalne"], catalog.InCountry("PL").Select(s => s.Id));
        Assert.Equal(["cz-1", "pl-ogolne-logo", "pl-lokalne-logo", "pl-http", "pl-ogolne", "pl-lokalne"], catalog.List(null).Select(s => s.Id));
    }

    [Fact]
    public void ListaFiltrujeKategoriaIKrajem()
    {
        var catalog = Small();

        Assert.Equal(["cz-1"], catalog.List("CZ", RadioCategories.Rock).Select(s => s.Id));
        Assert.Equal(["cz-1"], catalog.List(null, RadioCategories.Rock).Select(s => s.Id));
        Assert.Equal(10, catalog.List(null).Count);
        Assert.Empty(catalog.List("XX"));
    }

    [Fact]
    public void KrajeKrajDomyslnyPierwszyPotemAlfabetycznieWgNazwBezDiakrytykow()
    {
        var names = new Dictionary<string, string>
        {
            ["PL"] = "Polska", ["CZ"] = "Česko", ["UA"] = "Україна", ["RO"] = "Rumunia", ["ES"] = "Hiszpania",
            ["GB"] = "Wielka Brytania", ["US"] = "Stany Zjednoczone",
        };

        var order = Small().Countries(c => names[c], "RO").Select(e => e.Code);

        Assert.Equal(["RO", "CZ", "ES", "PL", "US", "UA", "GB"], order);
    }

    [Fact]
    public void KrajeMajaLiczbyStacji()
    {
        var entry = Small().Countries(c => c, null).Single(e => e.Code == "PL");

        Assert.Equal(4, entry.Count);
    }

    [Fact]
    public void KategorieKrajuBezProguLiczebnosciWStalejKolejnosci()
    {
        var categories = Small().Categories("CZ");

        Assert.Equal([RadioCategories.Hits, RadioCategories.Rock], categories.Select(c => c.Key));
        Assert.All(categories, c => Assert.Equal(1, c.Count));
    }

    [Theory]
    [InlineData("pl", null, null, "PL")]
    [InlineData("en", null, null, "GB")]
    [InlineData("en", "US", null, "GB")] // bez stacji angielskich w USA w tym małym katalogu
    [InlineData("ru", "US", null, "GB")]
    [InlineData("uk", null, null, "UA")]
    [InlineData("pt", null, null, null)] // PT i BR bez stacji w katalogu
    [InlineData("pl", null, "CZ", "CZ")]
    [InlineData("pl", null, "XX", "PL")]
    [InlineData("en-GB", null, null, "GB")]
    public void KrajDomyslnyWgOstatniegoWyboruLokalizacjiIJezyka(string language, string? home, string? last, string? expected) =>
        Assert.Equal(expected, Small().DefaultCountry(language, home, last));

    [Fact]
    public void LokalizacjaDomowaWygrywaWObrebieJezykaGdyKrajMaStacjeWTymJezyku()
    {
        var stations = new List<RadioStation>();
        for (var i = 0; i < 3; i++)
        {
            stations.Add(Station($"us-{i}", $"US {i}", "US", languages: ["en"]));
            stations.Add(Station($"gb-{i}", $"GB {i}", "GB", languages: ["en"]));
        }

        var catalog = new RadioCatalog(stations);

        Assert.Equal("US", catalog.DefaultCountry("en", "US"));
        Assert.Equal("GB", catalog.DefaultCountry("en", "FR"));
        Assert.Equal("GB", catalog.DefaultCountry("ru", "US"));
    }

    [Fact]
    public void KrajWNazwieZawszeGdyStacjaMaKraj()
    {
        var catalog = Small();
        string Name(string code) => code == "RO" ? "Rumunia" : code == "ES" ? "Hiszpania" : code;

        Assert.Equal("Kiss FM (Rumunia)", catalog.DisplayName(catalog.Find("ro-1")!, Name));
        Assert.Equal("Kiss FM (Hiszpania)", catalog.DisplayName(catalog.Find("es-1")!, Name));
        Assert.Equal("Evropa 2 (CZ)", catalog.DisplayName(catalog.Find("cz-1")!, Name));

        var withoutCountry = catalog.Find("cz-1")!.Copy();
        withoutCountry.Country = string.Empty;
        Assert.Equal("Evropa 2", catalog.DisplayName(withoutCountry, Name));
    }

    [Fact]
    public void NazwyTeSameZRoznaCzestotliwosciaTezSaPowtorzone()
    {
        var catalog = new RadioCatalog([Station("a", "Radio Impuls 90.1", "CZ"), Station("b", "Radio Impuls", "RO")]);

        Assert.True(catalog.IsNameRepeated(catalog.Find("a")!));
    }

    [Fact]
    public void KolejnoscAdresowZalecanyPierwszyGdyKodekDostepny()
    {
        var station = Station("x", "X", "PL", streams:
        [
            Stream("https://a.example.com/mp3", "mp3"),
            Stream("https://b.example.com/aac", "aac"),
            Stream("https://c.example.com/hls", "aac", hls: true),
        ]);
        station.Recommended = 1;

        var withAac = station.PlayableStreams(_ => true).Select(s => s.Url);
        var onlyMp3 = station.PlayableStreams(c => c == LiveCodec.Mp3).Select(s => s.Url);

        Assert.Equal(["https://b.example.com/aac", "https://a.example.com/mp3", "https://c.example.com/hls"], withAac);
        Assert.Equal(["https://a.example.com/mp3"], onlyMp3);
    }

    [Fact]
    public void StacjaBezOdtwarzalnegoAdresuNieMaPoleceniaDlaSilnika()
    {
        var station = Station("x", "X", "PL", streams: [Stream("https://b.example.com/aac", "aac")]);

        Assert.False(station.IsPlayable(c => c == LiveCodec.Mp3));
        Assert.Null(station.ToLiveSource(c => c == LiveCodec.Mp3));
    }

    [Fact]
    public void PolecenieDlaSilnikaMaIdentyfikatorNazweKrajIAdresyWKolejnosci()
    {
        var station = Station("pl-x", "Stacja X", "PL", streams: [Stream("https://a.example.com/1"), Stream("https://b.example.com/2")]);

        var source = station.ToLiveSource(_ => true);

        Assert.NotNull(source);
        Assert.Equal("pl-x", source.StationId);
        Assert.Equal("Stacja X", source.Name);
        Assert.Equal("PL", source.CountryCode);
        Assert.False(source.AllowLocalAddress);
        Assert.Equal(["https://a.example.com/1", "https://b.example.com/2"], source.Urls.Select(u => u.ToString()));
    }

    [Fact]
    public void NieznanyKodekAdresuSluchaczaJestProbowanyZawsze()
    {
        var typed = RadioState.CreateCustom("Moje", "http://192.168.0.2:8000/live", null, typed: true);

        var source = typed.ToLiveSource(_ => false);

        Assert.NotNull(source);
        Assert.True(source.AllowLocalAddress);
        Assert.Null(source.CountryCode);
    }

    [Fact]
    public void DomyslnaPlatformaOdtwarzaMp3()
    {
        Assert.True(LiveSupport.IsPlayable(LiveCodec.Mp3));
        Assert.False(LiveSupport.IsPlayable(LiveCodec.Unknown));
    }

    [Fact]
    public void WczytanieZeStrumieniaOdtwarzaZapisanyPlik()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"schema":1,"stations":[{"id":"a","name":"A","country":"PL","categories":["hits"],"languages":["pl"],"streams":[{"url":"https://a.example.com/x","codec":"mp3","kbps":128}],"logos":[]}]}"""));

        var catalog = RadioCatalog.Load(stream);

        var station = Assert.Single(catalog.Stations);
        Assert.Equal("A", station.Name);
        Assert.Equal("mp3", station.Streams[0].Codec);
        Assert.Equal(LiveCodec.Mp3, station.Streams[0].LiveCodec);
        Assert.Equal(StationOrigin.Catalog, station.Origin);
    }
}
