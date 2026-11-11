using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cewka.App.Services;

namespace Cewka.App.Controls;

/// <summary>
/// Ruchome tło całego interfejsu: cztery barwy z okładki jako miękkie plamy, każda dryfuje po
/// własnym torze i lekko jaśnieje z muzyką. Cztery gradienty na klatkę są tańsze niż rozmyta
/// bitmapa okładki.
/// </summary>
public sealed class LiveBackdrop : Control
{
    /// <summary>Połowa częstotliwości ekranu; przy tak wolnym ruchu różnicy nie widać.</summary>
    private const double UpdateInterval = 1.0 / 30;

    /// <summary>Czas przenikania do barw nowego utworu, w sekundach.</summary>
    private const double PaletteFadeSeconds = 1.4;

    /// <summary>
    /// Prędkości, fazy i promienie torów plam. Prędkości są niewspółmierne, żeby układ się nie
    /// powtarzał. Promień to ułamek krótszego boku okna, wyraźnie poniżej jedności, bo plama
    /// wielkości okna zamienia się w jednolity nalot.
    /// </summary>
    private static readonly (double SpeedX, double SpeedY, double PhaseX, double PhaseY, double Radius)[] Paths =
    [
        (0.050, 0.039, 0.0, 1.7, 0.62),
        (0.031, 0.055, 2.3, 0.4, 0.50),
        (0.042, 0.026, 4.1, 3.2, 0.56),
        (0.023, 0.046, 5.6, 2.1, 0.44),
    ];

    /// <summary>
    /// Okres i faza rozjaśniania każdej plamy, w sekundach i radianach. Okresy są liczbami
    /// pierwszymi, żeby tło nie pulsowało jako całość. Rytm idzie z zegara dryfu, więc w pauzie
    /// zamiera razem z nim.
    /// </summary>
    private static readonly (double Period, double Phase)[] Pulses =
    [
        (13.0, 0.0),
        (17.0, 1.9),
        (11.0, 3.4),
        (19.0, 5.1),
    ];

    /// <summary>Jak daleko od środka odchodzi plama, jako ułamek okna.</summary>
    private const double DriftX = 0.40;
    private const double DriftY = 0.36;

    public static readonly StyledProperty<IReadOnlyList<Color>?> PaletteProperty =
        AvaloniaProperty.Register<LiveBackdrop, IReadOnlyList<Color>?>(nameof(Palette));

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LiveBackdrop, double>(nameof(Level));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LiveBackdrop, bool>(nameof(IsActive), true);

    /// <summary>Tryb ograniczony: dwie plamy, nieruchome, bez oddychania.</summary>
    public static readonly StyledProperty<bool> IsReducedProperty =
        AvaloniaProperty.Register<LiveBackdrop, bool>(nameof(IsReduced));

    /// <summary>Mnożnik tempa dryfu i rytmu plam: 1 w oknie, wolniej w scenie.</summary>
    public static readonly StyledProperty<double> TempoProperty =
        AvaloniaProperty.Register<LiveBackdrop, double>(nameof(Tempo), 1.0);

    /// <summary>
    /// Czas, w jakim ruch wygasa po pauzie, w sekundach. Zero to łagodne wygasanie okna (kilka
    /// sekund); scena wygasza ruch w sekundę, żeby pauza szybko przestała kosztować klatki.
    /// </summary>
    public static readonly StyledProperty<double> StopSecondsProperty =
        AvaloniaProperty.Register<LiveBackdrop, double>(nameof(StopSeconds));

    public static readonly StyledProperty<IBrush?> BaseBrushProperty =
        AvaloniaProperty.Register<LiveBackdrop, IBrush?>(nameof(BaseBrush));

    /// <summary>
    /// Dolny i górny próg siły plam z ustawienia intensywności barw; każda plama waha się między
    /// nimi własnym rytmem.
    /// </summary>
    public static readonly StyledProperty<double> MinimumStrengthProperty =
        AvaloniaProperty.Register<LiveBackdrop, double>(nameof(MinimumStrength), 0.88);

    public static readonly StyledProperty<double> MaximumStrengthProperty =
        AvaloniaProperty.Register<LiveBackdrop, double>(nameof(MaximumStrength), 1.12);

    private readonly RenderLoop _loop;
    private readonly RadialGradientBrush[] _brushes = new RadialGradientBrush[CoverPaletteSize];
    private readonly Color[] _shown = new Color[CoverPaletteSize];
    private readonly Color[] _from = new Color[CoverPaletteSize];
    private readonly Color[] _to = new Color[CoverPaletteSize];

