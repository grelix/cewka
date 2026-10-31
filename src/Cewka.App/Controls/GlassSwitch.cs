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
/// Przełącznik 44×25 włączający korektor, rysowany wprost zamiast <c>ToggleSwitch</c>
/// z Fluent, który ma nagłówek, podpis i własne wymiary.
/// </summary>
public sealed class GlassSwitch : Control, ICustomHitTest
{
    private const double KnobDiameter = 19.0;
    private const double KnobInset = 2.0;

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<GlassSwitch, bool>(
            nameof(IsChecked), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> OffBrushProperty =
        AvaloniaProperty.Register<GlassSwitch, IBrush?>(nameof(OffBrush));

    public static readonly StyledProperty<IBrush?> OnBrushProperty =
        AvaloniaProperty.Register<GlassSwitch, IBrush?>(nameof(OnBrush));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<GlassSwitch, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<IBrush?> KnobBrushProperty =
        AvaloniaProperty.Register<GlassSwitch, IBrush?>(nameof(KnobBrush), Brushes.White);

    static GlassSwitch()
    {
        AffectsRender<GlassSwitch>(
            IsCheckedProperty, OffBrushProperty, OnBrushProperty,
            BorderBrushProperty, KnobBrushProperty);
    }

    public GlassSwitch()
    {
        Width = 44;
        Height = 25;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
    }

    public bool IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public IBrush? OffBrush { get => GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }
    public IBrush? OnBrush { get => GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public IBrush? KnobBrush { get => GetValue(KnobBrushProperty); set => SetValue(KnobBrushProperty, value); }

    public event EventHandler<bool>? Toggled;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Toggle();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Space or Key.Enter)) return;

        Toggle();
        e.Handled = true;
    }

    private void Toggle()
    {
        IsChecked = !IsChecked;
        Toggled?.Invoke(this, IsChecked);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsCheckedProperty && ControlAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                State(change.GetOldValue<bool>()), State(change.GetNewValue<bool>()));
        }
    }

    private static ToggleState State(bool on) => on ? ToggleState.On : ToggleState.Off;

    protected override AutomationPeer OnCreateAutomationPeer() => new GlassSwitchAutomationPeer(this);

    /// <summary>Przełącznik dla czytnika ekranu: pole wyboru ze stanem włączenia.</summary>
    private sealed class GlassSwitchAutomationPeer(GlassSwitch owner) : ControlAutomationPeer(owner), IToggleProvider
    {
        private GlassSwitch Switch => (GlassSwitch)Owner;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        public ToggleState ToggleState => State(Switch.IsChecked);

        public void Toggle() => Switch.Toggle();
    }

    /// <summary>Cały prostokąt kontrolki przyjmuje wskaźnik, nie tylko narysowany pasek.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 8 || height < 8) return;

        var background = IsChecked ? OnBrush : OffBrush;
        var pen = BorderBrush is null ? null : new Pen(BorderBrush, 1);

        context.DrawRectangle(background, pen,
            new RoundedRect(new Rect(0, 0, width, height), height / 2));

        var travel = width - KnobDiameter - KnobInset * 2;
        var knobLeft = KnobInset + (IsChecked ? travel : 0);
        var knobCentre = new Point(knobLeft + KnobDiameter / 2, height / 2);

        context.DrawEllipse(KnobBrush, null, knobCentre, KnobDiameter / 2, KnobDiameter / 2);
    }
}
