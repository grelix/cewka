using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Kolejność odtwarzania losowego.</summary>
public class ShuffleOrderTests
{
    private static List<QueueEntry> Queue(int count) =>
        Enumerable.Range(0, count).Select(i => new QueueEntry { Path = $"utwor-{i}.flac" }).ToList();

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
