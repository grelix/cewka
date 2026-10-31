using ATL;
using Cewka.Audio.Metadata;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Odczyt tagów i okładek na plikach Opus złożonych w teście i otagowanych przez ATL.</summary>
public sealed class MetadataTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-test-").FullName;

    /// <summary>Najmniejszy poprawny PNG (1 × 1 piksel); dopisany ogon odróżnia obrazy od siebie.</summary>
    private static byte[] Png(int tail = 0) =>
    [
        .. Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="),
        .. new byte[tail],
    ];

    private string Opus(string name, TestAudioFiles.OpusOptions? options = null)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, TestAudioFiles.Opus(options ?? new TestAudioFiles.OpusOptions { Frames = 10 }));
        return path;
    }

    private static void AddPictures(string path, params (byte[] Data, PictureInfo.PIC_TYPE Type)[] pictures)
    {
        var track = new Track(path);
        foreach (var (data, type) in pictures)
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(data, type));
        Assert.True(track.Save());
    }

    [Fact]
    public void FrontCoverIsPreferred()
    {
        var path = Opus("okladki.opus");
        AddPictures(path, (Png(10), PictureInfo.PIC_TYPE.Back), (Png(20), PictureInfo.PIC_TYPE.Front));

        Assert.Equal(Png(20), MetadataReader.ReadCover(path));
    }

    [Fact]
    public void FirstPictureWhenThereIsNoFrontCover()
    {
        var path = Opus("tyl.opus");
        AddPictures(path, (Png(10), PictureInfo.PIC_TYPE.Back));

        Assert.Equal(Png(10), MetadataReader.ReadCover(path));
    }

    [Fact]
    public void OversizedCoverIsSkipped()
    {
        var path = Opus("duza.opus");
        AddPictures(path, (Png(5000), PictureInfo.PIC_TYPE.Front));

        Assert.Null(MetadataReader.ReadCover(path, maxBytes: 1000));
        Assert.NotNull(MetadataReader.ReadCover(path));
    }

    [Fact]
    public void FileWithoutPicturesOrMissingFileGivesNoCover()
    {
        Assert.Null(MetadataReader.ReadCover(Opus("bez.opus")));
        Assert.Null(MetadataReader.ReadCover(Path.Combine(_directory, "nie-ma.opus")));
    }

    [Fact]
    public void TagsAreReadWithoutPictures()
    {
        var path = Opus("tagi.opus");
        var track = new Track(path) { Title = "Tytuł", Artist = "Wykonawca" };
        Assert.True(track.Save());
        AddPictures(path, (Png(), PictureInfo.PIC_TYPE.Front));

        var metadata = MetadataReader.Read(path);

        Assert.Equal("Tytuł", metadata.Title);
        Assert.Equal("Wykonawca", metadata.Artist);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
