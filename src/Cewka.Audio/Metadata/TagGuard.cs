using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text;

namespace Cewka.Audio.Metadata;

/// <summary>
/// Kontrola pliku przed oddaniem go bibliotece znaczników. Blok obrazu we FLAC-u i w komentarzach
/// Ogg podaje długości typu, opisu i danych, a biblioteka przydziela według nich pamięć, zanim
/// cokolwiek przeczyta: plik kilku kilobajtów żądał tak po kilka gigabajtów na odczyt.
/// Tutaj sprawdzane jest tylko to, czy te długości mieszczą się w bloku, który je niesie.
/// Wszystko, czego ta kontrola nie rozumie, przechodzi dalej bez zastrzeżeń.
/// </summary>
internal static class TagGuard
{
    /// <summary>Tyle początku bloku obrazu wystarcza na pola długości; typ i opis są krótkie.</summary>
    private const int PictureHeadBytes = 4096;

    private const int MaxFlacBlocks = 4096;

    /// <summary>Pakiet komentarzy z okładką ma do kilku megabajtów.</summary>
    private const int MaxCommentBytes = 64 * 1024 * 1024;

    private const int PageHeaderBytes = 27;

    private const int MaxOggPages = 20_000;

    /// <summary>Fałsz, gdy plik niesie długości, których nie wolno oddać bibliotece. Pozycja strumienia wraca na początek.</summary>
    public static bool IsSafe(Stream stream)
    {
        try
        {
            stream.Position = 0;
            return Check(stream);
        }
        catch (EndOfStreamException)
        {
            // Plik urwany w połowie nagłówka: biblioteka sama skończy na końcu danych.
            return true;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static bool Check(Stream stream)
    {
        Span<byte> head = stackalloc byte[10];
        if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return true;

        long start = 0;

        if (head[..3].SequenceEqual("ID3"u8))
        {
            // Znacznik ID3v2 przed właściwym plikiem: długość w czterech bajtach po siedem bitów.
            var size = (head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F);
            start = 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);

            stream.Position = start;
            if (stream.ReadAtLeast(head[..4], 4, throwOnEndOfStream: false) < 4) return true;
        }

        if (head[..4].SequenceEqual("fLaC"u8))
        {
            stream.Position = start + 4;
            return FlacIsSafe(stream);
        }

        if (head[..4].SequenceEqual("OggS"u8))
        {
            stream.Position = start;
            return OggIsSafe(stream);
        }

        return true;
    }

    /// <summary>
    /// Bloki są przechodzone tak, jak robi to biblioteka, nie tak, jak każe format: pierwszy blok
    /// ma dla niej zawsze 34 bajty bez względu na zapisaną długość, a blok o typie spoza zakresu
    /// kończy czytanie. Kontrola idąca inną drogą patrzyłaby w inne bajty niż biblioteka.
    /// </summary>
    private static bool FlacIsSafe(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];

        if (stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) < 4) return true;
        stream.Position += 34;

        for (var block = 0; block < MaxFlacBlocks; block++)
        {
            if (stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) < 4) return true;

            var type = header[0] & 0x7F;
            var last = (header[0] & 0x80) != 0;
            var length = header[1] << 16 | header[2] << 8 | header[3];
            var next = stream.Position + length;

            if (type >= 7) return true;

            if (type == 6)
            {
                // Biblioteka czyta pola obrazu ze strumienia bez oglądania się na długość bloku,
                // więc tutaj też liczy się to, co faktycznie leży za nagłówkiem.
                var picture = ReadUpTo(stream, PictureHeadBytes);
                if (!PictureIsSafe(picture, length)) return false;
            }
            else if (type == 4)
            {
                if (!CommentsAreSafe(ReadUpTo(stream, length))) return false;
            }

