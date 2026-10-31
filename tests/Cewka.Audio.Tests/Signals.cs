using Cewka.Audio.Dsp;

namespace Cewka.Audio.Tests;

/// <summary>Sygnały testowe i miary wspólne dla testów przetwarzania sygnału.</summary>
internal static class Signals
{
    public const int Channels = 2;

    public static float[] Sine(double frequency, double amplitude, int frames, int sampleRate,
        long startFrame = 0)
    {
        var buffer = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (frame + startFrame) / sampleRate));
            buffer[frame * Channels] = value;
            buffer[frame * Channels + 1] = value;
        }

        return buffer;
    }

    /// <summary>Ton w samym sygnale boku: lewy kanał w fazie, prawy w przeciwfazie.</summary>
    public static float[] SideOnly(double frequency, double amplitude, int frames, int sampleRate)
    {
        var buffer = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
            buffer[frame * Channels] = value;
            buffer[frame * Channels + 1] = -value;
        }

        return buffer;
    }

    /// <summary>Amplituda składowej o zadanej częstotliwości w jednym kanale, algorytmem Goertzela.</summary>
    public static double Goertzel(float[] buffer, int channel, double frequency, int sampleRate,
        int skipFrames, int? endFrame = null)
    {
        var frames = endFrame ?? buffer.Length / Channels;
        var count = frames - skipFrames;
        if (count <= 0) return 0;

        var omega = 2 * Math.PI * frequency / sampleRate;
        var cosine = Math.Cos(omega);
        var coefficient = 2 * cosine;

        double s1 = 0, s2 = 0;
        for (var frame = skipFrames; frame < frames; frame++)
        {
            var s0 = buffer[frame * Channels + channel] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        var real = s1 - s2 * cosine;
        var imaginary = s2 * Math.Sin(omega);
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
    }

    public static double Peak(float[] buffer, int skipFrames = 0)
    {
        var peak = 0.0;
        for (var i = skipFrames * Channels; i < buffer.Length; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
        return peak;
    }

    /// <summary>Największa różnica między sąsiednimi próbkami jednego kanału: miara skoku w przebiegu.</summary>
    public static double LargestStep(float[] buffer, int channel)
    {
        var largest = 0.0;
        for (var frame = 1; frame < buffer.Length / Channels; frame++)
        {
            var step = Math.Abs(buffer[frame * Channels + channel] - buffer[(frame - 1) * Channels + channel]);
            largest = Math.Max(largest, step);
        }

        return largest;
    }

    public static bool AllFinite(float[] buffer)
    {
        foreach (var sample in buffer)
            if (!float.IsFinite(sample)) return false;
        return true;
    }

    public static double Db(double ratio) => 20 * Math.Log10(ratio);

    public delegate void BlockProcessor(Span<float> buffer, int frames);

    /// <summary>Przepuszcza sygnał przez stopień kawałkami o zadanej długości, jak wątek dźwięku.</summary>
    public static void ProcessInBlocks(IAudioProcessor processor, float[] buffer, int blockFrames) =>
        ProcessInBlocks(processor.Process, buffer, blockFrames);

    public static void ProcessInBlocks(AudioGraph graph, float[] buffer, int blockFrames) =>
        ProcessInBlocks(graph.Process, buffer, blockFrames);

    public static void ProcessInBlocks(BlockProcessor process, float[] buffer, int blockFrames)
    {
        var frames = buffer.Length / Channels;
        for (var start = 0; start < frames; start += blockFrames)
        {
            var count = Math.Min(blockFrames, frames - start);
            process(buffer.AsSpan(start * Channels, count * Channels), count);
        }
    }
}
