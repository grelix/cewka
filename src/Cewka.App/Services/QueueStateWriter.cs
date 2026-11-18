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
    private volatile bool _keepFile;

    /// <summary>
    /// Plik był przy starcie niedostępny, więc kolejka z niego nie wróciła; do końca sesji
    /// zostaje nietknięty, żeby pusta kolejka nie zastąpiła poprzedniej.
    /// </summary>
    public void KeepExistingFile() => _keepFile = true;

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

        Write(state, version, full: true);
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

            Write(state, version, full: false);
        }
    }

    /// <summary>Lista z ostatniego udanego zapisu pliku kolejki w tej sesji i jej numer.</summary>
    private QueueState? _savedList;

    /// <param name="full">
    /// Zapis przy zamknięciu: zawsze cały plik kolejki, żeby po zwykłym końcu programu był
    /// kompletny sam w sobie. Zapis w tle przy niezmienionej liście dotyka tylko pliku pozycji.
    /// </param>
    private void Write(QueueState state, long version, bool full)
    {
        lock (_file)
        {
            if (version <= _written || _keepFile) return;

            _written = version;

            // Plik kolejki musi nadal być tym, który zapisała ta kopia. Druga kopia programu
            // (wyłączona jedna kopia) albo ręczna podmiana zmieniają numer zapisu; pozycja dopisana
            // do cudzej listy byłaby przy odczycie odrzucona i unieważniłaby cudzą. Wtedy idzie
            // cały stan, tak jak przed wprowadzeniem pliku pozycji: obowiązuje ostatni piszący.
            if (!full && _savedList is { } saved && SameList(saved, state)
                && QueueStateStore.ReadRevision(path) == saved.Revision)
            {
                QueueStateStore.SavePosition(state, saved.Revision, path);
                return;
            }

            state.Revision = NextRevision(_savedList?.Revision ?? 0);
            _savedList = QueueStateStore.Save(state, path) ? state : null;
        }
    }

    /// <summary>Numer inny niż wszystkie wcześniejsze, także z poprzednich uruchomień.</summary>
    private static long NextRevision(long previous) => Math.Max(previous + 1, DateTime.UtcNow.Ticks);

    /// <summary>Wszystko poza miejscem odtwarzania jest takie samo.</summary>
    private static bool SameList(QueueState a, QueueState b)
    {
        if (a.Shuffle != b.Shuffle || a.RepeatQueue != b.RepeatQueue
            || !string.Equals(a.Repeat, b.Repeat, StringComparison.Ordinal)
            || !SamePaths(a.Paths, b.Paths) || !SameSheets(a.Sheets, b.Sheets))
            return false;

        var first = a.PendingOpens;
        var second = b.PendingOpens;
        if (first is null || second is null) return first is null && second is null;
        if (first.Count != second.Count) return false;

        for (var i = 0; i < first.Count; i++)
        {
            if (!string.Equals(first[i].Action, second[i].Action, StringComparison.Ordinal)
                || !SamePaths(first[i].Paths, second[i].Paths))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Utwory arkusza jednego pliku mają tę samą ścieżkę, więc przestawienie ich w kolejce widać
    /// dopiero w wycinkach.
    /// </summary>
    private static bool SameSheets(List<SheetEntryState>? a, List<SheetEntryState>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Index != b[i].Index || !Equals(a[i].ToTrack(), b[i].ToTrack())) return false;
        }

        return true;
    }

    private static bool SamePaths(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
