using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// A three-pixel scroll indicator for the queue, drawn beside the list instead of inside it.
/// It hides itself whenever the content fits, which is the behaviour the design assumes.
/// </summary>
public sealed class ThinScrollBar : Control
{
    private const double MinimumThumbLength = 28.0;
    private const double TrackWidth = 3.0;
    private const double ThumbWidth = 5.0;
    private const double EndPadding = 4.0;

    public static readonly StyledProperty<ScrollViewer?> TargetProperty =
        AvaloniaProperty.Register<ThinScrollBar, ScrollViewer?>(nameof(Target));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ThinScrollBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<ThinScrollBar, IBrush?>(nameof(ThumbBrush));

    static ThinScrollBar()
    {
        AffectsRender<ThinScrollBar>(TrackBrushProperty, ThumbBrushProperty);
    }

    public ThinScrollBar()
    {
        Width = 6;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public ScrollViewer? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? ThumbBrush { get => GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }

    private ScrollViewer? _subscribed;
    private bool _dragging;
    private double _grabOffset;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetProperty) Rebind();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebind()
    {
        Unsubscribe();

        _subscribed = Target;
        if (_subscribed is null) return;

        _subscribed.ScrollChanged += OnScrollChanged;
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (_subscribed is null) return;
        _subscribed.ScrollChanged -= OnScrollChanged;
        _subscribed = null;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    private bool TryGetMetrics(out double thumbTop, out double thumbLength, out double trackLength)
    {
        thumbTop = thumbLength = trackLength = 0;

        var viewer = Target;
        if (viewer is null) return false;

        var viewport = viewer.Viewport.Height;
        var extent = viewer.Extent.Height;
        if (extent <= 0 || viewport <= 0 || extent <= viewport + 0.5) return false;

        trackLength = Math.Max(0, Bounds.Height - EndPadding * 2);
        if (trackLength <= MinimumThumbLength) return false;

        thumbLength = Math.Max(MinimumThumbLength, trackLength * (viewport / extent));

        var maximumOffset = extent - viewport;
        var progress = Math.Clamp(viewer.Offset.Y / maximumOffset, 0, 1);
        thumbTop = EndPadding + (trackLength - thumbLength) * progress;

        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!TryGetMetrics(out var thumbTop, out var thumbLength, out _)) return;

        var y = e.GetPosition(this).Y;
        _dragging = true;

        // Grabbing the thumb keeps its position under the pointer; clicking the
        // track elsewhere centres the thumb there instead.
        _grabOffset = y >= thumbTop && y <= thumbTop + thumbLength
            ? y - thumbTop
            : thumbLength / 2;

        e.Pointer.Capture(this);
        ScrollToPointer(y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        ScrollToPointer(e.GetPosition(this).Y);
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

    private void ScrollToPointer(double y)
    {
        var viewer = Target;
        if (viewer is null) return;
        if (!TryGetMetrics(out _, out var thumbLength, out var trackLength)) return;

        var span = trackLength - thumbLength;
        if (span <= 0) return;

        var progress = Math.Clamp((y - EndPadding - _grabOffset) / span, 0, 1);
        var maximumOffset = viewer.Extent.Height - viewer.Viewport.Height;
        viewer.Offset = viewer.Offset.WithY(progress * maximumOffset);
    }

    public override void Render(DrawingContext context)
    {
        if (!TryGetMetrics(out var thumbTop, out var thumbLength, out var trackLength)) return;

        var centreX = Bounds.Width / 2;

        if (TrackBrush is { } track)
        {
            using (context.PushOpacity(0.45))
            {
                context.DrawRectangle(track, null, new RoundedRect(
                    new Rect(centreX - TrackWidth / 2, EndPadding, TrackWidth, trackLength),
                    TrackWidth / 2));
            }
        }

        if (ThumbBrush is { } thumb)
        {
            using (context.PushOpacity(0.9))
            {
                context.DrawRectangle(thumb, null, new RoundedRect(
                    new Rect(centreX - ThumbWidth / 2, thumbTop, ThumbWidth, thumbLength),
                    ThumbWidth / 2));
            }
        }
    }
}
