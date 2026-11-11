using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Models;
using Cewka.App.ViewModels;

namespace Cewka.App.Views;

/// <summary>
/// Boczny panel kolejki i stacji: uchwyt przy prawej krawędzi, wysuwanie po najechaniu, przypinanie,
/// zmiana szerokości przeciąganiem krawędzi i stopka z poleceniami menu.
/// <para>
/// Przypięty panel stoi w kolumnie obok bloku odtwarzania, a w oknie za wąskim na kolumnę jako
/// nakładka z kliknięciem obok, które go zamyka. Nieprzypięty wysuwa się nad treść jako podgląd, bez
/// przesuwania treści, i chowa po zjechaniu kursorem (<see cref="QueuePeek"/>).
/// </para>
/// </summary>
public partial class MainWindow
{
    private readonly System.Diagnostics.Stopwatch _queueStopwatch = System.Diagnostics.Stopwatch.StartNew();

    private QueuePeek _peek = null!;
    private DispatcherTimer _queueTimer = null!;
    private DispatcherTimer _queueSlideTimer = null!;

    /// <summary>Zegar podglądu panelu; testy podstawiają własny i wołają <see cref="AdvanceQueueTimers"/>.</summary>
    internal Func<TimeSpan> QueueClock { get; set; } = null!;

    /// <summary>Panel jest przypięty i stoi w kolumnie obok bloku odtwarzania.</summary>
    private bool _queueAsColumn;

    /// <summary>Chowany podgląd jeszcze się wysuwa poza okno; do końca ruchu jest widoczny.</summary>
    private bool _queueSliding;

    /// <summary>Szerokość panelu w bieżącym układzie.</summary>
    private double _queuePanelWidth = 320;

    private Transitions? _queueSlideIn;
    private Transitions? _queueSlideOut;
    private Transitions? _queueFadeIn;
    private Transitions? _queueFadeOut;

    /// <summary>Licznik zdarzeń przeciągania plików; opuszczenie okna liczy się, gdy po nim nic nie przyszło.</summary>
    private int _dragOverStamp;

    /// <summary>Fokus sprzed otwarcia panelu klawiszem L, przywracany przy zamknięciu.</summary>
    private IInputElement? _focusBeforeQueue;

    private bool _resizingQueue;
    private double _resizeStartX;
    private double _resizeStartWidth;

    /// <summary>Panel jest widoczny: przypięty, wysunięty albo w trakcie chowania.</summary>
    internal bool QueuePanelVisible => QueueColumn.IsVisible;

    /// <summary>Panel jest wysunięty jako podgląd (nieprzypięty).</summary>
    internal bool QueuePeekShown => _peek.IsShown;

    internal QueuePeek Peek => _peek;

    /// <summary>Nakładana kolejka jest otwarta: przypięty panel w oknie za wąskim na kolumnę.</summary>
    internal bool QueueOverlayOpen => _viewModel.QueuePinned && !_queueAsColumn;

    /// <summary>Okno jest za wąskie na kolumnę: przypięty panel staje się nakładką.</summary>
    internal bool QueueIsOverlay => _queueAsOverlay;

    /// <summary>Szerokość panelu w bieżącym układzie.</summary>
    internal double QueuePanelShownWidth => _queuePanelWidth;

    private TranslateTransform QueueSlide => (TranslateTransform)QueueColumn.RenderTransform!;

