using System.Text.Json;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenia plików językowych.
///
/// <para>Brakujący klucz nie wywraca programu — wraca wtedy tekst polski — i właśnie dlatego
/// wymaga testu. Luka w tłumaczeniu wygląda jak zdanie w obcym języku pośród reszty i można ją
/// przeoczyć w każdym języku poza własnym.</para>
/// </summary>
public sealed class LanguageFileTests
{
    private const string Reference = "pl";
    private static readonly string[] Codes = ["pl", "en", "es", "de", "fr"];

    /// <summary>Etykiety, które trafiają na wąskie przyciski pasków segmentowych.</summary>
    private static readonly string[] ShortLabels =
    [
        "ThemeSystem", "ThemeLight", "ThemeDark", "LanguageAuto",
        "ControlsRight", "ControlsLeft", "ControlsMacOs",
        "EffectsAuto", "EffectsFull", "EffectsReduced",
        "QualityOff", "QualityStandard", "QualityHigh",
        "LatencyLow", "LatencyBalanced", "LatencySafe",
    ];

    /// <summary>
    /// Przekroczenie tej długości nie mieści się na przycisku segmentu przy najwęższym oknie.
    /// Wartość dobrana z pomiaru: 18 znaków w foncie Cantarell przy 11,5 punktu.
    /// </summary>
    private const int ShortLabelLimit = 18;

    private static string Directory => Path.Combine(AppContext.BaseDirectory, "Languages");

    private static Dictionary<string, string> Load(string code)
    {
        var path = Path.Combine(Directory, $"{code}.json");
        Assert.True(File.Exists(path), $"brak pliku języka: {path}");

        var text = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(text);

        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void KazdyJezykMaKompletZnakow()
    {
        foreach (var code in Codes)
        {
            var strings = Load(code);
            Assert.NotEmpty(strings);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("fr")]
    public void ZestawKluczyZgodnyZPolskim(string code)
    {
        var reference = Load(Reference);
        var translated = Load(code);

        var missing = reference.Keys.Except(translated.Keys).Order().ToArray();
        var extra = translated.Keys.Except(reference.Keys).Order().ToArray();

        Assert.True(
            missing.Length == 0,
            $"{code}.json nie ma kluczy: {string.Join(", ", missing)}");

        Assert.True(
            extra.Length == 0,
            $"{code}.json ma klucze spoza wzorca: {string.Join(", ", extra)}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("fr")]
    public void ZadenTekstNiePozostalPoPolsku(string code)
    {
        var reference = Load(Reference);
        var translated = Load(code);

        // Klucze, których wartość z założenia jest wspólna dla wszystkich języków.
        string[] shared = ["_language", "_code", "AppName", "Preamp", "NoFormat", "ControlsMacOs"];

        var copied = reference
            .Where(pair => !shared.Contains(pair.Key))
            .Where(pair => pair.Value.Length > 12)
            .Where(pair => translated.TryGetValue(pair.Key, out var value) && value == pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        Assert.True(
            copied.Length == 0,
            $"{code}.json powtarza tekst polski w kluczach: {string.Join(", ", copied)}");
    }

    [Theory]
    [InlineData("pl")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("fr")]
    public void EtykietySegmentowMieszczaSieNaPrzycisku(string code)
    {
        var strings = Load(code);

        var toolong = ShortLabels
            .Where(key => strings.TryGetValue(key, out var value) && value.Length > ShortLabelLimit)
            .Select(key => $"{key}=\"{strings[key]}\"")
            .ToArray();

        Assert.True(
            toolong.Length == 0,
            $"{code}.json ma etykiety dłuższe niż {ShortLabelLimit} znaków: {string.Join(", ", toolong)}");
    }

    /// <summary>
    /// Symbole zastępcze przechodzą przez <c>string.Format</c>. Tłumacz, który zamieni je na
    /// tekst albo przestawi specyfikator formatu, wywołałby wyjątek dopiero w czasie działania
    /// programu — i to wyłącznie w swoim języku.
    /// </summary>
    [Theory]
    [InlineData("pl")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("fr")]
    public void SymboleZastepczePrzetrwalyTlumaczenie(string code)
    {
        var strings = Load(code);

        Assert.True(strings.TryGetValue("LatencyMeasured", out var measured));
        Assert.Contains("{0}", measured);
        Assert.Contains("{1:F1}", measured);

        // Sprawdzenie właściwe: tekst musi dać się sformatować bez wyjątku.
        var formatted = string.Format(measured, 1024, 21.3);
        Assert.Contains("1024", formatted);
    }

    /// <summary>
    /// Jedno kodowanie dla wszystkich plików językowych.
    ///
    /// <para>Program czyta je przez <c>StreamReader</c>, który znacznik BOM usuwa sam, więc nie
    /// chodzi o poprawność działania. Chodzi o to, żeby pięć plików tego samego rodzaju miało
    /// jedno kodowanie: RFC 8259 zabrania dopisywania znacznika do dokumentu JSON, a plik, który
    /// go ma, wygląda w narzędziach porównujących na zmieniony w pierwszej linii bez powodu.</para>
    /// </summary>
    [Theory]
    [InlineData("pl")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("fr")]
    public void PlikiNieMajaZnacznikaKolejnosciBajtow(string code)
    {
        var path = Path.Combine(Directory, $"{code}.json");
        var head = new byte[3];

        using (var stream = File.OpenRead(path)) _ = stream.Read(head, 0, 3);

        Assert.False(
            head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
            $"{code}.json zaczyna się znacznikiem BOM");
    }

    [Theory]
    [InlineData("pl", "Polski")]
    [InlineData("en", "English")]
    [InlineData("es", "Español")]
    [InlineData("de", "Deutsch")]
    [InlineData("fr", "Français")]
    public void NazwaJezykaZapisanaWJezykuWlasnym(string code, string expected)
    {
        var strings = Load(code);

        Assert.Equal(expected, strings["_language"]);
        Assert.Equal(code, strings["_code"]);
    }

    /// <summary>
    /// Znaki obecne w osadzonych fontach: pismo łacińskie wraz z rozszerzeniami oraz garść
    /// znaków interpunkcyjnych spoza tego zakresu.
    ///
    /// <para>Znak, którego font nie ma, wyświetla się jako pusty prostokąt i wykryć to można
    /// tylko patrząc na ekran w tym właśnie języku. Najczęstszy przypadek to francuska spacja
    /// nierozdzielająca o zerowej szerokości (U+202F), której Cantarell nie zawiera — należy
    /// użyć zwykłej spacji nierozdzielającej (U+00A0).</para>
    /// </summary>
    private static readonly char[] AllowedBeyondLatin =
    [
        '–', '—',                     // półpauza, pauza
        '‘', '’', '‚',           // apostrofy i cudzysłowy pojedyncze
        '“', '”', '„',           // cudzysłowy podwójne
        '…',                               // wielokropek
    ];

    [Theory]
    [InlineData("pl")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("fr")]
    public void TekstyUzywajaWylacznieZnakowObecnychWFontach(string code)
    {
        var strings = Load(code);

        var offending = strings
            .SelectMany(pair => pair.Value.Select(character => (pair.Key, character)))
            .Where(item => item.character >= 0x0250 && !AllowedBeyondLatin.Contains(item.character))
            .Select(item => $"{item.Key}: U+{(int)item.character:X4}")
            .Distinct()
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"{code}.json ma znaki spoza zakresu osadzonych fontów: {string.Join(", ", offending)}");
    }
}
