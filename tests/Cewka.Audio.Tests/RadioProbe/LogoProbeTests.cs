using Cewka.Audio.Streaming;
using Cewka.Audio.Tests.Streaming;
using Cewka.RadioProbe;
using Xunit;

namespace Cewka.Audio.Tests.RadioProbe;

public class LogoProbeTests
{
    private static readonly TestCertificates Certificates = new();

    private static FakeRadioServer Secure(Func<FakeRequest, Stream, CancellationToken, Task> script) =>
        new(script, certificate: Certificates.Server);

    private static Task<LogoResult> Probe(Uri url, double seconds = 5) =>
        new LogoProbe(new LiveOptions { IsLocalAddress = _ => false, TrustedRootForTests = Certificates.Root }, TimeSpan.FromSeconds(seconds))
            .ProbeAsync("stacja", url, false, CancellationToken.None);

    [Fact]
    public async Task Status_format_and_dimensions_are_reported()
    {
        await using var server = Secure(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\ncontent-type:image/png\r\ncontent-length:33\r\n", ImageHeaderTests.Png(512, 256)));

        var result = await Probe(server.Url("/logo.png"));

        Assert.Null(result.Failure);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("png", result.Format);
        Assert.Equal(512, result.Width);
        Assert.Equal(256, result.Height);
        Assert.False(result.TooLarge);
    }

    [Fact]
    public async Task Format_comes_from_bytes_when_the_type_lies()
    {
        await using var server = Secure(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\ncontent-type:image/png\r\n", ImageHeaderTests.Jpeg(64, 48)));

        var result = await Probe(server.Url());

        Assert.Equal("jpeg", result.Format);
        Assert.Equal(64, result.Width);
    }

    [Fact]
    public async Task Redirect_is_followed()
    {
        await using var server = Secure((request, stream, ct) => request.Path == "/a"
            ? stream.RespondAsync("HTTP/1.1 301 Moved\r\nlocation:/b.png\r\nconnection:close\r\n", [], ct)
            : stream.RespondAsync("HTTP/1.1 200 OK\r\n", ImageHeaderTests.Png(8, 8), ct));

        var result = await Probe(server.Url("/a"));

        Assert.Equal("png", result.Format);
        Assert.Equal(server.Url("/b.png"), result.FinalUrl);
    }

    [Fact]
    public async Task Missing_logo_is_a_reason_code()
    {
        await using var server = Secure(FakeRadio.Respond("HTTP/1.1 404 Nie\r\nconnection:close\r\n"));

        var result = await Probe(server.Url());

        Assert.Equal(LiveFailureReason.NotFound, result.Failure?.Reason);
        Assert.Equal(404, result.HttpStatus);
    }

    [Fact]
    public async Task Html_instead_of_an_image_is_a_reason_code()
    {
        await using var server = Secure(FakeRadio.Respond("HTTP/1.1 200 OK\r\n", "<!doctype html>"));

        var result = await Probe(server.Url());

        Assert.Equal(LiveFailureReason.UnrecognisedContent, result.Failure?.Reason);
        Assert.Null(result.Format);
    }

    [Fact]
    public async Task Declared_size_over_the_limit_is_marked()
    {
        await using var server = Secure(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\ncontent-length:3000000\r\n", ImageHeaderTests.Png(8, 8)));

        var result = await Probe(server.Url());

        Assert.True(result.TooLarge);
        Assert.Equal("png", result.Format);
    }

    [Fact]
    public async Task Reading_stops_at_the_limit_without_dimensions()
    {
        // Bez znacznika wymiarów JPEG nie ma końca: atrapa śle, aż klient się rozłączy.
        await using var server = Secure(async (_, stream, ct) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\n\r\n", ct);
            await stream.WriteAsync(new byte[] { 0xFF, 0xD8 }, ct);
            var segment = new byte[60_002];
            segment[0] = 0xFF;
            segment[1] = 0xE1;
            segment[2] = 0xEA;
            segment[3] = 0x60;
            while (true)
            {
                await stream.WriteAsync(segment, ct);
                await stream.FlushAsync(ct);

                // Bez przerw bufory gniazd połknęłyby wiele megabajtów, zanim atrapa zauważy rozłączenie.
                await Task.Delay(1, ct);
            }
        });

        var result = await Probe(server.Url());

        Assert.True(result.TooLarge);
        Assert.Null(result.Width);
        Assert.InRange(result.BytesRead, 1, LogoProbe.MaxBytes);
        Assert.True(server.BytesSent < LogoProbe.MaxBytes + 1024 * 1024, $"Wysłano {server.BytesSent}");
    }

    /// <summary>HTTP dopuszczony tylko dla dźwięku: obraz po HTTP można podmienić w drodze.</summary>
    [Fact]
    public async Task Redirect_from_https_to_http_is_refused()
    {
        await using var plain = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 200 OK\r\n", ImageHeaderTests.Png(8, 8)));
        await using var secure = Secure(FakeRadio.Respond($"HTTP/1.1 302 Moved\r\nlocation:{plain.Url()}\r\nconnection:close\r\n"));

        var result = await Probe(secure.Url());

        Assert.Equal(LiveFailureReason.InsecureDowngrade, result.Failure?.Reason);
        Assert.Null(result.Format);
        Assert.Equal("http", result.FinalUrl?.Scheme);
    }

    [Fact]
    public async Task Logo_address_over_http_is_refused_without_connecting()
    {
        await using var plain = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 200 OK\r\n", ImageHeaderTests.Png(8, 8)));

        var result = await Probe(plain.Url());

        Assert.Equal(LiveFailureReason.InsecureDowngrade, result.Failure?.Reason);
        Assert.Equal(0, plain.ConnectionCount);
    }

    [Fact]
    public async Task Catalogue_logo_on_a_local_address_is_refused()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 200 OK\r\n", ImageHeaderTests.Png(8, 8)));

        var result = await new LogoProbe(new LiveOptions(), TimeSpan.FromSeconds(1))
            .ProbeAsync("stacja", server.Url(), false, CancellationToken.None);

        Assert.Equal(LiveFailureReason.AddressRefused, result.Failure?.Reason);
        Assert.Equal(0, server.ConnectionCount);
    }
}
