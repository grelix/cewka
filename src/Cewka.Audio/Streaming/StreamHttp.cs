using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Cewka.Audio.Streaming;

/// <summary>Co wiadomo o jednym pobraniu; narzędzie katalogu czyta to do raportu.</summary>
internal sealed class ConnectionTrace
{
    public bool SawIcyStatusLine;
    public Uri? FinalUrl;
    public int Redirects;
    public SslPolicyErrors CertificateErrors;
    public double HeadersSeconds;
    public int? HttpStatus;
    public string? StatusLine;
    public string? ContentType;

    /// <summary>Początek treści po zdjęciu metadanych; tyle, ile wystarczy do rozpoznania jej rodzaju.</summary>
    public byte[]? FirstBytes;

    /// <summary>Połączono z adresem spoza sieci lokalnej; dalsze przejścia nie mogą do niej wrócić.</summary>
    public bool ConnectedPublic;
}

internal sealed record RequestContext(bool AllowLocal, ConnectionTrace Trace);

/// <summary>
/// Klient jednej sesji: wiersz ICY, reguła adresów przy łączeniu, bez przekierowań, ciastek
/// i kompresji. Połączeń się nie używa ponownie, bo każde musi przejść regułę adresów.
/// </summary>
internal sealed class StreamHttp : IDisposable
{
    internal static readonly HttpRequestOptionsKey<RequestContext> ContextKey = new("Cewka.Live.Context");

    private const int MaxIssuerFetches = 3;
    private const int MaxIssuerBytes = 64 * 1024;
    private const int MaxIssuerRedirects = 3;
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");
    private const string AuthorityInformationAccess = "1.3.6.1.5.5.7.1.1";

    private readonly LiveOptions _options;
    private readonly AddressPolicy _policy;
    private readonly HttpClient _client;
    private ConnectionTrace? _activeTrace;

    public StreamHttp(LiveOptions options)
    {
        _options = options;
        var policy = _policy = new AddressPolicy(options);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.ConnectTimeout,
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = options.WrapConnection is { } wrap
                ? async (context, ct) => wrap(await policy.ConnectAsync(context, ct).ConfigureAwait(false))
                : policy.ConnectAsync,
            ResponseHeaderEncodingSelector = (_, _) => Encoding.Latin1,
            PlaintextStreamFilter = (context, _) =>
            {
                context.InitialRequestMessage.Options.TryGetValue(ContextKey, out var request);
                return ValueTask.FromResult<Stream>(new IcyStatusLineFilter(context.PlaintextStream, request?.Trace));
            },
            SslOptions =
            {
                RemoteCertificateValidationCallback = ValidateCertificate,

                CertificateChainPolicy = ChainPolicyWithoutDownloads(),
            },
        };
        if (options.Proxy is { } proxy)
            handler.Proxy = proxy;
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Pobieranie wystawcy przez system ominęłoby regułę adresów; łańcuch dopełnia <see cref="ValidateCertificate"/>.</summary>
    internal static X509ChainPolicy ChainPolicyWithoutDownloads() => new()
    {
        DisableCertificateDownloads = true,
        RevocationMode = X509RevocationMode.NoCheck,
    };

