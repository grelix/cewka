using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Cewka.Audio.Interop;

namespace Cewka.Audio.Decoding;

/// <summary>
/// MP3, FLAC i WAV przez dekodery dr_libs z miniaudio. Miniaudio oddaje próbki w formacie
/// pliku, a kanały i częstotliwość zmienia ta sama konwersja co w pozostałych dekoderach.
/// <para>
/// Plik czytany jest przez zarządzany <see cref="Stream"/>, bo funkcje plikowe miniaudio
/// przyjmują wąskie <c>char*</c> i psułyby ścieżki z Unicode.
/// </para>
/// </summary>
internal sealed unsafe class MiniaudioDecoder : ManagedDecoderBase
{
    /// <summary>Tyle bajtów początku pliku WAV przegląda się w poszukiwaniu maski kanałów.</summary>
    private const int MaxHeaderScan = 64 * 1024;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private GCHandle _self;
    private nint _handle;
    private bool _disposed;

    public MiniaudioDecoder(
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

        // Maskę trzeba przeczytać przed miniaudio, bo potem strumień stoi w środku danych.
        var mask = ReadWaveMask(stream);

        _self = GCHandle.Alloc(this);

        // Zera: format pliku bez wbudowanej konwersji miniaudio.
        var result = NativeAudio.DecoderOpen(
            &OnRead, &OnSeek, &OnTell, (void*)GCHandle.ToIntPtr(_self), 0, 0, 0,
            out _handle, out var lengthFrames, out var sourceRate, out var sourceChannels);

        if (result != NativeAudio.Success)
        {
            _self.Free();
            if (_ownsStream) _stream.Dispose();
            DecoderFailures.Log($"miniaudio: {NativeAudio.Describe(result)} (kod {result})");
            throw DecoderFailures.Create(DecoderFailure.DamagedFile);
        }

        try
        {
            if (sourceRate == 0 || sourceChannels == 0)
                throw DecoderFailures.Create(DecoderFailure.DamagedFile);

            var channels = (int)sourceChannels;
            var layout = mask is { } given ? ChannelLayouts.FromWaveMask(given, channels) : ChannelLayouts.Wave(channels);

            Configure((int)sourceRate, channels, outputSampleRate, outputChannels, meter, layout);

            // Długość z miniaudio jest w ramkach pliku; tu w ramkach wyjściowych, jak pozycja.
            TotalFrames = (int)sourceRate == outputSampleRate
                ? (long)lengthFrames
                : Dsp.SincResampler.OutputFrames((long)lengthFrames, (int)sourceRate, outputSampleRate);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public override bool CanSeek => _stream.CanSeek;

    protected override int ReadSource(Span<float> destination, int frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        fixed (float* output = destination)
            return (int)NativeAudio.DecoderRead(_handle, output, (ulong)frames);
    }

    protected override void SeekSource(long sourceFrame) => SeekSourceTo(sourceFrame);

    /// <summary>
    /// Po nieudanym przewinięciu miniaudio stoi gdzie indziej niż cel; podawane jest miejsce,
    /// w którym dekoder faktycznie stoi.
    /// </summary>
    protected override long SeekSourceTo(long sourceFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return NativeAudio.DecoderSeek(_handle, (ulong)Math.Max(0, sourceFrame)) == NativeAudio.Success
            ? sourceFrame
            : (long)NativeAudio.DecoderCursor(_handle);
    }

    /// <summary>
    /// Maska kanałów z nagłówka WAVE_FORMAT_EXTENSIBLE (RIFF, RF64, BW64). Bez niej plik
    /// wielokanałowy ma domyślną kolejność WAVE, tę samą co FLAC. Null dla innych formatów.
    /// </summary>
    private static uint? ReadWaveMask(Stream stream)
    {
        if (!stream.CanSeek) return null;

        var start = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[12];
            if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12) return null;

            var riff = header[..4];
            if (!riff.SequenceEqual("RIFF"u8) && !riff.SequenceEqual("RF64"u8) && !riff.SequenceEqual("BW64"u8)) return null;
            if (!header[8..].SequenceEqual("WAVE"u8)) return null;

            Span<byte> chunk = stackalloc byte[8];
            Span<byte> format = stackalloc byte[24];

            while (stream.Position - start < MaxHeaderScan)
            {
                if (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) < 8) return null;

                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);

                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (size < 24 || stream.ReadAtLeast(format, 24, throwOnEndOfStream: false) < 24) return null;

                    // WAVE_FORMAT_EXTENSIBLE; maska leży po polach cbSize i wValidBitsPerSample.
                    return BinaryPrimitives.ReadUInt16LittleEndian(format) == 0xFFFE && size >= 40
                        ? BinaryPrimitives.ReadUInt32LittleEndian(format[20..])
                        : null;
                }

                // Fragmenty mają parzystą długość; dopełnienie nie wlicza się do rozmiaru.
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            stream.Position = start;
        }
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

    protected override void DisposeCore()
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
