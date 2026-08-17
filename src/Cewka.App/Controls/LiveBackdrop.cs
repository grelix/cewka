using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// The moving colour field behind the whole interface.
///
/// <para><b>Skąd taki kształt.</b> Pierwotnie tłem była okładka powiększona do 1900 pikseli,
/// rozmyta i powoli obracana. Wyglądało to dobrze, ale nie zmieniało się w trakcie utworu —
/// obracająca się plama rozmyta na 100 pikseli jest praktycznie nieruchoma — a rozmycie
/// obrazu tej wielkości trzeba było wypalać do bitmapy przy każdej zmianie utworu.</para>
///
/// <para>Tutaj to samo wrażenie powstaje z czterech barw wyciągniętych z okładki. Każda
/// z nich jest wielką miękką plamą dryfującą po własnym torze, a jej jasność delikatnie
/// oddycha razem z muzyką. Kosztuje to cztery gradienty na klatkę zamiast skalowania
/// wielkiej bitmapy, tło faktycznie żyje, a barwy nadal pochodzą z okładki, więc każdy
/// album ma własny nastrój.</para>
/// </summary>
public sealed class LiveBackdrop : Control
{
    /// <summary>Half the display rate: the motion is slow, so nobody can tell.</summary>
    private const double UpdateInterval = 1.0 / 30;

    /// <summary>Seconds taken to cross-fade to a new track's colours.</summary>
    private const double PaletteFadeSeconds = 1.4;

    /// <summary>
    /// Per-blob path speeds, phases and radii. The speeds are deliberately incommensurable, so
    /// the arrangement never visibly repeats.
    ///
    /// <para>Promienie podane są jako ułamek dłuższego boku okna. Trzymane są wyraźnie poniżej
    /// jedności: plama o promieniu porównywalnym z oknem barwi je w całości i przestaje być
    /// plamą — zostaje jednolity nalot, który nigdzie nie płynie, bo nie ma go z czym
    /// porównać. Mniejsze plamy widać jako osobne rozlewiska wędrujące po tle.</para>
    /// </summary>
    private static readonly (double SpeedX, double SpeedY, double PhaseX, double PhaseY, double Radius)[] Paths =
    [
        (0.050, 0.039, 0.0, 1.7, 0.62),
        (0.031, 0.055, 2.3, 0.4, 0.50),
        (0.042, 0.026, 4.1, 3.2, 0.56),
        (0.023, 0.046, 5.6, 2.1, 0.44),
    ];

    /// <summary>
    /// Okres i faza rozjaśniania każdej plamy, w sekundach i radianach.
    ///
    /// <para>Okresy są liczbami pierwszymi i wzajemnie niewspółmierne, tak samo jak prędkości
    /// w <see cref="Paths"/>. Gdyby dwie plamy jaśniały w tym samym rytmie, tło pulsowałoby jako
    /// całość — a to wygląda jak migotanie, nie jak cztery światła o własnym życiu. Przy takich
    /// okresach ten sam układ jasności nie wraca w rozsądnym czasie.</para>
    ///
    /// <para>Rytm liczony jest z tego samego zegara co dryf, więc przy pauzie zwalnia razem z nim
    /// do jednej czwartej, a w trybie ograniczonych efektów jeszcze dwa i pół raza bardziej.</para>
    /// </summary>
    private static readonly (double Period, double Phase)[] Pulses =
    [
        (13.0, 0.0),
        (17.0, 1.9),
        (11.0, 3.4),
        (19.0, 5.1),
    ];

    /// <summary>How far from the centre a blob travels, as a fraction of the window.</summary>
    private const double DriftX = 0.40;
    private const double DriftY = 0.36;

    public static readonly StyledProperty<IReadOnlyList<Color>?> PaletteProperty =
        AvaloniaProperty.Register<LiveBackdrop, IReadOnlyList<Color>?>(nameof(Palette));

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LiveBackdrop, double>(nameof(Level));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LiveBackdrop, bool>(nameof(IsActive), true);

