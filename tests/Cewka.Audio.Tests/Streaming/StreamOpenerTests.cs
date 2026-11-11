using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

/// <summary>Przekierowania, listy i rozpoznanie treści na atrapie stacji.</summary>
public class StreamOpenerTests
{
    private const string HttpOk = "HTTP/1.1 200 OK\r\nConnection: close\r\n";

    private static readonly LiveOptions Base = new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        StallTimeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<OpenedStream> OpenAsync(Uri url, LiveOptions? options = null, bool allowLocal = true)
    {
        options ??= Base;
        var http = new StreamHttp(options);
        return await new StreamOpener(http, new AddressPolicy(options), options).OpenAsync(url, allowLocal, CancellationToken.None);
    }

    private static async Task<LiveFailure> FailureOfAsync(Uri url, LiveOptions? options = null, bool allowLocal = true)
    {
        var failure = await Assert.ThrowsAsync<LiveOpenException>(() => OpenAsync(url, options, allowLocal));
        return failure.Failure;
    }

    /// <summary>Ramki MP3, potem atrapa czeka, aż klient się rozłączy.</summary>
    private static Func<FakeRequest, Stream, CancellationToken, Task> Station(string head = FakeRadio.IcyOk, int frames = 8) =>
        async (_, stream, ct) =>
        {
            await stream.RespondAsync(head, FakeRadio.Mp3Frames(frames), ct);
            await FakeRadio.HoldAsync(stream, ct);
        };

    private static string Redirect(Uri to, int status = 302) => $"HTTP/1.1 {status} Moved\r\nLocation: {to}\r\nContent-Length: 0\r\n";

    [Fact]
    public async Task PlainIcyStationOpensWithHeadersAndHead()
    {
        await using var server = new FakeRadioServer(Station(
            "ICY 200 OK\r\nicy-name:Radio Testowe\r\nicy-genre:Pop\r\nicy-url:http://x/\r\nicy-br:128,128\r\nicy-sr:44100\r\ncontent-type:audio/mpeg\r\n"));

        using var opened = await OpenAsync(server.Url());

        Assert.Equal(LiveCodec.Mp3, opened.Codec);
        Assert.NotEmpty(opened.Head);
        Assert.Equal(new IcyHeaders("Radio Testowe", "Pop", "http://x/", 128, 44100, "audio/mpeg"), opened.Headers);
        Assert.Equal(server.Url(), opened.FinalUrl);
        Assert.Null(opened.Demuxer);
        Assert.Equal(0, opened.MetaInt);
    }

    [Theory]
    [InlineData("128000")]
    [InlineData("2147483647")]
    public async Task ImplausibleHeaderBitrateIsNotReported(string value)
    {
        await using var server = new FakeRadioServer(Station($"ICY 200 OK\r\nicy-br:{value}\r\n"));

        using var opened = await OpenAsync(server.Url());

        Assert.Null(opened.Headers.BitrateKbps);
    }

    [Fact]
    public async Task RedirectLoopFailsAtOnce()
    {
        FakeRadioServer server = null!;
        server = new FakeRadioServer((request, stream, ct) => stream.RespondAsync(
            Redirect(server.Url(request.Path == "/a" ? "/b" : "/a")), [], ct));
        await using var _ = server;

        var failure = await FailureOfAsync(server.Url("/a"));

        Assert.Equal(LiveFailureReason.TooManyRedirects, failure.Reason);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task RedirectLimitIsFive(int redirects, bool opens)
    {
        FakeRadioServer server = null!;
        server = new FakeRadioServer(async (request, stream, ct) =>
        {
            var step = int.Parse(request.Path[1..]);
            if (step < redirects)
                await stream.RespondAsync(Redirect(server.Url($"/{step + 1}")), [], ct);
            else
                await Station()(request, stream, ct);
        });
        await using var _ = server;

        if (opens)
        {
            using var opened = await OpenAsync(server.Url("/0"));
            Assert.Equal(server.Url($"/{redirects}"), opened.FinalUrl);
        }
        else
        {
            Assert.Equal(LiveFailureReason.TooManyRedirects, (await FailureOfAsync(server.Url("/0"))).Reason);
        }
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task RelativeRedirectIsResolved(int status)
    {
        await using var server = new FakeRadioServer((request, stream, ct) => request.Path == "/k/a"
            ? stream.RespondAsync($"HTTP/1.1 {status} Moved\r\nLocation: b\r\nContent-Length: 0\r\n", [], ct)
            : Station()(request, stream, ct));

        using var opened = await OpenAsync(server.Url("/k/a"));

        Assert.Equal(server.Url("/k/b"), opened.FinalUrl);
    }

    [Theory]
    [InlineData("HTTP/1.1 302 Moved\r\nContent-Length: 0\r\n")]
    [InlineData("HTTP/1.1 302 Moved\r\nLocation: ftp://x/y\r\nContent-Length: 0\r\n")]
    public async Task RedirectWithoutUsableLocationIsAStatusFailure(string head)
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(head));

        var failure = await FailureOfAsync(server.Url());

        Assert.Equal(LiveFailureReason.OtherHttpStatus, failure.Reason);
        Assert.Equal(302, failure.HttpStatus);
    }

