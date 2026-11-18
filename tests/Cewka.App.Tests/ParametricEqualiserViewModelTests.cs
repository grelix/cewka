using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Tryb korektora, pasma parametryczne, import i eksport w modelu odtwarzacza.</summary>
[Collection(UiCollection.Name)]
public sealed class ParametricEqualiserViewModelTests
{
    private const string AutoEq = """
        Preamp: -6.1 dB
        Filter 1: ON LSC Fc 105 Hz Gain 6.4 dB Q 0.70
        Filter 2: ON PK Fc 8800 Hz Gain 5.1 dB Q 1.42
        Filter 3: ON HSC Fc 10000 Hz Gain -2.1 dB Q 0.70
        Filter 4: ON PK Fc 5332 Hz Gain -1.1 dB Q 5.75
        """;

    [Fact]
    public void DomyslnieGraKorektorGraficzny() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();

        Assert.Equal(EqualiserMode.Graphic, h.Player.EqualiserMode);
        Assert.Empty(h.Player.Parametric.Bands);
        Assert.True(h.Engine.Graph.Equaliser.Enabled);
        Assert.False(h.Engine.Graph.Parametric.Enabled);
        Assert.Same(h.Player.Preamp, h.Player.ActivePreamp);
        Assert.True(h.Player.EqualiserModeChoice.Options[0].IsSelected);
    });

    /// <summary>Tryb przełącza tor, suwak przedwzmacniacza, podpowiedź zerowania i zapis.</summary>
    [Fact]
    public void ZmianaTrybuPrzelaczaTorIPrzedwzmacniacz() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();

        h.Player.EqualiserModeChoice.Choose(h.Player.EqualiserModeChoice.Options[1]);

        Assert.Equal(EqualiserMode.Parametric, h.Player.EqualiserMode);
        Assert.Equal(EqualiserMode.Parametric, h.Settings.Current.EqualiserMode);
        Assert.False(h.Engine.Graph.Equaliser.Enabled);
        Assert.True(h.Engine.Graph.Parametric.Enabled);
        Assert.Same(h.Player.Parametric.Preamp, h.Player.ActivePreamp);
        Assert.Equal(Strings.Current["TooltipEqualiserResetParametric"], h.Player.EqualiserResetTooltip);

        h.Player.EqualiserEnabled = false;
        Assert.False(h.Engine.Graph.Equaliser.Enabled);
        Assert.False(h.Engine.Graph.Parametric.Enabled);

        h.Player.EqualiserEnabled = true;
        h.Player.EqualiserMode = EqualiserMode.Graphic;
        Assert.True(h.Engine.Graph.Equaliser.Enabled);
        Assert.False(h.Engine.Graph.Parametric.Enabled);
        Assert.Equal(Strings.Current["TooltipEqualiserReset"], h.Player.EqualiserResetTooltip);
    });

    [Fact]
    public void DodaniePasmaDajeDzwon1kHzIDochodziDoToru() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var parametric = h.Player.Parametric;

        parametric.AddBand();

        var band = Assert.Single(parametric.Bands);
        Assert.Equal(ParametricFilterType.Peak, band.Type);
        Assert.Equal(1000, band.Frequency);
        Assert.Equal(0, band.Gain);
        Assert.Equal(1.41, band.Q);
        Assert.True(band.Enabled);
        Assert.Equal(1, band.Number);
        Assert.Equal(band.ToBand(), Assert.Single(h.Engine.Graph.Parametric.Bands));
        Assert.Single(h.Settings.Current.ParametricBands);

        band.Gain = 4.5;
        band.Type = ParametricFilterType.HighShelf;
        band.Frequency = 30000;
        band.Q = 0;
        band.Enabled = false;

        Assert.Equal(new ParametricBand(band.Id, ParametricFilterType.HighShelf, 20000, 4.5, 0.1, false),
            Assert.Single(h.Engine.Graph.Parametric.Bands));
        var stored = Assert.Single(h.Settings.Current.ParametricBands);
        Assert.Equal((ParametricFilterType.HighShelf, 20000.0, 4.5, 0.1, false),
            (stored.Type, stored.Frequency, stored.Gain, stored.Q, stored.Enabled));
    });

    [Fact]
    public void NajwyzejDwadziesciaPasm() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var parametric = h.Player.Parametric;

        for (var i = 0; i < 25; i++) parametric.AddBand();

        Assert.Equal(ParametricEqualiser.MaxBands, parametric.Bands.Count);
        Assert.False(parametric.CanAddBand);

        parametric.RemoveBand(parametric.Bands[3]);
        Assert.True(parametric.CanAddBand);
        Assert.Equal(Enumerable.Range(1, 19), parametric.Bands.Select(band => band.Number));
        Assert.Equal(19, h.Engine.Graph.Parametric.Bands.Count);
    });

    /// <summary>Nazwy pól dla czytnika ekranu mają numer pasma, który idzie za miejscem na liście.</summary>
    [Fact]
    public void NazwyPolMajaNumerPasma() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var parametric = h.Player.Parametric;
        parametric.AddBand();
        parametric.AddBand();

        var second = parametric.Bands[1];
        Assert.Equal(string.Format(Strings.Current["EqualiserBandGainName"], 2), second.GainName);

        parametric.RemoveBand(parametric.Bands[0]);
        Assert.Equal(string.Format(Strings.Current["EqualiserBandGainName"], 1), second.GainName);
        Assert.Equal(string.Format(Strings.Current["EqualiserBandFilterName"], 1), second.FilterName);
    });

    /// <summary>Zerowanie w trybie parametrycznym usuwa pasma i zeruje jego przedwzmacniacz, korektora graficznego nie rusza.</summary>
    [Fact]
    public void ZerowanieDzialaNaGrajacyTryb() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.Bands[2].Gain = 5;
        h.Player.EqualiserMode = EqualiserMode.Parametric;
        Assert.False(h.Player.CanResetEqualiser);

        h.Player.Parametric.AddBand();
        h.Player.Parametric.Preamp.Gain = -4;
        Assert.True(h.Player.CanResetEqualiser);

        h.Player.ResetEqualiser();

        Assert.Empty(h.Player.Parametric.Bands);
        Assert.Equal(0, h.Player.Parametric.Preamp.Gain);
        Assert.Equal(0, h.Engine.Graph.Parametric.Preamp);
        Assert.Equal(5, h.Player.Bands[2].Gain);
        Assert.False(h.Player.CanResetEqualiser);

        h.Player.EqualiserMode = EqualiserMode.Graphic;
        Assert.True(h.Player.CanResetEqualiser);
        h.Player.ResetEqualiser();
        Assert.Equal(0, h.Player.Bands[2].Gain);
    });

    /// <summary>Kropka przetwarzania świeci, gdy grający korektor jest niepłaski.</summary>
    [Fact]
    public void KropkaPrzetwarzaniaIdzieZaGrajacymTrybem() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.Parametric.AddBand();
        h.Player.Parametric.Bands[0].Gain = 3;
        Assert.False(h.Player.ProcessingActive);

        h.Player.EqualiserMode = EqualiserMode.Parametric;
        Assert.True(h.Player.ProcessingActive);
    });

    [Fact]
    public void ImportZastepujePasmaIPrzelaczaNaParametryczny() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var parametric = h.Player.Parametric;
        parametric.AddBand();

        h.Player.ImportEqualiser(WriteFile(h, AutoEq));

        Assert.Equal(EqualiserMode.Parametric, h.Player.EqualiserMode);
        Assert.Equal(4, parametric.Bands.Count);
        Assert.Equal(-6.1, parametric.Preamp.Gain);
        Assert.Equal(-6.1, h.Engine.Graph.Parametric.Preamp);
        Assert.Equal(4, h.Engine.Graph.Parametric.Bands.Count);
        Assert.Equal(4, h.Settings.Current.ParametricBands.Count);
        Assert.Equal(ParametricFilterType.LowShelf, parametric.Bands[0].Type);
        Assert.Equal(string.Format(Strings.Current["EqualiserImported"], 4), parametric.Message);
        Assert.True(parametric.HasMessage);

        // Komunikat znika przy następnej zmianie.
        parametric.Bands[1].Gain = 1;
        Assert.False(parametric.HasMessage);
    });

    [Fact]
    public void ImportCzesciowyIPrzycietyPrzedwzmacniaczMajaKomunikat() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var parametric = h.Player.Parametric;

        h.Player.ImportEqualiser(WriteFile(h, "Preamp: -20 dB\nFilter 1: ON PK Fc 100 Hz Gain 2 dB Q 1\nFilter 2: ON LP Fc 100 Hz"));

        Assert.Equal(-12, parametric.Preamp.Gain);
        Assert.Equal(
            string.Format(Strings.Current["EqualiserImportedPartly"], 1, 1) + " " + Strings.Current["EqualiserImportPreampLimited"],
            parametric.Message);

        h.Player.ImportEqualiser(WriteFile(h, "Preamp: 15 dB\nFilter 1: ON PK Fc 100 Hz Gain 2 dB Q 1"));
        Assert.Equal(12, parametric.Preamp.Gain);
        Assert.EndsWith(Strings.Current["EqualiserImportPreampLimitedHigh"], parametric.Message);
    });

    /// <summary>Plik bez pasm i plik nie do odczytania niczego nie zmieniają, tylko zostawiają komunikat.</summary>
    [Fact]
    public void NieudanyImportNiczegoNieZmienia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var parametric = h.Player.Parametric;
        parametric.AddBand();
        var before = parametric.Curve;

        h.Player.ImportEqualiser(WriteFile(h, "Preamp: -3 dB\n# nic więcej"));
        Assert.Equal(Strings.Current["EqualiserImportEmpty"], parametric.Message);
        Assert.Equal(EqualiserMode.Graphic, h.Player.EqualiserMode);
        Assert.Same(before, parametric.Curve);
        Assert.Equal(0, parametric.Preamp.Gain);

        h.Player.ImportEqualiser(Path.Combine(h.Directory, "brak.txt"));
        Assert.Equal(Strings.Current["EqualiserImportFailed"], parametric.Message);

        var large = Path.Combine(h.Directory, "duzy.txt");
        File.WriteAllBytes(large, new byte[EqualizerApoFile.MaxFileBytes + 10]);
        h.Player.ImportEqualiser(large);
        Assert.Equal(Strings.Current["EqualiserImportFailed"], parametric.Message);
        Assert.Same(before, parametric.Curve);
    });

    [Fact]
    public void EksportZapisujePlikIKomunikat() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var parametric = h.Player.Parametric;
        parametric.AddBand();
        parametric.Bands[0].Gain = -2.5;
        parametric.Preamp.Gain = -3;

        var path = Path.Combine(h.Directory, "korektor.txt");
        h.Player.ExportEqualiser(path);

        Assert.Equal("Preamp: -3 dB\nFilter 1: ON PK Fc 1000 Hz Gain -2.5 dB Q 1.41\n", File.ReadAllText(path));
        Assert.Equal(string.Format(Strings.Current["EqualiserExported"], "korektor.txt"), parametric.Message);

        h.Player.ExportEqualiser(h.Directory);
        Assert.Equal(Strings.Current["EqualiserExportFailed"], parametric.Message);
    });

    /// <summary>Tryb, pasma i przedwzmacniacz trybu wracają po ponownym uruchomieniu.</summary>
    [Fact]
    public void PasmaWracajaPoPonownymUruchomieniu() => Ui.Run(() =>
    {
        string directory;
        using (var h = new PlayerHarness())
        {
            directory = h.Directory;
            h.Player.ImportEqualiser(WriteFile(h, AutoEq));
            h.Player.Parametric.Bands[2].Enabled = false;
            h.Settings.SaveNow();
        }

        using var again = new PlayerHarness(directory: directory);
        Assert.Equal(EqualiserMode.Parametric, again.Player.EqualiserMode);
        Assert.Equal(4, again.Player.Parametric.Bands.Count);
        Assert.False(again.Player.Parametric.Bands[2].Enabled);
        Assert.Equal(-6.1, again.Player.Parametric.Preamp.Gain);
        Assert.True(again.Engine.Graph.Parametric.Enabled);
        Assert.Equal(4, again.Engine.Graph.Parametric.Bands.Count);
    });

    /// <summary>Ręcznie poprawiony plik: za dużo pasm, wartości spoza zakresów, puste wpisy, nieznany tryb.</summary>
    [Fact]
    public void UstawieniaKorektoraSaPoprawianePrzyOdczycie() => Ui.Run(() =>
    {
        var bands = string.Join(",", Enumerable.Range(0, 25).Select(_ => """{ "type": "LowShelf", "frequency": 5, "gain": 99, "q": 0 }"""));
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, $$"""
            { "equaliserMode": 7, "parametricPreamp": -40, "parametricBands": [ null, {{bands}} ] }
            """);

        var store = new SettingsStore(path);

        Assert.Equal(ParametricEqualiser.MaxBands, store.Current.ParametricBands.Count);
        Assert.All(store.Current.ParametricBands, band =>
        {
            Assert.Equal(20, band.Frequency);
            Assert.Equal(24, band.Gain);
            Assert.Equal(0.1, band.Q);
        });
        Assert.Equal(-12, store.Current.ParametricPreamp);
        Assert.Equal(EqualiserMode.Graphic, store.Current.EqualiserMode);
    });

    private static string WriteFile(PlayerHarness h, string text)
    {
        var path = Path.Combine(h.Directory, $"zestaw-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text);
        return path;
    }
}
