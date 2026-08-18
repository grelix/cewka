namespace Cewka.Audio.Dsp;

/// <summary>
/// Kompensacja głośności: podbicie basu i skrajnej góry tym mocniejsze, im ciszej gra muzyka.
///
/// <para><b>Po co.</b> Czułość ucha zmienia się z poziomem. Przy cichym słuchaniu bas i najwyższe
/// tony słabną wyraźnie szybciej niż środek pasma — opisują to krzywe jednakowej głośności
/// znormalizowane w ISO 226. Dlatego wzmacniacze miały kiedyś przycisk „loudness". Ten stopień
/// robi to samo, tyle że wielkość korekty wynika z rzeczywistego poziomu odsłuchu, a nie
/// z jednego ustalonego z góry przybliżenia.</para>
///
/// <para><b>Skąd wiadomo, jak cicho gra.</b> Z dwóch rzeczy naraz: z położenia suwaka głośności
/// oraz z wzmocnienia, które nadała normalizacja EBU R128. To drugie jest tu istotne i stanowi
/// przewagę nad rozwiązaniami sterowanymi samym suwakiem: ciche nagranie, któremu normalizacja
/// dodała sześć decybeli, gra <em>głośniej</em>, niż wynikałoby z suwaka, i nie powinno dostać
/// pełnego podbicia basu.</para>
///
/// <para>Wartość odniesienia to suwak na maksimum przy zerowym wzmocnieniu normalizacji. Powyżej
/// niej nic się nie dzieje — stopień wyłącznie dodaje przy ściszaniu, nigdy nie odejmuje.</para>
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
    /// decybeli i zamiast wyrównywać słyszenie, zamieniłaby nagranie w dudnienie.
    /// </summary>
    private const double MaximumLowDb = 10;
    private const double MaximumHighDb = 4;

    /// <summary>Poniżej tej zmiany współczynniki zostają w spokoju.</summary>
    private const double RecomputeThresholdDb = 0.02;

    private Biquad[,] _sections = new Biquad[0, 0];

    private int _sampleRate = 48000;
    private int _channels = 2;
    private double _smoothingPerBuffer = 0.5;

    private double _targetDeficitDb;
    private double _currentDeficitDb;
    private double _appliedLowDb = -1;
    private double _appliedHighDb = -1;

    private double _strength = 100;

    /// <summary>Gdy wyłączona, sygnał przechodzi bez zmiany ani jednej próbki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Siła działania w procentach: 100 to pełna korekta, 50 to jej połowa.</summary>
    public double Strength
    {
        get => _strength;
        set => _strength = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// Bieżący ubytek poziomu względem odniesienia, w decybelach. Ustawiane raz na bufor
    /// przez <see cref="AudioGraph"/>, które jako jedyne widzi i suwak, i normalizację.
    /// </summary>
    public void SetListeningLevel(double volumeDb, double normalisationDb)
    {
        var deficit = -(volumeDb + normalisationDb);
        _targetDeficitDb = Math.Clamp(deficit, 0, 40);
    }

    /// <summary>Podbicie basu, jakie stopień właśnie stosuje. Do pokazania w interfejsie.</summary>
    public double CurrentBassBoostDb => _appliedLowDb < 0 ? 0 : _appliedLowDb;

    public void Prepare(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;

        // Dwie półki na kanał: dolna i górna.
        _sections = new Biquad[2, channels];
        for (var shelf = 0; shelf < 2; shelf++)
        for (var channel = 0; channel < channels; channel++)
            _sections[shelf, channel] = Biquad.Identity();

        const double assumedBufferSeconds = 0.01;
        _smoothingPerBuffer = 1 - Math.Exp(-assumedBufferSeconds / 0.05);

        _currentDeficitDb = _targetDeficitDb;
        _appliedLowDb = -1;
        UpdateCoefficients(force: true);
    }

    public void Process(Span<float> buffer, int frames)
    {
        if (!Enabled || _strength <= 0) return;

        UpdateCoefficients(force: false);

        // Przy odniesieniu obie półki są dokładnie przepustem; liczenie ich byłoby stratą czasu.
        if (_appliedLowDb < RecomputeThresholdDb && _appliedHighDb < RecomputeThresholdDb) return;

        for (var shelf = 0; shelf < 2; shelf++)
        {
            var gain = shelf == 0 ? _appliedLowDb : _appliedHighDb;
            if (gain < RecomputeThresholdDb) continue;

            for (var channel = 0; channel < _channels; channel++)
            {
                ref var section = ref _sections[shelf, channel];
                for (var frame = 0; frame < frames; frame++)
                {
                    var index = frame * _channels + channel;
                    buffer[index] = section.Process(buffer[index]);
                }
            }
        }
    }

    private void UpdateCoefficients(bool force)
    {
        // Ubytek zmienia się przy każdym ruchu suwaka głośności, a skok współczynników filtru
        // słychać jako trzask — stąd wygładzanie, tak samo jak w korektorze.
        if (!force && Math.Abs(_targetDeficitDb - _currentDeficitDb) < RecomputeThresholdDb)
        {
            if (_appliedLowDb >= 0) return;
        }

        _currentDeficitDb = force
            ? _targetDeficitDb
            : _currentDeficitDb + (_targetDeficitDb - _currentDeficitDb) * _smoothingPerBuffer;

        if (Math.Abs(_targetDeficitDb - _currentDeficitDb) < RecomputeThresholdDb)
            _currentDeficitDb = _targetDeficitDb;

        var fraction = _strength / 100.0;
        var low = Math.Min(MaximumLowDb, LowSlope * _currentDeficitDb) * fraction;
        var high = Math.Min(MaximumHighDb, HighSlope * _currentDeficitDb) * fraction;

        if (!force && Math.Abs(low - _appliedLowDb) < RecomputeThresholdDb
                   && Math.Abs(high - _appliedHighDb) < RecomputeThresholdDb) return;

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
