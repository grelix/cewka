using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Kolejność odtwarzania losowego.</summary>
public class ShuffleOrderTests
{
    private static List<QueueEntry> Queue(int count) =>
        Enumerable.Range(0, count).Select(i => new QueueEntry { Path = $"utwor-{i}.flac" }).ToList();

    /// <summary>Podgląd przy dowolnym układzie błędnych wpisów zgadza się z prawdziwym przejściem i niczego nie zmienia.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void PreviewMatchesTheRealWalkAndChangesNothing(int seed)
    {
        var queue = Queue(6);
        var order = new ShuffleOrder(new Random(seed));
        order.Reset(queue, null);

        var picker = new Random(seed * 31 + 1);
        var from = order.Order[picker.Next(2, 6)];
        var others = queue.Where(entry => !ReferenceEquals(entry, from)).OrderBy(_ => picker.Next()).ToList();
        foreach (var entry in others.Take(picker.Next(0, 5))) entry.OpenFailure = TrackFailureReason.DamagedFile;

        bool Playable(QueueEntry entry) => entry.OpenFailure is null;

        var snapshot = order.Order.ToArray();
        var version = order.Version;
        var previous = order.Previous(from, wrap: true, queue);
        var predicted = order.Preview(from, wrap: true, queue, Playable);

        for (var i = 0; i < 4; i++) Assert.Same(predicted, order.Preview(from, wrap: true, queue, Playable));
        Assert.Equal(snapshot, order.Order);
        Assert.Equal(version, order.Version);
        Assert.Same(previous, order.Previous(from, wrap: true, queue));

        var next = order.Next(from, wrap: true, queue);
        for (var left = queue.Count; next is { OpenFailure: not null } && left > 0; left--)
            next = order.Next(next, wrap: true, queue);

        Assert.Same(predicted, next);
    }

    public static TheoryData<int> Seeds()
    {
        var seeds = new TheoryData<int>();
        for (var seed = 1; seed <= 40; seed++) seeds.Add(seed);
        return seeds;
    }