    [Fact]
    public async Task HttpsToHttpIsAllowedForAudio()
    {
        using var certificates = new TestCertificates();
        await using var plain = new FakeRadioServer(Station());
        await using var secure = new FakeRadioServer(
            FakeRadio.Respond(Redirect(plain.Url())), certificate: certificates.Server);

        using var opened = await OpenAsync(secure.Url(), Base with { TrustedRootForTests = certificates.Root });

        Assert.Equal(plain.Url(), opened.FinalUrl);
    }

    [Fact]
    public async Task HttpsToHttpIsRefusedForPlaylist()
    {
        using var certificates = new TestCertificates();
        await using var plain = new FakeRadioServer(FakeRadio.Respond(HttpOk, "#EXTM3U\nhttp://127.0.0.1:1/\n"));
        await using var secure = new FakeRadioServer(
            FakeRadio.Respond(Redirect(plain.Url())), certificate: certificates.Server);

        var failure = await FailureOfAsync(secure.Url(), Base with { TrustedRootForTests = certificates.Root });

        Assert.Equal(LiveFailureReason.InsecureDowngrade, failure.Reason);
    }

    [Fact]
    public async Task HttpEntryOnHttpsListPlaysWhenAudio()
    {
        using var certificates = new TestCertificates();
        await using var plain = new FakeRadioServer(Station());
        await using var secure = new FakeRadioServer(
            FakeRadio.Respond(HttpOk, $"#EXTM3U\n{plain.Url()}\n"), certificate: certificates.Server);

        using var opened = await OpenAsync(secure.Url(), Base with { TrustedRootForTests = certificates.Root });

        Assert.Equal(plain.Url(), opened.FinalUrl);
    }

    [Fact]
    public async Task HttpEntryOnHttpsListIsRefusedWhenPlaylist()
    {
        using var certificates = new TestCertificates();
        await using var plain = new FakeRadioServer(FakeRadio.Respond(HttpOk, "[playlist]\nFile1=http://127.0.0.1:1/\n"));
        await using var secure = new FakeRadioServer(
            FakeRadio.Respond(HttpOk, $"#EXTM3U\n{plain.Url()}\n"), certificate: certificates.Server);

        var failure = await FailureOfAsync(secure.Url(), Base with { TrustedRootForTests = certificates.Root });

        Assert.Equal(LiveFailureReason.InsecureDowngrade, failure.Reason);
    }

