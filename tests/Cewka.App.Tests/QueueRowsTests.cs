using Cewka.App.Localisation;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Wiersze kolejki przy przesuwaniu, usuwaniu i dodawaniu folderów.</summary>
[Collection(UiCollection.Name)]
public sealed class QueueRowsTests
{
    [Fact]
    public void PrzesuniecieNiePrzebudowujeWierszy() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));
        var rows = h.Player.Queue.ToArray();

        h.Player.MoveInQueue(0, 2);

        Assert.Same(rows[0], h.Player.Queue[2]);
        Assert.Same(rows[1], h.Player.Queue[0]);
        Assert.Equal([1, 2, 3], h.Player.Queue.Select(row => row.Number));
        Assert.True(h.Player.Queue[2].IsCurrent);
        Assert.Equal(2, h.Player.CurrentIndex);
    });

    [Fact]
    public void UsuniecieNiePrzebudowujePozostalychWierszy() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));
        var rows = h.Player.Queue.ToArray();

        h.Player.RemoveFromQueue(rows[1]);

        Assert.Equal([rows[0], rows[2]], h.Player.Queue);
        Assert.Equal([1, 2], h.Player.Queue.Select(row => row.Number));
        Assert.True(rows[0].IsCurrent);
    });

    [Fact]
    public void UsuniecieOstatniegoWierszaBezOdtwarzaniaZmieniaTytul() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a")], replace: false);
        Assert.Equal(Strings.Current["NothingPlaying"], h.Player.Title);

        h.Player.RemoveFromQueue(h.Player.Queue[0]);

        Assert.Equal(Strings.Current["NoTrack"], h.Player.Title);
    });

    [Fact]
    public void FolderDodawanyWTleWPorzadkuNaturalnym() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var folder = Path.Combine(h.Directory, "album");
        Directory.CreateDirectory(Path.Combine(folder, "CD2"));
        foreach (var name in (string[])["10 - x.wav", "2 - y.wav", "1 - z.wav", Path.Combine("CD2", "1 - w.wav")])
            File.Copy(h.Track("wzor" + name.Length), Path.Combine(folder, name));
        File.WriteAllText(Path.Combine(folder, "okladka.txt"), "x");

        var task = h.Player.OpenPaths([folder], replace: false);

        Assert.True(Ui.WaitUntil(() => task.IsCompleted));
        Assert.Equal(
            ["1 - z.wav", "2 - y.wav", "10 - x.wav", "1 - w.wav"],
            h.Player.Queue.Select(row => Path.GetFileName(row.Path)));
    });

    [Theory]
    [InlineData("2 - y", "10 - x", -1)]
    [InlineData("track 9", "Track 10", -1)]
    [InlineData("b", "A", 1)]
    [InlineData("01", "1", -1)]
    [InlineData("same", "same", 0)]
    public void PorzadekNaturalny(string x, string y, int sign) =>
        Assert.Equal(sign, Math.Sign(NaturalOrder.Instance.Compare(x, y)));

    [Theory]
    [InlineData(59, false, "0:59")]
    [InlineData(600, false, "10:00")]
    [InlineData(3600, false, "1:00:00")]
    [InlineData(36000 + 61, false, "10:01:01")]
    [InlineData(59, true, "0:00:59")]
    public void CzasOdGodzinyZGodzinami(double seconds, bool hours, string expected) =>
        Assert.Equal(expected, MainViewModel.FormatTime(seconds, hours));
}
