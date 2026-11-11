using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// Pierścień fokusu z klawiatury: 2 px w barwie <c>FocusRingBrush</c> (w scenie <c>SceneText2Brush</c>), z odstępem od kontrolki
/// i zaokrągleniem dopasowanym do jej kształtu. Wstawiany jako ozdobnik fokusu każdej kontrolki,
/// więc pojawia się tylko przy przejściu klawiszem Tab albo strzałkami.
/// </summary>
public sealed class FocusRing : Control
{
    /// <summary>Odstęp pierścienia od krawędzi kontrolki.</summary>
    public const double Gap = 2;

    public const double DefaultThickness = 2;

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<FocusRing, IBrush?>(nameof(Brush));

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<FocusRing, double>(nameof(RingThickness), DefaultThickness);

    static FocusRing()
    {
        AffectsRender<FocusRing>(BrushProperty, RingThicknessProperty);
    }

    public FocusRing()
    {
        IsHitTestVisible = false;
        Margin = new Thickness(-(Gap + DefaultThickness));

        // Pierścień leży poza kontrolką, więc nie może być przycinany do jej granic.
        AdornerLayer.SetIsClipEnabled(this, false);
    }

    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    /// <summary>Grubość obrysu w jednostkach kontrolki, którą obejmuje pierścień.</summary>
    public double RingThickness { get => GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Obrys leży poza kontrolką, więc grubszy wystaje dalej.
        if (change.Property == RingThicknessProperty) Margin = new Thickness(-(Gap + RingThickness));
    }

    public override void Render(DrawingContext context)
    {
        if (Brush is null || Bounds.Width < 2 || Bounds.Height < 2) return;

        var thickness = RingThickness;
        var half = thickness / 2;
        var rect = new Rect(half, half, Bounds.Width - thickness, Bounds.Height - thickness);
        var radius = Radius(AdornerLayer.GetAdornedElement(this)) + Gap + half;

        context.DrawRectangle(null, new Pen(Brush, thickness), new RoundedRect(rect, Math.Min(radius, rect.Height / 2)));
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
