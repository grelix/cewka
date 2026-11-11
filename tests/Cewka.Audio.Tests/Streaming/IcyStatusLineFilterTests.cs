using System.Net;
using System.Security.Authentication;
using System.Text;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

/// <summary>Wiersz „ICY 200 OK” przez prawdziwego klienta HTTP, na atrapie stacji.</summary>
public class IcyStatusLineFilterTests
{
    private static readonly LiveOptions Options = new() { ConnectTimeout = TimeSpan.FromSeconds(5) };

    private static (StreamHttp Http, RequestContext Context) Client(LiveOptions? options = null) =>
        (new StreamHttp(options ?? Options), new RequestContext(true, new ConnectionTrace()));

    [Fact]
    public async Task IcyOkBecomesHttp10WithHeadersAndBody()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(
            "ICY 200 OK\r\nicy-notice1:<BR>SHOUTcast<BR>\r\nicy-metaint:8192\r\nicy-br:192\r\n", "dane"));
        var (http, context) = Client();
        using var _ = http;

        using var response = await http.SendAsync(server.Url(), context, true, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version10, response.Version);
        Assert.Equal("8192", response.Headers.GetValues("icy-metaint").Single());
        Assert.Equal("dane", await response.Content.ReadAsStringAsync());
        Assert.True(context.Trace.SawIcyStatusLine);
        Assert.True(context.Trace.HeadersSeconds >= 0);
    }

    [Fact]
    public async Task RequestCarriesOnlyAgreedHeaders()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk));
        var (http, context) = Client();
        using var _ = http;

        using var response = await http.SendAsync(server.Url(), context, true, CancellationToken.None);

        var request = Assert.Single(server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("1", request.Headers["Icy-MetaData"]);
        Assert.Equal("*/*", request.Headers["Accept"]);
        Assert.StartsWith("Cewka/", request.Headers["User-Agent"]);
        Assert.False(request.Headers.ContainsKey("Referer"));
        Assert.False(request.Headers.ContainsKey("Accept-Encoding"));
        Assert.False(request.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task MetadataHeaderCanBeLeftOut()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk));
        var (http, context) = Client();
        using var _ = http;

        using var response = await http.SendAsync(server.Url(), context, false, CancellationToken.None);

        Assert.False(Assert.Single(server.Requests).WantsMetadata);
    }

    [Fact]
    public async Task StatusLineArrivingOneByteAtATimeIsRecognised()
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            foreach (var b in FakeRadio.Latin1("ICY 200 OK\r\nicy-br:128\r\n\r\ndane"))
            {
                await stream.WriteAsync(new[] { b }, ct);
                await stream.FlushAsync(ct);
                await Task.Delay(1, ct);
            }
        });
        var (http, context) = Client();
        using var _ = http;

        using var response = await http.SendAsync(server.Url(), context, true, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("dane", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("ICY 404 Not Found", 404)]
    [InlineData("ICY 401 Service Unavailable", 401)]
    [InlineData("ICY 200", 200)]
    public async Task IcyStatusCodesPassThrough(string statusLine, int expected)
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(statusLine + "\r\nicy-br:64\r\n"));
        var (http, context) = Client();
        using var _ = http;

        using var response = await http.SendAsync(server.Url(), context, true, CancellationToken.None);

        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Fact]
    public async Task PlainHttpIsNotTouched()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 200 OK\r\nContent-Length: 4\r\n", "dane"));
        var (http, context) = Client();
        using var _ = http;

        using var response = await http.SendAsync(server.Url(), context, true, CancellationToken.None);

        Assert.Equal(HttpVersion.Version11, response.Version);
        Assert.Equal("dane", await response.Content.ReadAsStringAsync());
        Assert.False(context.Trace.SawIcyStatusLine);
    }

    [Fact]
    public async Task ChunkedBodyIsDecodedByClient()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n", "4\r\ndane\r\n3\r\nabc\r\n0\r\n\r\n"));
        var (http, context) = Client();
        using var _ = http;

        using var response = await http.SendAsync(server.Url(), context, true, CancellationToken.None);

        Assert.Equal("daneabc", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("I")]
    [InlineData("IC")]
    [InlineData("ICY")]
    [InlineData("X")]
    public async Task VeryShortResponseFailsAtOnce(string raw)
    {
        await using var server = new FakeRadioServer(async (_, stream, ct) => await stream.WriteAsync(raw, ct));
        var (http, context) = Client();
        using var _ = http;

        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.SendAsync(server.Url(), context, true, CancellationToken.None));
    }

    [Fact]
    public async Task IcyOverTlsWorks()
    {
        using var certificates = new TestCertificates();
        await using var server = new FakeRadioServer(
            FakeRadio.Respond(FakeRadio.IcyOk, "dane"), certificate: certificates.Server);
        var (http, context) = Client(Options with { TrustedRootForTests = certificates.Root });
        using var _ = http;

        using var response = await http.SendAsync(server.Url(), context, true, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("dane", await response.Content.ReadAsStringAsync());
        Assert.True(context.Trace.SawIcyStatusLine);
        Assert.Equal(System.Net.Security.SslPolicyErrors.None, context.Trace.CertificateErrors);
    }

    [Fact]
    public async Task UntrustedCertificateIsRefusedAndRecorded()
    {
        using var certificates = new TestCertificates();
        await using var server = new FakeRadioServer(
            FakeRadio.Respond(FakeRadio.IcyOk), certificate: certificates.Server);
        var (http, context) = Client();
        using var _ = http;

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.SendAsync(server.Url(), context, true, CancellationToken.None));

        Assert.Equal(HttpRequestError.SecureConnectionError, failure.HttpRequestError);
        Assert.IsType<AuthenticationException>(failure.InnerException);
        Assert.NotEqual(System.Net.Security.SslPolicyErrors.None, context.Trace.CertificateErrors);
    }

    /// <summary>Kontrola: bez filtra klient odrzuca ICY, więc pozostałe testy mają sens.</summary>
    [Fact]
    public async Task WithoutFilterClientRejectsIcyStatusLine()
    {
        await using var server = new FakeRadioServer(FakeRadio.Respond(FakeRadio.IcyOk, "dane"));
        using var plain = new HttpClient(new SocketsHttpHandler());

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => plain.GetAsync(server.Url()));

        Assert.Equal(HttpRequestError.InvalidResponse, failure.HttpRequestError);
    }

    [Fact]
    public async Task SilentServerEndsWithTimeoutNotCancellation()
    {
        await using var server = new FakeRadioServer((_, stream, ct) => FakeRadio.HoldAsync(stream, ct));
        var (http, context) = Client(Options with { ConnectTimeout = TimeSpan.FromMilliseconds(150) });
        using var _ = http;

        await Assert.ThrowsAsync<TimeoutException>(
            () => http.SendAsync(server.Url(), context, true, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyReadDoesNotDecideOrConsume()
    {
        using var filter = new IcyStatusLineFilter(new MemoryStream("ICY 200 OK"u8.ToArray()), null);

        Assert.Equal(0, await filter.ReadAsync(Memory<byte>.Empty));
        var buffer = new byte[64];
        var total = 0;
        int read;
        while ((read = await filter.ReadAsync(buffer.AsMemory(total))) > 0)
            total += read;

        Assert.Equal("HTTP/1.0 200 OK", Encoding.ASCII.GetString(buffer, 0, total));
    }

    [Fact]
    public void SmallDestinationReceivesReplacementInPieces()
    {
        using var filter = new IcyStatusLineFilter(new MemoryStream("ICY 200"u8.ToArray()), null);
        var output = new StringBuilder();
        var one = new byte[1];

        while (filter.Read(one, 0, 1) == 1)
            output.Append((char)one[0]);

        Assert.Equal("HTTP/1.0 200", output.ToString());
    }
}
