using System.Buffers.Binary;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Przejście przez strony pliku Ogg przed oddaniem go czytnikowi Concentus. Czytnik trzyma
/// w pamięci obiekt na każdy pakiet pliku, a pakiety nagłówkowe pomija rekurencyjnie, więc plik
/// z milionami pustych pakietów zajmował gigabajty, a seria pakietów „OpusHead” przepełniała
/// stos, czego nie da się złapać. Tutaj liczą się same tablice segmentów i pierwsze bajty pakietów.
/// </summary>
internal static class OggPacketScan
{
    /// <summary>Ponad dwie doby dźwięku w ramkach po 20 ms; tyle obiektów czytnik jeszcze udźwignie.</summary>
    internal const int MaxPackets = 10_000_000;

    /// <summary>Koder nie zapisuje pustych pakietów; garść może zostać po uszkodzeniu pliku.</summary>
    internal const int MaxEmptyPackets = 64;

    /// <summary>Łańcuch strumieni daje po dwa pakiety nagłówkowe z rzędu.</summary>
    internal const int MaxHeaderRun = 16;

    private const int PageHeaderBytes = 27;
    private const int PrefixBytes = 9;

    /// <summary>Opis powodu, dla którego pliku nie wolno oddać czytnikowi; null, gdy wolno.</summary>
    internal static string? FindOpusHazard(Stream stream)
    {
        if (!stream.CanSeek) return null;

        var origin = stream.Position;

        try
        {
            stream.Position = 0;
            return Scan(stream);
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static string? Scan(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var filled = 0;
        var at = 0;

        long packets = 0;
        long empty = 0;

        // Stan pierwszego strumienia: tylko jego pakiety przechodzą przez rekurencję czytnika.
        uint? firstSerial = null;
        var prefix = new byte[PrefixBytes];
        var prefixLength = 0;
        long packetLength = 0;
        var headerRun = 0;

        // Dosuwa dane tak, żeby od „at” było co najmniej „count” bajtów; fałsz na końcu pliku.
        bool Ensure(int count)
        {
            if (filled - at >= count) return true;

            buffer.AsSpan(at, filled - at).CopyTo(buffer);
            filled -= at;
            at = 0;

            while (filled < count)
            {
                var read = stream.Read(buffer, filled, buffer.Length - filled);
                if (read == 0) return false;
                filled += read;
            }

            return true;
        }

        while (true)
        {
            if (!Ensure(PageHeaderBytes)) return null;

            // Stroną jest to samo, co dla czytnika: znacznik, wersja zero, całość w pliku i zgodna
            // suma kontrolna. Wszystko inne to śmieci, w których czytnik szuka następnego znacznika
            // bajt po bajcie, także wewnątrz tego, co wyglądało na stronę. Kontrola ufająca
            // tablicy segmentów strony ze złą sumą przeskoczyłaby strony ukryte w jej treści.
            var body = 0;
            var page = buffer.AsSpan(at, 4).SequenceEqual("OggS"u8) && buffer[at + 4] == 0;

            if (page)
            {
                int count = buffer[at + 26];
                page = Ensure(PageHeaderBytes + count);

                if (page)
                {
                    for (var i = 0; i < count; i++) body += buffer[at + PageHeaderBytes + i];

                    // Strona ma najwyżej 255 segmentów po 255 bajtów, więc mieści się w buforze.
                    page = Ensure(PageHeaderBytes + count + body)
                           && ChecksumMatches(buffer.AsSpan(at, PageHeaderBytes + count + body));
                }
            }

            if (!page)
            {
                if (filled - at < 5) return null;

                var next = buffer.AsSpan(at + 1, filled - at - 1).IndexOf("OggS"u8);
                at = next >= 0 ? at + 1 + next : Math.Max(at + 1, filled - 3);
                continue;
            }

            int segments = buffer[at + 26];

            var continued = (buffer[at + 5] & 0x01) != 0;
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 14));
            firstSerial ??= serial;
            var first = serial == firstSerial;

            var table = buffer.AsSpan(at + PageHeaderBytes, segments);
            var data = buffer.AsSpan(at + PageHeaderBytes + segments, body);
            var offset = 0;

            if (first && !continued)
            {
                // Strona zaczyna nowy pakiet, więc niedokończony z poprzedniej przepada.
                prefixLength = 0;
                packetLength = 0;
            }

            // Czy bieżący segment należy do pakietu zaczętego wcześniej.
            var open = continued;

            foreach (var lacing in table)
            {
                if (first)
                {
                    var take = Math.Min(Math.Min(lacing, PrefixBytes - prefixLength), Math.Max(0, data.Length - offset));
                    if (take > 0)
                    {
                        data.Slice(offset, take).CopyTo(prefix.AsSpan(prefixLength));
                        prefixLength += take;
                    }

                    packetLength += lacing;
                }

                offset += lacing;

                // Segment krótszy niż 255 bajtów kończy pakiet.
                if (lacing == 255)
                {
                    open = true;
                    continue;
                }

                if (++packets > MaxPackets) return $"ponad {MaxPackets} pakietów";
                if (lacing == 0 && !open && ++empty > MaxEmptyPackets) return "puste pakiety";

                open = false;
                if (!first) continue;

                var header = packetLength > 8 && prefixLength >= 8 &&
                             (prefix.AsSpan(0, 8).SequenceEqual("OpusHead"u8) || prefix.AsSpan(0, 8).SequenceEqual("OpusTags"u8));

                headerRun = header ? headerRun + 1 : 0;
                if (headerRun > MaxHeaderRun) return "seria pakietów nagłówkowych";

                prefixLength = 0;
                packetLength = 0;
            }

            at += PageHeaderBytes + segments + body;
        }
    }

    private static readonly uint[] ChecksumTable = BuildChecksumTable();

    private static uint[] BuildChecksumTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            var value = i << 24;
            for (var bit = 0; bit < 8; bit++) value = (value & 0x80000000) != 0 ? (value << 1) ^ 0x04C11DB7 : value << 1;
            table[i] = value;
        }

        return table;
    }

    /// <summary>Suma kontrolna strony Ogg, liczona z polem sumy wyzerowanym.</summary>
    private static bool ChecksumMatches(ReadOnlySpan<byte> page)
    {
        var table = ChecksumTable;
        uint crc = 0;

        foreach (var value in page[..22]) crc = (crc << 8) ^ table[(crc >> 24) ^ value];
        for (var i = 0; i < 4; i++) crc = (crc << 8) ^ table[crc >> 24];
        foreach (var value in page[26..]) crc = (crc << 8) ^ table[(crc >> 24) ^ value];

        return crc == BinaryPrimitives.ReadUInt32LittleEndian(page[22..]);
    }
}
