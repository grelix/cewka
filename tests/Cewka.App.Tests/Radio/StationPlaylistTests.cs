using Cewka.App.Radio;
using Xunit;
using static Cewka.App.Tests.Radio.RadioTestData;

namespace Cewka.App.Tests.Radio;

public class StationPlaylistTests
{
    [Fact]
    public void M3uZExtinfDajeNazwyIAdresy()
    {
        const string text = "#EXTM3U\r\n#EXTINF:-1,Radio Jeden\r\nhttp://a.example.com/live\r\n#EXTINF:-1 tvg-id=\"x\",Radio Dwa\r\nhttps://b.example.com/live\r\n";

        var import = StationPlaylist.Import(text);

        Assert.Equal(["Radio Jeden", "Radio Dwa"], import.Stations.Select(s => s.Name));
        Assert.Equal(["http://a.example.com/live", "https://b.example.com/live"], import.Stations.Select(s => s.Streams[0].Url));
        Assert.Empty(import.Rejected);
    }

    [Fact]
    public void M3uBezExtinfUzywaAdresuJakoNazwyAKomentarzeSaPomijane()
    {
        var import = StationPlaylist.Import("\uFEFF# komentarz\nhttp://a.example.com/live\n\n");

        var station = Assert.Single(import.Stations);
        Assert.Equal("http://a.example.com/live", station.Name);
    }

    [Fact]
    public void PlsJestRozpoznawanyPoNaglowkuIKolejnosciWpisow()
    {
        const string text = "[playlist]\nNumberOfEntries=2\nFile2=http://b.example.com/live\nTitle2=Dwa\nFile1=http://a.example.com/live\nTitle1=Jeden\nVersion=2\n";

        var import = StationPlaylist.Import(text);

        Assert.Equal(["Jeden", "Dwa"], import.Stations.Select(s => s.Name));
    }

    [Theory]
    [InlineData("http://stream.example.ru/live", AddressVerdict.Blocked)]
    [InlineData("https://radio.example.by/a", AddressVerdict.Blocked)]
    [InlineData("http://radio.example.su/a", AddressVerdict.Blocked)]
    [InlineData("file:///C:/muzyka/a.mp3", AddressVerdict.UnsupportedScheme)]
    [InlineData("rtsp://a.example.com/x", AddressVerdict.UnsupportedScheme)]
    public void ImportOdrzucaAdresyZBlokadyIInnegoSchematu(string url, AddressVerdict expected)
    {
        var import = StationPlaylist.Import($"#EXTM3U\n#EXTINF:-1,X\n{url}\nhttp://ok.example.com/live\n");

        Assert.Single(import.Stations);
        var rejected = Assert.Single(import.Rejected);
        Assert.Equal(url, rejected.Url);
        Assert.Equal(expected, rejected.Reason);
    }

    [Fact]
    public void AdresyZImportuNigdyNieSiegajaDoSieciLokalnej()
    {
        var import = StationPlaylist.Import("#EXTINF:-1,Domowe\nhttp://192.168.1.10:8000/live\n");

        var station = Assert.Single(import.Stations);
        Assert.False(station.AllowLocalAddress);
        Assert.Equal(StationOrigin.Imported, station.Origin);
        Assert.False(station.ToLiveSource()!.AllowLocalAddress);
    }

    [Fact]
    public void ImportOdrzucaNazwePodmiotuZListyBlokad()
    {
        var import = StationPlaylist.Import("#EXTINF:-1,RT Deutsch\nhttps://stream.example.com/live\n");

        Assert.Empty(import.Stations);
        Assert.Single(import.Rejected);
    }

    [Fact]
    public void TenSamAdresWPlikuDajeJednaStacje()
    {
        var import = StationPlaylist.Import("http://a.example.com/live\nhttp://a.example.com/live\n");

        Assert.Single(import.Stations);
    }

    [Fact]
    public void PustyPlikDajePustyWynik()
    {
        var import = StationPlaylist.Import("");

        Assert.Empty(import.Stations);
        Assert.Empty(import.Rejected);
    }

    [Fact]
    public void EksportZapisujeRozszerzonyM3uZPierwszymAdresem()
    {
        var a = Station("a", "Radio A", "PL");
        var b = Station("b", "Radio B", "PL", streams: [Stream("https://b.example.com/1"), Stream("https://b.example.com/2")]);
        var empty = Station("c", "Pusta", "PL");
        empty.Streams.Clear();

        var text = StationPlaylist.Export([a, b, empty]);

        Assert.Equal("#EXTM3U\n#EXTINF:-1,Radio A\nhttps://a.example.com/live\n#EXTINF:-1,Radio B\nhttps://b.example.com/1\n", text);
    }

    [Fact]
    public void EksportUzywaNazwyZKrajemOdWywolujacegoIWycinaZnakiNowejLinii()
    {
        var station = Station("a", "Kiss\nFM", "RO");

        var text = StationPlaylist.Export([station], s => $"{s.Name} (Rumunia)");

        Assert.Contains("#EXTINF:-1,Kiss FM (Rumunia)\n", text);
    }

    [Fact]
    public void EksportIImportToDajaTeSameAdresy()
    {
        var stations = new[] { Station("a", "Radio A", "PL"), Station("b", "Radio B", "PL") };

        var import = StationPlaylist.Import(StationPlaylist.Export(stations));

        Assert.Equal(["Radio A", "Radio B"], import.Stations.Select(s => s.Name));
        Assert.Equal(stations.Select(s => s.Streams[0].Url), import.Stations.Select(s => s.Streams[0].Url));
    }
}
