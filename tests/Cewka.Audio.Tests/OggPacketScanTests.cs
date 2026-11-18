using System.Buffers.Binary;
using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Plik Opus spreparowany pod słabości czytnika kontenera: obiekt na każdy pakiet pliku i rekurencja
/// na pakietach nagłówkowych. Taki plik ma się nie otworzyć, zamiast zająć pamięć albo przepełnić stos.
/// </summary>
public sealed class OggPacketScanTests
{
    private const uint Serial = 0x43657721;

    private static IAudioDecoder Open(byte[] bytes) =>
        DecoderFactory.Open(new MemoryStream(bytes), AudioFileFormat.Opus, ownsStream: true, 48000, 2);

    /// <summary>Strona z podaną tablicą segmentów; treść to kolejne bajty <paramref name="body"/>.</summary>
    private static byte[] Page(uint sequence, byte[] lacing, byte[] body, bool continued = false, bool first = false)
    {
        var page = new byte[27 + lacing.Length + body.Length];
        "OggS"u8.CopyTo(page);
        page[5] = (byte)((continued ? 0x01 : 0) | (first ? 0x02 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), Serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Length;
        lacing.CopyTo(page, 27);
        body.CopyTo(page, 27 + lacing.Length);
        return page;
    }

    private static byte[] Head()
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        return head;
    }

    private static byte[] Tags()
    {
        var tags = new byte[16];
        "OpusTags"u8.CopyTo(tags);
        return tags;
    }

    private static byte[] Build(IEnumerable<byte[]> pages)
    {
        var bytes = pages.SelectMany(page => page).ToArray();
        TestAudioFiles.OggWriter.FixChecksums(bytes);
        return bytes;
    }

    private static IEnumerable<byte[]> Start()
    {
        yield return Page(0, [19], Head(), first: true);
        yield return Page(1, [16], Tags());
    }

    /// <summary>
    /// Czytnik pomija pakiet „OpusHead”, wywołując samego siebie. Dwieście tysięcy takich pakietów
    /// z rzędu to przepełnienie stosu, które kończy program bez możliwości obsługi.
    /// </summary>
    [Fact]
    public void SeriaPakietowNaglowkowychNieOtwieraSie()
    {
        var packet = new byte[9];
        "OpusHead"u8.CopyTo(packet);

        var lacing = Enumerable.Repeat((byte)9, 255).ToArray();
        var body = Enumerable.Repeat(packet, 255).SelectMany(p => p).ToArray();

        var bytes = Build(Start().Concat(Enumerable.Range(0, 800).Select(i => Page((uint)(2 + i), lacing, body))));

        var ex = Assert.Throws<AudioException>(() => Open(bytes));
        Assert.Equal(DecoderFailure.DamagedFile, ex.Reason);
    }

    /// <summary>Pakiet nagłówkowy rozcięty między strony jest dla czytnika tym samym pakietem.</summary>
    [Fact]
    public void SeriaPakietowNaglowkowychRozcietychMiedzyStronyNieOtwieraSie()
    {
        // Każdy pakiet ma 255 + 5 bajtów: pierwsze 255 kończy stronę, reszta zaczyna następną.
        var opening = new byte[255];
        "OpusTags"u8.CopyTo(opening);

        var pages = new List<byte[]>(Start());
        for (var i = 0; i < 100; i++)
        {
            pages.Add(Page((uint)(2 + 2 * i), [255], opening));
            pages.Add(Page((uint)(3 + 2 * i), [5], new byte[5], continued: true));
        }

        Assert.Equal("seria pakietów nagłówkowych", OggPacketScan.FindOpusHazard(new MemoryStream(Build(pages))));
    }

    /// <summary>
    /// Strona z 255 pustymi pakietami zajmuje 282 bajty, a czytnik tworzy obiekt na każdy pakiet
    /// i trzyma go do zamknięcia pliku.
    /// </summary>
    [Fact]
    public void PustePakietyNieZajmujaPamieci()
    {
        var bytes = Build(Start().Concat(Enumerable.Range(0, 4000).Select(i => Page((uint)(2 + i), new byte[255], []))));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<AudioException>(() => Open(bytes));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(DecoderFailure.DamagedFile, ex.Reason);
        Assert.True(allocated < 8 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    [Fact]
    public void ZwyklyPlikPrzechodzi()
    {
        var bytes = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 200, PacketsPerPage = 50 });

        Assert.Null(OggPacketScan.FindOpusHazard(new MemoryStream(bytes)));
    }

