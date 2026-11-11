using System.Collections.Concurrent;
using System.Diagnostics;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;
using Cewka.Audio.Tests.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Cały tor stacji: atrapa serwera, prawdziwa sesja i dekoder, silnik na atrapie wyjścia.</summary>
public class PlaybackEngineLiveEndToEndTests
{
    private const int MetaInt = 4096;

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

    private sealed class Rig : IDisposable
    {
        private int _started;
        private int _failed;

        public Rig(LiveOptions? options = null)
        {
            Harness = new EngineHarness();
            Engine.OpenLiveSession = source =>
            {
                var session = new LiveSession(source, options ?? Fast);
                Sessions.Enqueue(session);
                return session;
            };
            Engine.LiveStarted += _ => Interlocked.Increment(ref _started);
            Engine.LiveTitleChanged += (_, title) => Titles.Enqueue(title);
            Engine.LiveStateChanged += (_, state) => States.Enqueue(state);
            Engine.LiveInfoChanged += (_, info) => Infos.Enqueue(info);
            Engine.LiveFailed += (_, failure) =>
            {
                Failures.Enqueue(failure);
                Interlocked.Increment(ref _failed);
            };
        }

        public EngineHarness Harness { get; }
        public PlaybackEngine Engine => Harness.Engine;
        public ConcurrentQueue<LiveSession> Sessions { get; } = new();
        public ConcurrentQueue<string?> Titles { get; } = new();
        public ConcurrentQueue<LiveState> States { get; } = new();
        public ConcurrentQueue<LiveStreamInfo> Infos { get; } = new();
        public ConcurrentQueue<LiveFailure> Failures { get; } = new();
        public int Started => Volatile.Read(ref _started);
        public int Failed => Volatile.Read(ref _failed);

        public int NonZeroFrames
        {
            get
            {
                var samples = Harness.Output.Samples;
                var count = 0;
                for (var i = 0; i < samples.Length; i += EngineHarness.Channels)
                    if (samples[i] != 0) count++;
                return count;
            }
        }

        public void Dispose() => Harness.Dispose();
    }

    private static LiveSource Station(FakeRadioServer server) =>
        new("s1", "Stacja", [server.Url()], AllowLocalAddress: true);

    private static async Task SendAndHold(Stream stream, string head, byte[] body, CancellationToken ct)
    {
        await stream.RespondAsync(head, body, ct);
        await FakeRadio.HoldAsync(stream, ct);
    }

    /// <summary>Odtworzenie niezerowe to dźwięk, który przeszedł przez dekoder i cały tor.</summary>
    [Fact]
    public async Task IcyStreamWithMetadataPlaysAndNamesTheSong()
    {
        var audio = TestAudioFiles.Mp3NoiseFrames(200);
        var body = FakeRadio.WithMetadata(audio, MetaInt, _ => FakeRadio.Title("Wykonawca - Utwór"));
        await using var server = new FakeRadioServer((_, stream, ct) =>
            SendAndHold(stream, FakeRadio.IcyOk + $"icy-metaint:{MetaInt}\r\n", body, ct));
        using var rig = new Rig();
        var h = rig.Harness;

        rig.Engine.PlayLive(Station(server));
        h.PumpUntil(() => !rig.Titles.IsEmpty && rig.NonZeroFrames > 48000);

        Assert.Equal(1, rig.Started);
        Assert.Equal("Wykonawca - Utwór", rig.Engine.LiveTitle);
        Assert.Equal("Wykonawca - Utwór", rig.Titles.Single());
        Assert.Equal(LiveCodec.Mp3, rig.Engine.LiveCodec);
        Assert.Equal(LiveState.Playing, rig.Engine.LiveState);
        Assert.Contains(LiveState.Playing, rig.States);
        Assert.Equal("Stacja", rig.Engine.LiveInfo!.IcyName);
        Assert.True(rig.Engine.LiveInfo.HasMetadata);
        Assert.NotEmpty(rig.Infos);
        Assert.True(Signals.AllFinite(h.Output.Samples));
        Assert.True(rig.Engine.Position > TimeSpan.Zero);
    }

