using System.Collections.Concurrent;
using System.Text;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming.Hls;

/// <summary>Od playlisty głównej do bajtów dźwięku, na prawdziwych połączeniach z atrapą serwera.</summary>
public sealed class HlsStreamTests
{
    private const string Ok = "HTTP/1.1 200 OK\r\nConnection: close\r\n";
    private const string NotFound = "HTTP/1.1 404 Not Found\r\nConnection: close\r\n";

    private static readonly LiveOptions Options = new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        StallTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Atrapa: adres, odpowiedź; wszystkie żądania zapisane w kolejności.</summary>
    private sealed class Site : IAsyncDisposable
    {
        private readonly FakeRadioServer _server;

        public Site(Func<string, (string Head, byte[] Body)> respond)
        {
            _server = new FakeRadioServer((request, stream, ct) =>
            {
                Paths.Enqueue(request.Path);
                var (head, body) = respond(request.Path);
                return stream.RespondAsync(head, body, ct);
            });
        }

        public ConcurrentQueue<string> Paths { get; } = new();

        public Uri Url(string path) => _server.Url(path);

        public ValueTask DisposeAsync() => _server.DisposeAsync();
    }

    private static (string, byte[]) Text(string text) => (Ok, Encoding.UTF8.GetBytes(text));

    /// <summary>Ramki ADTS z tonu: każdy segment ma własne, rozpoznawalne bajty.</summary>
    private static byte[] Adts(int count, int first = 0)
    {
        var tone = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", "ton-440-stereo-44100.aac"));
        var at = 0;
        var index = 0;
        var result = new List<byte>();

        while (at < tone.Length && index < first + count)
        {
            Assert.True(Cewka.Audio.Decoding.Live.AdtsHeader.TryParse(tone.AsSpan(at), out var header));
            if (index >= first) result.AddRange(tone.AsSpan(at, header.FrameLength));
            at += header.FrameLength;
            index++;
        }

        return [.. result];
    }

    private static async Task<OpenedStream> OpenAsync(Uri url, LiveOptions? options = null)
    {
        options ??= Options;
        var http = new StreamHttp(options);
        return await new StreamOpener(http, new AddressPolicy(options), options).OpenAsync(url, true, CancellationToken.None);
    }

    private static async Task<LiveFailure> FailureOfAsync(Uri url, LiveOptions? options = null) =>
        (await Assert.ThrowsAsync<LiveOpenException>(() => OpenAsync(url, options))).Failure;

    /// <summary>Głowa i treść, aż będzie tyle bajtów albo strumień się skończy.</summary>
    private static async Task<byte[]> ReadAsync(OpenedStream opened, int count)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var all = new List<byte>(opened.Head);
        var chunk = new byte[4096];

        while (all.Count < count)
        {
            var read = await opened.Body.ReadAsync(chunk, limit.Token);
            if (read == 0) break;
            all.AddRange(chunk.AsSpan(0, read));
        }