            if (last || next >= stream.Length) return true;
            stream.Position = next;
        }

        // Tysiące bloków metadanych to nie jest plik od kodera.
        return false;
    }

    /// <summary>
    /// Nagłówek bloku obrazu: rodzaj, długość typu i typ, długość opisu i opis, cztery liczby
    /// o wymiarach, długość danych. <paramref name="total"/> to rozmiar całego bloku.
    /// </summary>
    internal static bool PictureIsSafe(ReadOnlySpan<byte> head, long total)
    {
        // Gdy pola wypadają poza wczytany początek, a blok jest dłuższy, typ albo opis ma kilobajty.
        bool Beyond(ReadOnlySpan<byte> seen) => seen.Length >= total;

        if (head.Length < 8) return Beyond(head);

        long mime = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        if (mime > total - 8) return false;

        var at = 8 + mime;
        if (at + 4 > head.Length) return Beyond(head);

        long description = BinaryPrimitives.ReadUInt32BigEndian(head[(int)at..]);
        if (description > total - at - 4) return false;

        at += 4 + description;
        if (at + 20 > head.Length) return Beyond(head);

        long data = BinaryPrimitives.ReadUInt32BigEndian(head[(int)(at + 16)..]);
        return data <= total - at - 20;
    }

    /// <summary>
    /// Komentarze Vorbis: nazwa kodera, liczba wpisów, wpisy „NAZWA=wartość”. Długości samych wpisów
    /// biblioteka sprawdza; tu chodzi o obraz zapisany we wpisie jako base64.
    /// </summary>
    internal static bool CommentsAreSafe(ReadOnlySpan<byte> data)
    {
        if (!TryTake(ref data, out var vendor) || vendor > data.Length) return true;
        data = data[(int)vendor..];

        if (!TryTake(ref data, out var count)) return true;

        for (uint i = 0; i < count; i++)
        {
            if (!TryTake(ref data, out var length) || length > data.Length) return true;

            var entry = data[..(int)length];
            data = data[(int)length..];

            var separator = entry.IndexOf((byte)'=');
            if (separator != 22 || !Ascii.EqualsIgnoreCase(entry[..22], "METADATA_BLOCK_PICTURE"u8)) continue;

            if (!EncodedPictureIsSafe(entry[23..])) return false;
        }

        return true;
    }

    private static bool TryTake(ref ReadOnlySpan<byte> data, out uint value)
    {
        value = 0;
        if (data.Length < 4) return false;

        value = BinaryPrimitives.ReadUInt32LittleEndian(data);
        data = data[4..];
        return true;
    }

    /// <summary>
    /// Obraz zapisany w komentarzu. Tekst jest przygotowywany tak samo jak w bibliotece: długość
    /// obcięta do wielokrotności czterech, bajty zerowe liczone jako znak dopełnienia, białe znaki
    /// pominięte. Inaczej dałoby się zapisać go tak, żeby kontrola uznała go za zepsuty,
    /// a biblioteka zdekodowała.
    /// </summary>
    private static bool EncodedPictureIsSafe(ReadOnlySpan<byte> encoded)
    {
        encoded = encoded[..(encoded.Length - encoded.Length % 4)];

        var text = ArrayPool<byte>.Shared.Rent(PictureHeadBytes / 3 * 4);
        var bytes = ArrayPool<byte>.Shared.Rent(PictureHeadBytes);

        try
        {
            var taken = 0;
            long characters = 0;

            foreach (var c in encoded)
            {
                if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;

                characters++;
                if (taken < PictureHeadBytes / 3 * 4) text[taken++] = c == 0 ? (byte)'=' : c;
            }

            var status = Base64.DecodeFromUtf8(
                text.AsSpan(0, taken - taken % 4), bytes, out _, out var written, isFinalBlock: characters == taken);

            // Tekst, który nie jest base64, biblioteka odrzuca przed czytaniem jakichkolwiek długości.
            if (status == OperationStatus.InvalidData) return true;

            return PictureIsSafe(bytes.AsSpan(0, written), characters / 4 * 3);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(text);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// Komentarze w kontenerze Ogg, zebrane tak, jak zbiera je biblioteka: dla każdego strumienia
    /// treść stron od tej, która zaczyna drugi pakiet, do strony zaczynającej trzeci. Biblioteka nie
    /// patrzy przy tym na sumy kontrolne, komentarze czyta z każdego strumienia w pliku, a gdy za
    /// stroną nie leży następna, za jej treść bierze wszystko aż do najbliższego znacznika strony.
    /// </summary>
    private static bool OggIsSafe(Stream stream)
    {
        var header = new byte[PageHeaderBytes + 255];
        var chunk = new byte[64 * 1024];

        var started = new Dictionary<uint, int>();
        var collected = new Dictionary<uint, MemoryStream>();
        long total = 0;

        // Tak daleko biblioteka szuka następnej strony, gdy nie ma jej tam, gdzie powinna być.
        var searchLimit = (long)Math.Round(stream.Length * 0.1);

        for (var page = 0; ; page++)
        {
            // Komentarze leżą na początku pliku; kto szuka ich tak daleko, ten nie czyta pliku od kodera.
            if (page == MaxOggPages) return false;

            if (stream.ReadAtLeast(header.AsSpan(0, PageHeaderBytes), PageHeaderBytes, throwOnEndOfStream: false) < PageHeaderBytes) break;
            if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8)) break;

            int segments = header[26];
            if (stream.ReadAtLeast(header.AsSpan(PageHeaderBytes, segments), segments, throwOnEndOfStream: false) < segments) break;

            long size = 0;
            for (var i = 0; i < segments; i++) size += header[PageHeaderBytes + i];

            var serial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
            var fresh = (header[5] & 0x01) == 0;
            var start = stream.Position;

            var known = started.TryGetValue(serial, out var packets);

            if (known && packets < 2)
            {
                stream.Position = start + size;

                if (stream.ReadAtLeast(header.AsSpan(0, 4), 4, throwOnEndOfStream: false) < 4 || !header.AsSpan(0, 4).SequenceEqual("OggS"u8))
                {
                    stream.Position = start;

                    var found = FindPageMark(stream, searchLimit);
                    if (found < 0) break;

                    size = found - 4 - start;
                }
            }

            if (!known)
            {
                started[serial] = 1;
            }
            else
            {
                if (fresh) started[serial] = ++packets;

                if (packets == 2)
                {
                    if ((total += size) > MaxCommentBytes) return false;

                    if (!collected.TryGetValue(serial, out var comments)) collected[serial] = comments = new MemoryStream();

                    stream.Position = start;

                    for (var left = size; left > 0;)
                    {
                        var read = stream.Read(chunk, 0, (int)Math.Min(left, chunk.Length));
                        if (read == 0) break;

                        comments.Write(chunk, 0, read);
                        left -= read;
                    }
                }

                // Biblioteka kończy na stronie, która zaczyna trzeci pakiet swojego strumienia.
                if (packets >= 3) break;
            }

            if (start + size >= stream.Length) break;
            stream.Position = start + size;
        }

        foreach (var comments in collected.Values)
        {
            var content = comments.GetBuffer().AsSpan(0, (int)comments.Length);

            // Komentarze zaczynają się za znacznikiem kodeka: po 7 bajtach w Vorbisie, po 8 w Opusie,
            // po 4 we FLAC-u, a w pozostałych od początku. Biblioteka wybiera miejsce według rodzaju
            // całego pliku, nie tego pakietu, więc sprawdzane są wszystkie cztery.
            foreach (var offset in (ReadOnlySpan<int>)[0, 4, 7, 8])
                if (content.Length >= offset && !CommentsAreSafe(content[offset..])) return false;
        }

        return true;
    }

    /// <summary>
    /// Pozycja tuż za najbliższym znacznikiem strony albo −1. Szuka dokładnie tak jak biblioteka,
    /// razem z jej uproszczeniem: po nieudanym dopasowaniu bieżący bajt nie zaczyna nowego.
    /// </summary>
    private static long FindPageMark(Stream stream, long limit)
    {
        var mark = "OggS"u8;
        var buffer = new byte[512];
        var origin = stream.Position;

        var left = limit > 0 ? Math.Min(stream.Length - origin, limit) : stream.Length - origin;
        var matched = 0;
        long passed = 0;

        while (left > 0)
        {
            var wanted = (int)Math.Min(left, buffer.Length);
            var read = stream.Read(buffer, 0, wanted);

            for (var i = 0; i < read; i++)
            {
                if (mark[matched] == buffer[i]) matched++;
                else if (matched > 0) matched = 0;

                if (matched == mark.Length) return origin + passed + i + 1;
            }

            left -= wanted;
            passed += read;

            if (read == 0) break;
        }

        return -1;
    }

    private static byte[] ReadUpTo(Stream stream, int count)
    {
        var buffer = new byte[(int)Math.Min(count, Math.Max(0, stream.Length - stream.Position))];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }
}