    /// <summary>Reduced mode: fewer blobs, no breathing, much slower drift.</summary>
    public static readonly StyledProperty<bool> IsReducedProperty =
        AvaloniaProperty.Register<LiveBackdrop, bool>(nameof(IsReduced));

    public static readonly StyledProperty<IBrush?> BaseBrushProperty =
        AvaloniaProperty.Register<LiveBackdrop, IBrush?>(nameof(BaseBrush));

    /// <summary>
    /// Dolny i górny próg siły plam, z ustawienia intensywności barw. Każda plama waha się
    /// między nimi własnym rytmem; jedność to siła, z jaką tło było projektowane.
    /// </summary>
    public static readonly StyledProperty<double> MinimumStrengthProperty =
        AvaloniaProperty.Register<LiveBackdrop, double>(nameof(MinimumStrength), 0.88);

    public static readonly StyledProperty<double> MaximumStrengthProperty =
        AvaloniaProperty.Register<LiveBackdrop, double>(nameof(MaximumStrength), 1.12);

    private readonly RenderLoop _loop;
    private readonly Color[] _shown = new Color[CoverPaletteSize];
    private readonly Color[] _from = new Color[CoverPaletteSize];
    private readonly Color[] _to = new Color[CoverPaletteSize];

    private const int CoverPaletteSize = 4;

    private double _time;
    private double _sinceUpdate;
    private double _fade = 1;
    private double _breathing;
    private TimeSpan _lastFrame;
    private bool _hasPalette;

    static LiveBackdrop()
    {
        AffectsRender<LiveBackdrop>(
            BaseBrushProperty, MinimumStrengthProperty, MaximumStrengthProperty);
    }

    public LiveBackdrop()
    {
        IsHitTestVisible = false;
        _loop = new RenderLoop(this, OnFrame);
    }

    /// <summary>Colours taken from the current cover.</summary>
    public IReadOnlyList<Color>? Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    /// <summary>Signal envelope, 0 to 1. Drives a gentle brightening on the beat.</summary>
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public bool IsReduced { get => GetValue(IsReducedProperty); set => SetValue(IsReducedProperty, value); }

    /// <summary>Colour painted underneath the blobs.</summary>
    public IBrush? BaseBrush { get => GetValue(BaseBrushProperty); set => SetValue(BaseBrushProperty, value); }

