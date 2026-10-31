namespace Cewka.Audio.Decoding;

/// <summary>Container formats the player recognises.</summary>
public enum AudioFileFormat
{
    Unknown,
    Wav,
    Mp3,
    Flac,
    OggVorbis,
    Opus,

    /// <summary>MP4 family and raw ADTS: .m4a, .aac, .alac. Handled by system codecs.</summary>
    Mp4,

    /// <summary>Windows Media. Out of the declared scope; recognised only to explain itself.</summary>
    Wma,
}

/// <summary>
/// Works out what a file actually is. The extension is only a hint — it is checked against
/// the leading bytes, because a mislabelled file should fail with a clear message rather
/// than with a decoder error from deep inside a library.
/// </summary>
public static class AudioFileFormatDetector
{
    /// <summary>Extensions offered in file dialogs and accepted from drag and drop.</summary>
    public static readonly string[] SupportedExtensions =
    [
        ".mp3", ".flac", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".mp4", ".aac", ".alac",
    ];

    public static AudioFileFormat Detect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Detect(stream, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FromExtension(path);
        }
    }

    public static AudioFileFormat Detect(Stream stream, string? path = null)
    {
        Span<byte> header = stackalloc byte[16];
        var origin = stream.CanSeek ? stream.Position : 0;

        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (stream.CanSeek) stream.Position = origin;

        if (read >= 12)
        {
            // RIFF....WAVE
            if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E')
                return AudioFileFormat.Wav;

            // ftyp at offset 4 marks the MP4 family.
            if (header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p')
                return AudioFileFormat.Mp4;
        }

        if (read >= 4)
        {
            if (header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
                return AudioFileFormat.Flac;

            // OggS — the codec inside is named by the first packet.
            if (header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S')
                return DetectInsideOgg(stream, path);

            if (header[0] == 'I' && header[1] == 'D' && header[2] == '3' && read >= 10)
                return DetectBehindId3(stream, header, path);

            if (FromFrameSync(header) is { } framed)
                return framed;
        }

        if (read >= 16 && header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75)
            return AudioFileFormat.Wma;

        return FromExtension(path);
    }

    /// <summary>
    /// ADTS i ramka MPEG Audio mają ten sam 12-bitowy znacznik synchronizacji. Rozróżnia je
    /// pole warstwy: w ADTS zawsze 00, a w MPEG Audio ta wartość jest zarezerwowana.
    /// </summary>
    private static AudioFileFormat? FromFrameSync(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2 || header[0] != 0xFF) return null;

        if ((header[1] & 0xF6) == 0xF0) return AudioFileFormat.Mp4;
        if ((header[1] & 0xE0) == 0xE0 && (header[1] & 0x06) != 0) return AudioFileFormat.Mp3;

        return null;
    }

    /// <summary>
    /// Znacznik ID3v2 stoi przed MP3, ale także przed surowym AAC (podcasty) i czasem przed FLAC,
    /// więc o formacie rozstrzyga dopiero to, co leży za nim.
    /// </summary>
    private static AudioFileFormat DetectBehindId3(Stream stream, ReadOnlySpan<byte> header, string? path)
    {
        var hinted = FromExtension(path);
        var fallback = hinted is AudioFileFormat.Mp4 or AudioFileFormat.Flac ? hinted : AudioFileFormat.Mp3;

        if (!stream.CanSeek) return fallback;

        // Rozmiar zapisany jest w czterech bajtach po siedem bitów i nie obejmuje nagłówka ani stopki.
        var size = 10L + ((header[6] & 0x7F) << 21 | (header[7] & 0x7F) << 14 | (header[8] & 0x7F) << 7 | (header[9] & 0x7F));
        if ((header[5] & 0x10) != 0) size += 10;

        var origin = stream.Position;
        try
        {
            stream.Position = origin + size;

            Span<byte> next = stackalloc byte[4];
            var read = stream.ReadAtLeast(next, next.Length, throwOnEndOfStream: false);

            if (read >= 4 && next[0] == 'f' && next[1] == 'L' && next[2] == 'a' && next[3] == 'C')
                return AudioFileFormat.Flac;

            return (read >= 2 ? FromFrameSync(next) : null) ?? fallback;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>
    /// Vorbis, Opus and FLAC all travel inside Ogg. The codec identifier sits in the first
    /// packet, right after the 27-byte page header plus the segment table. A readable packet
    /// naming some other codec gives Unknown, so the queue does not call the file damaged.
    /// </summary>
    private static AudioFileFormat DetectInsideOgg(Stream stream, string? path)
    {
        if (!stream.CanSeek) return FromExtension(path);

        var origin = stream.Position;
        try
        {
            Span<byte> page = stackalloc byte[64];
            var read = stream.ReadAtLeast(page, page.Length, throwOnEndOfStream: false);
            if (read < 35) return FromExtension(path);

            var segmentCount = page[26];
            var payload = 27 + segmentCount;
            if (payload + 8 > read) return FromExtension(path);

            var body = page[payload..];

            if (body.Length >= 8 && body[0] == 'O' && body[1] == 'p' && body[2] == 'u' && body[3] == 's')
                return AudioFileFormat.Opus;

            if (body.Length >= 7 && body[1] == 'v' && body[2] == 'o' && body[3] == 'r' &&
                body[4] == 'b' && body[5] == 'i' && body[6] == 's')
                return AudioFileFormat.OggVorbis;

            // Ogg FLAC: 0x7F i „FLAC”. Dekoder FLAC w miniaudio sam rozpakowuje kontener Ogg.
            if (body.Length >= 5 && body[0] == 0x7F && body[1] == 'F' && body[2] == 'L' &&
                body[3] == 'A' && body[4] == 'C')
                return AudioFileFormat.Flac;

            return AudioFileFormat.Unknown;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static AudioFileFormat FromExtension(string? path) =>
        Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
        {
            ".mp3" => AudioFileFormat.Mp3,
            ".flac" => AudioFileFormat.Flac,
            ".wav" or ".wave" => AudioFileFormat.Wav,
            ".ogg" or ".oga" => AudioFileFormat.OggVorbis,
            ".opus" => AudioFileFormat.Opus,
            ".m4a" or ".mp4" or ".aac" or ".alac" => AudioFileFormat.Mp4,
            ".wma" => AudioFileFormat.Wma,
            _ => AudioFileFormat.Unknown,
        };

    /// <summary>Wording shown in the queue when a file cannot be played.</summary>
    public static string DescribeUnsupported(AudioFileFormat format) => format switch
    {
        AudioFileFormat.Mp4 => "Formaty AAC, M4A i ALAC obsługiwane są przez kodeki systemowe, " +
                               "których w tym systemie nie udało się odnaleźć.",
        AudioFileFormat.Wma => "Format WMA jest poza zakresem odtwarzacza.",
        AudioFileFormat.Unknown => "Nierozpoznany format pliku.",
        _ => "Formatu nie udało się odtworzyć.",
    };
}
