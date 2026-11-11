using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace Cewka.Audio.Streaming;

/// <summary>Czasy i limity sieci. Testy podają małe wartości i atrapy rozwiązywania nazw.</summary>
internal sealed record LiveOptions
{
    /// <summary>Łącznie z TLS i nagłówkami odpowiedzi.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Jedno otwarcie razem z listami i ich wpisami: martwa lista nie trzyma stacji minutami.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public double StartSeconds { get; init; } = 1.5;
    public double RebufferSeconds { get; init; } = 3;
    public double TargetSeconds { get; init; } = 10;
    public double MaxSeconds { get; init; } = 30;
    public int HardLimitBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Okno dekodera i pierścień PCM biorą bajty od razu: niska przepływność potrzebuje zapasu ponad nie.</summary>
    public int MinStartBytes { get; init; } = 16 * 1024;
    public int MinRebufferBytes { get; init; } = 16 * 1024;

    public int MaxRedirects { get; init; } = 5;
    public int MaxPlaylistDepth { get; init; } = 3;
    public int MaxPlaylistBytes { get; init; } = 256 * 1024;
    public int SniffBytes { get; init; } = 16 * 1024;

    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
    [
        TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
    ];
    public double Jitter { get; init; } = 0.2;
    public int MaxAttempts { get; init; } = 8;
    public TimeSpan MaxRetryTime { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Zmiana sieci przychodzi seriami: najwyżej jedno przerwanie czekania na taki czas.</summary>
    public TimeSpan NetworkWakeInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Tylko do odczytu czasu; oczekiwanie idzie zwykłym zegarem.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public Random Random { get; init; } = Random.Shared;

    public string UserAgent { get; init; } = DefaultUserAgent();

    public Func<string, CancellationToken, Task<IPAddress[]>> ResolveHost { get; init; } =
        (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    public Func<IPAddress, bool> IsLocalAddress { get; init; } = AddressPolicy.IsLocal;

    /// <summary>Łączenie gniazda; testy podstawiają adres, który nie odpowiada.</summary>
    public Func<Socket, IPEndPoint, CancellationToken, ValueTask> ConnectSocket { get; init; } =
        static (socket, endPoint, ct) => socket.ConnectAsync(endPoint, ct);

    /// <summary>Kod przyczyny, gdy adres jest zablokowany; null, gdy wolno.</summary>
    public Func<Uri, LiveFailureReason?> UrlGuard { get; init; } = _ => null;

    /// <summary>Kodeki, które umie zagrać dekoder.</summary>
    public Func<LiveCodec, bool> CodecPlayable { get; init; } = Decoding.Live.LiveFrameDecoders.IsPlayable;

    public X509Certificate2? TrustedRootForTests { get; init; }

    /// <summary>Serwer pośredniczący; null to ustawienie systemowe.</summary>
    public IWebProxy? Proxy { get; init; }

    /// <summary>Tyle najwyżej trwa dopełnianie niepełnego łańcucha certyfikatów; wstrzymuje uzgadnianie TLS.</summary>
    public TimeSpan IssuerFetchTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Tyle najwyżej czeka reguła adresów na nazwę stacji za serwerem pośredniczącym.</summary>
    public TimeSpan ProxyResolveTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Opakowuje gniazdo każdego połączenia; narzędzie katalogu liczy tak bajty.</summary>
    public Func<Stream, Stream>? WrapConnection { get; init; }

    private static string DefaultUserAgent()
    {
        var assembly = typeof(LiveOptions).Assembly;
        var version = assembly.GetName().Version;
        var repository = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value;
        var text = version is null ? "0" : $"{version.Major}.{version.Minor}.{version.Build}";
        return repository is null ? $"Cewka/{text}" : $"Cewka/{text} (+{repository})";
    }
}
