namespace Cewka.Audio.Dsp;

/// <summary>
/// Kompensacja głośności: podbicie basu i skrajnej góry tym mocniejsze, im ciszej gra muzyka.
///
/// <para>Wielkość korekty (według krzywych jednakowej głośności z ISO 226) wynika z położenia
/// suwaka i z tego, o ile poziom docelowy wyrównania odbiega od odniesienia ReplayGain; samo
/// wzmocnienie wyrównania nie mówi nic o głośności odsłuchu. Przy suwaku na maksimum i poziomie
/// odniesienia korekta wynosi zero i nigdy nie jest ujemna.</para>
/// </summary>
public sealed class Loudness : IAudioProcessor
{
    private const double LowShelfHz = 120;
    private const double HighShelfHz = 8000;
    private const double ShelfQ = 0.7071;

    /// <summary>
    /// Ile decybeli podbicia przypada na każdy decybel ściszenia. Wartości wzięte z różnicy
    /// krzywych jednakowej głośności między okolicą 80 a 50 fonów: bas traci mniej więcej
    /// jedną trzecią, góra około jednej siódmej ubytku poziomu.
    /// </summary>
    private const double LowSlope = 0.35;
    private const double HighSlope = 0.15;

    /// <summary>
    /// Ograniczenia. Bez nich przy suwaku na kilku procentach korekta sięgnęłaby kilkunastu
    /// decybeli i zamieniłaby nagranie w dudnienie.
    /// </summary>
    private const double MaximumLowDb = 10;
    private const double MaximumHighDb = 4;

    /// <summary>Poniżej tej zmiany współczynniki zostają w spokoju.</summary>
    private const double RecomputeThresholdDb = 0.02;

    /// <summary>Poniżej tej różnicy udział siły przyciągany jest do celu.</summary>
    private const double FractionSnap = 1e-3;

    private const double SmoothingSeconds = 0.05;

    private Biquad[,] _sections = new Biquad[0, 0];
    private readonly bool[] _shelfRunning = new bool[2];

    private int _sampleRate = 48000;
    private int _channels = 2;

    private double _targetDeficitDb;
    private double _currentDeficitDb;
    private double _currentFraction;
    private double _appliedLowDb = -1;
    private double _appliedHighDb = -1;

    private double _strength = 100;

    /// <summary>Po wyłączeniu podbicie wygasa płynnie, a potem sygnał przechodzi bez zmiany ani jednej próbki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Siła działania w procentach: 100 to pełna korekta, 50 to jej połowa.</summary>
    public double Strength
    {
        get => _strength;
        set => _strength = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// Ustawia poziom odsłuchu. Wołane raz na bufor przez <see cref="AudioGraph"/>.
    /// </summary>
    /// <param name="volumeDb">Położenie suwaka głośności, w decybelach.</param>
    /// <param name="programmeLevelDb">O ile program gra głośniej od poziomu odniesienia ReplayGain.</param>
    public void SetListeningLevel(double volumeDb, double programmeLevelDb)
    {
        var deficit = -(volumeDb + programmeLevelDb);
        _targetDeficitDb = Math.Clamp(deficit, 0, 40);
    }

    /// <summary>Podbicie basu, jakie stopień właśnie stosuje. Do pokazania w interfejsie.</summary>
    public double CurrentBassBoostDb => _appliedLowDb < 0 ? 0 : _appliedLowDb;

    private double TargetFraction => Enabled ? _strength / 100.0 : 0;

    public void Prepare(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;

        // Dwie półki na kanał: dolna i górna.
        _sections = new Biquad[2, channels];
        for (var shelf = 0; shelf < 2; shelf++)
        for (var channel = 0; channel < channels; channel++)
            _sections[shelf, channel] = Biquad.Identity();
        Array.Clear(_shelfRunning);

        _currentDeficitDb = _targetDeficitDb;
        _currentFraction = TargetFraction;
        _appliedLowDb = -1;
        UpdateCoefficients(0, force: true);
    }

    public void Process(Span<float> buffer, int frames)
    {
        if (TargetFraction <= 0 && _currentFraction <= 0) return;

        UpdateCoefficients(frames, force: false);

        for (var shelf = 0; shelf < 2; shelf++)
        {
            var gain = shelf == 0 ? _appliedLowDb : _appliedHighDb;

            // Przy odniesieniu półka jest przepustem; liczenie jej byłoby stratą czasu.
            if (gain < RecomputeThresholdDb)
            {
                _shelfRunning[shelf] = false;
                continue;
            }

            for (var channel = 0; channel < _channels; channel++)
            {
                ref var section = ref _sections[shelf, channel];

                // Stan zatrzymany przy pominięciu pochodzi sprzed wielu buforów.
                if (!_shelfRunning[shelf]) section.Reset();

                for (var frame = 0; frame < frames; frame++)
                {
                    var index = frame * _channels + channel;
                    buffer[index] = section.Process(buffer[index]);
                }

                section.Sanitise();
            }

            _shelfRunning[shelf] = true;
        }
    }

    private void UpdateCoefficients(int frames, bool force)
    {
        // Skok współczynników filtru słychać jako trzask, dlatego i ubytek, i siła dochodzą
        // do celu wykładniczo, ze stałą czasową liczoną w sekundach.
        var targetFraction = TargetFraction;

        if (!force && _appliedLowDb >= 0
                   && Math.Abs(_targetDeficitDb - _currentDeficitDb) < RecomputeThresholdDb
                   && Math.Abs(targetFraction - _currentFraction) < FractionSnap) return;

        var step = force ? 1 : 1 - Math.Exp(-frames / (_sampleRate * SmoothingSeconds));

        _currentDeficitDb += (_targetDeficitDb - _currentDeficitDb) * step;
        if (Math.Abs(_targetDeficitDb - _currentDeficitDb) < RecomputeThresholdDb)
            _currentDeficitDb = _targetDeficitDb;

        _currentFraction += (targetFraction - _currentFraction) * step;
        if (Math.Abs(targetFraction - _currentFraction) < FractionSnap)
            _currentFraction = targetFraction;

        var low = Math.Min(MaximumLowDb, LowSlope * _currentDeficitDb) * _currentFraction;
        var high = Math.Min(MaximumHighDb, HighSlope * _currentDeficitDb) * _currentFraction;

        if (!force && Math.Abs(low - _appliedLowDb) < RecomputeThresholdDb
                   && Math.Abs(high - _appliedHighDb) < RecomputeThresholdDb
                   && _currentFraction != 0) return;

        _appliedLowDb = low;
        _appliedHighDb = high;

        for (var channel = 0; channel < _channels; channel++)
        {
            _sections[0, channel].SetLowShelf(LowShelfHz, ShelfQ, low, _sampleRate);
            _sections[1, channel].SetHighShelf(HighShelfHz, ShelfQ, high, _sampleRate);
        }
    }

    public void Reset()
    {
        for (var shelf = 0; shelf < 2; shelf++)
        for (var channel = 0; channel < _channels; channel++)
            _sections[shelf, channel].Reset();
    }
}