    [Fact]
    public async Task BrokenFrameInTheMiddleOfTheStreamReconnectsAndPlaysOn()
    {
        var whole = TestAudioFiles.Mp3NoiseFrames(100);
        var torn = whole.AsSpan(0, whole.Length - 200).ToArray();
        var next = TestAudioFiles.Mp3NoiseFrames(200, seed: 2);

        await using var server = new FakeRadioServer((request, stream, ct) => request.Index == 0
            ? stream.RespondAsync(FakeRadio.IcyOk, torn, ct)
            : SendAndHold(stream, FakeRadio.IcyOk, next, ct));
        using var rig = new Rig();
        var h = rig.Harness;

        rig.Engine.PlayLive(Station(server));
        h.PumpUntil(() => server.ConnectionCount >= 2 && rig.NonZeroFrames > 240000, 20000);

        Assert.Equal(0, rig.Failed);
        Assert.Contains(LiveState.Reconnecting, rig.States);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
        Assert.True(Signals.AllFinite(h.Output.Samples));
    }

    [Fact]
    public async Task SampleRateChangeInTheMiddleOfTheStreamPassesThroughTheWholeChain()
    {
        var body = TestAudioFiles.Concat(
            TestAudioFiles.Mp3NoiseFrames(120),
            TestAudioFiles.Mp3NoiseFrames(120, seed: 3, sampleRate: 48000));
        await using var server = new FakeRadioServer((_, stream, ct) => SendAndHold(stream, FakeRadio.IcyOk, body, ct));

        // Bez limitu zastoju w zasięgu testu: ponowne połączenie dołożyłoby dźwięku do policzenia.
        using var rig = new Rig(Fast with { StallTimeout = TimeSpan.FromMinutes(1) });
        var h = rig.Harness;

        rig.Engine.PlayLive(Station(server));

        // Pompuje do końca danych: wcześniejsze przerwanie nic nie mówi o górnej granicy.
        var last = -1;
        var steady = 0;
        h.PumpUntil(() =>
        {
            var frames = rig.NonZeroFrames;
            steady = frames == last ? steady + 1 : 0;
            last = frames;
            return frames > 0 && steady >= 100;
        }, 30000);

        // 120 ramek po 1152 próbki w 44,1 kHz i tyle samo w 48 kHz, po konwersji na 48 kHz, bez
        // ostatnich 5760 B z okna dekodera, które czekają na kolejne bajty: to 15 ramek po 384 B.
        var expected = (int)(120 * 1152 * 48000.0 / 44100 + (120 - 15) * 1152);

        Assert.True(Signals.AllFinite(h.Output.Samples));
        Assert.Equal(0, rig.Failed);
        Assert.Equal(1, server.ConnectionCount);
        Assert.InRange(rig.NonZeroFrames, expected - 4096, expected + 4096);
    }

    [Live.AacDecoderFact]
    public async Task HlsPlaysThroughTheEngineAndReconnectsFromTheMasterPlaylist()
    {
        var tone = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", "ton-440-stereo-44100.ts"));
        var masters = 0;

        await using var server = new FakeRadioServer((request, stream, ct) =>
        {
            const string head = "HTTP/1.1 200 OK\r\nConnection: close\r\n";

            if (request.Path == "/master.m3u8")
            {
                Interlocked.Increment(ref masters);
                return stream.RespondAsync(head, FakeRadio.Latin1("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000\naudio.m3u8\n"), ct);
            }

            if (request.Path == "/audio.m3u8")
            {
                return stream.RespondAsync(head, FakeRadio.Latin1(
                    "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n"
                    + string.Concat(Enumerable.Range(0, 8).Select(i => $"#EXTINF:1.5,\ns{i}.ts\n"))), ct);
            }

            // Pierwsze połączenie gra do końca, potem segment wypada z serwera.
            return Volatile.Read(ref masters) == 1 && request.Path != "/s7.ts"
                ? stream.RespondAsync(head, tone, ct)
                : stream.RespondAsync("HTTP/1.1 404 Not Found\r\nConnection: close\r\n", [], ct);
        });

        using var rig = new Rig();
        var h = rig.Harness;

        rig.Engine.PlayLive(new LiveSource("hls", "Stacja", [server.Url("/master.m3u8")], AllowLocalAddress: true));
        h.PumpUntil(() => rig.NonZeroFrames > 48000 || rig.Failed > 0);

        Assert.Equal(0, rig.Failed);
        Assert.True(Signals.AllFinite(h.Output.Samples));

        h.PumpUntil(() => Volatile.Read(ref masters) >= 2, 20000);
    }

