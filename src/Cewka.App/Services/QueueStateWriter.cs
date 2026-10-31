using Cewka.App.Models;

namespace Cewka.App.Services;

/// <summary>
/// Zapisuje stan kolejki poza wątkiem interfejsu. Zapis z wymuszeniem na dysk trwa przy długiej
/// kolejce i wolnym dysku dziesiątki milisekund, a w trakcie gry przypada co pół minuty, więc na
/// wątku interfejsu przycinałby animacje. Stan zamówiony w trakcie trwającego zapisu czeka,
/// a z kilku czekających zapisuje się tylko najnowszy.
/// </summary>
public sealed class QueueStateWriter(string path)
{
    private readonly object _gate = new();
    private readonly object _file = new();

    private QueueState? _pending;
    private long _pendingVersion;
    private long _version;
    private long _written;
    private bool _running;
    private Task _worker = Task.CompletedTask;

    /// <summary>Zamawia zapis w tle; zadanie kończy się, gdy nie czeka już żaden stan.</summary>
    public Task SaveInBackground(QueueState state)
    {
        lock (_gate)
        {
            _pending = state;
            _pendingVersion = ++_version;

            if (!_running)
            {
                _running = true;
                _worker = Task.Run(Drain);
            }

            return _worker;
        }
    }

    /// <summary>
    /// Zapisuje od razu, przy zamykaniu programu. Zapis z tła, który jeszcze trwa, nie nadpisze
    /// potem pliku starszym stanem.
    /// </summary>
    public void SaveNow(QueueState state)
    {
        long version;
        lock (_gate)
        {
            _pending = null;
            version = ++_version;
        }

        Write(state, version);
    }

    private void Drain()
    {
        while (true)
        {
            QueueState state;
            long version;

            lock (_gate)
            {
                if (_pending is null)
                {
                    _running = false;
                    return;
                }

                state = _pending;
                version = _pendingVersion;
                _pending = null;
            }

            Write(state, version);
        }
    }

    private void Write(QueueState state, long version)
    {
        lock (_file)
        {
            if (version <= _written) return;

            _written = version;
            QueueStateStore.Save(state, path);
        }
    }
}
