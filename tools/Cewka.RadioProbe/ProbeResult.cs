using Cewka.Audio.Streaming;

namespace Cewka.RadioProbe;

/// <summary>Stacja i adres do sprawdzenia.</summary>
internal sealed record ProbeTarget(string StationId, Uri Url)
{
    public string? StationName { get; init; }
    public string? CountryCode { get; init; }

    /// <summary>Adres wpisany ręcznie może prowadzić do sieci lokalnej; stacja z katalogu nigdy.</summary>
    public bool AllowLocal { get; init; }
}

/// <summary>Wynik jednego adresu. Trafia do raportu, więc nie ma w nim treści strumienia.</summary>
internal sealed class ProbeResult
{
    public required string StationId { get; init; }
    public required string Url { get; init; }
    public Uri? FinalUrl { get; set; }

    public int? HttpStatus { get; set; }
    public string? StatusLine { get; set; }
    public bool IcyStatusLine { get; set; }
    public string? ContentType { get; set; }

    /// <summary>Pierwsze bajty treści szesnastkowo, po zdjęciu metadanych ICY.</summary>
    public string? Signature { get; set; }

    public LiveCodec Codec { get; set; }
    public int? MeasuredKbps { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public int? HeaderKbps { get; set; }
    public int? MetaInt { get; set; }

    public double? HeadersSeconds { get; set; }
    public double? FirstAudioSeconds { get; set; }
    public double TotalSeconds { get; set; }

    public int Redirects { get; set; }
    public bool Https { get; set; }
    public string? CertificateError { get; set; }
    public IReadOnlyList<string> ForbiddenInUrl { get; set; } = [];

    public long BytesRead { get; set; }
    public double? Lufs { get; set; }
    public string? Title { get; set; }
    public LiveFailure? Failure { get; set; }
}
