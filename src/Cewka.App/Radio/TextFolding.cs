using System.Text;

namespace Cewka.App.Radio;

/// <summary>
/// Klucz porównań tekstu stacji i zapytań: małe litery, bez znaków diakrytycznych, grecki i cyrylica
/// w zapisie łacińskim, „102,2” równe „102.2”. Własna tablica, bo przy <c>InvariantGlobalization</c>
/// <c>string.Normalize</c> nie zdejmuje znaków diakrytycznych.
/// </summary>
public static class TextFolding
{
    private static readonly Dictionary<char, string> Map = BuildMap();

    // Ukraińskie х po łacinie bywa „kh” albo „h”; zapytanie „hit fm” ma znaleźć „Хіт FM”, a „khit” też.
    private static readonly Dictionary<char, string> AlternativeMap = new() { ['х'] = "kh" };

    /// <summary>Główny klucz tekstu.</summary>
    public static string Fold(string? text) => Convert(text, Map, alternative: false);

    /// <summary>Klucz z drugim zapisem znaków, które mają dwa; ten sam co <see cref="Fold"/>, gdy takich nie ma.</summary>
    public static string FoldAlternative(string? text) => Convert(text, Map, alternative: true);

    /// <summary>Klucz sortowania: ten sam co <see cref="Fold"/>, więc „Česko” leży przy „C”, a „Україна” przy „U”.</summary>
    public static string SortKey(string? text) => Fold(text);

    private static string Convert(string? text, Dictionary<char, string> map, bool alternative)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = new StringBuilder(text.Length + 4);
        var lastSpace = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= '̀' and <= 'ͯ')
                continue;

            // Przecinek dziesiętny w częstotliwości: „102,2” jako „102.2”.
            if (c == ',' && i > 0 && i + 1 < text.Length && char.IsAsciiDigit(text[i - 1]) && char.IsAsciiDigit(text[i + 1]))
                c = '.';

            if (c == '.' && i > 0 && i + 1 < text.Length && char.IsAsciiDigit(text[i - 1]) && char.IsAsciiDigit(text[i + 1]))
            {
                result.Append('.');
                lastSpace = false;
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            string? replacement = null;
            if (alternative && AlternativeMap.TryGetValue(lower, out var alt))
                replacement = alt;
            else if ((lower is 'υ' or 'ύ') && i > 0 && char.ToLowerInvariant(text[i - 1]) is 'ο' or 'ό')
                replacement = "u";
            else if (map.TryGetValue(lower, out var mapped))
                replacement = mapped;

            if (replacement is not null)
            {
                result.Append(replacement);
                lastSpace = replacement.Length == 0 && lastSpace;
                continue;
            }

            if (char.IsLetterOrDigit(lower))
            {
                result.Append(lower);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                result.Append(' ');
                lastSpace = true;
            }
        }

        return result.ToString().TrimEnd();
    }

    private static Dictionary<char, string> BuildMap()
    {
        var map = new Dictionary<char, string>();

        void Group(string target, string letters)
        {
            foreach (var letter in letters)
                map[letter] = target;
        }

        // Łacina: języki aplikacji i polskie, czeskie, rumuńskie, tureckie, węgierskie nazwy własne.
        Group("a", "àáâãäåāăąǎ");
        Group("c", "çćĉċč");
        Group("d", "ďđð");
        Group("e", "èéêëēĕėęěẽ");
        Group("g", "ĝğġģ");
        Group("h", "ĥħ");
        Group("i", "ìíîïĩīĭįıǐİ");
        Group("j", "ĵ");
        Group("k", "ķ");
        Group("l", "ĺļľŀł");
        Group("n", "ñńņňŉ");
        Group("o", "òóôõöøōŏőǒ");
        Group("r", "ŕŗř");
        Group("s", "śŝşšș");
        Group("t", "ţťŧț");
        Group("u", "ùúûüũūŭůűųǔ");
        Group("w", "ŵ");
        Group("y", "ýÿŷ");
        Group("z", "źżž");
        map['ß'] = "ss";
        map['æ'] = "ae";
        map['œ'] = "oe";
        map['þ'] = "th";

        // Wietnamski: litery złożone w jednym znaku.
        Group("a", "ảạằắẳẵặầấẩẫậ");
        Group("e", "ẻẹềếểễệ");
        Group("i", "ỉị");
        Group("o", "ỏọồốổỗộơờớởỡợ");
        Group("u", "ủụưừứửữự");
        Group("y", "ỳỷỹỵ");

        // Grecki: bez tonosu i dialityki, potem transliteracja.
        Group("a", "αάΑΆ");
        map['β'] = "v"; map['γ'] = "g"; map['δ'] = "d";
        Group("e", "εέ");
        map['ζ'] = "z";
        Group("i", "ηήιίϊΐ");
        map['θ'] = "th";
        map['κ'] = "k"; map['λ'] = "l"; map['μ'] = "m"; map['ν'] = "n"; map['ξ'] = "x";
        Group("o", "οόωώ");
        map['π'] = "p"; map['ρ'] = "r";
        Group("s", "σς");
        map['τ'] = "t";
        Group("y", "υύϋΰ");
        map['φ'] = "f"; map['χ'] = "ch"; map['ψ'] = "ps";

        // Cyrylica (ukraińska i pokrewne).
        map['а'] = "a"; map['б'] = "b"; map['в'] = "v"; map['г'] = "h"; map['ґ'] = "g"; map['д'] = "d";
        map['е'] = "e"; map['є'] = "ye"; map['ж'] = "zh"; map['з'] = "z"; map['и'] = "y"; map['і'] = "i";
        map['ї'] = "yi"; map['й'] = "y"; map['к'] = "k"; map['л'] = "l"; map['м'] = "m"; map['н'] = "n";
        map['о'] = "o"; map['п'] = "p"; map['р'] = "r"; map['с'] = "s"; map['т'] = "t"; map['у'] = "u";
        map['ф'] = "f"; map['х'] = "h"; map['ц'] = "ts"; map['ч'] = "ch"; map['ш'] = "sh"; map['щ'] = "shch";
        map['ь'] = ""; map['ю'] = "yu"; map['я'] = "ya"; map['ы'] = "y"; map['э'] = "e"; map['ъ'] = "";
        map['ё'] = "e";

        return map;
    }
}
