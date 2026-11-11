using System.Diagnostics;
using System.Text;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

/// <summary>Cała sesja na atrapie stacji: stany, ponawianie, tytuły, zatrzymywanie.</summary>
public class LiveSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>Małe progi i krótkie czasy, żeby test nie czekał minut.</summary>
    private static readonly LiveOptions Fast = new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        StallTimeout = TimeSpan.FromSeconds(3),
        Backoff = [TimeSpan.FromMilliseconds(5)],
        StartSeconds = 0.05,
        RebufferSeconds = 0.1,
        MinStartBytes = 800,
        MinRebufferBytes = 1600,
    };

    private sealed class Rig : IAsyncDisposable
    {
        private readonly object _lock = new();
        private readonly List<LiveState> _states = [];

        public Rig(LiveSource source, LiveOptions? options = null)
        {
            Session = new LiveSession(source, options ?? Fast);
            Session.StateChanged += s =>
            {
                lock (_lock)
                    _states.Add(s);
            };
        }

        public LiveSession Session { get; }

        public List<LiveState> States
        {
            get { lock (_lock) return [.. _states]; }
        }

        public async Task WaitForStateAsync(LiveState state)
        {
            await Until(() => States.Contains(state), $"stan {state}; były: {string.Join(",", States)}");
        }

        public async Task<(List<byte> Bytes, List<(int Position, StreamMarker Marker)> Markers)> ReadUntilAsync(
            Func<List<byte>, List<(int, StreamMarker)>, bool> done)
        {
            var bytes = new List<byte>();
            var markers = new List<(int, StreamMarker)>();
            var scratch = new byte[4096];
            var watch = Stopwatch.StartNew();
            while (!done(bytes, markers))
            {
                Assert.True(watch.Elapsed < Patience, $"odczyt stanął po {bytes.Count} B; stany: {string.Join(",", States)}");
                var status = Session.Bytes.Read(scratch, out var count, out var marker);
                if (status == LiveReadStatus.Ended)
                    break;
                if (status == LiveReadStatus.Waiting)
                {
                    await Task.Delay(2);
                    continue;
                }

                if (marker is not null)
                    markers.Add((bytes.Count, marker));
                bytes.AddRange(scratch.AsSpan(0, count).ToArray());
            }

            return (bytes, markers);
        }

        public async ValueTask DisposeAsync()
        {
            Session.Stop();
            await Session.Completion.WaitAsync(Patience);
        }
    }

    private static async Task Until(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Patience, $"nie doczekano się: {description}");
            await Task.Delay(2);
        }
    }

    private static LiveSource Source(params Uri[] urls) => new("stacja", "Radio Testowe", urls, "PL", AllowLocalAddress: true);

    private static Func<FakeRequest, Stream, CancellationToken, Task> Playing(string head = FakeRadio.IcyOk, int frames = 16) =>
        async (_, stream, ct) =>
        {
            await stream.RespondAsync(head, FakeRadio.Mp3Frames(frames), ct);
            await FakeRadio.HoldAsync(stream, ct);
        };

    [Fact]
    public async Task StatesRunInOrderToPlayingAndStopped()
    {
        await using var server = new FakeRadioServer(Playing());
        var rig = new Rig(Source(server.Url()));
        await using (rig)
        {
            rig.Session.Start();
            await rig.WaitForStateAsync(LiveState.Playing);
            Assert.Equal(LiveState.Playing, rig.Session.State);
        }

        Assert.Equal(new[] { LiveState.Connecting, LiveState.Buffering, LiveState.Playing, LiveState.Stopped }, rig.States);
        Assert.Equal(LiveState.Stopped, rig.Session.State);
        Assert.Null(rig.Session.Failure);
    }

    [Fact]
    public async Task InfoDescribesStream()
    {
        await using var server = new FakeRadioServer(Playing($"{FakeRadio.IcyOk}icy-genre:Pop\r\nicy-metaint:8192\r\n"));
        await using var rig = new Rig(Source(server.Url()));
        LiveStreamInfo? raised = null;
        rig.Session.InfoChanged += info => raised = info;

        rig.Session.Start();
        await Until(() => raised is not null, "informacja o strumieniu");

        Assert.Equal(new LiveStreamInfo(LiveCodec.Mp3, 128, "Stacja", "Pop", server.Url(), true), raised);
        Assert.Equal(raised, rig.Session.Info);
    }

    /// <summary>Nazwa w nagłówku ma te same kodowania co tytuł; ta sama stacja nie może mieć zepsutej nazwy.</summary>
    [Theory]
    [InlineData(65001)]
    [InlineData(1250)]
    public async Task IcyNameIsDecodedLikeTitles(int codePage)
    {
        var encoding = codePage == 65001 ? Encoding.UTF8 : IcyMetadataParser.FallbackFor("PL");
        var name = FakeRadio.Latin1String(encoding.GetBytes("Radio Łódź"));
        await using var server = new FakeRadioServer(Playing($"ICY 200 OK\r\nicy-name:{name}\r\nicy-genre:{name}\r\ncontent-type:audio/mpeg\r\n"));
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await Until(() => rig.Session.Info is not null, "informacja o strumieniu");

        Assert.Equal("Radio Łódź", rig.Session.Info!.IcyName);
        Assert.Equal("Radio Łódź", rig.Session.Info.IcyGenre);
    }

    [Fact]
    public async Task CutInTheMiddleOfFrameReconnectsWithNewBegin()
    {
        var first = FakeRadio.Mp3Frames(8)[..(417 * 7 + 100)];
        var second = FakeRadio.Mp3Frames(8);
        await using var server = new FakeRadioServer(async (request, stream, ct) =>
        {
            if (request.Index == 0)
            {
                await stream.RespondAsync(FakeRadio.IcyOk, first, ct);
                return;
            }

            await stream.RespondAsync(FakeRadio.IcyOk, second, ct);
            await FakeRadio.HoldAsync(stream, ct);
        });
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        var (bytes, markers) = await rig.ReadUntilAsync((b, _) => b.Count >= first.Length + second.Length);

        Assert.Equal(new (int, StreamMarker)[]
        {
            (0, new StreamBegin(LiveCodec.Mp3)),
            (first.Length, new StreamBegin(LiveCodec.Mp3)),
        }, markers);
        Assert.Equal([.. first, .. second], bytes);
        Assert.Equal(2, server.ConnectionCount);
        var states = rig.States;
        Assert.True(states.IndexOf(LiveState.Reconnecting) > states.IndexOf(LiveState.Playing));
    }

    [Fact]
    public async Task EveryConnectionStartsFromEntryAddress()
    {
        FakeRadioServer server = null!;
        server = new FakeRadioServer(async (request, stream, ct) =>
        {
            if (request.Path == "/wejscie")
            {
                await stream.RespondAsync($"HTTP/1.1 302 Moved\r\nLocation: {server.Url("/koniec")}\r\nContent-Length: 0\r\n", [], ct);
                return;
            }

            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(4), ct);
        });
        await using var _ = server;
        await using var rig = new Rig(Source(server.Url("/wejscie")));

        rig.Session.Start();
        await Until(() => server.Requests.Count >= 4, "dwa połączenia");

        Assert.Equal(new[] { "/wejscie", "/koniec", "/wejscie", "/koniec" }, server.Requests.Take(4).Select(r => r.Path));
    }

    [Fact]
    public async Task TitleKeepsItsPlaceInBytes()
    {
        const int metaInt = 8192;
        var audio = FakeRadio.Mp3Frames(70);
        var stream = FakeRadio.WithMetadata(audio, metaInt, i => i switch
        {
            0 => FakeRadio.Title("Pierwszy"),
            2 => FakeRadio.Title("Drugi"),
            _ => [],
        });
        await using var server = new FakeRadioServer(async (_, s, ct) =>
        {
            await s.RespondAsync($"{FakeRadio.IcyOk}icy-metaint:{metaInt}\r\n", stream, ct);
            await FakeRadio.HoldAsync(s, ct);
        });
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        var (bytes, markers) = await rig.ReadUntilAsync((_, m) => m.Count >= 3);

        Assert.Equal(new (int, StreamMarker)[]
        {
            (0, new StreamBegin(LiveCodec.Mp3)),
            (metaInt, new StreamTitle("Pierwszy")),
            (metaInt * 3, new StreamTitle("Drugi")),
        }, markers);
        Assert.Equal(audio[..bytes.Count], bytes);
    }

    [Fact]
    public async Task EmptyAdvertAndRepeatedTitlesGiveNoMarkers()
    {
        const int metaInt = 8192;
        var audio = FakeRadio.Mp3Frames(100);
        var blocks = new[]
        {
            FakeRadio.Title("Utwor"), FakeRadio.Title("Utwor"), FakeRadio.Title("Radio Testowe"),
            "StreamTitle='Reklama';adw_ad='true';"u8.ToArray(), "StreamUrl='http://x/';"u8.ToArray(),
        };
        var stream = FakeRadio.WithMetadata(audio, metaInt, i => i < blocks.Length ? blocks[i] : []);
        await using var server = new FakeRadioServer(async (_, s, ct) =>
        {
            await s.RespondAsync($"{FakeRadio.IcyOk}icy-metaint:{metaInt}\r\n", stream, ct);
            await FakeRadio.HoldAsync(s, ct);
        });
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        var (_, markers) = await rig.ReadUntilAsync((b, _) => b.Count >= metaInt * 5);

        // Nazwa stacji znaczy „bez tytułu”, reklama i blok bez pola nic nie zmieniają.
        Assert.Equal(
            new StreamMarker[] { new StreamBegin(LiveCodec.Mp3), new StreamTitle("Utwor"), new StreamTitle(null) },
            markers.Select(m => m.Marker));
    }

    [Fact]
    public async Task TitleFloodGivesAtMostOneMarkerPerSecondOfAudio()
    {
        const int metaInt = 256;
        var audio = FakeRadio.Mp3Frames(160);
        var stream = FakeRadio.WithMetadata(audio, metaInt, i => FakeRadio.Title($"Tytul {i}"));
        await using var server = new FakeRadioServer(async (_, s, ct) =>
        {
            await s.RespondAsync($"{FakeRadio.IcyOk}icy-metaint:{metaInt}\r\n", stream, ct);
            await FakeRadio.HoldAsync(s, ct);
        });
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        var (bytes, markers) = await rig.ReadUntilAsync((b, _) => b.Count >= audio.Length - audio.Length % metaInt);

        var seconds = bytes.Count / 16000.0;
        var titles = markers.Where(m => m.Marker is StreamTitle).ToList();
        Assert.InRange(titles.Count, 2, (int)seconds + 2);
        for (var i = 1; i < titles.Count; i++)
            Assert.True(titles[i].Position - titles[i - 1].Position >= 15000, "znaczniki zbyt gęsto");
    }

    [Fact]
    public async Task SilenceAfterDataIsStalledAndDropsConnection()
    {
        await using var server = new FakeRadioServer(Playing());
        var options = Fast with { StallTimeout = TimeSpan.FromMilliseconds(200), MaxAttempts = 2 };
        await using var rig = new Rig(Source(server.Url()), options);

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(LiveFailureReason.Stalled, rig.Session.Failure!.Value.Reason);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task SilenceAfterHeadersIsStalled()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync(FakeRadio.IcyOk + "\r\n", ct);
            await FakeRadio.HoldAsync(stream, ct);
        });
        var options = Fast with { StallTimeout = TimeSpan.FromMilliseconds(200), MaxAttempts = 2 };
        await using var rig = new Rig(Source(server.Url()), options);

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(LiveFailureReason.Stalled, rig.Session.Failure!.Value.Reason);
    }

    [Fact]
    public async Task SilenceBeforeHeadersIsConnectionFailure()
    {
        await using var server = new FakeRadioServer((_, stream, ct) => FakeRadio.HoldAsync(stream, ct));
        var options = Fast with { ConnectTimeout = TimeSpan.FromMilliseconds(200), MaxAttempts = 2 };
        await using var rig = new Rig(Source(server.Url()), options);

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(LiveFailureReason.ConnectionFailed, rig.Session.Failure!.Value.Reason);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task EightServerFailuresGiveUpWithLastReason()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 503 Busy\r\nContent-Length: 0\r\n"));
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(8, server.ConnectionCount);
        var failure = rig.Session.Failure!.Value;
        Assert.Equal(LiveFailureReason.ServerError, failure.Reason);
        Assert.Equal(503, failure.HttpStatus);
        Assert.Equal(failure, rig.Session.Bytes.Failure);
        Assert.Equal(new[] { LiveState.Connecting, LiveState.Failed }, rig.States);
    }

    [Fact]
    public async Task NotFoundMakesExactlyOneAttempt()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("ICY 404 Not Found\r\n"));
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);
        await Task.Delay(50);

        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(LiveFailureReason.NotFound, rig.Session.Failure!.Value.Reason);
    }

    [Fact]
    public async Task HtmlInsteadOfStreamFailsWithoutRetry()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nConnection: close\r\n", "<html><body>Zaloguj</body></html>"));
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(LiveFailureReason.NotAStream, rig.Session.Failure!.Value.Reason);
    }

    [Fact]
    public async Task OggFailsWithCodecNotSupported()
    {
        var ogg = new byte[8192];
        "OggS"u8.CopyTo(ogg);
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk, ogg));
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(new LiveFailure(LiveFailureReason.CodecNotSupportedYet, Codec: LiveCodec.Ogg), rig.Session.Failure);
    }

    [Fact]
    public async Task HlsFailsWithReasonCode()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nConnection: close\r\n", "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MAP:URI=\"i.mp4\"\n"));
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(LiveFailureReason.HlsNotSupportedYet, rig.Session.Failure!.Value.Reason);
    }

    [Fact]
    public async Task BadCertificateFailsWithoutRetry()
    {
        using var certificates = new TestCertificates();
        await using var server = new FakeRadioServer(Playing(), certificate: certificates.Server);
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(LiveFailureReason.TlsFailed, rig.Session.Failure!.Value.Reason);
    }

    [Fact]
    public async Task CatalogueStationOnLocalAddressFailsWithoutConnecting()
    {
        await using var server = new FakeRadioServer(Playing());
        await using var rig = new Rig(new LiveSource("stacja", "Radio", [server.Url()]));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(0, server.ConnectionCount);
        Assert.Equal(LiveFailureReason.AddressRefused, rig.Session.Failure!.Value.Reason);
    }

    [Fact]
    public async Task CatalogueStationResolvingToLocalFailsWithoutConnecting()
    {
        await using var server = new FakeRadioServer(Playing());
        var options = Fast with { ResolveHost = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Loopback }) };
        await using var rig = new Rig(new LiveSource("stacja", "Radio", [new Uri($"http://stacja.test:{server.Port}/")]), options);

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(0, server.ConnectionCount);
        Assert.Equal(LiveFailureReason.AddressRefused, rig.Session.Failure!.Value.Reason);
    }

    [Fact]
    public async Task ForbiddenFallsThroughToSpareAddress()
    {
        await using var refused = new FakeRadioServer(FakeRadio.Respond("ICY 403 Forbidden\r\n"));
        await using var spare = new FakeRadioServer(Playing());
        await using var rig = new Rig(Source(refused.Url(), spare.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Playing);

        Assert.Equal(1, refused.ConnectionCount);
        Assert.Equal(1, spare.ConnectionCount);
    }

    [Fact]
    public async Task AllAddressesRefusedGiveReasonOfFirst()
    {
        await using var a = new FakeRadioServer(FakeRadio.Respond("ICY 401 Auth\r\n"));
        await using var b = new FakeRadioServer(FakeRadio.Respond("ICY 403 Forbidden\r\n"));
        await using var rig = new Rig(Source(a.Url(), b.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(LiveFailureReason.Unauthorized, rig.Session.Failure!.Value.Reason);
    }

    [Fact]
    public async Task BufferedDataIsReadableAfterFailure()
    {
        await using var server = new FakeRadioServer(async (request, stream, ct) =>
        {
            if (request.Index == 0)
                await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(4), ct);
            else
                await stream.WriteAsync("ICY 404 Gone\r\n\r\n", ct);
        });
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);
        var (bytes, _) = await rig.ReadUntilAsync((_, _) => false);

        Assert.Equal(417 * 4, bytes.Count);
        Assert.Equal(LiveFailureReason.NotFound, rig.Session.Bytes.Failure!.Value.Reason);
    }

    private static async Task<double> MedianStopMilliseconds(Func<LiveSession> create, Func<LiveSession, Task> connecting)
    {
        var results = new List<double>();
        for (var round = 0; round < 5; round++)
        {
            var session = create();
            session.Start();
            await connecting(session);

            var watch = Stopwatch.StartNew();
            session.Stop();
            await session.Completion.WaitAsync(Patience);
            results.Add(watch.Elapsed.TotalMilliseconds);
        }

        results.Sort();
        return results[2];
    }

    [Fact]
    public async Task StopWhileResolvingNameCompletesQuickly()
    {
        var waiting = 0;
        var options = Fast with
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ResolveHost = async (_, ct) =>
            {
                Interlocked.Increment(ref waiting);
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            },
        };
        var entered = 0;

        var median = await MedianStopMilliseconds(
            () => new LiveSession(Source(new Uri("http://stacja.test/")), options),
            async _ =>
            {
                entered++;
                await Until(() => Volatile.Read(ref waiting) == entered, "rozwiązywanie nazwy");
            });

        Assert.True(median < 100, $"mediana {median:F1} ms");
    }

    [Fact]
    public async Task StopWhileServerIsSilentCompletesQuickly()
    {
        await using var server = new FakeRadioServer((_, stream, ct) => FakeRadio.HoldAsync(stream, ct));
        var options = Fast with { ConnectTimeout = TimeSpan.FromSeconds(30) };
        var entered = 0;

        var median = await MedianStopMilliseconds(
            () => new LiveSession(Source(server.Url()), options),
            async _ =>
            {
                entered++;
                await Until(() => server.Requests.Count == entered, "żądanie");
            });

        Assert.True(median < 100, $"mediana {median:F1} ms");
    }

    [Fact]
    public async Task StopWhileReadingCompletesQuickly()
    {
        await using var server = new FakeRadioServer(Playing());
        var options = Fast with { StallTimeout = TimeSpan.FromSeconds(30) };

        var median = await MedianStopMilliseconds(
            () => new LiveSession(Source(server.Url()), options),
            session => Until(() => session.State == LiveState.Playing, "granie"));

        Assert.True(median < 100, $"mediana {median:F1} ms");
    }

    [Fact]
    public async Task StopReturnsAtOnceAndEndsBytesWithoutFailure()
    {
        await using var server = new FakeRadioServer(Playing());
        var session = new LiveSession(Source(server.Url()), Fast);
        session.Start();
        await Until(() => session.State == LiveState.Playing, "granie");

        var watch = Stopwatch.StartNew();
        session.Stop();
        Assert.True(watch.ElapsedMilliseconds < 50, $"{watch.ElapsedMilliseconds} ms");
        await session.Completion.WaitAsync(Patience);

        var scratch = new byte[64 * 1024];
        while (session.Bytes.Read(scratch, out _, out _) == LiveReadStatus.Data)
        {
        }

        Assert.Equal(LiveReadStatus.Ended, session.Bytes.Read(scratch, out _, out _));
        Assert.Null(session.Bytes.Failure);
    }

    [Fact]
    public async Task StopWithoutStartCompletes()
    {
        var session = new LiveSession(Source(new Uri("http://stacja.test/")), Fast);

        session.Stop();

        await session.Completion.WaitAsync(Patience);
        Assert.Equal(LiveState.Stopped, session.State);
    }

    [Fact]
    public async Task StopDuringBackoffWakesAtOnce()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 503 Busy\r\nContent-Length: 0\r\n"));
        var session = new LiveSession(Source(server.Url()), Fast with { Backoff = [TimeSpan.FromMinutes(5)] });
        session.Start();
        await Until(() => server.ConnectionCount == 1, "pierwsza próba");
        await Task.Delay(20);

        var watch = Stopwatch.StartNew();
        session.Stop();
        await session.Completion.WaitAsync(Patience);

        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>Czytelnik i sieć zgłaszają bramkę z dwóch wątków: spóźnione zamknięcie nie obowiązuje, gdy znów jest otwarta.</summary>
    [Fact]
    public async Task LateGateReportDoesNotOverrideTheGateAsItIsNow()
    {
        await using var server = new FakeRadioServer(Playing());
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Playing);
        rig.Session.OnGateChanged();

        Assert.True(((CompressedBuffer)rig.Session.Bytes).IsGateOpen);
        Assert.Equal(LiveState.Playing, rig.Session.State);
    }

    /// <summary>Serwer zamykający połączenie w trakcie uzgadniania jest przeciążony albo zmieniła się sieć; to nie zły certyfikat.</summary>
    [Fact]
    public async Task ConnectionClosedDuringTlsHandshakeIsRetried()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var accepted = 0;
        var closing = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref accepted);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await using (var rig = new Rig(Source(new Uri($"https://127.0.0.1:{port}/")), Fast with { MaxAttempts = 3 }))
        {
            rig.Session.Start();
            await rig.WaitForStateAsync(LiveState.Failed);

            Assert.Equal(3, Volatile.Read(ref accepted));
            Assert.Equal(LiveFailureReason.ConnectionFailed, rig.Session.Failure!.Value.Reason);
        }

        await stop.CancelAsync();
        await closing;
    }

    /// <summary>Serwer po restarcie podaje jedną ramkę: za mało na rozpoznanie, ale to nie powód, żeby kończyć stację.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseCutShortBeforeRecognitionIsRetried(bool thenSilence)
    {
        await using var server = new FakeRadioServer(async (request, stream, ct) =>
        {
            if (request.Index == 1)
            {
                await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(1), ct);
                if (thenSilence)
                    await FakeRadio.HoldAsync(stream, ct);
                return;
            }

            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(16), ct);
            if (request.Index > 1)
                await FakeRadio.HoldAsync(stream, ct);
        });
        await using var rig = new Rig(Source(server.Url()), Fast with { StallTimeout = TimeSpan.FromMilliseconds(300) });

        rig.Session.Start();
        await Until(() => server.ConnectionCount >= 3, "trzecie połączenie");

        Assert.Null(rig.Session.Failure);
    }

    /// <summary>Sączenie po bajcie nie jest graniem: bez tego sesja nigdy nie ponowi ani nie zgłosi błędu.</summary>
    [Fact]
    public async Task TrickleOfBytesCountsAsAStall()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(4), ct);
            while (true)
            {
                await Task.Delay(50, ct);
                await stream.WriteAsync(new byte[1], ct);
                await stream.FlushAsync(ct);
            }
        });
        await using var rig = new Rig(Source(server.Url()), Fast with { StallTimeout = TimeSpan.FromMilliseconds(300), MaxAttempts = 2 });

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(LiveFailureReason.Stalled, rig.Session.Failure!.Value.Reason);
    }

    /// <summary>Zdrowa stacja nadaje w czasie rzeczywistym: każda porcja dźwięku odsuwa limit zastoju.</summary>
    [Fact]
    public async Task StreamAtRealTimeOutlivesTheStallLimit()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(4), ct);
            while (true)
            {
                await Task.Delay(30, ct);
                await stream.WriteAsync(FakeRadio.Mp3Frames(1), ct);
                await stream.FlushAsync(ct);
            }
        });
        await using var rig = new Rig(Source(server.Url()), Fast with { StallTimeout = TimeSpan.FromSeconds(1) });

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Playing);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(1, server.ConnectionCount);
        Assert.DoesNotContain(LiveState.Reconnecting, rig.States);
    }

    [Fact]
    public async Task NetworkChangeCutsBackoffShort()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 503 Busy\r\nContent-Length: 0\r\n"));
        await using var rig = new Rig(Source(server.Url()), Fast with { Backoff = [TimeSpan.FromMinutes(5)] });

        rig.Session.Start();
        await Until(() => server.ConnectionCount == 1, "pierwsza próba");

        await Until(() =>
        {
            rig.Session.NotifyNetworkChanged();
            return server.ConnectionCount >= 2;
        }, "druga próba po zmianie sieci");
    }

    /// <summary>Wi-Fi zgłasza zmiany seriami; stacja nie może zużyć przez to prób w pół sekundy.</summary>
    [Fact]
    public async Task SeriesOfNetworkChangesDoesNotUseUpTheAttempts()
    {
        var resolved = 0;
        var options = Fast with
        {
            Backoff = [TimeSpan.FromMilliseconds(100)],
            Jitter = 0,
            MaxAttempts = 5,
            NetworkWakeInterval = TimeSpan.FromHours(1),
            ResolveHost = (_, _) =>
            {
                Interlocked.Increment(ref resolved);
                throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
            },
        };
        await using var rig = new Rig(Source(new Uri("http://stacja.test/")), options);
        var clock = Stopwatch.StartNew();

        rig.Session.Start();
        await Until(() =>
        {
            rig.Session.NotifyNetworkChanged();
            return rig.States.Contains(LiveState.Failed);
        }, "porażka po wyczerpaniu prób");

        Assert.True(clock.ElapsedMilliseconds >= 350, $"poddała się po {clock.ElapsedMilliseconds} ms");
        Assert.Equal(6, Volatile.Read(ref resolved));
    }

    /// <summary>Serwer sam podał, kiedy wrócić; zmiana sieci tego nie skraca.</summary>
    [Fact]
    public async Task NetworkChangeDoesNotCutRetryAfterShort()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 503 Busy\r\nRetry-After: 30\r\nContent-Length: 0\r\n"));
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await Until(() => server.ConnectionCount == 1, "pierwsza próba");
        for (var i = 0; i < 50; i++)
        {
            rig.Session.NotifyNetworkChanged();
            await Task.Delay(2);
        }

        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task NetworkChangeDoesNothingAfterFailure()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("ICY 404 Not Found\r\n"));
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);
        rig.Session.NotifyNetworkChanged();
        await Task.Delay(50);

        Assert.Equal(1, server.ConnectionCount);
    }

    /// <summary>Stacja wolniejsza od czytelnika: bramka zamyka się i otwiera, a bajty się nie gubią.</summary>
    [Fact]
    public async Task SlowStreamRebuffersWithoutLosingBytes()
    {
        var audio = FakeRadio.Mp3Frames(40);
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync(FakeRadio.IcyOk + "\r\n", ct);
            for (var at = 0; at < audio.Length; at += 400)
            {
                await stream.WriteAsync(audio.AsMemory(at, Math.Min(400, audio.Length - at)), ct);
                await Task.Delay(10, ct);
            }

            await FakeRadio.HoldAsync(stream, ct);
        });
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        var (bytes, _) = await rig.ReadUntilAsync((b, _) => b.Count >= audio.Length - audio.Length % 1600);

        Assert.Equal(audio[..bytes.Count], bytes);
        var states = rig.States;
        var rebuffering = states.IndexOf(LiveState.Rebuffering);
        Assert.True(rebuffering > states.IndexOf(LiveState.Playing), string.Join(",", states));
        Assert.Contains(LiveState.Playing, states.Skip(rebuffering));
    }

    [Fact]
    public async Task ReconnectingKeepsPlayingBufferedAudio()
    {
        await using var server = new FakeRadioServer(async (request, stream, ct) =>
        {
            if (request.Index == 0)
            {
                await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(8), ct);
                return;
            }

            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(8), ct);
            await FakeRadio.HoldAsync(stream, ct);
        });
        await using var rig = new Rig(Source(server.Url()));

        rig.Session.Start();
        await Until(() => server.ConnectionCount == 2 && rig.States.Contains(LiveState.Reconnecting), "ponowne połączenie");
        await Until(() => rig.States.Count(s => s == LiveState.Playing) >= 2, "powrót do grania");

        var states = rig.States;
        Assert.DoesNotContain(LiveState.Rebuffering, states);
        Assert.DoesNotContain(LiveState.Failed, states);
    }

    [Fact]
    public async Task LongGapDiscardsStaleBuffer()
    {
        var clock = new ManualClock();
        var firstDone = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var server = new FakeRadioServer(async (request, stream, ct) =>
        {
            if (request.Index == 0)
            {
                await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(8), ct);
                firstDone.TrySetResult();
                await release.Task.WaitAsync(ct);
                return;
            }

            await stream.RespondAsync(FakeRadio.IcyOk, FakeRadio.Mp3Frames(16), ct);
            await FakeRadio.HoldAsync(stream, ct);
        });
        await using var rig = new Rig(Source(server.Url()), Fast with { Time = clock });

        rig.Session.Start();
        await firstDone.Task.WaitAsync(Patience);
        await rig.WaitForStateAsync(LiveState.Playing);
        clock.Now += TimeSpan.FromSeconds(31);
        release.TrySetResult();
        await Until(() => rig.States.Count(s => s == LiveState.Playing) >= 2, "powrót do grania");

        var (bytes, markers) = await rig.ReadUntilAsync((b, _) => b.Count >= 417 * 16);

        Assert.Equal(417 * 16, bytes.Count);
        Assert.Equal(417 * 8, ((CompressedBuffer)rig.Session.Bytes).DroppedBytes);
        Assert.Equal(new (int, StreamMarker)[] { (0, new StreamBegin(LiveCodec.Mp3)) }, markers);
    }

    [Fact]
    public void StationWithoutAddressIsRefused() =>
        Assert.Throws<ArgumentException>(() => new LiveSession(new LiveSource("stacja", "Radio", []), Fast));

    /// <summary>Błąd poza obsługą porażek połączenia nie może zostawić czytelnika bez końca strumienia.</summary>
    [Fact]
    public async Task UnexpectedErrorEndsSessionAsFailed()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("ICY 404 Not Found\r\n"));
        await using var rig = new Rig(Source(server.Url()), Fast with { Time = new BrokenClock() });

        rig.Session.Start();
        await rig.WaitForStateAsync(LiveState.Failed);

        Assert.Equal(LiveFailureReason.Unspecified, rig.Session.Failure!.Value.Reason);
        Assert.Equal(LiveReadStatus.Ended, rig.Session.Bytes.Read(new byte[1], out _, out _));
    }

    private sealed class BrokenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("zegar");
    }

    [Fact]
    public async Task SubscriberExceptionDoesNotStopEvents()
    {
        await using var server = new FakeRadioServer(Playing());
        await using var rig = new Rig(Source(server.Url()));
        rig.Session.InfoChanged += _ => throw new InvalidOperationException();

        rig.Session.Start();

        await rig.WaitForStateAsync(LiveState.Playing);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
