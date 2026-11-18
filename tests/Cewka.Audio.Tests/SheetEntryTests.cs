using Cewka.Audio.Decoding;
using Cewka.Audio.Metadata;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Wpis kolejki z utworem arkusza: dekoder wycinka, znaczniki i tożsamość albumu.</summary>
public sealed class SheetEntryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-arkusz-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const int Rate = 44100;

    private static SheetTrack Track(int number, long start, long? end, string sheet = "a.cue") =>
        new() { SheetPath = sheet, Number = number, Start = start, End = end };

    /// <summary>Plik, w którym wartość próbki to numer ramki; z wyniku widać, które miejsce zagrało.</summary>
    private static byte[] RampWav(int frames)
    {
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++) data[2 * i] = data[2 * i + 1] = i / (float)(1 << 20);
        return TestAudioFiles.Wav(data, 2, Rate);
    }

    private static IAudioDecoder Wav(byte[] file) =>
        DecoderFactory.Open(new MemoryStream(file), AudioFileFormat.Wav, ownsStream: true, Rate, 2);

    private static long FrameOf(float sample) => (long)Math.Round(sample * (1 << 20));

    [Fact]
    public void WycinekMaWlasnaDlugoscPozycjeIPrzewijanie()
    {
        // 75 klatek arkusza = sekunda = 44 100 ramek.
        var file = RampWav(Rate * 4);
        using var slice = new SliceDecoder(Wav(file), "a.wav", Track(2, 75, 150));

        Assert.Equal(Rate, slice.TotalFrames);
        Assert.Equal(0, slice.Position);

        var buffer = new float[1000 * 2];
        Assert.Equal(1000, slice.Read(buffer));
        Assert.Equal(Rate, FrameOf(buffer[0]));
        Assert.Equal(1000, slice.Position);

        slice.Seek(Rate - 10);
        Assert.Equal(Rate - 10, slice.Position);
        Assert.Equal(10, slice.Read(buffer));
        Assert.Equal(2 * Rate - 1, FrameOf(buffer[18]));
        Assert.Equal(0, slice.Read(buffer));

        slice.Seek(10 * Rate);
        Assert.Equal(0, slice.Read(buffer));
        Assert.Equal(Rate, slice.Position);
    }

    [Fact]
    public void OstatniWycinekGraDoKoncaPliku()
    {
        using var slice = new SliceDecoder(Wav(RampWav(Rate * 3)), "a.wav", Track(3, 150, null));

        Assert.Equal(Rate, slice.TotalFrames);

        var buffer = new float[Rate * 4];
        var total = 0;
        int frames;
        while ((frames = slice.Read(buffer)) > 0) total += frames;
        Assert.Equal(Rate, total);
    }

    [Fact]
    public void NastepnyWycinekTegoSamegoPlikuGraZTegoSamegoDekodera()
    {
        using var slice = new SliceDecoder(Wav(RampWav(Rate * 3)), "a.wav", Track(1, 0, 75));
        var buffer = new float[Rate * 4];

        Assert.False(slice.TryAdvance("a.wav", Track(2, 75, 150)), "dekoder nie doszedł jeszcze do końca wycinka");

        while (slice.Read(buffer) > 0) { }

        Assert.False(slice.TryAdvance("b.wav", Track(2, 75, 150)));
        Assert.False(slice.TryAdvance("a.wav", Track(3, 150, null)));
        Assert.True(slice.TryAdvance("a.wav", Track(2, 75, 150)));

        Assert.Equal(0, slice.Position);
        Assert.Equal(Rate, slice.TotalFrames);
        Assert.Equal(100, slice.Read(buffer.AsSpan(0, 200)));
        Assert.Equal(Rate, FrameOf(buffer[0]));
    }

    [Fact]
    public void ZnacznikiArkuszaWeWpisieKolejki()
    {
        var path = Path.Combine(_directory, "plyta.wav");
        File.WriteAllBytes(path, RampWav(Rate * 3));

        var entry = new QueueEntry { Path = path, Sheet = Track(2, 75, 150) with { Title = "Drugi", Performer = "Ktoś", Album = "Płyta" } };
        Assert.Equal("Drugi", entry.Title);

        entry.EnsureMetadata();

        Assert.Equal("Drugi", entry.Title);
        Assert.Equal("Ktoś", entry.Artist);
        Assert.Equal("Płyta", entry.Metadata!.Album);
        Assert.Equal(2, entry.Metadata.TrackNumber);
        Assert.Equal(TimeSpan.FromSeconds(1), entry.Duration);
        Assert.Equal(AudioFileFormat.Wav, entry.Format);
        Assert.NotEqual(path, entry.CacheKey);
        Assert.Equal(path, new QueueEntry { Path = path }.CacheKey);
        Assert.Equal(new QueueItem(path, entry.Sheet), entry.Item);
    }

    private static QueueEntry Entry(SheetTrack? sheet, string? album = "Album", string folder = "x")
    {
        var path = Path.Combine(Path.GetTempPath(), folder, "plyta.flac");
        var metadata = new TrackMetadata { Path = path, Title = "t", Album = album };
        return new QueueEntry { Path = path, Sheet = sheet, Metadata = sheet?.Apply(metadata) ?? metadata };
    }

    [Fact]
    public void UtworyJednegoArkuszaToJedenAlbum()
    {
        var first = Entry(Track(1, 0, 75));
        var second = Entry(Track(2, 75, 150));
        var third = Entry(Track(3, 150, null));

        Assert.True(AlbumIdentity.HasAlbum(Entry(Track(1, 0, 75), album: null)));
        Assert.True(AlbumIdentity.SameAlbum(first, third));
        Assert.True(AlbumIdentity.IsSuccessor(first, second));
        Assert.True(AlbumIdentity.IsSuccessor(second, third));
        Assert.False(AlbumIdentity.IsSuccessor(first, third));
        Assert.False(AlbumIdentity.IsSuccessor(second, first));

        // Inny arkusz z tym samym albumem i plik bez arkusza to już inny album.
        Assert.False(AlbumIdentity.SameAlbum(first, Entry(Track(2, 75, 150, sheet: "b.cue"))));
        Assert.False(AlbumIdentity.SameAlbum(first, Entry(null)));
        Assert.False(AlbumIdentity.SameAlbum(Entry(null), first));
    }
}
