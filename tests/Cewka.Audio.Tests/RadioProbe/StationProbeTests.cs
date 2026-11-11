using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Dsp;
using Cewka.Audio.Streaming;
using Cewka.Audio.Tests.Streaming;
using Cewka.RadioProbe;
using Xunit;

namespace Cewka.Audio.Tests.RadioProbe;

/// <summary>Wyłącznie atrapa stacji na adresie lokalnym: bez internetu, bez urządzenia dźwięku.</summary>
public class StationProbeTests
{
    private const int Limit = 200 * 1024;
    private const int Sample = 32 * 1024;
    private const string IcyHead = "ICY 200 OK\r\nicy-br:320\r\ncontent-type:audio/mpeg\r\n";

    /// <summary>Więcej niż Sample: próbka kończy się na limicie bajtów, a czas ma duży zapas na wolną maszynę.</summary>
    private static readonly byte[] Noise = TestAudioFiles.Mp3NoiseFrames(120, 5);

    private static Func<FakeRequest, Stream, CancellationToken, Task> Station(string head, byte[] body) =>
        async (_, stream, ct) =>
        {
            await stream.RespondAsync(head, body, ct);
            await FakeRadio.HoldAsync(stream, ct);
        };

    /// <summary>Atrapa jest na 127.0.0.1, więc reguła adresów ma ją widzieć jako publiczną.</summary>
    private static LiveOptions PublicLoopback => new() { IsLocalAddress = _ => false };

    private static Task<ProbeResult> Probe(
        Uri url, double seconds = 5, int maxBytes = Sample, LiveOptions? options = null) =>
        new StationProbe(options ?? PublicLoopback, new ProbeLimits(TimeSpan.FromSeconds(seconds), maxBytes))
            .ProbeAsync(new ProbeTarget("stacja", url), false, CancellationToken.None);

    [Fact]
    public async Task Icy_status_line_is_reported_and_marked()
    {
        await using var server = new FakeRadioServer(Station(IcyHead, Noise));

        var result = await Probe(server.Url());

        Assert.Null(result.Failure);
        Assert.True(result.IcyStatusLine);
        Assert.Equal("ICY 200 OK", result.StatusLine);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal(server.Url(), result.FinalUrl);
        Assert.False(result.Https);
    }

    [Fact]
    public async Task Plain_http_is_not_marked_as_icy()
    {
        await using var server = new FakeRadioServer(Station("HTTP/1.1 200 OK\r\ncontent-type:audio/mpeg\r\n", Noise));

        var result = await Probe(server.Url());

        Assert.Null(result.Failure);
        Assert.False(result.IcyStatusLine);
        Assert.Equal("HTTP/1.1 200 OK", result.StatusLine);
    }

    [Fact]
    public async Task Codec_comes_from_frames_when_the_header_lies()
    {
        await using var mp3 = new FakeRadioServer(Station("HTTP/1.1 200 OK\r\ncontent-type:audio/aacp\r\n", Noise));
        await using var aac = new FakeRadioServer(Station(IcyHead, FakeRadio.AdtsFrames(200)));

        Assert.Equal(LiveCodec.Mp3, (await Probe(mp3.Url())).Codec);
        Assert.Equal(LiveCodec.Aac, (await Probe(aac.Url())).Codec);
    }

    [Fact]
    public async Task Bitrate_comes_from_frames_not_from_the_icy_header()
    {
        await using var server = new FakeRadioServer(Station(IcyHead, Noise));

        var result = await Probe(server.Url());

        Assert.Equal(320, result.HeaderKbps);
        Assert.InRange(result.MeasuredKbps!.Value, 126, 130);
        Assert.Equal(44100, result.SampleRate);
        Assert.Equal(2, result.Channels);
        Assert.NotNull(result.FirstAudioSeconds);
    }