    [Live.AacDecoderFact]
    public async Task AdtsPlaysThroughTheSystemDecoder()
    {
        var tone = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", "ton-440-stereo-44100.aac"));
        var body = Enumerable.Repeat(tone, 8).SelectMany(bytes => bytes).ToArray();

        await using var server = new FakeRadioServer((_, stream, ct) => SendAndHold(stream, FakeRadio.IcyOk, body, ct));
        using var rig = new Rig();
        var h = rig.Harness;

        rig.Engine.PlayLive(Station(server));
        h.PumpUntil(() => rig.NonZeroFrames > 96000 || rig.Failed > 0);

        Assert.True(Signals.AllFinite(h.Output.Samples));
        Assert.Equal(0, rig.Failed);
        Assert.Equal(LiveState.Playing, rig.Engine.LiveState);
        Assert.Empty(h.Failures);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\ncontent-type:audio/mpeg\r\n", "<!doctype html><html><body>Stacja</body></html>", LiveFailureReason.NotAStream)]
    [InlineData("HTTP/1.1 200 OK\r\ncontent-type:audio/x-mpegurl\r\n", "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MAP:URI=\"i.mp4\"\n#EXTINF:6,\nseg1.ts\n", LiveFailureReason.HlsNotSupportedYet)]
    [InlineData("HTTP/1.1 404 Not Found\r\n", "", LiveFailureReason.NotFound)]
    public async Task PermanentFailureGetsItsReasonAfterOneAttempt(string head, string body, LiveFailureReason reason)
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(head + $"content-length:{body.Length}\r\n", body));
        using var rig = new Rig();
        var h = rig.Harness;

        rig.Engine.PlayLive(Station(server));
        h.PumpUntil(() => rig.Failed > 0);

