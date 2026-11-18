using System.Buffers.Binary;
using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Ogg Vorbis na tonie z prawdziwego kodera; plik leży w <c>Dane</c>.</summary>
public sealed class VorbisDecoderTests
{
    private const int ToneFrames = 20480;

    private static IAudioDecoder Open(byte[] bytes, int rate = 44100) =>
        DecoderFactory.Open(new MemoryStream(bytes), AudioFileFormat.OggVorbis, ownsStream: true, rate, 2);

    private static long ReadAll(IAudioDecoder decoder)
    {
        var buffer = new float[4096 * decoder.Channels];
        long frames = 0;
        int read;

        while ((read = decoder.Read(buffer)) > 0) frames += read;
        return frames;
    }

    [Fact]
    public void PlikGraWCalosciIPoPrzewinieciu()
    {
        using var decoder = Open(TestAudioFiles.Seed("ogg"));

        Assert.Equal(ToneFrames, decoder.TotalFrames);
        Assert.Equal(44100, decoder.SourceSampleRate);
        Assert.Equal(2, decoder.SourceChannels);
        Assert.Equal(ToneFrames, ReadAll(decoder));

        decoder.Seek(ToneFrames / 2);
        Assert.InRange(ReadAll(decoder), ToneFrames / 2 - 2048, ToneFrames / 2 + 2048);

        decoder.Seek(0);
        Assert.Equal(ToneFrames, ReadAll(decoder));
    }

