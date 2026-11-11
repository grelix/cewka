using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Teksty interfejsu w językach, w których powstają (polski i angielski), nie zwracają się do
/// użytkownika i nie używają form zakazanych w standardzie tekstów.
///
/// <para>Pozostałe języki są tłumaczeniami tych dwóch, więc ich forma wynika z wzorca. Wzorce
/// nie używają <c>\b</c>, bo ta granica nie rozpoznaje polskich liter; zamiast niej stoi
/// <c>\p{L}</c>. Tryb rozkazujący przycisków i poleceń (Zapisz, Odtwórz, Spróbuj ponownie)
/// nie jest na liście, więc nie wymaga wyjątków.</para>
/// </summary>
public sealed class TextStandardTests
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Formy drugiej osoby, tryb rozkazujący zwracający się do czytelnika, słowa zbędne.</summary>
    private static readonly Dictionary<string, Regex> Forbidden = new()
    {
        ["pl"] = new Regex(
            @"(?<!\p{L})(ty|tw(ój|oja|oje|oich|oim|oją|ojego|ojej)|ci|cię|możesz|możecie|musisz|musicie|kliknij|kliknijcie|wpisz|zapiszcie|upewnij się" +
            @"|proszę|przepraszam|przepraszamy|niestety|ups|pomyślnie)(?!\p{L})|ł(eś|aś)(?!\p{L})",
            Options),
        ["en"] = new Regex(
            @"(?<!\p{L})(you|your|yours|yourself|we|our|my|i|please|sorry|apologi[sz]e|unfortunately|successfully|oops|click|make sure|be sure)(?!\p{L})",
            Options),
    };

    private static readonly Regex Exclamation = new("!");

    /// <summary>Symbole z bloków emotikon i piktogramów oraz emotikony znakowe.</summary>
    private static readonly Regex Pictograms = new(
        @"[\uD83C-\uD83E][\uDC00-\uDFFF]|[☀-➿]|(?<!\S)[:;]-?[)(DP](?!\p{L})");

    /// <summary>Wyraz złożony z samych wielkich liter, dłuższy niż trzy litery, jest krzykiem, chyba że to skrót.</summary>
    private static readonly Regex Shouting = new(@"(?<!\p{L})\p{Lu}{4,}(?!\p{L})");

    /// <summary>Skróty techniczne, które z natury piszą się wielkimi literami.</summary>
    private static readonly string[] AllowedAbbreviations = ["LUFS", "FLAC", "ALAC", "MPRIS", "ReplayGain", "OLED"];

    public static TheoryData<string> Written => new("pl", "en");

    private static Dictionary<string, string> Load(string code)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Languages", $"{code}.json");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));

        Assert.NotNull(parsed);
        return parsed;
    }

    /// <summary>Klucze i wartości do sprawdzenia: metadane pliku nie są tekstem interfejsu.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Texts(string code) =>
        Load(code).Where(pair => !pair.Key.StartsWith('_') && pair.Key != "AppName");

    [Theory]
    [MemberData(nameof(Written))]
    public void TekstyNieMajaFormZakazanychWStandardzie(string code)
    {
        var pattern = Forbidden[code];

        var found = Texts(code)
            .Select(pair => (pair.Key, Match: pattern.Match(pair.Value)))
            .Where(item => item.Match.Success)
            .Select(item => $"{item.Key}: \"{item.Match.Value}\"")
            .ToArray();

        Assert.True(found.Length == 0, $"{code}.json ma formy zakazane: {string.Join("; ", found)}");
    }

    [Theory]
    [MemberData(nameof(Written))]
    public void TekstyNieMajaWykrzyknikowIEmotikon(string code)
    {
        var found = Texts(code)
            .Where(pair => Exclamation.IsMatch(pair.Value) || Pictograms.IsMatch(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();

        Assert.True(found.Length == 0, $"{code}.json ma wykrzykniki lub emotikony w kluczach: {string.Join(", ", found)}");
    }

    [Theory]
    [MemberData(nameof(Written))]
    public void TekstyNieMajaWyrazowWersalikami(string code)
    {
        var found = Texts(code)
            .SelectMany(pair => Shouting.Matches(pair.Value)
                .Select(match => (pair.Key, Word: match.Value)))
            .Where(item => !AllowedAbbreviations.Contains(item.Word))
            .Select(item => $"{item.Key}: {item.Word}")
            .ToArray();

        Assert.True(found.Length == 0, $"{code}.json ma wyrazy wersalikami: {string.Join("; ", found)}");
    }

    /// <summary>
    /// Wzorce muszą łapać to, do czego służą. Wzorzec, który przestał cokolwiek znajdować
    /// (zła flaga, zła granica wyrazu), przepuszczałby każde naruszenie bez śladu.
    /// </summary>
    [Theory]
    [InlineData("pl", "Twój plik został zapisany")]
    [InlineData("pl", "Możesz zamknąć okno")]
    [InlineData("pl", "Nie podałeś hasła")]
    [InlineData("pl", "Nie wybrałaś pliku")]
    [InlineData("pl", "Kliknij tutaj")]
    [InlineData("pl", "Upewnij się, że plik istnieje")]
    [InlineData("pl", "Za błędy przepraszam")]
    [InlineData("pl", "Niestety plik jest uszkodzony")]
    [InlineData("pl", "Zapisano pomyślnie")]
    [InlineData("pl", "Ups, coś poszło nie tak")]
    [InlineData("en", "Your file was saved")]
    [InlineData("en", "Please close the window")]
    [InlineData("en", "I apologise for the mistakes")]
    [InlineData("en", "Click here")]
    [InlineData("en", "Unfortunately the file is damaged")]
    [InlineData("en", "Saved successfully")]
    public void WzorceZakazanychFormWykrywajaNaruszenia(string code, string text)
    {
        Assert.Matches(Forbidden[code], text);
    }

    [Theory]
    [InlineData("Nie można otworzyć pliku.")]
    [InlineData("Zapisz")]
    [InlineData("Spróbuj ponownie")]
    [InlineData("Dźwięk jest kierowany do urządzenia domyślnego.")]
    [InlineData("Użytkownik może wybrać inny plik.")]
    public void WzorzecPolskiNieLapieDozwolonychTekstow(string text)
    {
        Assert.DoesNotMatch(Forbidden["pl"], text);
    }

    [Fact]
    public void WzorzecWersalikowLapieKrzykIPrzepuszczaSkroty()
    {
        Assert.Matches(Shouting, "ZAPISZ GRAFIKĘ");
        Assert.Matches(Shouting, "PREAMP");
        Assert.DoesNotMatch(Shouting, "Preamp");
        Assert.DoesNotMatch(Shouting, "Pliki WAV i MP3");
        Assert.Matches(Pictograms, "Gotowe :)");
        Assert.Matches(Pictograms, "Gotowe \U0001F600");
        Assert.DoesNotMatch(Pictograms, "Powtarzanie: wyłączone (R)");
    }

    /// <summary>Jednoliterowy spójnik lub przyimek polski ze zwykłą spacją za sobą: wisi na końcu wiersza.</summary>
    private static readonly Regex HangingConjunction = new(@"(?<![\p{L}\p{N}+])[aiouwzAIOUWZ] (?=\S)");

    /// <summary>Liczba (albo symbol liczby) oddzielona zwykłą spacją od jednostki.</summary>
    private static readonly Regex SplitUnit = new(@"[\d}] (?:dB|LUFS|kHz|Hz|ms)(?!\p{L})");

    [Fact]
    public void PolskieTekstyMajaTwardaSpacjePoJednoliterowychSpojnikach()
    {
        var found = Texts("pl")
            .Where(pair => HangingConjunction.IsMatch(pair.Value))
            .Select(pair => $"{pair.Key}: \"{HangingConjunction.Match(pair.Value).Value}\"")
            .ToArray();

        Assert.True(found.Length == 0, $"pl.json ma wiszące spójniki: {string.Join("; ", found)}");
    }

    [Theory]
    [MemberData(nameof(Written))]
    public void LiczbaZJednostkaNieRozdzielaSieNaKoniecWiersza(string code)
    {
        var found = Texts(code)
            .Where(pair => SplitUnit.IsMatch(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();

        Assert.True(found.Length == 0, $"{code}.json oddziela liczbę od jednostki zwykłą spacją: {string.Join(", ", found)}");
    }

    [Fact]
    public void WzorceTwardychSpacjiLapiaZwykleSpacje()
    {
        Assert.Matches(HangingConjunction, "Pliki i odtwarzanie");
        Assert.Matches(HangingConjunction, "O programie");
        Assert.DoesNotMatch(HangingConjunction, "Pliki i odtwarzanie");
        Assert.DoesNotMatch(HangingConjunction, "Skrót Ctrl+, otwiera");
        Assert.Matches(SplitUnit, "Poziom −23 LUFS");
        Assert.Matches(SplitUnit, "ok. {1:F1} ms");
        Assert.DoesNotMatch(SplitUnit, "Poziom −23 LUFS");
    }

    /// <summary>Teksty zdaniowe: podpowiedzi, komunikaty, opisy. Kończą się kropką.</summary>
    private static readonly Regex SentenceKey = new(
        @"^(Hint[A-Z]|EffectDescription|AudioFailure|DecoderFailure|TrackFailure|MediaPanel[A-Z]" +
        @"|Associations(Current|Stale|Absent|Unsupported|Failed)|Update(Available|Current|Failed|Notice)$" +
        @"|Playlist(Saved|Missing|Empty|LoadFailed|SaveFailed)$|Device(Idle|LostNotice|FallbackNotice)$" +
        @"|DropHint$|FolderReadFailed$|Latency(Measured|Unknown)$|TracksSkipped$)");

    /// <summary>Etykiety, nazwy, podpowiedzi przycisków i skróty: bez kropki na końcu.</summary>
    private static readonly Regex LabelKey = new(
        @"^(Setting|Section|Option|Tooltip|Shortcut|Palette|Queue[A-Z]|Theme|Controls|Effects[A-Z]|Colours[A-Z]|Quality[A-Z]" +
        @"|Latency(Low|Balanced|Safe)$|FileOpen|Open|About|Effect(Crossfeed|Loudness|VirtualBass|DynamicRange|StereoWidth|Strength)$)");

    [Theory]
    [MemberData(nameof(Written))]
    public void TekstyZdaniowePrzechodzaKropkaAEtykietyNie(string code)
    {
        var withoutPeriod = Texts(code)
            .Where(pair => SentenceKey.IsMatch(pair.Key) && !pair.Value.EndsWith('.'))
            .Select(pair => pair.Key)
            .ToArray();

        var withPeriod = Texts(code)
            .Where(pair => LabelKey.IsMatch(pair.Key) && pair.Value.EndsWith('.'))
            .Select(pair => pair.Key)
            .ToArray();

        Assert.True(withoutPeriod.Length == 0, $"{code}.json: zdania bez kropki: {string.Join(", ", withoutPeriod)}");
        Assert.True(withPeriod.Length == 0, $"{code}.json: etykiety z kropką: {string.Join(", ", withPeriod)}");
    }

    private static readonly Regex SentenceEnd = new(@"[.!?…](?=\s+\p{Lu}|\s*$)");

    /// <summary>Podpowiedź ma najwyżej dwa zdania; dłuższy wywód należy do dokumentacji.</summary>
    [Theory]
    [MemberData(nameof(Written))]
    public void PodpowiedziMajaNajwyzejDwaZdania(string code)
    {
        var tooLong = Texts(code)
            .Where(pair => pair.Key.StartsWith("Hint", StringComparison.Ordinal))
            .Select(pair => (pair.Key, Sentences: SentenceEnd.Matches(pair.Value).Count))
            .Where(item => item.Sentences > 2)
            .Select(item => $"{item.Key} ({item.Sentences})")
            .ToArray();

        Assert.True(tooLong.Length == 0, $"{code}.json ma podpowiedzi dłuższe niż dwa zdania: {string.Join(", ", tooLong)}");
    }

    /// <summary>Tekst, który wskazuje przycisk po nazwie, musi używać nazwy ze słownika.</summary>
    [Theory]
    [MemberData(nameof(Written))]
    public void OdwolaniaDoPrzyciskowUzywajaIchNazwZeSlownika(string code)
    {
        var strings = Load(code);

        (string Text, string Button)[] references =
        [
            ("HintQuality", "QualityOff"),
            ("HintUpdateCheck", "UpdateCheckNow"),
            ("AssociationsStale", "AssociationsRegister"),
        ];

        foreach (var (text, button) in references)
            Assert.Contains(strings[button], strings[text]);
    }

    [Fact]
    public void WzorceKluczyIZdanRozpoznajaTypyTekstow()
    {
        Assert.Matches(SentenceKey, "HintQuality");
        Assert.Matches(SentenceKey, "AudioFailureDevice");
        Assert.Matches(SentenceKey, "TrackFailureDamagedFile");
        Assert.DoesNotMatch(SentenceKey, "AudioRetry");
        Assert.Matches(LabelKey, "SettingQuality");
        Assert.Matches(LabelKey, "TooltipRepeatOff");
        Assert.Matches(LabelKey, "FileOpenReplace");
        Assert.DoesNotMatch(LabelKey, "HintQuality");

        Assert.Single(SentenceEnd.Matches("Jedno zdanie, bez końca w środku."));
        Assert.Equal(2, SentenceEnd.Matches("Pierwsze. Drugie.").Count);
        Assert.Equal(3, SentenceEnd.Matches("Pierwsze. Drugie. Trzecie.").Count);
    }

    /// <summary>
    /// Jedno pojęcie, jedna nazwa: synonimy, które wcześniej oznaczały to samo co nazwa przyjęta
    /// w słowniku, nie wracają do tekstów.
    /// </summary>
    private static readonly Dictionary<string, Regex> Synonyms = new()
    {
        // normalizacja głośności (nie „wyrównanie”), skojarzenia plików (nie „rejestracja formatów”),
        // panel (nie „pas”), urządzenie (nie „wyjście”), pełna nazwa przedwzmacniacza (nie „preamp”)
        ["pl"] = new Regex(
            @"wyrówn|zarejestruj formaty|usuń rejestrację|(?<!\p{L})pasie(?!\p{L})|aktywne wyjście|(?<!\p{L})preamp",
            Options),
        ["en"] = new Regex(
            @"loudness matching|matches loudness|register formats|remove registration|in the strip|active output|preamplifier|blobs" +
            @"|(?<![\w-])start(?![\w-])|in the system(?! (settings|media))",
            Options),
    };

    [Theory]
    [MemberData(nameof(Written))]
    public void TekstyNieUzywajaZarzuconychSynonimow(string code)
    {
        var found = Texts(code)
            .Select(pair => (pair.Key, Match: Synonyms[code].Match(pair.Value)))
            .Where(item => item.Match.Success)
            .Select(item => $"{item.Key}: \"{item.Match.Value}\"")
            .ToArray();

        Assert.True(found.Length == 0, $"{code}.json ma zarzucone synonimy: {string.Join("; ", found)}");
    }
}
