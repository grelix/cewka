using System.Text.Json;
using Cewka.App.Models;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Kolejka zapisana między uruchomieniami, z trybem powtarzania w dwóch postaciach: tekstowej
/// i starszej, logicznej, która też musi się wczytać.
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

        // Ten sam kontekst, którym czyta QueueStateStore.
        var state = JsonSerializer.Deserialize(starszy, QueueStateJsonContext.Default.QueueState);

        Assert.NotNull(state);
        Assert.Single(state.Paths);
        Assert.Equal(0, state.CurrentIndex);
        Assert.Equal(12.5, state.PositionSeconds, precision: 3);
        Assert.True(state.Shuffle);
        Assert.Equal(RepeatMode.Queue, state.ReadRepeat());
    }
}
