using Cewka.App.Radio;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

public class RadioBlocklistTests
{
    private static readonly RadioBlocklist List = RadioBlocklist.BuiltIn;

    [Theory]
    [InlineData("stream.example.ru")]
    [InlineData("example.RU")]
    [InlineData("a.b.example.by")]
    [InlineData("radio.example.su")]
    [InlineData("rt.com")]
    [InlineData("de.rt.com")]
    [InlineData("sputniknews.com")]
    [InlineData("xn--e1afmkfd.xn--p1ai")]
    public void DomenyZListyBlokadSaBlokowaneZSubdomenami(string host) => Assert.True(List.IsBlockedHost(host), host);

    [Theory]
    [InlineData("notrt.com")]
    [InlineData("rt.com.example.org")]
    [InlineData("sputnik.mdr.de")]
    [InlineData("example.com")]
    [InlineData("ruhr.example.de")]
    [InlineData("subway.pl")]
    [InlineData("example.bydgoszcz.pl")]
    public void FalszywychTrafienDopasowaniePoEtykietachNieDaje(string host) => Assert.False(List.IsBlockedHost(host), host);

    [Fact]
    public void BlokadaNazwDotyczyCalejNazwyNieFragmentu()
    {
        Assert.True(List.IsBlockedName("Radyo Sputnik"));
        Assert.True(List.IsBlockedName("RT Deutsch"));
        Assert.False(List.IsBlockedName("MDR Sputnik"));
        Assert.False(List.IsBlockedName("Art Radio"));
    }

    [Theory]
    [InlineData("http://stream.example.com/live", AddressVerdict.Allowed)]
    [InlineData("https://stream.example.com:8000/live?x=1", AddressVerdict.Allowed)]
    [InlineData("https://stream.example.ru/live", AddressVerdict.Blocked)]
    [InlineData("http://192.168.1.5:8000/", AddressVerdict.Allowed)]
    [InlineData("ftp://stream.example.com/live", AddressVerdict.UnsupportedScheme)]
    [InlineData("file:///C:/radio.m3u", AddressVerdict.UnsupportedScheme)]
    [InlineData("stream.example.com/live", AddressVerdict.Invalid)]
    [InlineData("", AddressVerdict.Invalid)]
    [InlineData(null, AddressVerdict.Invalid)]
    public void AdresSłuchaczaJestSprawdzany(string? text, AddressVerdict expected) => Assert.Equal(expected, List.CheckAddress(text));

    [Fact]
    public void StrazZwracaPrzyczyneUrlBlockedDlaZablokowanegoAdresu()
    {
        Assert.Equal(LiveFailureReason.UrlBlocked, RadioBlocklist.UrlGuard(new Uri("https://x.example.ru/live")));
        Assert.Null(RadioBlocklist.UrlGuard(new Uri("https://x.example.com/live")));
    }

    [Fact]
    public void ListaDodatkowaDomenDzialaZSubdomenami()
    {
        var custom = new RadioBlocklist(["zlosc.example"], ["Zła Stacja"]);

        Assert.True(custom.IsBlockedHost("a.zlosc.example"));
        Assert.True(custom.IsBlockedName("zla stacja"));
        Assert.False(custom.IsBlockedHost("example"));
    }

    [Fact]
    public void ZapisanaStacjaZeWszystkimiAdresamiZablokowanymiNieGra()
    {
        var blocked = RadioTestData.Station("x-1", "Stacja", "PL", streams: [RadioTestData.Stream("https://a.example.ru/live")]);
        var partly = RadioTestData.Station("x-2", "Stacja", "PL", streams: [RadioTestData.Stream("https://a.example.ru/live"), RadioTestData.Stream("https://b.example.com/live")]);

        Assert.Equal(AddressVerdict.Blocked, List.CheckStation(blocked));
        Assert.Equal(AddressVerdict.Allowed, List.CheckStation(partly));
    }

    /// <summary>Strażnik trafia do silnika: domyślna sesja stacji odmawia adresu z listy blokad bez sięgania do sieci.</summary>
    [Fact]
    public void SilnikDajeStrazaDomyslnejSesjiStacji()
    {
        using var engine = new PlaybackEngine(static (_, _) => throw new InvalidOperationException("bez wyjścia dźwięku"))
        {
            LiveUrlGuard = RadioBlocklist.UrlGuard,
        };
        var source = new LiveSource("x", "Stacja", [new Uri("http://stream.example.ru/live")]);

        using var session = engine.OpenLiveSession(source);
        session.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (session.State != LiveState.Failed && DateTime.UtcNow < deadline)
            Thread.Sleep(20);

        Assert.Equal(LiveState.Failed, session.State);
        Assert.Equal(LiveFailureReason.UrlBlocked, session.Failure?.Reason);
    }
}
