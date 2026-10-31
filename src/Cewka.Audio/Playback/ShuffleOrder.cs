namespace Cewka.Audio.Playback;

/// <summary>
/// Kolejność odtwarzania losowego: jedna permutacja kolejki, po której chodzi się w przód
/// i w tył. Dzięki niej „Poprzedni” wraca do utworu granego wcześniej, a każdy utwór gra
/// raz, zanim którykolwiek zagra ponownie.
///
/// <para>Wpisy rozpoznaje się po tożsamości obiektu, nie po numerze w kolejce: przestawienie
/// albo usunięcie wiersza nie może rozjechać kolejności.</para>
/// </summary>
internal sealed class ShuffleOrder
{
    private readonly List<QueueEntry> _order = [];
    private readonly Random _random;

    public ShuffleOrder(Random? random = null) => _random = random ?? Random.Shared;

    public IReadOnlyList<QueueEntry> Order => _order;

    /// <summary>Nowa permutacja całej kolejki; <paramref name="first"/>, o ile jest w kolejce, staje na początku.</summary>
    public void Reset(IReadOnlyList<QueueEntry> queue, QueueEntry? first)
    {
        _order.Clear();
        _order.AddRange(queue);

        // Fisher–Yates.
        for (var i = _order.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }

        if (first is not null && _order.Remove(first)) _order.Insert(0, first);
    }

    public void Clear() => _order.Clear();

    /// <summary>Nowe wpisy trafiają w losowe miejsca za <paramref name="after"/>, żeby nie zagrały przed już zaplanowanymi wstecz.</summary>
    public void Insert(IEnumerable<QueueEntry> entries, QueueEntry? after)
    {
        var floor = after is null ? 0 : _order.IndexOf(after) + 1;

        foreach (var entry in entries)
        {
            var position = _random.Next(floor, _order.Count + 1);
            _order.Insert(position, entry);
        }
    }

    public void Remove(QueueEntry entry) => _order.Remove(entry);

    /// <summary>
    /// Stawia wpis zaraz za <paramref name="anchor"/>. Utwór wybrany ręcznie gra wtedy jako
    /// następny w permutacji, więc „Poprzedni” wraca do tego, co grało przed nim, a reszta
    /// kolejności zostaje nienaruszona.
    /// </summary>
    public void MoveAfter(QueueEntry entry, QueueEntry? anchor)
    {
        if (ReferenceEquals(entry, anchor)) return;
        if (!_order.Remove(entry)) return;

        var position = anchor is null ? 0 : _order.IndexOf(anchor) + 1;
        _order.Insert(position, entry);
    }

    /// <summary>
    /// Wpis po <paramref name="from"/>. Na końcu permutacji: przy <paramref name="wrap"/> nowa
    /// permutacja, której pierwszy wpis różni się od ostatnio granego, w przeciwnym razie null.
    /// </summary>
    public QueueEntry? Next(QueueEntry? from, bool wrap, IReadOnlyList<QueueEntry> queue)
    {
        EnsureMatches(queue, from);
        if (_order.Count == 0) return null;

        var position = from is null ? -1 : _order.IndexOf(from);
        if (position + 1 < _order.Count) return _order[position + 1];
        if (!wrap) return null;

        Reset(queue, null);
        if (_order.Count > 1 && ReferenceEquals(_order[0], from))
            (_order[0], _order[^1]) = (_order[^1], _order[0]);

        return _order[0];
    }

    /// <summary>Wpis przed <paramref name="from"/>; na początku permutacji przy <paramref name="wrap"/> ostatni, inaczej null.</summary>
    public QueueEntry? Previous(QueueEntry? from, bool wrap, IReadOnlyList<QueueEntry> queue)
    {
        EnsureMatches(queue, from);
        if (_order.Count == 0) return null;

        var position = from is null ? -1 : _order.IndexOf(from);
        if (position > 0) return _order[position - 1];

        return wrap ? _order[^1] : null;
    }

    /// <summary>Permutacja budowana, gdy losowanie było wyłączone w chwili zmian kolejki, może nie pasować.</summary>
    private void EnsureMatches(IReadOnlyList<QueueEntry> queue, QueueEntry? current)
    {
        if (_order.Count == queue.Count) return;
        Reset(queue, current);
    }
}
