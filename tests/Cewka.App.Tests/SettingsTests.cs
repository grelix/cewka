using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cewka.App.Models;
using Cewka.Audio;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenia ustawień: przełożenia wyborów na wartości warstwy dźwięku oraz trwałości
/// ustawień między uruchomieniami.
/// </summary>
public sealed class SettingsTests
{
    private static readonly JsonSerializerOptions FileFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---------- Przełożenie wyborów na liczby ----------

    [Theory]
    [InlineData(ResamplerQuality.Off, 0)]
    [InlineData(ResamplerQuality.Standard, AudioQuality.DefaultFilterOrder)]
    [InlineData(ResamplerQuality.High, AudioQuality.MaximumFilterOrder)]
    public void JakoscOdpowiadaRzedowiFiltru(ResamplerQuality quality, int expected) =>
        Assert.Equal(expected, AudioPreferences.FilterOrder(quality));

    /// <summary>
    /// Wartość domyślna musi zostawiać brzmienie takim, jakie było przed wprowadzeniem
    /// ustawienia — inaczej sama aktualizacja programu zmieniłaby dźwięk.
    /// </summary>
    [Fact]
    public void DomyslnaJakoscToWartoscMiniaudio()
    {
        var settings = new AppSettings();

        Assert.Equal(ResamplerQuality.Standard, settings.ResamplerQuality);
        Assert.Equal(AudioQuality.DefaultFilterOrder, AudioPreferences.FilterOrder(settings.ResamplerQuality));
    }

    [Fact]
    public void RzadFiltruNieWychodziPozaZakresMiniaudio()
    {
        var restore = AudioQuality.ResamplerFilterOrder;

        try
        {
            AudioQuality.ResamplerFilterOrder = 99;
            Assert.Equal(AudioQuality.MaximumFilterOrder, AudioQuality.ResamplerFilterOrder);

            AudioQuality.ResamplerFilterOrder = -5;
            Assert.Equal(0, AudioQuality.ResamplerFilterOrder);
        }
        finally
        {
            AudioQuality.ResamplerFilterOrder = restore;
        }
    }

    /// <summary>
    /// Wyważony rozmiar bufora oznacza „bez żądania": zero zostawia wybór sterownikowi, czyli
    /// zachowanie sprzed wprowadzenia ustawienia.
    /// </summary>
    [Fact]
    public void WywazonyBuforNieNarzucaRozmiaru() =>
        Assert.Equal(0, AudioPreferences.PeriodFrames(OutputLatency.Balanced));

    [Fact]
    public void MalyBuforJestMniejszyOdBezpiecznego() =>
        Assert.True(AudioPreferences.PeriodFrames(OutputLatency.Low) <
                    AudioPreferences.PeriodFrames(OutputLatency.Safe));

    [Theory]
    [InlineData(LoudnessTarget.Broadcast, -23.0)]
    [InlineData(LoudnessTarget.Reference, -18.0)]
    [InlineData(LoudnessTarget.Streaming, -14.0)]
    public void PoziomDocelowyMaWartoscWLufs(LoudnessTarget target, double expected) =>
        Assert.Equal(expected, AudioPreferences.Lufs(target));

