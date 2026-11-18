using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Cewka.Audio.Dsp;

namespace Cewka.App.Controls;

/// <summary>
/// Charakterystyka korektora parametrycznego: oś częstotliwości logarytmiczna 20 Hz–20 kHz z tymi
/// samymi opisami co suwaki korektora graficznego, oś ±12 dB, a gdy krzywa wychodzi poza ten zakres,
/// ±24 dB. Krzywa w barwie akcentu z wypełnieniem tej samej barwy o małym kryciu, z przedwzmacniaczem
/// i bez wyłączonych pasm.
/// </summary>
public sealed class EqResponseChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ParametricBand>?> BandsProperty =
        AvaloniaProperty.Register<EqResponseChart, IReadOnlyList<ParametricBand>?>(nameof(Bands));

    public static readonly StyledProperty<double> PreampProperty =
        AvaloniaProperty.Register<EqResponseChart, double>(nameof(Preamp));

    public static readonly StyledProperty<IBrush?> CurveBrushProperty =
        AvaloniaProperty.Register<EqResponseChart, IBrush?>(nameof(CurveBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<EqResponseChart, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<EqResponseChart, IBrush?>(nameof(LabelBrush));

    /// <summary>Opisy osi częstotliwości: te same zapisy co pod suwakami korektora graficznego.</summary>
    internal static readonly (double Frequency, string Label)[] FrequencyLabels =
    [
        (32, "32"), (64, "64"), (125, "125"), (250, "250"), (500, "500"),
        (1000, "1k"), (2000, "2k"), (4000, "4k"), (8000, "8k"), (16000, "16k"),
    ];

    private const double MinFrequency = 20;
    private const double MaxFrequency = 20000;

    /// <summary>Częstotliwość próbkowania, dla której liczona jest krzywa; urządzenia różnią się tu tylko tuż pod 20 kHz.</summary>
    private const double SampleRate = 48000;

    private const double LabelFontSize = 9.5;
    private const double LeftGutter = 26;
    private const double BottomGutter = 15;
    private const double FillOpacity = 0.16;

    private double[] _frequencies = [];
    private double[] _response = [];

    static EqResponseChart()
    {
        AffectsRender<EqResponseChart>(BandsProperty, PreampProperty, CurveBrushProperty, GridBrushProperty, LabelBrushProperty);
    }

    public IReadOnlyList<ParametricBand>? Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    public double Preamp { get => GetValue(PreampProperty); set => SetValue(PreampProperty, value); }

    public IBrush? CurveBrush { get => GetValue(CurveBrushProperty); set => SetValue(CurveBrushProperty, value); }

    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    /// <summary>Zakres osi w decybelach: 12, a gdy krzywa wychodzi poza ±12 dB, 24.</summary>
    public double RangeDb { get; private set; } = 12;

    /// <summary>Krzywa w decybelach dla podanych częstotliwości; do testów i do rysowania.</summary>
    public static void Compute(IReadOnlyList<ParametricBand>? bands, double preamp, ReadOnlySpan<double> frequencies, Span<double> result)
    {
        var list = bands is null ? [] : bands as ParametricBand[] ?? [.. bands];
        ParametricEqualiser.Response(list, preamp, SampleRate, frequencies, result);
    }

    public static double RangeFor(ReadOnlySpan<double> response)
    {
        foreach (var value in response)
            if (Math.Abs(value) > 12.0001) return 24;
        return 12;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

    public override void Render(DrawingContext context)
    {
        var plot = new Rect(LeftGutter, 6, Math.Max(1, Bounds.Width - LeftGutter - 6), Math.Max(1, Bounds.Height - 6 - BottomGutter));
        var points = Math.Clamp((int)plot.Width, 2, 600);

        if (_frequencies.Length != points)
        {
            _frequencies = new double[points];
            _response = new double[points];
            for (var i = 0; i < points; i++) _frequencies[i] = FrequencyAt(i / (double)(points - 1));
        }

        Compute(Bands, Preamp, _frequencies, _response);
        RangeDb = RangeFor(_response);

        double X(double frequency) => plot.Left + plot.Width * Math.Log(frequency / MinFrequency) / Math.Log(MaxFrequency / MinFrequency);
        double Y(double db) => plot.Top + plot.Height * (RangeDb - Math.Clamp(db, -RangeDb, RangeDb)) / (2 * RangeDb);

        var typeface = new Typeface(TextElement.GetFontFamily(this));

        if (GridBrush is { } grid)
        {
            var thin = new Pen(grid, 1);
            foreach (var (frequency, _) in FrequencyLabels)
            {
                var x = Math.Round(X(frequency)) + 0.5;
                context.DrawLine(thin, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }

            context.DrawLine(thin, new Point(plot.Left, Math.Round(Y(RangeDb)) + 0.5), new Point(plot.Right, Math.Round(Y(RangeDb)) + 0.5));
            context.DrawLine(thin, new Point(plot.Left, Math.Round(Y(-RangeDb)) - 0.5), new Point(plot.Right, Math.Round(Y(-RangeDb)) - 0.5));

            // Linia zera mocniejsza od siatki.
            var zeroY = Math.Round(Y(0)) + 0.5;
            context.DrawLine(new Pen(grid, 1.5), new Point(plot.Left, zeroY), new Point(plot.Right, zeroY));
        }

        if (LabelBrush is { } label)
        {
            var range = RangeDb.ToString("0", CultureInfo.InvariantCulture);
            DrawLabel(context, "+" + range, typeface, label, new Point(LeftGutter - 4, Y(RangeDb)), alignRight: true);
            DrawLabel(context, "0", typeface, label, new Point(LeftGutter - 4, Y(0)), alignRight: true);
            DrawLabel(context, "−" + range, typeface, label, new Point(LeftGutter - 4, Y(-RangeDb)), alignRight: true);

            foreach (var (frequency, text) in FrequencyLabels)
            {
                var formatted = Text(text, typeface, label);
                context.DrawText(formatted, new Point(X(frequency) - formatted.Width / 2, plot.Bottom + 2));
            }
        }

        if (CurveBrush is not { } curve) return;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineContext = line.Open())
        using (var areaContext = area.Open())
        {
            var zero = Y(0);
            areaContext.BeginFigure(new Point(plot.Left, zero), isFilled: true);

            for (var i = 0; i < points; i++)
            {
                var point = new Point(plot.Left + plot.Width * i / (points - 1), Y(_response[i]));
                if (i == 0) lineContext.BeginFigure(point, isFilled: false);
                else lineContext.LineTo(point);
                areaContext.LineTo(point);
            }

            areaContext.LineTo(new Point(plot.Right, zero));
            areaContext.EndFigure(true);
            lineContext.EndFigure(false);
        }

        if (curve is ISolidColorBrush solid)
            context.DrawGeometry(new SolidColorBrush(solid.Color, solid.Opacity * FillOpacity), null, area);

        context.DrawGeometry(null, new Pen(curve, 1.6, lineJoin: PenLineJoin.Round), line);
    }

    private static double FrequencyAt(double fraction) => MinFrequency * Math.Pow(MaxFrequency / MinFrequency, fraction);

    private static FormattedText Text(string text, Typeface typeface, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, LabelFontSize, brush);

    private static void DrawLabel(DrawingContext context, string text, Typeface typeface, IBrush brush, Point anchor, bool alignRight)
    {
        var formatted = Text(text, typeface, brush);
        var x = alignRight ? anchor.X - formatted.Width : anchor.X;
        context.DrawText(formatted, new Point(x, anchor.Y - formatted.Height / 2));
    }

    /// <summary>Dla czytnika ekranu wykres jest obrazem z nazwą.</summary>
    private sealed class ChartAutomationPeer(EqResponseChart owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