    /// <summary>
    /// Nakładany panel stoi nad treścią, więc nie ma pod sobą tła okna i wyglądał jak jednolita płaszczyzna.
    /// Warstwa <c>QueueBackdropLayer</c> rysuje kopię fragmentu żywego tła spod panelu, a nad nią to samo
    /// przyciemnienie i szkło co przypięta kolumna. Wycinek liczy się z miejsca panelu w układzie, bez
    /// przesunięcia animacji, więc w trakcie wsuwania tło jedzie razem z panelem i staje równo na końcu.
    /// </summary>
    private void UpdateQueueBackdrop()
    {
        var overlay = QueueColumn.IsVisible && QueueColumn.Classes.Contains("queue-overlay");
        QueueBackdropLayer.IsVisible = overlay;
        if (!overlay) return;

        var size = QueueColumn.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (Body.TranslatePoint(QueueColumn.Bounds.TopLeft, Backdrop) is not { } origin) return;

        var source = new RelativeRect(origin.X, origin.Y, size.Width, size.Height, RelativeUnit.Absolute);
        if (QueueBackdropCopy.Background is VisualBrush current && current.SourceRect == source) return;

        QueueBackdropCopy.Background = new VisualBrush(Backdrop)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            SourceRect = source,
            DestinationRect = new RelativeRect(0, 0, size.Width, size.Height, RelativeUnit.Absolute),
        };
    }

    private void AttachQueuePanel()
    {
        // Wycinek tła pod nakładanym panelem idzie za jego miejscem i rozmiarem.
        QueueColumn.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == IsVisibleProperty) UpdateQueueBackdrop();
        };
        QueueColumn.Classes.CollectionChanged += (_, _) => UpdateQueueBackdrop();

        QueueClock = () => _queueStopwatch.Elapsed;
        _peek = new QueuePeek(() => QueueClock());
        _peek.Changed += OnPeekChanged;
        _peek.DeadlineChanged += ScheduleQueueTimer;

        _queueTimer = new DispatcherTimer();
        _queueTimer.Tick += (_, _) =>
        {
            _queueTimer.Stop();
            _peek.Update();
            ScheduleQueueTimer();
        };

        _queueSlideTimer = new DispatcherTimer { Interval = QueuePanelTimes.SlideOut };
        _queueSlideTimer.Tick += (_, _) => FinishQueueSlide();

        // Najechanie liczy się tylko dla myszy i pióra: dotyk nie ma stanu „nad”, więc dotknięcie
        // uchwytu przypina panel jak kliknięcie.
        QueueHandle.PointerEntered += (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Touch) _peek.HandleEntered();
        };
        QueueHandle.PointerExited += (_, _) => _peek.HandleExited();
        QueueHandle.AddHandler(PointerPressedEvent, OnQueueHandlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Bez ramki systemowej skrajne piksele uchwytu należą do krawędzi okna: tam kursor i naciśnięcie
        // zmieniają rozmiar okna, a nie przypinają panelu.
        QueueHandle.PointerMoved += (_, e) =>
            QueueHandle.Cursor = WindowResizeEdgeAt(e) is null ? HandCursor : EdgeCursor;

        QueueColumn.PointerEntered += (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Touch) OnQueuePanelEntered();
        };
        QueueColumn.PointerExited += (_, _) => _peek.PanelExited();

        // Przeciąganie wiersza, paska przewijania albo krawędzi trzyma panel do puszczenia przycisku,
        // także poza panelem.
        QueueColumn.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(QueueColumn).Properties.IsLeftButtonPressed) _peek.SetHold(QueuePeek.Hold.Pointer, true);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _peek.SetHold(QueuePeek.Hold.Pointer, false), RoutingStrategies.Tunnel, handledEventsToo: true);

        // Fokus klawiatury i pole tekstowe w panelu trzymają go; fokus z kliknięcia wiersza nie.
        QueueColumn.AddHandler(GotFocusEvent, (_, e) =>
            _peek.SetHold(QueuePeek.Hold.Focus, e.Source is TextBox || e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional),
            RoutingStrategies.Bubble, handledEventsToo: true);
        QueueColumn.AddHandler(LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!QueueColumn.IsKeyboardFocusWithin) _peek.SetHold(QueuePeek.Hold.Focus, false);
        }), RoutingStrategies.Bubble, handledEventsToo: true);

        // Menu kontekstowe i rozwinięte listy leżą w osobnych oknach, ale należą do panelu.
        QueueMenu.Opened += (_, _) => UpdateQueueMenuHold();
        QueueMenu.Closed += (_, _) => UpdateQueueMenuHold();
        RadioMenu.Opened += (_, _) => UpdateQueueMenuHold();
        RadioMenu.Closed += (_, _) => UpdateQueueMenuHold();
        RadioCountryBox.DropDownOpened += (_, _) => UpdateQueueMenuHold();
        RadioCountryBox.DropDownClosed += (_, _) => UpdateQueueMenuHold();
        RadioCategoryBox.DropDownOpened += (_, _) => UpdateQueueMenuHold();
        RadioCategoryBox.DropDownClosed += (_, _) => UpdateQueueMenuHold();

        // Pliki przeciągane nad oknem: najechanie na uchwyt nie wysuwa panelu, a wysunięty czeka do upuszczenia.
        AddHandler(DragDrop.DragEnterEvent, (_, e) => NoteFileDrag(e), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            var stamp = _dragOverStamp;
            Dispatcher.UIThread.Post(() =>
            {
                if (stamp == _dragOverStamp) _peek.SetHold(QueuePeek.Hold.DragOver, false);
            });
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // Okno nieaktywne (okno plików, ustawienia, korektor): liczniki stoją, po powrocie liczą od nowa.
        Activated += (_, _) => _peek.SetHold(QueuePeek.Hold.Inactive, false);
        Deactivated += (_, _) => _peek.SetHold(QueuePeek.Hold.Inactive, true);

        QueueResizeGrip.PointerPressed += OnQueueGripPressed;
        QueueResizeGrip.PointerMoved += OnQueueGripMoved;
        QueueResizeGrip.PointerReleased += (_, _) => EndQueueResize();
        QueueResizeGrip.PointerCaptureLost += (_, _) => EndQueueResize();

        UpdatePeekSettings();
    }

    // Kursory tworzone przy pierwszym użyciu: wymagają już działającej platformy.
    private static Cursor? _handCursor;
    private static Cursor? _edgeCursor;

    internal static Cursor HandCursor => _handCursor ??= new Cursor(StandardCursorType.Hand);

    internal static Cursor EdgeCursor => _edgeCursor ??= new Cursor(StandardCursorType.SizeWestEast);

    /// <summary>Krawędź okna pod wskaźnikiem, gdy okno bez ramki systemowej daje się w tym miejscu rozciągać.</summary>
    private WindowEdge? WindowResizeEdgeAt(PointerEventArgs e) =>
        !_systemFrame && WindowState == WindowState.Normal ? TryGetResizeEdge(e.GetPosition(this)) : null;

    private void OnQueueHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowResizeEdgeAt(e) is { } edge)
        {
            BeginResizeDrag(edge, e);
            e.Handled = true;
            return;
        }

        _peek.HandlePressed();
    }

    /// <summary>Kursor wszedł nad panel; nad chowający się panel wraca go z powrotem.</summary>
    internal void OnQueuePanelEntered()
    {
        _peek.PanelEntered();
        if (_queueSliding && !_viewModel.QueuePinned) _peek.Show();
    }

    private void UpdateQueueMenuHold() => _peek.SetHold(QueuePeek.Hold.Menu,
        QueueMenu.IsOpen || RadioMenu.IsOpen || RadioCountryBox.IsDropDownOpen || RadioCategoryBox.IsDropDownOpen);

    /// <summary>Przeciąganie nad oknem; blokuje panel tylko wtedy, gdy przeciągane są pliki.</summary>
    private void NoteFileDrag(DragEventArgs e)
    {
        _dragOverStamp++;
        if (e.DataTransfer.Contains(DataFormat.File)) _peek.SetHold(QueuePeek.Hold.DragOver, true);
    }

    private void EndFileDrag()
    {
        _dragOverStamp++;
        _peek.SetHold(QueuePeek.Hold.DragOver, false);
    }

    /// <summary>Ustawienia wysuwania i stan okna, od których zależy podgląd.</summary>
    private void UpdatePeekSettings()
    {
        _peek.RevealDelay = QueuePanelTimes.RevealDelay(_viewModel.QueueRevealDelay);
        _peek.HideDelay = QueuePanelTimes.HideDelay(_viewModel.QueueHideDelay);
        _peek.Enabled = !_viewModel.QueuePinned && _viewModel.QueueReveal == QueueReveal.Hover && !Scene.IsOpen;
        _peek.Update();
        UpdateQueueVisibility();
    }

    private void ScheduleQueueTimer()
    {
        _queueTimer.Stop();
        if (_peek.NextDeadline is not { } at) return;

        var wait = at - QueueClock();
        _queueTimer.Interval = wait > TimeSpan.FromMilliseconds(10) ? wait : TimeSpan.FromMilliseconds(10);
        _queueTimer.Start();
    }

    /// <summary>Wysuwa albo chowa podgląd, gdy jego czas minął; testy wołają to po przesunięciu zegara.</summary>
    internal void AdvanceQueueTimers() => _peek.Update();

    /// <summary>
    /// Kolumna albo nakładka i szerokość panelu. Przejście przypiętej kolumny w nakładkę przy zwężaniu
    /// okna odpina panel: nakładka, która sama nakrywa treść, zabierałaby ją bez prośby. Po poszerzeniu
    /// okna panel wraca przypięty.
    /// </summary>
    /// <param name="width">Szerokość okna.</param>
    /// <param name="fallback">Szerokość domyślna stopnia układu, gdy nikt nie ustawił własnej.</param>
    /// <returns>Szerokość panelu.</returns>
    private double ApplyQueueMode(double width, double fallback)
    {
        var columnWidth = QueuePanelWidth.Column(_viewModel.QueueWidth, fallback, width);
        var overlay = !QueuePanelWidth.FitsColumn(_viewModel.QueueWidth, fallback, width);

        if (overlay != _queueAsOverlay)
        {
            _queueAsOverlay = overlay;

            if (overlay && _viewModel.QueuePinned)
            {
                _queueCollapsedByWidth = true;
                SetQueuePinned(false);
            }
            else if (!overlay && _queueCollapsedByWidth)
            {
                _queueCollapsedByWidth = false;
                SetQueuePinned(true);
            }
        }

        var column = !_queueAsOverlay && _viewModel.QueuePinned;
        _queueAsColumn = column;

        var shown = column ? columnWidth : QueuePanelWidth.Shown(_viewModel.QueueWidth, fallback, width, column: false);
        _queuePanelWidth = shown;
        QueueColumn.Width = shown;

        Grid.SetColumn(QueueColumn, column ? 1 : 0);
        Grid.SetColumnSpan(QueueColumn, column ? 1 : 2);
        QueueColumn.HorizontalAlignment = column ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        QueueColumn.Classes.Set("queue-overlay", !column);

        // Szerokość wewnętrzna (bez marginesów listy) poniżej 300 px: bez kolumny czasu i podsumowania.
        QueueColumn.Classes.Set("queue-narrow", shown - NarrowInset < NarrowRow);

        UpdateQueueVisibility();
        return shown;
    }

    /// <summary>Marginesy listy w panelu, odejmowane od jego szerokości przy wyborze układu wiersza.</summary>
    private const double NarrowInset = 44;

    /// <summary>Najwęższy wiersz, w którym obok tytułu mieści się kolumna czasu.</summary>
    private const double NarrowRow = 300;

    private void UpdateQueueVisibility()
    {
        var pinned = _viewModel.QueuePinned;

        QueueColumn.IsVisible = pinned || _peek.IsShown || _queueSliding;

        // Krawędź przypiętej kolumny przejmuje rolę uchwytu.
        QueueHandle.IsVisible = !_queueAsColumn;

        // Kliknięcie obok zamyka nakładkę przypiętą i podgląd, który sam się nie chowa.
        QueueDismiss.IsVisible = pinned ? !_queueAsColumn : _peek.IsShown && _peek.HideDelay is null;
    }

    private void SetQueuePinned(bool pinned)
    {
        _adjustingQueue = true;
        try { _viewModel.SetQueuePinned(pinned, remember: false); }
        finally { _adjustingQueue = false; }
    }

    private void OnQueuePinnedChanged()
    {
        if (!_adjustingQueue) _queueCollapsedByWidth = false;

        var pinned = _viewModel.QueuePinned;
        var overPanel = QueueColumn.IsVisible && QueueColumn.IsPointerOver;

        if (pinned) StopQueueSlide();
        else if (QueueColumn.IsKeyboardFocusWithin && !overPanel) RestoreFocusAfterQueue();

        UpdatePeekSettings();

        // Panel odpięty spod kursora (przypinką w stopce) zostaje jako podgląd i chowa się po zjechaniu.
        if (!pinned && overPanel)
        {
            _peek.PanelEntered();
            _peek.Show();
        }

        ApplyResponsiveLayout();

        // Ukryty panel nie przewija się przy zmianie utworu, więc po otwarciu i ułożeniu listy pokazuje grany wiersz.
        if (QueueColumn.IsVisible) Dispatcher.UIThread.Post(RevealCurrent, DispatcherPriority.Loaded);
    }

    /// <summary>Wsuwa albo wysuwa podgląd; z ograniczonymi efektami bez ruchu.</summary>
    private void OnPeekChanged()
    {
        if (_viewModel.QueuePinned)
        {
            UpdateQueueVisibility();
            return;
        }

        var animate = !_viewModel.ReducedEffects;
        var slide = QueueSlide;

        if (_peek.IsShown)
        {
            _queueSlideTimer.Stop();
            _queueSliding = false;

            if (animate)
            {
                // Z ukrycia ruch zaczyna się za prawą krawędzią; w trakcie chowania cofa się z miejsca, w którym jest.
                if (!QueueColumn.IsVisible)
                {
                    SetSlideTransitions(null, null);
                    slide.X = _queuePanelWidth;
                    QueueColumn.Opacity = 0;
                }

                _queueSlideIn ??= [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = QueuePanelTimes.SlideIn, Easing = new CubicEaseOut() }];
                _queueFadeIn ??= [new DoubleTransition { Property = OpacityProperty, Duration = QueuePanelTimes.SlideIn }];
                SetSlideTransitions(_queueSlideIn, _queueFadeIn);
            }
            else SetSlideTransitions(null, null);

            slide.X = 0;
            QueueColumn.Opacity = 1;
            UpdateQueueVisibility();
            Dispatcher.UIThread.Post(RevealCurrent, DispatcherPriority.Loaded);
            return;
        }

        // Fokus nie zostaje w chowanym panelu.
        if (QueueColumn.IsKeyboardFocusWithin) RestoreFocusAfterQueue();

        if (animate && QueueColumn.IsVisible)
        {
            _queueSlideOut ??= [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = QueuePanelTimes.SlideOut, Easing = new CubicEaseIn() }];
            _queueFadeOut ??= [new DoubleTransition { Property = OpacityProperty, Duration = QueuePanelTimes.SlideOut }];
            SetSlideTransitions(_queueSlideOut, _queueFadeOut);
            slide.X = _queuePanelWidth;
            QueueColumn.Opacity = 0;

            _queueSliding = true;
            _queueSlideTimer.Stop();
            _queueSlideTimer.Start();
        }
        else StopQueueSlide();

        UpdateQueueVisibility();
    }

    private void SetSlideTransitions(Transitions? slide, Transitions? fade)
    {
        QueueSlide.Transitions = slide;
        QueueColumn.Transitions = fade;
    }

    /// <summary>Kończy chowanie od razu: panel znika, a położenie i krycie wracają do spoczynku.</summary>
    private void StopQueueSlide()
    {
        _queueSlideTimer.Stop();
        _queueSliding = false;
        SetSlideTransitions(null, null);
        QueueSlide.X = 0;
        QueueColumn.Opacity = 1;
    }

    /// <summary>Czas, po którym chowany panel znika; testy wydłużają go i kończą ruch same.</summary>
    internal TimeSpan QueueSlideOutTime
    {
        get => _queueSlideTimer.Interval;
        set => _queueSlideTimer.Interval = value;
    }

    /// <summary>Koniec ruchu chowania; testy wołają to zamiast czekać na zegar.</summary>
    internal void FinishQueueSlide()
    {
        if (!_queueSliding) return;

        StopQueueSlide();
        UpdateQueueVisibility();
    }

    // ---------- Klawiatura ----------

    /// <summary>
    /// Klawisz L: przypina panel i ustawia fokus na granym (albo pierwszym) wierszu, a przypięty odpina,
    /// z fokusem z powrotem tam, skąd przyszedł.
    /// </summary>
    private void ToggleQueueByKey()
    {
        var pin = !_viewModel.QueuePinned;
        if (pin && !(QueueColumn.IsVisible && QueueColumn.IsKeyboardFocusWithin))
            _focusBeforeQueue = FocusManager?.GetFocusedElement();

        _viewModel.ToggleQueue();

        if (pin && QueueColumn.IsVisible) Dispatcher.UIThread.Post(FocusQueueRow, DispatcherPriority.Loaded);
    }

    /// <summary>Esc: chowa podgląd albo odpina nakładkę; wynik mówi, czy było co zamknąć.</summary>
    private bool CloseQueueByKey()
    {
        if (_peek.IsShown)
        {
            _peek.Close();
            return true;
        }

        if (!QueueOverlayOpen) return false;

        _viewModel.QueuePinned = false;
        return true;
    }

    private void FocusQueueRow()
    {
        if (!QueueColumn.IsVisible) return;

        if (_viewModel.IsRadioMode)
        {
            var radioIndex = Math.Max(0, RadioList.SelectedIndex);
            if (RadioList.ItemCount == 0) return;

            RadioList.ScrollIntoView(radioIndex);
            RadioList.UpdateLayout();
            RadioList.ContainerFromIndex(radioIndex)?.Focus(NavigationMethod.Directional);
            return;
        }

        if (_viewModel.Queue.Count == 0)
        {
            QueueList.Focus(NavigationMethod.Directional);
            return;
        }

        var index = Math.Clamp(_viewModel.CurrentIndex, 0, _viewModel.Queue.Count - 1);
        QueueList.ScrollIntoView(index);
        QueueList.UpdateLayout();
        QueueList.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>Fokus wraca tam, skąd panel go wziął; gdy tamtego elementu nie ma, nie zostaje w ukrytym panelu.</summary>
    private void RestoreFocusAfterQueue()
    {
        var target = _focusBeforeQueue;
        _focusBeforeQueue = null;

        if (target is InputElement { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true } element
            && element is Visual visual && !QueueColumn.IsVisualAncestorOf(visual)
            && ReferenceEquals(TopLevel.GetTopLevel(element), this)
            && element.Focus(NavigationMethod.Directional))
            return;

        FocusManager?.Focus(null);
    }

    private void OnQueueDismissPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel.QueuePinned) _viewModel.QueuePinned = false;
        else _peek.Close();

        e.Handled = true;
    }

    // ---------- Stopka ----------

    /// <summary>Polecenie stopki otwiera inne okno: podgląd chowa się od razu, jak zwija się menu w pasku tytułu.</summary>
    private void OnFooterOpensWindow(object? sender, RoutedEventArgs e) => _peek.Close();

    private void OnFooterSettings(object? sender, RoutedEventArgs e)
    {
        _peek.Close();
        OpenSettings();
    }

    // ---------- Szerokość ----------

    /// <summary>Największa szerokość przy przeciąganiu; kolumna zostawia blokowi odtwarzania jego minimum.</summary>
    private double QueueDragMaximum(double window) => _queueAsColumn
        ? QueuePanelWidth.ColumnDragMaximum(window)
        : QueuePanelWidth.Maximum(window, column: false);

    private void OnQueueGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(QueueResizeGrip).Properties.IsLeftButtonPressed) return;
        e.Handled = true;

        // Dwuklik przywraca szerokość domyślną dla okna.
        if (e.ClickCount == 2)
        {
            EndQueueResize();
            _viewModel.QueueWidth = null;
            return;
        }

        _resizingQueue = true;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = _queuePanelWidth;
        QueueResizeGrip.Classes.Add("dragging");
        e.Pointer.Capture(QueueResizeGrip);
    }

    private void OnQueueGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingQueue) return;

        var window = Bounds.Width > 0 ? Bounds.Width : Width;
        var width = _resizeStartWidth + (_resizeStartX - e.GetPosition(this).X);
        _viewModel.QueueWidth = Math.Round(Math.Clamp(width, QueuePanelWidth.Minimum, QueueDragMaximum(window)));
    }

    private void EndQueueResize()
    {
        if (!_resizingQueue) return;

        _resizingQueue = false;
        QueueResizeGrip.Classes.Remove("dragging");
    }
}
