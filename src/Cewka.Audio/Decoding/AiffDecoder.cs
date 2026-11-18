using System.Buffers.Binary;

namespace Cewka.Audio.Decoding;

/// <summary>
/// AIFF i AIFF-C bez kompresji: liczby całkowite 8–32 bity w obu kolejnościach bajtów oraz
/// liczby zmiennoprzecinkowe 32 i 64 bity. Dane leżą w pliku wprost, więc dekoder tylko je
/// przelicza; przewijanie to ustawienie strumienia.
/// </summary>
internal sealed class AiffDecoder : ManagedDecoderBase
{
    /// <summary>Plik od kodera ma kilka fragmentów; tysiące to plik spreparowany.</summary>
    private const int MaxChunks = 4096;

    /// <summary>Bufor źródła rośnie z liczbą kanałów, więc ta musi mieć granicę.</summary>
    internal const int MaxChannels = 32;

    /// <summary>
    /// Poza tym zakresem częstotliwość to uszkodzony nagłówek. Dolna granica pilnuje też, żeby kilka
    /// kilobajtów danych nie rozwinęło się w godziny dźwięku po konwersji.
    /// </summary>
    private const double MinRate = 1000, MaxRate = 768_000;

    private enum Encoding { BigEndian, LittleEndian, Float32, Float64 }

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Encoding _encoding;
    private readonly int _sampleBytes;
    private readonly int _frameBytes;
    private readonly long _dataStart;
    private readonly long _totalSourceFrames;

    private long _remaining;
    private byte[] _raw = [];

    public AiffDecoder(Stream stream, bool ownsStream, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;

        try
        {
            var header = ReadHeader(stream);

            _encoding = header.Encoding;
            _sampleBytes = header.SampleBytes;
            _frameBytes = header.SampleBytes * header.Channels;
            _dataStart = header.DataStart;

            var available = Math.Max(0, Math.Min(header.DataBytes, stream.Length - header.DataStart));
            _totalSourceFrames = Math.Min(header.Frames, available / _frameBytes);
            _remaining = _totalSourceFrames;

            stream.Position = _dataStart;

            Configure(header.Rate, header.Channels, outputSampleRate, outputChannels, meter,
                ChannelLayouts.Wave(header.Channels));

            TotalFrames = header.Rate == outputSampleRate
                ? _totalSourceFrames
                : Dsp.SincResampler.OutputFrames(_totalSourceFrames, header.Rate, outputSampleRate);
        }
        catch (Exception ex) when (ex is not AudioException and not OutOfMemoryException)
        {
            if (_ownsStream) _stream.Dispose();
            DecoderFailures.Log($"nagłówek AIFF nieczytelny: {ex.Message}");
            throw DecoderFailures.Create(DecoderFailure.DamagedFile, ex);
        }
        catch
        {
            if (_ownsStream) _stream.Dispose();
            throw;
        }
    }

    public override bool CanSeek => _stream.CanSeek;

    private readonly record struct Header(
        int Channels, long Frames, int Rate, Encoding Encoding, int SampleBytes, long DataStart, long DataBytes);

