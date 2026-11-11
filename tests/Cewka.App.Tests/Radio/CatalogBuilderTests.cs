using System.Text.Json;
using System.Text.Json.Nodes;
using Cewka.App.Radio;
using Cewka.Tools.StationList;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Generator listy: reguły odpadania stacji i adresów, kolejność adresów, pola wynikowe.</summary>
public class CatalogBuilderTests
{
    private static JsonObject Stream(string url, string origin = "oficjalny", string codec = "MP3", int bitrate = 128,
        string protocol = "icecast", string? container = null, string result = "ok") => new()
    {
        ["url"] = url, ["pochodzenie"] = origin, ["kodek"] = codec, ["bitrate"] = bitrate, ["protokol"] = protocol,
        ["kontener_hls"] = container, ["wynik"] = result, ["icy_metadane"] = true,
    };

    private static JsonObject Station(string id, params JsonObject[] streams) => new()
    {
        ["id"] = id,
        ["nazwa"] = "Stacja " + id,
        ["popularnosc"] = "tajne udziały 12 %",
        ["typ"] = "commercial",
        ["zasieg"] = "national",
        ["region"] = "Mazowsze",
        ["miasto"] = new JsonObject { ["nazwa"] = "Łódź", ["lacinska"] = "Lodz" },
        ["format"] = new JsonArray("hits", "country"),
        ["kategorie"] = new JsonArray("Przeboje"),
        ["jezyki"] = new JsonArray("pl"),
        ["strona"] = "https://example.com",
        ["strumienie"] = new JsonArray(streams.Select(s => (JsonNode)s.DeepClone()).ToArray()),
        ["zalecany_strumien"] = 0,
        ["logo"] = new JsonArray(
            new JsonObject { ["url"] = "https://example.com/a.svg", ["format"] = "svg", ["wynik"] = "ok" },
            new JsonObject { ["url"] = "https://example.com/b.png", ["format"] = "png", ["wynik"] = "ok" }),
        ["zalecane_logo"] = 1,
        ["logo_wymaga_zgody"] = false,
        ["sankcje"] = new JsonObject { ["wynik"] = "brak trafień" },
        ["ocena_prawna"] = new JsonObject { ["grupa"] = "II", ["wniosek"] = "dopuszczona" },
    };

    private static BuildResult Build(string country, params JsonObject[] stations) =>
        CatalogBuilder.Build([new JsonObject { ["kraj"] = country, ["stacje"] = new JsonArray(stations.Select(s => (JsonNode)s).ToArray()) }.ToJsonString()]);

    private static JsonObject Good(string id = "pl-a") => Station(id, Stream("https://a.example.com/live"));

    [Fact]
    public void PoprawnaStacjaPrzechodziZPolamiProgramu()
    {
        var result = Build("PL", Good());

        var station = Assert.Single(result.Stations);
        Assert.Equal("pl-a", station.Id);
        Assert.Equal("PL", station.Country);
        Assert.Equal("Łódź", station.City);
        Assert.Equal("Lodz", station.CityLatin);
        Assert.Equal("Mazowsze", station.Region);
        Assert.Equal(["hits"], station.Categories);
        Assert.Equal(["pl"], station.Languages);
        Assert.True(station.Titles);
        Assert.Equal(["https://example.com/b.png"], station.Logos);
        Assert.Equal(0, result.Read - result.Stations.Count);
    }

    [Fact]
    public void WynikNieZawieraPopularnosciUwagPrawnychPochodzeniaIGrupy()
    {
        var json = JsonSerializer.Serialize(Build("PL", Good()).Stations[0], RadioJsonContext.Default.RadioStation);
        foreach (var forbidden in new[] { "popularnosc", "tajne", "ocena_prawna", "pochodzenie", "grupa", "sankcje" })
            Assert.DoesNotContain(forbidden, json);
    }

    [Fact]
    public void RegionPrzechodziTylkoDlaPolski()
    {
        Assert.Null(Build("CZ", Good()).Stations[0].Region);
    }

