using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Cewka.App.Controls;

/// <summary>
/// Płyta: korpus, rowki, pierścień, naklejka i otwór. Obrót idzie przez
/// <see cref="Visual.RenderTransform"/>, więc rowki są teselowane raz, a klatka kosztuje
/// tylko zmianę macierzy.
/// <para>
/// Promienie warstw podaje się jako ułamki promienia płyty. Odstęp rowków, ich odległość od
/// krawędzi i grubość linii podaje się w jednostkach <see cref="DesignDiameter"/>, więc rosną
/// razem z płytą; bez średnicy wzorcowej to stałe piksele. Wartości domyślne dają płytę okna.
/// </para>
/// </summary>
public sealed class VinylDisc : Control
{
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

    /// <summary>Draw the grooves.</summary>
    public static readonly StyledProperty<bool> ShowGroovesProperty =
        AvaloniaProperty.Register<VinylDisc, bool>(nameof(ShowGrooves), true);

    public static readonly StyledProperty<double> CoreFractionProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(CoreFraction), 0.26);

    public static readonly StyledProperty<double> BodyFractionProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(BodyFraction), 0.26 + 0.004);

    public static readonly StyledProperty<IBrush?> RimBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(RimBrush));

    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(OutlineBrush));

    public static readonly StyledProperty<double> DesignDiameterProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(DesignDiameter));

    public static readonly StyledProperty<double> GroovePitchProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(GroovePitch), 4.0);

    public static readonly StyledProperty<double> GrooveInsetProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(GrooveInset), 2.0);

    public static readonly StyledProperty<double> GrooveInnerFractionProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(GrooveInnerFraction), 0.40);

    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(SeparatorBrush));

    public static readonly StyledProperty<IReadOnlyList<double>?> SeparatorFractionsProperty =
        AvaloniaProperty.Register<VinylDisc, IReadOnlyList<double>?>(nameof(SeparatorFractions));

    public static readonly StyledProperty<double> LabelFractionProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(LabelFraction), 0.40);

    public static readonly StyledProperty<IBrush?> LabelOutlineBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(LabelOutlineBrush));

    public static readonly StyledProperty<double> HoleFractionProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(HoleFraction), 5.5 / 150);

    public static readonly StyledProperty<IBrush?> HoleOutlineBrushProperty =
        AvaloniaProperty.Register<VinylDisc, IBrush?>(nameof(HoleOutlineBrush));

    static VinylDisc()
    {
        AffectsRender<VinylDisc>(
            CoverProperty, CoreBrushProperty, BodyBrushProperty, GrooveBrushProperty,
            RingBrushProperty, HoleBrushProperty, ShowGroovesProperty,
            CoreFractionProperty, BodyFractionProperty, RimBrushProperty, OutlineBrushProperty,
            DesignDiameterProperty, GroovePitchProperty, GrooveInsetProperty, GrooveInnerFractionProperty,
            SeparatorBrushProperty, SeparatorFractionsProperty, LabelFractionProperty,
            LabelOutlineBrushProperty, HoleFractionProperty, HoleOutlineBrushProperty);
    }

    // Pędzel korpusu i pióra powstają przy zmianie wyglądu albo rozmiaru, nie przy każdym rysowaniu.
    // Pióra trzymają pędzle źródłowe, więc idą za ich barwą same; pędzel korpusu pamięta barwy,
    // z których powstał, bo pędzel źródłowy można przebarwić bez zmiany właściwości płyty.
    private RadialGradientBrush? _body;
    private (Color Core, Color Body, Color Rim) _bodyColours;
    private double _penUnit = double.NaN;
    private Pen? _groovePen;
    private Pen? _separatorPen;
    private Pen? _ringPen;
    private Pen? _outlinePen;
    private Pen? _labelOutlinePen;
    private Pen? _holeOutlinePen;

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

    /// <summary>Promień, do którego sięga barwa środka.</summary>
    public double CoreFraction { get => GetValue(CoreFractionProperty); set => SetValue(CoreFractionProperty, value); }

    /// <summary>Promień, od którego zaczyna się barwa korpusu; między nim a środkiem barwy przechodzą płynnie.</summary>
    public double BodyFraction { get => GetValue(BodyFractionProperty); set => SetValue(BodyFractionProperty, value); }

    /// <summary>Barwa przy krawędzi, w którą korpus przechodzi płynnie; bez niej korpus ma jedną barwę.</summary>
    public IBrush? RimBrush { get => GetValue(RimBrushProperty); set => SetValue(RimBrushProperty, value); }

    /// <summary>
    /// Obrys płyty linią jednej jednostki tuż za jej krawędzią, więc wychodzi o jednostkę poza
    /// granice kontrolki.
    /// </summary>
    public IBrush? OutlineBrush { get => GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }

    /// <summary>
    /// Średnica, przy której jednostka to piksel: przy płycie dwa razy większej odstęp rowków
    /// i grubość linii też są dwa razy większe. Zero — stałe piksele przy każdym rozmiarze.
    /// </summary>
    public double DesignDiameter { get => GetValue(DesignDiameterProperty); set => SetValue(DesignDiameterProperty, value); }

    /// <summary>Odstęp rowków w jednostkach.</summary>
    public double GroovePitch { get => GetValue(GroovePitchProperty); set => SetValue(GroovePitchProperty, value); }

    /// <summary>Odległość zewnętrznego rowka od krawędzi płyty w jednostkach.</summary>
    public double GrooveInset { get => GetValue(GrooveInsetProperty); set => SetValue(GrooveInsetProperty, value); }

    /// <summary>Promień, poniżej którego rowków już nie ma.</summary>
    public double GrooveInnerFraction { get => GetValue(GrooveInnerFractionProperty); set => SetValue(GrooveInnerFractionProperty, value); }

    /// <summary>Barwa rozdzielaczy ścieżek, linii jednej jednostki rysowanych na rowkach.</summary>
    public IBrush? SeparatorBrush { get => GetValue(SeparatorBrushProperty); set => SetValue(SeparatorBrushProperty, value); }

    /// <summary>
    /// Promienie rozdzielaczy ścieżek. W XAML liczby rozdziela się przecinkami. Rozdzielacze należą
    /// do warstwy rowków i znikają razem z nią.
    /// </summary>
    public IReadOnlyList<double>? SeparatorFractions { get => GetValue(SeparatorFractionsProperty); set => SetValue(SeparatorFractionsProperty, value); }

    /// <summary>Promień naklejki z okładką.</summary>
    public double LabelFraction { get => GetValue(LabelFractionProperty); set => SetValue(LabelFractionProperty, value); }

    /// <summary>Obrys naklejki linią jednej jednostki tuż za jej brzegiem; rysuje się tylko z okładką.</summary>
    public IBrush? LabelOutlineBrush { get => GetValue(LabelOutlineBrushProperty); set => SetValue(LabelOutlineBrushProperty, value); }

    /// <summary>Promień otworu.</summary>
    public double HoleFraction { get => GetValue(HoleFractionProperty); set => SetValue(HoleFractionProperty, value); }

    /// <summary>Obrys otworu linią jednej jednostki tuż za jego brzegiem.</summary>
    public IBrush? HoleOutlineBrush { get => GetValue(HoleOutlineBrushProperty); set => SetValue(HoleOutlineBrushProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AngleProperty)
        {
            if (RenderTransform is RotateTransform rotate) rotate.Angle = Angle;
        }
        else if (change.Property != CoverProperty && change.Property.OwnerType == typeof(VinylDisc))
        {
            // Zmiana wyglądu: pędzel korpusu i pióra powstaną od nowa przy najbliższym rysowaniu.
            _body = null;
            _penUnit = double.NaN;
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4) return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2;
        var unit = DesignDiameter > 0 ? size / DesignDiameter : 1;
        if (unit != _penUnit) CreatePens(unit);

        DrawBody(context, centre, radius);
        if (ShowGrooves)
        {
            DrawGrooves(context, centre, radius, unit);
            DrawSeparators(context, centre, radius);
        }

        DrawInnerRing(context, centre, radius, size);
        DrawLabel(context, centre, radius);
        DrawHole(context, centre, radius);
    }

    private void CreatePens(double unit)
    {
        _penUnit = unit;
        _groovePen = Line(GrooveBrush, unit);
        _separatorPen = Line(SeparatorBrush, unit);
        _ringPen = Line(RingBrush, unit);
        _outlinePen = Line(OutlineBrush, unit);
        _labelOutlinePen = Line(LabelOutlineBrush, unit);
        _holeOutlinePen = Line(HoleOutlineBrush, unit);

        static Pen? Line(IBrush? brush, double thickness) => brush is null ? null : new Pen(brush, thickness);
    }

    /// <summary>
    /// Obrys tuż za brzegiem koła, jak <c>box-shadow: 0 0 0 1px</c> we wzorze: koło zachowuje
    /// swoją średnicę, a linia go otacza.
    /// </summary>
    private static void DrawOutline(DrawingContext context, Pen? pen, Point centre, double radius)
    {
        if (pen is null || !(radius > 0)) return;

        var r = radius + pen.Thickness / 2;
        context.DrawEllipse(null, pen, centre, r, r);
    }

    private void DrawBody(DrawingContext context, Point centre, double radius)
    {
        var core = (CoreBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x1A, 0x1A, 0x1C);
        var body = (BodyBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x0C, 0x0C, 0x0E);
        var colours = (core, body, (RimBrush as ISolidColorBrush)?.Color ?? body);

        if (_body is null || colours != _bodyColours)
        {
            _body = CreateBody(colours);
            _bodyColours = colours;
        }

        context.DrawEllipse(_body, null, centre, radius, radius);
        DrawOutline(context, _outlinePen, centre, radius);
    }

    private RadialGradientBrush CreateBody((Color Core, Color Body, Color Rim) colours)
    {
        // radial-gradient(circle, core 0 26%, body 26.4% 100%); z barwą brzegu korpus przechodzi
        // w nią aż do krawędzi.
        var (core, body, rim) = colours;

        return new RadialGradientBrush
        {
            Center = RelativePoint.Center,
            GradientOrigin = RelativePoint.Center,
            RadiusX = RelativeScalar.Middle,
            RadiusY = RelativeScalar.Middle,
            GradientStops =
            {
                new GradientStop(core, 0),
                new GradientStop(core, CoreFraction),
                new GradientStop(body, BodyFraction),
                new GradientStop(rim, 1),
            },
        };
    }

    private void DrawGrooves(DrawingContext context, Point centre, double radius, double unit)
    {
        var pen = _groovePen;
        var pitch = GroovePitch * unit;
        if (pen is null || !(pitch > 0)) return;

        var innerLimit = radius * GrooveInnerFraction;

        for (var r = radius - GrooveInset * unit; r > innerLimit; r -= pitch)
            context.DrawEllipse(null, pen, centre, r, r);
    }

    private void DrawSeparators(DrawingContext context, Point centre, double radius)
    {
        var pen = _separatorPen;
        var fractions = SeparatorFractions;
        if (pen is null || fractions is null) return;

        for (var i = 0; i < fractions.Count; i++)
        {
            var r = radius * fractions[i];
            if (r > 0) context.DrawEllipse(null, pen, centre, r, r);
        }
    }

    private void DrawInnerRing(DrawingContext context, Point centre, double radius, double size)
    {
        var pen = _ringPen;
        if (pen is null) return;

        // CSS: inset 8px on a 300px disc — scaled so it holds at other sizes too.
        var inset = 8.0 * (size / 300.0);
        context.DrawEllipse(null, pen, centre, radius - inset, radius - inset);
    }

    private void DrawLabel(DrawingContext context, Point centre, double radius)
    {
        var cover = Cover;
        if (cover is null) return;

        var labelRadius = radius * LabelFraction;
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

        DrawOutline(context, _labelOutlinePen, centre, labelRadius);
    }

    private void DrawHole(DrawingContext context, Point centre, double radius)
    {
        var holeRadius = radius * HoleFraction;

        if (HoleBrush is { } brush)
            context.DrawEllipse(brush, null, centre, holeRadius, holeRadius);

        DrawOutline(context, _holeOutlinePen, centre, holeRadius);
    }
}