    /// <summary>Początek nagłówka komentarzy: znacznik, długość nazwy kodera, nazwa, liczba wpisów.</summary>
    private static (int VendorLength, int Count) CommentFields(byte[] bytes)
    {
        var header = bytes.AsSpan().IndexOf("\u0003vorbis"u8);
        Assert.True(header > 0);

        var vendor = header + 7;
        return (vendor, vendor + 4 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(vendor)));
    }

    /// <summary>
    /// Biblioteka przydziela tablicę na tyle komentarzy, ile zapowiada nagłówek, zanim sprawdzi,
    /// czy pakiet je mieści. Plik pięciu kilobajtów żądał w ten sposób kilku gigabajtów
    /// przy każdym otwarciu: do grania i do pomiaru głośności.
    /// </summary>
    [Theory]
    [InlineData(0x20000000u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    public void ZapowiedzMilionowKomentarzyNieZajmujePamieci(uint count)
    {
        var bytes = TestAudioFiles.Seed("ogg");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CommentFields(bytes).Count), count);
        TestAudioFiles.OggWriter.FixChecksums(bytes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<AudioException>(() => Open(bytes));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(DecoderFailure.DamagedFile, ex.Reason);
        Assert.True(allocated < 8 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    [Theory]
    [InlineData(0x40000000u)]
    [InlineData(0xFFFFFFF0u)]
    public void NazwaKoderaDluzszaNizPakietNieZajmujePamieci(uint length)
    {
        var bytes = TestAudioFiles.Seed("ogg");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CommentFields(bytes).VendorLength), length);
        TestAudioFiles.OggWriter.FixChecksums(bytes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<AudioException>(() => Open(bytes));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(DecoderFailure.DamagedFile, ex.Reason);
        Assert.True(allocated < 8 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    /// <summary>
    /// Drugi strumień wpleciony w plik nie jest odtwarzany, więc jego nagłówków nikt nie czyta.
    /// Wcześniej biblioteka rozbierała je, gdy tylko na nie trafiła, czyli w połowie utworu.
    /// </summary>
    [Fact]
    public void NaglowkiDrugiegoStrumieniaNieSaCzytane()
    {
        var bytes = TestAudioFiles.Seed("ogg");

        // Trzecia strona ziarna niesie cały dźwięk; obcy strumień trafia tuż przed nią.
        var third = bytes.AsSpan().LastIndexOf("OggS"u8);

        var identification = bytes.AsSpan(bytes.AsSpan().IndexOf("\u0001vorbis"u8), 30).ToArray();
        var comments = new byte[15];
        "\u0003vorbis"u8.CopyTo(comments);
        BinaryPrimitives.WriteUInt32LittleEndian(comments.AsSpan(11), 0x7FFFFFFF);

        var foreign = new TestAudioFiles.OggWriter();
        foreign.WritePage(identification, 0, bos: true, eos: false);
        foreign.WritePage(comments, 0, bos: false, eos: false);

        byte[] mixed = [.. bytes[..third], .. foreign.ToArray(), .. bytes[third..]];

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var decoder = Open(mixed);
        var frames = ReadAll(decoder);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(ToneFrames, frames);
        Assert.True(allocated < 32 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    /// <summary>Pakiety pliku z jednym strumieniem, złożone z segmentów kolejnych stron.</summary>
    private static List<byte[]> Packets(byte[] ogg)
    {
        var packets = new List<byte[]>();
        var current = new List<byte>();

        for (var at = 0; at + 27 <= ogg.Length;)
        {
            int segments = ogg[at + 26];
            var data = at + 27 + segments;

            for (var i = 0; i < segments; i++)
            {
                int lacing = ogg[at + 27 + i];
                current.AddRange(ogg.AsSpan(data, lacing));
                data += lacing;

                if (lacing == 255) continue;

                packets.Add([.. current]);
                current.Clear();
            }

            at = data;
        }

        return packets;
    }

    /// <summary>Plik z prawdziwymi nagłówkami identyfikacji i komentarzy oraz podanym nagłówkiem konfiguracji.</summary>
    private static byte[] WithSetup(byte[] setup)
    {
        var packets = Packets(TestAudioFiles.Seed("ogg"));

        var ogg = new TestAudioFiles.OggWriter();
        ogg.WritePage(packets[0], 0, bos: true, eos: false);
        ogg.WritePage([packets[1], setup], 0, bos: false, eos: true);
        return ogg.ToArray();
    }

    /// <summary>Kontrola nagłówka konfiguracji nie może odrzucić tego, co zapisał prawdziwy koder.</summary>
    [Fact]
    public void PrawdziwyNaglowekKonfiguracjiPrzechodzi()
    {
        var packets = Packets(TestAudioFiles.Seed("ogg"));

        Assert.True(packets.Count > 3);
        Assert.True(packets[2].AsSpan().StartsWith("\u0005vorbis"u8));

        using var decoder = Open(TestAudioFiles.Seed("ogg"));
        Assert.Equal(ToneFrames, ReadAll(decoder));
    }

    /// <summary>
    /// Księga kodowa podaje liczbę wpisów, a biblioteka przydziela na nie tablice, zanim przeczyta
    /// choć jeden. Szesnaście milionów wpisów w każdej z 256 ksiąg to kilkanaście gigabajtów
    /// z nagłówka, który mieści się w kilobajcie.
    /// </summary>
    [Fact]
    public void KsiegaKodowaZMilionamiWpisowNieZajmujePamieci()
    {
        var setup = Packets(TestAudioFiles.Seed("ogg"))[2];

        // Typ i napis (7 bajtów), liczba ksiąg, znacznik księgi (3), wymiary (2), wpisy (3).
        Assert.True(setup.AsSpan(8, 3).SequenceEqual(new byte[] { 0x42, 0x43, 0x56 }));
        setup[13] = setup[14] = setup[15] = 0xFF;

        var bytes = WithSetup(setup);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<AudioException>(() => Open(bytes));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(DecoderFailure.DamagedFile, ex.Reason);
        Assert.True(allocated < 8 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    /// <summary>
    /// Księga o długościach rosnących podaje, ile wpisów ma każdą kolejną długość. Gdy pakiet urywa
    /// się w tym miejscu, odczyt zwraca zera, a biblioteka czekała w pętli na wpisy, które nie
    /// przyjdą: wątek dekodujący nie wracał z otwierania pliku.
    /// </summary>
    [Fact]
    public async Task UrwanaKsiegaUporzadkowanaNieWieszaOtwierania()
    {
        byte[] setup =
        [
            5, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s',
            0,                  // jedna księga
            0x42, 0x43, 0x56,   // znacznik księgi
            1, 0,               // jeden wymiar
            0xE8, 0x03, 0,      // tysiąc wpisów
            0x01,               // długości rosnące; dalej pakiet się kończy
        ];

        var bytes = WithSetup(setup);

        var open = Task.Run(() => Assert.Throws<AudioException>(() => Open(bytes)));

        Assert.Same(open, await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(DecoderFailure.DamagedFile, (await open).Reason);
    }

    /// <summary>
    /// Drugi plik z prawdziwego kodera, w wysokiej jakości i mono: inny zestaw ksiąg kodowych niż
    /// w tonie stereo. Kontrola nagłówków ma przepuszczać każdy z nich.
    /// </summary>
    [Fact]
    public void PlikWysokiejJakosciMonoGraWCalosci()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", "szum-mono-48000.ogg"));

        using var decoder = Open(bytes, 48000);

        Assert.Equal(1, decoder.SourceChannels);
        Assert.Equal(48000, decoder.SourceSampleRate);
        Assert.Equal(40960, decoder.TotalFrames);
        Assert.Equal(40960, ReadAll(decoder));
    }

    /// <summary>
    /// Licznik komentarzy zawyżony o kilka zdarza się po błędzie programu tagującego. Biblioteka
    /// czyta wtedy brakujące wpisy jako puste i plik gra, więc kontrola nie może go odrzucić.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(100u)]
    public void LicznikKomentarzyZawyzonyOKilkaNieOdbieraPliku(uint extra)
    {
        var bytes = TestAudioFiles.Seed("ogg");
        var count = CommentFields(bytes).Count;

        // Komentarze i konfiguracja leżą w ziarnie na jednej stronie, więc pakiet komentarzy kończy
        // się tam, gdzie zaczyna się następny; do próby potrzebny jest pakiet, po którym nic nie ma.
        var packets = Packets(bytes);
        var comments = packets[1];
        var offset = count - bytes.AsSpan().IndexOf(comments.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(
            comments.AsSpan(offset), BinaryPrimitives.ReadUInt32LittleEndian(comments.AsSpan(offset)) + extra);

        // Bit ramki na końcu pakietu komentarzy biblioteka pomija; bez niego pakiet kończy się na ostatnim wpisie.
        var ogg = new TestAudioFiles.OggWriter();
        ogg.WritePage(packets[0], 0, bos: true, eos: false);
        ogg.WritePage(comments[..^1], 0, bos: false, eos: false);
        ogg.WritePage(packets[2], 0, bos: false, eos: false);
        ogg.WritePage(packets.Skip(3).ToList(), ToneFrames, bos: false, eos: true);

        using var decoder = Open(ogg.ToArray());
        Assert.True(ReadAll(decoder) > ToneFrames / 2);
    }
}
