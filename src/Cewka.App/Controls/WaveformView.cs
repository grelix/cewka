using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// Trzy nakładające się fale nad paskiem przewijania; <see cref="Level"/> to amplituda
/// z obwiedni sygnału, do której dochodzi animacja.
/// <para>
/// W pauzie fala opada do linii spoczynkowej, a pętla klatek staje, gdy ruch ustanie.
/// W trybie ograniczonym fala jest prostą linią.
/// </para>
/// </summary>
public sealed class WaveformView : Control
{
    private const int Curves = 3;
    private const double StepPixels = 3.0;

    /// <summary>
    /// How quickly the drawn amplitude catches up with <see cref="Level"/>. Przy tej wartości
    /// fala dochodzi do poziomu w niecałe sto milisekund, więc ruch odpowiada temu, co słychać.
    /// </summary>
    private const double AmplitudeEasing = 0.17;

    /// <summary>Wysokość fali jako część wysokości kontrolki, przy pełnej amplitudzie.</summary>
    private const double AmplitudeShare = 0.42;

    /// <summary>Amplituda linii spoczynkowej w pauzie.</summary>
    private const double IdleAmplitude = 0.06;

    /// <summary>Poniżej tej różnicy zmiana amplitudy jest niewidoczna, więc pętla może stanąć.</summary>
    private const double Settled = 0.002;

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Level), 1.0);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<bool> IsReducedProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(IsReduced));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(Stroke));

    private readonly RenderLoop _loop;
    private double _phase;
    private double _amplitude;
    private TimeSpan _lastFrame;
    private bool _primed;
    private bool _frozen;
    private Pen? _pen;
    private double[] _envelope = [];
    private double _envelopeWidth;

    public WaveformView()
    {
        _loop = new RenderLoop(this, OnFrame);
        IsHitTestVisible = false;
    }

    /// <summary>Target amplitude, 0 to 1.</summary>
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    /// <summary>When false the waves settle to a nearly flat idle line.</summary>
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>Tryb ograniczonego ruchu: zamiast fali prosta linia.</summary>
    public bool IsReduced { get => GetValue(IsReducedProperty); set => SetValue(IsReducedProperty, value); }

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Pętla działa tylko wtedy, gdy fala się porusza.</summary>
    public bool IsLooping => _loop.IsRunning;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StrokeProperty)
        {
            _pen = Stroke is { } brush ? new Pen(brush, 1.25) : null;
            InvalidateVisual();
        }
        else if (change.Property == IsActiveProperty || change.Property == IsReducedProperty)
        {
            Wake();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Wake();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _loop.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Wake()
    {
        if (_frozen || _loop.IsRunning || VisualRoot is null) return;

        // Bez wyzerowania pierwsza klatka po przerwie dostałaby największy krok i fala by skoczyła.
        _lastFrame = TimeSpan.Zero;
        _loop.Start();
    }

    private double Target => IsReduced ? 0 : IsActive ? Math.Clamp(Level, 0, 1) : IdleAmplitude;

    private void OnFrame(TimeSpan timestamp)
    {
        // Start at the target rather than easing up from silence, so the first painted
        // frame already shows the wave — this matters for the snapshot tool as well.
        if (!_primed)
        {
            _primed = true;
            _amplitude = Target;
        }

        // Normalise to the 60 Hz step the mock was written against, so the motion
        // looks the same on a 144 Hz display as on a 60 Hz one.
        var delta = _lastFrame == TimeSpan.Zero ? 1.0 : (timestamp - _lastFrame).TotalSeconds * 60.0;
        _lastFrame = timestamp;
        delta = Math.Clamp(delta, 0.1, 4.0);

        var target = Target;
        _amplitude += (target - _amplitude) * AmplitudeEasing * delta;

        var moving = IsActive && !IsReduced;
        if (moving) _phase += 0.016 * delta;

        if (!moving && Math.Abs(target - _amplitude) < Settled)
        {
            _amplitude = target;
            _loop.Stop();
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Zatrzymuje fale na początku cyklu, dla narzędzia zrzutów. Amplituda ustawiana jest od
    /// razu na docelową, żeby zrzut tuż po pauzie nie złapał fali w trakcie opadania.
    /// </summary>
    public void FreezeForCapture()
    {
        _frozen = true;
        _loop.Stop();

        _primed = true;
        _phase = 0;
        _amplitude = Target;

        InvalidateVisual();
    }

    /// <summary>
    /// Obwiednia przypina oba końce fali do linii środkowej. Zależy tylko od szerokości,
    /// więc liczona jest raz na rozmiar, a nie w każdej klatce.
    /// </summary>
    private double[] Envelope(double width)
    {
        if (width == _envelopeWidth) return _envelope;

        var count = (int)(width / StepPixels) + 1;
        var envelope = new double[count];
        for (var i = 0; i < count; i++)
            envelope[i] = Math.Pow(Math.Sin(Math.PI * Math.Min(1, i * StepPixels / width)), 1.3);

        _envelopeWidth = width;
        _envelope = envelope;
        return envelope;
    }

    public override void Render(DrawingContext context)
    {
        var pen = _pen;
        if (pen is null) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 4 || height < 4) return;

        if (_amplitude < Settled)
        {
            using (context.PushOpacity(0.34))
                context.DrawLine(pen, new Point(0, height / 2), new Point(width, height / 2));
            return;
        }

        var envelope = Envelope(width);

        for (var k = 0; k < Curves; k++)
        {
            var frequency = 1.6 + k * 0.9;
            var speed = 0.7 + k * 0.35;
            var amplitude = height * AmplitudeShare * _amplitude
                            * (0.5 + 0.5 * Math.Sin(_phase * (0.35 + k * 0.14) + k * 2.2));

            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                for (var i = 0; i < envelope.Length; i++)
                {
                    var x = i * StepPixels;
                    var p = x / width;
                    var y = height / 2
                            + Math.Sin(p * Math.PI * 2 * frequency + _phase * speed * 2.4 + k * 1.7)
                            * amplitude * envelope[i];

                    var point = new Point(x, y);
                    if (i == 0) sink.BeginFigure(point, false);
                    else sink.LineTo(point);
                }

                sink.EndFigure(false);
            }

            using (context.PushOpacity(0.34 - k * 0.09))
            {
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }
}
