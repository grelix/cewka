using Cewka.Audio.Interop;

namespace Cewka.Audio.Decoding.Live;

/// <summary>MP3 ramka po ramce przez dekoder dr_mp3 pchany z warstwy zarządzanej.</summary>
internal sealed unsafe class Mp3FrameDecoder : ILiveFrameDecoder
{
    private nint _handle;

    public Mp3FrameDecoder()
    {
        NativeAudio.EnsureCompatible();
        NativeAudio.ThrowIfFailed(NativeAudio.Mp3StreamOpen(out _handle), "Otwarcie dekodera MP3");
    }

    /// <summary>
    /// Pułapka: krótsza porcja zostaje zjedzona jako śmieci i zeruje stan dekodera. 6000 B to ponad
    /// dwie ramki wolnego formatu po 2304 B i nagłówek następnej.
    /// </summary>
    public int MinInputBytes => 6000;

    public int MaxOutputSamples => 2304;

    public int Decode(ReadOnlySpan<byte> input, Span<float> output, out int consumed, out LiveFrameFormat format)
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);

        int frames, sampleRate, channels, bitrate;

        fixed (byte* inputPointer = input)
        fixed (float* outputPointer = output)
        {
            frames = NativeAudio.Mp3StreamDecode(
                _handle, inputPointer, input.Length, outputPointer, output.Length,
                out consumed, out sampleRate, out channels, out bitrate);
        }

        if (frames < 0) throw new AudioException("Dekoder MP3 odrzucił argumenty.");

        format = new LiveFrameFormat(sampleRate, channels, bitrate);
        return frames;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
        NativeAudio.Mp3StreamReset(_handle);
    }

    public void Dispose()
    {
        if (_handle == nint.Zero) return;

        NativeAudio.Mp3StreamClose(_handle);
        _handle = nint.Zero;
    }
}
