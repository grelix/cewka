using Cewka.Audio.Metadata;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Kiedy dwa wpisy kolejki należą do jednego albumu i kiedy jeden następuje po drugim.</summary>
public sealed class AlbumIdentityTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "Wykonawca", "Album");
    private static readonly string OtherFolder = Path.Combine(Path.GetTempPath(), "Składanka");

    internal static QueueEntry Entry(string name, string? album, string? albumArtist = null, int? track = null,
        int? disc = null, string? folder = null)
    {
        var path = Path.Combine(folder ?? Folder, name + ".flac");
        return new QueueEntry
        {
            Path = path,
            Metadata = new TrackMetadata
            {
                Path = path,
                Title = name,
                Album = album,
                AlbumArtist = albumArtist,
                TrackNumber = track,
                DiscNumber = disc,
            },
        };
    }

    [Fact]
    public void TenSamZnacznikIKatalogToJedenAlbum()
    {
        Assert.True(AlbumIdentity.SameAlbum(Entry("a", "Album"), Entry("b", "Album")));
        Assert.True(AlbumIdentity.HasAlbum(Entry("a", "Album")));
    }

    /// <summary>Wielkość liter i odstępy na brzegach nie rozdzielają albumu.</summary>
    [Fact]
    public void ZnacznikPorownywanyBezWielkosciLiterIPoPrzycieciu() =>
        Assert.True(AlbumIdentity.SameAlbum(Entry("a", "Lovely Fear"), Entry("b", "  lovely fear ")));

    [Fact]
    public void InnyZnacznikToInnyAlbum() =>
        Assert.False(AlbumIdentity.SameAlbum(Entry("a", "Album"), Entry("b", "Album 2")));

    /// <summary>Wpis bez znacznika albumu nie tworzy albumu, nawet z drugim takim samym.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BezZnacznikaNieMaAlbumu(string? album)
    {
        Assert.False(AlbumIdentity.HasAlbum(Entry("a", album)));
        Assert.False(AlbumIdentity.SameAlbum(Entry("a", album), Entry("b", album)));
        Assert.False(AlbumIdentity.SameAlbum(Entry("a", "Album"), Entry("b", album)));
    }

    /// <summary>Metadanych jeszcze nie odczytano: wpis nie należy do żadnego albumu.</summary>
    [Fact]
    public void WpisBezMetadanychNieNalezyDoAlbumu()
    {
        var unread = new QueueEntry { Path = Path.Combine(Folder, "c.flac") };

        Assert.False(AlbumIdentity.HasAlbum(unread));
        Assert.False(AlbumIdentity.SameAlbum(Entry("a", "Album"), unread));
        Assert.False(AlbumIdentity.SameAlbum(unread, Entry("a", "Album")));
    }

    /// <summary>Równy wykonawca albumu łączy pliki z różnych katalogów, na przykład płyty w podkatalogach.</summary>
    [Fact]
    public void WykonawcaAlbumuLaczyRozneKatalogi() =>
        Assert.True(AlbumIdentity.SameAlbum(
            Entry("a", "Album", "Zespół", folder: Folder),
            Entry("b", "album", "zespół", folder: OtherFolder)));

    [Fact]
    public void InnyWykonawcaAlbumuRozdzielaNawetWJednymKatalogu() =>
        Assert.False(AlbumIdentity.SameAlbum(Entry("a", "Greatest Hits", "Zespół"), Entry("b", "Greatest Hits", "Inny")));

    /// <summary>Bez wykonawcy albumu rozstrzyga katalog: „Greatest Hits” różnych zespołów to różne albumy.</summary>
    [Fact]
    public void BezWykonawcyAlbumuRozstrzygaKatalog()
    {
        Assert.False(AlbumIdentity.SameAlbum(
            Entry("a", "Greatest Hits", folder: Folder), Entry("b", "Greatest Hits", folder: OtherFolder)));
        Assert.True(AlbumIdentity.SameAlbum(Entry("a", "Album", "Zespół"), Entry("b", "Album")));
        Assert.False(AlbumIdentity.SameAlbum(
            Entry("a", "Album", "Zespół", folder: Folder), Entry("b", "Album", folder: OtherFolder)));
    }

    [Theory]
    [InlineData(3, null, 4, null, true)]
    [InlineData(3, 1, 4, null, true)]
    [InlineData(3, null, 4, 1, true)]
    [InlineData(3, 2, 4, 2, true)]
    [InlineData(12, 1, 1, 2, true)]
    [InlineData(3, null, 5, null, false)]
    [InlineData(4, null, 3, null, false)]
    [InlineData(3, 1, 4, 2, false)]
    [InlineData(12, 1, 2, 2, false)]
    [InlineData(12, 2, 1, 1, false)]
    public void NastepcaToKolejnaSciezkaZUwzglednieniemPlyty(int track, int? disc, int nextTrack, int? nextDisc, bool expected) =>
        Assert.Equal(expected, AlbumIdentity.IsSuccessor(
            Entry("a", "Album", track: track, disc: disc), Entry("b", "Album", track: nextTrack, disc: nextDisc)));

    /// <summary>Album bez numerów ścieżek uznaje się za grany po kolei; numer tylko u jednego wpisu nie wystarcza.</summary>
    [Fact]
    public void BezNumerowUObuToNastepca()
    {
        Assert.True(AlbumIdentity.IsSuccessor(Entry("a", "Album"), Entry("b", "Album")));
        Assert.False(AlbumIdentity.IsSuccessor(Entry("a", "Album", track: 1), Entry("b", "Album")));
        Assert.False(AlbumIdentity.IsSuccessor(Entry("a", "Album"), Entry("b", "Album", track: 2)));
    }

    [Fact]
    public void InnyAlbumNieMaNastepcy()
    {
        Assert.False(AlbumIdentity.IsSuccessor(Entry("a", "Album", track: 1), Entry("b", "Inny", track: 2)));
        Assert.False(AlbumIdentity.IsSuccessor(Entry("a", null), Entry("b", null)));
    }
}
