using System.Runtime.InteropServices;
using Cewka.Audio.Interop;

namespace Cewka.Audio.Decoding;

/// <summary>
/// MP3, FLAC and WAV, decoded by the dr_libs decoders bundled inside miniaudio, with its
/// channel conversion and resampler doing the format matching.
/// <para>
/// The file itself is read through a managed <see cref="Stream"/> rather than by path.
/// That keeps Unicode paths working identically on both systems — miniaudio's own file
/// helpers take a narrow <c>char*</c> — and avoids loading whole albums into memory.
/// </para>
/// </summary>
internal sealed unsafe class MiniaudioDecoder : IAudioDecoder
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private GCHandle _self;
    private nint _handle;
    private long _position;
    private bool _disposed;

    private readonly BitrateMeter? _meter;

    public MiniaudioDecoder(
        Stream stream, bool ownsStream, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _meter = meter;

        _self = GCHandle.Alloc(this);

        var result = NativeAudio.DecoderOpen(
            &OnRead, &OnSeek, (void*)GCHandle.ToIntPtr(_self),
            (uint)outputSampleRate, (uint)outputChannels, (uint)AudioQuality.ResamplerFilterOrder,
            out _handle, out var lengthFrames, out var sourceRate, out var sourceChannels);

        if (result != NativeAudio.Success)
        {
            _self.Free();
            if (_ownsStream) _stream.Dispose();
            throw new AudioException($"Nie udało się otworzyć dekodera — {NativeAudio.Describe(result)}.");
        }

        SampleRate = outputSampleRate;
        Channels = outputChannels;
        SourceSampleRate = (int)sourceRate;
        SourceChannels = (int)sourceChannels;

        _meter?.Configure(outputSampleRate);

        // Dlugosc przychodzi juz w ramkach wyjsciowych - miniaudio uwzglednia wlasna
        // konwersje czestotliwosci - wiec nie wolno jej przeliczac po raz drugi.
        TotalFrames = (long)lengthFrames;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalFrames { get; }
    public long Position => _position;
    public bool CanSeek => _stream.CanSeek;
    public int SourceSampleRate { get; }
    public int SourceChannels { get; }
    public int InstantaneousBitrate => _meter?.Kilobits ?? 0;

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var frames = destination.Length / Channels;
        if (frames <= 0) return 0;

        fixed (float* output = destination)
        {
            var read = (int)NativeAudio.DecoderRead(_handle, output, (ulong)frames);
            _position += read;
            _meter?.AddFrames(read);
            return read;
        }
    }

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek) return;

        // Przewijanie liczone jest w ramkach wyjsciowych, tak samo jak zwracana dlugosc:
        // miniaudio uwzglednia wlasna konwersje czestotliwosci po swojej stronie. Przeliczanie
        // na ramki zrodlowe wysylaloby plik 96 kHz odtwarzany przez urzadzenie 48 kHz
        // dwukrotnie za daleko, czyli zwykle poza koniec nagrania.
        if (NativeAudio.DecoderSeek(_handle, (ulong)Math.Max(0, frame)) != NativeAudio.Success) return;

        _position = frame;

        // Po przewinięciu związek między pobranymi bajtami a czasem dźwięku przestaje
        // obowiązywać, więc pomiar zaczyna się od nowa.
        _meter?.Reset();
    }

    // ---------- wywołania zwrotne z kodu natywnego ----------

    [UnmanagedCallersOnly]
    private static nuint OnRead(void* user, void* buffer, nuint bytesToRead)
    {
        // An exception must never cross back into native frames, so everything is caught here.
        try
        {
            var self = (MiniaudioDecoder?)GCHandle.FromIntPtr((nint)user).Target;
            if (self is null) return 0;

            var span = new Span<byte>(buffer, checked((int)bytesToRead));
            return (nuint)self._stream.Read(span);
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
            var self = (MiniaudioDecoder?)GCHandle.FromIntPtr((nint)user).Target;
            if (self is null || !self._stream.CanSeek) return 0;

            self._stream.Seek(offset, origin == 0 ? SeekOrigin.Begin : SeekOrigin.Current);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != nint.Zero)
        {
            NativeAudio.DecoderClose(_handle);
            _handle = nint.Zero;
        }

        if (_self.IsAllocated) _self.Free();
        if (_ownsStream) _stream.Dispose();
    }
}
