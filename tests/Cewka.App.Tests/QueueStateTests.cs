using System.Text.Json;
using Cewka.App.Models;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Tests for the queue saved between runs, with the repeat mode written two different ways.
/// <para>
/// Tryb powtarzania był wcześniej zapisywany jako pojedyncza wartość logiczna, bo istniały tylko
/// dwa stany. Po dodaniu powtarzania jednego utworu format musiał się zmienić, a plik zapisany
/// przez starszą wersję nadal musi dać się odczytać — inaczej pierwsze uruchomienie po
/// aktualizacji po cichu gubiłoby ustawienie.
/// </para>
/// </summary>
public class QueueStateTests
{
    [Theory]
    [InlineData("None", RepeatMode.None)]
    [InlineData("Queue", RepeatMode.Queue)]
    [InlineData("Track", RepeatMode.Track)]
    public void NazwaTrybuJestOdczytywana(string zapisane, RepeatMode oczekiwane)
    {
        var state = new QueueState { Repeat = zapisane };
        Assert.Equal(oczekiwane, state.ReadRepeat());
    }

    [Theory]
    [InlineData(true, RepeatMode.Queue)]
    [InlineData(false, RepeatMode.None)]
    public void StarszyZapisLogicznyNadalDziala(bool repeatQueue, RepeatMode oczekiwane)
    {
        var state = new QueueState { Repeat = null, RepeatQueue = repeatQueue };
        Assert.Equal(oczekiwane, state.ReadRepeat());
    }

    [Fact]
    public void NieznanaNazwaSprowadzaSieDoBrakuPowtarzania()
    {
        var state = new QueueState { Repeat = "cokolwiek" };
        Assert.Equal(RepeatMode.None, state.ReadRepeat());
    }

    [Fact]
    public void PlikZapisanyPrzezStarszaWersjeDajeSieOdczytac()
    {
        const string starszy =
            """{"paths":["a.mp3"],"currentIndex":0,"positionSeconds":12.5,"shuffle":true,"repeatQueue":true}""";

        var state = JsonSerializer.Deserialize<QueueState>(
            starszy, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(state);
        Assert.Single(state.Paths);
        Assert.Equal(0, state.CurrentIndex);
        Assert.Equal(12.5, state.PositionSeconds, precision: 3);
        Assert.True(state.Shuffle);
        Assert.Equal(RepeatMode.Queue, state.ReadRepeat());
    }
}
