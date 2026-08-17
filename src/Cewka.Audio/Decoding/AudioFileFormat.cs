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

    /// <summary>MP4 family: .m4a, .aac, .alac. Handled by system codecs in stage 5.</summary>
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

            // OggS — the codec inside is decided further in; the extension settles it.
            if (header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S')
                return DetectInsideOgg(stream, path);

            // ID3 tag, or an MPEG frame sync.
            if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
                return AudioFileFormat.Mp3;

            if (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
                return AudioFileFormat.Mp3;
        }

        if (read >= 16 && header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75)
            return AudioFileFormat.Wma;

        return FromExtension(path);
    }

    /// <summary>
    /// Both Vorbis and Opus travel inside Ogg. The codec identifier sits in the first
    /// packet, right after the 27-byte page header plus the segment table.
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

            return FromExtension(path);
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
