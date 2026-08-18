using System.Text.Json;
using Cewka.App.Models;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenia plików językowych.
///
/// <para>Brakujący klucz nie wywraca programu — wraca wtedy tekst polski — i właśnie dlatego
/// wymaga testu. Luka w tłumaczeniu wygląda jak zdanie w obcym języku pośród reszty i można ją
/// przeoczyć w każdym języku poza własnym.</para>
///
/// <para><b>Jedna lista języków.</b> Wcześniej każdy sprawdzian miał własny zestaw atrybutów
/// <c>InlineData</c> i dołożenie języka znaczyło dopisanie go w sześciu miejscach. Teraz lista
/// jest jedna, a wszystkie sprawdziany biorą ją stąd — zapomnieć o którymś nie ma jak.
/// Zgodność listy z tym, co program faktycznie udostępnia, pilnuje narzędzie zrzutów: renderuje
/// okno ustawień w każdym języku z tej listy, korzystając z prawdziwej klasy Strings.</para>
/// </summary>
public sealed class LanguageFileTests
{
    private const string Reference = "pl";

    private static readonly string[] Codes =
    [
        "pl", "en",
        "cs", "de", "el", "es", "fr", "hu", "id", "it", "nl", "pt", "ro", "ru", "tr", "uk", "vi",
    ];

    /// <summary>Języki poza wzorcowym — te, które podlegają porównaniu z polskim.</summary>
    public static TheoryData<string> Translated =>
        new(Codes.Where(code => code != Reference));

    public static TheoryData<string> All => new(Codes);

    /// <summary>Etykiety, które trafiają na wąskie przyciski pasków segmentowych.</summary>
    private static readonly string[] ShortLabels =
    [
        "ThemeSystem", "ThemeLight", "ThemeDark", "LanguageAuto",
        "ControlsRight", "ControlsLeft", "ControlsMacOs",
        "EffectsAuto", "EffectsFull", "EffectsReduced",
        "ColoursSubtle", "ColoursRecommended", "ColoursIntense",
        "QualityOff", "QualityStandard", "QualityHigh",
        "LatencyLow", "LatencyBalanced", "LatencySafe",
        "FileOpenAppend", "FileOpenAppendPlay", "FileOpenReplace",
    ];

    /// <summary>
    /// Przekroczenie tej długości nie mieści się na przycisku segmentu przy najwęższym oknie.
    /// Wartość dobrana z pomiaru: 18 znaków w foncie Cantarell przy 11,5 punktu.
    /// </summary>
    private const int ShortLabelLimit = 18;

    /// <summary>
    /// Nazwy par barw domyślnej okładki. Stoją na przycisku szerokim na 168 px obok próbki
    /// barwy szerokiej na 34 px, więc na sam tekst zostaje około 113 px — mniej niż na pasku
    /// segmentowym, choć przycisk jest większy.
    /// </summary>
    /// <summary>
    /// Klucze wyprowadzone z wyliczenia, a nie wypisane. Lista wypisana z ręki rozjechała się
    /// już raz z rzeczywistością: doszło sześć par barw, a nazw dla nich nie dołożył nikt.
    /// </summary>
    private static readonly string[] PaletteLabels =
        Enum.GetValues<PlaceholderPalette>().Select(value => "Palette" + value).ToArray();

    private const int PaletteLabelLimit = 20;

    /// <summary>
    /// Nazwy zakładek w pionowym menu okna ustawień, które ma stałą szerokość 188 px.
    /// Dotąd nie pilnowało ich nic, a doszła kolejna zakładka.
    /// </summary>
    private static readonly string[] SectionLabels =
    [
        "SectionAppearance", "SectionLanguage", "SectionAudio",
        "SectionPlayback", "SectionSystem", "SectionAbout",
    ];

    /// <summary>
    /// Wartość zmierzona na renderze, nie wyliczona z szerokości menu. Najdłuższa istniejąca
    /// nazwa — hiszpańskie „Integración con el sistema", 26 znaków — zajmuje w menu około 160
    /// z 188 px i mieści się w całości. Na tekst zostaje około 160 px, czyli mniej więcej
    /// dwadzieścia osiem znaków przy tej wielkości pisma.
    /// </summary>
    private const int SectionLabelLimit = 28;

