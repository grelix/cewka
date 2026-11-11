using System.Text;

namespace Cewka.Audio.Streaming;

internal enum SniffKind { NeedMore, Audio, Hls, Playlist, Html, Unknown }

internal enum PlaylistKind { None, M3u, Pls, Asx, Xspf }

/// <summary>
/// Rozpoznaje treść po bajtach: nagłówki stacji bywają nieprawdziwe, a dane mogą zaczynać się
/// w środku ramki. Typ treści rozstrzyga tylko, czy niezrozumiała odpowiedź to strona WWW.
/// </summary>
internal static class ContentSniffer
{
    private const string M3uHeader = "#EXTM3U";
    private const string HlsTag = "#EXT-X-";

    /// <summary>Tyle znacznika ID3 rozpoznanie przeczeka; dekoder nie czeka na dźwięk dłużej.</summary>
    private const int MaxLeadingTagBytes = 256 * 1024;

    /// <param name="final">Więcej danych nie będzie: brak rozstrzygnięcia to Unknown albo Html.</param>
    public static SniffKind Classify(
        ReadOnlySpan<byte> head, string? contentType, out LiveCodec codec, out PlaylistKind list, bool final = false)
    {
        codec = LiveCodec.Unknown;
        list = PlaylistKind.None;

        var text = ClassifyText(head, ref list);
        if (text is not (null or SniffKind.NeedMore))
            return text.Value;

        if (ScanAudio(head, out codec))
            return SniffKind.Audio;

        if (!final)
            return SniffKind.NeedMore;
        return contentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true
            ? SniffKind.Html
            : SniffKind.Unknown;
    }

    private static SniffKind? ClassifyText(ReadOnlySpan<byte> head, ref PlaylistKind list)
    {
        string decoded;
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            decoded = Encoding.Unicode.GetString(head[2..]);
        else if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            decoded = Encoding.BigEndianUnicode.GetString(head[2..]);
        else if (head.StartsWith<byte>([0xEF, 0xBB, 0xBF]))
            decoded = Encoding.Latin1.GetString(head[3..]);
        else
            decoded = Encoding.Latin1.GetString(head);

        var text = decoded.AsSpan().TrimStart();
        if (text.IsEmpty)
            return SniffKind.NeedMore;

        return text[0] switch
        {
            '#' => ClassifyM3uComment(text, ref list),
            '[' => Signature(text, "[playlist]", PlaylistKind.Pls, ref list),
            '<' => ClassifyMarkup(text, ref list),
            'h' or 'H' => ClassifyUrlLine(text, ref list),
            _ => null,
        };
    }

    private static SniffKind? Signature(ReadOnlySpan<char> text, string signature, PlaylistKind kind, ref PlaylistKind list)
    {
        if (text.StartsWith(signature, StringComparison.OrdinalIgnoreCase))
        {
            list = kind;
            return SniffKind.Playlist;
        }

        return IsPartialOf(text, signature) ? SniffKind.NeedMore : null;
    }

    private static SniffKind? ClassifyM3uComment(ReadOnlySpan<char> text, ref PlaylistKind list)
    {
        if (text.StartsWith(M3uHeader, StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains(HlsTag, StringComparison.OrdinalIgnoreCase))
                return SniffKind.Hls;
            list = PlaylistKind.M3u;
            return SniffKind.Playlist;
        }

        if (IsPartialOf(text, M3uHeader) || IsPartialOf(text, HlsTag))
            return SniffKind.NeedMore;

        // Komentarze bez nagłówka: rozstrzyga pierwszy wiersz, który nim nie jest.
        var rest = text;
        while (!rest.IsEmpty)
        {
            var end = rest.IndexOfAny('\r', '\n');
            var line = (end < 0 ? rest : rest[..end]).Trim();
            if (line.StartsWith(HlsTag, StringComparison.OrdinalIgnoreCase))
                return SniffKind.Hls;
            if (!line.IsEmpty && line[0] != '#')
                return ClassifyUrlLine(line, ref list);
            if (end < 0)
                return SniffKind.NeedMore;
            rest = rest[(end + 1)..];
        }

        return SniffKind.NeedMore;
    }

    private static SniffKind? ClassifyUrlLine(ReadOnlySpan<char> text, ref PlaylistKind list)
    {
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            list = PlaylistKind.M3u;
            return SniffKind.Playlist;
        }

