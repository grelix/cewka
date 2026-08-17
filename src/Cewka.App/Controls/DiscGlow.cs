using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// A soft highlight that follows the pointer across the record, standing in for the
/// mock's <c>mix-blend-mode: screen</c> layer. Avalonia has no blend modes for vector
/// content, so this is a plain translucent white gradient — over the dark disc the
/// difference is not visible, and over the light theme it stays subtle by design.
/// </summary>
public sealed class DiscGlow : Control
{
    private const double GlowRadius = 190.0;

    public static readonly StyledProperty<Point> CentreProperty =
        AvaloniaProperty.Register<DiscGlow, Point>(nameof(Centre));

    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<DiscGlow, double>(nameof(Intensity));

    static DiscGlow()
    {
        AffectsRender<DiscGlow>(CentreProperty, IntensityProperty);
    }

    public DiscGlow()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Pointer position in this control's coordinates.</summary>
    public Point Centre { get => GetValue(CentreProperty); set => SetValue(CentreProperty, value); }

    /// <summary>0 hides the glow entirely; 1 is the full highlight.</summary>
    public double Intensity { get => GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }

    public override void Render(DrawingContext context)
    {
        var intensity = Math.Clamp(Intensity, 0, 1);
        if (intensity <= 0.001) return;

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4) return;

        var radius = GlowRadius * (size / 300.0);

        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(Centre, RelativeUnit.Absolute),
            GradientOrigin = new RelativePoint(Centre, RelativeUnit.Absolute),
            RadiusX = new RelativeScalar(radius, RelativeUnit.Absolute),
            RadiusY = new RelativeScalar(radius, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF), 0.42),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.72),
            },
        };

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        using (context.PushOpacity(intensity))
        {
            context.DrawEllipse(brush, null, centre, size / 2, size / 2);
        }
    }
}