    /// <summary>
    /// Przechodzi fragmenty pliku do chwili, gdy zna oba potrzebne: opis formatu (<c>COMM</c>)
    /// i dane (<c>SSND</c>). Kolejność fragmentów jest dowolna, a pozostałe są pomijane bez czytania.
    /// </summary>
    private static Header ReadHeader(Stream stream)
    {
        if (!stream.CanSeek) throw new InvalidDataException("AIFF wymaga strumienia z przewijaniem.");

        Span<byte> head = stackalloc byte[12];
        stream.ReadExactly(head);

        if (!head[..4].SequenceEqual("FORM"u8)) throw DecoderFailures.Create(DecoderFailure.UnrecognisedFormat);

        var compressed = head[8..12].SequenceEqual("AIFC"u8);
        if (!compressed && !head[8..12].SequenceEqual("AIFF"u8)) throw DecoderFailures.Create(DecoderFailure.UnrecognisedFormat);

        var end = Math.Min(stream.Length, 8L + BinaryPrimitives.ReadUInt32BigEndian(head[4..]));

        Span<byte> chunk = stackalloc byte[8];
        Span<byte> comm = stackalloc byte[26];
        Span<byte> offsets = stackalloc byte[8];
        Header? format = null;
        long dataStart = -1, dataBytes = 0;
        long position = 12;

        for (var count = 0; count < MaxChunks && position + 8 <= end; count++)
        {
            stream.Position = position;
            stream.ReadExactly(chunk);

            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
            var body = position + 8;

            if (chunk[..4].SequenceEqual("COMM"u8))
            {
                var wanted = (int)Math.Min(size, comm.Length);
                if (wanted < 18) throw new InvalidDataException("fragment COMM za krótki");

                stream.ReadExactly(comm[..wanted]);
                format = ParseFormat(comm[..wanted], compressed);
            }
            else if (chunk[..4].SequenceEqual("SSND"u8))
            {
                if (size < 8) throw new InvalidDataException("fragment SSND za krótki");

                stream.ReadExactly(offsets);

                var offset = (long)BinaryPrimitives.ReadUInt32BigEndian(offsets);
                dataStart = body + 8 + offset;
                dataBytes = Math.Max(0, size - 8 - offset);
            }

            if (format is not null && dataStart >= 0) return format.Value with { DataStart = dataStart, DataBytes = dataBytes };

            // Fragmenty mają parzystą długość; dopełnienie nie wlicza się do rozmiaru.
            position = body + size + (size & 1);
        }

        throw new InvalidDataException("brak fragmentu COMM albo SSND");
    }

    private static Header ParseFormat(ReadOnlySpan<byte> comm, bool compressed)
    {
        var channels = BinaryPrimitives.ReadInt16BigEndian(comm);
        var frames = (long)BinaryPrimitives.ReadUInt32BigEndian(comm[2..]);
        var bits = BinaryPrimitives.ReadInt16BigEndian(comm[6..]);
        var rate = ReadExtended(comm[8..18]);

        if (channels is < 1 or > MaxChannels) throw new InvalidDataException($"liczba kanałów {channels}");
        if (!(rate >= MinRate && rate <= MaxRate)) throw new InvalidDataException($"częstotliwość {rate}");

        var compression = compressed
            ? comm.Length >= 22 ? comm[18..22] : throw new InvalidDataException("brak rodzaju kompresji")
            : "NONE"u8;

        var (encoding, bytes) = Describe(compression, bits)
            ?? throw DecoderFailures.Create(DecoderFailure.UnrecognisedFormat);

        return new Header(channels, frames, (int)Math.Round(rate), encoding, bytes, 0, 0);
    }

    /// <summary>Sposób zapisu próbek i ich szerokość w bajtach; null dla kompresji, której dekoder nie zna.</summary>
    private static (Encoding Encoding, int Bytes)? Describe(ReadOnlySpan<byte> compression, int bits)
    {
        // Próbka zajmuje pełne bajty i jest wyrównana do lewej, więc 20 bitów leży w trzech bajtach.
        var whole = bits is >= 1 and <= 32 ? (bits + 7) / 8 : 0;

        if (compression.SequenceEqual("NONE"u8) || compression.SequenceEqual("twos"u8))
            return whole > 0 ? (Encoding.BigEndian, whole) : throw new InvalidDataException($"głębia {bits}");
        if (compression.SequenceEqual("sowt"u8))
            return whole > 0 ? (Encoding.LittleEndian, whole) : throw new InvalidDataException($"głębia {bits}");
        if (compression.SequenceEqual("in24"u8)) return (Encoding.BigEndian, 3);
        if (compression.SequenceEqual("in32"u8)) return (Encoding.BigEndian, 4);
        if (compression.SequenceEqual("fl32"u8) || compression.SequenceEqual("FL32"u8)) return (Encoding.Float32, 4);
        if (compression.SequenceEqual("fl64"u8) || compression.SequenceEqual("FL64"u8)) return (Encoding.Float64, 8);

        return null;
    }