    [Theory]
    [InlineData("RU")]
    [InlineData("by")]
    public void KrajRosjiIBialorusiOdpada(string country)
    {
        var result = Build(country, Good());
        Assert.Empty(result.Stations);
        Assert.Equal(1, result.DroppedStations["kraj RU lub BY"]);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("rus")]
    [InlineData("Russian")]
    [InlineData("русский")]
    public void StacjaZRosyjskimWsrodJezykowOdpadaTakzeWielojezyczna(string language)
    {
        var station = Good();
        station["jezyki"] = new JsonArray("pl", language);

        var result = Build("PL", station);

        Assert.Empty(result.Stations);
        Assert.Equal(1, result.DroppedStations["język rosyjski"]);
    }

    [Fact]
    public void TrafienieNaListeSankcjiIBrakDopuszczeniaOdrzucajaStacje()
    {
        var sanctioned = Good("pl-s");
        sanctioned["sankcje"] = new JsonObject { ["wynik"] = "trafienie" };
        var forbidden = Good("pl-z");
        forbidden["ocena_prawna"] = new JsonObject { ["wniosek"] = "wykluczona" };

        var result = Build("PL", sanctioned, forbidden, Good("pl-ok"));

        Assert.Equal(["pl-ok"], result.Stations.Select(s => s.Id));
        Assert.Equal(2, result.DroppedStations.Values.Sum());
    }

    [Fact]
    public void NazwaZListyBlokadOdpada()
    {
        var station = Good();
        station["nazwa"] = "Radyo Sputnik";
        Assert.Empty(Build("TR", station).Stations);
    }

    [Theory]
    [InlineData("https://stream.example.ru/live")]
    [InlineData("https://radio.example.by/live")]
    [InlineData("http://radio.example.su/live")]
    [InlineData("https://x.example.com/live?aw_0_1st.skey=1")]
    [InlineData("https://x.example.com/live?listenerid=abc")]
    [InlineData("https://x.example.com/tunein/live")]
    [InlineData("ftp://x.example.com/live")]
    public void AdresZakazanyJestOdrzucanyIBezAdresuStacjaOdpada(string url)
    {
        var result = Build("PL", Station("pl-a", Stream(url)));

        Assert.Empty(result.Stations);
        Assert.Equal(1, result.DroppedAddresses.Values.Sum());
        Assert.Equal(1, result.DroppedStations["brak dopuszczonego adresu"]);
    }

    [Fact]
    public void AdresZakazanyOdpadaAleStacjaZOkazanymDrugimZostaje()
    {
        var result = Build("PL", Station("pl-a", Stream("https://x.example.com/live?preroll=1"), Stream("https://y.example.com/live")));

        var station = Assert.Single(result.Stations);
        Assert.Equal(["https://y.example.com/live"], station.Streams.Select(s => s.Url));
    }

    [Fact]
    public void AdresPosrednikaINiesprawdzonyOdpadaja()
    {
        var result = Build("PL", Station("pl-a",
            Stream("https://p.example.com/live", origin: "posrednik"),
            Stream("https://q.example.com/live", result: "blad"),
            Stream("https://r.example.com/live")));

        Assert.Equal(["https://r.example.com/live"], result.Stations[0].Streams.Select(s => s.Url));
        Assert.Equal(2, result.DroppedAddresses.Values.Sum());
    }

    [Fact]
    public void StacjaTylkoZAdresamiZapasowymiOdpada()
    {
        var result = Build("PL", Station("pl-a", Stream("https://a.example.com/live", origin: "wywnioskowany")));

        Assert.Empty(result.Stations);
        Assert.Equal(1, result.DroppedStations["adresy tylko zapasowe (wywnioskowane lub niepotwierdzone)"]);
    }

    [Fact]
    public void AdresWywnioskowanyZostajeJakoZapasZaOficjalnym()
    {
        var result = Build("PL", Station("pl-a",
            Stream("https://zapas.example.com/live", origin: "wywnioskowany", bitrate: 320),
            Stream("https://oficjalny.example.com/live", bitrate: 64)));

        Assert.Equal(["https://oficjalny.example.com/live", "https://zapas.example.com/live"], result.Stations[0].Streams.Select(s => s.Url));
    }

