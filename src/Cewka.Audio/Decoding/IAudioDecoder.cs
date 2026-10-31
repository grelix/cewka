namespace Cewka.Audio.Decoding;

/// <summary>
/// Źródło przeplecionych próbek 32-bitowych, już w liczbie kanałów i częstotliwości toru
/// odtwarzania.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Sample rate of the data returned by <see cref="Read"/>.</summary>
    int SampleRate { get; }

    /// <summary>Channel count of the data returned by <see cref="Read"/>.</summary>
    int Channels { get; }

    /// <summary>Length in output frames, or 0 when the source does not report one.</summary>
    long TotalFrames { get; }

    /// <summary>Current position in output frames.</summary>
    long Position { get; }

    bool CanSeek { get; }

    /// <summary>Native sample rate of the file, for display purposes.</summary>
    int SourceSampleRate { get; }

    /// <summary>Native channel count of the file, for display purposes.</summary>
    int SourceChannels { get; }

    /// <summary>
    /// Bitrate of the passage being decoded right now, in kilobits per second. Zero when it
    /// cannot be measured. Only interesting for variable-bitrate files, where the figure in
    /// the tags is an average over the whole track and says nothing about the current moment.
    /// </summary>
    int InstantaneousBitrate { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved samples and returns the number
    /// of whole frames written. A result smaller than the request means end of stream.
    /// </summary>
    int Read(Span<float> destination);

    /// <summary>Moves to the given output frame. Ignored when <see cref="CanSeek"/> is false.</summary>
    void Seek(long frame);
}
