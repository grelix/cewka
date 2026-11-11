using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Pobieranie obrazów na atrapach odpowiedzi: żadne połączenie z siecią nie wychodzi.</summary>
public sealed class ImageFetcherTests
{
    private static HttpResponseMessage Response(byte[] body, string? encoding = null, long? declaredLength = null)
    {
        var content = new ByteArrayContent(body);
        if (encoding is not null) content.Headers.ContentEncoding.Add(encoding);
        if (declaredLength is not null) content.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
            gzip.Write(data);
        return output.ToArray();
    }

    [Fact]
    public async Task ZwyklaTresc_JestZwracanaWCalosci()
    {
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        using var response = Response(bytes);

        var result = await ImageFetcher.ReadAsync(response, 10_000, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(bytes, result.Bytes);
    }

    [Fact]
    public async Task TrescGzip_JestRozpakowywana()
    {
        var bytes = Enumerable.Range(0, 3000).Select(i => (byte)(i % 7)).ToArray();
        using var response = Response(Gzip(bytes), "gzip");

        var result = await ImageFetcher.ReadAsync(response, 10_000, CancellationToken.None);

        Assert.Equal(bytes, result.Bytes);
    }

    [Fact]
    public async Task DlugoscZNaglowkaPonadLimit_OdrzucaBezCzytania()
    {
        using var response = Response([1, 2, 3], declaredLength: 600_000);

        var result = await ImageFetcher.ReadAsync(response, ImageFetcher.DefaultMaxBytes, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ImageFetchProblem.TooLarge, result.Problem);
    }

    [Fact]
    public async Task TrescPonadLimitBezNaglowka_JestOdrzucana()
    {
        using var response = Response(new byte[40_000]);

        var result = await ImageFetcher.ReadAsync(response, 10_000, CancellationToken.None);

        Assert.Equal(ImageFetchProblem.TooLarge, result.Problem);
    }

    [Fact]
    public async Task SpakowanaBombaPonadLimitPoRozpakowaniu_JestOdrzucana()
    {
        var packed = Gzip(new byte[5_000_000]);
        Assert.True(packed.Length < 20_000);
        using var response = Response(packed, "gzip");

        var result = await ImageFetcher.ReadAsync(response, ImageFetcher.DefaultMaxBytes, CancellationToken.None);

        Assert.Equal(ImageFetchProblem.TooLarge, result.Problem);
    }

    [Fact]
    public async Task UszkodzonyGzip_RzucaWyjatkiemObslugiwanymPrzezPobieranie()
    {
        using var response = Response([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], "gzip");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ImageFetcher.ReadAsync(response, 10_000, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://example.org/logo.png")]
    [InlineData("https://127.0.0.1/logo.png")]
    [InlineData("https://192.168.1.10/logo.png")]
    [InlineData("https://[::1]/logo.png")]
    public async Task AdresNieHttpsAlboLokalny_JestOdmawianyBezPolaczenia(string address)
    {
        using var fetcher = new ImageFetcher();

        var result = await fetcher.FetchAsync(
            new Uri(address), ImageFetcher.DefaultMaxBytes, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ImageFetchProblem.Refused, result.Problem);
    }
}
