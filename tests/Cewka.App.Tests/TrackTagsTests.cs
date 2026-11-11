using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Wykonawca i album bieżącego utworu w osobnych polach, obok wspólnego wiersza okna.</summary>
[Collection(UiCollection.Name)]
public sealed class TrackTagsTests
{
    [Fact]
    public void WykonawcaIAlbumPochodzaZTagow() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var raised = new List<string?>();
        h.Player.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.OpenPaths([Tagged(h, "a", artist: "Wykonawca", album: "Album")]);

        Assert.True(h.PumpUntil(() => h.Player.Artist == "Wykonawca"), $"wykonawca: „{h.Player.Artist}”");
        Assert.Equal("Album", h.Player.Album);
        Assert.True(h.Player.HasArtist);
        Assert.True(h.Player.HasAlbum);
        Assert.Equal("Wykonawca — Album", h.Player.ArtistLine);
        Assert.Contains(nameof(MainViewModel.Artist), raised);
        Assert.Contains(nameof(MainViewModel.Album), raised);
        Assert.Contains(nameof(MainViewModel.HasArtist), raised);
        Assert.Contains(nameof(MainViewModel.HasAlbum), raised);
    });

    [Fact]
    public void BezWykonawcyUtworuWystepujeWykonawcaAlbumu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();

        h.OpenPaths([Tagged(h, "a", albumArtist: "Wykonawca albumu")]);

        Assert.True(h.PumpUntil(() => h.Player.Artist == "Wykonawca albumu"), $"wykonawca: „{h.Player.Artist}”");
        Assert.Equal(string.Empty, h.Player.Album);
        Assert.False(h.Player.HasAlbum);
    });

    [Fact]
    public void UtworBezTagowCzysciWykonawceIAlbumPoprzedniego() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([Tagged(h, "a", artist: "Wykonawca", album: "Album"), h.Track("b")]);
        Assert.True(h.PumpUntil(() => h.Player.Artist == "Wykonawca"), $"wykonawca: „{h.Player.Artist}”");

        h.Player.Next();

        Assert.True(h.PumpUntil(() => h.Player.Title == "b"));
        Assert.Equal(string.Empty, h.Player.Artist);
        Assert.Equal(string.Empty, h.Player.Album);
        Assert.False(h.Player.HasArtist);
        Assert.False(h.Player.HasAlbum);
    });

    [Fact]
    public void UsuniecieGrajacegoUtworuCzysciWykonawceIAlbum() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([Tagged(h, "a", artist: "Wykonawca", album: "Album"), h.Track("b")]);
        Assert.True(h.PumpUntil(() => h.Player.Artist == "Wykonawca"), $"wykonawca: „{h.Player.Artist}”");

        h.Player.RemoveFromQueue(h.Player.Queue[0]);

        Assert.Equal(string.Empty, h.Player.Artist);
        Assert.Equal(string.Empty, h.Player.Album);
        Assert.False(h.Player.HasArtist);
        Assert.False(h.Player.HasAlbum);
    });

    /// <summary>Plik WAV z uprzęży z tagami dopisanymi przez ATL.</summary>
    private static string Tagged(PlayerHarness h, string name, string? artist = null, string? album = null, string? albumArtist = null)
    {
        var path = h.Track(name);
        var track = new ATL.Track(path);
        if (artist is not null) track.Artist = artist;
        if (album is not null) track.Album = album;
        if (albumArtist is not null) track.AlbumArtist = albumArtist;
        Assert.True(track.Save());
        return path;
    }
}
