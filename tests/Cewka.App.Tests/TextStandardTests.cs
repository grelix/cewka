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
    private static readonly string[] AllowedAbbreviations = ["LUFS", "FLAC", "ALAC", "MPRIS", "ReplayGain"];

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
}
