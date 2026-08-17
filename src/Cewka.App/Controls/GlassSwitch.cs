using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// The 44×25 switch that arms the equaliser. Fluent's <c>ToggleSwitch</c> carries a template
/// with a header, an on/off caption and its own metrics; drawing this one directly is both
/// shorter and an exact match for the design.
/// </summary>
public sealed class GlassSwitch : Control
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
