using System.Text.Json;
using System.Text.Json.Serialization;
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
        Assert.Null(state.PendingOpens);
    }

    [Fact]
    public void KolejkaBezOczekujacychOtwarcNieZapisujePola()
    {
        var json = JsonSerializer.Serialize(new QueueState { Paths = ["a.mp3"] }, QueueStateJsonContext.Default.QueueState);

        Assert.DoesNotContain("pendingOpens", json);
    }

    /// <summary>
    /// Wersja 0.8.1 czyta kolejkę kontekstem o tych samych opcjach, bez pola otwarć oczekujących;
    /// nieznane pole ma pominąć, a nie odrzucić cały plik.
    /// </summary>
    [Fact]
    public void StarszaWersjaPomijaPoleOczekujacychOtwarc()
    {
        var json = JsonSerializer.Serialize(new QueueState
        {
            Paths = ["a.mp3"],
            CurrentIndex = 0,
            PendingOpens = [new PendingOpenState { Paths = ["/muzyka/album"], Action = "Outside" }],
        }, QueueStateJsonContext.Default.QueueState);

        Assert.Contains("pendingOpens", json);

        var old = JsonSerializer.Deserialize(json, OldQueueStateJsonContext.Default.OldQueueState);

        Assert.NotNull(old);
        Assert.Equal(["a.mp3"], old.Paths);
        Assert.Equal(0, old.CurrentIndex);
    }

    [Fact]
    public void OczekujaceOtwarciaWracajaZZapisuWKolejnosci()
    {
        var json = JsonSerializer.Serialize(new QueueState
        {
            PendingOpens =
            [
                new PendingOpenState { Paths = ["/muzyka/album"], Action = "Outside" },
                new PendingOpenState { Paths = ["/muzyka/a.mp3"], Action = "Add" },
            ],
        }, QueueStateJsonContext.Default.QueueState);

        var state = JsonSerializer.Deserialize(json, QueueStateJsonContext.Default.QueueState)!;

        Assert.Equal(["/muzyka/album", "/muzyka/a.mp3"], state.PendingOpens!.Select(open => open.Paths.Single()));
        Assert.Equal(["Outside", "Add"], state.PendingOpens!.Select(open => open.Action));
    }
}

/// <summary>Kształt kolejki z wersji 0.8.1.</summary>
public sealed class OldQueueState
{
    public List<string> Paths { get; set; } = [];
    public int CurrentIndex { get; set; } = -1;
    public double PositionSeconds { get; set; }
    public bool Shuffle { get; set; }
    public string? Repeat { get; set; }
    public bool RepeatQueue { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OldQueueState))]
internal sealed partial class OldQueueStateJsonContext : JsonSerializerContext;
