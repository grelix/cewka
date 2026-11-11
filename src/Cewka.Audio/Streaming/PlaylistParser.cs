using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Cewka.Audio.Streaming;

/// <summary>Adresy z list PLS, M3U, ASX i XSPF. Dane pochodzą z sieci: błąd składni to mniej wpisów, nie wyjątek.</summary>
internal static class PlaylistParser
{
    // ASX bywa niepoprawnym XML-em, więc wyrażenie zamiast czytnika.
    private static readonly Regex AsxRef = new(
        """<ref\b[^>]*?\bhref\s*=\s*(?:"([^"]*)"|'([^']*)')""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>Łączny limit adresów jednego otwarcia: więcej i tak nikt nie sprawdzi.</summary>
    public const int MaxEntries = 32;

    /// <summary>Tyle wierszy najwyżej idzie na adres. Każdy kopiuje adres bazowy, a powtórki nie zbliżają do limitu wpisów.</summary>
    private const int MaxLinesTried = 1024;

    /// <summary>Działa bez tokenu, więc koszt ma granicę niezależną od tego, co podał serwer.</summary>
    public static IReadOnlyList<Uri> Parse(ReadOnlySpan<byte> data, PlaylistKind kind, Uri baseUrl)
    {
        var text = Decode(data);
        var entries = kind switch
        {
            PlaylistKind.M3u => M3uEntries(text),
            PlaylistKind.Pls => PlsEntries(text),
            PlaylistKind.Asx => AsxEntries(text),
            PlaylistKind.Xspf => XspfEntries(text),
            _ => [],
        };

        var urls = new List<Uri>();
        foreach (var entry in entries.Take(MaxLinesTried))
        {
            if (Uri.TryCreate(baseUrl, entry.Trim(), out var url) && url.Scheme is "http" or "https" && !urls.Contains(url))
                urls.Add(url);
            if (urls.Count == MaxEntries)
                break;
        }

        return urls;
    }

    private static string Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data[2..]);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data[2..]);
        if (data.StartsWith<byte>([0xEF, 0xBB, 0xBF]))
            data = data[3..];
        return Encoding.UTF8.GetString(data);
    }

    private static List<string> M3uEntries(string text)
    {
        var entries = new List<string>();
        foreach (var range in text.AsSpan().SplitAny('\r', '\n'))
        {
            var line = text.AsSpan()[range].Trim();
            if (!line.IsEmpty && line[0] != '#')
                entries.Add(line.ToString());
        }

        return entries;
    }

    private static List<string> PlsEntries(string text)
    {
        var numbered = new List<(int Number, string Url)>();
        foreach (var range in text.AsSpan().SplitAny('\r', '\n'))
        {
            var line = text.AsSpan()[range].Trim();
            var equals = line.IndexOf('=');
            if (equals <= 4 || !line.StartsWith("file", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(line[4..equals], out var number))
                numbered.Add((number, line[(equals + 1)..].ToString()));
        }

        return numbered.OrderBy(e => e.Number).Select(e => e.Url).ToList();
    }

    private static List<string> AsxEntries(string text)
    {
        var entries = new List<string>();
        try
        {
            foreach (Match match in AsxRef.Matches(text))
            {
                var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                entries.Add(WebUtility.HtmlDecode(value));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Zostaje to, co zdążyło się dopasować.
        }

        return entries;
    }

    private static List<string> XspfEntries(string text)
    {
        var entries = new List<string>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var inTrack = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "track")
                {
                    inTrack = false;
                }
                else if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "track")
                        inTrack = !reader.IsEmptyElement;
                    else if (inTrack && reader.LocalName == "location")
                        entries.Add(reader.ReadElementContentAsString());
                }
            }
        }
        catch (XmlException)
        {
            // Wpisy sprzed błędu zostają; lista z DTD nie ma ich wcale.
        }

        return entries;
    }
}
