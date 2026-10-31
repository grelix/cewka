using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Noty składników w <c>THIRD-PARTY-NOTICES.txt</c>, dołączanym do każdego wydania.
/// <para>
/// Licencje pakietów i fontu pozwalają je rozpowszechniać pod warunkiem dołączenia not. Nowy
/// pakiet albo zmiana wersji bez sekcji w pliku not łamałaby ten warunek po cichu.
/// </para>
/// </summary>
public sealed partial class ThirdPartyNoticesTests
{
    private const string NoticesFile = "THIRD-PARTY-NOTICES.txt";

    private static readonly string[] FontExtensions = [".otf", ".ttf", ".ttc", ".woff", ".woff2"];

    [GeneratedRegex(@"^Pakiet NuGet:\s+(\S+)\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex PackageLine();

    [GeneratedRegex(@"^Plik:\s+(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex FileLine();

    [GeneratedRegex(@"<(\w+)>([^<]*)</\1>")]
    private static partial Regex Property();

    [GeneratedRegex(@"\$\((\w+)\)")]
    private static partial Regex PropertyReference();

    /// <summary>Katalog główny repozytorium, odnaleziony w górę od katalogu testów.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cewka.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Nie odnaleziono katalogu repozytorium nad " + AppContext.BaseDirectory);
    }

    private static string Notices(string root) => File.ReadAllText(Path.Combine(root, NoticesFile));

    /// <summary>Pakiety z projektów programu, z wersją po podstawieniu właściwości z Directory.Build.props.</summary>
    internal static List<(string Name, string Version)> ReleasePackages(string root)
    {
        var properties = Property().Matches(File.ReadAllText(Path.Combine(root, "Directory.Build.props")))
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Trim());

        string Resolve(string value) => PropertyReference().Replace(value, m =>
            properties.TryGetValue(m.Groups[1].Value, out var resolved)
                ? resolved
                : throw new InvalidOperationException($"Nieznana właściwość {m.Value}"));

        var packages = new List<(string, string)>();
        foreach (var project in Directory.GetDirectories(Path.Combine(root, "src")).SelectMany(d => Directory.GetFiles(d, "*.csproj")))
        {
            foreach (var reference in XDocument.Load(project).Descendants("PackageReference"))
            {
                // Pakiet deweloperski (analizator, narzędzie budowania) nie trafia do wydania.
                var privateAssets = (string?)reference.Attribute("PrivateAssets") ?? (string?)reference.Element("PrivateAssets");
                if (string.Equals(privateAssets?.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = (string?)reference.Attribute("Include")
                    ?? throw new InvalidOperationException($"PackageReference bez Include w {project}");
                var version = (string?)reference.Attribute("Version") ?? (string?)reference.Element("Version")
                    ?? throw new InvalidOperationException($"{name} bez wersji w {project}");

                packages.Add((name, Resolve(version)));
            }
        }

        return packages;
    }

    /// <summary>Pary nazwa–wersja z wierszy „Pakiet NuGet:” pliku not.</summary>
    internal static HashSet<string> NoticedPackages(string notices) =>
        PackageLine().Matches(notices)
            .Select(m => m.Groups[1].Value + " " + m.Groups[2].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void KazdyPakietProgramuMaSekcjeWNotach()
    {
        var root = RepositoryRoot();
        var packages = ReleasePackages(root);
        var noticed = NoticedPackages(Notices(root));

        Assert.NotEmpty(packages);
        var missing = packages.Select(p => p.Name + " " + p.Version).Where(p => !noticed.Contains(p)).ToList();
        Assert.True(missing.Count == 0,
            $"brak sekcji w {NoticesFile} (wiersz „Pakiet NuGet: nazwa wersja”) dla: {string.Join(", ", missing)}");
    }

    [Fact]
    public void KazdyFontMaSekcjeWNotach()
    {
        var root = RepositoryRoot();
        var fonts = Directory.GetFiles(Path.Combine(root, "src", "Cewka.App", "Assets", "Fonts"))
            .Where(f => FontExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();
        var noticed = FileLine().Matches(Notices(root)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(fonts);
        var missing = fonts.Where(f => !noticed.Contains(f!)).ToList();
        Assert.True(missing.Count == 0,
            $"brak sekcji w {NoticesFile} (wiersz „Plik: nazwa”) dla: {string.Join(", ", missing)}");
    }
}
