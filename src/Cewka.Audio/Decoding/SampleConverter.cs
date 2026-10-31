using Cewka.Audio.Interop;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Mapowanie kanałów i zmiana częstotliwości dla dekoderów zarządzanych, resamplerem
/// miniaudio, żeby wszystkie ścieżki brzmiały tak samo.
/// </summary>
internal sealed unsafe class SampleConverter : IDisposable
{
    /// <summary>Współczynnik ITU-R BS.775 dla środka i kanałów otoczenia przy miksie do stereo.</summary>
    private const float SurroundLevel = 0.70710678f;

    private readonly int _sourceChannels;
    private readonly int _targetChannels;

    /// <summary>Macierz miksu [wyjście × wejście]; null przy kopiowaniu i powielaniu mono.</summary>
    private readonly float[]? _matrix;

    private nint _resampler;
    private float[] _mixed = [];
    private bool _disposed;

    /// <param name="filterOrder">
    /// Rząd filtru dolnoprzepustowego resamplera; wartość pochodzi z ustawień użytkownika
    /// przez <see cref="AudioQuality.ResamplerFilterOrder"/>.
    /// </param>
    /// <param name="sourceLayout">
    /// Położenia kanałów źródła; bez niego przyjmowana jest domyślna kolejność WAVE.
    /// </param>
    public SampleConverter(
        int sourceChannels, int sourceRate, int targetChannels, int targetRate,
        int filterOrder = AudioQuality.DefaultFilterOrder, IReadOnlyList<Speaker>? sourceLayout = null)
    {
        _sourceChannels = sourceChannels;
        _targetChannels = targetChannels;

        var layout = sourceLayout is { } given && given.Count == sourceChannels
            ? given
            : ChannelLayouts.Wave(sourceChannels);

        if (sourceChannels > 1 && sourceChannels != targetChannels)
            _matrix = BuildMatrix(layout, targetChannels);
        else if (sourceChannels > 2 && sourceChannels == targetChannels)
            _matrix = BuildReorder(layout);

        SourceRate = sourceRate;
        TargetRate = targetRate;
        FilterOrder = Math.Clamp(filterOrder, 0, AudioQuality.MaximumFilterOrder);

        if (sourceRate != targetRate)
        {
            NativeAudio.EnsureCompatible();
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

    /// <summary>Mono is duplicated across every output; anything else goes through the mix matrix.</summary>
    private ReadOnlySpan<float> MapChannels(ReadOnlySpan<float> source, int frames)
    {
        if (_matrix is null && _sourceChannels == _targetChannels)
            return source[..(frames * _targetChannels)];

        var needed = frames * _targetChannels;
        if (_mixed.Length < needed) _mixed = new float[needed];

        var destination = _mixed.AsSpan(0, needed);

        if (_matrix is null)
        {
            for (var i = 0; i < frames; i++)
            {
                var value = source[i];
                for (var c = 0; c < _targetChannels; c++) destination[i * _targetChannels + c] = value;
            }

            return destination;
        }

        for (var i = 0; i < frames; i++)
        {
            var input = source.Slice(i * _sourceChannels, _sourceChannels);

            for (var c = 0; c < _targetChannels; c++)
            {
                var row = _matrix.AsSpan(c * _sourceChannels, _sourceChannels);
                var sum = 0f;
                for (var s = 0; s < _sourceChannels; s++) sum += row[s] * input[s];
                destination[i * _targetChannels + c] = sum;
            }
        }

        return destination;
    }

    /// <summary>
    /// Do stereo i mono: współczynniki ITU-R BS.775 (środek i otoczenie po 0,707, LFE pominięty),
    /// zmniejszone tak, żeby pełna skala we wszystkich kanałach nie przesterowała wyjścia.
    /// Kanał o nieznanym położeniu wymusza zwykłe uśrednienie wszystkich. Szersze cele
    /// (rzadkie urządzenia wielokanałowe) dostają kanały o tych samych numerach, a brakujące
    /// wypełnia średnia.
    /// </summary>
    private static float[] BuildMatrix(IReadOnlyList<Speaker> layout, int targetChannels)
    {
        var sources = layout.Count;
        var matrix = new float[targetChannels * sources];

        if (targetChannels > 2)
        {
            for (var c = 0; c < targetChannels; c++)
            {
                if (c < sources)
                    matrix[c * sources + c] = 1f;
                else
                    for (var s = 0; s < sources; s++) matrix[c * sources + s] = 1f / sources;
            }

            return matrix;
        }

        var left = new float[sources];
        var right = new float[sources];

        if (layout.Contains(Speaker.Other))
        {
            Array.Fill(left, 1f / sources);
            Array.Fill(right, 1f / sources);
        }
        else
        {
            for (var s = 0; s < sources; s++)
                (left[s], right[s]) = StereoWeights(layout[s]);

            var loudest = Math.Max(left.Sum(), right.Sum());
            if (loudest > 1f)
            {
                for (var s = 0; s < sources; s++)
                {
                    left[s] /= loudest;
                    right[s] /= loudest;
                }
            }
        }

        for (var s = 0; s < sources; s++)
        {
            if (targetChannels == 1)
            {
                matrix[s] = (left[s] + right[s]) / 2f;
            }
            else
            {
                matrix[s] = left[s];
                matrix[sources + s] = right[s];
            }
        }

        return matrix;
    }

    /// <summary>
    /// Przy tej samej liczbie kanałów urządzenie dostaje je w kolejności WAVE, więc źródło
    /// o innej kolejności (Vorbis, Opus) trzeba przestawić. Kanały boczne i tylne mogą się
    /// zastępować, bo urządzenie 5.1 bywa w obu wariantach. Null, gdy nic nie trzeba
    /// przestawiać albo któregoś głośnika nie da się dopasować.
    /// </summary>
    private static float[]? BuildReorder(IReadOnlyList<Speaker> layout)
    {
        var channels = layout.Count;
        var target = ChannelLayouts.Wave(channels);
        var taken = new bool[channels];
        var matrix = new float[channels * channels];
        var identity = true;

        for (var s = 0; s < channels; s++)
        {
            var t = FreeIndex(target, taken, layout[s]);
            if (t < 0) t = FreeIndex(target, taken, Counterpart(layout[s]));
            if (t < 0) return null;

            taken[t] = true;
            matrix[t * channels + s] = 1f;
            identity &= t == s;
        }

        return identity ? null : matrix;
    }

    private static int FreeIndex(Speaker[] layout, bool[] taken, Speaker speaker)
    {
        if (speaker == Speaker.Other) return -1;

        for (var i = 0; i < layout.Length; i++)
            if (!taken[i] && layout[i] == speaker) return i;

        return -1;
    }

    private static Speaker Counterpart(Speaker speaker) => speaker switch
    {
        Speaker.SideLeft => Speaker.BackLeft,
        Speaker.SideRight => Speaker.BackRight,
        Speaker.BackLeft => Speaker.SideLeft,
        Speaker.BackRight => Speaker.SideRight,
        _ => Speaker.Other,
    };

    private static (float Left, float Right) StereoWeights(Speaker speaker) => speaker switch
    {
        Speaker.FrontLeft or Speaker.FrontLeftOfCenter => (1f, 0f),
        Speaker.FrontRight or Speaker.FrontRightOfCenter => (0f, 1f),
        Speaker.FrontCenter => (SurroundLevel, SurroundLevel),
        Speaker.BackLeft or Speaker.SideLeft => (SurroundLevel, 0f),
        Speaker.BackRight or Speaker.SideRight => (0f, SurroundLevel),
        Speaker.BackCenter => (0.5f, 0.5f),
        _ => (0f, 0f),
    };

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