    /// <summary>W ciszy koder oszczędzający pasmo zapisuje pakiety jednobajtowe; to nie są pakiety puste.</summary>
    [Fact]
    public void PakietyJednobajtowePrzechodza()
    {
        var lacing = Enumerable.Repeat((byte)1, 255).ToArray();
        var bytes = Build(Start().Concat(Enumerable.Range(0, 400).Select(i => Page((uint)(2 + i), lacing, new byte[255]))));

        Assert.Null(OggPacketScan.FindOpusHazard(new MemoryStream(bytes)));
    }

    /// <summary>Pakiet o długości będącej wielokrotnością 255 kończy się segmentem zerowym i nie jest pusty.</summary>
    [Fact]
    public void PakietyKonczoneSegmentemZerowymPrzechodza()
    {
        var lacing = Enumerable.Range(0, 200).Select(i => (byte)(i % 2 == 0 ? 255 : 0)).ToArray();
        var body = new byte[100 * 255];
        Array.Fill(body, (byte)0x7F);

        var bytes = Build(Start().Concat(Enumerable.Range(0, 20).Select(i => Page((uint)(2 + i), lacing, body))));

        Assert.Null(OggPacketScan.FindOpusHazard(new MemoryStream(bytes)));
    }

    /// <summary>Śmieci między stronami nie zatrzymują przejścia ani nie gubią stron za nimi.</summary>
    [Fact]
    public void SmieciMiedzyStronamiNieUkrywajaDalszychStron()
    {
        var garbage = new byte[100_000];
        new Random(5).NextBytes(garbage);

        var bytes = Build(Start()
            .Append(garbage)
            .Concat(Enumerable.Range(0, 400).Select(i => Page((uint)(2 + i), new byte[255], []))));

        Assert.Equal("puste pakiety", OggPacketScan.FindOpusHazard(new MemoryStream(bytes)));
    }

    [Fact]
    public void PozycjaStrumieniaWracaNaMiejsce()
    {
        var stream = new MemoryStream(TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 20 })) { Position = 7 };

        OggPacketScan.FindOpusHazard(stream);

        Assert.Equal(7, stream.Position);
    }

    /// <summary>
    /// Czytnik odrzuca stronę ze złą sumą kontrolną i szuka następnej bajt po bajcie, także w jej
    /// treści. Strony ukryte w treści takiej strony muszą więc liczyć się tak samo jak zwykłe:
    /// inaczej seria pakietów nagłówkowych przeszłaby obok kontroli.
    /// </summary>
    [Fact]
    public void StronyUkryteWStronieZeZlaSumaTezSieLicza()
    {
        var packet = new byte[9];
        "OpusHead"u8.CopyTo(packet);

        // Poprawne strony po 20 pakietów nagłówkowych, każda schowana w treści strony-opakowania.
        var hidden = Build([Page(7, Enumerable.Repeat((byte)9, 20).ToArray(), Enumerable.Repeat(packet, 20).SelectMany(p => p).ToArray())]);
        Assert.True(hidden.Length < 255);

        var start = Build(Start());
        var wrappers = new List<byte>();

        for (var i = 0; i < 2000; i++)
        {
            var wrapper = Page((uint)(100 + i), [(byte)hidden.Length], hidden);
            wrapper[22] = 0xDE;
            wrapper[23] = 0xAD;
            wrappers.AddRange(wrapper);
        }

        byte[] bytes = [.. start, .. wrappers];

        Assert.Equal("seria pakietów nagłówkowych", OggPacketScan.FindOpusHazard(new MemoryStream(bytes)));

        var ex = Assert.Throws<AudioException>(() => Open(bytes));
        Assert.Equal(DecoderFailure.DamagedFile, ex.Reason);
    }

    /// <summary>Strona urwana końcem pliku nie jest stroną; to, co w niej leży, czytnik przeszukuje dalej.</summary>
    [Fact]
    public void StronaUrwanaKoncemPlikuNieUkrywaStronWSrodku()
    {
        var hidden = Build(Enumerable.Range(0, 300).Select(i => Page((uint)(2 + i), new byte[255], [])));

        // Opakowanie zapowiada więcej treści, niż zostało w pliku.
        var wrapper = Page(1, Enumerable.Repeat((byte)255, 255).ToArray(), []);

        byte[] bytes = [.. Build(Start()), .. wrapper, .. hidden[..60_000]];

        Assert.Equal("puste pakiety", OggPacketScan.FindOpusHazard(new MemoryStream(bytes)));
    }
}
