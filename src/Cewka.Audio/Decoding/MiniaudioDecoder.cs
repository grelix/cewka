using System.Runtime.InteropServices;
using Cewka.Audio.Interop;

namespace Cewka.Audio.Decoding;

/// <summary>
/// MP3, FLAC i WAV przez dekodery dr_libs z miniaudio, razem z jego konwersją kanałów
/// i resamplerem.
/// <para>
/// Plik czytany jest przez zarządzany <see cref="Stream"/>, bo funkcje plikowe miniaudio
/// przyjmują wąskie <c>char*</c> i psułyby ścieżki z Unicode.
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

        try
        {
            NativeAudio.EnsureCompatible();
        }
        catch
        {
            if (_ownsStream) _stream.Dispose();
            throw;
        }

        _self = GCHandle.Alloc(this);

        var result = NativeAudio.DecoderOpen(
            &OnRead, &OnSeek, &OnTell, (void*)GCHandle.ToIntPtr(_self),
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

        // Długość przychodzi już w ramkach wyjściowych, bo miniaudio uwzględnia własną
        // konwersję częstotliwości; nie wolno jej przeliczać drugi raz.
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

        // Pułapka: przewijanie liczone jest w ramkach wyjściowych, jak długość, bo miniaudio
        // samo uwzględnia konwersję częstotliwości. Ramki źródłowe wysłałyby plik 96 kHz na
        // urządzeniu 48 kHz dwa razy za daleko. Po nieudanym przewinięciu podawana jest pozycja,
        // na której dekoder faktycznie stoi.
        _position = NativeAudio.DecoderSeek(_handle, (ulong)Math.Max(0, frame)) == NativeAudio.Success
            ? frame
            : (long)NativeAudio.DecoderCursor(_handle);

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

            var seekOrigin = origin switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => (SeekOrigin?)null,
            };
            if (seekOrigin is null) return 0;

            self._stream.Seek(offset, seekOrigin.Value);
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
            var self = (MiniaudioDecoder?)GCHandle.FromIntPtr((nint)user).Target;
            if (self is null || !self._stream.CanSeek) return -1;

            return what == 0 ? self._stream.Position : self._stream.Length;
        }
        catch
        {
            return -1;
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
