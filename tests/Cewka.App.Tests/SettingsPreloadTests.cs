using Cewka.App.Models;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Start programu: ustawienie jednej kopii czytane bez pełnego odczytu ustawień i pełny odczyt
/// w tle przed startem Avalonii.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsPreloadTests
{
    private static string FileWith(string json)
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    [InlineData("""{"singleInstance":false}""", false)]
    [InlineData("""{"singleInstance":true}""", true)]
    [InlineData("""{"volume":0.5}""", true)]
    [InlineData("""{"window":{"singleInstance":false},"volume":0.5}""", true)]
    [InlineData("""{"singleInstance":true,"singleInstance":false}""", false)]
    [InlineData("""{ /* komentarz */ "singleInstance": false, }""", false)]
    [InlineData("null", true)]
    [InlineData("""{"singleInstance":false,"volume":""", true)]
    [InlineData("""{"singleInstance":"nie"}""", true)]
    [InlineData("""{"singleInstance":null}""", true)]
    [InlineData("to nie jest JSON", true)]
    [InlineData("", true)]
    public void FlagaJednejKopiiJestTaSamaCoPrzyPelnymOdczycie(string json, bool expected)
    {
        Assert.Equal(expected, SettingsStore.ReadSingleInstanceFlag(FileWith(json)));
    }

    /// <summary>
    /// Plik z poprawną flagą, ale odrzucony w całości przez pełny odczyt (zła wartość innego
    /// ustawienia), daje ustawienia domyślne, czyli jedną kopię. Flaga nie może mówić co innego
    /// niż magazyn, bo program działałby wtedy jako niezarejestrowana kopia.
    /// </summary>
    [Theory]
    [InlineData("""{"singleInstance":false,"volume":"głośno"}""")]
    [InlineData("""{"singleInstance":false,"theme":"Rozowy"}""")]
    public void FlagaZPlikuOdrzuconegoPrzezPelnyOdczytJestDomyslna(string json) => Ui.Run(() =>
    {
        var path = FileWith(json);

        Assert.True(SettingsStore.ReadSingleInstanceFlag(path));
        Assert.True(new SettingsStore(path).Current.SingleInstance);
    });

    /// <summary>Flaga zgadza się z magazynem dla każdego pliku z tej listy, także odrzucanego i w innym kodowaniu.</summary>
    [Theory]
    [InlineData("""{"singleInstance":false}""", false)]
    [InlineData("""{"singleInstance":false,"volume":0.5,"theme":"Dark"}""", false)]
    [InlineData("""{"singleInstance":false,"volume":"głośno"}""", false)]
    [InlineData("""{"singleInstance":false}""", true)]
    [InlineData("""{"singleInstance":true}""", true)]
    [InlineData("""{"volume":0.5}""", true)]
    public void FlagaJednejKopiiZgadzaSieZMagazynem(string json, bool utf16) => Ui.Run(() =>
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, json, utf16 ? System.Text.Encoding.Unicode : new System.Text.UTF8Encoding(false));

        var flag = SettingsStore.ReadSingleInstanceFlag(path);

        Assert.Equal(new SettingsStore(path).Current.SingleInstance, flag);
    });

    /// <summary>Potwierdzenie wyłączonej jednej kopii nie czyta pliku dwa razy: magazyn dostaje ten sam odczyt.</summary>
    [Fact]
    public void WylaczonaJednaKopiaJestPotwierdzanaPelnymOdczytemKtoryPrzejmujeMagazyn() => Ui.Run(() =>
    {
        var path = FileWith("""{"schemaVersion":1,"singleInstance":false,"volume":0.25}""");

        Assert.False(SettingsStore.ReadSingleInstanceFlag(path));
        File.WriteAllText(path, """{"schemaVersion":1,"singleInstance":false,"volume":0.75}""");
        SettingsStore.Preload(path);

        Assert.Equal(0.25, new SettingsStore(path).Current.Volume);
    });

    [Fact]
    public void FlagaJednejKopiiBezPlikuJestDomyslna()
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");

        Assert.Equal(new AppSettings().SingleInstance, SettingsStore.ReadSingleInstanceFlag(path));
    }

    /// <summary>Plik zapisany przez program (ze znacznikiem kolejności bajtów albo bez) daje tę samą flagę.</summary>
    [Fact]
    public void FlagaJednejKopiiZPlikuZapisanegoPrzezProgram() => Ui.Run(() =>
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        var store = new SettingsStore(path);
        store.Current.SingleInstance = false;
        store.SaveNow();

        Assert.False(SettingsStore.ReadSingleInstanceFlag(path));

        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(path).SkipWhile(b => b is 0xEF or 0xBB or 0xBF)]);

        Assert.False(SettingsStore.ReadSingleInstanceFlag(path));
    });

    [Fact]
    public void MagazynBierzeUstawieniaWczytaneWTle() => Ui.Run(() =>
    {
        var path = FileWith("""{"schemaVersion":1,"volume":0.25,"singleInstance":false}""");

        SettingsStore.Preload(path);
        var store = new SettingsStore(path);

        Assert.Equal(0.25, store.Current.Volume);
        Assert.False(store.Current.SingleInstance);
    });

    /// <summary>Wynik odczytu w tle służy jednemu magazynowi: następny widzi plik taki, jaki jest teraz.</summary>
    [Fact]
    public void OdczytWTleSluzyTylkoPierwszemuMagazynowi() => Ui.Run(() =>
    {
        var path = FileWith("""{"schemaVersion":1,"volume":0.25}""");

        SettingsStore.Preload(path);
        _ = new SettingsStore(path);
        File.WriteAllText(path, """{"schemaVersion":1,"volume":0.75}""");

        Assert.Equal(0.75, new SettingsStore(path).Current.Volume);
    });

    [Fact]
    public void OdczytWTleInnegoPlikuNieJestUzywany() => Ui.Run(() =>
    {
        var other = FileWith("""{"schemaVersion":1,"volume":0.25}""");
        var path = FileWith("""{"schemaVersion":1,"volume":0.75}""");

        SettingsStore.Preload(other);

        Assert.Equal(0.75, new SettingsStore(path).Current.Volume);
        Assert.Equal(0.25, new SettingsStore(other).Current.Volume);
    });

    /// <summary>
    /// Nieczytelny plik wczytany w tle kończy tak samo jak czytany wprost: ustawienia domyślne
    /// i kopia oryginału obok, zrobiona dopiero przez magazyn.
    /// </summary>
    [Fact]
    public void NieczytelnyPlikWczytanyWTleZostawiaKopie() => Ui.Run(() =>
    {
        var path = FileWith("""{"volume":"głośno"}""");
        var directory = Path.GetDirectoryName(path)!;

        SettingsStore.Preload(path);
        Thread.Sleep(200);
        Assert.Single(Directory.GetFiles(directory));

        var store = new SettingsStore(path);

        Assert.Equal(new AppSettings().Volume, store.Current.Volume);
        Assert.Contains(Directory.GetFiles(directory), file => file.Contains(".bad-", StringComparison.Ordinal));
    });
}
