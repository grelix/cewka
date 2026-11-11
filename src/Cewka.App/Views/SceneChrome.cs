using Avalonia;

namespace Cewka.App.Views;

/// <summary>
/// Stan warstwy sterowania sceny: kiedy ją widać i kiedy gaśnie. Bez interfejsu i z zegarem
/// podawanym z zewnątrz, więc testy przesuwają czas same.
/// <para>
/// Jeden licznik ukrywania: każde wejście, także klawisz, liczy go od nowa. Licznik stoi przy
/// przeciąganiu paska, gdy pasek postępu jest w regulacji, przy otwartym rzędzie kolejki, bo to praca
/// z kolejką, której warstwa nie może przerwać zgaśnięciem, i gdy kursor jest nad warstwą, ale
/// tylko przy widocznym kursorze: po ruchu wskaźnika, kółku albo kliknięciu od jej odsłonięcia
/// i od ostatniego klawisza. Fokus
/// klawiatury warstwy nie trzyma: po chwili bez klawiszy gaśnie ona razem z nim.
/// </para>
/// </summary>
internal sealed class SceneChrome
{
    /// <summary>Warstwa gaśnie po tylu sekundach bez wejścia.</summary>
    public static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(4);

    /// <summary>W pauzie warstwa zostaje dłużej, potem też gaśnie.</summary>
    public static readonly TimeSpan PausedHideDelay = TimeSpan.FromSeconds(30);

    /// <summary>Ruch kursora mniejszy niż tyle jednostek sceny nie odsłania warstwy.</summary>
    public const double MoveThreshold = 8;

    private readonly Func<TimeSpan> _clock;
    private TimeSpan _hideAt;

    /// <summary>Punkt, od którego liczy się próg ruchu: odsłonięcie, zgaszenie warstwy albo klawisz.</summary>
    private Point? _pointer;

    /// <summary>Ostatnie położenie wskaźnika, zapamiętywane przy każdym zdarzeniu.</summary>
    private Point? _lastPointer;
    private bool _pointerOver;

    /// <summary>Wskaźnik ruszył się, kręciło się kółko albo padło kliknięcie od odsłonięcia warstwy i od ostatniego klawisza.</summary>
    private bool _pointerMoved;
    private bool _dragging;
    private bool _adjusting;
    private bool _queueRow;

    public SceneChrome(Func<TimeSpan> clock)
    {
        _clock = clock;
    }

    /// <summary>Zmiana czegokolwiek, co widać.</summary>
    public event Action? Changed;

    /// <summary>Zmiana <see cref="NextDeadline"/>: zegar sceny trzeba ustawić od nowa.</summary>
    public event Action? DeadlineChanged;

    /// <summary>Czas do zgaszenia warstwy przy graniu; testy z prawdziwym zegarem go skracają.</summary>
    internal TimeSpan HideAfter { get; set; } = HideDelay;

    /// <summary>Czas do zgaszenia warstwy w pauzie.</summary>
    internal TimeSpan PausedHideAfter { get; set; } = PausedHideDelay;

    /// <summary>Warstwa sterowania jest widoczna.</summary>
    public bool IsShown { get; private set; }

    /// <summary>Fokus jest w warstwie z klawiatury: strzałki przenoszą go po przyciskach.</summary>
    public bool HasKeyboardFocus { get; private set; }

    /// <summary>Pasek postępu przyjmuje strzałki jako przewijanie.</summary>
    public bool IsAdjusting => _adjusting;

    public bool IsPaused { get; private set; }

    /// <summary>
    /// Kursor widać: warstwa jest widoczna, a od jej odsłonięcia i od ostatniego klawisza wskaźnik
    /// się ruszył, kręciło się kółko albo padło kliknięcie. Odsłonięcie klawiszem i pauzą zostawia
    /// kursor ukryty.
    /// </summary>
    public bool IsCursorShown => IsShown && _pointerMoved;

    // Najechanie liczy się tylko przy widocznym kursorze, więc nieruchomy, niewidoczny kursor, nad
    // którym warstwa wyrosła po klawiszu albo pauzie, jej nie trzyma.
    private bool Held => _pointerOver || _dragging || _adjusting || _queueRow;

    private TimeSpan Delay => IsPaused ? PausedHideAfter : HideAfter;

    /// <summary>Chwila, w której warstwa zgaśnie; nic, gdy jest ukryta albo przytrzymana.</summary>
    public TimeSpan? NextDeadline => IsShown && !Held ? _hideAt : null;

    /// <summary>Stan przy wejściu do sceny: warstwa ukryta.</summary>
    public void Reset(bool playing)
    {
        IsShown = false;
        HasKeyboardFocus = false;
        IsPaused = !playing;
        _pointer = null;
        _lastPointer = null;
        _pointerOver = false;
        _pointerMoved = false;
        _dragging = false;
        _adjusting = false;
        _queueRow = false;
        Refresh();
    }

    /// <summary>Pauza odsłania warstwę i daje jej więcej czasu; wznowienie liczy zwykły czas.</summary>
    public void PlayingChanged(bool playing)
    {
        if (IsPaused == !playing) return;

        IsPaused = !playing;
        if (IsPaused) IsShown = true;
        Restart();
        Refresh();
    }

    /// <summary>
    /// Ruch kursora w jednostkach sceny. Pierwsze zdarzenie tylko zapamiętuje położenie, a ruch
    /// poniżej progu (także zdarzenie po zmianie układu bez przesunięcia) niczego nie odsłania.
    /// Zgaszenie warstwy i klawisz liczą próg od nowa od ostatniego położenia.
    /// </summary>
    public void PointerMoved(Point position)
    {
        _lastPointer = position;
        if (_pointer is not { } last)
        {
            _pointer = position;
            return;
        }

        var dx = position.X - last.X;
        var dy = position.Y - last.Y;
        if (dx * dx + dy * dy <= MoveThreshold * MoveThreshold) return;

        _pointer = position;
        RevealByPointer();
    }

