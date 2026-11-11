using System.Text;
using System.Text.Unicode;

namespace Cewka.Audio.Streaming;

/// <param name="Title">Null, gdy blok nie zawiera pola tytułu; pusty tekst, gdy pole jest puste.</param>
internal readonly record struct IcyFields(string? Title, bool IsAd);

/// <summary>Treść bloku metadanych ICY. Stacje piszą ją w różnych kodowaniach i nie zawsze poprawnie.</summary>
internal static class IcyMetadataParser
{
    private const int MaxTitleChars = 300;

    private static readonly Lazy<bool> CodePages = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    public static IcyFields Parse(ReadOnlySpan<byte> block, Encoding fallback)
    {
        var end = block.Length;
        while (end > 0 && block[end - 1] == 0)
            end--;
        block = block[..end];

        return new IcyFields(ParseTitle(block, fallback), IndexOfIgnoreCase(block, "adw_ad='true'"u8) >= 0);
    }

    private static string? ParseTitle(ReadOnlySpan<byte> block, Encoding fallback)
    {
        var key = "streamtitle='"u8;
        var start = IndexOfIgnoreCase(block, key);
        if (start < 0)
            return null;

        var value = block[(start + key.Length)..];
        var end = value.IndexOf("';"u8);
        if (end >= 0)
        {
            value = value[..end];
        }
        else
        {
            while (!value.IsEmpty && value[^1] is 0 or (byte)'\'')
                value = value[..^1];
        }

        return Utf8.IsValid(value) ? Encoding.UTF8.GetString(value) : fallback.GetString(value);
    }

    /// <summary>Klient podaje nagłówki jako Latin-1, czyli bajt w bajt; rozpoznanie jak przy tytułach.</summary>
    public static string? DecodeHeader(string? latin1, Encoding fallback)
    {
        if (latin1 is null)
            return null;

        var bytes = Encoding.Latin1.GetBytes(latin1);
        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : fallback.GetString(bytes);
    }

    /// <summary>Strona kodowa, którą stacje z danego kraju piszą tytuły spoza UTF-8.</summary>
    public static Encoding FallbackFor(string? countryCode)
    {
        _ = CodePages.Value;
        var codePage = countryCode?.ToUpperInvariant() switch
        {
            "PL" or "CZ" or "HU" or "RO" => 1250,
            "UA" => 1251,
            "GR" or "CY" => 1253,
            "TR" => 1254,
            "VN" => 1258,
            _ => 1252,
        };

        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Tytuł do pokazania: bez znaków sterujących i kierunku; pusty albo równy nazwie stacji to null.</summary>
    public static string? Normalise(string? title, string? stationName)
    {
        if (title is null)
            return null;

        var text = new StringBuilder(Math.Min(title.Length, MaxTitleChars + 1));
        foreach (var c in title)
        {
            if (text.Length > MaxTitleChars)
                break;

            if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && text[^1] != ' ')
                    text.Append(' ');
            }
            else if (!char.IsControl(c) && !IsDirectionMark(c))
            {
                text.Append(c);
            }
        }

        if (text.Length > MaxTitleChars)
        {
            text.Length = MaxTitleChars;
            if (char.IsHighSurrogate(text[^1]))
                text.Length--;
        }

        var result = text.ToString().TrimEnd();
        if (result.Length == 0)
            return null;
        if (string.Equals(result, stationName?.Trim(), StringComparison.OrdinalIgnoreCase) || IsFiller(result))
            return null;
        return result;
    }

    /// <summary>Napisy zastępcze, które stacje wysyłają zamiast tytułu; porównanie całego tekstu, bez wielkości liter i znaków na końcu.</summary>
    private static readonly HashSet<string> FillerTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "now playing",
        "now playing info goes here",
        "no title",
        "no title available",
        "no information",
        "no info",
        "unknown",
        "unknown title",
        "title unknown",
        "untitled",
        "n/a",
    };

    private static bool IsFiller(string text)
    {
        var core = text.TrimEnd(' ', '.', '!', ':', ';', ',', '-', '…', '_', '?').TrimStart(' ', '-', '.');
        return core.Length > 0 && FillerTitles.Contains(core);
    }

    private static bool IsDirectionMark(char c) => c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩';

    /// <summary>Wzorzec małymi literami; wielkość liter ma znaczenie tylko dla ASCII.</summary>
    private static int IndexOfIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> lowerPattern)
    {
        for (var i = 0; i <= data.Length - lowerPattern.Length; i++)
        {
            var j = 0;
            while (j < lowerPattern.Length && ToLower(data[i + j]) == lowerPattern[j])
                j++;
            if (j == lowerPattern.Length)
                return i;
        }

        return -1;
    }

    private static byte ToLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
}
