using Cewka.App.ViewModels;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Oznaczenie wiersza kolejki po nieudanym otwarciu albo odtworzeniu utworu.</summary>
public class QueueItemFailureTests
{
    private static QueueItemViewModel Row() => new()
    {
        Number = 1,
        Title = "utwór",
        Artist = "wykonawca",
        AlbumLine = string.Empty,
        Format = string.Empty,
        Path = "C:/muzyka/utwór.flac",
        Duration = "0:00",
    };

    /// <summary>Chwilowy błąd nie pokazuje opisu wyjątku i znika, gdy utwór w końcu zagra.</summary>
    [Theory]
    [InlineData(TrackFailureReason.FileUnavailable)]
    [InlineData(TrackFailureReason.PlaybackInterrupted)]
    [InlineData(TrackFailureReason.FileMissing)]
    public void TemporaryFailureClearsAfterPlaying(TrackFailureReason reason)
    {
        var row = Row();

        row.ShowFailure(reason, "The process cannot access the file", "Ogólny komunikat");

        Assert.True(row.IsUnsupported);
        Assert.Equal("Ogólny komunikat", row.UnsupportedReason);

        row.ClearTemporaryFailure();

        Assert.False(row.IsUnsupported);
        Assert.Equal(row.Path, row.Tooltip);
    }

    /// <summary>Format nieobsługiwany zostaje oznaczony na stałe, z opisem z wykrywania formatu.</summary>
    [Fact]
    public void UnsupportedFormatStaysMarked()
    {
        var row = Row();

        row.ShowFailure(TrackFailureReason.UnsupportedFormat, "Opis formatu", "Ogólny komunikat");
        row.ClearTemporaryFailure();

        Assert.True(row.IsUnsupported);
        Assert.Equal("Opis formatu", row.UnsupportedReason);
    }
}