    [Fact]
    public void KolejnoscAdresowToPochodzenieHttpsStrumienCiagłyIPrzeplywnosc()
    {
        var result = Build("PL", Station("pl-a",
            Stream("http://a.example.com/live", bitrate: 320),
            Stream("https://b.example.com/live", origin: "domena-nadawcy", bitrate: 320),
            Stream("https://c.example.com/live.m3u8", bitrate: 320, protocol: "hls", codec: "HLS", container: "ADTS"),
            Stream("https://d.example.com/live", bitrate: 64),
            Stream("https://e.example.com/live", bitrate: 128)));

        Assert.Equal(
            [
                "https://e.example.com/live", "https://d.example.com/live", "https://c.example.com/live.m3u8",
                "http://a.example.com/live", "https://b.example.com/live",
            ],
            result.Stations[0].Streams.Select(s => s.Url));
    }

    [Fact]
    public void ZalecanyAdresIdzieZaPrzesunieciemIJestNaWskazanymMiejscu()
    {
        var station = Station("pl-a",
            Stream("http://a.example.com/live"),
            Stream("https://b.example.com/live", bitrate: 64));
        station["zalecany_strumien"] = 0;

        var result = Build("PL", station);

        Assert.Equal("http://a.example.com/live", result.Stations[0].Streams[result.Stations[0].Recommended].Url);
    }

    [Theory]
    [InlineData("MP3", null, "mp3")]
    [InlineData("AAC", null, "aac")]
    [InlineData("HE-AAC", null, "aac")]
    [InlineData("OGG-VORBIS", null, "ogg")]
    [InlineData("HLS", "ADTS", "aac")]
    [InlineData("HLS", "TS-AAC", "aac")]
    [InlineData("HLS", "TS-MP3", "mp3")]
    [InlineData("HLS", "TS", "unknown")]
    public void KodekIKontenerSaMapowaneNaKluczeProgramu(string codec, string? container, string expected)
    {
        var result = Build("PL", Station("pl-a", Stream("https://a.example.com/x", codec: codec, container: container, protocol: codec == "HLS" ? "hls" : "icecast")));

        var stream = result.Stations[0].Streams[0];
        Assert.Equal(expected, stream.Codec);
        Assert.Equal(codec == "HLS", stream.Hls);
    }

    [Fact]
    public void LogoZWymogiemZgodyAlboBezZalecenieZostajePuste()
    {
        var consent = Good("pl-c");
        consent["logo_wymaga_zgody"] = true;
        var none = Good("pl-n");
        none["zalecane_logo"] = null;

        var result = Build("PL", consent, none);

        Assert.All(result.Stations, s => Assert.Empty(s.Logos));
        Assert.Equal(1, result.DroppedLogos["wymóg zgody nadawcy"]);
    }

    [Fact]
    public void LogoPrzezHttpIZListyBlokadOdpada()
    {
        var station = Good();
        station["logo"] = new JsonArray(
            new JsonObject { ["url"] = "http://example.com/a.png", ["format"] = "png", ["wynik"] = "ok" },
            new JsonObject { ["url"] = "https://logo.example.ru/b.png", ["format"] = "png", ["wynik"] = "ok" });
        station["zalecane_logo"] = 0;

        Assert.Empty(Build("PL", station).Stations[0].Logos);
    }

    [Fact]
    public void PowtorzonyIdentyfikatorOdpada()
    {
        var result = Build("PL", Good("pl-a"), Good("pl-a"));

        Assert.Single(result.Stations);
        Assert.Equal(1, result.DroppedStations["powtórzony identyfikator"]);
    }

    [Fact]
    public void WynikJestPosortowanyKrajemIIdentyfikatorem()
    {
        var result = CatalogBuilder.Build(
        [
            new JsonObject { ["kraj"] = "PL", ["stacje"] = new JsonArray(Good("pl-b"), Good("pl-a")) }.ToJsonString(),
            new JsonObject { ["kraj"] = "CZ", ["stacje"] = new JsonArray(Good("cz-a")) }.ToJsonString(),
        ]);

        Assert.Equal(["cz-a", "pl-a", "pl-b"], result.Stations.Select(s => s.Id));
    }

    [Fact]
    public void KategorieWynikajaZFormatow()
    {
        var station = Good();
        station["format"] = new JsonArray("news", "talk", "classical");
        station["kategorie"] = new JsonArray("Informacje i rozmowy", "Klasyczna i jazz");

        var built = Build("PL", station);

        Assert.Equal([RadioCategories.NewsTalk, RadioCategories.ClassicalJazz], built.Stations[0].Categories);
        Assert.Empty(built.Warnings);
    }
}