    [Fact]
    public void DomyslnyPoziomToPoziomOdniesieniaReplayGain() =>
        Assert.Equal(LoudnessService.ReferenceLufs, AudioPreferences.Lufs(new AppSettings().LoudnessTarget));

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(7, 5)]
    [InlineData(9, 10)]
    [InlineData(30, 30)]
    [InlineData(600, 30)]
    [InlineData(-3, 5)]
    public void KrokPrzewijaniaWracaDoWartosciZListy(int stored, int expected) =>
        Assert.Equal(expected, AudioPreferences.NearestSeekStep(stored));

    // ---------- Trwałość ustawień ----------

    /// <summary>
    /// Kopia ustawień musi obejmować każdą własność. Dopisanie nowego ustawienia i zapomnienie
    /// o metodzie <c>Clone</c> daje błąd, który objawia się wyłącznie w tym jednym miejscu,
    /// gdzie kopia jest używana — i wygląda wtedy jak przypadkowa utrata ustawienia.
    /// </summary>
    [Fact]
    public void KopiaObejmujeKazdaWlasnosc()
    {
        var source = new AppSettings();
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties.Where(p => p.CanWrite))
            property.SetValue(source, Vary(property, property.GetValue(source)));

        var clone = source.Clone();

        foreach (var property in properties)
        {
            var expected = JsonSerializer.Serialize(property.GetValue(source), property.PropertyType);
            var actual = JsonSerializer.Serialize(property.GetValue(clone), property.PropertyType);

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Kopia nie może dzielić z oryginałem tablic ani obiektów, które da się zmienić.</summary>
    [Fact]
    public void KopiaNieDzieliObiektowZmiennych()
    {
        var source = new AppSettings { Window = new WindowGeometry { Width = 900, Height = 600 } };
        var clone = source.Clone();

        clone.EqualiserGains[0] = -11;
        clone.Window!.Width = 1;

        Assert.NotEqual(-11, source.EqualiserGains[0]);
        Assert.Equal(900, source.Window!.Width);
    }

    /// <summary>
    /// Plik ustawień zapisany przez wcześniejszą wersję nie zawiera nowych pól. Musi się wczytać
    /// bez błędu, a brakujące ustawienia przyjąć wartości domyślne — pojawienie się nowej opcji
    /// nie może kasować tego, co użytkownik już ustawił.
    /// </summary>
    [Fact]
    public void StarszyPlikUstawienWczytujeSieZWartosciamiDomyslnymi()
    {
        const string older = """
        {
          "theme": "Dark",
          "language": "en",
          "windowControls": "Left",
          "panelOpen": false,
          "volume": 0.4,
          "equaliserEnabled": true,
          "preamp": 2.0,
          "equaliserGains": [1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
          "limiterEnabled": true,
          "normalisationEnabled": false,
          "effects": "Full",
          "outputDevice": "Głośniki",
          "mediaKeys": false,
          "singleInstance": false
        }
        """;

        var loaded = JsonSerializer.Deserialize<AppSettings>(older, FileFormat);
        Assert.NotNull(loaded);

        // Ustawienia z pliku zachowane.
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.Equal("en", loaded.Language);
        Assert.False(loaded.MediaKeys);
        Assert.False(loaded.NormalisationEnabled);

        // Nowe ustawienia z wartościami domyślnymi.
        var defaults = new AppSettings();
        Assert.Equal(defaults.ResamplerQuality, loaded.ResamplerQuality);
        Assert.Equal(defaults.OutputLatency, loaded.OutputLatency);
        Assert.Equal(defaults.LoudnessTarget, loaded.LoudnessTarget);
        Assert.Equal(defaults.AlwaysAnalyse, loaded.AlwaysAnalyse);
        Assert.Equal(defaults.RestoreSession, loaded.RestoreSession);
        Assert.Equal(defaults.SeekStep, loaded.SeekStep);

        // Efekty dźwiękowe: plik sprzed ich wprowadzenia nie może ich włączyć. Program ma grać
        // tak samo jak przedtem, dopóki nikt sam po nie nie sięgnie.
        Assert.False(loaded.CrossfeedEnabled);
        Assert.False(loaded.LoudnessEnabled);
        Assert.False(loaded.VirtualBassEnabled);
        Assert.False(loaded.DynamicRangeEnabled);
        Assert.False(loaded.StereoWidthEnabled);

        Assert.Equal(defaults.CrossfeedStrength, loaded.CrossfeedStrength);
        Assert.Equal(defaults.LoudnessStrength, loaded.LoudnessStrength);

        // Kolejka ma od 0.8.0 własny stan. Plik sprzed rozdzielenia niesie tylko wartość dla
        // pasa dolnego, a kolejka musi wyjść widoczna — inaczej aktualizacja programu schowałaby
        // komuś kolejkę bez pytania.
        Assert.False(loaded.PanelOpen);
        Assert.True(loaded.QueueOpen);
    }

    /// <summary>
    /// Siła każdego efektu przechodzi zapis i odczyt bez zmiany. Wartości leżą w zakresie 0–1,
    /// więc pomyłka o dwa rzędy wielkości nie rzucałaby się w oczy inaczej niż w brzmieniu.
    /// </summary>
    [Fact]
    public void UstawieniaEfektowPrzechodzaZapisIOdczyt()
    {
        var source = new AppSettings
        {
            CrossfeedEnabled = true,
            CrossfeedStrength = 0.75,
            LoudnessEnabled = true,
            LoudnessStrength = 0.4,
            VirtualBassEnabled = true,
            VirtualBassStrength = 0.9,
            DynamicRangeEnabled = true,
            DynamicRangeStrength = 0.25,
            StereoWidthEnabled = true,
            StereoWidthStrength = 0.6,
        };

        var loaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(source, FileFormat), FileFormat);

        Assert.NotNull(loaded);
        Assert.True(loaded.CrossfeedEnabled);
        Assert.Equal(0.75, loaded.CrossfeedStrength);
        Assert.Equal(0.4, loaded.LoudnessStrength);
        Assert.Equal(0.9, loaded.VirtualBassStrength);
        Assert.Equal(0.25, loaded.DynamicRangeStrength);
        Assert.Equal(0.6, loaded.StereoWidthStrength);
    }

    [Fact]
    public void NoweUstawieniaPrzechodzaZapisIOdczyt()
    {
        var source = new AppSettings
        {
            ResamplerQuality = ResamplerQuality.High,
            OutputLatency = OutputLatency.Low,
            LoudnessTarget = LoudnessTarget.Streaming,
            AlwaysAnalyse = true,
            RestoreSession = false,
            SeekStep = 30,
        };

        var loaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(source, FileFormat), FileFormat);

        Assert.NotNull(loaded);
        Assert.Equal(ResamplerQuality.High, loaded.ResamplerQuality);
        Assert.Equal(OutputLatency.Low, loaded.OutputLatency);
        Assert.Equal(LoudnessTarget.Streaming, loaded.LoudnessTarget);
        Assert.True(loaded.AlwaysAnalyse);
        Assert.False(loaded.RestoreSession);
        Assert.Equal(30, loaded.SeekStep);
    }

    /// <summary>Wartość różna od podanej, dobrana według typu własności.</summary>
    private static object? Vary(PropertyInfo property, object? current) => current switch
    {
        bool value => !value,
        int value => value + 7,
        double value => value + 0.25,
        string value => value + "-inne",
        double[] value => value.Select(gain => gain - 1).ToArray(),
        Enum value => NextEnumValue(value),
        null when property.PropertyType == typeof(string) => "cokolwiek",
        null when property.PropertyType == typeof(WindowGeometry) =>
            new WindowGeometry { X = 3, Y = 4, Width = 1280, Height = 800, Maximized = true },
        _ => current,
    };

    private static object NextEnumValue(Enum value)
    {
        var values = Enum.GetValues(value.GetType()).Cast<Enum>().ToArray();
        var index = Array.IndexOf(values, value);

        return values[(index + 1) % values.Length];
    }
}
