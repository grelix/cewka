using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// A vertical equaliser fader. The fill grows out of the centre in whichever direction
/// the band is cut or boosted, which is what makes a ten-band curve readable at a glance.
/// </summary>
public sealed class EqFader : Control
{
    /// <summary>Thumb height; the travel is the control height minus this.</summary>
    private const double ThumbHeight = 11.0;
    private const double ThumbWidth = 22.0;
    private const double TrackWidth = 5.0;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<EqFader, double>(
            nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> RangeProperty =
        AvaloniaProperty.Register<EqFader, double>(nameof(Range), 12.0);

    /// <summary>Rounding applied while dragging, in decibels.</summary>
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<EqFader, double>(nameof(Step), 0.5);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<EqFader, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<EqFader, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<EqFader, IBrush?>(nameof(ThumbBrush));

    public static readonly StyledProperty<IBrush?> ThumbBorderBrushProperty =
        AvaloniaProperty.Register<EqFader, IBrush?>(nameof(ThumbBorderBrush));

    public static readonly StyledProperty<IBrush?> CentreLineBrushProperty =
        AvaloniaProperty.Register<EqFader, IBrush?>(nameof(CentreLineBrush));

    public static readonly StyledProperty<bool> ShowCentreLineProperty =
        AvaloniaProperty.Register<EqFader, bool>(nameof(ShowCentreLine), true);

    static EqFader()
    {
        AffectsRender<EqFader>(
            ValueProperty, RangeProperty, TrackBrushProperty, FillBrushProperty,
            ThumbBrushProperty, ThumbBorderBrushProperty, CentreLineBrushProperty,
            ShowCentreLineProperty);
    }

    public EqFader()
    {
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        Width = 26;
    }

    /// <summary>Gain in decibels, between −<see cref="Range"/> and +<see cref="Range"/>.</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Range { get => GetValue(RangeProperty); set => SetValue(RangeProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public IBrush? ThumbBrush { get => GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }
    public IBrush? ThumbBorderBrush { get => GetValue(ThumbBorderBrushProperty); set => SetValue(ThumbBorderBrushProperty, value); }
    public IBrush? CentreLineBrush { get => GetValue(CentreLineBrushProperty); set => SetValue(CentreLineBrushProperty, value); }
    public bool ShowCentreLine { get => GetValue(ShowCentreLineProperty); set => SetValue(ShowCentreLineProperty, value); }

    /// <summary>Raised whenever the value changes through user interaction.</summary>
    public event EventHandler<double>? Adjusted;

    private bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _dragging = true;
        e.Pointer.Capture(this);
        ApplyPointer(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        ApplyPointer(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>Scroll wheel nudges the band, which is far quicker than dragging for fine work.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var next = Math.Clamp(Value + Math.Sign(e.Delta.Y) * Step, -Range, Range);
        if (Math.Abs(next - Value) > 1e-9)
        {
            Value = next;
            Adjusted?.Invoke(this, next);
        }

        e.Handled = true;
    }

    private void ApplyPointer(double y)
    {
        var travel = Math.Max(1, Bounds.Height - ThumbHeight);
        var fraction = Math.Clamp((y - ThumbHeight / 2) / travel, 0, 1);

        var raw = (1 - fraction) * 2 * Range - Range;
        var stepped = Step > 0 ? Math.Round(raw / Step) * Step : raw;
        var value = Math.Clamp(stepped, -Range, Range);

        if (Math.Abs(value - Value) < 1e-9) return;

        Value = value;
        Adjusted?.Invoke(this, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (height < ThumbHeight + 2) return;

        var travel = height - ThumbHeight;
        var centreX = width / 2;
        var trackLeft = centreX - TrackWidth / 2;
        var trackRadius = TrackWidth / 2;

        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null,
                new RoundedRect(new Rect(trackLeft, 0, TrackWidth, height), trackRadius));
        }

        var fraction = Math.Clamp((Range - Value) / (2 * Range), 0, 1);
        var thumbTop = fraction * travel;
        var thumbCentreY = thumbTop + ThumbHeight / 2;
        var neutralY = height / 2;

        if (ShowCentreLine && CentreLineBrush is { } centreLine)
        {
            context.DrawRectangle(centreLine, null, new Rect(0, neutralY - 0.5, width, 1));
        }

        if (FillBrush is { } fill)
        {
            // Stop at the near edge of the thumb rather than its centre: the thumb is
            // translucent, and a fill running underneath it shows through as a bright
            // line across the cap.
            var thumbEdge = thumbCentreY + (thumbCentreY > neutralY ? -ThumbHeight / 2 : ThumbHeight / 2);
            var end = thumbCentreY > neutralY
                ? Math.Max(neutralY, thumbEdge)
                : Math.Min(neutralY, thumbEdge);

            var top = Math.Min(neutralY, end);
            var extent = Math.Abs(end - neutralY);
            if (extent > 0.5)
            {
                context.DrawRectangle(fill, null,
                    new RoundedRect(new Rect(trackLeft, top, TrackWidth, extent), trackRadius));
            }
        }

        var thumbRect = new Rect(centreX - ThumbWidth / 2, thumbTop, ThumbWidth, ThumbHeight);
        context.DrawRectangle(
            ThumbBrush,
            ThumbBorderBrush is null ? null : new Pen(ThumbBorderBrush, 1),
            new RoundedRect(thumbRect, 6));
    }
}