    [Theory]
    [InlineData(401, (int)LiveFailureReason.Unauthorized)]
    [InlineData(403, (int)LiveFailureReason.Forbidden)]
    [InlineData(404, (int)LiveFailureReason.NotFound)]
    [InlineData(410, (int)LiveFailureReason.NotFound)]
    [InlineData(451, (int)LiveFailureReason.UnavailableForLegalReasons)]
    [InlineData(408, (int)LiveFailureReason.ServerError)]
    [InlineData(429, (int)LiveFailureReason.ServerError)]
    [InlineData(500, (int)LiveFailureReason.ServerError)]
    [InlineData(503, (int)LiveFailureReason.ServerError)]
    [InlineData(400, (int)LiveFailureReason.OtherHttpStatus)]
    public async Task StatusCodesMapToReasons(int status, int reason)
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond($"ICY {status} X\r\n"));

        var failure = await FailureOfAsync(server.Url());

        Assert.Equal((LiveFailureReason)reason, failure.Reason);
        Assert.Equal(status, failure.HttpStatus);
    }

    [Fact]
    public async Task RetryAfterIsRead()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 503 Busy\r\nRetry-After: 7\r\nContent-Length: 0\r\n"));

        var failure = await Assert.ThrowsAsync<LiveOpenException>(() => OpenAsync(server.Url()));

        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
    }

    [Fact]
    public async Task HtmlDressedAsAudioIsNotAStream()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nConnection: close\r\n", "<!DOCTYPE html><html><body>Zaloguj</body></html>"));

        Assert.Equal(LiveFailureReason.NotAStream, (await FailureOfAsync(server.Url())).Reason);
    }

    [Fact]
    public async Task Fmp4PlaylistIsReportedAsHlsNotSupported()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nContent-Type: audio/x-mpegurl\r\nConnection: close\r\n", "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MAP:URI=\"i.mp4\"\n#EXTINF:6,\na.ts\n"));

        Assert.Equal(LiveFailureReason.HlsNotSupportedYet, (await FailureOfAsync(server.Url("/live.m3u8"))).Reason);
    }

    [Fact]
    public async Task HlsTagFarIntoListIsStillHls()
    {
        var padding = string.Concat(Enumerable.Repeat("#EXTINF:-1,x\n", 2000));
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync(HttpOk + "\r\n#EXTM3U\n", ct);
            await Task.Delay(50, ct);
            await stream.WriteAsync(padding + "#EXT-X-VERSION:3\n#EXT-X-MAP:URI=\"i.mp4\"\nhttp://x/\n", ct);
        });

        Assert.Equal(LiveFailureReason.HlsNotSupportedYet, (await FailureOfAsync(server.Url())).Reason);
    }

    [Fact]
    public async Task AdtsWithoutASystemDecoderGivesCodecUnavailable()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk, FakeRadio.AdtsFrames(4)));

        var failure = (await Assert.ThrowsAsync<LiveOpenException>(
            () => OpenAsync(server.Url(), Base with { CodecPlayable = _ => false }))).Failure;

        Assert.Equal(LiveFailureReason.CodecUnavailable, failure.Reason);
        Assert.Equal(LiveCodec.Aac, failure.Codec);
    }

    [Fact]
    public async Task CodecBecomesPlayableThroughOption()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.AdtsFrames(4), ct);
            await FakeRadio.HoldAsync(stream, ct);
        });

        using var opened = await OpenAsync(server.Url(), Base with { CodecPlayable = _ => true });

        Assert.Equal(LiveCodec.Aac, opened.Codec);
    }

    [Fact]
    public async Task RandomBytesAreUnrecognised()
    {
        var noise = new byte[2000];
        new Random(5).NextBytes(noise);
        noise[0] = 0;
        await using var server = new FakeRadioServer(FakeRadio.Respond(HttpOk, noise));

        Assert.Equal(LiveFailureReason.UnrecognisedContent, (await FailureOfAsync(server.Url())).Reason);
    }

    /// <summary>Przyczyna zostaje, a ponawianie ma sens tylko wtedy, gdy danych nie starczyło do rozpoznania.</summary>
    [Fact]
    public async Task UnrecognisedContentIsPermanentOnlyWhenThereWasEnoughOfIt()
    {
        await using var shortBody = new FakeRadioServer(FakeRadio.Respond(HttpOk, new byte[2000]));
        await using var longBody = new FakeRadioServer(FakeRadio.Respond(HttpOk, new byte[Base.SniffBytes + 100]));

        var cut = await Assert.ThrowsAsync<LiveOpenException>(() => OpenAsync(shortBody.Url()));
        var whole = await Assert.ThrowsAsync<LiveOpenException>(() => OpenAsync(longBody.Url()));

        Assert.Equal(LiveFailureReason.UnrecognisedContent, cut.Failure.Reason);
        Assert.False(LiveErrors.Classify(cut).Permanent);
        Assert.Equal(LiveFailureReason.UnrecognisedContent, whole.Failure.Reason);
        Assert.True(LiveErrors.Classify(whole).Permanent);
    }

    [Fact]
    public async Task EmptyBodyIsATransientEnd()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(HttpOk));

        Assert.Equal(LiveFailureReason.StreamEnded, (await FailureOfAsync(server.Url())).Reason);
    }

    [Fact]
    public async Task SilenceAfterHeadersIsStalled()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync(FakeRadio.IcyOk + "\r\n", ct);
            await FakeRadio.HoldAsync(stream, ct);
        });

        var failure = await FailureOfAsync(server.Url(), Base with { StallTimeout = TimeSpan.FromMilliseconds(150) });

        Assert.Equal(LiveFailureReason.Stalled, failure.Reason);
    }

    [Fact]
    public async Task PartialDataThenSilenceIsDecidedOnWhatArrived()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync(FakeRadio.IcyOk + "\r\n<ht", ct);
            await FakeRadio.HoldAsync(stream, ct);
        });

        var failure = await FailureOfAsync(server.Url(), Base with { StallTimeout = TimeSpan.FromMilliseconds(150) });

        Assert.Equal(LiveFailureReason.UnrecognisedContent, failure.Reason);
    }

    [Fact]
    public async Task PlsWithDeadFirstEntryPlaysSecond()
    {
        await using var station = new FakeRadioServer(Station());
        await using var list = new FakeRadioServer(FakeRadio.Respond(
            HttpOk, $"[playlist]\r\nFile2={station.Url()}\r\nFile1=http://127.0.0.1:1/martwy\r\n"));

        using var opened = await OpenAsync(list.Url());

        Assert.Equal(station.Url(), opened.FinalUrl);
    }

    [Fact]
    public async Task WhenNoEntryPlaysFirstReasonWins()
    {
        await using var gone = new FakeRadioServer(FakeRadio.Respond("ICY 404 Gone\r\n"));
        await using var refused = new FakeRadioServer(FakeRadio.Respond("ICY 403 No\r\n"));
        await using var list = new FakeRadioServer(FakeRadio.Respond(HttpOk, $"#EXTM3U\n{gone.Url()}\n{refused.Url()}\n"));

        Assert.Equal(LiveFailureReason.NotFound, (await FailureOfAsync(list.Url())).Reason);
        Assert.Equal(1, refused.ConnectionCount);
    }

    private static FakeRadioServer NestedLists(int depth)
    {
        FakeRadioServer server = null!;
        server = new FakeRadioServer((request, stream, ct) =>
        {
            var level = int.Parse(request.Path[1..]);
            return level < depth
                ? stream.RespondAsync(HttpOk, FakeRadio.Latin1($"#EXTM3U\n{server.Url($"/{level + 1}")}\n"), ct)
                : Station()(request, stream, ct);
        });
        return server;
    }

    [Fact]
    public async Task ThreeNestedListsPlay()
    {
        await using var server = NestedLists(3);

        using var opened = await OpenAsync(server.Url("/0"));

        Assert.Equal(server.Url("/3"), opened.FinalUrl);
    }

    [Fact]
    public async Task FourNestedListsAreTooDeep()
    {
        await using var server = NestedLists(4);

        Assert.Equal(LiveFailureReason.PlaylistTooDeep, (await FailureOfAsync(server.Url("/0"))).Reason);
    }

    [Fact]
    public async Task EmptyListIsReported()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(HttpOk, "[playlist]\nNumberOfEntries=0\n"));

        Assert.Equal(LiveFailureReason.PlaylistEmpty, (await FailureOfAsync(server.Url())).Reason);
    }

    [Fact]
    public async Task HugeListIsCutOff()
    {
        var line = FakeRadio.Latin1("#EXTINF:-1,xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n");
        const int total = 10 * 1024 * 1024;
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync(HttpOk + "\r\n#EXTM3U\n", ct);
            for (var sent = 0; sent < total; sent += line.Length)
                await stream.WriteAsync(line, ct);
        });

        var failure = await FailureOfAsync(server.Url());

        Assert.Equal(LiveFailureReason.PlaylistTooLarge, failure.Reason);
        Assert.True(server.BytesSent < total, $"wysłano {server.BytesSent} B");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("16")]
    [InlineData("100000")]
    [InlineData("abc")]
    public async Task BadMetaIntReconnectsWithoutMetadataHeader(string value)
    {
        var audio = FakeRadio.Mp3Frames(8);
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync($"{FakeRadio.IcyOk}icy-metaint:{value}\r\n", audio, ct);
            await FakeRadio.HoldAsync(stream, ct);
        });

        using var opened = await OpenAsync(server.Url());

        Assert.Equal(2, server.ConnectionCount);
        Assert.True(server.Requests[0].WantsMetadata);
        Assert.False(server.Requests[1].WantsMetadata);
        Assert.Null(opened.Demuxer);
        Assert.Equal(audio.AsSpan(0, opened.Head.Length).ToArray(), opened.Head);
    }

    [Fact]
    public async Task ConflictingMetaIntIsDistrusted()
    {
        await using var server = new FakeRadioServer(Station($"{FakeRadio.IcyOk}icy-metaint:8192\r\nicy-metaint:4096\r\n"));

        using var opened = await OpenAsync(server.Url());

        Assert.Equal(2, server.ConnectionCount);
        Assert.Null(opened.Demuxer);
    }

    [Fact]
    public async Task HeadIsFreeOfMetadataAndKeepsBlocks()
    {
        const int metaInt = 256;
        var audio = FakeRadio.Mp3Frames(8);
        var stream = FakeRadio.WithMetadata(audio, metaInt, i => i == 0 ? FakeRadio.Title("Pierwszy") : []);
        await using var server = new FakeRadioServer(async (_, s, ct) =>
        {
            await s.RespondAsync($"{FakeRadio.IcyOk}icy-metaint:{metaInt}\r\n", stream, ct);
            await FakeRadio.HoldAsync(s, ct);
        });

        using var opened = await OpenAsync(server.Url());

        Assert.Equal(metaInt, opened.MetaInt);
        Assert.NotNull(opened.Demuxer);
        Assert.Equal(audio.AsSpan(0, opened.Head.Length).ToArray(), opened.Head);
        var first = Assert.Single(opened.HeadMetadata);
        Assert.Equal(metaInt, first.Offset);
        Assert.StartsWith("StreamTitle='Pierwszy'", Encoding.ASCII.GetString(first.Block));
    }

    [Fact]
    public async Task ChunkedStreamWithMetadataIsDemuxedAfterDechunking()
    {
        const int metaInt = 256;
        var audio = FakeRadio.Mp3Frames(8);
        var stream = FakeRadio.WithMetadata(audio, metaInt, _ => FakeRadio.Title("Tytul"));
        await using var server = new FakeRadioServer(async (_, s, ct) =>
        {
            await s.WriteAsync($"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nicy-metaint:{metaInt}\r\n\r\n", ct);
            for (var at = 0; at < stream.Length; at += 100)
            {
                var count = Math.Min(100, stream.Length - at);
                await s.WriteAsync($"{count:x}\r\n", ct);
                await s.WriteAsync(stream.AsMemory(at, count), ct);
                await s.WriteAsync("\r\n", ct);
            }

            await FakeRadio.HoldAsync(s, ct);
        });

        using var opened = await OpenAsync(server.Url());

        Assert.Equal(audio.AsSpan(0, opened.Head.Length).ToArray(), opened.Head);
        Assert.NotEmpty(opened.HeadMetadata);
    }

    [Fact]
    public async Task UntrustedCertificateFailsAsTlsError()
    {
        using var certificates = new TestCertificates();
        await using var server = new FakeRadioServer(Station(), certificate: certificates.Server);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => OpenAsync(server.Url()));

        Assert.Equal(HttpRequestError.SecureConnectionError, failure.HttpRequestError);
        Assert.Empty(server.Requests);
    }

    /// <summary>Podmieniona odpowiedź po HTTP nie może podsunąć listy, nawet z adresem HTTPS.</summary>
    [Fact]
    public async Task HttpEntryOnHttpsListRedirectingToAnotherHttpsListIsRefused()
    {
        using var certificates = new TestCertificates();
        await using var foreign = new FakeRadioServer(
            FakeRadio.Respond(HttpOk, "#EXTM3U\nhttp://127.0.0.1:1/\n"), certificate: certificates.Server);
        await using var plain = new FakeRadioServer(FakeRadio.Respond(Redirect(foreign.Url())));
        await using var list = new FakeRadioServer(
            FakeRadio.Respond(HttpOk, $"#EXTM3U\n{plain.Url()}\n"), certificate: certificates.Server);

        var failure = await FailureOfAsync(list.Url(), Base with { TrustedRootForTests = certificates.Root });

        Assert.Equal(LiveFailureReason.InsecureDowngrade, failure.Reason);
    }

    /// <summary>Druga próba po niewiarygodnym metaint to dalej przejście: po adresie publicznym lokalny jest zakazany.</summary>
    [Fact]
    public async Task SecondFetchAfterBadMetaIntDoesNotReachALocalAddress()
    {
        await using var server = new FakeRadioServer(Station($"{FakeRadio.IcyOk}icy-metaint:1\r\n"));
        var resolved = 0;
        var options = Base with
        {
            IsLocalAddress = address => address.Equals(System.Net.IPAddress.Parse("127.0.0.2")),
            ResolveHost = (_, _) => Task.FromResult(new[]
            {
                System.Net.IPAddress.Parse(Interlocked.Increment(ref resolved) == 1 ? "127.0.0.1" : "127.0.0.2"),
            }),
        };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(new Uri($"http://stacja.test:{server.Port}/"), options));

        Assert.Equal(LiveFailureReason.AddressRefused, LiveErrors.Classify(failure).Failure.Reason);
        Assert.Equal(1, server.ConnectionCount);
    }

    /// <summary>Przez pośrednika nie widać adresu stacji; nazwa liczy się jako publiczna, więc do routera się nie przejdzie.</summary>
    [Fact]
    public async Task StationBehindAProxyCannotRedirectToALocalAddress()
    {
        await using var proxy = new FakeRadioServer(FakeRadio.Respond(Redirect(new Uri("http://192.168.1.1/router"))));
        var options = Base with { Proxy = new System.Net.WebProxy(proxy.Url("/")) };

        var failure = await FailureOfAsync(new Uri("http://stacja.test/"), options);

        Assert.Equal(LiveFailureReason.AddressRefused, failure.Reason);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task ListOfSilentEntriesEndsAtTheOpeningLimit()
    {
        await using var silent = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync(FakeRadio.IcyOk + "\r\n", ct);
            await FakeRadio.HoldAsync(stream, ct);
        });
        var entries = string.Concat(Enumerable.Range(0, 32).Select(i => $"{silent.Url($"/{i}")}\n"));
        await using var list = new FakeRadioServer(FakeRadio.Respond(HttpOk, "#EXTM3U\n" + entries));
        var options = Base with { StallTimeout = TimeSpan.FromMilliseconds(300), OpenTimeout = TimeSpan.FromSeconds(1.5) };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var failure = await FailureOfAsync(list.Url(), options);

        Assert.Equal(LiveFailureReason.ConnectionFailed, failure.Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"trwało {clock.Elapsed}");
    }

    /// <summary>Limit dotyczy otwierania; strumień otwarty czyta się dalej bez względu na niego.</summary>
    [Fact]
    public async Task OpenedStreamOutlivesTheOpeningLimit()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(8), ct);
            await Task.Delay(300, ct);
            await stream.WriteAsync(FakeRadio.Mp3Frames(1), ct);
            await FakeRadio.HoldAsync(stream, ct);
        });

        using var opened = await OpenAsync(server.Url(), Base with { OpenTimeout = TimeSpan.FromMilliseconds(100) });

        var read = await opened.Body.ReadAsync(new byte[1024]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(read > 0);
    }

    private static byte[] Id3Tag(int size)
    {
        var tag = new byte[10 + size];
        "ID3"u8.CopyTo(tag);
        tag[3] = 3;
        tag[6] = (byte)((size >> 21) & 0x7F);
        tag[7] = (byte)((size >> 14) & 0x7F);
        tag[8] = (byte)((size >> 7) & 0x7F);
        tag[9] = (byte)(size & 0x7F);
        return tag;
    }

    /// <summary>Okładka w znaczniku bywa większa od okna rozpoznania; dekoder i tak ją pominie.</summary>
    [Fact]
    public async Task Id3TagLargerThanTheSniffWindowIsSkipped()
    {
        var body = TestAudioFiles.Concat(Id3Tag(20 * 1024), FakeRadio.Mp3Frames(8));
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync(FakeRadio.IcyOk, body, ct);
            await FakeRadio.HoldAsync(stream, ct);
        });

        using var opened = await OpenAsync(server.Url());

        Assert.Equal(LiveCodec.Mp3, opened.Codec);
    }

    [Fact]
    public async Task Id3TagBeyondTheLimitIsUnrecognised()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync(FakeRadio.IcyOk, Id3Tag(600 * 1024), ct);
            await FakeRadio.HoldAsync(stream, ct);
        });

        Assert.Equal(LiveFailureReason.UnrecognisedContent, (await FailureOfAsync(server.Url())).Reason);
        Assert.Equal(1, server.ConnectionCount);
    }

    // ---------- brakujący certyfikat pośredni ----------
    //
    // Sprawdzane bez uzgadniania TLS: serwer na Schannel sam pobiera wystawcę i zaciemnia liczbę żądań.

    /// <summary>Do testów 127.0.0.1 uchodzi za publiczny, a lokalny jest tylko 127.0.0.2.</summary>
    private static bool OnlySecondLoopbackIsLocal(System.Net.IPAddress address) =>
        address.Equals(System.Net.IPAddress.Parse("127.0.0.2"));

    private static (FakeRadioServer Issuer, TestCertificates Certificates) Issuer(
        string address, Func<TestCertificates, Func<FakeRequest, Stream, CancellationToken, Task>> script)
    {
        TestCertificates? certificates = null;
        var issuer = new FakeRadioServer((request, stream, ct) => script(certificates!)(request, stream, ct), address);
        certificates = new TestCertificates(issuer.Url("/wystawca.crt"));
        return (issuer, certificates);
    }

    private static Func<FakeRequest, Stream, CancellationToken, Task> ServesIntermediate(TestCertificates certificates) =>
        FakeRadio.Respond("HTTP/1.1 200 OK\r\nContent-Type: application/pkix-cert\r\nConnection: close\r\n", certificates.Intermediate!.RawData);

    private static bool Validate(TestCertificates certificates, LiveOptions? options = null, params X509Certificate2[] offered)
    {
        using var http = new StreamHttp((options ?? Base) with
        {
            TrustedRootForTests = certificates.Root,
            IsLocalAddress = OnlySecondLoopbackIsLocal,
        });
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.AddRange(offered);
        return http.ValidateCertificate(new object(), certificates.Server, chain, SslPolicyErrors.RemoteCertificateChainErrors);
    }

    /// <summary>Niepełny łańcuch to częsty błąd konfiguracji stacji: wystawcę pobiera się własną drogą, przez regułę adresów.</summary>
    [Fact]
    public async Task MissingIntermediateIsFetchedByTheStreamClient()
    {
        var (issuer, certificates) = Issuer("127.0.0.1", ServesIntermediate);
        await using (issuer)
        using (certificates)
        {
            Assert.True(Validate(certificates));

            var request = Assert.Single(issuer.Requests);
            Assert.Equal("/wystawca.crt", request.Path);
            Assert.Equal(Base.UserAgent, request.Headers["User-Agent"]);
        }
    }

    [Fact]
    public async Task IntermediateSentByTheServerNeedsNoFetch()
    {
        var (issuer, certificates) = Issuer("127.0.0.1", ServesIntermediate);
        await using (issuer)
        using (certificates)
        {
            Assert.True(Validate(certificates, null, certificates.Intermediate!));

            Assert.Equal(0, issuer.ConnectionCount);
        }
    }

    /// <summary>Adres wystawcy wybiera serwer, więc reguła adresów obowiązuje zawsze, także gdy adres stacji może być lokalny.</summary>
    [Fact]
    public async Task IssuerOnALocalAddressIsNotContacted()
    {
        var (issuer, certificates) = Issuer("127.0.0.2", ServesIntermediate);
        await using (issuer)
        using (certificates)
        {
            Assert.False(Validate(certificates));

            Assert.Equal(0, issuer.ConnectionCount);
        }
    }

    /// <summary>Wydawcy certyfikatów przenoszą pliki, a bez pobrania systemowego nikt inny nie pójdzie za przekierowaniem.</summary>
    [Fact]
    public async Task IssuerRedirectIsFollowed()
    {
        TestCertificates? held = null;
        await using var target = new FakeRadioServer((request, stream, ct) => ServesIntermediate(held!)(request, stream, ct));
        var (issuer, certificates) = Issuer("127.0.0.1", _ => FakeRadio.Respond(Redirect(target.Url("/cel.crt"))));
        held = certificates;
        await using (issuer)
        using (certificates)
        {
            Assert.True(Validate(certificates));

            Assert.Equal("/cel.crt", Assert.Single(target.Requests).Path);
        }
    }

    [Fact]
    public async Task IssuerRedirectToALocalAddressIsNotContacted()
    {
        await using var local = new FakeRadioServer(FakeRadio.Respond(HttpOk), "127.0.0.2");
        var (issuer, certificates) = Issuer("127.0.0.1", _ => FakeRadio.Respond(Redirect(local.Url("/cel.crt"))));
        await using (issuer)
        using (certificates)
        {
            Assert.False(Validate(certificates));

            Assert.Equal(0, local.ConnectionCount);
        }
    }

    [Fact]
    public async Task IssuerRedirectLoopEndsAfterTheLimit()
    {
        Uri? self = null;
        var (issuer, certificates) = Issuer("127.0.0.1", _ => FakeRadio.Respond(Redirect(self!)));
        self = issuer.Url("/wystawca.crt");
        await using (issuer)
        using (certificates)
        {
            Assert.False(Validate(certificates));

            Assert.Equal(4, issuer.Requests.Count);
        }
    }

    [Fact]
    public async Task IssuerAnsweringWithGarbageLeavesTheChainIncomplete()
    {
        var (issuer, certificates) = Issuer("127.0.0.1", _ => FakeRadio.Respond(HttpOk, "to nie jest certyfikat"));
        await using (issuer)
        using (certificates)
        {
            Assert.False(Validate(certificates));

            Assert.Single(issuer.Requests);
        }
    }

    /// <summary>Wywołanie zwrotne wstrzymuje uzgadnianie, więc milczący wystawca nie może go trzymać dłużej niż limit.</summary>
    [Fact]
    public async Task SilentIssuerEndsWithinItsLimit()
    {
        var (issuer, certificates) = Issuer("127.0.0.1", _ => (_, stream, ct) => FakeRadio.HoldAsync(stream, ct));
        await using (issuer)
        using (certificates)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            Assert.False(Validate(certificates, Base with { IssuerFetchTimeout = TimeSpan.FromMilliseconds(200) }));

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"trwało {clock.Elapsed}");
        }
    }

    /// <summary>Pobranie przez system ominęłoby regułę adresów; łańcuch dopełnia wyłącznie klient strumienia.</summary>
    [Fact]
    public void SystemDoesNotDownloadCertificates()
    {
        var policy = StreamHttp.ChainPolicyWithoutDownloads();

        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
    }

    /// <summary>Logo idzie wyłącznie po HTTPS, więc żądanie po przekierowaniu na HTTP nie może wyjść jawnym tekstem.</summary>
    [Fact]
    public async Task ImageRedirectToHttpIsRefusedBeforeTheRequestIsSent()
    {
        using var certificates = new TestCertificates();
        await using var plain = new FakeRadioServer(FakeRadio.Respond(HttpOk));
        await using var secure = new FakeRadioServer(FakeRadio.Respond(Redirect(plain.Url())), certificate: certificates.Server);
        var options = Base with { TrustedRootForTests = certificates.Root };
        using var http = new StreamHttp(options);
        var opener = new StreamOpener(http, new AddressPolicy(options), options);

        var failure = await Assert.ThrowsAsync<LiveOpenException>(
            () => opener.FetchImageAsync(secure.Url(), true, CancellationToken.None));

        Assert.Equal(LiveFailureReason.InsecureDowngrade, failure.Failure.Reason);
        Assert.Equal(0, plain.ConnectionCount);
    }

    /// <summary>Przejściowy błąd wpisu może minąć, więc adres nie wypada z obiegu, choć inny wpis zniknął na stałe.</summary>
    [Theory]
    [InlineData(404, 503)]
    [InlineData(503, 404)]
    public async Task ListWithAnyTransientEntryFailureIsRetried(int firstStatus, int secondStatus)
    {
        await using var entries = new FakeRadioServer((request, stream, ct) => stream.WriteAsync(
            $"HTTP/1.1 {(request.Path == "/a" ? firstStatus : secondStatus)} Status\r\nContent-Length: 0\r\n\r\n", ct));
        await using var list = new FakeRadioServer(
            FakeRadio.Respond(HttpOk, $"#EXTM3U\n{entries.Url("/a")}\n{entries.Url("/b")}\n"));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(list.Url()));

        var (classified, permanent) = LiveErrors.Classify(failure);
        Assert.False(permanent);
        Assert.Equal(LiveFailureReason.ServerError, classified.Reason);
    }

    [Fact]
    public async Task ListWithOnlyPermanentEntryFailuresIsPermanentWithTheFirstReason()
    {
        await using var entries = new FakeRadioServer((request, stream, ct) => stream.WriteAsync(
            $"HTTP/1.1 {(request.Path == "/a" ? 404 : 403)} Status\r\nContent-Length: 0\r\n\r\n", ct));
        await using var list = new FakeRadioServer(
            FakeRadio.Respond(HttpOk, $"#EXTM3U\n{entries.Url("/a")}\n{entries.Url("/b")}\n"));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(list.Url()));

        var (classified, permanent) = LiveErrors.Classify(failure);
        Assert.True(permanent);
        Assert.Equal(LiveFailureReason.NotFound, classified.Reason);
    }

    [Fact]
    public async Task NonHttpSchemeIsRefused()
    {
        Assert.Equal(LiveFailureReason.UrlBlocked, (await FailureOfAsync(new Uri("mms://stacja.test/a"))).Reason);
    }
}
