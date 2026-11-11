using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// Drobinki światła w tle sceny: miękkie kropki w barwach okładki, które powoli unoszą się i lekko
/// kołyszą. Ruch jest wolny i równy, a krycie małe, więc jasność kadru prawie się nie zmienia (bez
/// błysków). W pauzie, przy ograniczonym ruchu i w ukrytej scenie pętla stoi; zostaje ostatni kadr.
/// </summary>
public sealed class SceneParticles : Control
{
    /// <summary>Tempo animacji: połowa częstotliwości ekranu wystarcza przy tak wolnym ruchu.</summary>
    private const double MinInterval = 1.0 / 30;

    /// <summary>Najdłuższy krok czasu: po przerwie drobinki nie przeskakują.</summary>
    private const double MaxStep = 0.1;

    public static readonly StyledProperty<IReadOnlyList<Color>?> PaletteProperty =
        AvaloniaProperty.Register<SceneParticles, IReadOnlyList<Color>?>(nameof(Palette));

    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<SceneParticles, int>(nameof(Count), 40);

    /// <summary>Krycie drobinki w środku, 0–1.</summary>
    public static readonly StyledProperty<double> StrengthProperty =
        AvaloniaProperty.Register<SceneParticles, double>(nameof(Strength), 0.35);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<SceneParticles, bool>(nameof(IsActive));

    private readonly RenderLoop _loop;
    private readonly List<Particle> _particles = [];
    private readonly List<IBrush> _brushes = [];
    private TimeSpan _last;
    private bool _hasLast;

    /// <summary>Stałe ziarno: ten sam kadr początkowy w każdym uruchomieniu i w testach.</summary>
    private readonly Random _random = new(7);

    public SceneParticles()
    {
        IsHitTestVisible = false;
        _loop = new RenderLoop(this, OnFrame);
        Populate();
        RebuildBrushes();
    }

    public IReadOnlyList<Color>? Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public double Strength
    {
        get => GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Liczba narysowanych drobinek, dla testów.</summary>
    internal int ParticleCount => _particles.Count;

    /// <summary>Wysokość i faza kołysania każdej drobinki, dla testów.</summary>
    internal IEnumerable<(double Y, double Phase)> Positions => _particles.Select(p => (p.Y, p.Phase));

    /// <summary>Pętla klatek działa, dla testów.</summary>
    internal bool IsRunning => _loop.IsRunning;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CountProperty)
        {
            Populate();
            InvalidateVisual();
        }
        else if (change.Property == PaletteProperty || change.Property == StrengthProperty)
        {
            RebuildBrushes();
            InvalidateVisual();
        }
        else if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty)
        {
            UpdateLoop();
        }
    }

    private void UpdateLoop()
    {
        _hasLast = false;
        if (IsActive && IsVisible) _loop.Start();
        else _loop.Stop();
    }

    private void Populate()
    {
        _particles.Clear();
        for (var i = 0; i < Math.Max(0, Count); i++)
        {
            _particles.Add(new Particle
            {
                X = _random.NextDouble(),
                Y = _random.NextDouble(),
                Radius = 3 + _random.NextDouble() * 7,
                Rise = 0.008 + _random.NextDouble() * 0.018,
                Sway = 0.004 + _random.NextDouble() * 0.01,
                Phase = _random.NextDouble() * Math.PI * 2,
                Colour = i,
            });
        }
    }

    /// <summary>Pędzle powstają raz na paletę: miękka kropka, kryjąca w środku i przezroczysta na brzegu.</summary>
    private void RebuildBrushes()
    {
        _brushes.Clear();
        var palette = Palette is { Count: > 0 } colours ? colours : [Colors.White];
        var alpha = (byte)Math.Round(Math.Clamp(Strength, 0, 1) * 255);
        foreach (var colour in palette)
        {
            // Barwy rozjaśnione, bo drobinki to światło, a nie plamy.
            var light = Color.FromRgb(
                (byte)(colour.R + (255 - colour.R) * 0.45),
                (byte)(colour.G + (255 - colour.G) * 0.45),
                (byte)(colour.B + (255 - colour.B) * 0.45));
            _brushes.Add(new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(alpha, light.R, light.G, light.B), 0),
                    new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 1),
                },
            });
        }
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_hasLast)
        {
            _last = now;
            _hasLast = true;
            return;
        }

        var dt = (now - _last).TotalSeconds;
        if (dt < MinInterval - 0.002) return;

        _last = now;
        Step(Math.Min(dt, MaxStep));
    }

    /// <summary>Mnożnik tempa ruchu z ustawień.</summary>
    public double MotionScale { get; set; } = 1;

    /// <summary>Jeden krok ruchu; drobinka, która wyjdzie górą, wraca dołem. Wołany przez pętlę i w testach.</summary>
    internal void Step(double dt)
    {
        dt *= MotionScale;
        foreach (var particle in _particles)
        {
            particle.Y -= particle.Rise * dt;
            particle.Phase += dt * 0.6;
            if (particle.Y < -0.05)
            {
                particle.Y = 1.05;
                particle.X = _random.NextDouble();
            }
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_brushes.Count == 0 || Bounds.Width < 1 || Bounds.Height < 1) return;

        // Promień rośnie z wysokością kadru, żeby drobinki miały ten sam wygląd na każdym ekranie.
        var scale = Bounds.Height / 1080;
        foreach (var particle in _particles)
        {
            var x = (particle.X + Math.Sin(particle.Phase) * particle.Sway) * Bounds.Width;
            var y = particle.Y * Bounds.Height;
            var radius = particle.Radius * scale;
            context.DrawEllipse(_brushes[particle.Colour % _brushes.Count], null, new Point(x, y), radius, radius);
        }
    }

    private sealed class Particle
    {
        public double X;
        public double Y;
        public double Radius;
        public double Rise;
        public double Sway;
        public double Phase;
        public int Colour;
    }
}