    private const int CoverPaletteSize = 4;

    /// <summary>Tempo, z jakim dryf zwalnia do zatrzymania po pauzie, na sekundę.</summary>
    private const double SpeedEasing = 1.6;

    /// <summary>Poniżej tego ruch jest niewidoczny, więc pętla może stanąć.</summary>
    private const double Settled = 0.002;

    private double _time;
    private double _sinceUpdate;
    private double _fade = 1;
    private double _breathing;
    private double _speed;
    private TimeSpan _lastFrame;
    private bool _hasPalette;
    private bool _frozen;
    private readonly FrameBudget _budget = new();
    private RenderTargetBitmap? _buffer;

    static LiveBackdrop()
    {
        AffectsRender<LiveBackdrop>(
            BaseBrushProperty, MinimumStrengthProperty, MaximumStrengthProperty);
    }

    public LiveBackdrop()
    {
        IsHitTestVisible = false;
        _loop = new RenderLoop(this, OnFrame, OnShown);

        // Pędzle wielokrotnego użytku: klatka zmienia ich środek, promień i barwy zamiast
        // tworzyć nowe.
        for (var i = 0; i < CoverPaletteSize; i++)
        {
            _brushes[i] = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Transparent, 0.30),
                    new GradientStop(Colors.Transparent, 0.62),
                    new GradientStop(Colors.Transparent, 1),
                },
            };
        }
    }

    /// <summary>Barwy z bieżącej okładki.</summary>
    public IReadOnlyList<Color>? Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    /// <summary>Obwiednia sygnału, 0–1; lekko rozjaśnia plamy w rytm muzyki.</summary>
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public bool IsReduced { get => GetValue(IsReducedProperty); set => SetValue(IsReducedProperty, value); }

    public double Tempo { get => GetValue(TempoProperty); set => SetValue(TempoProperty, value); }

    /// <summary>Mnożnik tempa z ustawień, niezależny od <see cref="Tempo"/> ustawionego w widoku.</summary>
    public double MotionScale { get; set; } = 1;

    public double StopSeconds { get => GetValue(StopSecondsProperty); set => SetValue(StopSecondsProperty, value); }

    /// <summary>Barwa pod plamami.</summary>
    public IBrush? BaseBrush { get => GetValue(BaseBrushProperty); set => SetValue(BaseBrushProperty, value); }

    /// <summary>Najsłabszy moment cyklu jasności plamy; 1 to siła nominalna.</summary>
    public double MinimumStrength
    {
        get => GetValue(MinimumStrengthProperty);
        set => SetValue(MinimumStrengthProperty, value);
    }

    /// <summary>Najjaśniejszy moment cyklu.</summary>
    public double MaximumStrength
    {
        get => GetValue(MaximumStrengthProperty);
        set => SetValue(MaximumStrengthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PaletteProperty) AdoptPalette(Palette);
        else if (change.Property == IsActiveProperty || change.Property == IsReducedProperty) Wake();
    }

    /// <summary>Pętla działa tylko wtedy, gdy coś na tle się porusza.</summary>
    public bool IsLooping => _loop.IsRunning;

    /// <summary>Położenie na osi czasu dryfu, w sekundach; rośnie wolniej przy mniejszym tempie.</summary>
    internal double DriftTime => _time;

    /// <summary>Pętla zamawia klatki: działa i nie czeka na odsłonięcie tła.</summary>
    public bool IsDrawing => _loop.IsRunning && !_loop.IsParked;

    /// <summary>Barwy plam w tej chwili, z przenikaniem.</summary>
    internal IReadOnlyList<Color> ShownColours => _shown;

    private void Wake()
    {
        if (_frozen || _loop.IsRunning || VisualRoot is null) return;

        _lastFrame = TimeSpan.Zero;
        _loop.Start();
    }

    private void AdoptPalette(IReadOnlyList<Color>? palette)
    {
        if (palette is null || palette.Count == 0) return;

        // Pierwsza paleta pojawia się od razu, każda następna przez przenikanie. Ukryte tło też
        // przyjmuje ją od razu: przenikanie, którego nikt nie widzi, pokazałoby po odsłonięciu
        // barwy dawnego utworu, a w pauzie trzymało pętlę dłużej. Ukryte jest też tło jeszcze
        // poza oknem, jak w scenie przed pierwszym otwarciem: Avalonia ma je za widoczne,
        // a pętla bez okna nie rusza, więc nie czeka na odsłonięcie.
        var instant = !_hasPalette || VisualRoot is null || !_loop.IsOwnerShown || _loop.IsParked;

        for (var i = 0; i < CoverPaletteSize; i++)
        {
            var colour = palette[Math.Min(i, palette.Count - 1)];

            _from[i] = instant ? colour : _shown[i];
            _to[i] = colour;
            if (instant) _shown[i] = colour;
        }

        _fade = instant ? 1 : 0;
        _hasPalette = true;
        InvalidateVisual();
        Wake();
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
        _buffer?.Dispose();
        _buffer = null;
    }

    /// <summary>Przenikanie przerwane ukryciem kończy się od razu, a czas klatek liczy się od nowa.</summary>
    private void OnShown()
    {
        _lastFrame = TimeSpan.Zero;
        if (_fade >= 1) return;

        _fade = 1;
        Array.Copy(_to, _shown, CoverPaletteSize);
        InvalidateVisual();
    }

    private void OnFrame(TimeSpan timestamp)
    {
        var delta = _lastFrame == TimeSpan.Zero ? 0 : (timestamp - _lastFrame).TotalSeconds;
        _lastFrame = timestamp;
        if (delta > 0 && _budget.Observe(delta)) InvalidateVisual();

        Advance(delta);
    }

    /// <summary>Jedna klatka ruchu; testy wołają ją wprost, z dowolnym krokiem czasu.</summary>
    internal void Advance(double seconds)
    {
        var delta = Math.Clamp(seconds, 0, 0.25);

        // W pauzie dryf zwalnia do zatrzymania, a w trybie ograniczonym tło stoi od razu.
        var targetSpeed = IsActive && !IsReduced ? 1.0 : 0.0;
        var stop = StopSeconds;

        if (IsReduced) _speed = 0;
        else if (targetSpeed == 0 && stop > 0) _speed = Math.Max(0, _speed - delta / stop);
        else _speed += (targetSpeed - _speed) * Math.Min(1, delta * SpeedEasing);

        _time += delta * _speed * Tempo * MotionScale;

        if (_fade < 1)
        {
            _fade = Math.Min(1, _fade + delta / PaletteFadeSeconds);
            for (var i = 0; i < CoverPaletteSize; i++) _shown[i] = Lerp(_from[i], _to[i], Easing(_fade));
        }

        var target = IsReduced || !IsActive ? 0 : Math.Clamp(Level, 0, 1);
        if (target == 0 && stop > 0) _breathing = Math.Max(0, _breathing - delta / stop);
        else _breathing += (target - _breathing) * Math.Min(1, delta * 4);

        if (targetSpeed == 0 && _fade >= 1 && _speed < Settled && _breathing < Settled)
        {
            _speed = 0;
            _breathing = 0;
            _loop.Stop();
            InvalidateVisual();
            return;
        }

        _sinceUpdate += delta;
        if (_sinceUpdate < UpdateInterval) return;

        _sinceUpdate = 0;
        InvalidateVisual();
    }

    /// <summary>
    /// Zatrzymuje dryf w ustalonym punkcie toru, dla narzędzia zrzutów; bez tego każdy zrzut
    /// miałby inny układ plam. Trwające przenikanie jest kończone, żeby zrzut nie mieszał barw
    /// dwóch utworów.
    /// </summary>
    /// <param name="atSeconds">
    /// Moment na osi czasu tła. Zero to układ z samych stałych fazowych; inne wartości pokazują
    /// kilka chwil jednego cyklu.
    /// </param>
    public void FreezeForCapture(double atSeconds = 0)
    {
        _frozen = true;
        _loop.Stop();

        _time = atSeconds;
        _breathing = 0;
        _fade = 1;
        if (_hasPalette) Array.Copy(_to, _shown, CoverPaletteSize);

        InvalidateVisual();
    }

    private static double Easing(double t) => t * t * (3 - 2 * t);

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var area = new Rect(0, 0, width, height);

        if (!_hasPalette)
        {
            if (BaseBrush is { } plain) context.DrawRectangle(plain, null, area);
            return;
        }

        // Przy przeciążeniu plamy trafiają do bufora o niższej rozdzielczości, rozciąganego na okno:
        // gradienty są miękkie, więc utrata ostrości jest niewidoczna, a koszt rysowania spada.
        if (_budget.Level > 0 && BaseBrush is { } baseFill && IsOpaque(baseFill) && TryRenderBuffered(context, width, height, area, baseFill)) return;

        if (BaseBrush is { } baseBrush) context.DrawRectangle(baseBrush, null, area);
        PaintBlobs(context, width, height, area);
    }

    /// <summary>Rozmiar bufora w pikselach dla poziomu obniżenia: 960 x 540 (nie większy od okna) razy mnożnik poziomu.</summary>
    internal static PixelSize BufferSize(double width, double height, int level)
    {
        var fit = Math.Min(1, Math.Min(BufferWidth / width, BufferHeight / height)) * FrameBudget.ScaleFor(level);
        return new PixelSize(Math.Max(1, (int)Math.Round(width * fit)), Math.Max(1, (int)Math.Round(height * fit)));
    }

    private const double BufferWidth = 960;
    private const double BufferHeight = 540;

    /// <summary>Poziom obniżenia rozdzielczości bufora; 0 to rysowanie wprost.</summary>
    internal FrameBudget Budget => _budget;

    /// <summary>Rozmiar bufora wewnętrznego; pusty, gdy tło rysuje wprost.</summary>
    internal PixelSize? BufferPixels => _buffer?.PixelSize;

    private static bool IsOpaque(IBrush brush) =>
        brush.Opacity >= 1 && (brush is not ISolidColorBrush solid || solid.Color.A == 255);

    private bool TryRenderBuffered(DrawingContext context, double width, double height, Rect area, IBrush baseBrush)
    {
        try
        {
            var size = BufferSize(width, height, _budget.Level);
            if (_buffer is null || _buffer.PixelSize != size)
            {
                _buffer?.Dispose();
                _buffer = new RenderTargetBitmap(size, new Vector(96, 96));
            }

            var scale = (double)size.Width / width;
            using (var target = _buffer.CreateDrawingContext())
            using (target.PushTransform(Matrix.CreateScale(scale, scale)))
            {
                target.DrawRectangle(baseBrush, null, area);
                PaintBlobs(target, width, height, area);
            }

            context.DrawImage(_buffer, new Rect(0, 0, size.Width, size.Height), area);
            return true;
        }
        catch (Exception)
        {
            _buffer = null;
            return false;
        }
    }

    private void PaintBlobs(DrawingContext context, double width, double height, Rect area)
    {

        // Pułapka: promień liczony od krótszego boku. Od dłuższego plama zakrywałaby na szerokim
        // oknie całą wysokość.
        var span = Math.Min(width, height);
        var blobs = IsReduced ? 2 : CoverPaletteSize;

        for (var i = 0; i < blobs; i++)
        {
            var path = Paths[i];

            var centreX = width * (0.5 + DriftX * Math.Sin(_time * path.SpeedX * 2 * Math.PI + path.PhaseX));
            var centreY = height * (0.5 + DriftY * Math.Cos(_time * path.SpeedY * 2 * Math.PI + path.PhaseY));
            var radius = span * path.Radius * (1 + 0.06 * _breathing);

            // Każda plama nieco słabsza od poprzedniej, żeby nie zlewały się w jedną płaszczyznę,
            // i z własnym cyklem jaśnienia między progami intensywności. Intensywność zmienia siłę
            // tła, a nie odcień plamy.
            var pulse = Pulses[i];
            var cycle = 0.5 + 0.5 * Math.Sin(_time * 2 * Math.PI / pulse.Period + pulse.Phase);
            var level = MinimumStrength + (MaximumStrength - MinimumStrength) * cycle;

            var strength = (0.86 - i * 0.11) * (1 + 0.18 * _breathing) * level;
            var colour = _shown[i];

            // Barwa trzyma się przy środku i opada szybciej przy brzegu; równomierne przejście
            // dałoby mgłę bez kształtu, na której nie widać ruchu.
            var brush = _brushes[i];
            var centre = new RelativePoint(centreX, centreY, RelativeUnit.Absolute);
            var reach = new RelativeScalar(radius, RelativeUnit.Absolute);
            brush.Center = centre;
            brush.GradientOrigin = centre;
            brush.RadiusX = reach;
            brush.RadiusY = reach;

            var stops = brush.GradientStops;
            stops[0].Color = WithAlpha(colour, strength);
            stops[1].Color = WithAlpha(colour, strength * 0.74);
            stops[2].Color = WithAlpha(colour, strength * 0.30);
            stops[3].Color = WithAlpha(colour, 0);

            context.DrawRectangle(brush, null, area);
        }
    }

    private static Color WithAlpha(Color colour, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), colour.R, colour.G, colour.B);
}
