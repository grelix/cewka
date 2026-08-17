using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cewka.Platform;

namespace Cewka.App.Services;

/// <summary>Wynik sprawdzenia: co udało się ustalić i czy w ogóle się udało.</summary>
public sealed record UpdateResult
{
    /// <summary>Wersja najnowszego wydania albo <c>null</c>, gdy nie udało się jej odczytać.</summary>
    public Version? Latest { get; init; }

    /// <summary>Adres strony wydania, wzięty z odpowiedzi serwisu.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>Ustawione, gdy sprawdzenie się nie udało; treść przeznaczona do dziennika, nie do okna.</summary>
    public string? Failure { get; init; }

    public bool Succeeded => Failure is null && Latest is not null;
}

/// <summary>Kształt odpowiedzi serwisu GitHuba — tylko te pola, które są tu potrzebne.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;

/// <summary>
/// Pyta serwis GitHuba o najnowsze wydanie i porównuje je z wersją działającego programu.
///
/// <para><b>Co dokładnie się dzieje.</b> Jedno żądanie GET na
/// <c>api.github.com/repos/…/releases/latest</c>, bez uwierzytelniania, z ośmiosekundowym
/// ograniczeniem czasu. Program nie wysyła niczego o odtwarzanych plikach ani o użytkowniku —
/// ale GitHub, jak każdy serwer, widzi adres IP i nagłówek identyfikujący program. Dlatego
/// sprawdzanie automatyczne jest domyślnie wyłączone i nic się nie dzieje, dopóki nikt o to
/// nie poprosi.</para>
///
/// <para><b>Czego nie robi.</b> Nie pobiera plików, nie aktualizuje się sam i nie przechowuje
/// wyniku. Jedyne, co zostaje zapisane, to data ostatniego sprawdzenia — po to, żeby przy
/// sprawdzaniu automatycznym nie odpytywać serwisu częściej niż raz na dobę.</para>
/// </summary>
public static class UpdateCheck
{
    /// <summary>Najkrótszy odstęp między sprawdzeniami automatycznymi.</summary>
    public static readonly TimeSpan AutomaticInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Adres repozytorium z metadanych zestawu. Wpisany raz, w Directory.Build.props, skąd biorą
    /// go także skrypt pakujący i PKGBUILD.
    /// </summary>
    public static string Repository { get; } = typeof(UpdateCheck).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(metadata => metadata.Key == "RepositoryUrl")?.Value ?? string.Empty;

    /// <summary>
    /// Wersja działającego programu, w trzech członach — w takiej postaci, w jakiej występuje
    /// w znacznikach wydań.
    /// </summary>
    public static Version Current { get; } = ReadCurrent();

    private static Version ReadCurrent()
    {
        var version = typeof(UpdateCheck).Assembly.GetName().Version;
        return version is null
            ? new Version(0, 0, 0)
            : new Version(version.Major, version.Minor, version.Build);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Serwis GitHuba odrzuca żądania bez nagłówka identyfikującego program. Podana jest sama
    /// nazwa i wersja — bez nazwy systemu, nazwy użytkownika czy czegokolwiek o bibliotece.
    /// </summary>
    private static string UserAgent(Version current) => $"Cewka/{current.ToString(3)}";

    /// <summary>
    /// Buduje adres usługi z adresu repozytorium. Zwraca <c>null</c>, gdy adres nie wygląda
    /// na repozytorium GitHuba — wtedy sprawdzanie po prostu nie jest dostępne.
    /// </summary>
    public static string? ApiUrl(string? repositoryUrl)
    {
        if (!WebLink.IsSafe(repositoryUrl)) return null;

        var uri = new Uri(repositoryUrl!);
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        return $"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest";
    }

    /// <summary>Strona wydań tego repozytorium.</summary>
    public static string? ReleasesUrl(string? repositoryUrl) =>
        WebLink.IsSafe(repositoryUrl) ? repositoryUrl!.TrimEnd('/') + "/releases" : null;

    /// <summary>
    /// Odczytuje numer wersji ze znacznika wydania. Znaczniki mają postać <c>v0.7.0</c>,
    /// więc wiodąca litera jest odcinana; wszystko po znaku minus (oznaczenia przedwydań)
    /// również, bo <see cref="Version"/> tego nie przyjmuje.
    /// </summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];

        // Version przyjmuje też zapis dwuczłonowy, ale porównanie ma być na trzech członach:
        // „0.8" i „0.8.0" to to samo wydanie, a Version uznałby pierwsze za starsze.
        if (!Version.TryParse(text, out var parsed)) return null;

        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
    }

    public static async Task<UpdateResult> LatestAsync(
        string? repositoryUrl, Version current, CancellationToken token = default)
    {
        var api = ApiUrl(repositoryUrl);
        if (api is null) return new UpdateResult { Failure = "adres repozytorium nie wskazuje na GitHuba" };

        try
        {
            using var client = new HttpClient { Timeout = Timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent(current));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await client.GetAsync(api, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UpdateResult { Failure = $"serwis odpowiedział {(int)response.StatusCode}" };

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(json, ReleaseJsonContext.Default.GitHubRelease);

            if (release is null) return new UpdateResult { Failure = "odpowiedź nie dała się odczytać" };

            // Punkt „latest" pomija wersje robocze i przedwydania, ale sprawdzenie jest tanie,
            // a serwis mógłby kiedyś zachować się inaczej.
            if (release.Draft || release.Prerelease)
                return new UpdateResult { Failure = "najnowsze wydanie jest wersją roboczą" };

            var latest = ParseTag(release.TagName);
            if (latest is null)
                return new UpdateResult { Failure = $"znacznik {release.TagName} nie jest numerem wersji" };

            return new UpdateResult
            {
                Latest = latest,
                ReleaseUrl = WebLink.IsSafe(release.HtmlUrl) ? release.HtmlUrl : ReleasesUrl(repositoryUrl),
            };
        }
        catch (OperationCanceledException)
        {
            return new UpdateResult { Failure = "sprawdzanie przerwane albo przekroczyło czas" };
        }
        catch (Exception ex)
        {
            // Brak połączenia jest zwyczajną sytuacją, nie awarią programu.
            return new UpdateResult { Failure = ex.Message };
        }
    }
}