    /// <summary>Nazwa każdego języka zapisana w tym języku.</summary>
    private static readonly Dictionary<string, string> Endonyms = new()
    {
        ["pl"] = "Polski",
        ["en"] = "English",
        ["cs"] = "Čeština",
        ["de"] = "Deutsch",
        ["el"] = "Ελληνικά",
        ["es"] = "Español",
        ["fr"] = "Français",
        ["hu"] = "Magyar",
        ["id"] = "Bahasa Indonesia",
        ["it"] = "Italiano",
        ["nl"] = "Nederlands",
        ["pt"] = "Português",
        ["ro"] = "Română",
        ["ru"] = "Русский",
        ["tr"] = "Türkçe",
        ["uk"] = "Українська",
        ["vi"] = "Tiếng Việt",
    };

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

    /// <summary>
    /// Katalog nie zawiera plików spoza listy. Plik dołożony i nieuwzględniony na liście nie
    /// byłby sprawdzany przez nic poniżej, więc jego braki wyszłyby dopiero u użytkownika.
    /// </summary>
    [Fact]
    public void KatalogZawieraDokladnieJezykiZListy()
    {
        var found = System.IO.Directory
            .GetFiles(Directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Order()
            .ToArray();

        Assert.Equal(Codes.Order().ToArray(), found);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void KazdyJezykMaKompletZnakow(string code)
    {
        Assert.NotEmpty(Load(code));
    }

    [Theory]
    [MemberData(nameof(Translated))]
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
    [MemberData(nameof(Translated))]
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
    [MemberData(nameof(All))]
    public void EtykietySegmentowMieszczaSieNaPrzycisku(string code)
    {
        var strings = Load(code);

        var toolong = ShortLabels
            .Where(key => strings.TryGetValue(key, out var value) && value.Length > ShortLabelLimit)
            .Select(key => $"{key}=\"{strings[key]}\" ({strings[key].Length})")
            .ToArray();

        Assert.True(
            toolong.Length == 0,
            $"{code}.json ma etykiety dłuższe niż {ShortLabelLimit} znaków: {string.Join(", ", toolong)}");
    }

    [Theory]
    [MemberData(nameof(All))]
    public void NazwyZakladekMieszczaSieWMenu(string code)
    {
        var strings = Load(code);

        var toolong = SectionLabels
            .Where(key => strings.TryGetValue(key, out var value) && value.Length > SectionLabelLimit)
            .Select(key => $"{key}=\"{strings[key]}\" ({strings[key].Length})")
            .ToArray();

        Assert.True(
            toolong.Length == 0,
            $"{code}.json ma nazwy zakładek dłuższe niż {SectionLabelLimit} znaków: {string.Join(", ", toolong)}");
    }

    /// <summary>
    /// Każda para barw domyślnej okładki musi mieć nazwę — także ta dołożona wczoraj.
    ///
    /// <para>Sprawdzenie długości poniżej pomija klucze, których nie ma, więc samo nie wyłapałoby
    /// braku. A brak wygląda w oknie ustawień jak pozycja podpisana <c>[PaletteCoś]</c>, czego
    /// nikt nie zauważy, dopóki nie otworzy tej zakładki w tym właśnie języku.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(All))]
    public void KazdaParaBarwMaNazwe(string code)
    {
        var strings = Load(code);
        var missing = PaletteLabels.Where(key => !strings.ContainsKey(key)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"{code}.json nie ma nazw par barw: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(All))]
    public void NazwyBarwMieszczaSieObokProbki(string code)
    {
        var strings = Load(code);

        var toolong = PaletteLabels
            .Where(key => strings.TryGetValue(key, out var value) && value.Length > PaletteLabelLimit)
            .Select(key => $"{key}=\"{strings[key]}\" ({strings[key].Length})")
            .ToArray();

        Assert.True(
            toolong.Length == 0,
            $"{code}.json ma nazwy barw dłuższe niż {PaletteLabelLimit} znaków: {string.Join(", ", toolong)}");
    }

    /// <summary>
    /// Symbole zastępcze przechodzą przez <c>string.Format</c>. Tłumacz, który zamieni je na
    /// tekst albo przestawi specyfikator formatu, wywołałby wyjątek dopiero w czasie działania
    /// programu — i to wyłącznie w swoim języku.
    /// </summary>
    [Theory]
    [MemberData(nameof(All))]
    public void SymboleZastepczePrzetrwalyTlumaczenie(string code)
    {
        var strings = Load(code);

        Assert.True(strings.TryGetValue("LatencyMeasured", out var measured));
        Assert.Contains("{0}", measured);
        Assert.Contains("{1:F1}", measured);

        // Sprawdzenie właściwe: tekst musi dać się sformatować bez wyjątku.
        Assert.Contains("1024", string.Format(measured, 1024, 21.3));

        foreach (var key in (string[])["PlaylistSaved", "PlaylistMissing"])
        {
            Assert.True(strings.TryGetValue(key, out var text), $"{code}.json nie ma klucza {key}");
            Assert.Contains("{0}", text);
            Assert.Contains("7", string.Format(text, 7));
        }
    }

    /// <summary>
    /// Jedno kodowanie dla wszystkich plików językowych.
    ///
    /// <para>Program czyta je przez <c>StreamReader</c>, który znacznik BOM usuwa sam, więc nie
    /// chodzi o poprawność działania. Chodzi o to, żeby pliki tego samego rodzaju miały jedno
    /// kodowanie: RFC 8259 zabrania dopisywania znacznika do dokumentu JSON, a plik, który go ma,
    /// wygląda w narzędziach porównujących na zmieniony w pierwszej linii bez powodu.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(All))]
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
    [MemberData(nameof(All))]
    public void NazwaJezykaZapisanaWJezykuWlasnym(string code)
    {
        var strings = Load(code);

        Assert.Equal(Endonyms[code], strings["_language"]);
        Assert.Equal(code, strings["_code"]);
    }

    /// <summary>
    /// Znaki z diakrytyką muszą być zapisane w postaci złożonej, jako pojedyncze znaki.
    ///
    /// <para>Osadzony Cantarell zawiera dwadzieścia dziewięć znaków łączących, w tym akut, grawis
    /// i kropkę dolną — więc zapis rozłożony przeszedłby sprawdzenie pokrycia fontu i nie zostałby
    /// przez nic zauważony. Wyświetliłby się natomiast źle: font nie ma zakotwiczeń pozwalających
    /// ułożyć dwa znaki diakrytyczne jeden nad drugim, a właśnie tego wymaga wietnamski, w którym
    /// litera nosi jednocześnie znak barwy samogłoski i znak tonu. Sprawdzenie jest wprost na
    /// zakresie znaków łączących, bez wywoływania normalizacji — ta wymaga biblioteki ICU,
    /// której to wydanie celowo nie ładuje.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(All))]
    public void ZnakiZDiakrytykaZapisaneWPostaciZlozonej(string code)
    {
        var strings = Load(code);

        var decomposed = strings
            .SelectMany(pair => pair.Value.Select(character => (pair.Key, character)))
            .Where(item => item.character >= 0x0300 && item.character <= 0x036F)
            .Select(item => $"{item.Key}: U+{(int)item.character:X4}")
            .Distinct()
            .ToArray();

        Assert.True(
            decomposed.Length == 0,
            $"{code}.json ma znaki łączące, czyli zapis rozłożony: {string.Join(", ", decomposed)}");
    }

    /// <summary>
    /// Każdy znak w każdym tekście musi mieć glif w osadzonym foncie interfejsu.
    ///
    /// <para><b>Dlaczego czytany jest sam font.</b> Wcześniej ten sprawdzian miał wpisaną na
    /// sztywno granicę U+0250 i listę dopuszczonych znaków interpunkcyjnych — dobraną ręcznie do
    /// tego, co zawierał osadzony wtedy Cantarell 1.004. Po wymianie fontu na wydanie 0.303,
    /// które ma cyrylicę i grekę, taka lista opisywała już nie ten font i odrzucałaby teksty
    /// rosyjskie oraz ukraińskie, choć font potrafi je narysować. Teraz sprawdzian pyta font,
    /// a nie pamięć: znak, którego font nie ma, wyświetla się jako pusty prostokąt i wykryć to
    /// można inaczej tylko patrząc na ekran w tym właśnie języku.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(All))]
    public void TekstyUzywajaWylacznieZnakowObecnychWFoncie(string code)
    {
        var font = FontCoverage.Load(Path.Combine(AppContext.BaseDirectory, "Fonts", "Cantarell-Regular.otf"));

        // Zabezpieczenie przed odczytem, który „udał się" i nie znalazł niczego: przy pustym
        // zbiorze każdy tekst przeszedłby jako pozbawiony znaków spoza fontu.
        Assert.True(font.GlyphCount > 500, $"odczyt tablicy znaków fontu dał tylko {font.GlyphCount} pozycji");

        var strings = Load(code);

        var offending = strings
            .SelectMany(pair => pair.Value.Select(character => (pair.Key, character)))
            .Where(item => !char.IsControl(item.character) && !font.HasGlyph(item.character))
            .Select(item => $"{item.Key}: U+{(int)item.character:X4} '{item.character}'")
            .Distinct()
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"{code}.json ma znaki, których nie ma w foncie: {string.Join(", ", offending)}");
    }
}
