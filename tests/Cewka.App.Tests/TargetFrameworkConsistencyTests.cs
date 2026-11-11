using System.Text.RegularExpressions;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Wersja .NET podana poza plikami projektów zgadza się z celem kompilacji z <c>Directory.Build.props</c>.
/// <para>
/// Cel kompilacji zmienia się w jednym miejscu, ale SDK wymagają też przepływy GitHuba, pakiet źródłowy
/// dla Archa i instrukcja budowania. Starsze SDK nie zbuduje nowszego celu (NETSDK1045), a błąd wychodzi
/// dopiero w czystym środowisku, np. przy makepkg.
/// </para>
/// </summary>
public sealed partial class TargetFrameworkConsistencyTests
{
    [GeneratedRegex(@"<TargetFramework>net(\d+)\.0</TargetFramework>")]
    private static partial Regex TargetFramework();

    // Każdy zapis wersji: w cudzysłowie pojedynczym, podwójnym albo bez.
    [GeneratedRegex(@"dotnet-version:\s*['""]?([^'""\s#]+)")]
    private static partial Regex WorkflowVersion();

    [GeneratedRegex(@"uses:\s*actions/setup-dotnet@")]
    private static partial Regex SetupDotnet();

    [GeneratedRegex(@"<TargetFrameworks?>([^<]*)</TargetFrameworks?>")]
    private static partial Regex AnyTargetFramework();

    [GeneratedRegex(@"makedepends=\(([^)]*)\)")]
    private static partial Regex MakeDepends();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cewka.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Nie odnaleziono katalogu repozytorium nad " + AppContext.BaseDirectory);
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative));

    private static int TargetMajor()
    {
        var match = TargetFramework().Match(Read("Directory.Build.props"));
        Assert.True(match.Success, "Brak <TargetFramework>netN.0</TargetFramework> w Directory.Build.props");
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>
    /// Każdy krok <c>setup-dotnet</c> w każdym przepływie podaje wersję SDK celu kompilacji;
    /// krok bez <c>dotnet-version</c> (np. z <c>global-json-file</c>) też jest błędem.
    /// </summary>
    [Fact]
    public void PrzeplywyGitHubaInstalujaSdkCeluKompilacji()
    {
        var workflows = Directory.GetFiles(Path.Combine(RepositoryRoot(), ".github", "workflows"), "*.yml");
        Assert.NotEmpty(workflows);
        var expected = $"{TargetMajor()}.0.x";

        foreach (var workflow in workflows)
        {
            var text = File.ReadAllText(workflow);
            var steps = SetupDotnet().Matches(text).Count;
            var versions = WorkflowVersion().Matches(text).Select(m => m.Groups[1].Value).ToList();

            Assert.True(steps == versions.Count,
                $"{Path.GetFileName(workflow)}: kroków setup-dotnet {steps}, wpisów dotnet-version {versions.Count}");
            Assert.All(versions, version => Assert.Equal(expected, version));
        }
    }

    /// <summary>Żaden projekt nie nadpisuje celu kompilacji z <c>Directory.Build.props</c> innym.</summary>
    [Fact]
    public void ProjektyNieNadpisujaCeluKompilacji()
    {
        var root = RepositoryRoot();
        var expected = $"net{TargetMajor()}.0";
        var projects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "artifacts"))
            .ToList();
        Assert.NotEmpty(projects);

        foreach (var project in projects)
            foreach (Match match in AnyTargetFramework().Matches(File.ReadAllText(project)))
                Assert.True(match.Groups[1].Value.Trim() == expected,
                    $"{Path.GetRelativePath(root, project)}: {match.Value}");
    }

    /// <summary>
    /// Pakiet źródłowy dla Archa wymaga SDK nie starszego niż cel kompilacji. Bieżący pakiet
    /// <c>dotnet-sdk</c> z ograniczeniem wersji zostaje dobry także po przejściu Archa na nowsze SDK.
    /// </summary>
    [Fact]
    public void PakietArchaWymagaSdkNieStarszegoNizCelKompilacji()
    {
        var match = MakeDepends().Match(Read("packaging/arch/PKGBUILD"));
        Assert.True(match.Success, "Brak makedepends w PKGBUILD");

        var sdk = Regex.Matches(match.Groups[1].Value, @"'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .Single(dependency => dependency.StartsWith("dotnet-sdk", StringComparison.Ordinal));

        Assert.Equal($"dotnet-sdk>={TargetMajor()}.0", sdk);
    }

    [Theory]
    [InlineData("README.md", "Potrzebny jest .NET SDK {0} ")]
    [InlineData("README.en.md", "You need the .NET {0} SDK ")]
    public void InstrukcjaBudowaniaPodajeSdkCeluKompilacji(string readme, string phrase)
    {
        Assert.Contains(string.Format(phrase, TargetMajor()), Read(readme));
    }
}
