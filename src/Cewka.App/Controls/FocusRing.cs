using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// Pierścień fokusu z klawiatury: 2 px w barwie <c>FocusRingBrush</c>, z odstępem od kontrolki
/// i zaokrągleniem dopasowanym do jej kształtu. Wstawiany jako ozdobnik fokusu każdej kontrolki,
/// więc pojawia się tylko przy przejściu klawiszem Tab albo strzałkami.
/// </summary>
public sealed class FocusRing : Control
{
    /// <summary>Odstęp pierścienia od krawędzi kontrolki.</summary>
    public const double Gap = 2;

    public const double Thickness = 2;

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<FocusRing, IBrush?>(nameof(Brush));

    static FocusRing()
    {
        AffectsRender<FocusRing>(BrushProperty);
    }

    public FocusRing()
    {
        IsHitTestVisible = false;
        Margin = new Thickness(-(Gap + Thickness));
    }

    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Brush is null || Bounds.Width < 2 || Bounds.Height < 2) return;

        var half = Thickness / 2;
        var rect = new Rect(half, half, Bounds.Width - Thickness, Bounds.Height - Thickness);
        var radius = Radius(AdornerLayer.GetAdornedElement(this)) + Gap + half;

        context.DrawRectangle(null, new Pen(Brush, Thickness), new RoundedRect(rect, Math.Min(radius, rect.Height / 2)));
    }

    /// <summary>Zaokrąglenie narożników kontrolki, którą obejmuje pierścień.</summary>
    private static double Radius(Visual? element) => element switch
    {
        TemplatedControl templated => templated.CornerRadius.TopLeft,
        Border border => border.CornerRadius.TopLeft,
        GlassSwitch or TrackSlider => element.Bounds.Height / 2,
        EqFader => 6,
        _ => 4,
    };
}
