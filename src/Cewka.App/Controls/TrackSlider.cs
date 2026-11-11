using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Cewka.App.Controls;

/// <summary>
/// Poziomy pasek przewijania i głośności: zaokrąglony tor, wypełnienie i okrągły uchwyt.
/// Własny, bo szablon <see cref="Slider"/> ma znacznie więcej struktury, niż trzeba.
/// </summary>
public sealed class TrackSlider : Control, ICustomHitTest
{
    /// <summary>Grubość obrysu uchwytu.</summary>
    private const double ThumbOutlineThickness = 0.5;

    /// <summary>Cień uchwytu: przesunięcie w dół i promień rozmycia.</summary>
    private const double ThumbShadowOffset = 1.0;
    private const double ThumbShadowBlur = 1.5;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<TrackSlider, double>(
            nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<TrackSlider, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<TrackSlider, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<double> TrackThicknessProperty =
        AvaloniaProperty.Register<TrackSlider, double>(nameof(TrackThickness), 5.0);

    public static readonly StyledProperty<double> ThumbDiameterProperty =
        AvaloniaProperty.Register<TrackSlider, double>(nameof(ThumbDiameter), 13.0);

    public static readonly StyledProperty<bool> ThumbInTrackProperty =
        AvaloniaProperty.Register<TrackSlider, bool>(nameof(ThumbInTrack));

    public static readonly StyledProperty<double> ThumbOpacityProperty =
        AvaloniaProperty.Register<TrackSlider, double>(nameof(ThumbOpacity), 1.0);

    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<TrackSlider, IBrush?>(nameof(ThumbBrush));

    public static readonly StyledProperty<IBrush?> ThumbOutlineBrushProperty =
        AvaloniaProperty.Register<TrackSlider, IBrush?>(nameof(ThumbOutlineBrush));

    public static readonly StyledProperty<IBrush?> ThumbShadowBrushProperty =
        AvaloniaProperty.Register<TrackSlider, IBrush?>(nameof(ThumbShadowBrush));

    public static readonly StyledProperty<string?> ValueTextProperty =
        AvaloniaProperty.Register<TrackSlider, string?>(nameof(ValueText));

    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<TrackSlider, double>(nameof(Step));

    public static readonly StyledProperty<double> KeyboardStepProperty =
        AvaloniaProperty.Register<TrackSlider, double>(nameof(KeyboardStep));

    public static readonly StyledProperty<double> WheelStepProperty =
        AvaloniaProperty.Register<TrackSlider, double>(nameof(WheelStep));

    static TrackSlider()
    {
        AffectsRender<TrackSlider>(
            ValueProperty, TrackBrushProperty, FillBrushProperty,
            TrackThicknessProperty, ThumbDiameterProperty, ThumbInTrackProperty, ThumbOpacityProperty,
            ThumbBrushProperty, ThumbOutlineBrushProperty, ThumbShadowBrushProperty);
    }

    public TrackSlider()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Position along the track, 0 to 1.</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public double TrackThickness { get => GetValue(TrackThicknessProperty); set => SetValue(TrackThicknessProperty, value); }
    public double ThumbDiameter { get => GetValue(ThumbDiameterProperty); set => SetValue(ThumbDiameterProperty, value); }

    /// <summary>
    /// Uchwyt w torze, jak suwak range w przeglądarce: przy 0 i 1 jego brzeg stoi na końcu toru,
    /// a środek odsuwa się od końca o bieżący promień, także gdy uchwyt rośnie. Bez tego środek
    /// stoi na końcu wypełnienia. Wypełnienie się nie zmienia, a wskaźnik ustawia wartość tak,
    /// żeby uchwyt stał pod nim.
    /// </summary>
    public bool ThumbInTrack { get => GetValue(ThumbInTrackProperty); set => SetValue(ThumbInTrackProperty, value); }

    /// <summary>
    /// Krycie uchwytu razem z obrysem i cieniem, od 0 do 1; tor i wypełnienie zostają kryjące. Można je przenikać
    /// przejściem <see cref="Avalonia.Animation.DoubleTransition"/>.
    /// </summary>
    public double ThumbOpacity { get => GetValue(ThumbOpacityProperty); set => SetValue(ThumbOpacityProperty, value); }

    /// <summary>Barwa uchwytu; bez niej uchwyt ma barwę wypełnienia.</summary>
    public IBrush? ThumbBrush { get => GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }

    /// <summary>Obrys uchwytu linią 0,5 tuż za jego brzegiem, więc wychodzi o 0,5 poza średnicę.</summary>
    public IBrush? ThumbOutlineBrush { get => GetValue(ThumbOutlineBrushProperty); set => SetValue(ThumbOutlineBrushProperty, value); }

    /// <summary>
    /// Barwa cienia pod uchwytem, przesuniętego o 1 w dół i rozmytego o 1,5. Liczy się tylko
    /// pędzel jednolity; bez niego uchwyt nie ma cienia.
    /// </summary>
    public IBrush? ThumbShadowBrush { get => GetValue(ThumbShadowBrushProperty); set => SetValue(ThumbShadowBrushProperty, value); }

    /// <summary>
    /// Wartość słowami dla czytnika ekranu, np. „0:39 z 3:59”. Bez niej czytnik dostaje tylko
    /// położenie w procentach.
    /// </summary>
    public string? ValueText { get => GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }

    /// <summary>
    /// Odstęp stopni, na które przeskakuje wskaźnik; zero oznacza suwak ciągły. Model widoku
    /// zaokrąglający wartość nie cofnie suwaka sam, bo wiązanie pomija zgłoszenia wysłane
    /// w trakcie zapisu do źródła.
    /// </summary>
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    /// <summary>
    /// Krok strzałek. Zero zostawia suwak poza kolejnością klawisza Tab: postęp i głośność mają
    /// skróty okna, a suwak z fokusem po kliknięciu zabrałby im strzałki.
    /// </summary>
    public double KeyboardStep { get => GetValue(KeyboardStepProperty); set => SetValue(KeyboardStepProperty, value); }

    /// <summary>Krok kółka myszy; zero wyłącza kółko.</summary>
    public double WheelStep { get => GetValue(WheelStepProperty); set => SetValue(WheelStepProperty, value); }

    /// <summary>Raised while the user drags, and once more when the drag ends.</summary>
    public event EventHandler<double>? Scrubbing;

    /// <summary>Raised when the pointer is released, carrying the committed value.</summary>
    public event EventHandler<double>? Committed;

    private bool _dragging;
    private IPointer? _dragPointer;
    private double _wheel;

    // Pióro obrysu i pędzel cienia powstają przy zmianie właściwości, nie w każdym rysowaniu:
    // suwak postępu rysuje się przy każdej zmianie położenia. Pióro trzyma pędzel źródłowy, więc
    // idzie za jego barwą samo; pędzel cienia pamięta barwę, z której powstał, bo pędzel źródłowy
    // można przebarwić bez zmiany właściwości suwaka.
    private Pen? _thumbOutline;
    private RadialGradientBrush? _thumbShadow;
    private Color _thumbShadowColour;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == KeyboardStepProperty)
        {
            Focusable = KeyboardStep > 0;
        }
        else if (change.Property == ValueProperty
                 && ControlAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty,
                Percent(change.GetOldValue<double>()), Percent(change.GetNewValue<double>()));
        }
        else if (change.Property == ValueTextProperty
                 && ControlAutomationPeer.FromElement(this) is { } textPeer)
        {
            textPeer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty,
                change.GetOldValue<string?>(), change.GetNewValue<string?>());
        }
        else if (change.Property == ThumbOutlineBrushProperty)
        {
            _thumbOutline = ThumbOutlineBrush is { } outline ? new Pen(outline, ThumbOutlineThickness) : null;
        }
        else if (change.Property == ThumbShadowBrushProperty || change.Property == ThumbDiameterProperty)
        {
            _thumbShadow = null;
        }
    }

    private static double Percent(double value) => Math.Round(Math.Clamp(value, 0, 1) * 100, 1);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _dragging = true;
        _dragPointer = e.Pointer;
        e.Pointer.Capture(this);
        ApplyPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        ApplyPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;

        _dragging = false;
        _dragPointer = null;
        e.Pointer.Capture(null);
        Committed?.Invoke(this, Value);
        e.Handled = true;
    }

    // Capture can be taken away (an alt-tab, a popup); without this the control
    // would stay stuck in drag mode and keep swallowing pointer moves.
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;

        _dragging = false;
        _dragPointer = null;
        Committed?.Invoke(this, Value);
    }

    /// <summary>
    /// Kończy przeciąganie jak puszczenie przycisku, np. gdy suwak znika z widoku w trakcie
    /// przeciągania: zwalnia przechwycenie wskaźnika i zatwierdza bieżącą wartość. Dalszy ruch
    /// i puszczenie trafiają już tam, gdzie jest wskaźnik.
    /// </summary>
    public void EndDrag()
    {
        if (!_dragging) return;

        var pointer = _dragPointer;
        _dragging = false;
        _dragPointer = null;
        if (ReferenceEquals(pointer?.Captured, this)) pointer!.Capture(null);
        Committed?.Invoke(this, Value);
    }

    /// <summary>Kółko sumuje drobne kroki gładzika, zanim przesunie suwak o cały krok.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (WheelStep <= 0) return;

        var steps = WheelAccumulator.Add(ref _wheel, e.Delta.Y);
        if (steps != 0) SetFromUser(Value + steps * WheelStep);

        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _wheel = 0;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || KeyboardStep <= 0 || e.KeyModifiers != KeyModifiers.None) return;

        double? target = e.Key switch
        {
            Key.Right or Key.Up => Value + KeyboardStep,
            Key.Left or Key.Down => Value - KeyboardStep,
            Key.PageUp => Value + 3 * KeyboardStep,
            Key.PageDown => Value - 3 * KeyboardStep,
            Key.Home => 0,
            Key.End => 1,
            _ => null,
        };

        if (target is not { } value) return;

        SetFromUser(value);
        e.Handled = true;
    }

    /// <summary>
    /// Zmiana spoza przeciągania (klawiatura, kółko, czytnik ekranu): przebiega jak krótkie
    /// przeciągnięcie, więc przewijanie odtwarzania dzieje się dokładnie raz.
    /// </summary>
    internal void SetFromUser(double value)
    {
        value = Snap(Math.Clamp(value, 0, 1));
        if (Math.Abs(value - Value) < 1e-9) return;

        Scrubbing?.Invoke(this, value);
        Value = value;
        Committed?.Invoke(this, value);
    }

    private double Snap(double value)
    {
        if (Step <= 0) return value;

        var steps = Math.Round(1 / Step);
        return Math.Round(value * steps) / steps;
    }

    /// <summary>
    /// Zgłoszenie przeciągania idzie przed zmianą wartości: wiązanie zapisuje ją do modelu widoku
    /// od razu, a ten przy pierwszym naciśnięciu musi już wiedzieć, że ma czekać na puszczenie.
    /// </summary>
    private void ApplyPointer(double x)
    {
        var inset = Inset(Bounds.Width);
        var usable = Math.Max(1, Bounds.Width - 2 * inset);
        var value = Snap(Math.Clamp((x - inset) / usable, 0, 1));

        Scrubbing?.Invoke(this, value);
        Value = value;
    }

    /// <summary>
    /// Odległość środka uchwytu od końca toru przy 0 i 1: promień uchwytu w torze, przycięty tak,
    /// żeby między skrajnymi położeniami zostało miejsce.
    /// </summary>
    private double Inset(double width) =>
        ThumbInTrack ? Math.Clamp(ThumbDiameter / 2, 0, Math.Max(0, width / 2 - 0.5)) : 0;

    protected override AutomationPeer OnCreateAutomationPeer() => new TrackSliderAutomationPeer(this);

    /// <summary>Cały prostokąt kontrolki przyjmuje wskaźnik, nie tylko narysowany pasek.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 2) return;

        var thickness = TrackThickness;
        var radius = thickness / 2;
        var top = (height - thickness) / 2;
        var value = Math.Clamp(Value, 0, 1);

        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null,
                new RoundedRect(new Rect(0, top, width, thickness), radius));
        }

        if (FillBrush is { } fill)
        {
            var filled = width * value;
            if (filled > 0.5)
            {
                context.DrawRectangle(fill, null,
                    new RoundedRect(new Rect(0, top, filled, thickness), radius));
            }
        }

        var thumbBrush = ThumbBrush ?? FillBrush;
        var thumbOpacity = Math.Clamp(ThumbOpacity, 0, 1);
        if (thumbBrush is null || ThumbDiameter <= 0 || !(thumbOpacity > 0)) return;

        var inset = Inset(width);
        var centre = new Point(inset + value * (width - 2 * inset), height / 2);
        if (thumbOpacity < 1)
        {
            // Uchwyt z obrysem i cieniem gaśnie jako całość, we własnej warstwie. Bez niej krycie
            // trafia do każdego kształtu osobno i cień prześwituje przez przygaszony uchwyt.
            using (context.PushRenderOptions(new RenderOptions { RequiresFullOpacityHandling = true }))
            using (context.PushOpacity(thumbOpacity))
                DrawThumb(context, thumbBrush, centre);
        }
        else
        {
            DrawThumb(context, thumbBrush, centre);
        }
    }

    private void DrawThumb(DrawingContext context, IBrush brush, Point centre)
    {
        var radius = ThumbDiameter / 2;

        if (ThumbShadow(radius) is { } shadow)
        {
            var spread = radius + ThumbShadowBlur;
            context.DrawEllipse(shadow, null,
                new Point(centre.X, centre.Y + ThumbShadowOffset), spread, spread);
        }

        context.DrawEllipse(brush, null, centre, radius, radius);

        // Obrys tuż za brzegiem, jak box-shadow 0 0 0 0.5px: uchwyt zachowuje swoją średnicę.
        if (_thumbOutline is { } outline)
        {
            var ring = radius + ThumbOutlineThickness / 2;
            context.DrawEllipse(null, outline, centre, ring, ring);
        }
    }

    /// <summary>
    /// Cień jako gradient promienisty w kole większym o rozmycie: pełna barwa do promienia
    /// uchwytu pomniejszonego o rozmycie, zero na brzegu. Przy tak małym rozmyciu to wystarcza,
    /// a rysuje się jak zwykłe koło, bez filtra rozmycia.
    /// </summary>
    private RadialGradientBrush? ThumbShadow(double radius)
    {
        if (ThumbShadowBrush is not ISolidColorBrush solid) return null;

        var source = solid.Color;
        var alpha = (byte)Math.Round(source.A * Math.Clamp(solid.Opacity, 0, 1));
        var colour = Color.FromArgb(alpha, source.R, source.G, source.B);
        if (_thumbShadow is not null && colour == _thumbShadowColour) return _thumbShadow;

        var solidTo = Math.Max(0, radius - ThumbShadowBlur) / (radius + ThumbShadowBlur);

        _thumbShadowColour = colour;
        _thumbShadow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(colour, solidTo),
                new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), 1),
            },
        };

        return _thumbShadow;
    }

    /// <summary>
    /// Suwak dla czytnika ekranu: położenie w procentach przez wzorzec zakresu, a przy ustawionym
    /// tekście wartości dodatkowo ten tekst przez wzorzec wartości.
    /// </summary>
    private sealed class TrackSliderAutomationPeer(TrackSlider owner)
        : ControlAutomationPeer(owner), IRangeValueProvider, IValueProvider
    {
        private TrackSlider Slider => (TrackSlider)Owner;

        /// <summary>
        /// Bez tekstu wartości suwak nie zgłasza wzorca wartości, żeby czytnik nie dostał pustego
        /// napisu obok procentu.
        /// </summary>
        protected override object? GetProviderCore(Type providerType) =>
            providerType == typeof(IValueProvider) && string.IsNullOrEmpty(Slider.ValueText)
                ? null
                : base.GetProviderCore(providerType);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        public bool IsReadOnly => !Slider.IsEffectivelyEnabled;

        public double Minimum => 0;

        public double Maximum => 100;

        public double Value => Percent(Slider.Value);

        public double SmallChange => (Slider.KeyboardStep > 0 ? Slider.KeyboardStep : Slider.WheelStep > 0 ? Slider.WheelStep : 0.05) * 100;

        public double LargeChange => SmallChange * 3;

        public void SetValue(double value) => Slider.SetFromUser(value / 100);

        // Tylko do odczytu tak samo jak zakres: inaczej czytnik mógłby ogłosić przesuwany suwak
        // jako tylko do odczytu.
        bool IValueProvider.IsReadOnly => IsReadOnly;

        string? IValueProvider.Value => Slider.ValueText;

        // Tekst wartości tylko opisuje położenie; zmienia się je przez zakres.
        void IValueProvider.SetValue(string? value) =>
            throw new InvalidOperationException("Tekstu wartości suwaka nie da się wpisać.");
    }
}

/// <summary>
/// Kółko i gładzik: gładzik precyzyjny wysyła dziesiątki zdarzeń z ułamkowym przesunięciem
/// w jednym geście, więc krok liczy się dopiero po zebraniu pełnej jednostki.
/// </summary>
internal static class WheelAccumulator
{
    /// <summary>Dodaje przesunięcie i zwraca liczbę pełnych kroków do wykonania.</summary>
    public static int Add(ref double accumulated, double delta)
    {
        // Zmiana kierunku zaczyna liczenie od nowa, zamiast najpierw spłacać resztę.
        if (Math.Sign(delta) != Math.Sign(accumulated)) accumulated = 0;

        accumulated += delta;
        var steps = (int)Math.Truncate(accumulated);
        accumulated -= steps;
        return steps;
    }
}
