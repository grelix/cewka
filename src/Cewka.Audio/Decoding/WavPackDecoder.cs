using System.Runtime.InteropServices;
using Cewka.Audio.Interop;

namespace Cewka.Audio.Decoding;

/// <summary>
/// WavPack (wersje 4 i 5, PCM całkowity i zmiennoprzecinkowy) przez bibliotekę WavPack
/// wbudowaną w nakładkę natywną. Kanały i częstotliwość zmienia ta sama konwersja co
/// w pozostałych dekoderach; kolejność kanałów opisuje maska WAVE zapisana w pliku.
/// <para>
/// Plik czytany jest przez zarządzany <see cref="Stream"/>, jak w <see cref="MiniaudioDecoder"/>.
/// Plik korekcyjny <c>.wvc</c> jest pomijany, DSD i pliki sprzed wersji 4 są nieobsługiwane.
/// </para>
/// </summary>
internal sealed unsafe class WavPackDecoder : ManagedDecoderBase
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private GCHandle _streamHandle;
    private nint _handle;
    private bool _disposed;

    public WavPackDecoder(
        Stream stream, bool ownsStream, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;

        try
        {
            NativeAudio.EnsureCompatible();
        }
        catch
        {
            if (_ownsStream) _stream.Dispose();
            throw;
        }

        _streamHandle = GCHandle.Alloc(stream);

        // Pusty napis na wypadek, gdyby biblioteka odmówiła przed zapisaniem opisu.
        var error = stackalloc byte[NativeAudio.WavPackErrorCapacity];
        error[0] = 0;
        var result = NativeAudio.WavPackOpen(
            &OnRead, &OnSeek, &OnTell, (void*)GCHandle.ToIntPtr(_streamHandle), error, NativeAudio.WavPackErrorCapacity,
            out _handle, out var sourceRate, out var sourceChannels, out var channelMask,
            out var bits, out var isFloat, out var totalFrames);

        if (result != NativeAudio.WavPackOk)
        {
            _streamHandle.Free();
            if (_ownsStream) _stream.Dispose();

            DecoderFailures.Log($"WavPack: {Marshal.PtrToStringUTF8((nint)error)} (kod {result})");

            throw DecoderFailures.Create(result switch
            {
                NativeAudio.WavPackUnsupported => DecoderFailure.Unspecified,
                NativeAudio.WavPackChannels => DecoderFailure.UnsupportedChannelLayout,
                _ => DecoderFailure.DamagedFile,
            });
        }

        try
        {
            var channels = (int)sourceChannels;
            var rate = (int)sourceRate;

            BitsPerSample = (int)bits;
            IsFloat = isFloat != 0;

            Configure(rate, channels, outputSampleRate, outputChannels, meter, ChannelLayouts.FromWaveMask(channelMask, channels));

            // Długość w ramkach wyjściowych, jak pozycja; plik bez długości podaje −1.
            TotalFrames = totalFrames <= 0
                ? 0
                : rate == outputSampleRate ? totalFrames : Dsp.SincResampler.OutputFrames(totalFrames, rate, outputSampleRate);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Głębia bitowa zapisana w pliku.</summary>
    internal int BitsPerSample { get; }

    /// <summary>Próbki zmiennoprzecinkowe.</summary>
    internal bool IsFloat { get; }

    public override bool CanSeek => _stream.CanSeek;

    protected override int ReadSource(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        frames = Math.Min(frames, destination.Length / SourceChannels);

        fixed (float* output = destination)
            return Math.Max(0, NativeAudio.WavPackRead(_handle, output, frames));
    }

    protected override void SeekSource(long sourceFrame) => SeekSourceTo(sourceFrame);

    /// <summary>
    /// Cel za końcem pliku stawia dekoder na końcu. Po nieudanym przewinięciu biblioteka milknie;
    /// w obu razach podawane jest miejsce, w którym dekoder faktycznie stoi.
    /// </summary>
    protected override long SeekSourceTo(long sourceFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        NativeAudio.WavPackSeek(_handle, Math.Max(0, sourceFrame));
        return NativeAudio.WavPackPosition(_handle);
    }

    // ---------- wywołania zwrotne z kodu natywnego ----------

    private static Stream? StreamOf(void* user) => GCHandle.FromIntPtr((nint)user).Target as Stream;

    [UnmanagedCallersOnly]
    private static nuint OnRead(void* user, void* buffer, nuint bytesToRead)
    {
        // Wyjątek nie może przejść przez ramki kodu natywnego.
        try
        {
            if (StreamOf(user) is not { } stream) return 0;
            return (nuint)stream.Read(new Span<byte>(buffer, checked((int)bytesToRead)));
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnSeek(void* user, long offset, int origin)
    {
        try
        {
            if (StreamOf(user) is not { CanSeek: true } stream) return 0;

            SeekOrigin? seekOrigin = origin switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => null,
            };
            if (seekOrigin is null) return 0;

            stream.Seek(offset, seekOrigin.Value);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Położenie (<paramref name="what"/> = 0) albo długość strumienia (1); −1, gdy nieznane.</summary>
    [UnmanagedCallersOnly]
    private static long OnTell(void* user, int what)
    {
        try
        {
            if (StreamOf(user) is not { CanSeek: true } stream) return -1;
            return what == 0 ? stream.Position : stream.Length;
        }
        catch
        {
            return -1;
        }
    }

    protected override void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != nint.Zero)
        {
            NativeAudio.WavPackClose(_handle);
            _handle = nint.Zero;
        }

        if (_streamHandle.IsAllocated) _streamHandle.Free();
        if (_ownsStream) _stream.Dispose();
    }
}
