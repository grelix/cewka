using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// The horizontal bar used for both seeking and volume: a rounded track, a filled
/// portion and a round thumb. Written from scratch rather than restyling
/// <see cref="Slider"/>, whose template carries far more structure than this needs.
/// </summary>
public sealed class TrackSlider : Control
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

    /// <summary>Raised while the user drags, and once more when the drag ends.</summary>
    public event EventHandler<double>? Scrubbing;

    /// <summary>Raised when the pointer is released, carrying the committed value.</summary>
    public event EventHandler<double>? Committed;

    private bool _dragging;

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

    private void ApplyPointer(double x)
    {
        var usable = Math.Max(1, Bounds.Width);
        var value = Math.Clamp(x / usable, 0, 1);
        Value = value;
        Scrubbing?.Invoke(this, value);
    }

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
}
