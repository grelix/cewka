using Cewka.Audio.Interop;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Channel mapping and sample-rate conversion for the managed decoders.
/// <para>
/// miniaudio does this internally for the formats it decodes itself; Vorbis and Opus come
/// out of managed libraries at their native format and need the same treatment applied
/// afterwards. Reusing miniaudio's resampler keeps both paths sounding the same.
/// </para>
/// </summary>
internal sealed unsafe class SampleConverter : IDisposable
{
    private readonly int _sourceChannels;
    private readonly int _targetChannels;
    private nint _resampler;
    private float[] _mixed = [];
    private bool _disposed;

    /// <param name="filterOrder">
    /// Rząd filtru dolnoprzepustowego resamplera; wartość pochodzi z ustawień użytkownika
    /// przez <see cref="AudioQuality.ResamplerFilterOrder"/>.
    /// </param>
    public SampleConverter(
        int sourceChannels, int sourceRate, int targetChannels, int targetRate,
        int filterOrder = AudioQuality.DefaultFilterOrder)
    {
        _sourceChannels = sourceChannels;
        _targetChannels = targetChannels;

        SourceRate = sourceRate;
        TargetRate = targetRate;
        FilterOrder = Math.Clamp(filterOrder, 0, AudioQuality.MaximumFilterOrder);

        if (sourceRate != targetRate)
        {
            NativeAudio.ThrowIfFailed(
                NativeAudio.ResamplerCreate(
                    (uint)targetChannels, (uint)sourceRate, (uint)targetRate, (uint)FilterOrder, out _resampler),
                "Utworzenie resamplera");
        }
    }

    public int SourceRate { get; }
    public int TargetRate { get; }

    /// <summary>Rząd filtru, z jakim resampler powstał. Zerowy oznacza brak filtrowania.</summary>
    public int FilterOrder { get; }

    public bool NeedsResampling => _resampler != nint.Zero;

    /// <summary>How many source frames are needed to produce the given number of output frames.</summary>
    public long RequiredSourceFrames(long outputFrames) =>
        _resampler == nint.Zero
            ? outputFrames
            : (long)NativeAudio.ResamplerRequiredInput(_resampler, (ulong)outputFrames);

    /// <summary>
    /// Maps channels and resamples. Returns the number of output frames written and reports
    /// through <paramref name="sourceFramesUsed"/> how much of the input was consumed.
    /// </summary>
    public int Process(ReadOnlySpan<float> source, int sourceFrames, Span<float> destination, out int sourceFramesUsed)
    {
        ReadOnlySpan<float> mapped = MapChannels(source, sourceFrames);

        if (_resampler == nint.Zero)
        {
            var frames = Math.Min(sourceFrames, destination.Length / _targetChannels);
            mapped[..(frames * _targetChannels)].CopyTo(destination);
            sourceFramesUsed = frames;
            return frames;
        }

        var inFrames = (ulong)sourceFrames;
        var outFrames = (ulong)(destination.Length / _targetChannels);

        fixed (float* input = mapped)
        fixed (float* output = destination)
        {
            NativeAudio.ThrowIfFailed(
                NativeAudio.ResamplerProcess(_resampler, input, ref inFrames, output, ref outFrames),
                "Przetwarzanie resamplera");
        }

        sourceFramesUsed = (int)inFrames;
        return (int)outFrames;
    }

    /// <summary>
    /// Mono is duplicated across both outputs; anything wider than the target is folded down
    /// by averaging. Neither case is common in a music library, but silently dropping a
    /// channel would be worse than a plain average.
    /// </summary>
    private ReadOnlySpan<float> MapChannels(ReadOnlySpan<float> source, int frames)
    {
        if (_sourceChannels == _targetChannels)
            return source[..(frames * _targetChannels)];

        var needed = frames * _targetChannels;
        if (_mixed.Length < needed) _mixed = new float[needed];

        var destination = _mixed.AsSpan(0, needed);

        if (_sourceChannels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                var value = source[i];
                for (var c = 0; c < _targetChannels; c++) destination[i * _targetChannels + c] = value;
            }
        }
        else
        {
            for (var i = 0; i < frames; i++)
            {
                for (var c = 0; c < _targetChannels; c++)
                {
                    // Take the matching channel when it exists, otherwise the average of all.
                    if (c < _sourceChannels)
                    {
                        destination[i * _targetChannels + c] = source[i * _sourceChannels + c];
                    }
                    else
                    {
                        var sum = 0f;
                        for (var s = 0; s < _sourceChannels; s++) sum += source[i * _sourceChannels + s];
                        destination[i * _targetChannels + c] = sum / _sourceChannels;
                    }
                }
            }
        }

        return destination;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_resampler != nint.Zero)
        {
            NativeAudio.ResamplerDestroy(_resampler);
            _resampler = nint.Zero;
        }
    }
}
