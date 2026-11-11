using Cewka.App.Models;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Odczyt pliku ustawień z dysku: przeniesienie ze starszej wersji, odporność na ręczną edycję
/// i ochrona pliku, którego nie dało się przeczytać.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsStoreTests
{
    [Fact]
    public void NowaInstalacjaMaPlaskiKorektor()
    {
        var settings = new AppSettings();

        Assert.Equal(0, settings.Preamp);
        Assert.All(settings.EqualiserGains, gain => Assert.Equal(0, gain));
        Assert.Equal(10, settings.EqualiserGains.Length);
    }

    [Fact]
    public void BrakPlikuDajeUstawieniaBiezacejWersji() => Ui.Run(() =>
    {
        var store = new SettingsStore(Path.Combine(TestEnvironment.NewDirectory(), "settings.json"));

        Assert.Equal(AppSettings.CurrentSchema, store.Current.SchemaVersion);
        Assert.Equal(new AppSettings().Volume, store.Current.Volume);
    });

    [Fact]
    public void GlosnoscZeStarszejWersjiPrzechodziNaSkalePercepcyjna() => Ui.Run(() =>
    {
        var store = Store("""{ "volume": 0.72 }""");

        Assert.Equal(1 + 20 * Math.Log10(0.72) / 60, store.Current.Volume, precision: 6);
        Assert.False(store.Current.IsMuted);
        Assert.Equal(AppSettings.CurrentSchema, store.Current.SchemaVersion);
    });

    [Fact]
    public void ZerowaGlosnoscZeStarszejWersjiStajeSieWyciszeniem() => Ui.Run(() =>
    {
        var store = Store("""{ "volume": 0 }""");

        Assert.True(store.Current.IsMuted);
        Assert.Equal(0.5, store.Current.Volume);
    });

    [Fact]
    public void GlosnoscBiezacejWersjiNiePrzechodziPrzeliczenia() => Ui.Run(() =>
    {
        var store = Store($$"""{ "schemaVersion": {{AppSettings.CurrentSchema}}, "volume": 0.3, "isMuted": false }""");

        Assert.Equal(0.3, store.Current.Volume);
        Assert.False(store.Current.IsMuted);
    });

    [Fact]
    public void KomentarzIPrzecinekNaKoncuNieKasujaUstawien() => Ui.Run(() =>
    {
        var store = Store("""
        {
          // ręczna poprawka
          "theme": "Dark",
          "language": "en",
        }
        """);

        Assert.Equal(ThemeMode.Dark, store.Current.Theme);
        Assert.Equal("en", store.Current.Language);
    });

    [Fact]
    public void BrakTablicyKorektoraNieKasujaUstawien() => Ui.Run(() =>
    {
        var store = Store("""{ "language": "en", "equaliserGains": null }""");

        Assert.Equal("en", store.Current.Language);
        Assert.Equal(10, store.Current.EqualiserGains.Length);
    });

    /// <summary>Wskaźniki wycofano: zapisany wybór daje Płytę, a plik nie jest odrzucany razem z resztą ustawień.</summary>
    [Fact]
    public void WycofaneWskaznikiDajaPlyteBezUtratyUstawien() => Ui.Run(() =>
    {
        var store = Store("""{ "language": "en", "sceneVisual": "Meters" }""");

        Assert.Equal("en", store.Current.Language);
        Assert.Equal(SceneVisual.Disc, store.Current.SceneVisual);
    });

    /// <summary>Płyty nie da się pominąć w cyklu V; wycofane i powtórzone wartości znikają z listy.</summary>
    [Fact]
    public void ListaPominietychWizualizacjiBezPlytyIPowtorzen() => Ui.Run(() =>
    {
        var store = Store("""{ "sceneCycleOff": ["Disc", "Meters", "Night", "Night", "Cover"] }""");

        Assert.Equal([SceneVisual.Night, SceneVisual.Cover], store.Current.SceneCycleOff);
    });

    [Fact]
    public void NieczytelnyPlikZostajeZachowanyObokDomyslnych() => Ui.Run(() =>
    {
        const string original = """{ "language": "en", "placeholderPalette": "Nieznana" }""";
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, original);

        var store = new SettingsStore(path);
        store.SaveNow();

        var copies = Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.bad-*");
        Assert.Single(copies);
        Assert.Equal(original, File.ReadAllText(copies[0]));
        Assert.Equal("auto", store.Current.Language);
    });

    [Fact]
    public void ChwilowoZablokowanyPlikNieZostajeNadpisanyDomyslnymi() => Ui.Run(() =>
    {
        const string original = """{ "schemaVersion": 1, "language": "en", "volume": 0.3 }""";
        var directory = TestEnvironment.NewDirectory();
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, original);

        SettingsStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            store = new SettingsStore(path);

        store.Current.Volume = 0.9;
        store.SaveNow();

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(["settings.json"], Directory.GetFiles(directory).Select(Path.GetFileName));
    });

    [Fact]
    public void ZablokowanyPlikUszkodzonyPoZwolnieniuTrafiaDoKopii() => Ui.Run(() =>
    {
        var directory = TestEnvironment.NewDirectory();
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{ uszkodzony");

        SettingsStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            store = new SettingsStore(path);

        store.SaveNow();

        Assert.Single(Directory.GetFiles(directory, "settings.json.bad-*"));
        Assert.Contains("schemaVersion", File.ReadAllText(path));
    });

    [Fact]
    public void ZapisNieZostawiaPlikowTymczasowych() => Ui.Run(() =>
    {
        var directory = TestEnvironment.NewDirectory();
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));

        store.SaveNow();
        store.SaveNow();

        Assert.Equal(["settings.json"], Directory.GetFiles(directory).Select(Path.GetFileName));
    });

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.0316227766)]
    public void KrzywaGlosnosciObejmuje60Decybeli(double position, double gain) =>
        Assert.Equal(gain, VolumeCurve.ToGain(position), precision: 6);

    [Fact]
    public void KrzywaGlosnosciRosnieIOdwracaSie()
    {
        var previous = -1.0;

        for (var i = 1; i <= 100; i++)
        {
            var position = i / 100.0;
            var gain = VolumeCurve.ToGain(position);

            Assert.True(gain > previous);
            Assert.Equal(position, VolumeCurve.FromGain(gain), precision: 9);
            previous = gain;
        }
    }

    private static SettingsStore Store(string json)
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path);
    }
}
