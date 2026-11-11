using System.Text.RegularExpressions;

namespace Cewka.Audio.Streaming;

/// <summary>
/// Elementy, których nie może mieć adres stacji: dane słuchacza i sesji, reklama, oznaczenia
/// odtwarzacza innej platformy i tokeny. Wspólne dla narzędzia katalogu i testu katalogu.
/// </summary>
internal static partial class ForbiddenUrlParts
{
    private static readonly HashSet<string> Parameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "listenerid", "awparams", "amsparams", "preroll", "aggregator", "dist", "source", "origem", "origine",
        "devicename", "playerid", "playsessionid", "sabc", "gdpr", "gdpr_consent", "rj-tok", "zt",
    };

    private const string ParameterPrefix = "aw_0_";

    private static readonly HashSet<string> PathParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "tunein", "airable", "homepage", "mytuner", "radiogarden", "radioplayer", "adsonline",
    };

    [GeneratedRegex(@"eyJ[\w-]+\.[\w-]+\.[\w-]+")]
    private static partial Regex WebToken();

    /// <summary>Nazwy znalezionych elementów, każda raz: parametry, potem JWT i człony ścieżki.</summary>
    public static IReadOnlyList<string> Find(Uri url)
    {
        var found = new List<string>();
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0];
            if (Parameters.Contains(name) || name.StartsWith(ParameterPrefix, StringComparison.OrdinalIgnoreCase))
                AddOnce(found, name.ToLowerInvariant());
        }

        if (WebToken().IsMatch(url.AbsolutePath + url.Query))
            found.Add("JWT");

        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (PathParts.Contains(parts[i]) && !IsServerPlaylist(parts, i))
                AddOnce(found, "/" + parts[i].ToLowerInvariant());
        }

        return found;
    }

    /// <summary>Serwer Centova Cast publikuje listy pod /tunein/nazwa.pls; to jego funkcja, nie nazwa platformy.</summary>
    private static bool IsServerPlaylist(string[] parts, int index) =>
        parts[index].Equals("tunein", StringComparison.OrdinalIgnoreCase)
        && index == parts.Length - 2
        && parts[^1].EndsWith(".pls", StringComparison.OrdinalIgnoreCase);

    private static void AddOnce(List<string> found, string name)
    {
        if (!found.Contains(name))
            found.Add(name);
    }
}