        return IsPartialOf(text, "http://") || IsPartialOf(text, "https://") ? SniffKind.NeedMore : null;
    }

    private static SniffKind? ClassifyMarkup(ReadOnlySpan<char> text, ref PlaylistKind list)
    {
        if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            var close = text.IndexOf("?>");
            if (close < 0)
                return SniffKind.NeedMore;

            var root = text[(close + 2)..];
            if (!SkipComments(ref root) || root.IsEmpty)
                return SniffKind.NeedMore;
            return ClassifyRoot(root, ref list) ?? SniffKind.Html;
        }

        if (IsPartialOf(text, "<?xml"))
            return SniffKind.NeedMore;
        return ClassifyRoot(text, ref list);
    }

    /// <summary>False, gdy komentarz jeszcze się nie skończył.</summary>
    private static bool SkipComments(ref ReadOnlySpan<char> text)
    {
        text = text.TrimStart();
        while (text.StartsWith("<!--"))
        {
            var close = text.IndexOf("-->");
            if (close < 0)
                return false;
            text = text[(close + 3)..].TrimStart();
        }

        return !IsPartialOf(text, "<!--");
    }

    private static SniffKind? ClassifyRoot(ReadOnlySpan<char> text, ref PlaylistKind list)
    {
        if (text.StartsWith("<asx", StringComparison.OrdinalIgnoreCase))
        {
            list = PlaylistKind.Asx;
            return SniffKind.Playlist;
        }

        if (text.StartsWith("<playlist", StringComparison.OrdinalIgnoreCase))
        {
            var close = text.IndexOf('>');
            var tag = close < 0 ? text : text[..close];
            if (tag.Contains("xspf.org", StringComparison.OrdinalIgnoreCase))
            {
                list = PlaylistKind.Xspf;
                return SniffKind.Playlist;
            }

            return close < 0 ? SniffKind.NeedMore : SniffKind.Html;
        }

        if (text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase))
            return SniffKind.Html;

        return IsPartialOf(text, "<asx") || IsPartialOf(text, "<playlist") || IsPartialOf(text, "<html")
               || IsPartialOf(text, "<!doctype")
            ? SniffKind.NeedMore
            : null;
    }

    private static bool IsPartialOf(ReadOnlySpan<char> text, string signature) =>
        text.Length < signature.Length && signature.AsSpan().StartsWith(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>O tyle dłużej warto czytać: znacznik na początku nie mówi nic o treści.</summary>
    public static int LeadingTagBytes(ReadOnlySpan<byte> head) => Math.Min(Id3Length(head), MaxLeadingTagBytes);

    private static int Id3Length(ReadOnlySpan<byte> head)
    {
        if (head.Length < 10 || !head.StartsWith("ID3"u8))
            return 0;

        var length = 10 + ((head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F));
        return (head[5] & 0x10) != 0 ? length + 10 : length;
    }

    private static bool ScanAudio(ReadOnlySpan<byte> head, out LiveCodec codec)
    {
        codec = LiveCodec.Unknown;
        if (head.StartsWith("ID3"u8) && head.Length < 10)
            return false;

        var start = Id3Length(head);
        if (start >= head.Length && start > 0)
            return false;

        if (head[start..].StartsWith("fLaC"u8))
        {
            codec = LiveCodec.Flac;
            return true;
        }

        for (var i = start; i < head.Length - 3; i++)
        {
            if (head[i] == (byte)'O' && head.Length - i >= 5 && head[i..].StartsWith("OggS\0"u8))
            {
                codec = LiveCodec.Ogg;
                return true;
            }

            if (head[i] != 0xFF || (head[i + 1] & 0xE0) != 0xE0)
                continue;

            if (TwoMpegFrames(head, i))
            {
                codec = LiveCodec.Mp3;
                return true;
            }

            if (TwoAdtsFrames(head, i))
            {
                codec = LiveCodec.Aac;
                return true;
            }
        }

        return false;
    }

    private static bool TwoMpegFrames(ReadOnlySpan<byte> data, int at)
    {
        var length = MpegFrameLength(data[at..]);
        if (length <= 0 || at + length + 4 > data.Length)
            return false;
        var next = at + length;
        const int sameStream = 0xFE; // wersja i warstwa
        return data[next] == 0xFF && (data[next + 1] & sameStream) == (data[at + 1] & sameStream)
            && (data[next + 2] & 0x0C) == (data[at + 2] & 0x0C)
            && MpegFrameLength(data[next..]) > 0;
    }

    /// <summary>Długość ramki z nagłówka; 0 przy nagłówku niepoprawnym albo wolnym formacie.</summary>
    private static int MpegFrameLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
            return 0;

        var version = (header[1] >> 3) & 3; // 0: 2.5, 2: 2, 3: 1
        var layer = (header[1] >> 1) & 3;   // 1: III, 2: II, 3: I
        var bitrateIndex = header[2] >> 4;
        var rateIndex = (header[2] >> 2) & 3;
        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rateIndex == 3)
            return 0;

        var mpeg1 = version == 3;
        var sampleRate = (rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 }) >> (mpeg1 ? 0 : version == 2 ? 1 : 2);
        var bitrate = 1000 * BitrateTable(mpeg1, layer)[bitrateIndex - 1];
        var padding = (header[2] >> 1) & 1;

        if (layer == 3)
            return (12 * bitrate / sampleRate + padding) * 4;
        var factor = layer == 1 && !mpeg1 ? 72 : 144;
        return factor * bitrate / sampleRate + padding;
    }

    private static ReadOnlySpan<short> BitrateTable(bool mpeg1, int layer) => (mpeg1, layer) switch
    {
        (true, 3) => [32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
        (true, 2) => [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
        (true, _) => [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
        (false, 3) => [32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
        (false, _) => [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    };

    private static bool TwoAdtsFrames(ReadOnlySpan<byte> data, int at)
    {
        var length = AdtsFrameLength(data[at..]);
        if (length <= 0 || at + length + 7 > data.Length)
            return false;
        var next = at + length;
        return AdtsFrameLength(data[next..]) > 0
            && (data[next + 1] & 0xF6) == (data[at + 1] & 0xF6)
            && (data[next + 2] & 0x3C) == (data[at + 2] & 0x3C);
    }

    private static int AdtsFrameLength(ReadOnlySpan<byte> header) =>
        Decoding.Live.AdtsHeader.TryParse(header, out var parsed) ? parsed.FrameLength : 0;
}
