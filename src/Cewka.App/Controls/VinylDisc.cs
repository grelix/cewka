using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Cewka.App.Controls;

/// <summary>
/// The record: body, grooves, inner ring, album label and spindle hole.
/// <para>
/// Rotation is applied through <see cref="Visual.RenderTransform"/> rather than inside
/// <see cref="Render"/>, so spinning costs nothing beyond a matrix update — the ~36 groove
/// rings are tessellated once and then reused frame after frame.
/// </para>
/// </summary>
public sealed class VinylDisc : Control
{
    /// <summary>Distance between grooves, in device-independent pixels.</summary>
    private const double GroovePitch = 4.0;

    /// <summary>Label radius as a fraction of the disc radius (CSS <c>inset: 30%</c>).</summary>
    private const double LabelRadiusFraction = 0.40;

    /// <summary>Boundary between the lighter core and the darker body.</summary>
    private const double CoreFraction = 0.26;

    public static readonly StyledProperty<IImage?> CoverProperty =
        AvaloniaProperty.Register<VinylDisc, IImage?>(nameof(Cover));

    public static readonly StyledProperty<double> AngleProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(Angle));

    public static readonly StyledProperty<IBrush?> CoreBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(CoreBrush));

    public static readonly StyledProperty<IBrush?> BodyBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(BodyBrush));

    public static readonly StyledProperty<IBrush?> GrooveBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(GrooveBrush));

    public static readonly StyledProperty<IBrush?> RingBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(RingBrush));

    public static readonly StyledProperty<IBrush?> HoleBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(HoleBrush));

    /// <summary>Draw the grooves. Turned off by the reduced-effects mode.</summary>
    public static readonly StyledProperty<bool> ShowGroovesProperty =
        AvaloniaProperty.Register<VinylDisc, bool>(nameof(ShowGrooves), true);

    static VinylDisc()
    {
        AffectsRender<VinylDisc>(
            CoverProperty, CoreBrushProperty, BodyBrushProperty, GrooveBrushProperty,
            RingBrushProperty, HoleBrushProperty, ShowGroovesProperty);
    }

    public VinylDisc()
    {
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = new RotateTransform(0);
    }

    public IImage? Cover { get => GetValue(CoverProperty); set => SetValue(CoverProperty, value); }
    public double Angle { get => GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public IBrush? CoreBrush { get => GetValue(CoreBrushProperty); set => SetValue(CoreBrushProperty, value); }
    public IBrush? BodyBrush { get => GetValue(BodyBrushProperty); set => SetValue(BodyBrushProperty, value); }
    public IBrush? GrooveBrush { get => GetValue(GrooveBrushProperty); set => SetValue(GrooveBrushProperty, value); }
    public IBrush? RingBrush { get => GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public IBrush? HoleBrush { get => GetValue(HoleBrushProperty); set => SetValue(HoleBrushProperty, value); }
    public bool ShowGrooves { get => GetValue(ShowGroovesProperty); set => SetValue(ShowGroovesProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AngleProperty && RenderTransform is RotateTransform rotate)
            rotate.Angle = Angle;
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4) return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2;

        DrawBody(context, centre, radius);
        if (ShowGrooves) DrawGrooves(context, centre, radius);
        DrawInnerRing(context, centre, radius, size);
        DrawLabel(context, centre, radius);
        DrawHole(context, centre, size);
    }

    private void DrawBody(DrawingContext context, Point centre, double radius)
    {
        // radial-gradient(circle, core 0 26%, body 26.4% 100%)
        var body = new RadialGradientBrush
        {
            Center = RelativePoint.Center,
            GradientOrigin = RelativePoint.Center,
            RadiusX = RelativeScalar.Middle,
            RadiusY = RelativeScalar.Middle,
            GradientStops =
            {
                new GradientStop((CoreBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x1A, 0x1A, 0x1C), 0),
                new GradientStop((CoreBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x1A, 0x1A, 0x1C), CoreFraction),
                new GradientStop((BodyBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x0C, 0x0C, 0x0E), CoreFraction + 0.004),
                new GradientStop((BodyBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x0C, 0x0C, 0x0E), 1),
            },
        };

        context.DrawEllipse(body, null, centre, radius, radius);
    }

    private void DrawGrooves(DrawingContext context, Point centre, double radius)
    {
        var brush = GrooveBrush;
        if (brush is null) return;

        var pen = new Pen(brush, 1);
        var innerLimit = radius * LabelRadiusFraction;

        for (var r = radius - 2; r > innerLimit; r -= GroovePitch)
            context.DrawEllipse(null, pen, centre, r, r);
    }

    private void DrawInnerRing(DrawingContext context, Point centre, double radius, double size)
    {
        var brush = RingBrush;
        if (brush is null) return;

        // CSS: inset 8px on a 300px disc — scaled so it holds at other sizes too.
        var inset = 8.0 * (size / 300.0);
        context.DrawEllipse(null, new Pen(brush, 1), centre, radius - inset, radius - inset);
    }

    private void DrawLabel(DrawingContext context, Point centre, double radius)
    {
        var cover = Cover;
        if (cover is null) return;

        var labelRadius = radius * LabelRadiusFraction;
        var box = new Rect(
            centre.X - labelRadius, centre.Y - labelRadius,
            labelRadius * 2, labelRadius * 2);

        using (context.PushGeometryClip(new EllipseGeometry(box)))
        {
            // Cover the circle regardless of the source aspect ratio (object-fit: cover).
            var source = cover.Size;
            var scale = Math.Max(box.Width / source.Width, box.Height / source.Height);
            var scaled = new Size(source.Width * scale, source.Height * scale);
            var destination = new Rect(
                box.X + (box.Width - scaled.Width) / 2,
                box.Y + (box.Height - scaled.Height) / 2,
                scaled.Width, scaled.Height);

            context.DrawImage(cover, new Rect(source), destination);
        }
    }

    private void DrawHole(DrawingContext context, Point centre, double size)
    {
        var brush = HoleBrush;
        if (brush is null) return;

        var holeRadius = 5.5 * (size / 300.0);
        context.DrawEllipse(brush, null, centre, holeRadius, holeRadius);
    }
}
