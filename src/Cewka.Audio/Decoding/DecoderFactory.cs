namespace Cewka.Audio.Decoding;

/// <summary>
/// Picks a decoder for a file and hands it back configured for the pipeline's format.
/// </summary>
public static class DecoderFactory
{
    /// <summary>Formats the managed layer can decode on its own, on every system.</summary>
    public static bool IsSupported(AudioFileFormat format) => format switch
    {
        AudioFileFormat.Wav or AudioFileFormat.Mp3 or AudioFileFormat.Flac => true,
        AudioFileFormat.OggVorbis or AudioFileFormat.Opus => true,

        // Zalezne od systemu: Media Foundation w Windows, GStreamer w Linux.
        _ when SystemCodecs.Handles(format) => SystemCodecs.IsAvailable,

        _ => false,
    };

    /// <summary>Explains why a format cannot be played on this machine.</summary>
    public static string DescribeUnsupported(AudioFileFormat format) =>
        SystemCodecs.Handles(format) && !SystemCodecs.IsAvailable
            ? SystemCodecs.UnavailableReason ?? AudioFileFormatDetector.DescribeUnsupported(format)
            : AudioFileFormatDetector.DescribeUnsupported(format);

    /// <summary>
    /// Opens <paramref name="path"/> and converts it to <paramref name="outputSampleRate"/>
    /// and <paramref name="outputChannels"/>.
    /// </summary>
    /// <exception cref="AudioException">The format is out of scope or the file is damaged.</exception>
    public static IAudioDecoder Open(string path, int outputSampleRate, int outputChannels)
    {
        var format = AudioFileFormatDetector.Detect(path);
        if (!IsSupported(format)) throw new AudioException(DescribeUnsupported(format));

        // Kodeki systemowe czytaja plik po sciezce, wiec omijaja strumien zliczajacy;
        // dla nich chwilowa przeplywnosc nie jest mierzona.
        if (SystemCodecs.Handles(format))
            return SystemCodecs.Open(path, outputSampleRate, outputChannels, null);

        // Buffered because the decoders read in small pieces and every one of them would
        // otherwise become a separate call into the operating system. The counting wrapper
        // sits outside the buffer, so it measures what is actually pulled from disk per
        // second of audio - which is the definition of the instantaneous bitrate.
        var meter = new BitrateMeter();
        var stream = new CountingStream(
            new BufferedStream(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                64 * 1024),
            meter,
            ownsInner: true);

        try
        {
            return Open(stream, format, ownsStream: true, outputSampleRate, outputChannels, meter);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static IAudioDecoder Open(
        Stream stream, AudioFileFormat format, bool ownsStream, int outputSampleRate, int outputChannels,
        BitrateMeter? meter = null)
        => format switch
        {
            AudioFileFormat.Wav or AudioFileFormat.Mp3 or AudioFileFormat.Flac =>
                new MiniaudioDecoder(stream, ownsStream, outputSampleRate, outputChannels, meter),

            AudioFileFormat.OggVorbis =>
                new VorbisDecoder(stream, ownsStream, outputSampleRate, outputChannels, meter),

            AudioFileFormat.Opus =>
                new OpusDecoder(stream, ownsStream, outputSampleRate, outputChannels, meter),

            _ => throw new AudioException(AudioFileFormatDetector.DescribeUnsupported(format)),
        };
}
