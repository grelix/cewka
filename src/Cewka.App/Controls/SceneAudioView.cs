using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.Audio.Dsp;

namespace Cewka.App.Controls;

/// <summary>Co rysuje <see cref="SceneAudioView"/>.</summary>
public enum SceneAudioMode
{
    None,
    Spectrum,
    Waveform,

    /// <summary>Obwiednia utworu z głowicą postępu.</summary>
    Horizon,

    /// <summary>Pierścień widma wokół płyty.</summary>
    Ring,
}

/// <summary>
/// Wizualizacje sceny oparte na danych dźwięku: widmo, fala, Horyzont i pierścień widma płyty.
/// Wszystkie korzystają z jednej pętli klatek (<see cref="RenderLoop"/>) i z jednego ogranicznika
/// błysków (<see cref="FlashLimiter"/>): szacunkowa luminancja klatki przechodzi przez ogranicznik,
/// a wynikowe wzmocnienie ścisza całą wizualizację. Pędzle, pióra i bufory powstają raz.
/// W pauzie pętla stoi, więc nie ma żadnej pracy.
/// </summary>
public sealed class SceneAudioView : Control
{
    private const int WaveWindow = 4096;
    private const int SearchSamples = 1024;
    private const int WavePoints = 480;
    private const int StereoPoints = 320;
    private const int RingTicks = 72;
    private const double WaveSeconds = 0.04;

    private readonly RenderLoop _loop;
    private readonly FlashLimiter _limiter = new();
    private readonly SpectrumBars _spectrum = new();
    private readonly ScopeTrigger _trigger = new();
    private readonly float[] _left = new float[WaveWindow];
    private readonly float[] _right = new float[WaveWindow];
    private readonly float[] _mid = new float[WaveWindow];
    private readonly Point[] _points = new Point[WavePoints];

    private readonly GradientStop _stopLow = new(Colors.Gray, 0);
    private readonly GradientStop _stopHigh = new(Colors.White, 1);
    private readonly LinearGradientBrush _barBrush;
    private readonly SolidColorBrush _peakBrush = new(Colors.White);
    private readonly Pen _linePen = new(Brushes.White, 3.5, lineCap: PenLineCap.Round);
    private readonly Pen _glowPen = new(Brushes.White, 11, lineCap: PenLineCap.Flat);
    private readonly Pen _tickPen = new(Brushes.White, 3, lineCap: PenLineCap.Round);
    private readonly SolidColorBrush _lineBrush = new(Colors.White);
    private readonly SolidColorBrush _glowBrush = new(Colors.White);
    private readonly SolidColorBrush _inkBrush = new(Colors.White);
    private readonly SolidColorBrush _dimPeakBrush = new(Colors.White);
    private readonly SolidColorBrush _dimBodyBrush = new(Colors.White);
    private readonly SolidColorBrush _litPeakBrush = new(Colors.White);
    private readonly SolidColorBrush _litBodyBrush = new(Colors.White);
    private readonly SolidColorBrush _headBrush = new(Colors.White);
    private readonly Pen _headPen = new(Brushes.White, 3, lineCap: PenLineCap.Round);

    private Color _low = Color.FromRgb(120, 160, 220);
    private Color _high = Color.FromRgb(230, 190, 120);
    private RenderTargetBitmap? _horizonDim;
    private RenderTargetBitmap? _horizonLit;
    private Size _horizonSize;
    private FileEnvelope? _envelope;
    private double _envelopeNorm = 1;
    private double _progress;
    private double _headLevel;
    private int _horizonRebuilds;
    private SceneAudioMode _mode;
    private bool _active;
    private bool _mono;
    private double _gain = 1;
    private double _luminance;
    private TimeSpan _last;
    private bool _hasLast;
    private int _frames;