    /// <summary>Najsłabszy moment cyklu jasności plamy; 1 to siła, z jaką tło było projektowane.</summary>
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
    }

    private void AdoptPalette(IReadOnlyList<Color>? palette)
    {
        if (palette is null || palette.Count == 0) return;

        for (var i = 0; i < CoverPaletteSize; i++)
        {
            var colour = palette[Math.Min(i, palette.Count - 1)];

            // Pierwsza paleta pojawia sie od razu; kazda kolejna jest przenikana.
            _from[i] = _hasPalette ? _shown[i] : colour;
            _to[i] = colour;
            if (!_hasPalette) _shown[i] = colour;
        }

        _fade = _hasPalette ? 0 : 1;
        _hasPalette = true;
        InvalidateVisual();
    }

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

    private void OnFrame(TimeSpan timestamp)
    {
        var delta = _lastFrame == TimeSpan.Zero ? 0 : (timestamp - _lastFrame).TotalSeconds;
        _lastFrame = timestamp;
        delta = Math.Clamp(delta, 0, 0.25);

        // Ruch nie zatrzymuje sie przy pauzie, tylko zwalnia - okno ma zyc, ale spokojniej.
        var speed = IsActive ? 1.0 : 0.25;
        if (IsReduced) speed *= 0.4;
        _time += delta * speed;

        if (_fade < 1)
        {
            _fade = Math.Min(1, _fade + delta / PaletteFadeSeconds);
            for (var i = 0; i < CoverPaletteSize; i++) _shown[i] = Lerp(_from[i], _to[i], Easing(_fade));
        }

        var target = IsReduced || !IsActive ? 0 : Math.Clamp(Level, 0, 1);
        _breathing += (target - _breathing) * Math.Min(1, delta * 4);

        _sinceUpdate += delta;
        if (_sinceUpdate < UpdateInterval) return;

        _sinceUpdate = 0;
        InvalidateVisual();
    }

    /// <summary>
    /// Stops the drift and puts it at a fixed point on its path, for the snapshot tool.
    /// <para>
    /// The blobs travel on wall-clock time, so two runs of the same code photographed the
    /// background in two different arrangements. Zero is the arrangement the phase constants in
    /// <see cref="Paths"/> were chosen to give, which makes it the one defensible choice.
    /// </para>
    /// <para>
    /// Any pending cross-fade is completed rather than interrupted: freezing mid-fade would
    /// photograph a blend of the previous track's colours and this one's.
    /// </para>
    /// </summary>
    /// <param name="atSeconds">
    /// Moment na osi czasu tła. Zero to układ, jaki dają same stałe fazowe. Inne wartości służą
    /// do sfotografowania kilku chwil jednego cyklu — bez tego nie da się na nieruchomym obrazie
    /// pokazać, że plamy jaśnieją niezależnie od siebie.
    /// </param>
    public void FreezeForCapture(double atSeconds = 0)
    {
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

        if (BaseBrush is { } baseBrush) context.DrawRectangle(baseBrush, null, area);
        if (!_hasPalette) return;

        // Promien odnosi sie do krotszego boku okna, nie do dluzszego. Przy skalowaniu do
        // dluzszego plama o promieniu 0,46 zakrywala na szerokim oknie cala jego wysokosc
        // i barwa znow rozlewala sie na wszystko, zamiast plywac po tle.
        var span = Math.Min(width, height);
        var blobs = IsReduced ? 2 : CoverPaletteSize;

        for (var i = 0; i < blobs; i++)
        {
            var path = Paths[i];

            var centreX = width * (0.5 + DriftX * Math.Sin(_time * path.SpeedX * 2 * Math.PI + path.PhaseX));
            var centreY = height * (0.5 + DriftY * Math.Cos(_time * path.SpeedY * 2 * Math.PI + path.PhaseY));
            var radius = span * path.Radius * (1 + 0.06 * _breathing);

            // Kazda plama nieco slabsza od poprzedniej, zeby nie zlewaly sie w jedna plaszczyzne.
            //
            // Do tego wlasny cykl jasnienia: sinus o okresie i fazie wzietych z Pulses, przelozony
            // na zakres miedzy progami z ustawienia intensywnosci. Mnoznik intensywnosci wchodzi
            // tutaj, a nie w barwe — to samo pokretlo ma zmieniac sile calego tla, nie odcien
            // poszczegolnej plamy.
            var pulse = Pulses[i];
            var cycle = 0.5 + 0.5 * Math.Sin(_time * 2 * Math.PI / pulse.Period + pulse.Phase);
            var level = MinimumStrength + (MaximumStrength - MinimumStrength) * cycle;

            var strength = (0.86 - i * 0.11) * (1 + 0.18 * _breathing) * level;
            var colour = _shown[i];

            // Barwa trzyma sie dluzej przy srodku i opada szybciej przy brzegu, zamiast gasnac
            // rownomiernie. Rownomierne przejscie na calej szerokosci daje mgle bez ksztaltu;
            // takie ma wyrazny rdzen i czytelna krawedz, po ktorej widac, ze plama sie przesuwa.
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(centreX, centreY, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(centreX, centreY, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(radius, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(radius, RelativeUnit.Absolute),
                GradientStops =
                {
                    new GradientStop(WithAlpha(colour, strength), 0),
                    new GradientStop(WithAlpha(colour, strength * 0.74), 0.30),
                    new GradientStop(WithAlpha(colour, strength * 0.30), 0.62),
                    new GradientStop(WithAlpha(colour, 0), 1),
                },
            };

            context.DrawRectangle(brush, null, area);
        }
    }

    private static Color WithAlpha(Color colour, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), colour.R, colour.G, colour.B);
}