    public async Task<HttpResponseMessage> SendAsync(Uri url, RequestContext context, bool icyMetadata, CancellationToken ct)
    {
        if (GoesThroughProxy(url))
            await _policy.CheckThroughProxyAsync(url, context, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (icyMetadata)
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        request.Options.Set(ContextKey, context);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ConnectTimeout);
        Volatile.Write(ref _activeTrace, context.Trace);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            context.Trace.HeadersSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Brak nagłówków po {_options.ConnectTimeout.TotalSeconds} s");
        }
    }

    /// <summary>Ta sama odpowiedź co w kliencie HTTP, także wyjątek serwera pośredniczącego znaczy połączenie bez niego.</summary>
    private bool GoesThroughProxy(Uri url)
    {
        var proxy = _options.Proxy ?? HttpClient.DefaultProxy;
        try
        {
            return !proxy.IsBypassed(url) && proxy.GetProxy(url) is { } address && address != url;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Weryfikacji nie da się wyłączyć: błąd łańcucha znika wyłącznie po zbudowaniu pełnego łańcucha.</summary>
    internal bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var trace = Volatile.Read(ref _activeTrace);

        if (errors == SslPolicyErrors.RemoteCertificateChainErrors && certificate is not null && chain is not null)
        {
            using var leaf = new X509Certificate2(certificate);
            if (CompleteChain(leaf, chain.ChainPolicy.ExtraStore))
                errors = SslPolicyErrors.None;

            // Pobranie wystawcy miało własny zapis pobrania.
            Volatile.Write(ref _activeTrace, trace);
        }

        if (trace is not null)
            trace.CertificateErrors = errors;
        return errors == SslPolicyErrors.None;
    }

    /// <summary>Dokłada wystawców wskazanych w certyfikatach, własną drogą przez regułę adresów, bo adres wybrał serwer.</summary>
    private bool CompleteChain(X509Certificate2 leaf, X509Certificate2Collection offered)
    {
        using var limit = new CancellationTokenSource(_options.IssuerFetchTimeout);
        var known = new X509Certificate2Collection(offered);

        for (var fetched = 0; ; fetched++)
        {
            using var chain = NewChain(known, allowDownloads: false);
            if (chain.Build(leaf))
                return true;

            if (chain.ChainStatus.All(status => status.Status != X509ChainStatusFlags.PartialChain))
                return IsTrustedAfterRootUpdate(leaf, known, chain);

            if (fetched == MaxIssuerFetches)
                return false;

            var issuer = TryFetchIssuer(chain.ChainElements[^1].Certificate, limit.Token);
            if (issuer is null)
                return false;
            known.Add(issuer);
        }
    }

    /// <summary>
    /// Łańcuch jest pełny, a brakuje tylko głównego: Windows pobiera zaufane główne na żądanie z własnego
    /// katalogu. Żaden certyfikat nie wskazuje tu już wystawcy, więc serwer nie ma wpływu na adres.
    /// </summary>
    private bool IsTrustedAfterRootUpdate(X509Certificate2 leaf, X509Certificate2Collection known, X509Chain complete)
    {
        if (_options.TrustedRootForTests is not null
            || complete.ChainStatus.Any(status => status.Status != X509ChainStatusFlags.UntrustedRoot))
        {
            return false;
        }

        using var chain = NewChain(known, allowDownloads: true);
        chain.ChainPolicy.UrlRetrievalTimeout = _options.IssuerFetchTimeout;
        return chain.Build(leaf);
    }

    private X509Chain NewChain(X509Certificate2Collection known, bool allowDownloads)
    {
        var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.DisableCertificateDownloads = !allowDownloads;
        policy.ApplicationPolicy.Add(ServerAuthentication);
        policy.ExtraStore.AddRange(known);
        if (_options.TrustedRootForTests is { } root)
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.Add(root);
        }

        return chain;
    }

    /// <summary>Każde niepowodzenie znaczy to samo: łańcuch zostaje niepełny, a stacja dostaje błąd TLS.</summary>
    private X509Certificate2? TryFetchIssuer(X509Certificate2 certificate, CancellationToken ct)
    {
        if (IssuerAddress(certificate) is not { } url)
            return null;

        try
        {
            // Wywołanie zwrotne TLS jest synchroniczne, a limit pilnuje czasu wstrzymania.
            return Task.Run(() => FetchIssuerAsync(url, ct), ct).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Uri? IssuerAddress(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != AuthorityInformationAccess)
                continue;

            // Wystawców podaje się po HTTP; po HTTPS byłoby to kolejne uzgadnianie w trakcie tego.
            foreach (var uri in new X509AuthorityInformationAccessExtension(extension.RawData).EnumerateCAIssuersUris())
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out var url) && url.Scheme == "http")
                    return url;
            }
        }

        return null;
    }

    private async Task<X509Certificate2?> FetchIssuerAsync(Uri url, CancellationToken ct)
    {
        for (var hop = 0; hop <= MaxIssuerRedirects; hop++)
        {
            _policy.CheckAddress(url, allowLocal: false);

            using var response = await SendAsync(url, new RequestContext(false, new ConnectionTrace()), false, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                url = new Uri(url, location);
                if (url.Scheme != "http")
                    return null;
                continue;
            }

            return response.StatusCode == HttpStatusCode.OK ? await ReadIssuerAsync(response, ct).ConfigureAwait(false) : null;
        }

        return null;
    }

    private static async Task<X509Certificate2?> ReadIssuerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxIssuerBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
                return X509CertificateLoader.LoadCertificate(buffer.AsSpan(0, length));
            length += read;
        }

        return null;
    }

    public void Dispose() => _client.Dispose();
}
