using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Cewka.App.Services;
using SkiaSharp;

namespace Cewka.App.Controls;

/// <summary>
/// Zorza: tło sceny rysowane shaderem na całym kadrze. Reaguje na dźwięk trzema pasmami (bas wysokością
/// wstęg, środek tempem falowania, góra drobną fakturą) i poziomem; pasma są wygładzone z narastaniem
/// 80 ms i opadaniem 250 ms. Tempo falowania jest całką czasu, więc zmiana pasma nie powoduje skoku.
/// <para>
/// Ogranicznik błysków to ten sam <see cref="FlashLimiter"/>, co w pozostałych wizualizacjach. Luminancję
/// klatki mierzy się na rastrze 64 x 36 rysowanym tym samym shaderem. Wstrzymane narastanie przyciemnia
/// wstęgi mnożnikiem, a wstrzymane opadanie podtrzymuje bas i poziom. Oba szuka się pomiarem, bo luminancja
/// nie jest liniowa względem kanału sRGB: mnożnik liczony proporcją przyciemniał obraz poniżej dozwolonej
/// wartości i sam tworzył przejścia.
/// </para>
/// Pętla chodzi tylko przy odtwarzaniu, do 60 kl./s; w pauzie nie zamawia klatek.
/// </summary>
public sealed class AuroraView : Control
{
    /// <summary>Bazowe tempo falowania i wpływ środka pasma, w jednostkach czasu shadera na sekundę.</summary>
    public const double BaseRate = 0.30;

    public const double MidRate = 0.45;

    /// <summary>Górny limit tempa; razem ze stałymi częstotliwościami w shaderze ogranicza prędkość ruchu.</summary>
    public const double MaxRate = 0.75;

    private const int LowBands = 8;
    private const int MidBands = 28;
    private const double Epsilon = 1e-4;
    private const int SearchSteps = 7;

    private readonly RenderLoop _loop;
    private readonly FlashLimiter _limiter = new();
    private readonly SpectrumBars _spectrum = new();

    /// <summary>Widmo, z którego zorza bierze pasma; scena ustawia mu opadanie.</summary>
    public SpectrumBars Spectrum => _spectrum;
    private readonly FrameBudget _budget = new();
    private readonly AuroraRenderer _measureRenderer = new();
    private readonly AuroraBuffer _buffer = new();

    private SKBitmap? _measureBitmap;
    private SKCanvas? _measureCanvas;
    private SKColor _colour1 = new(120, 160, 220);
    private SKColor _colour2 = new(230, 190, 120);
    private double _low;
    private double _mid;
    private double _high;
    private double _level;
    private double _phase;
    private double _luminance;
    private double _gain = 1;
    private double _lift;
    private AuroraFrame _frame;
    private bool _active;
    private bool _hasLast;
    private TimeSpan _last;
    private int _frames;

    public AuroraView()
    {
        IsHitTestVisible = false;
        _loop = new RenderLoop(this, OnFrame);
        _frame = Compose(0, 0, 1);
    }

    /// <summary>Dane dźwięku; bez nich Zorza płynie w ciszy.</summary>
    public ISceneAudioSource? Source { get; set; }

    /// <summary>Sufit jasności wstęg; wyższy niż domyślny ustawia tylko test obciążeniowy ogranicznika.</summary>
    internal float Ceiling { get; set; } = AuroraRenderer.DefaultCeiling;

    /// <summary>Ogranicza do 30 kl./s (zasilanie bateryjne).</summary>
    public bool Throttled { get; set; }

    public FlashLimiter Limiter => _limiter;

    public FrameBudget Budget => _budget;

    public bool IsRunning => _loop.IsRunning;

    public int FramesRendered => _frames;

    /// <summary>Mnożnik wstęg z ogranicznika błysków, 1 = bez tłumienia.</summary>
    public double LimiterGain => _gain;

    /// <summary>Luminancja klatki przed ogranicznikiem.</summary>
    public double EstimatedLuminance => _luminance;

    /// <summary>Podtrzymanie z ogranicznika: 0 bez podtrzymania, do 1 przy pełnym uniesieniu basu i poziomu.</summary>
    public double HoldLift => _lift;

    /// <summary>Całka tempa falowania, czyli czas shadera.</summary>
    public double Phase => _phase;

    public double Low => _low;

    public double Mid => _mid;

    public double High => _high;

    public double Level => _level;

    /// <summary>Parametry ostatnio przygotowanej klatki.</summary>
    public AuroraFrame Frame => _frame;

    /// <summary>Bufor wewnętrzny; testy sprawdzają, że w ruchu nie powstaje od nowa.</summary>
    internal AuroraBuffer Buffer => _buffer;

    /// <summary>
    /// Mnożnik rozdzielczości bufora zamiast wynikającego z <see cref="Budget"/>. Ustawiają go tylko testy
    /// ogranicznika błysków, bo shader rysowany na procesorze jest dla nich najdroższą częścią pomiaru.
    /// </summary>
    internal double? BufferScale { get; set; }

    /// <summary>Dwie barwy palety okładki: dół i góra wstęg. Czerwień obcina też shader.</summary>
    public void SetColours(Color first, Color second)
    {
        first = SceneAudioView.LimitRed(first);
        second = SceneAudioView.LimitRed(second);
        _colour1 = new SKColor(first.R, first.G, first.B);
        _colour2 = new SKColor(second.R, second.G, second.B);
        _frame = Compose(_frame.Low, _frame.Level, (float)_gain);
        InvalidateVisual();
    }

    /// <summary>Włącza albo wyłącza pętlę: aktywna tylko przy odtwarzaniu i widocznej Zorzy.</summary>
    public void SetActive(bool active)
    {
        active &= Source is not null;
        if (_active == active) return;

        _active = active;
        _hasLast = false;
        if (active) _loop.Start();
        else _loop.Stop();
    }

