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

    /// <summary>
    /// Wpis, który zamknął poprzednią rundę, dopóki bieżąca nie ruszyła dalej niż o pierwszy
    /// wpis. Pytanie o następny po nim daje wtedy początek bieżącej rundy, a „Poprzedni”
    /// z tego początku wraca do niego.
    /// </summary>
    private QueueEntry? _closedRound;

    /// <summary>Następna runda ustalona przez podgląd; każda zmiana kolejności ją unieważnia.</summary>
    private List<QueueEntry>? _plannedRound;

    public ShuffleOrder(Random? random = null) => _random = random ?? Random.Shared;

    public IReadOnlyList<QueueEntry> Order => _order;

    /// <summary>Rośnie przy każdej zmianie kolejności; po nim widać, że migawka się zestarzała.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Kopia kolejności do pokazania, bez losowania i bez zmiany stanu. Wpis, który zamknął poprzednią
    /// rundę i nadal gra, stoi przed nową rundą, bo zaraz po nim zagra jej początek; przy
    /// <paramref name="wrap"/> wyłączonym kończy kolejność, bo kolejka skończy się na nim.
    /// </summary>
    public QueueEntry[] Snapshot(QueueEntry? current, bool wrap)
    {
        if (_closedRound is not { } closed || !ReferenceEquals(closed, current) || !_order.Contains(closed))
            return _order.ToArray();

        var result = new QueueEntry[_order.Count];
        var next = wrap ? 1 : 0;
        result[wrap ? 0 : result.Length - 1] = closed;

        foreach (var entry in _order)
            if (!ReferenceEquals(entry, closed)) result[next++] = entry;

        return result;
    }

    /// <summary>Nowa permutacja całej kolejki; <paramref name="first"/>, o ile jest w kolejce, staje na początku.</summary>
    public void Reset(IReadOnlyList<QueueEntry> queue, QueueEntry? first)
    {
        Version++;
        _closedRound = null;
        _plannedRound = null;
        _order.Clear();
        _order.AddRange(Shuffled(queue));

        if (first is not null && _order.Remove(first)) _order.Insert(0, first);
    }

    public void Clear()
    {
        Version++;
        _order.Clear();
        _closedRound = null;
        _plannedRound = null;
    }

    /// <summary>Permutacja metodą Fisher–Yates.</summary>
    private List<QueueEntry> Shuffled(IReadOnlyList<QueueEntry> queue)
    {
        var result = new List<QueueEntry>(queue);

        for (var i = result.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
    }

    /// <summary>Kolejna runda; zaczyna się od innego wpisu niż ten, który zamyka bieżącą.</summary>
    private List<QueueEntry> DrawRound(IReadOnlyList<QueueEntry> queue, QueueEntry? closing)
    {
        var round = Shuffled(queue);
        if (round.Count > 1 && ReferenceEquals(round[0], closing)) (round[0], round[^1]) = (round[^1], round[0]);
        return round;
    }

    /// <summary>
    /// Podgląd wpisu, który zagra po <paramref name="from"/>, z pominięciem tych, których nie da się
    /// zagrać. Niczego nie przesuwa: kolejna runda jest tylko planowana, a prawdziwe zawinięcie
    /// zużyje ten sam plan, więc zapowiedź zgadza się z przejściem, a „Poprzedni” działa jak bez niej.
    /// </summary>
    public QueueEntry? Preview(QueueEntry? from, bool wrap, IReadOnlyList<QueueEntry> queue, Func<QueueEntry, bool> playable)
    {
        EnsureMatches(queue, from);
        if (_order.Count == 0) return null;

        int start;
        if (_closedRound is not null && ReferenceEquals(from, _closedRound))
        {
            if (!wrap) return null;
            start = 0;
        }
        else
        {
            start = (from is null ? -1 : _order.IndexOf(from)) + 1;
        }

        for (var i = start; i < _order.Count; i++)
            if (playable(_order[i])) return _order[i];

        if (!wrap) return null;

        _plannedRound ??= DrawRound(queue, _order[^1]);
        return _plannedRound.FirstOrDefault(playable);
    }

    /// <summary>Nowe wpisy trafiają w losowe miejsca za <paramref name="after"/>, żeby nie zagrały przed już zaplanowanymi wstecz.</summary>
    public void Insert(IEnumerable<QueueEntry> entries, QueueEntry? after)
    {
        Version++;
        _plannedRound = null;
        var floor = after is null ? 0 : _order.IndexOf(after) + 1;

        foreach (var entry in entries)
        {
            var position = _random.Next(floor, _order.Count + 1);
            _order.Insert(position, entry);
        }
    }

    public void Remove(QueueEntry entry)
    {
        Version++;
        _plannedRound = null;
        _order.Remove(entry);
        if (ReferenceEquals(entry, _closedRound)) _closedRound = null;
    }

    /// <summary>
    /// Stawia wpis zaraz za <paramref name="anchor"/>. Utwór wybrany ręcznie gra wtedy jako
    /// następny w permutacji, więc „Poprzedni” wraca do tego, co grało przed nim, a reszta
    /// kolejności zostaje nienaruszona.
    /// </summary>
    public void MoveAfter(QueueEntry entry, QueueEntry? anchor)
    {
        if (ReferenceEquals(entry, anchor)) return;
        if (!_order.Remove(entry)) return;

        Version++;
        _closedRound = null;
        _plannedRound = null;

        var position = anchor is null ? 0 : _order.IndexOf(anchor) + 1;
        _order.Insert(position, entry);
    }

    /// <summary>
    /// Wpis po <paramref name="from"/>. Na końcu permutacji: przy <paramref name="wrap"/> nowa
    /// permutacja, której pierwszy wpis różni się od ostatnio granego, w przeciwnym razie null.
    /// Ponowne pytanie o wpis, który zamknął rundę, daje ten sam początek nowej, o ile wolno
    /// zawinąć; po wyłączeniu powtarzania kolejka kończy się na nim.
    /// </summary>
    public QueueEntry? Next(QueueEntry? from, bool wrap, IReadOnlyList<QueueEntry> queue)
    {
        EnsureMatches(queue, from);
        if (_order.Count == 0) return null;

        if (_closedRound is not null)
        {
            if (ReferenceEquals(from, _closedRound)) return wrap ? _order[0] : null;
            Version++;
            _closedRound = null;
        }

        var position = from is null ? -1 : _order.IndexOf(from);
        if (position + 1 < _order.Count) return _order[position + 1];
        if (!wrap) return null;

        var round = _plannedRound ?? DrawRound(queue, from);
        _plannedRound = null;
        _order.Clear();
        _order.AddRange(round);

        Version++;
        _closedRound = from;
        return _order[0];
    }

    /// <summary>
    /// Wpis przed <paramref name="from"/>. Na początku permutacji: wpis, który zamknął
    /// poprzednią rundę, a gdy takiej nie było, przy <paramref name="wrap"/> ostatni, inaczej null.
    /// </summary>
    public QueueEntry? Previous(QueueEntry? from, bool wrap, IReadOnlyList<QueueEntry> queue)
    {
        EnsureMatches(queue, from);
        if (_order.Count == 0) return null;

        var position = from is null ? -1 : _order.IndexOf(from);
        if (position > 0) return _order[position - 1];
        if (position == 0 && _closedRound is not null) return _closedRound;

        return wrap ? _order[^1] : null;
    }

    /// <summary>Permutacja budowana, gdy losowanie było wyłączone w chwili zmian kolejki, może nie pasować.</summary>
    private void EnsureMatches(IReadOnlyList<QueueEntry> queue, QueueEntry? current)
    {
        if (_order.Count == queue.Count) return;
        Reset(queue, current);
    }
}