    [Fact]
    public async Task Stream_is_cut_at_the_byte_limit_counted_on_the_server()
    {
        var sent = new TaskCompletionSource();
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            try
            {
                await stream.WriteAsync(IcyHead + "\r\n", ct);
                var frames = TestAudioFiles.Mp3NoiseFrames(20, 9);
                for (var i = 0; i < 500; i++)
                {
                    await stream.WriteAsync(frames, ct);
                    await stream.FlushAsync(ct);
                    await Task.Delay(1, ct);
                }
            }
            finally
            {
                sent.TrySetResult();
            }
        });

        var result = await Probe(server.Url(), maxBytes: Limit);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(result.Failure);
        Assert.InRange(result.BytesRead, Limit - 16 * 1024, Limit);
        Assert.True(result.TotalSeconds < 5, $"Trwało {result.TotalSeconds} s");

        // Zapas na bufory gniazd między atrapą a klientem.
        Assert.InRange(server.BytesSent, Limit - 16 * 1024, Limit + 256 * 1024);
    }

    [Fact]
    public async Task Byte_limit_covers_the_list_and_the_stream_together()
    {
        FakeRadioServer? self = null;
        await using var server = new FakeRadioServer((request, stream, ct) => request.Path == "/lista.pls"
            ? stream.RespondAsync("HTTP/1.1 200 OK\r\nconnection:close\r\n", FakeRadio.Latin1($"[playlist]\nFile1={self!.Url("/live")}\n" + new string(' ', 8000)), ct)
            : Station(IcyHead, TestAudioFiles.Mp3NoiseFrames(300, 2))(request, stream, ct));
        self = server;

        var result = await Probe(server.Url("/lista.pls"), maxBytes: 64 * 1024);

        Assert.Null(result.Failure);
        Assert.Equal(server.Url("/live"), result.FinalUrl);
        Assert.InRange(result.BytesRead, 60 * 1024, 64 * 1024);
    }

    [Fact]
    public async Task Time_limit_ends_a_slow_stream_without_a_failure()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            await stream.RespondAsync(IcyHead, Noise, ct);
            while (true)
            {
                await Task.Delay(20, ct);
                await stream.WriteAsync(Noise.AsMemory(0, 417), ct);
                await stream.FlushAsync(ct);
            }
        });

        var result = await Probe(server.Url(), seconds: 1.5, maxBytes: Limit);

        Assert.Null(result.Failure);
        Assert.InRange(result.TotalSeconds, 1.2, 4);
    }

    [Fact]
    public async Task Silence_before_headers_is_a_connection_failure_within_the_limit()
    {
        await using var server = new FakeRadioServer((_, stream, ct) => FakeRadio.HoldAsync(stream, ct));

        var result = await Probe(server.Url(), seconds: 0.3);

        Assert.Equal(LiveFailureReason.ConnectionFailed, result.Failure?.Reason);
        Assert.Null(result.HttpStatus);
        Assert.True(result.TotalSeconds < 3, $"Trwało {result.TotalSeconds} s");
    }

    [Fact]
    public async Task Silence_after_headers_is_a_stall_within_the_limit()
    {
        await using var server = new FakeRadioServer(Station(IcyHead, []));

        var result = await Probe(server.Url(), seconds: 1);

        Assert.Equal(LiveFailureReason.Stalled, result.Failure?.Reason);
        Assert.Equal(200, result.HttpStatus);
        Assert.True(result.TotalSeconds < 4, $"Trwało {result.TotalSeconds} s");
    }

    [Fact]
    public async Task Stream_closed_before_the_limits_is_reported()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(IcyHead, Noise));

        var result = await Probe(server.Url(), maxBytes: Limit);

        Assert.Equal(LiveFailureReason.StreamEnded, result.Failure?.Reason);
        Assert.Equal(LiveCodec.Mp3, result.Codec);
        Assert.NotNull(result.MeasuredKbps);
    }

    [Fact]
    public async Task Html_is_reported_with_status_type_and_signature()
    {
        await using var server = new FakeRadioServer(
            FakeRadio.Respond("HTTP/1.1 200 OK\r\ncontent-type:audio/mpeg\r\n", "<!doctype html><html></html>"));

        var result = await Probe(server.Url());

        Assert.Equal(LiveFailureReason.NotAStream, result.Failure?.Reason);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("3C 21 64 6F 63 74 79 70", result.Signature);
    }

    [Theory]
    [InlineData(401, LiveFailureReason.Unauthorized)]
    [InlineData(403, LiveFailureReason.Forbidden)]
    [InlineData(404, LiveFailureReason.NotFound)]
    [InlineData(451, LiveFailureReason.UnavailableForLegalReasons)]
    [InlineData(503, LiveFailureReason.ServerError)]
    public async Task Http_errors_are_reason_codes(int status, LiveFailureReason reason)
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond($"HTTP/1.1 {status} Nie\r\nconnection:close\r\n"));

        var result = await Probe(server.Url());

        Assert.Equal(reason, result.Failure?.Reason);
        Assert.Equal(status, result.HttpStatus);
        Assert.Equal($"HTTP/1.1 {status} Nie", result.StatusLine);
    }

    [Fact]
    public async Task Redirect_loop_is_a_reason_code()
    {
        await using var server = new FakeRadioServer(async (request, stream, ct) =>
            await stream.RespondAsync(
                $"HTTP/1.1 302 Found\r\nlocation:{(request.Path == "/a" ? "/b" : "/a")}\r\nconnection:close\r\n", [], ct));

        var result = await Probe(server.Url("/a"));

        Assert.Equal(LiveFailureReason.TooManyRedirects, result.Failure?.Reason);
    }

    [Fact]
    public async Task Hls_is_recognised_and_reported_by_code()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\ncontent-type:audio/x-mpegurl\r\n", "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MAP:URI=\"i.mp4\"\n#EXTINF:5,\nseg1.ts\n"));

        var result = await Probe(server.Url("/playlist.m3u8"));

        Assert.Equal(LiveFailureReason.HlsNotSupportedYet, result.Failure?.Reason);
    }

    [Live.AacDecoderFact]
    public async Task Hls_sample_ending_at_the_next_segment_is_not_a_connection_failure()
    {
        var segment = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", "ton-440-stereo-44100.ts"));

        await using var server = new FakeRadioServer((request, stream, ct) => stream.RespondAsync(
            "HTTP/1.1 200 OK\r\nConnection: close\r\n",
            request.Path == "/live.m3u8"
                ? FakeRadio.Latin1("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n1.ts\n#EXTINF:2,\n2.ts\n#EXTINF:2,\n3.ts\n")
                : segment,
            ct));

        // Granica próbki przechodzi po kolejnych bajtach, także po nagłówkach następnego segmentu.
        for (var maxBytes = segment.Length - 200; maxBytes < segment.Length + 400; maxBytes += 20)
        {
            var result = await Probe(server.Url("/live.m3u8"), maxBytes: maxBytes);

            Assert.True(result.Failure is null, $"limit {maxBytes}: {result.Failure}");
            Assert.Equal(LiveCodec.Aac, result.Codec);
        }
    }

    [Live.AacDecoderFact]
    public async Task Aac_is_decoded_and_its_bitrate_comes_from_adts_frames()
    {
        var tone = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", "ton-440-stereo-44100.aac"));
        await using var server = new FakeRadioServer(Station(IcyHead, tone));

        var result = await Probe(server.Url());

        Assert.Null(result.Failure);
        Assert.Equal(LiveCodec.Aac, result.Codec);
        Assert.InRange(result.MeasuredKbps!.Value, 55, 75);
        Assert.Equal(44100, result.SampleRate);
        Assert.Equal(2, result.Channels);
    }

    [Fact]
    public async Task Ogg_is_a_reason_code()
    {
        var ogg = new byte[8192];
        "OggS"u8.CopyTo(ogg);
        await using var server = new FakeRadioServer(Station("HTTP/1.1 200 OK\r\n", ogg));

        var result = await Probe(server.Url());

        Assert.Equal(LiveFailureReason.CodecNotSupportedYet, result.Failure?.Reason);
        Assert.Equal(LiveCodec.Ogg, result.Codec);
        Assert.Null(result.MeasuredKbps);
    }

    [Fact]
    public async Task Redirects_and_token_in_the_final_address_are_reported()
    {
        await using var server = new FakeRadioServer((request, stream, ct) => request.Path switch
        {
            "/a" => stream.RespondAsync("HTTP/1.1 302 Found\r\nlocation:/b\r\nconnection:close\r\n", [], ct),
            "/b" => stream.RespondAsync("HTTP/1.1 302 Found\r\nlocation:/live?listenerid=7\r\nconnection:close\r\n", [], ct),
            _ => Station(IcyHead, Noise)(request, stream, ct),
        });

        var result = await Probe(server.Url("/a"));

        Assert.Null(result.Failure);
        Assert.Equal(2, result.Redirects);
        Assert.Equal(server.Url("/live?listenerid=7"), result.FinalUrl);
        Assert.Equal(["listenerid"], result.ForbiddenInUrl);
    }

    [Fact]
    public async Task Token_is_reported_even_when_the_final_address_refuses()
    {
        await using var server = new FakeRadioServer((request, stream, ct) => request.Path == "/a"
            ? stream.RespondAsync("HTTP/1.1 302 Found\r\nlocation:/live?zt=7\r\nconnection:close\r\n", [], ct)
            : stream.RespondAsync("HTTP/1.1 403 Nie\r\nconnection:close\r\n", [], ct));

        var result = await Probe(server.Url("/a"));

        Assert.Equal(LiveFailureReason.Forbidden, result.Failure?.Reason);
        Assert.Equal(["zt"], result.ForbiddenInUrl);
    }

    [Fact]
    public async Task Tls_is_reported_for_trusted_and_untrusted_certificates()
    {
        using var certificates = new TestCertificates();
        await using var server = new FakeRadioServer(Station(IcyHead, Noise), certificate: certificates.Server);

        var trusted = await Probe(server.Url(), options: PublicLoopback with { TrustedRootForTests = certificates.Root });
        var untrusted = await Probe(server.Url());

        Assert.Null(trusted.Failure);
        Assert.True(trusted.Https);
        Assert.Null(trusted.CertificateError);
        Assert.True(trusted.IcyStatusLine);

        Assert.Equal(LiveFailureReason.TlsFailed, untrusted.Failure?.Reason);
        Assert.Contains("RemoteCertificateChainErrors", untrusted.CertificateError);
    }

    [Fact]
    public async Task Catalogue_station_on_a_local_address_is_refused_but_a_typed_one_works()
    {
        await using var server = new FakeRadioServer(Station(IcyHead, Noise));
        var probe = new StationProbe(new LiveOptions(), new ProbeLimits(TimeSpan.FromSeconds(5), Sample));

        var catalogue = await probe.ProbeAsync(new ProbeTarget("k", server.Url()), false, CancellationToken.None);
        var typed = await probe.ProbeAsync(new ProbeTarget("w", server.Url()) { AllowLocal = true }, false, CancellationToken.None);

        Assert.Equal(LiveFailureReason.AddressRefused, catalogue.Failure?.Reason);
        Assert.Equal(1, server.ConnectionCount);
        Assert.Null(typed.Failure);
    }

    [Fact]
    public async Task Title_and_metadata_interval_are_reported()
    {
        var body = FakeRadio.WithMetadata(Noise, 8192, _ => FakeRadio.Title("Wykonawca – Żółć"));
        await using var server = new FakeRadioServer(Station(IcyHead + "icy-metaint:8192\r\n", body));

        var result = await Probe(server.Url());

        Assert.Null(result.Failure);
        Assert.Equal(8192, result.MetaInt);
        Assert.Equal("Wykonawca – Żółć", result.Title);
        Assert.InRange(result.MeasuredKbps!.Value, 126, 130);
    }

    [Fact]
    public async Task Request_identifies_the_program_without_user_data()
    {
        await using var server = new FakeRadioServer(Station(IcyHead, Noise));

        await Probe(server.Url());

        // Pełny wzór zamiast szukania nazw: nazwa konta albo komputera może być fragmentem nazwy programu.
        Assert.Matches(@"^Cewka/\d+\.\d+\.\d+( \(\+https://github\.com/[^/\s)]+/cewka\))?$", server.Requests[0].Headers["User-Agent"]);
    }

    [Fact]
    public async Task Playlist_leads_to_the_stream()
    {
        FakeRadioServer? self = null;
        await using var server = new FakeRadioServer((request, stream, ct) => request.Path == "/lista.pls"
            ? stream.RespondAsync("HTTP/1.1 200 OK\r\nconnection:close\r\n", FakeRadio.Latin1($"[playlist]\nFile1={self!.Url("/live")}\n"), ct)
            : Station(IcyHead, Noise)(request, stream, ct));
        self = server;

        var result = await Probe(server.Url("/lista.pls"));

        Assert.Null(result.Failure);
        Assert.Equal(LiveCodec.Mp3, result.Codec);
        Assert.Equal(server.Url("/live"), result.FinalUrl);
    }

    [Fact]
    public async Task Loudness_is_measured_on_decoded_frames_only_when_asked()
    {
        var noise = TestAudioFiles.Mp3NoiseFrames(200, 13);
        await using var server = new FakeRadioServer(Station(IcyHead, noise));
        var probe = new StationProbe(PublicLoopback, new ProbeLimits(TimeSpan.FromSeconds(5), 64 * 1024));

        var measured = await probe.ProbeAsync(new ProbeTarget("s", server.Url()), true, CancellationToken.None);
        var plain = await probe.ProbeAsync(new ProbeTarget("s", server.Url()), false, CancellationToken.None);

        Assert.Null(plain.Lufs);
        Assert.NotNull(measured.Lufs);
        Assert.InRange(measured.Lufs!.Value, Direct(noise) - 0.5, Direct(noise) + 0.5);
    }

    [Fact]
    public async Task Loudness_of_a_short_or_silent_stream_is_absent()
    {
        await using var silent = new FakeRadioServer(Station(IcyHead, TestAudioFiles.Mp3Frames(200)));
        var probe = new StationProbe(PublicLoopback, new ProbeLimits(TimeSpan.FromSeconds(5), 64 * 1024));

        var result = await probe.ProbeAsync(new ProbeTarget("s", silent.Url()), true, CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Null(result.Lufs);
    }

    /// <summary>Ten sam pomiar wprost z dekodera i analizatora, bez sieci.</summary>
    private static double Direct(byte[] mp3)
    {
        using var decoder = new Mp3FrameDecoder();
        var analyser = new LoudnessAnalyser(44100, 2);
        var pcm = new float[decoder.MaxOutputSamples];
        var at = 0;
        while (at < mp3.Length)
        {
            var frames = decoder.Decode(mp3.AsSpan(at), pcm, out var consumed, out _);
            if (consumed == 0)
                break;
            at += consumed;
            analyser.Add(pcm, frames);
        }

        return analyser.ComputeIntegratedLoudness()!.Value;
    }

    [Fact]
    public async Task Cancelling_from_outside_is_not_a_station_failure()
    {
        await using var server = new FakeRadioServer((_, stream, ct) => FakeRadio.HoldAsync(stream, ct));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var probe = new StationProbe(PublicLoopback, new ProbeLimits(TimeSpan.FromSeconds(3), Limit));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ProbeAsync(new ProbeTarget("s", server.Url()), false, cancel.Token));
    }
}