    [Fact]
    public void ChangingTheOrderDropsThePlannedRound()
    {
        var queue = Queue(4);
        var order = new ShuffleOrder(new Random(5));
        order.Reset(queue, null);
        var last = order.Order[^1];
        _ = order.Preview(last, wrap: true, queue, _ => true);

        var added = new QueueEntry { Path = "dodany.flac" };
        queue.Add(added);
        order.Insert([added], last);

        // Plan sprzed dodania nie zawierałby nowego wpisu.
        var position = order.Order.ToList().IndexOf(added);
        Assert.InRange(position, 0, 4);
        Assert.Equal(5, order.Order.Count);
        var wrapped = order.Next(order.Order[^1], wrap: true, queue);
        Assert.NotNull(wrapped);
        Assert.Contains(added, order.Order);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void EveryEntryOnceBeforeTheEnd(int seed)
    {
        var queue = Queue(8);
        var order = new ShuffleOrder(new Random(seed));
        order.Reset(queue, queue[3]);

        var played = new List<QueueEntry> { queue[3] };
        for (var next = order.Next(queue[3], wrap: false, queue); next is not null; next = order.Next(next, wrap: false, queue))
            played.Add(next);

        Assert.Equal(8, played.Count);
        Assert.Equal(8, played.Distinct().Count());
    }

    [Fact]
    public void PreviousWalksBack()
    {
        var queue = Queue(5);
        var order = new ShuffleOrder(new Random(3));
        order.Reset(queue, queue[0]);

        var second = order.Next(queue[0], wrap: false, queue)!;
        var third = order.Next(second, wrap: false, queue)!;

        Assert.Same(second, order.Previous(third, wrap: false, queue));
        Assert.Same(queue[0], order.Previous(second, wrap: false, queue));
        Assert.Null(order.Previous(queue[0], wrap: false, queue));
    }

    /// <summary>Nowa runda zaczyna się od innego utworu niż ostatnio grany.</summary>
    [Fact]
    public void WrapNeverRepeatsTheLastTrack()
    {
        var queue = Queue(3);
        for (var seed = 0; seed < 50; seed++)
        {
            var order = new ShuffleOrder(new Random(seed));
            order.Reset(queue, null);
            var last = order.Order[^1];

            Assert.NotSame(last, order.Next(last, wrap: true, queue));
        }
    }

    /// <summary>
    /// Pytanie o następny wpis na końcu rundy, zadane ponownie (sprawdzenie zapasu dekodera
    /// po zmianie kolejki), daje ten sam wpis, zamiast układać kolejną permutację.
    /// </summary>
    /// <summary>
    /// Runda zamknięta przy powtarzaniu kolejki, potem powtarzanie wyłączone: o wpis, który ją
    /// zamknął, bez zawijania nie ma następnika, a po włączeniu z powrotem jest ten sam początek.
    /// </summary>
    [Fact]
    public void AskingWithoutWrapAfterARoundClosedGivesNothing()
    {
        var queue = Queue(3);
        var order = new ShuffleOrder(new Random(5));
        order.Reset(queue, null);
        var last = order.Order[^1];

        var first = order.Next(last, wrap: true, queue);

        Assert.Null(order.Next(last, wrap: false, queue));
        Assert.Same(first, order.Next(last, wrap: true, queue));
    }

    [Fact]
    public void AskingAgainAtTheEndOfARoundGivesTheSameEntry()
    {
        var queue = Queue(6);
        for (var seed = 0; seed < 20; seed++)
        {
            var order = new ShuffleOrder(new Random(seed));
            order.Reset(queue, null);
            var last = order.Order[^1];

            var first = order.Next(last, wrap: true, queue);
            var again = order.Next(last, wrap: true, queue);

            Assert.Same(first, again);
            Assert.Same(first, order.Order[0]);
        }
    }

    /// <summary>„Poprzedni” na początku nowej rundy wraca do utworu, który zamknął poprzednią.</summary>
    [Fact]
    public void PreviousAtTheStartOfANewRoundReturnsTheLastOfTheOldOne()
    {
        var queue = Queue(5);
        var order = new ShuffleOrder(new Random(11));
        order.Reset(queue, null);
        var last = order.Order[^1];

        var first = order.Next(last, wrap: true, queue)!;

        Assert.Same(last, order.Previous(first, wrap: true, queue));

        // Dalej runda biegnie zwyczajnie.
        var second = order.Next(first, wrap: true, queue)!;
        Assert.Same(order.Order[1], second);
        Assert.Same(first, order.Previous(second, wrap: true, queue));
    }

    /// <summary>Utwór wybrany ręcznie trafia za bieżący; „Poprzedni” wraca z niego do bieżącego.</summary>
    [Fact]
    public void PickedEntryGoesRightAfterTheCurrentOne()
    {
        var queue = Queue(6);
        var order = new ShuffleOrder(new Random(9));
        order.Reset(queue, queue[0]);

        var picked = order.Order[4];
        order.MoveAfter(picked, queue[0]);

        Assert.Same(picked, order.Next(queue[0], wrap: false, queue));
        Assert.Same(queue[0], order.Previous(picked, wrap: false, queue));
    }

    [Fact]
    public void RemovedEntriesDropOutAndNewOnesComeLater()
    {
        var queue = Queue(4);
        var order = new ShuffleOrder(new Random(5));
        order.Reset(queue, queue[0]);

        var removed = order.Order[2];
        queue.Remove(removed);
        order.Remove(removed);

        var added = new QueueEntry { Path = "nowy.flac" };
        queue.Add(added);
        order.Insert([added], queue[0]);

        Assert.DoesNotContain(removed, order.Order);
        Assert.True(order.Order.ToList().IndexOf(added) > 0);
        Assert.Equal(queue.Count, order.Order.Count);
    }
}
