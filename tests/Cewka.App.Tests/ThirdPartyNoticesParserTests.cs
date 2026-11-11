using System.Text.RegularExpressions;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Czytanie <c>THIRD-PARTY-NOTICES.txt</c> do listy składników widoku „Licencje składników”.
/// Sprawdziany na prawdziwym pliku pilnują, żeby nowa sekcja w notach nie zniknęła z widoku
/// po cichu, a na zmyślonych tekstach — pól, końców wierszy i odporności na uszkodzenia.
/// </summary>
public sealed partial class ThirdPartyNoticesParserTests
{
    private static readonly string Rule = new('=', 80);

    private static readonly string Dashes = new('-', 80);

    /// <summary>Wiersz spisu: „  12. Nazwa”.</summary>
    [GeneratedRegex(@"^\s*(\d+)\.\s+(\S.*?)\s*$")]
    private static partial Regex IndexLine();

    [GeneratedRegex(@"^Licencja:\s", RegexOptions.Multiline)]
    private static partial Regex LicenceField();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cewka.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Nie odnaleziono katalogu repozytorium nad " + AppContext.BaseDirectory);
    }

    /// <summary>Tekst pliku not z końcami wierszy <c>\n</c>, niezależnie od ustawień gita.</summary>
    internal static string RealFile() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.txt")).Replace("\r\n", "\n");

    /// <summary>Numery i nazwy ze spisu na początku pliku, czytane niezależnie od parsera.</summary>
    private static List<(int Number, string Name)> IndexOfFile(string text)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith("Spis / Contents", StringComparison.Ordinal));
        Assert.True(start >= 0, "plik not nie ma spisu „Spis / Contents”");

        var entries = new List<(int, string)>();
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("====", StringComparison.Ordinal); i++)
        {
            var match = IndexLine().Match(lines[i]);
            if (match.Success) entries.Add((int.Parse(match.Groups[1].Value), match.Groups[2].Value));
        }

        return entries;
    }

    private static string Section(int number, string name, string metadata, string licenceText) =>
        $"{Rule}\n{number}. {name}\n{Rule}\n\n{metadata}\n\n{Dashes}\n\n{licenceText}\n\n\n";

    [Fact]
    public void PlikDajeTyleSkladnikowIleJestPozycjiWSpisie()
    {
        var text = RealFile();
        var index = IndexOfFile(text);

        var components = ThirdPartyNotices.Parse(text);

        Assert.NotEmpty(index);
        Assert.Equal(index.Count, components.Count);
    }

    [Fact]
    public void NumeracjaINazwyZgodneZeSpisem()
    {
        var text = RealFile();

        var components = ThirdPartyNotices.Parse(text);

        Assert.Equal(
            IndexOfFile(text),
            components.Select(component => (component.Number, component.Name)).ToList());
    }

    [Fact]
    public void KazdySkladnikMaNazweLicencjeITekst()
    {
        var text = RealFile();

        var components = ThirdPartyNotices.Parse(text);

        Assert.All(components, component =>
        {
            Assert.False(string.IsNullOrWhiteSpace(component.Name), $"sekcja {component.Number} bez nazwy");
            Assert.False(string.IsNullOrWhiteSpace(component.LicenceText), $"{component.Name}: pusty tekst licencji");
            Assert.DoesNotContain('(', component.Licence);
            Assert.DoesNotContain('\r', component.LicenceText);
        });

        // Nazwę licencji ma każda sekcja, która podaje pole „Licencja:”. Sekcja zbiorcza
        // bibliotek natywnych go nie ma: jej tekst składa się z wielu licencji.
        var withLicenceField = LicenceField().Matches(text).Count;
        Assert.Equal(withLicenceField, components.Count(component => component.Licence.Length > 0));
        Assert.True(withLicenceField >= components.Count - 1,
            "więcej niż jedna sekcja bez pola „Licencja:”; sekcja zbiorcza jest wyjątkiem");
    }

    [Fact]
    public void PierwszySkladnikToAvaloniaPodLicencjaMit()
    {
        var first = ThirdPartyNotices.Parse(RealFile())[0];

        Assert.Equal(1, first.Number);
        Assert.Equal("Avalonia", first.Name);
        Assert.Equal("MIT", first.Licence);
        Assert.Contains(first.Packages, package => package.StartsWith("Avalonia ", StringComparison.Ordinal));
        Assert.StartsWith("https://", first.Project, StringComparison.Ordinal);
        Assert.StartsWith("The MIT License", first.LicenceText, StringComparison.Ordinal);
    }

    [Fact]
    public void KoncowkiWierszyNieZmieniajaWyniku()
    {
        var lf = RealFile();
        var crlf = lf.Replace("\n", "\r\n");

        var fromLf = ThirdPartyNotices.Parse(lf);
        var fromCrlf = ThirdPartyNotices.Parse(crlf);

        Assert.Equal(fromLf.Count, fromCrlf.Count);
        for (var i = 0; i < fromLf.Count; i++)
        {
            Assert.Equal(fromLf[i].Number, fromCrlf[i].Number);
            Assert.Equal(fromLf[i].Name, fromCrlf[i].Name);
            Assert.Equal(fromLf[i].Licence, fromCrlf[i].Licence);
            Assert.Equal(fromLf[i].Copyright, fromCrlf[i].Copyright);
            Assert.Equal(fromLf[i].Packages, fromCrlf[i].Packages);
            Assert.Equal(fromLf[i].Projects, fromCrlf[i].Projects);
            Assert.Equal(fromLf[i].LicenceText, fromCrlf[i].LicenceText);
        }
    }

    [Fact]
    public void PolaSekcjiTrafiajaDoWlasciwychWlasciwosci()
    {
        var metadata =
            "Pakiet NuGet:    Alfa 1.0.0\n" +
            "Pakiet NuGet:    Alfa.Beta 1.0.0\n" +
            "Zastosowanie:    tylko wydanie dla Windows\n" +
            "Projekt:         https://example.org/alfa\n" +
            "                 https://example.org/alfa-zrodla\n" +
            "Prawa autorskie: Copyright 2020 Jan Kowalski\n" +
            "                 Copyright 2021 Anna Nowak\n" +
            "Licencja:        BSD, 3 klauzule (plik LICENSE pakietu); noty biblioteki natywnej";
        var text = "Nagłówek\n\n" + Section(1, "Alfa", metadata, "Pierwszy wiersz licencji.\n\n  Wcięty wiersz.");

        var component = Assert.Single(ThirdPartyNotices.Parse(text));

        Assert.Equal(1, component.Number);
        Assert.Equal("Alfa", component.Name);
        Assert.Equal(new[] { "Alfa 1.0.0", "Alfa.Beta 1.0.0" }, component.Packages.ToArray());
        Assert.Equal(new[] { "https://example.org/alfa", "https://example.org/alfa-zrodla" }, component.Projects.ToArray());
        Assert.Equal("https://example.org/alfa", component.Project);
        Assert.Equal("Copyright 2020 Jan Kowalski\nCopyright 2021 Anna Nowak", component.Copyright);
        Assert.Equal("BSD, 3 klauzule", component.Licence);
        Assert.Equal("Pierwszy wiersz licencji.\n\n  Wcięty wiersz.", component.LicenceText);
    }

    [Fact]
    public void WierszZNumeremWTekscieLicencjiNieZaczynaNowejSekcji()
    {
        var licenceText = "1. Definitions.\n\n2. Grant of Copyright License.\n" + new string('=', 80) + "\n3. Cos\n";
        var text = Section(1, "Alfa", "Licencja: MIT", licenceText) + Section(2, "Beta", "Licencja: Apache-2.0", "Tekst beta.");

        var components = ThirdPartyNotices.Parse(text);

        Assert.Equal(new[] { "Alfa", "Beta" }, components.Select(component => component.Name).ToArray());
        Assert.Contains("2. Grant of Copyright License.", components[0].LicenceText);
        Assert.Equal("Tekst beta.", components[1].LicenceText);
    }

    [Theory]
    [InlineData("MIT (plik LICENSE repozytorium; pakiet nie zawiera tekstu)", "MIT")]
    [InlineData("BSD, 3 klauzule (plik LICENSE pakietu)", "BSD, 3 klauzule")]
    [InlineData("MPL 1.1, do wyboru GPL 2.0+ lub LGPL 2.1+; Cewka korzysta z MPL 1.1", "MPL 1.1, do wyboru GPL 2.0+ lub LGPL 2.1+")]
    [InlineData("Unlicense albo MIT-0, do wyboru (plik LICENSE projektu)", "Unlicense albo MIT-0, do wyboru")]
    [InlineData("SIL Open Font License 1.1", "SIL Open Font License 1.1")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NazwaLicencjiJestSkrocona(string? field, string expected) =>
        Assert.Equal(expected, ThirdPartyNotices.ShortLicenceName(field));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("To nie jest plik not o licencjach.\nDruga linia bez żadnej sekcji.")]
    [InlineData("\0\u0001\u0002 ¤¤¤ ��\n\0\0")]
    public void UszkodzonyPlikDajePustaListeBezWyjatku(string? text) =>
        Assert.Empty(ThirdPartyNotices.Parse(text));

    [Fact]
    public void SekcjaBezKresekAlboBezTekstuJestPomijana()
    {
        // Nagłówek urwany po tytule, nagłówek bez linii „-” i sekcja z linią „-”, ale bez tekstu.
        var truncated = $"{Rule}\n1. Alfa\n{Rule}\n";
        var noDashes = $"{Rule}\n1. Alfa\n{Rule}\n\nLicencja: MIT\nTekst bez kresek.\n";
        var noText = $"{Rule}\n1. Alfa\n{Rule}\n\nLicencja: MIT\n\n{Dashes}\n\n   \n";

        Assert.Empty(ThirdPartyNotices.Parse(truncated));
        Assert.Empty(ThirdPartyNotices.Parse(noDashes));
        Assert.Empty(ThirdPartyNotices.Parse(noText));
    }

    [Fact]
    public void NumeracjaNieZaczynajacaSieOdJednegoNieDajeSekcji() =>
        Assert.Empty(ThirdPartyNotices.Parse(Section(3, "Gamma", "Licencja: MIT", "Tekst.")));
}