        Assert.Equal(reason, rig.Failures.Single().Reason);
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(LiveState.Failed, rig.Engine.LiveState);
        Assert.Equal(0, rig.Started);
    }

    /// <summary>Adres główny zaczął podawać śmieci; zapasowy działa, więc stacja gra dalej.</summary>
    [Fact]
    public async Task UndecodableDataMovesToTheBackupAddress()
    {
        var broken = TestAudioFiles.Concat(TestAudioFiles.Mp3NoiseFrames(3), new byte[400 * 1024]);
        var good = TestAudioFiles.Mp3NoiseFrames(200);
        await using var primary = new FakeRadioServer((_, stream, ct) => SendAndHold(stream, FakeRadio.IcyOk, broken, ct));
        await using var backup = new FakeRadioServer((_, stream, ct) => SendAndHold(stream, FakeRadio.IcyOk, good, ct));
        using var rig = new Rig();
        var h = rig.Harness;

        rig.Engine.PlayLive(new LiveSource("s1", "Stacja", [primary.Url(), backup.Url()], AllowLocalAddress: true));
        h.PumpUntil(() => backup.ConnectionCount > 0 && rig.NonZeroFrames > 48000, 20000);

        Assert.Equal(0, rig.Failed);
        Assert.Equal(1, primary.ConnectionCount);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
    }

    /// <summary>Jedyny adres podaje śmieci: bez ponowień, bo to nie usterka połączenia.</summary>
    [Fact]
    public async Task UndecodableDataOnTheOnlyAddressFailsWithItsReason()
    {
        var broken = TestAudioFiles.Concat(TestAudioFiles.Mp3NoiseFrames(3), new byte[400 * 1024]);
        await using var server = new FakeRadioServer((_, stream, ct) => SendAndHold(stream, FakeRadio.IcyOk, broken, ct));
        using var rig = new Rig();
        var h = rig.Harness;

        rig.Engine.PlayLive(Station(server));
        h.PumpUntil(() => rig.Failed > 0, 20000);

        Assert.Equal(LiveFailureReason.UndecodableData, rig.Failures.Single().Reason);
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(LiveState.Failed, rig.Engine.LiveState);
    }

    /// <summary>Odbiorca, który przetrzymał zgłoszenie łączenia, i tak dostaje stan końcowy.</summary>
    [Fact]
    public async Task SlowReceiverOfStatesStillLearnsAboutTheFailure()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 404 Not Found\r\ncontent-length:0\r\n"));
        using var rig = new Rig();
        var h = rig.Harness;
        rig.Engine.LiveStateChanged += (_, state) =>
        {
            if (state == LiveState.Connecting) Thread.Sleep(300);
        };

        rig.Engine.PlayLive(Station(server));
        h.PumpUntil(() => rig.Failed > 0);
        EngineHarness.WaitUntil(() => rig.States.Contains(LiveState.Failed));

        Assert.Equal(LiveState.Failed, rig.States.Last());
        Assert.Single(rig.States, state => state == LiveState.Failed);
        Assert.Equal(LiveState.Failed, rig.Engine.LiveState);
    }

    /// <summary>Odtwórz po pauzie to nowe połączenie, a nie reszta starego bufora.</summary>
    [Fact]
    public async Task PauseClosesTheConnectionAndPlayOpensANewOne()
    {
        var body = TestAudioFiles.Mp3NoiseFrames(200);
        await using var server = new FakeRadioServer((_, stream, ct) => SendAndHold(stream, FakeRadio.IcyOk, body, ct));
        using var rig = new Rig();
        var h = rig.Harness;

        rig.Engine.PlayLive(Station(server));
        h.PumpUntil(() => rig.Started > 0);

        var first = rig.Sessions.Last();
        rig.Engine.Pause();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        rig.Engine.Play();
        h.PumpUntil(() => rig.Started > 1);

        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
    }

    /// <summary>Domyślne opcje: wiersz ICY i próg startu bez skróconych czasów.</summary>
    [Fact]
    public async Task DefaultSessionAndDecoderPlayAStream()
    {
        var body = TestAudioFiles.Mp3NoiseFrames(200);
        await using var server = new FakeRadioServer((_, stream, ct) => SendAndHold(stream, FakeRadio.IcyOk, body, ct));
        using var h = new EngineHarness();
        var started = 0;
        h.Engine.LiveStarted += _ => Interlocked.Increment(ref started);

        h.Engine.PlayLive(Station(server));
        h.PumpUntil(() => Volatile.Read(ref started) > 0);

        h.Engine.StopLive();
        Assert.False(h.Engine.IsLive);
    }

    // ---------- zatrzymanie ----------

    private const int StopLimitMilliseconds = 100;

    /// <summary>Polecenie Zatrzymaj nie może czekać na czas sieci ani na żaden sen wątku dekodującego.</summary>
    [Fact]
    public async Task StopWhileConnectingTakesUnderAHundredMilliseconds()
    {
        await using var server = new FakeRadioServer((_, stream, ct) => FakeRadio.HoldAsync(stream, ct));
        using var rig = new Rig();
        rig.Engine.Initialise();
        rig.Harness.Output.StartAutoPump(independentOfSystemClock: true);

        var measured = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var before = server.ConnectionCount;
            rig.Engine.PlayLive(Station(server));
            EngineHarness.WaitUntil(() => server.ConnectionCount > before && !rig.Engine.IsSeeking);

            measured.Add(await MeasureStop(rig));
        }

        Assert.True(Median(measured) < StopLimitMilliseconds, string.Join(", ", measured));
    }

    [Fact]
    public async Task StopWhileWaitingForDataTakesUnderAHundredMilliseconds()
    {
        var body = TestAudioFiles.Mp3NoiseFrames(30);
        await using var server = new FakeRadioServer((_, stream, ct) => SendAndHold(stream, FakeRadio.IcyOk, body, ct));
        using var rig = new Rig();
        rig.Engine.Initialise();
        rig.Harness.Output.StartAutoPump(independentOfSystemClock: true);

        var measured = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var started = rig.Started;
            rig.Engine.PlayLive(Station(server));
            EngineHarness.WaitUntil(() => rig.Started > started && rig.Engine.BufferedFrames == 0);

            measured.Add(await MeasureStop(rig));
        }

        Assert.True(Median(measured) < StopLimitMilliseconds, string.Join(", ", measured));
    }

    private static async Task<double> MeasureStop(Rig rig)
    {
        var session = rig.Sessions.Last();
        var clock = Stopwatch.StartNew();

        rig.Engine.StopLive();
        await session.Completion;
        while (rig.Engine.IsSeeking) await Task.Yield();

        return clock.Elapsed.TotalMilliseconds;
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
}