    private double MinInterval => Throttled ? 1.0 / 30 : 1.0 / 60;

    private void OnFrame(TimeSpan now)
    {
        if (!_hasLast)
        {
            _last = now;
            _hasLast = true;
            Step(1.0 / 60);
            return;
        }

        var dt = (now - _last).TotalSeconds;
        if (dt < MinInterval - 0.002) return;

        _last = now;
        if (_budget.Observe(dt)) InvalidateVisual();
        Step(dt);
    }

    /// <summary>
    /// Jeden krok: odczyt pasm, wygładzenie, całkowanie tempa, pomiar luminancji, ogranicznik i
    /// unieważnienie widoku. Wołany przez pętlę, a w testach bezpośrednio.
    /// </summary>
    public void Step(double dt)
    {
        dt = Math.Clamp(double.IsFinite(dt) ? dt : 0, 0, 0.25);

        double lowTarget = 0, midTarget = 0, highTarget = 0, levelTarget = 0;
        if (Source is { } source)
        {
            _spectrum.Update(source.ReadSpectrum(), dt);
            var bars = _spectrum.Bars;
            lowTarget = Average(bars, 0, LowBands);
            midTarget = Average(bars, LowBands, MidBands);
            highTarget = Average(bars, MidBands, SpectrumBars.Count);

            var levels = source.ReadLevels(1024);
            var rms = (levels.RmsLeft + levels.RmsRight) * 0.5;
            levelTarget = double.IsFinite(rms) ? Math.Clamp(rms * 3, 0, 1) : 0;
        }

        _low = Slew(_low, lowTarget, dt);
        _mid = Slew(_mid, midTarget, dt);
        _high = Slew(_high, highTarget, dt);
        _level = Slew(_level, levelTarget, dt);
        _phase += Math.Min(MaxRate, BaseRate + MidRate * _mid) * dt;

        _luminance = Measure(_low, _level, 1);
        var allowed = _limiter.Limit(_luminance, dt);

        var low = _low;
        var level = _level;
        _lift = 0;
        _gain = 1;
        if (allowed > _luminance + Epsilon)
        {
            // Ogranicznik wstrzymuje opadanie: bas i poziom zostają wyżej, żeby obraz nie pociemniał szybciej.
            _lift = FindLift(allowed);
            low = _low + (1 - _low) * _lift;
            level = _level + (1 - _level) * _lift;
        }
        else if (allowed < _luminance - Epsilon)
        {
            _gain = FindGain(allowed);
        }

        _frame = Compose(low, level, (float)_gain);
        _frames++;
        InvalidateVisual();
    }

    private static double Average(ReadOnlySpan<float> bars, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to; i++) sum += bars[i];
        return sum / (to - from);
    }

    private static double Slew(double value, double target, double dt) => target > value
        ? Math.Min(target, value + dt / FlashLimiter.RiseSeconds)
        : Math.Max(target, value - dt / FlashLimiter.FallSeconds);

    private AuroraFrame Compose(double low, double level, double brightness) => new(
        (float)_phase, (float)low, (float)_mid, (float)_high, (float)level, _colour1, _colour2, (float)brightness, Ceiling);

    /// <summary>Najmniejsze uniesienie 0 do 1, przy którym obraz nie schodzi poniżej <paramref name="allowed"/>.</summary>
    private double FindLift(double allowed)
    {
        if (Measure(1, 1, 1) <= allowed) return 1;

        double lo = 0, hi = 1;
        for (var i = 0; i < SearchSteps; i++)
        {
            var middle = (lo + hi) / 2;
            if (Measure(_low + (1 - _low) * middle, _level + (1 - _level) * middle, 1) >= allowed) hi = middle;
            else lo = middle;
        }

        return hi;
    }

    /// <summary>Największy mnożnik wstęg 0 do 1, przy którym obraz nie przekracza <paramref name="allowed"/>.</summary>
    private double FindGain(double allowed)
    {
        double lo = 0, hi = 1;
        for (var i = 0; i < SearchSteps; i++)
        {
            var middle = (lo + hi) / 2;
            if (Measure(_low, _level, middle) <= allowed) lo = middle;
            else hi = middle;
        }

        return lo;
    }

    private double Measure(double low, double level, double brightness)
    {
        if (_measureBitmap is null)
        {
            _measureBitmap = new SKBitmap(new SKImageInfo(FrameLuminance.Width, FrameLuminance.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            _measureCanvas = new SKCanvas(_measureBitmap);
        }

        _measureRenderer.Ceiling = Ceiling;
        _measureRenderer.Draw(_measureCanvas!, FrameLuminance.Width, FrameLuminance.Height, (float)_phase, (float)low,
            (float)_mid, (float)_high, (float)level, _colour1, _colour2, (float)brightness);
        return FrameLuminance.Measure(_measureBitmap.GetPixelSpan());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _measureCanvas?.Dispose();
        _measureBitmap?.Dispose();
        _measureCanvas = null;
        _measureBitmap = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 2 || bounds.Height < 2) return;

        var (width, height) = AuroraBuffer.SizeFor(BufferScale ?? _budget.Scale);
        context.Custom(new DrawOperation(bounds, _buffer, _frame, width, height));
    }

    private sealed class DrawOperation(Rect bounds, AuroraBuffer buffer, AuroraFrame frame, int width, int height)
        : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point point) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;

            using var lease = feature.Lease();
            buffer.Present(lease.SkCanvas, lease.GrContext, in frame, (float)bounds.Width, (float)bounds.Height, width, height);
        }
    }
}
