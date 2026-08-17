namespace Cewka.Audio.Playback;

/// <summary>
/// A ring of interleaved float frames written by the decode thread and read by the audio
/// thread. Exactly one thread on each side, which is what lets it work without locks: the
/// producer only ever advances <c>_write</c> and the consumer only ever advances <c>_read</c>.
/// </summary>
internal sealed class PcmRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacityFrames;
    private readonly int _channels;

    private long _write;
    private long _read;

    public PcmRingBuffer(int capacityFrames, int channels)
    {
        _capacityFrames = capacityFrames;
        _channels = channels;
        _buffer = new float[capacityFrames * channels];
    }

    public int CapacityFrames => _capacityFrames;

    /// <summary>Total frames ever written. Only the producer advances this.</summary>
    public long TotalWritten => Volatile.Read(ref _write);

    /// <summary>Total frames ever read. Only the consumer advances this.</summary>
    public long TotalRead => Volatile.Read(ref _read);

    public int FramesAvailable => (int)(TotalWritten - TotalRead);

    public int SpaceAvailable => _capacityFrames - FramesAvailable;

    /// <summary>Producer side. Returns how many frames were accepted.</summary>
    public int Write(ReadOnlySpan<float> source, int frames)
    {
        var accepted = Math.Min(frames, SpaceAvailable);
        if (accepted <= 0) return 0;

        var start = (int)(_write % _capacityFrames);
        var firstChunk = Math.Min(accepted, _capacityFrames - start);

        source[..(firstChunk * _channels)].CopyTo(_buffer.AsSpan(start * _channels));

        if (accepted > firstChunk)
        {
            source.Slice(firstChunk * _channels, (accepted - firstChunk) * _channels)
                  .CopyTo(_buffer.AsSpan(0));
        }

        // Publish only after the data is in place.
        Volatile.Write(ref _write, _write + accepted);
        return accepted;
    }

    /// <summary>Consumer side. Returns how many frames were delivered.</summary>
    public int Read(Span<float> destination, int frames)
    {
        var delivered = Math.Min(frames, FramesAvailable);
        if (delivered <= 0) return 0;

        var start = (int)(_read % _capacityFrames);
        var firstChunk = Math.Min(delivered, _capacityFrames - start);

        _buffer.AsSpan(start * _channels, firstChunk * _channels).CopyTo(destination);

        if (delivered > firstChunk)
        {
            _buffer.AsSpan(0, (delivered - firstChunk) * _channels)
                   .CopyTo(destination[(firstChunk * _channels)..]);
        }

        Volatile.Write(ref _read, _read + delivered);
        return delivered;
    }

    /// <summary>
    /// Drops everything pending. Consumer-side operation — calling it while the audio
    /// thread is running is only safe from inside that same thread.
    /// </summary>
    public void DiscardAll() => Volatile.Write(ref _read, TotalWritten);
}