    /// <summary>
    /// Zapomina położenie wskaźnika, np. po zmianie skali kadru, w którego jednostkach jest liczone:
    /// następne zdarzenie znów tylko je zapamięta.
    /// </summary>
    public void ForgetPointer() => _pointer = _lastPointer = null;

    /// <summary>
    /// Naciśnięcie przycisku myszy. Przy ukrytym kursorze (także przy warstwie odsłoniętej klawiszem
    /// albo pauzą) tylko odsłania warstwę i kursor: wynik <c>true</c> oznacza, że kliknięcie nie może
    /// dotrzeć do niczego pod spodem.
    /// </summary>
    public bool PointerPressed()
    {
        if (IsCursorShown)
        {
            Restart();
            Refresh();
            return false;
        }

        RevealByPointer();
        return true;
    }

    /// <summary>
    /// Stuknięcie dotykiem albo piórem. Przy ukrytej warstwie tylko ją odsłania, bez kursora myszy:
    /// wynik <c>true</c> oznacza, że stuknięcie nie może dotrzeć do niczego pod spodem. Przy
    /// widocznej warstwie liczy licznik od nowa i przechodzi do jej części.
    /// </summary>
    public bool TouchPressed()
    {
        var hidden = !IsShown;
        IsShown = true;
        Restart();
        Refresh();
        return hidden;
    }

    /// <summary>
    /// Ruch wskaźnika, kółko albo kliknięcie: odsłania warstwę i kursor. Odsłonięcie myszą zostawia
    /// fokus poza warstwą, a trącenie myszy przy widocznej warstwie nie zdejmuje trybu klawiatury:
    /// kończy go dopiero fokus przeniesiony myszą.
    /// </summary>
    public void RevealByPointer()
    {
        IsShown = true;
        _pointerMoved = true;
        Restart();
        Refresh();
    }

    public void RevealByKeyboard()
    {
        IsShown = true;
        HasKeyboardFocus = true;
        Restart();
        Refresh();
    }

    /// <summary>
    /// Każdy klawisz liczy licznik od nowa i chowa kursor: nieruchomy kursor nad warstwą przestaje ją
    /// trzymać, dopóki wskaźnik znów się nie ruszy albo nie padnie kółko lub kliknięcie.
    /// </summary>
    public void KeyPressed()
    {
        if (!IsShown) return;
        _pointerMoved = false;
        _pointer = _lastPointer;
        Restart();
        Refresh();
    }

    public void SetPointerOver(bool over) => SetHold(ref _pointerOver, over && IsCursorShown);

    public void SetDragging(bool dragging) => SetHold(ref _dragging, dragging);

    public void SetAdjusting(bool adjusting) => SetHold(ref _adjusting, adjusting);

    /// <summary>
    /// Otwarty rząd kolejki odsłania warstwę i trzyma ją bez licznika, więc w pauzie nic nie budzi
    /// sceny. Po zamknięciu rzędu licznik biegnie od początku.
    /// </summary>
    public void SetQueueRowOpen(bool open)
    {
        if (open) IsShown = true;
        SetHold(ref _queueRow, open);
    }

    /// <summary>Fokus opuścił warstwę (np. kliknięcie myszą obok): koniec poziomu klawiatury.</summary>
    public void KeyboardFocusLeft()
    {
        if (!HasKeyboardFocus) return;

        HasKeyboardFocus = false;
        Restart();
        Refresh();
    }

    /// <summary>Gasi warstwę, gdy jej czas minął. Woła to zegar sceny w chwili <see cref="NextDeadline"/>.</summary>
    public void Update() => Refresh();

    private void SetHold(ref bool field, bool value)
    {
        if (field == value) return;

        field = value;

        // Po zwolnieniu licznik biegnie od początku, a nie od chwili sprzed przytrzymania.
        if (!value) Restart();
        Refresh();
    }

    private void Restart() => _hideAt = _clock() + Delay;

    private void Refresh()
    {
        if (IsShown && !Held && _clock() >= _hideAt)
        {
            IsShown = false;
            HasKeyboardFocus = false;
            _pointerMoved = false;
            _pointer = _lastPointer;
        }

        // Ukryty kursor nie jest nad niczym.
        if (!IsCursorShown) _pointerOver = false;

        // Odsłonięcie i ukrycie z metod wyżej też tu trafia, bo każda kończy się wywołaniem Refresh.
        if (_shownBefore != IsShown || _keyboardBefore != HasKeyboardFocus || _adjustingBefore != _adjusting
            || _cursorBefore != IsCursorShown)
        {
            _shownBefore = IsShown;
            _keyboardBefore = HasKeyboardFocus;
            _adjustingBefore = _adjusting;
            _cursorBefore = IsCursorShown;
            Changed?.Invoke();
        }

        // Termin przesuwa się także bez widocznej zmiany: zwolnienie przytrzymania, klawisz,
        // wznowienie przy widocznej warstwie.
        var deadline = NextDeadline;
        if (deadline != _deadlineBefore)
        {
            _deadlineBefore = deadline;
            DeadlineChanged?.Invoke();
        }
    }

    private bool _shownBefore;
    private bool _keyboardBefore;
    private bool _adjustingBefore;
    private bool _cursorBefore;
    private TimeSpan? _deadlineBefore;
}
