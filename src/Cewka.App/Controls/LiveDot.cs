using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// Czerwona kropka przed napisem „Na żywo”: koło wielkości kropki pisma, z odstępem jak między
/// słowami, wyśrodkowane w wierszu napisu. Przy graniu krycie powoli pulsuje (cykl 2 s, sinusoidalnie
/// od 1 do 0,35 i z powrotem); rozmiar się nie zmienia, więc nic obok się nie przesuwa.
/// <para>
/// W pauzie, przy ograniczonych efektach i po zamrożeniu do zrzutu kropka świeci stale. Pulsowanie
/// idzie pętlą klatek, która czeka, gdy kropki nie widać (ukryty napis, zamknięta scena, okno
/// zminimalizowane albo przykryte sceną). Kropka jest ozdobą: czytnik ekranu jej nie widzi.
/// </para>
/// </summary>
public sealed class LiveDot : Control
{
    /// <summary>Średnica jako część rozmiaru pisma: 0,6 wysokości x kroju Noto Sans (0,536 em).</summary>
    public const double DiameterPerEm = 0.6 * 0.536;

    /// <summary>Odstęp od napisu jako część rozmiaru pisma: szerokość spacji Noto Sans (0,26 em).</summary>
    public const double GapPerEm = 0.26;

    /// <summary>Czas jednego cyklu pulsowania; pół herca, daleko od błysków (WCAG 2.3.1).</summary>
    public const double PeriodSeconds = 2.0;

    /// <summary>Najmniejsze krycie w połowie cyklu.</summary>
    public const double LowestOpacity = 0.35;

    /// <summary>Najdłuższy krok jednej klatki: przestój (przeciąganie okna, uśpienie) nie przeskakuje fazy.</summary>
    private const double LongestStep = 0.25;

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<LiveDot, double>(nameof(FontSize), 12);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LiveDot, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LiveDot, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<bool> IsReducedProperty =
        AvaloniaProperty.Register<LiveDot, bool>(nameof(IsReduced));

    private readonly RenderLoop _loop;
    private TimeSpan _lastFrame;
    private double _phase;
    private bool _frozen;

    static LiveDot()
    {
        AffectsMeasure<LiveDot>(FontSizeProperty);
        AffectsRender<LiveDot>(FillProperty);
    }

    public LiveDot()
    {
        // Po odsłonięciu pierwsza klatka nie liczy czasu, w którym kropki nie było widać.
        _loop = new RenderLoop(this, OnFrame, () => _lastFrame = TimeSpan.Zero);
        IsHitTestVisible = false;
        Focusable = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    /// <summary>Rozmiar pisma napisu, przed którym stoi kropka; od niego zależą średnica i odstęp.</summary>
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Stacja gra; w pauzie kropka świeci stale.</summary>
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>Tryb ograniczonego ruchu: kropka świeci stale.</summary>
    public bool IsReduced { get => GetValue(IsReducedProperty); set => SetValue(IsReducedProperty, value); }

    public double Diameter => FontSize * DiameterPerEm;

    public double Gap => FontSize * GapPerEm;

    /// <summary>Pętla zamawia klatki, czyli kropka pulsuje i ktoś ją widzi.</summary>
    internal bool IsPulsing => _loop.IsRunning && !_loop.IsParked;

    private bool PulseWanted => !_frozen && IsActive && !IsReduced && IsVisible && VisualRoot is not null;

    /// <summary>Krycie w danej chwili cyklu: 1 na początku i końcu, <see cref="LowestOpacity"/> w połowie.</summary>
    internal static double OpacityAt(double seconds) =>
        LowestOpacity + (1 - LowestOpacity) * (0.5 + 0.5 * Math.Cos(2 * Math.PI * seconds / PeriodSeconds));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty || change.Property == IsReducedProperty || change.Property == IsVisibleProperty)
            Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _loop.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Kropka stoi w pełnym kryciu, bez ruchu, dla narzędzia zrzutów.</summary>
    public void FreezeForCapture()
    {
        _frozen = true;
        Update();
    }

    private void Update()
    {
        if (!PulseWanted)
        {
            // Następne pulsowanie zaczyna się od pełnego krycia, bez skoku.
            _loop.Stop();
            _phase = 0;
            Opacity = 1;
            return;
        }

        if (_loop.IsRunning) return;

        _lastFrame = TimeSpan.Zero;
        _loop.Start();
    }

    private void OnFrame(TimeSpan timestamp)
    {
        var delta = _lastFrame == TimeSpan.Zero ? 0 : (timestamp - _lastFrame).TotalSeconds;
        _lastFrame = timestamp;
        Advance(delta);
    }

    /// <summary>Jedna klatka pulsowania; testy wołają ją wprost, z dowolnym krokiem czasu.</summary>
    internal void Advance(double seconds)
    {
        if (!PulseWanted)
        {
            Opacity = 1;
            return;
        }

        _phase = (_phase + Math.Clamp(seconds, 0, LongestStep)) % PeriodSeconds;
        Opacity = OpacityAt(_phase);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Diameter + Gap, Diameter);

    public override void Render(DrawingContext context)
    {
        if (Fill is not { } fill) return;

        var radius = Diameter / 2;
        context.DrawEllipse(fill, null, new Point(radius, Bounds.Height / 2), radius, radius);
    }
}
