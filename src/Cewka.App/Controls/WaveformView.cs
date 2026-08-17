using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// Three overlapping travelling waves drawn above the seek bar.
/// <para>
/// The geometry is a direct port of the design mock. <see cref="Level"/> is the amplitude
/// the animation eases towards; stage 2 feeds it from the playback state, and stage 4 will
/// feed it from the real signal envelope without any change to this control.
/// </para>
/// </summary>
public sealed class WaveformView : Control
{
    private const int Curves = 3;
    private const double StepPixels = 3.0;

    /// <summary>How quickly the drawn amplitude catches up with <see cref="Level"/>.</summary>
    private const double AmplitudeEasing = 0.045;

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Level), 1.0);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(Stroke));

    private readonly RenderLoop _loop;
    private double _phase;
    private double _amplitude;
    private TimeSpan _lastFrame;

    static WaveformView()
    {
        AffectsRender<WaveformView>(StrokeProperty);
    }

    public WaveformView()
    {
        _loop = new RenderLoop(this, OnFrame);
        IsHitTestVisible = false;
    }

    /// <summary>Target amplitude, 0 to 1.</summary>
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    /// <summary>When false the waves settle to a nearly flat idle line.</summary>
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _loop.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _loop.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private bool _primed;

    private void OnFrame(TimeSpan timestamp)
    {
        // Start at the target rather than easing up from silence, so the first painted
        // frame already shows the wave — this matters for the snapshot tool as well.
        if (!_primed)
        {
            _primed = true;
            _amplitude = IsActive ? Math.Clamp(Level, 0, 1) : 0.06;
        }

        // Normalise to the 60 Hz step the mock was written against, so the motion
        // looks the same on a 144 Hz display as on a 60 Hz one.
        var delta = _lastFrame == TimeSpan.Zero ? 1.0 : (timestamp - _lastFrame).TotalSeconds * 60.0;
        _lastFrame = timestamp;
        delta = Math.Clamp(delta, 0.1, 4.0);

        var target = IsActive ? Math.Clamp(Level, 0, 1) : 0.06;
        _amplitude += (target - _amplitude) * AmplitudeEasing * delta;
        _phase += (IsActive ? 0.016 : 0.003) * delta;

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var brush = Stroke;
        if (brush is null) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 4 || height < 4) return;

        var pen = new Pen(brush, 1.25);

        for (var k = 0; k < Curves; k++)
        {
            var frequency = 1.6 + k * 0.9;
            var speed = 0.7 + k * 0.35;
            var amplitude = height * 0.31 * _amplitude
                            * (0.5 + 0.5 * Math.Sin(_phase * (0.35 + k * 0.14) + k * 2.2));

            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                var started = false;
                for (var x = 0.0; x <= width; x += StepPixels)
                {
                    var p = x / width;

                    // Envelope pins both ends to the centre line.
                    var envelope = Math.Pow(Math.Sin(Math.PI * p), 1.3);
                    var y = height / 2
                            + Math.Sin(p * Math.PI * 2 * frequency + _phase * speed * 2.4 + k * 1.7)
                            * amplitude * envelope;

                    var point = new Point(x, y);
                    if (!started) { sink.BeginFigure(point, false); started = true; }
                    else sink.LineTo(point);
                }

                if (started) sink.EndFigure(false);
            }

            using (context.PushOpacity(0.34 - k * 0.09))
            {
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }
}