        return [.. all];
    }

    [Fact]
    public async Task MasterLeadsToTheAudioVariantAndVideoIsNeverFetched()
    {
        var audio = Adts(20);

        await using var site = new Site(path => path switch
        {
            "/master.m3u8" => Text(
                "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360\nvideo.m3u8\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.2\"\naudio.m3u8\n"),
            "/audio.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n"),
            "/s.ts" => (Ok, TsTestFile.Mux(audio)),
            _ => (NotFound, []),
        });

        using var opened = await OpenAsync(site.Url("/master.m3u8"));

        Assert.Equal(LiveCodec.Aac, opened.Codec);
        Assert.Equal(64, opened.Headers.BitrateKbps);
        Assert.Equal(site.Url("/audio.m3u8"), opened.FinalUrl);
        Assert.Equal(audio, await ReadAsync(opened, int.MaxValue));
        Assert.Equal(["/master.m3u8", "/audio.m3u8", "/s.ts"], site.Paths);
    }

    [Fact]
    public async Task PlaylistOfAStationWithoutMasterPlaysDirectly()
    {
        var audio = Adts(20);

        await using var site = new Site(path => path switch
        {
            "/audio.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n"),
            _ => (Ok, TsTestFile.Mux(audio)),
        });

        using var opened = await OpenAsync(site.Url("/audio.m3u8"));

        Assert.Equal(audio, await ReadAsync(opened, int.MaxValue));
    }

    [Fact]
    public async Task EverySegmentOfAGrowingPlaylistPlaysOnceAndInOrder()
    {
        byte[][] segments = [Adts(10), Adts(10, 10), Adts(10, 20), Adts(10, 30)];
        var reloads = 0;

        await using var site = new Site(path =>
        {
            if (path == "/live.m3u8")
            {
                var last = Math.Min(Interlocked.Increment(ref reloads), 3);
                var first = Math.Max(0, last - 1);

                var text = $"#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:{first}\n";
                for (var i = first; i <= last; i++) text += $"#EXTINF:1,\ns{i}.ts\n";
                return Text(text);
            }

            return (Ok, TsTestFile.Mux(segments[path[2] - '0']));
        });

        using var opened = await OpenAsync(site.Url("/live.m3u8"));

        byte[] expected = [.. segments[0], .. segments[1], .. segments[2], .. segments[3]];
        Assert.Equal(expected, await ReadAsync(opened, expected.Length));
    }

    [Fact]
    public async Task LivePlaylistStartsNearItsEnd()
    {
        var audio = Adts(10);

        await using var site = new Site(path =>
        {
            if (path != "/live.m3u8") return (Ok, TsTestFile.Mux(audio));

            var text = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:100\n";
            for (var i = 0; i < 10; i++) text += $"#EXTINF:2,\ns{i}.ts\n";
            return Text(text);
        });

        using var opened = await OpenAsync(site.Url("/live.m3u8"));
        await ReadAsync(opened, audio.Length * 3);

        Assert.Equal(["/live.m3u8", "/s7.ts", "/s8.ts", "/s9.ts"], site.Paths);
    }

    [Fact]
    public async Task LongTargetDurationIsNotAStall()
    {
        await using var site = new Site(path => path == "/live.m3u8"
            ? Text("#EXTM3U\n#EXT-X-TARGETDURATION:30\n#EXTINF:30,\ns.ts\n")
            : (Ok, TsTestFile.Mux(Adts(10))));

        using var opened = await OpenAsync(site.Url("/live.m3u8"));

        Assert.Equal(TimeSpan.FromSeconds(90), opened.StallTimeout);
    }

    [Fact]
    public async Task PackedAudioLosesItsTagInEverySegment()
    {
        var tag = new byte[10 + 300];
        "ID3"u8.CopyTo(tag);
        tag[3] = 4;
        tag[8] = 2;
        tag[9] = 44;

        byte[][] parts = [Adts(10), Adts(10, 10)];

        await using var site = new Site(path => path switch
        {
            "/live.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n0.aac\n#EXTINF:2,\n1.aac\n#EXT-X-ENDLIST\n"),
            _ => (Ok, [.. tag, .. parts[path[1] - '0']]),
        });

        using var opened = await OpenAsync(site.Url("/live.m3u8"));

        Assert.Equal(LiveCodec.Aac, opened.Codec);
        byte[] expected = [.. parts[0], .. parts[1]];
        Assert.Equal(expected, await ReadAsync(opened, int.MaxValue));
    }

    [Fact]
    public async Task EncryptedPlaylistIsRefusedBeforeAnySegment()
    {
        await using var site = new Site(_ =>
            Text("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns.ts\n"));

        var failure = await FailureOfAsync(site.Url("/live.m3u8"));

        Assert.Equal(LiveFailureReason.HlsNotSupportedYet, failure.Reason);
        Assert.Single(site.Paths);
    }

    [Fact]
    public async Task FragmentedMp4SegmentIsRecognisedFromItsBytes()
    {
        byte[] box = [0, 0, 0, 24, .. "ftyp"u8.ToArray(), .. new byte[64]];

        await using var site = new Site(path => path == "/live.m3u8"
            ? Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns.m4s\n")
            : (Ok, box));

        Assert.Equal(LiveFailureReason.HlsNotSupportedYet, (await FailureOfAsync(site.Url("/live.m3u8"))).Reason);
    }

    [Fact]
    public async Task PlaylistWithoutSegmentsIsEmpty()
    {
        await using var site = new Site(_ => Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n"));

        Assert.Equal(LiveFailureReason.PlaylistEmpty, (await FailureOfAsync(site.Url("/live.m3u8"))).Reason);
    }

    [Fact]
    public async Task MasterPointingAtAnotherMasterEnds()
    {
        await using var site = new Site(_ => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000\nmaster.m3u8\n"));

        Assert.Equal(LiveFailureReason.PlaylistTooDeep, (await FailureOfAsync(site.Url("/master.m3u8"))).Reason);
    }

    [Fact]
    public async Task MissingVariantHasItsReason()
    {
        await using var site = new Site(path => path == "/master.m3u8"
            ? Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000\nbrak.m3u8\n")
            : (NotFound, []));

        Assert.Equal(LiveFailureReason.NotFound, (await FailureOfAsync(site.Url("/master.m3u8"))).Reason);
    }

    [Fact]
    public async Task BlockedSegmentAddressStaysBlocked()
    {
        var options = Options with
        {
            UrlGuard = url => url.AbsolutePath.EndsWith(".ts", StringComparison.Ordinal) ? LiveFailureReason.UrlBlocked : null,
        };

        await using var site = new Site(_ => Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns.ts\n"));

        Assert.Equal(LiveFailureReason.UrlBlocked, (await FailureOfAsync(site.Url("/live.m3u8"), options)).Reason);
    }

    /// <summary>Lista lokalna wpisana przez użytkownika prowadzi na adres publiczny, a ten nie może wrócić do sieci lokalnej.</summary>
    [Fact]
    public async Task PublicVariantCannotReachALocalSegment()
    {
        var publicPort = 0;

        await using var local = new FakeRadioServer(
            (request, stream, ct) => request.Path == "/master.m3u8"
                ? stream.RespondAsync(Ok, FakeRadio.Latin1($"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000\nhttp://publiczny.test:{publicPort}/audio.m3u8\n"), ct)
                : stream.RespondAsync(Ok, TsTestFile.Mux(Adts(20)), ct),
            address: "127.0.0.2");

        await using var variant = new FakeRadioServer((_, stream, ct) => stream.RespondAsync(
            Ok, FakeRadio.Latin1($"#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nhttp://lokalny.test:{local.Port}/s.ts\n"), ct));

        publicPort = variant.Port;

        var options = Options with
        {
            IsLocalAddress = address => address.Equals(System.Net.IPAddress.Parse("127.0.0.2")),
            ResolveHost = (host, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse(host == "lokalny.test" ? "127.0.0.2" : "127.0.0.1") }),
        };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(new Uri($"http://lokalny.test:{local.Port}/master.m3u8"), options));

        Assert.Equal(LiveFailureReason.AddressRefused, LiveErrors.Classify(failure).Failure.Reason);
        Assert.Equal(1, local.ConnectionCount);
    }

    [Fact]
    public async Task SegmentThatVanishedDuringPlayIsATransientFailure()
    {
        var audio = Adts(20);

        await using var site = new Site(path => path switch
        {
            "/live.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns0.ts\n#EXTINF:4,\ns1.ts\n"),
            "/s0.ts" => (Ok, TsTestFile.Mux(audio)),
            _ => (NotFound, []),
        });

        using var opened = await OpenAsync(site.Url("/live.m3u8"));

        var failure = await Assert.ThrowsAsync<LiveOpenException>(() => ReadAsync(opened, int.MaxValue));

        Assert.True(failure.Transient);
        Assert.Equal(LiveFailureReason.NotFound, failure.Failure.Reason);
    }

    [Fact]
    public async Task ReloadThatFailsIsATransientFailure()
    {
        var loads = 0;

        await using var site = new Site(path =>
        {
            if (path != "/live.m3u8") return (Ok, TsTestFile.Mux(Adts(20)));

            return Interlocked.Increment(ref loads) == 1
                ? Text("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\ns.ts\n")
                : ("HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n", []);
        });

        using var opened = await OpenAsync(site.Url("/live.m3u8"));

        var failure = await Assert.ThrowsAsync<LiveOpenException>(() => ReadAsync(opened, int.MaxValue));

        Assert.True(failure.Transient);
    }

    [Fact]
    public async Task SegmentWithOnlyVideoEndsWithoutAudio()
    {
        List<byte> video = [.. TsTestFile.Pat(), .. TsTestFile.Pmt(0x1B)];

        await using var site = new Site(path => path == "/live.m3u8"
            ? Text("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\ns.ts\n#EXT-X-ENDLIST\n")
            : (Ok, [.. video]));

        var failure = await FailureOfAsync(site.Url("/live.m3u8"));

        Assert.Equal(LiveFailureReason.StreamEnded, failure.Reason);
    }

    [Fact]
    public async Task HttpListBehindHttpsListIsRefused()
    {
        using var certificates = new TestCertificates();
        await using var plain = new Site(_ => Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns.ts\n"));
        await using var master = new FakeRadioServer(
            FakeRadio.Respond(Ok, $"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000\n{plain.Url("/audio.m3u8")}\n"),
            certificate: certificates.Server);

        var failure = await FailureOfAsync(master.Url("/master.m3u8"), Options with { TrustedRootForTests = certificates.Root });

        Assert.Equal(LiveFailureReason.InsecureDowngrade, failure.Reason);
    }
}
