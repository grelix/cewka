namespace Cewka.App.Controls;

/// <summary>
/// Obrót płyty: kąt, powrót okładką do pionu po ograniczeniu efektów i pętla klatek, która działa
/// tylko wtedy, gdy płyta się kręci albo dojeżdża do pionu. Wspólny dla okna i sceny.
/// </summary>
public sealed class DiscSpinner
{
    /// <summary>Jeden obrót na dziewięć sekund, w oknie i w scenie.</summary>
    private const double DegreesPerSecond = 360.0 / 9.0;

    /// <summary>
    /// Czas, w jakim płyta staje okładką prosto po ograniczeniu efektów: dość długi, żeby było
    /// widać zwalnianie, i dość krótki, żeby nikt na to nie czekał.
    /// </summary>
    private const double UprightReturnSeconds = 0.45;

    private readonly VinylDisc _disc;
    private readonly RenderLoop _loop;
    private TimeSpan _lastFrame;
    private double _angle;
    private bool _playing;
    private bool _reduced;
    private double _speed = 1;
    private bool _frozen;

    /// <summary>Płyta wytraca obrót, żeby stanąć okładką prosto.</summary>
    private bool _returningUpright;
    private double _returnFrom;
    private double _returnElapsed;

    public DiscSpinner(VinylDisc disc)
    {
        _disc = disc;
        _loop = new RenderLoop(disc, OnFrame, OnShown);
    }

    public double Angle => _angle;

    public bool IsRunning => _loop.IsRunning;

    /// <summary>Pętla zamawia klatki, czyli działa i nie czeka na odsłonięcie płyty.</summary>
    public bool IsDrawing => _loop.IsRunning && !_loop.IsParked;

    private bool SpinNeeded => (_playing && !_reduced) || _returningUpright;

    /// <summary>Mnożnik prędkości obrotu z ostatniego <see cref="Update"/>.</summary>
    public double Speed => _speed;

    /// <summary>
    /// Nowy stan odtwarzania i efektów. Przejście w tryb ograniczony zaczyna powrót do pionu;
    /// przekrzywiona nieruchoma płyta wygląda na błąd.
    /// </summary>
    /// <param name="speed">Mnożnik tempa obrotu z ustawień; zero to płyta stojąca jak przy ograniczonym ruchu.</param>
    public void Update(bool playing, bool reduced, double speed = 1)
    {
        reduced |= speed <= 0;
        _speed = Math.Max(0, speed);

        if (reduced != _reduced)
        {
            _reduced = reduced;
            BeginUprightReturn();
        }

        _playing = playing;
        Wake();
    }

    /// <summary>Uruchamia pętlę, gdy jest co liczyć; np. po dołączeniu płyty do okna.</summary>
    public void Wake()
    {
        if (_frozen || _loop.IsRunning || !SpinNeeded) return;

        _lastFrame = TimeSpan.Zero;
        _loop.Start();
    }

    /// <summary>Zatrzymuje pętlę; trwający powrót do pionu kończy się od razu, bo płyty już nie widać.</summary>
    public void Stop()
    {
        _loop.Stop();
        if (_returningUpright) StandUpright();
    }

    /// <summary>Płyta prosto i bez ruchu, dla narzędzia zrzutów.</summary>
    public void Freeze()
    {
        _frozen = true;
        _loop.Stop();
        StandUpright();
    }

    /// <summary>Powrót do pionu odłożony na czas ukrycia płyty odegrałby się po odsłonięciu.</summary>
    private void OnShown()
    {
        if (_returningUpright) StandUpright();
    }

    private void StandUpright()
    {
        _returningUpright = false;
        _angle = 0;
        ApplyAngle();
    }

    private void OnFrame(TimeSpan timestamp)
    {
        var delta = _lastFrame == TimeSpan.Zero ? 0 : (timestamp - _lastFrame).TotalSeconds;
        _lastFrame = timestamp;

        Advance(delta);
        if (!SpinNeeded) _loop.Stop();
    }

    /// <summary>Jedna klatka ruchu; testy wołają ją wprost, z dowolnym krokiem czasu.</summary>
    internal void Advance(double seconds)
    {
        // Długi przestój (przeciąganie okna, uśpienie) nie może zakręcić płytą.
        var delta = Math.Clamp(seconds, 0, 0.25);

        // W trybie ograniczonym płyta stoi: to najdroższy element wystroju, bo każda klatka
        // to obrót bitmapy okładki.
        if (_playing && !_reduced)
        {
            _returningUpright = false;
            _angle = (_angle + delta * DegreesPerSecond * _speed) % 360;
            ApplyAngle();
        }
        else if (_returningUpright)
        {
            AdvanceUprightReturn(delta);
        }
    }

    private void BeginUprightReturn()
    {
        if (!_reduced || _angle <= 0)
        {
            _returningUpright = false;
            return;
        }

        // Ruchu niewidocznej płyty nikt nie zobaczy, a odłożony obróciłby ją po odsłonięciu,
        // już w trybie ograniczonym.
        if (!_loop.IsOwnerShown)
        {
            StandUpright();
            return;
        }

        _returningUpright = true;
        _returnFrom = _angle;
        _returnElapsed = 0;
    }

    /// <summary>
    /// Jedna klatka wytracania obrotu. Wygładzanie sześcienne: płyta rusza z prędkością bliską
    /// grania i staje łagodnie.
    /// </summary>
    private void AdvanceUprightReturn(double delta)
    {
        _returnElapsed += delta;

        var progress = Math.Clamp(_returnElapsed / UprightReturnSeconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);

        // Zawsze w przód, do pełnego obrotu, żeby płyta nie cofała się do góry.
        _angle = _returnFrom + (360 - _returnFrom) * eased;

        if (progress >= 1)
        {
            _angle = 0;
            _returningUpright = false;
        }

        ApplyAngle();
    }

    private void ApplyAngle() => _disc.Angle = _angle;
}