    /// <summary>
    /// Czy kompresję pliku AIFF-C da się zdekodować. Fragmenty przegląda się tak samo jak przy
    /// otwieraniu; plik, w którym opisu formatu nie widać, rozstrzyga dopiero otwarcie.
    /// </summary>
    internal static bool IsDecodable(Stream stream)
    {
        if (!stream.CanSeek) return true;

        var origin = stream.Position;
        try
        {
            Span<byte> head = stackalloc byte[12];
            if (stream.ReadAtLeast(head, 12, throwOnEndOfStream: false) < 12) return true;
            if (!head[8..12].SequenceEqual("AIFC"u8)) return true;

            Span<byte> chunk = stackalloc byte[8];
            Span<byte> comm = stackalloc byte[22];
            var position = origin + 12;

            for (var count = 0; count < 64; count++)
            {
                stream.Position = position;
                if (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) < 8) return true;

                var size = (long)BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);

                if (chunk[..4].SequenceEqual("COMM"u8))
                {
                    if (size < 22 || stream.ReadAtLeast(comm, 22, throwOnEndOfStream: false) < 22) return true;

                    var compression = comm[18..22];
                    return compression.SequenceEqual("NONE"u8) || compression.SequenceEqual("twos"u8)
                        || compression.SequenceEqual("sowt"u8) || compression.SequenceEqual("in24"u8)
                        || compression.SequenceEqual("in32"u8) || compression.SequenceEqual("fl32"u8)
                        || compression.SequenceEqual("FL32"u8) || compression.SequenceEqual("fl64"u8)
                        || compression.SequenceEqual("FL64"u8);
                }

                position += 8 + size + (size & 1);
            }

            return true;
        }
        catch (IOException)
        {
            return true;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>
    /// Liczba zmiennoprzecinkowa 80-bitowa (IEEE 754 rozszerzona): znak i wykładnik z przesunięciem
    /// 16383 w dwóch bajtach, potem 64-bitowa mantysa z jawnym bitem całości.
    /// </summary>
    internal static double ReadExtended(ReadOnlySpan<byte> bytes)
    {
        var exponent = ((bytes[0] & 0x7F) << 8) | bytes[1];
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);

        if (exponent == 0x7FFF) return double.NaN;
        if (mantissa == 0) return 0;

        var value = Math.ScaleB(mantissa, exponent - 16383 - 63);
        return (bytes[0] & 0x80) != 0 ? -value : value;
    }

    protected override int ReadSource(Span<float> destination, int frames)
    {
        var wanted = (int)Math.Min(frames, _remaining);
        if (wanted <= 0) return 0;

        var bytes = wanted * _frameBytes;
        if (_raw.Length < bytes) _raw = new byte[bytes];

        var got = _stream.ReadAtLeast(_raw.AsSpan(0, bytes), bytes, throwOnEndOfStream: false);
        var whole = got / _frameBytes;

        // Plik urwany w połowie danych: to, co przyszło, gra, a dalej jest koniec.
        _remaining = got < bytes ? 0 : _remaining - whole;

        Convert(_raw.AsSpan(0, whole * _frameBytes), destination[..(whole * SourceChannels)]);
        return whole;
    }

    private void Convert(ReadOnlySpan<byte> raw, Span<float> samples)
    {
        const float Scale = 1f / 2147483648f;
        var width = _sampleBytes;

        for (int i = 0, at = 0; i < samples.Length; i++, at += width)
        {
            var sample = raw.Slice(at, width);

            samples[i] = _encoding switch
            {
                Encoding.Float32 => Finite(BinaryPrimitives.ReadSingleBigEndian(sample)),
                Encoding.Float64 => Finite((float)BinaryPrimitives.ReadDoubleBigEndian(sample)),
                Encoding.LittleEndian => (int)LeftAligned(sample, bigEndian: false) * Scale,
                _ => (int)LeftAligned(sample, bigEndian: true) * Scale,
            };
        }
    }

    /// <summary>Próbka całkowita przesunięta do najstarszych bitów, więc skala nie zależy od głębi.</summary>
    private static uint LeftAligned(ReadOnlySpan<byte> sample, bool bigEndian)
    {
        uint value = 0;
        for (var i = 0; i < sample.Length; i++)
            value |= (uint)sample[bigEndian ? i : sample.Length - 1 - i] << (24 - 8 * i);
        return value;
    }

    /// <summary>Nieskończoność albo nie-liczba z pliku zatrułaby cały tor przetwarzania.</summary>
    private static float Finite(float value) => float.IsFinite(value) ? value : 0f;

    protected override void SeekSource(long sourceFrame)
    {
        var frame = Math.Clamp(sourceFrame, 0, _totalSourceFrames);
        _stream.Position = _dataStart + frame * _frameBytes;
        _remaining = _totalSourceFrames - frame;
    }

    protected override void DisposeCore()
    {
        if (_ownsStream) _stream.Dispose();
    }
}