    public SceneAudioView()
    {
        IsHitTestVisible = false;
        _barBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops = [_stopLow, _stopHigh],
        };
        _loop = new RenderLoop(this, OnFrame);
        AdoptColours();
    }

    /// <summary>Dane dźwięku; bez nich wizualizacja stoi pusta.</summary>
    public ISceneAudioSource? Source { get; set; }

    public SceneAudioMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;

            _mode = value;
            ResetState();
            InvalidateVisual();
        }
    }

    public SpectrumShape Shape { get; set; }

    public WaveformKind Kind { get; set; }

    /// <summary>Zgłaszane, gdy zmienia się wykrycie nagrania mono w widoku stereo.</summary>
    public event Action<bool>? MonoChanged;

    public bool IsMono => _mono;

    /// <summary>Wzmocnienie z ogranicznika błysków, 1 = bez tłumienia.</summary>
    public double LimiterGain => _gain;

    /// <summary>Szacunkowa luminancja klatki przed ogranicznikiem.</summary>
    public double EstimatedLuminance => _luminance;

    public FlashLimiter Limiter => _limiter;

    public int FramesRendered => _frames;

    public SpectrumBars Spectrum => _spectrum;

    public ScopeTrigger Trigger => _trigger;

    public bool IsRunning => _loop.IsRunning;

    /// <summary>Postęp utworu 0 do 1 dla Horyzontu, czytany w każdej klatce.</summary>
    public Func<double>? ProgressProvider { get; set; }

    /// <summary>Obwiednia pliku dla Horyzontu; null rysuje płaską linię z postępem.</summary>
    public FileEnvelope? Envelope
    {
        get => _envelope;
        set
        {
            if (ReferenceEquals(_envelope, value)) return;

            _envelope = value;
            _envelopeNorm = 1;
            if (value is not null)
            {
                float top = 0.05f;
                for (var i = 0; i < value.Count; i++) top = Math.Max(top, value.PeakAt(i));
                _envelopeNorm = top;
            }

            DropHorizon();
            InvalidateVisual();
        }
    }

    /// <summary>Ostatni odczytany postęp Horyzontu.</summary>
    public double HorizonProgress => _progress;

    /// <summary>Ograniczony ruch: głowica się przesuwa, ale bez poświaty od dźwięku.</summary>
    public bool CalmHead { get; set; }

    /// <summary>Poświata głowicy 0 do 1, z narastaniem 80 ms i opadaniem 250 ms.</summary>
    public double HeadLevel => _headLevel;

    /// <summary>Ile razy narysowano od nowa bitmapy obwiedni; w ruchu nie rośnie.</summary>
    public int HorizonRebuilds => _horizonRebuilds;

    private void DropHorizon()
    {
        _horizonDim?.Dispose();
        _horizonLit?.Dispose();
        _horizonDim = null;
        _horizonLit = null;
    }

    /// <summary>Dwie barwy palety: dół i góra gradientu (Widmo), linia (Fala) i obwiednia (Horyzont).</summary>
    public void SetColours(Color low, Color high)
    {
        _low = LimitRed(low);
        _high = LimitRed(high);
        AdoptColours();
        DropHorizon();
        InvalidateVisual();
    }

    /// <summary>Włącza albo wyłącza pętlę: aktywna tylko przy odtwarzaniu, widocznej wizualizacji i pełnym ruchu.</summary>
    public void SetActive(bool active)
    {
        active &= _mode != SceneAudioMode.None && Source is not null;
        if (_active == active) return;

        _active = active;
        _hasLast = false;
        if (active) _loop.Start();
        else _loop.Stop();
    }

    /// <summary>Nasycona czerwień nie pulsuje: w elementach reagujących nasycenie czerwieni najwyżej 0,6.</summary>
    public static Color LimitRed(Color colour)
    {
        var hsv = colour.ToHsv();
        if ((hsv.H < 20 || hsv.H > 340) && hsv.S > 0.6)
            return new HsvColor(colour.A / 255.0, hsv.H, 0.6, hsv.V).ToRgb();
        return colour;
    }

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private void AdoptColours()
    {
        _stopLow.Color = _low;
        _stopHigh.Color = _high;
        _peakBrush.Color = Mix(_high, Color.FromRgb(235, 235, 235), 0.5);
        _lineBrush.Color = Mix(_high, Color.FromRgb(235, 235, 235), 0.35);
        _glowBrush.Color = Color.FromArgb(70, _low.R, _low.G, _low.B);
        _inkBrush.Color = Mix(_high, Color.FromRgb(228, 228, 228), 0.7);
        _dimPeakBrush.Color = Color.FromArgb(60, _low.R, _low.G, _low.B);
        _dimBodyBrush.Color = Color.FromArgb(110, _high.R, _high.G, _high.B);
        _litPeakBrush.Color = Color.FromArgb(120, _high.R, _high.G, _high.B);
        _litBodyBrush.Color = Mix(_high, Color.FromRgb(235, 235, 235), 0.35);
        _headBrush.Color = Mix(_high, Color.FromRgb(235, 235, 235), 0.5);
        _headPen.Brush = _headBrush;
        _linePen.Brush = _lineBrush;
        _glowPen.Brush = _glowBrush;
        _tickPen.Brush = _inkBrush;
    }

    private void ResetState()
    {
        _spectrum.Reset();
        _trigger.Reset();
        _limiter.Reset();
        _gain = 1;
        _luminance = 0;
        _headLevel = 0;
        _hasLast = false;
        SetMono(false);
    }

    private void SetMono(bool mono)
    {
        if (_mono == mono) return;

        _mono = mono;
        MonoChanged?.Invoke(mono);
    }

    private double MinInterval => _mode == SceneAudioMode.Horizon ? 1.0 / 30 : 1.0 / 60;

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
        Step(dt);
    }

    /// <summary>
    /// Jeden krok: odczyt danych, balistyka, wyzwalanie, ogranicznik błysków i unieważnienie widoku.
    /// Nie alokuje; wołany przez pętlę, a w testach bezpośrednio.
    /// </summary>
    public void Step(double dt)
    {
        if (Source is not { } source || _mode == SceneAudioMode.None) return;

        dt = Math.Clamp(dt, 0, 0.25);
        switch (_mode)
        {
            case SceneAudioMode.Spectrum:
                _spectrum.Update(source.ReadSpectrum(), dt);
                // Strefa 1600 x 410 to około 32% kadru 1920 x 1080; bez przerw i górnego zapasu słupki pełnej wysokości zajmują około 22%.
                _luminance = EstimateBars(0.22);
                break;
            case SceneAudioMode.Ring:
                _spectrum.Update(source.ReadSpectrum(), dt);
                _luminance = EstimateBars(0.04);
                break;
            case SceneAudioMode.Waveform:
                StepWave(source, dt);
                break;
            case SceneAudioMode.Horizon:
                StepHorizon(source, dt);
                break;
        }

        var allowed = _limiter.Limit(_luminance, dt);
        _gain = _limiter.Gain(_luminance, allowed);
        if (_mode is SceneAudioMode.Spectrum or SceneAudioMode.Ring) HoldBars(allowed);
        _frames++;
        InvalidateVisual();
    }

    /// <summary>
    /// Mnożnik jasności umie tylko przyciemnić. Gdy ogranicznik nie pozwala jeszcze obrazowi pociemnieć,
    /// słupki zostają wyżej, bo inaczej opadłyby swobodnie i obraz dalej by błyskał.
    /// </summary>
    private void HoldBars(double allowed)
    {
        if (_luminance > MinimumBarsLuminance && allowed > _luminance) _spectrum.Lift((float)(allowed / _luminance));
    }

    private const double MinimumBarsLuminance = 1e-4;

    private void StepHorizon(ISceneAudioSource source, double dt)
    {
        var progress = ProgressProvider?.Invoke() ?? 0;
        _progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;

        // Poświata głowicy od poziomu sygnału; tempo zmian zgodne z ogranicznikiem błysków.
        var levels = source.ReadLevels(1024);
        var rms = (levels.RmsLeft + levels.RmsRight) * 0.5;
        var target = double.IsFinite(rms) && !CalmHead ? Math.Clamp(rms * 3, 0, 1) : 0;
        _headLevel = target > _headLevel
            ? Math.Min(target, _headLevel + dt / FlashLimiter.RiseSeconds)
            : Math.Max(target, _headLevel - dt / FlashLimiter.FallSeconds);

        // Poświata to wąski pasek, więc udział w luminancji klatki jest znikomy.
        _luminance = Luma(_headBrush.Color) * 0.004 * _headLevel;
    }

    private double EstimateBars(double areaShare)
    {
        double sum = 0;
        foreach (var value in _spectrum.Bars) sum += value;
        return sum / SpectrumBars.Count * areaShare * Luma(Mix(_low, _high, 0.5));
    }

    private int WaveLength => Math.Clamp((int)((Source?.SampleRate > 0 ? Source.SampleRate : 44100) * WaveSeconds), 256, WaveWindow - SearchSamples);

    private void StepWave(ISceneAudioSource source, double dt)
    {
        var length = WaveLength + SearchSamples;
        var left = _left.AsSpan(0, length);
        var right = _right.AsSpan(0, length);
        if (!source.ReadWaveform(left, right))
        {
            left.Clear();
            right.Clear();
        }

        float peak = 0;
        float side = 0;
        for (var i = 0; i < length; i++)
        {
            var l = float.IsFinite(left[i]) ? left[i] : 0;
            var r = float.IsFinite(right[i]) ? right[i] : 0;
            _mid[i] = (l + r) * 0.5f;
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            side = Math.Max(side, Math.Abs(l - r));
        }

        _trigger.Adapt(peak, dt);
        SetMono(Kind == WaveformKind.Stereo && peak > 0.01f && side < 0.002f);
        _luminance = peak > 0.005f ? Luma(_lineBrush.Color) * 0.01 : 0;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 2 || bounds.Height < 2) return;

        using var _ = context.PushOpacity(Math.Clamp(_gain, 0, 1));
        switch (_mode)
        {
            case SceneAudioMode.Spectrum:
                if (Shape == SpectrumShape.Curve) DrawCurve(context, bounds);
                else DrawBars(context, bounds);
                break;
            case SceneAudioMode.Ring:
                DrawRing(context, bounds);
                break;
            case SceneAudioMode.Waveform:
                if (Kind == WaveformKind.Stereo) DrawStereo(context, bounds);
                else DrawLine(context, bounds);
                break;
            case SceneAudioMode.Horizon:
                DrawHorizon(context, bounds);
                break;
        }
    }

    /// <summary>
    /// Horyzont: obwiednia leży w dwóch bitmapach (przygaszonej i jaśniejszej) rysowanych raz;
    /// w klatce jaśniejsza jest odsłaniana do głowicy, a sama głowica dostaje poświatę od poziomu.
    /// </summary>
    private void DrawHorizon(DrawingContext context, Rect bounds)
    {
        EnsureHorizon(bounds.Size);
        var head = bounds.X + bounds.Width * _progress;
        if (_horizonDim is not null && _horizonLit is not null)
        {
            var source = new Rect(_horizonSize);
            context.DrawImage(_horizonDim, source, bounds);
            if (_progress > 0)
            {
                using (context.PushClip(new Rect(bounds.X, bounds.Y, bounds.Width * _progress, bounds.Height)))
                    context.DrawImage(_horizonLit, source, bounds);
            }
        }

        var top = bounds.Y + bounds.Height * 0.05;
        var bottom = bounds.Bottom - bounds.Height * 0.05;
        using (context.PushOpacity(0.15 + 0.35 * _headLevel))
            context.DrawRectangle(_glowBrush, null, new Rect(head - 9, top, 18, bottom - top), 9, 9);
        context.DrawLine(_headPen, new Point(head, top), new Point(head, bottom));
    }

    private void EnsureHorizon(Size size)
    {
        var pixels = new Size(Math.Ceiling(size.Width), Math.Ceiling(size.Height));
        if (_horizonDim is not null && _horizonLit is not null && _horizonSize == pixels) return;

        DropHorizon();
        _horizonSize = pixels;
        _horizonRebuilds++;
        try
        {
            _horizonDim = PaintHorizon(pixels, _dimPeakBrush, _dimBodyBrush);
            _horizonLit = PaintHorizon(pixels, _litPeakBrush, _litBodyBrush);
        }
        catch (Exception)
        {
            DropHorizon();
        }
    }

    private RenderTargetBitmap PaintHorizon(Size size, IBrush peakBrush, IBrush bodyBrush)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();
        const double column = 4;
        const double thickness = 2.5;
        var columns = Math.Max(1, (int)(size.Width / column));
        var middle = size.Height / 2;
        var span = size.Height * 0.9;
        for (var i = 0; i < columns; i++)
        {
            float peak = 0;
            float rms = 0;
            if (_envelope is { } envelope)
            {
                var from = (int)((long)i * envelope.Count / columns);
                var to = Math.Max(from + 1, (int)((long)(i + 1) * envelope.Count / columns));
                for (var k = from; k < Math.Min(to, envelope.Count); k++)
                {
                    peak = Math.Max(peak, envelope.PeakAt(k));
                    rms = Math.Max(rms, envelope.RmsAt(k));
                }
            }

            var x = i * column + (column - thickness) / 2;
            var peakHeight = Math.Max(2, peak / _envelopeNorm * span);
            var bodyHeight = Math.Max(2, Math.Min(peakHeight, rms / _envelopeNorm * span));
            context.DrawRectangle(peakBrush, null, new Rect(x, middle - peakHeight / 2, thickness, peakHeight), 1.2, 1.2);
            context.DrawRectangle(bodyBrush, null, new Rect(x, middle - bodyHeight / 2, thickness, bodyHeight), 1.2, 1.2);
        }

        return bitmap;
    }

    private void DrawBars(DrawingContext context, Rect bounds)
    {
        var count = SpectrumBars.Count;
        var slot = bounds.Width / count;
        var width = slot * 0.7;
        var bars = _spectrum.Bars;
        var peaks = _spectrum.Peaks;
        for (var i = 0; i < count; i++)
        {
            var x = bounds.X + i * slot + (slot - width) / 2;
            var height = Math.Max(3, bars[i] * bounds.Height);
            context.DrawRectangle(_barBrush, null, new Rect(x, bounds.Bottom - height, width, height), 2, 2);

            var peak = peaks[i] * bounds.Height;
            if (peak > height + 4)
                context.DrawRectangle(_peakBrush, null, new Rect(x, bounds.Bottom - peak - 3, width, 3), 1.5, 1.5);
        }
    }

    private void DrawCurve(DrawingContext context, Rect bounds)
    {
        var count = SpectrumBars.Count;
        var slot = bounds.Width / count;
        var bars = _spectrum.Bars;
        for (var pass = 0; pass < 2; pass++)
        {
            var pen = pass == 0 ? _glowPen : _linePen;
            var previous = new Point(bounds.X + slot / 2, bounds.Bottom - bars[0] * bounds.Height);
            for (var i = 1; i < count; i++)
            {
                var next = new Point(bounds.X + i * slot + slot / 2, bounds.Bottom - bars[i] * bounds.Height);
                context.DrawLine(pen, previous, next);
                previous = next;
            }
        }
    }

    private void DrawRing(DrawingContext context, Rect bounds)
    {
        var centre = bounds.Center;
        var inner = Math.Min(bounds.Width, bounds.Height) / 2 / 1.06;
        var maxLength = inner * 0.06;
        var bars = _spectrum.Bars;
        for (var j = 0; j < RingTicks; j++)
        {
            var fraction = (double)j / RingTicks;
            var index = (int)Math.Round(Math.Abs(1 - 2 * fraction) * (SpectrumBars.Count - 1));
            var length = Math.Max(1.5, bars[index] * maxLength);
            var angle = fraction * 2 * Math.PI + Math.PI / 2;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            context.DrawLine(_tickPen,
                new Point(centre.X + cos * inner, centre.Y + sin * inner),
                new Point(centre.X + cos * (inner + length), centre.Y + sin * (inner + length)));
        }
    }

    private void DrawLine(DrawingContext context, Rect bounds)
    {
        var length = WaveLength;
        var start = ScopeTrigger.Find(_mid.AsSpan(0, length + SearchSamples), SearchSamples);
        var step = (double)(length - 1) / (WavePoints - 1);
        var scale = bounds.Height * 0.45 * _trigger.Gain;
        var middle = bounds.Center.Y;
        for (var k = 0; k < WavePoints; k++)
        {
            var value = Math.Clamp(ScopeTrigger.Sample(_mid, start + k * step), -1.2f, 1.2f);
            _points[k] = new Point(bounds.X + bounds.Width * k / (WavePoints - 1), middle - value * scale);
        }

        for (var pass = 0; pass < 2; pass++)
        {
            var pen = pass == 0 ? _glowPen : _linePen;
            for (var k = 1; k < WavePoints; k++) context.DrawLine(pen, _points[k - 1], _points[k]);
        }
    }

    private void DrawStereo(DrawingContext context, Rect bounds)
    {
        var length = WaveLength + SearchSamples;
        var from = length - StereoPoints;
        const double root = 0.70710678;
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.45 * Math.Min(_trigger.Gain, 4);
        var centre = bounds.Center;
        for (var i = from; i < length; i++)
        {
            var l = float.IsFinite(_left[i]) ? _left[i] : 0;
            var r = float.IsFinite(_right[i]) ? _right[i] : 0;
            _points[i - from] = new Point(centre.X + (r - l) * root * radius, centre.Y - (l + r) * root * radius);
        }

        // Poświata osobnym przebiegiem, żeby końcówki segmentów nie składały się w kropki.
        for (var pass = 0; pass < 2; pass++)
        {
            var pen = pass == 0 ? _glowPen : _linePen;
            for (var k = 1; k < StereoPoints; k++) context.DrawLine(pen, _points[k - 1], _points[k]);
        }
    }
}
