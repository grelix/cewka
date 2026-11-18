using System.Buffers.Binary;
using System.Text;
using Cewka.Audio.Metadata;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Odczyt znaczników z pliku, któremu nie można ufać. Biblioteka znaczników przydziela pamięć według
/// długości zapisanych w pliku i potrafi wpaść w pętlę bez końca, więc plik przechodzi kontrolę,
/// zanim do niej trafi, a sam odczyt ma limit operacji.
/// </summary>
public sealed class TagGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cewka-znaczniki-" + Guid.NewGuid().ToString("N"));

    public TagGuardTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Osłony nie mogą zabrać niczego ze zwykłego pliku: znaczniki i okładka wychodzą jak dotąd.</summary>
    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("ogg")]
    [InlineData("opus")]
    [InlineData("m4a")]
    public void ZwyklyPlikOddajeZnacznikiIOkladke(string kind)
    {
        var path = Write("plik." + kind, TestAudioFiles.Tagged(kind));

        var metadata = MetadataReader.Read(path);
        var cover = MetadataReader.ReadCover(path);

        Assert.Equal(TestAudioFiles.TaggedTitle, metadata.Title);
        Assert.Equal("Wykonawca", metadata.Artist);
        Assert.Equal("Album", metadata.Album);
        Assert.Equal(3, metadata.TrackNumber);
        Assert.True(metadata.Duration > TimeSpan.FromMilliseconds(300), $"czas {metadata.Duration}");
        Assert.True(metadata.SampleRate > 0);

        Assert.NotNull(cover);
        Assert.Equal(TestAudioFiles.TaggedPictureBytes, cover.Length);
    }

    /// <summary>Rozszerzenie bywa błędne; biblioteka rozpoznaje wtedy format po treści, tak jak przy odczycie po ścieżce.</summary>
    [Fact]
    public void PlikOBlednymRozszerzeniuJestRozpoznawanyPoTresci()
    {
        var path = Write("plik.mp3", TestAudioFiles.Tagged("flac"));

        Assert.Equal(TestAudioFiles.TaggedTitle, MetadataReader.Read(path).Title);
    }

    private static byte[] Picture(uint mimeLength, uint descriptionLength, uint dataLength, int actualData = 16)
    {
        var mime = "image/jpeg"u8.ToArray();
        var block = new byte[8 + mime.Length + 4 + 16 + 4 + actualData];

        BinaryPrimitives.WriteUInt32BigEndian(block, 3);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), mimeLength);
        mime.CopyTo(block, 8);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8 + mime.Length), descriptionLength);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8 + mime.Length + 4 + 16), dataLength);

        return block;
    }

    [Theory]
    [InlineData(10u, 0u, 16u, true)]
    [InlineData(0x7FFFFFC7u, 0u, 16u, false)]
    [InlineData(10u, 0x40000000u, 16u, false)]
    [InlineData(10u, 0u, 0x7FFFFFC7u, false)]
    [InlineData(10u, 0u, 17u, false)]
    [InlineData(0xFFFFFFFFu, 0u, 16u, false)]
    public void DlugosciWBlokuObrazuMuszaMiescicSieWBloku(uint mime, uint description, uint data, bool safe)
    {
        var block = Picture(mime, description, data);

        Assert.Equal(safe, TagGuard.PictureIsSafe(block, block.Length));
    }

    /// <summary>Blok obrazu wstawiony do pliku FLAC zaraz za informacją o strumieniu.</summary>
    private static byte[] FlacWithPicture(byte[] picture, int declaredLength)
    {
        var flac = TestAudioFiles.Seed("flac");

        var header = new byte[4];
        header[0] = 6;
        header[1] = (byte)(declaredLength >> 16);
        header[2] = (byte)(declaredLength >> 8);
        header[3] = (byte)declaredLength;

        // Znacznik pliku, nagłówek pierwszego bloku i 34 bajty informacji o strumieniu.
        return [.. flac[..42], .. header, .. picture, .. flac[42..]];
    }

    /// <summary>
    /// Blok obrazu we FLAC-u z długością danych sięgającą dwóch gigabajtów, których w pliku nie ma.
    /// Biblioteka przydzielała tyle pamięci przy każdym odczycie znaczników i okładki.
    /// </summary>
    [Theory]
    [InlineData(10u, 0u, 0x7FFFFFC7u)]
    [InlineData(0x72656665u, 0u, 16u)]
    [InlineData(10u, 0x7FFFFFC7u, 16u)]
    public void ObrazZFalszywaDlugosciaNieZajmujePamieci(uint mime, uint description, uint data)
    {
        var picture = Picture(mime, description, data);
        var path = Write("obraz.flac", FlacWithPicture(picture, picture.Length));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var metadata = MetadataReader.Read(path);
        var cover = MetadataReader.ReadCover(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("obraz", metadata.Title);
        Assert.Null(cover);
        Assert.True(allocated < 4 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    /// <summary>Biblioteka czyta pola obrazu bez oglądania się na długość bloku, więc blok „pusty” też się liczy.</summary>
    [Fact]
    public void BlokObrazuOZerowejDlugosciNieUkrywaPolZaNim()
    {
        var path = Write("pusty-blok.flac", FlacWithPicture(Picture(0x72656665, 0, 16), declaredLength: 0));

        var before = GC.GetAllocatedBytesForCurrentThread();
        MetadataReader.Read(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    private static byte[] Comments(params string[] entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(4);
        writer.Write("test"u8);
        writer.Write(entries.Length);

        foreach (var entry in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        return stream.ToArray();
    }

    /// <summary>W kontenerze Ogg obraz siedzi w komentarzu jako base64; długości w nim są te same.</summary>
    [Fact]
    public void ObrazWKomentarzuPrzechodziTeSamaKontrole()
    {
        var good = Convert.ToBase64String(Picture(10, 0, 16));
        var bad = Convert.ToBase64String(Picture(10, 0, 0x7FFFFFC7));
        var spaced = string.Join("\r\n", bad.Chunk(20).Select(part => new string(part)));

        Assert.True(TagGuard.CommentsAreSafe(Comments("TITLE=a", "METADATA_BLOCK_PICTURE=" + good)));
        Assert.False(TagGuard.CommentsAreSafe(Comments("TITLE=a", "METADATA_BLOCK_PICTURE=" + bad)));
        Assert.False(TagGuard.CommentsAreSafe(Comments("metadata_block_picture=" + bad)));

        // Biblioteka pomija białe znaki przy dekodowaniu, więc nie mogą schować długości przed kontrolą.
        Assert.False(TagGuard.CommentsAreSafe(Comments("METADATA_BLOCK_PICTURE=" + spaced)));

        // Wpis, który tylko zaczyna się podobnie, to zwykły komentarz.
        Assert.True(TagGuard.CommentsAreSafe(Comments("METADATA_BLOCK_PICTURES=" + bad)));
    }

    [Fact]
    public void ObrazWKomentarzuPlikuOpusNieZajmujePamieci()
    {
        var bad = Convert.ToBase64String(Picture(10, 0, 0x7FFFFFC7));
        var opus = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions
        {
            Frames = 25,
            Tags = new Dictionary<string, string> { ["TITLE"] = "a", ["METADATA_BLOCK_PICTURE"] = bad },
        });
        var path = Write("obraz.opus", opus);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var metadata = MetadataReader.Read(path);
        var cover = MetadataReader.ReadCover(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("obraz", metadata.Title);
        Assert.Null(cover);
        Assert.True(allocated < 4 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    /// <summary>
    /// Atom MP4 o nazwie różniącej się od „mdat” tylko wielkością liter i o zerowej długości:
    /// biblioteka czytała jego nagłówek w kółko i nie wracała nigdy. Wątek zostawał w pętli
    /// do zamknięcia programu, a wpis kolejki, którego znaczniki czytał, blokował odtwarzanie.
    /// </summary>
    [Fact]
    public async Task PetlaWBiblioteceKonczySieNaLimicieOperacji()
    {
        var m4a = TestAudioFiles.Seed("m4a");

        var atom = m4a.AsSpan().IndexOf("mdat"u8);
        Assert.True(atom >= 4);

        m4a[atom] = (byte)'M';
        m4a.AsSpan(atom - 4, 4).Clear();

        var path = Write("petla.m4a", m4a);

        var read = Task.Run(() => MetadataReader.Read(path));
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(20))));

        Assert.Equal("petla", (await read).Title);
    }

    [Fact]
    public void LimitOperacjiPrzerywaOdczyt()
    {
        var stream = new BudgetStream(new MemoryStream(new byte[64]), budget: 3);
        var buffer = new byte[4];

        stream.ReadExactly(buffer);
        stream.Position = 0;
        stream.Seek(8, SeekOrigin.Begin);
        Assert.False(stream.Exhausted);

        Assert.Throws<IOException>(() => stream.ReadByte());
        Assert.True(stream.Exhausted);
    }

    /// <summary>
    /// Biblioteka przed dekodowaniem obcina tekst do wielokrotności czterech znaków i zamienia bajty
    /// zerowe na znak dopełnienia. Kontrola, która tego nie robi, uznałaby taki tekst za zepsuty
    /// i przepuściła, a biblioteka by go zdekodowała.
    /// </summary>
    [Fact]
    public void ObrazWKomentarzuJestDekodowanyTakJakWBibliotece()
    {
        var bad = Convert.ToBase64String(Picture(10, 0, 0x7FFFFFC7));
        Assert.EndsWith("==", bad);

        var zeros = bad.TrimEnd('=') + "\0\0";
        var ragged = bad + "abc";

        Assert.False(TagGuard.CommentsAreSafe(Comments("METADATA_BLOCK_PICTURE=" + zeros)));
        Assert.False(TagGuard.CommentsAreSafe(Comments("METADATA_BLOCK_PICTURE=" + ragged)));
    }

    /// <summary>Strony pliku Ogg, każda osobno.</summary>
    private static List<byte[]> Pages(byte[] ogg)
    {
        var pages = new List<byte[]>();

        for (var at = 0; at + 27 <= ogg.Length;)
        {
            int segments = ogg[at + 26];
            var size = 27 + segments;
            for (var i = 0; i < segments; i++) size += ogg[at + 27 + i];

            pages.Add(ogg[at..(at + size)]);
            at += size;
        }

        return pages;
    }

    private static byte[] OpusWithPicture(uint dataLength, int frames = 50) =>
        TestAudioFiles.Opus(new TestAudioFiles.OpusOptions
        {
            Frames = frames,
            Tags = new Dictionary<string, string>
            {
                ["METADATA_BLOCK_PICTURE"] = Convert.ToBase64String(Picture(10, 0, dataLength)),
            },
        });

    private long AllocatedByReading(string path)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        MetadataReader.Read(path);
        MetadataReader.ReadCover(path);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// Biblioteka czyta komentarze z każdego strumienia w pliku, nie tylko z tego, który gra.
    /// Obraz z fałszywą długością w drugim strumieniu to ten sam przydział pamięci.
    /// </summary>
    [Fact]
    public void ObrazWKomentarzuDrugiegoStrumieniaTezJestSprawdzany()
    {
        var good = Pages(TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 50 }));
        var hostile = Pages(OpusWithPicture(0x7FFFFFC7, frames: 2));

        // Drugi strumień dostaje własny numer; jego strony wchodzą między strony pierwszego.
        foreach (var page in hostile) page[14] ^= 0x55;

        byte[] mixed = [.. good[0], .. hostile[0], .. hostile[1], .. good.Skip(1).SelectMany(page => page), .. hostile.Skip(2).SelectMany(page => page)];
        TestAudioFiles.OggWriter.FixChecksums(mixed);

        var allocated = AllocatedByReading(Write("dwa-strumienie.opus", mixed));

        Assert.True(allocated < 4 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }

    /// <summary>
    /// Gdy za stroną nie leży następna, biblioteka bierze za treść strony wszystko aż do najbliższego
    /// znacznika. Komentarze schowane za stroną, która zapowiada zero bajtów, są więc dla niej widoczne.
    /// </summary>
    [Fact]
    public void KomentarzeZaStronaOZerowejDlugosciTezSaSprawdzane()
    {
        var pages = Pages(OpusWithPicture(0x7FFFFFC7));

        // Strona komentarzy: sam nagłówek bez segmentów, a treść pakietu zaraz za nim, luzem.
        var comments = pages[1];
        int segments = comments[26];
        var content = comments[(27 + segments)..];

        var empty = comments[..27];
        empty[26] = 0;

        byte[] file = [.. pages[0], .. empty, .. content, .. pages.Skip(2).SelectMany(page => page)];
        TestAudioFiles.OggWriter.FixChecksums(file);

        var allocated = AllocatedByReading(Write("zero.opus", file));

        Assert.True(allocated < 4 * 1024 * 1024, $"przydzielono {allocated / 1024} KB");
    }
}
