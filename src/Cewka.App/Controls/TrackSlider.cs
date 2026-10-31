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
/// The horizontal bar used for both seeking and volume: a rounded track, a filled
/// portion and a round thumb. Written from scratch rather than restyling
/// <see cref="Slider"/>, whose template carries far more structure than this needs.
/// </summary>
public sealed class TrackSlider : Control, ICustomHitTest
{
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
            TrackThicknessProperty, ThumbDiameterProperty);
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
    private double _wheel;

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
    }

    private static double Percent(double value) => Math.Round(Math.Clamp(value, 0, 1) * 100, 1);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _dragging = true;
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
        var usable = Math.Max(1, Bounds.Width);
        var value = Snap(Math.Clamp(x / usable, 0, 1));

        Scrubbing?.Invoke(this, value);
        Value = value;
    }

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

            var thumb = ThumbDiameter;
            if (thumb > 0)
            {
                context.DrawEllipse(fill, null,
                    new Point(width * value, height / 2), thumb / 2, thumb / 2);
            }
        }
    }

    /// <summary>Suwak dla czytnika ekranu, z wartością w procentach.</summary>
    private sealed class TrackSliderAutomationPeer(TrackSlider owner) : ControlAutomationPeer(owner), IRangeValueProvider
    {
        private TrackSlider Slider => (TrackSlider)Owner;

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
