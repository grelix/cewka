using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cewka.Audio.Streaming;

namespace Cewka.RadioProbe;

/// <summary>Raport JSON i tekstowy. Materiał o stacjach nie może trafić do publicznego repozytorium.</summary>
internal sealed class ProbeReport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public required DateTimeOffset Generated { get; init; }
    public string? MeasuredFrom { get; init; }
    public required string UserAgent { get; init; }
    public required double LimitSeconds { get; init; }
    public required int LimitKb { get; init; }
    public required ProbeSummary Summary { get; init; }
    public required IReadOnlyList<ProbeResult> Streams { get; init; }
    public required IReadOnlyList<LogoResult> Logos { get; init; }

    /// <summary>Ścieżka domyślna w katalogu tymczasowym systemu.</summary>
    public static string DefaultPath(DateTimeOffset now) =>
        Path.Combine(Path.GetFullPath(Path.GetTempPath()), $"cewka-radioprobe-{now:yyyyMMdd-HHmmss}.json");

    /// <summary>Katalog roboczy gita, także dołączone drzewo, gdzie .git jest plikiem; null poza repozytorium.</summary>
    public static string? FindRepository(string reportPath)
    {
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
             directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            var git = Path.Combine(directory, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return directory;
        }

        return null;
    }

    public void Write(string jsonPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        File.WriteAllText(Path.ChangeExtension(jsonPath, ".txt"), ToText(), new UTF8Encoding(false));
    }

    public static string StreamLine(ProbeResult r) =>
        string.Join(" | ", new[]
        {
            r.Failure is { } failure ? failure.Reason.ToString() : "OK",
            r.StationId,
            r.StatusLine,
            r.ContentType,
            Format(r),
            r.Url,
        }.Where(part => !string.IsNullOrEmpty(part)));

    public static string LogoLine(LogoResult r) =>
        string.Join(" | ", new[]
        {
            r.Failure is { } failure ? failure.Reason.ToString() : "OK",
            r.StationId,
            "logo",
            r.HttpStatus?.ToString(CultureInfo.InvariantCulture),
            r.Format is null ? null : $"{r.Format} {r.Width}x{r.Height}",
            r.TooLarge ? "ponad 2 MB" : null,
            r.Url,
        }.Where(part => !string.IsNullOrEmpty(part)));

    private string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(Invariant($"Cewka.RadioProbe, {Generated:yyyy-MM-dd HH:mm} UTC"));
        text.AppendLine($"Pomiar z: {MeasuredFrom ?? "nie podano"}");
        text.AppendLine(Invariant($"Limit na adres: {LimitSeconds:0.#} s, {LimitKb} KB"));
        text.AppendLine($"User-Agent: {UserAgent}");
        text.AppendLine();

        foreach (var stream in Streams)
            AppendStream(text, stream);
        foreach (var logo in Logos)
            text.AppendLine(LogoLine(logo));

        text.AppendLine();
        text.AppendLine(Invariant($"Adresy: {Summary.Addresses}, bez błędu {Summary.Ok}, z błędem {Summary.Failed}"));
        AppendCounts(text, "Przyczyny", Summary.Reasons);
        AppendCounts(text, "Kodeki z ramek", Summary.Codecs);
        AppendCounts(text, "Elementy zakazane", Summary.Forbidden);
        if (Logos.Count > 0)
            text.AppendLine(Invariant($"Logo: {Logos.Count}, z błędem {Summary.LogosFailed}"));
        return text.ToString();
    }

    private static void AppendStream(StringBuilder text, ProbeResult r)
    {
        text.AppendLine(StreamLine(r));

        var details = new List<string>();
        if (r.Signature is not null) details.Add($"sygnatura {r.Signature}");
        if (r.HeaderKbps is { } header) details.Add(Invariant($"icy-br {header}"));
        if (r.MetaInt is { } metaInt) details.Add(Invariant($"metaint {metaInt}"));
        if (r.HeadersSeconds is { } headers) details.Add(Invariant($"nagłówki {headers:0.00} s"));
        if (r.FirstAudioSeconds is { } first) details.Add(Invariant($"dźwięk {first:0.00} s"));
        details.Add(Invariant($"całość {r.TotalSeconds:0.00} s"));
        details.Add(Invariant($"{r.BytesRead / 1024} KB"));
        details.Add(Invariant($"przekierowania {r.Redirects}"));
        details.Add(r.Https ? "https" : "http");
        if (r.CertificateError is not null) details.Add($"certyfikat {r.CertificateError}");
        if (r.Lufs is { } lufs) details.Add(Invariant($"{lufs:0.0} LUFS"));
        text.Append("  ").AppendLine(string.Join(", ", details));

        if (r.FinalUrl is { } final && final.OriginalString != r.Url)
            text.Append("  adres końcowy: ").AppendLine(final.OriginalString);
        if (r.ForbiddenInUrl.Count > 0)
            text.Append("  zakazane w adresie: ").AppendLine(string.Join(", ", r.ForbiddenInUrl));
        if (r.Title is not null)
            text.Append("  tytuł: ").AppendLine(r.Title);
        if (r.Failure?.Detail is { } detail)
            text.Append("  szczegół: ").AppendLine(detail);
    }

    private static string? Format(ProbeResult r)
    {
        if (r.Codec == LiveCodec.Unknown)
            return null;

        var format = r.Codec.ToString();
        if (r.MeasuredKbps is { } kbps) format += Invariant($" {kbps} kb/s");
        if (r.SampleRate is { } rate) format += Invariant($" {rate} Hz");
        if (r.Channels is { } channels) format += Invariant($" {channels} kan.");
        return format;
    }

    private static void AppendCounts(StringBuilder text, string label, IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
            return;

        var items = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => Invariant($"{c.Key} {c.Value}"));
        text.AppendLine($"{label}: {string.Join(", ", items)}");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

internal sealed record ProbeSummary(
    int Addresses, int Ok, int Failed, int LogosFailed,
    IReadOnlyDictionary<string, int> Reasons,
    IReadOnlyDictionary<string, int> Codecs,
    IReadOnlyDictionary<string, int> Forbidden)
{
    public static ProbeSummary From(IReadOnlyList<ProbeResult> streams, IReadOnlyList<LogoResult> logos)
    {
        var failed = streams.Count(s => s.Failure is not null);
        return new ProbeSummary(
            streams.Count, streams.Count - failed, failed, logos.Count(l => l.Failure is not null),
            Count(streams.Where(s => s.Failure is not null).Select(s => s.Failure!.Value.Reason.ToString())),
            Count(streams.Where(s => s.Codec != LiveCodec.Unknown).Select(s => s.Codec.ToString())),
            Count(streams.SelectMany(s => s.ForbiddenInUrl)));
    }

    private static Dictionary<string, int> Count(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count());
}
