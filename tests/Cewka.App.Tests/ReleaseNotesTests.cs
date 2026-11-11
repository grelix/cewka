using System.Text.RegularExpressions;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Opis szkicu wydania powstaje z sekcji CHANGELOG.md dla wersji z Directory.Build.props
/// (<c>.github/workflows/release.yml</c>); bez tej sekcji przepływ wydania kończy się błędem.
/// </summary>
public sealed partial class ReleaseNotesTests
{
    [GeneratedRegex(@"<Version>([^<]+)</Version>")]
    private static partial Regex Version();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cewka.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Nie odnaleziono katalogu repozytorium nad " + AppContext.BaseDirectory);
    }

    /// <summary>Wiersze sekcji wersji, tak jak wycina je przepływ wydania: od nagłówka do następnego nagłówka „## ”.</summary>
    internal static List<string> Section(string changelog, string version)
    {
        var lines = new List<string>();
        var inside = false;
        foreach (var line in changelog.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inside = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) == version;
                continue;
            }

            if (inside) lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public void ChangelogMaSekcjeBiezacejWersji()
    {
        var root = RepositoryRoot();
        var match = Version().Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        Assert.True(match.Success, "Brak <Version> w Directory.Build.props");

        var section = Section(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")), match.Groups[1].Value);

        Assert.Contains(section, line => line.StartsWith("- ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Wynik skryptu wersji trafia najpierw do zmiennej. W <c>echo "x=$(skrypt)"</c> jego porażka ginie
    /// nawet przy <c>bash -e</c>, więc znacznik niezgodny z wersją przeszedłby sprawdzenie z pustą wersją.
    /// </summary>
    [Fact]
    public void PorazkaSkryptuWersjiZatrzymujePrzeplyw()
    {
        var workflows = Directory.GetFiles(Path.Combine(RepositoryRoot(), ".github", "workflows"), "*.yml");
        Assert.NotEmpty(workflows);

        var swallowed = workflows
            .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, number: i + 1)))
            .Where(x => SwallowedSubstitution().IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.number}: {x.line.Trim()}")
            .ToList();

        Assert.True(swallowed.Count == 0, "podstawienie w argumencie echo: " + string.Join("; ", swallowed));
    }

    [GeneratedRegex(@"\b(echo|printf)\b.*\$\(\s*\./tools/")]
    private static partial Regex SwallowedSubstitution();

    [Fact]
    public void SekcjaKonczySieNaNastepnejWersjiINieLapieWersjiOPodobnymNumerze()
    {
        const string changelog = "# Zmiany\n\n## 1.0.10 (2027-01-02)\n\n- inna\n\n## 1.0.1 (2027-01-01)\n\n- ta\n\n## 1.0.0\n\n- starsza\n";

        Assert.Equal(["", "- ta", ""], Section(changelog, "1.0.1"));
    }
}
