using System.Net;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

/// <summary>Reguła adresów: tabela, łączenie i przejścia.</summary>
public class AddressPolicyTests
{
    /// <summary>Do testów 127.0.0.1 uchodzi za publiczny, a lokalny jest tylko 127.0.0.2.</summary>
    private static readonly Func<IPAddress, bool> Pretend = address => address.Equals(IPAddress.Parse("127.0.0.2"));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("fec0::1")]
    [InlineData("feff::1")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.0.255")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("254.255.255.255")]
    [InlineData("::1")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fc00::1")]
    [InlineData("fdff::1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("ff02::1")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    public void LocalAddresses(string text) => Assert.True(AddressPolicy.IsLocal(IPAddress.Parse(text)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("11.0.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.167.1.1")]
    [InlineData("169.253.1.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("2a00:1450:4001::1")]
    [InlineData("::ffff:8.8.8.8")]
    public void PublicAddresses(string text) => Assert.False(AddressPolicy.IsLocal(IPAddress.Parse(text)));

    [Fact]
    public async Task CatalogueStationOnLoopbackIsRefused()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk));
        using var http = new StreamHttp(new LiveOptions());

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(
            server.Url(), new RequestContext(false, new ConnectionTrace()), true, CancellationToken.None));

        Assert.IsType<AddressRefusedException>(failure.InnerException);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task UserEnteredAddressMayBeLocal()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk));
        using var http = new StreamHttp(new LiveOptions());
        var trace = new ConnectionTrace();

        using var response = await http.SendAsync(server.Url(), new RequestContext(true, trace), true, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(trace.ConnectedPublic);
    }

    [Fact]
    public async Task HostWithPublicAndLocalAddressConnectsToPublicOne()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk));
        var options = new LiveOptions
        {
            IsLocalAddress = Pretend,
            ResolveHost = (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback }),
        };
        using var http = new StreamHttp(options);
        var trace = new ConnectionTrace();

        using var response = await http.SendAsync(
            new Uri($"http://stacja.test:{server.Port}/"), new RequestContext(false, trace), true, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(trace.ConnectedPublic);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task NameResolvingOnlyToLocalIsRefused()
    {
        var options = new LiveOptions { ResolveHost = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }) };
        using var http = new StreamHttp(options);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(
            new Uri("http://stacja.test:1/"), new RequestContext(false, new ConnectionTrace()), true, CancellationToken.None));

        Assert.IsType<AddressRefusedException>(failure.InnerException);
    }

    [Theory]
    [InlineData("http://127.0.0.1/", "http://127.0.0.2/", false, true)]
    [InlineData("http://127.0.0.1/", "http://127.0.0.2/", true, true)]
    [InlineData("http://127.0.0.2/", "http://127.0.0.2/", true, false)]
    [InlineData("http://127.0.0.2/", "http://127.0.0.1/", true, false)]
    [InlineData("http://127.0.0.2/", "http://localhost/", false, true)]
    [InlineData("http://127.0.0.1/", "http://127.0.0.1/", false, false)]
    public void TransitionRule(string from, string to, bool allowLocal, bool refused)
    {
        var policy = new AddressPolicy(new LiveOptions { IsLocalAddress = Pretend });

        void Check() => policy.CheckTransition(new Uri(from), new Uri(to), allowLocal);

        if (refused)
            Assert.Equal(LiveFailureReason.AddressRefused, Assert.Throws<LiveOpenException>(Check).Failure.Reason);
        else
            Check();
    }

    private static StreamOpener Opener(LiveOptions options) =>
        new(new StreamHttp(options), new AddressPolicy(options), options);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedirectFromPublicToLocalIsRefusedEvenForUserAddress(bool allowLocal)
    {
        await using var local = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk), "127.0.0.2");
        await using var publicServer = new FakeRadioServer(FakeRadio.Respond(
            $"HTTP/1.1 302 Moved\r\nLocation: {local.Url()}\r\nContent-Length: 0\r\n"));

        var failure = await Assert.ThrowsAsync<LiveOpenException>(() => Opener(new LiveOptions { IsLocalAddress = Pretend })
            .OpenAsync(publicServer.Url(), allowLocal, CancellationToken.None));

        Assert.Equal(LiveFailureReason.AddressRefused, failure.Failure.Reason);
        Assert.Equal(0, local.ConnectionCount);
    }

    [Fact]
    public async Task NamedHostResolvingPublicCannotRedirectToLocal()
    {
        await using var local = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk), "127.0.0.2");
        await using var publicServer = new FakeRadioServer(FakeRadio.Respond(
            $"HTTP/1.1 302 Moved\r\nLocation: {local.Url()}\r\nContent-Length: 0\r\n"));
        var options = new LiveOptions
        {
            IsLocalAddress = Pretend,
            ResolveHost = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
        };

        var failure = await Assert.ThrowsAsync<LiveOpenException>(() => Opener(options)
            .OpenAsync(new Uri($"http://stacja.test:{publicServer.Port}/"), true, CancellationToken.None));

        Assert.Equal(LiveFailureReason.AddressRefused, failure.Failure.Reason);
        Assert.Equal(0, local.ConnectionCount);
    }

    [Fact]
    public async Task ListEntryPointingToLocalIsRefused()
    {
        await using var local = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk), "127.0.0.2");
        await using var list = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nConnection: close\r\n", $"#EXTM3U\n{local.Url()}\n"));

        var failure = await Assert.ThrowsAsync<LiveOpenException>(() => Opener(new LiveOptions { IsLocalAddress = Pretend })
            .OpenAsync(list.Url(), true, CancellationToken.None));

        Assert.Equal(LiveFailureReason.AddressRefused, failure.Failure.Reason);
        Assert.Equal(0, local.ConnectionCount);
    }

    [Fact]
    public async Task LocalListMayPointToLocalForUserAddress()
    {
        await using var station = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(4), ct);
            await FakeRadio.HoldAsync(stream, ct);
        });
        await using var list = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nConnection: close\r\n", $"#EXTM3U\n{station.Url()}\n"));

        using var opened = await Opener(new LiveOptions()).OpenAsync(list.Url(), true, CancellationToken.None);

        Assert.Equal(station.Url(), opened.FinalUrl);
    }

    [Fact]
    public async Task UrlGuardStopsRedirectAndListEntry()
    {
        await using var target = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk));
        var options = new LiveOptions { UrlGuard = url => url.AbsolutePath == "/stream" ? LiveFailureReason.UrlBlocked : null };
        await using var redirect = new FakeRadioServer(FakeRadio.Respond(
            $"HTTP/1.1 302 Moved\r\nLocation: {target.Url()}\r\nContent-Length: 0\r\n"));
        await using var list = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nConnection: close\r\n", $"#EXTM3U\n{target.Url()}\n"));

        foreach (var entry in new[] { redirect.Url("/a"), list.Url("/a") })
        {
            var failure = await Assert.ThrowsAsync<LiveOpenException>(
                () => Opener(options).OpenAsync(entry, true, CancellationToken.None));
            Assert.Equal(LiveFailureReason.UrlBlocked, failure.Failure.Reason);
        }

        Assert.Equal(0, target.ConnectionCount);
    }

    [Fact]
    public async Task UrlGuardStopsTheEntryAddress()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk));
        var options = new LiveOptions { UrlGuard = _ => LiveFailureReason.UrlBlocked };

        var failure = await Assert.ThrowsAsync<LiveOpenException>(
            () => Opener(options).OpenAsync(server.Url(), true, CancellationToken.None));

        Assert.Equal(LiveFailureReason.UrlBlocked, failure.Failure.Reason);
        Assert.Equal(0, server.ConnectionCount);
    }

    /// <summary>Host z nazwą rozwiązany na adres publiczny: lista nie doprowadzi z niego do routera, choć adres wpisał użytkownik.</summary>
    [Fact]
    public async Task ListOnANamedPublicHostCannotPointToLocal()
    {
        await using var local = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk), "127.0.0.2");
        await using var list = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nConnection: close\r\n", $"#EXTM3U\n{local.Url()}\n"));
        var options = new LiveOptions
        {
            IsLocalAddress = Pretend,
            ResolveHost = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
        };

        var failure = await Assert.ThrowsAsync<LiveOpenException>(() => Opener(options)
            .OpenAsync(new Uri($"http://lista.test:{list.Port}/"), true, CancellationToken.None));

        Assert.Equal(LiveFailureReason.AddressRefused, failure.Failure.Reason);
        Assert.Equal(0, local.ConnectionCount);
    }

    /// <summary>Połączenie z puli nie przechodzi reguły adresów i nie zapisuje, że łączono z adresem publicznym.</summary>
    [Fact]
    public async Task EveryRequestGetsItsOwnConnection()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", ct);
            await FakeRadio.HoldAsync(stream, ct);
        });
        using var http = new StreamHttp(new LiveOptions { ConnectTimeout = TimeSpan.FromSeconds(2), IsLocalAddress = Pretend });
        var second = new ConnectionTrace();

        using (await http.SendAsync(server.Url(), new RequestContext(true, new ConnectionTrace()), false, CancellationToken.None))
        {
        }

        using (await http.SendAsync(server.Url(), new RequestContext(true, second), false, CancellationToken.None))
        {
        }

        Assert.Equal(2, server.ConnectionCount);
        Assert.True(second.ConnectedPublic);
    }

    /// <summary>Ciastko stacji pozwalałoby rozpoznać słuchacza między połączeniami.</summary>
    [Fact]
    public async Task CookieOfTheStationIsNotSentBack()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nSet-Cookie: stacja=1\r\nContent-Length: 0\r\nConnection: close\r\n"));
        using var http = new StreamHttp(new LiveOptions());

        for (var i = 0; i < 2; i++)
        {
            using var response = await http.SendAsync(
                server.Url(), new RequestContext(true, new ConnectionTrace()), false, CancellationToken.None);
        }

        Assert.Equal(2, server.Requests.Count);
        Assert.False(server.Requests[1].Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public void UrlGuardIsConsultedForEveryAddress()
    {
        var policy = new AddressPolicy(new LiveOptions
        {
            UrlGuard = url => url.Host.EndsWith(".zablokowany", StringComparison.Ordinal) ? LiveFailureReason.UrlBlocked : null,
        });
        var good = new Uri("http://stacja.test/");
        var bad = new Uri("http://adres.zablokowany/");

        policy.CheckAddress(good, false);
        Assert.Equal(LiveFailureReason.UrlBlocked,
            Assert.Throws<LiveOpenException>(() => policy.CheckAddress(bad, false)).Failure.Reason);
        Assert.Equal(LiveFailureReason.UrlBlocked,
            Assert.Throws<LiveOpenException>(() => policy.CheckTransition(good, bad, true)).Failure.Reason);
    }

    // ---------- serwer pośredniczący ----------

    private const string BadGateway = "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n";

    private static FakeRadioServer Proxy() => new(FakeRadio.Respond(BadGateway));

    private static LiveOptions Through(FakeRadioServer proxy, params string[] resolved) => new()
    {
        Proxy = new WebProxy(proxy.Url("/")),
        ResolveHost = (_, _) => Task.FromResult(resolved.Select(IPAddress.Parse).ToArray()),
    };

    private static async Task<LiveFailure> FailureThroughProxyAsync(LiveOptions options, string scheme, bool allowLocal = false)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Opener(options)
            .OpenAsync(new Uri($"{scheme}://stacja.test/x"), allowLocal, CancellationToken.None));
        return LiveErrors.Classify(failure).Failure;
    }

    /// <summary>Pośrednika wybrał użytkownik albo system, więc może stać w sieci lokalnej, także dla stacji z katalogu.</summary>
    [Theory]
    [InlineData("http", "GET")]
    [InlineData("https", "CONNECT")]
    public async Task ProxyOnALocalAddressCarriesAPublicStation(string scheme, string method)
    {
        await using var proxy = Proxy();

        var failure = await FailureThroughProxyAsync(Through(proxy, "93.184.216.34"), scheme);

        Assert.NotEqual(LiveFailureReason.AddressRefused, failure.Reason);
        Assert.Equal(method, Assert.Single(proxy.Requests).Method);
    }

    [Theory]
    [InlineData("http", "192.168.1.1")]
    [InlineData("https", "192.168.1.1")]
    [InlineData("http", "93.184.216.34", "10.0.0.1")]
    [InlineData("https", "93.184.216.34", "10.0.0.1")]
    public async Task NameResolvingToALocalAddressNeverReachesTheProxy(string scheme, params string[] resolved)
    {
        await using var proxy = Proxy();

        var failure = await FailureThroughProxyAsync(Through(proxy, resolved), scheme);

        Assert.Equal(LiveFailureReason.AddressRefused, failure.Reason);
        Assert.Equal(0, proxy.ConnectionCount);
    }

    /// <summary>Za pośrednikiem firmowym nazwy publiczne często nie rozwiązują się lokalnie.</summary>
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task NameThatDoesNotResolveLocallyGoesToTheProxy(string scheme)
    {
        await using var proxy = Proxy();
        var options = Through(proxy) with { ResolveHost = (_, _) => throw new System.Net.Sockets.SocketException(11001) };

        await FailureThroughProxyAsync(options, scheme);

        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task ResolvingThatHangsDoesNotHoldTheProxyRequest()
    {
        await using var proxy = Proxy();
        var options = Through(proxy) with
        {
            ProxyResolveTimeout = TimeSpan.FromMilliseconds(50),
            ResolveHost = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            },
        };

        await FailureThroughProxyAsync(options, "http");

        Assert.Single(proxy.Requests);
    }

    /// <summary>Adres wpisany przez użytkownika może być lokalny: nazwy nie trzeba rozwiązywać.</summary>
    [Fact]
    public async Task UserAddressThroughAProxyIsNotResolved()
    {
        await using var proxy = Proxy();
        var resolved = 0;
        var options = Through(proxy) with
        {
            ResolveHost = (_, _) =>
            {
                Interlocked.Increment(ref resolved);
                return Task.FromResult(new[] { IPAddress.Parse("192.168.1.1") });
            },
        };

        await FailureThroughProxyAsync(options, "http", allowLocal: true);

        Assert.Equal(0, resolved);
        Assert.Single(proxy.Requests);
    }

    /// <summary>Tunel nie niesie kontekstu żądania, a dalsze przejścia i tak nie mogą wrócić do sieci lokalnej.</summary>
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task NameBehindAProxyCountsAsPublic(string scheme)
    {
        await using var proxy = Proxy();
        using var http = new StreamHttp(Through(proxy, "93.184.216.34"));
        var trace = new ConnectionTrace();

        try
        {
            using var response = await http.SendAsync(
                new Uri($"{scheme}://stacja.test/x"), new RequestContext(true, trace), false, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            // Tunel odrzucony przez atrapę; liczy się to, co zapisano przed wysłaniem.
        }

        Assert.True(trace.ConnectedPublic);
    }

    /// <summary>Niedziałający pierwszy adres, na przykład AAAA, nie może zużyć całego czasu kolejnego.</summary>
    [Fact]
    public async Task AddressThatDoesNotAnswerLeavesTimeForTheNextOne()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n"));
        var silent = IPAddress.Parse("127.0.0.3");
        var options = new LiveOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            ResolveHost = (_, _) => Task.FromResult(new[] { silent, IPAddress.Loopback }),
            ConnectSocket = (socket, endPoint, ct) => endPoint.Address.Equals(silent)
                ? new ValueTask(Task.Delay(Timeout.Infinite, ct))
                : socket.ConnectAsync(endPoint, ct),
        };
        using var http = new StreamHttp(options);

        using var response = await http.SendAsync(
            new Uri($"http://stacja.test:{server.Port}/"), new RequestContext(true, new ConnectionTrace()), false, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.ConnectionCount);
    }
}
