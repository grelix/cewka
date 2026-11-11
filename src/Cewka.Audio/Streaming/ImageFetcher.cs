using System.IO.Compression;

namespace Cewka.Audio.Streaming;

/// <summary>Dlaczego obrazu nie udało się pobrać; decyduje o tym, jak długo wynik zostaje w pamięci podręcznej.</summary>
public enum ImageFetchProblem
{
    None,

    /// <summary>Adres nie jest HTTPS, wskazuje sieć lokalną albo przekierowanie obniża bezpieczeństwo.</summary>
    Refused,

    /// <summary>Brak połączenia, przekroczenie czasu albo błąd serwera (5xx).</summary>
    Unavailable,

    /// <summary>Serwer odpowiedział, że obrazu nie ma (4xx).</summary>
    Missing,

    /// <summary>Treść przekracza limit bajtów, także po rozpakowaniu.</summary>
    TooLarge,
}

/// <summary>Wynik pobrania obrazu: bajty albo przyczyna porażki.</summary>
public sealed record ImageFetchResult(byte[]? Bytes, ImageFetchProblem Problem, int Status = 0)
{
    public bool Ok => Bytes is not null;
}

/// <summary>
/// Pobiera małe obrazy (logo stacji) przez tę samą warstwę sieci co strumienie: tylko HTTPS, bez ciasteczek
/// i nagłówka Referer, z odmową adresów prywatnych i pętli zwrotnej, z limitem przekierowań, czasu i bajtów.
/// Warstwa sieci nie rozpakowuje treści, więc rozpakowanie (gzip, deflate, brotli) jest tutaj, z tym samym
/// limitem liczonym po rozpakowaniu.
/// </summary>
public sealed class ImageFetcher : IDisposable
{
    public const int DefaultMaxBytes = 512 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly StreamHttp _http;
    private readonly StreamOpener _opener;

    public ImageFetcher()
        : this(new LiveOptions { MaxRedirects = 3 })
    {
    }

    internal ImageFetcher(LiveOptions options)
    {
        _http = new StreamHttp(options);
        _opener = new StreamOpener(_http, new AddressPolicy(options), options);
    }

    public async Task<ImageFetchResult> FetchAsync(Uri url, int maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        try
        {
            using var response = await _opener.FetchImageAsync(url, false, limit.Token).ConfigureAwait(false);
            return await ReadAsync(response, maxBytes, limit.Token).ConfigureAwait(false);
        }
        catch (LiveOpenException e)
        {
            var status = e.Failure.HttpStatus ?? 0;
            return new ImageFetchResult(null, ProblemFor(e.Failure.Reason, status), status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ImageFetchResult(null, ImageFetchProblem.Unavailable);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException)
        {
            return new ImageFetchResult(null, ImageFetchProblem.Unavailable);
        }
    }

    private static ImageFetchProblem ProblemFor(LiveFailureReason reason, int status) => reason switch
    {
        LiveFailureReason.InsecureDowngrade or LiveFailureReason.AddressRefused => ImageFetchProblem.Refused,
        _ when status is >= 400 and < 500 => ImageFetchProblem.Missing,
        _ => ImageFetchProblem.Unavailable,
    };

    /// <summary>Czyta treść odpowiedzi z limitem bajtów (także po rozpakowaniu), bez ufania nagłówkowi długości.</summary>
    internal static async Task<ImageFetchResult> ReadAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var length = response.Content.Headers.ContentLength;
        if (length > maxBytes)
            return new ImageFetchResult(null, ImageFetchProblem.TooLarge);

        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault()?.Trim().ToLowerInvariant();
        await using var raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using Stream body = encoding switch
        {
            "gzip" or "x-gzip" => new GZipStream(raw, CompressionMode.Decompress),
            "deflate" => new DeflateStream(raw, CompressionMode.Decompress),
            "br" => new BrotliStream(raw, CompressionMode.Decompress),
            _ => raw,
        };

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return new ImageFetchResult(null, ImageFetchProblem.TooLarge);

            buffer.Write(chunk, 0, read);
        }

        return new ImageFetchResult(buffer.ToArray(), ImageFetchProblem.None, (int)response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
