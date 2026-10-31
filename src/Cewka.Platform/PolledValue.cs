namespace Cewka.Platform;

/// <summary>
/// Wartość odczytywana w tle nie częściej niż co zadany czas. Właściwość oddaje od razu ostatnią
/// znaną odpowiedź, więc pytający nigdy nie czeka na wolne źródło, a zmianę widzi z opóźnieniem
/// jednego cyklu.
/// </summary>
internal sealed class PolledValue
{
    private readonly Func<bool?> _read;
    private readonly TimeSpan _refresh;
    private readonly Func<DateTime> _clock;
    private readonly Action<Action> _schedule;
    private readonly object _gate = new();

    private bool _value;
    private DateTime _readAt = DateTime.MinValue;
    private bool _reading;

    /// <param name="read">Odczyt źródła; <c>null</c> albo wyjątek zostawia ostatnią znaną wartość.</param>
    public PolledValue(bool initial, Func<bool?> read, TimeSpan refresh,
                       Func<DateTime>? clock = null, Action<Action>? schedule = null)
    {
        _value = initial;
        _read = read;
        _refresh = refresh;
        _clock = clock ?? (() => DateTime.UtcNow);
        _schedule = schedule ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
    }

    public bool Value
    {
        get
        {
            lock (_gate)
            {
                if (!_reading && _clock() - _readAt >= _refresh)
                {
                    _reading = true;
                    _schedule(Refresh);
                }

                return _value;
            }
        }
    }

    private void Refresh()
    {
        bool? value = null;

        try
        {
            value = _read();
        }
        catch
        {
            // Źródło nieczytelne: zostaje ostatnia znana wartość.
        }

        lock (_gate)
        {
            if (value is { } known) _value = known;
            _readAt = _clock();
            _reading = false;
        }
    }
}
