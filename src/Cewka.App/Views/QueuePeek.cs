using Cewka.App.Models;

namespace Cewka.App.Views;

/// <summary>
/// Stan wysuwanego (nieprzypiętego) panelu kolejki i stacji: kiedy wysuwa się z uchwytu i kiedy chowa.
/// Bez interfejsu i z zegarem podawanym z zewnątrz, więc testy przesuwają czas same.
/// <para>
/// Panel wysuwa się, gdy kursor stoi w strefie uchwytu przez <see cref="RevealDelay"/>; ruch w strefie
/// jest dozwolony, a wyjście z niej albo naciśnięcie przycisku kasuje odliczanie. Wysunięty zostaje,
/// dopóki kursor jest nad nim albo nad uchwytem. Po zjechaniu chowa się po tolerancji
/// <see cref="QueuePanelTimes.Grace"/> i czasie <see cref="HideDelay"/>; powrót kursora wcześniej nic
/// nie zmienia. Blokada (otwarte menu, przeciąganie, fokus klawiatury, przeciągane pliki, nieaktywne
/// okno) wstrzymuje chowanie, a po jej ustaniu czas liczy się od początku.
/// </para>
/// </summary>
internal sealed class QueuePeek
{
    /// <summary>Przyczyny, dla których wysunięty panel nie może się schować.</summary>
    [Flags]
    public enum Hold
    {
        None = 0,

        /// <summary>Otwarte menu kontekstowe albo rozwinięta lista w panelu.</summary>
        Menu = 1,

        /// <summary>Przycisk wciśnięty nad panelem: przeciąganie wiersza, paska przewijania albo krawędzi.</summary>
        Pointer = 2,

        /// <summary>Fokus klawiatury albo pole tekstowe w panelu.</summary>
        Focus = 4,

        /// <summary>Nad oknem są przeciągane pliki; także nie pozwala panelowi się wysunąć.</summary>
        DragOver = 8,

        /// <summary>Okno nieaktywne (okno wyboru plików, ustawienia, korektor); także nie pozwala się wysunąć.</summary>
        Inactive = 16,
    }

    private readonly Func<TimeSpan> _clock;

    private bool _overHandle;
    private bool _overPanel;

    /// <summary>W strefie uchwytu padło naciśnięcie albo panel zamknięto: do wyjścia ze strefy nie wysuwa się.</summary>
    private bool _handleSpent;

    private TimeSpan? _openAt;
    private TimeSpan? _hideAt;
    private Hold _holds;
    private bool _enabled;

    public QueuePeek(Func<TimeSpan> clock)
    {
        _clock = clock;
    }

    /// <summary>Zmiana <see cref="IsShown"/>.</summary>
    public event Action? Changed;

    /// <summary>Zmiana <see cref="NextDeadline"/>: zegar okna trzeba ustawić od nowa.</summary>
    public event Action? DeadlineChanged;

    /// <summary>
    /// Wysuwanie po najechaniu jest możliwe: panel nieprzypięty, ustawione wysuwanie po najechaniu
    /// i scena zamknięta. Wyłączenie chowa podgląd od razu.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Evaluate();
        }
    }

    /// <summary>Zwłoka między najechaniem na uchwyt a wysunięciem.</summary>
    public TimeSpan RevealDelay { get; set; } = QueuePanelTimes.RevealDelay(QueueRevealDelay.Normal);

    /// <summary>Czas chowania po tolerancji; <c>null</c>, gdy panel nie chowa się sam.</summary>
    public TimeSpan? HideDelay { get; set; } = QueuePanelTimes.HideDelay(QueueHideDelay.TwoSeconds);

    /// <summary>Panel jest wysunięty jako podgląd.</summary>
    public bool IsShown { get; private set; }

    /// <summary>Kursor jest nad panelem albo nad uchwytem.</summary>
    public bool PointerInside => _overHandle || _overPanel;

    public Hold Holds => _holds;

    /// <summary>Chwila wysunięcia albo schowania; nic, gdy żadne nie czeka.</summary>
    public TimeSpan? NextDeadline => IsShown ? _hideAt : _openAt;

    public void HandleEntered()
    {
        _overHandle = true;
        Evaluate();
    }

    public void HandleExited()
    {
        _overHandle = false;
        _handleSpent = false;
        Evaluate();
    }

    public void PanelEntered()
    {
        _overPanel = true;
        Evaluate();
    }

    public void PanelExited()
    {
        _overPanel = false;
        Evaluate();
    }

    /// <summary>Naciśnięcie w strefie uchwytu: kliknięcie przypina, więc odliczanie wysunięcia znika.</summary>
    public void HandlePressed()
    {
        _handleSpent = true;
        Evaluate();
    }

    public void SetHold(Hold hold, bool on)
    {
        var holds = on ? _holds | hold : _holds & ~hold;
        if (holds == _holds) return;

        _holds = holds;
        Evaluate();
    }

    /// <summary>Esc, klawisz L, scena albo inne okno: chowa podgląd od razu.</summary>
    public void Close()
    {
        if (_overHandle) _handleSpent = true;
        _overPanel = false;
        _openAt = null;
        _hideAt = null;
        SetShown(false);
        Refresh();
    }

    /// <summary>
    /// Wysuwa podgląd od razu: kursor wrócił nad chowający się panel albo panel odpięto spod kursora.
    /// </summary>
    public void Show()
    {
        if (!_enabled) return;

        _openAt = null;
        SetShown(true);
        Evaluate();
    }

    /// <summary>Wysuwa albo chowa panel, gdy jego czas minął. Woła to zegar okna w chwili <see cref="NextDeadline"/>.</summary>
    public void Update() => Evaluate();

    private void Evaluate()
    {
        var now = _clock();

        if (!_enabled)
        {
            _openAt = null;
            _hideAt = null;
            SetShown(false);
            Refresh();
            return;
        }

        if (!IsShown)
        {
            _hideAt = null;

            var mayOpen = _overHandle && !_handleSpent && (_holds & (Hold.DragOver | Hold.Inactive)) == 0;
            if (!mayOpen) _openAt = null;
            else
            {
                _openAt ??= now + RevealDelay;
                if (now >= _openAt) SetShown(true);
            }
        }

        if (IsShown)
        {
            _openAt = null;

            if (PointerInside || _holds != Hold.None || HideDelay is not { } delay) _hideAt = null;
            else
            {
                _hideAt ??= now + QueuePanelTimes.Grace + delay;
                if (now >= _hideAt)
                {
                    _hideAt = null;
                    SetShown(false);
                }
            }
        }

        Refresh();
    }

    private void SetShown(bool shown)
    {
        if (IsShown == shown) return;

        IsShown = shown;
        Changed?.Invoke();
    }

    private TimeSpan? _deadlineBefore;

    private void Refresh()
    {
        var deadline = NextDeadline;
        if (deadline == _deadlineBefore) return;

        _deadlineBefore = deadline;
        DeadlineChanged?.Invoke();
    }
}
