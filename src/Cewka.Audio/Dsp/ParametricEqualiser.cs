namespace Cewka.Audio.Dsp;

/// <summary>Rodzaj filtru pasma korektora parametrycznego.</summary>
public enum ParametricFilterType
{
    /// <summary>Dzwon wokół częstotliwości środkowej (PK).</summary>
    Peak,

    /// <summary>Półka dolna (LSC): wszystko poniżej częstotliwości.</summary>
    LowShelf,

    /// <summary>Półka górna (HSC): wszystko powyżej częstotliwości.</summary>
    HighShelf,
}

/// <summary>Jedno pasmo korektora parametrycznego.</summary>
/// <param name="Id">
/// Stały identyfikator pasma. Tor rozpoznaje po nim pasmo po zmianie listy, więc usunięcie pasma
/// ze środka wycisza właśnie je, a nie przesuwa parametrów pozostałych.
/// </param>
public readonly record struct ParametricBand(
    int Id, ParametricFilterType Type, double Frequency, double Gain, double Q, bool Enabled = true)
{
    /// <summary>To samo pasmo z wartościami sprowadzonymi do dozwolonych zakresów.</summary>
    public ParametricBand Clamped() => this with
    {
        Type = Enum.IsDefined(Type) ? Type : ParametricFilterType.Peak,
        Frequency = ParametricEqualiser.ClampFrequency(Frequency),
        Gain = ParametricEqualiser.ClampGain(Gain),
        Q = ParametricEqualiser.ClampQ(Q),
    };
}

/// <summary>
/// Korektor parametryczny: do 20 pasm dzwonowych i półkowych z własnym przedwzmacniaczem.
/// <para>
/// Konfiguracja przychodzi z wątku interfejsu jako niezmienna tablica podmieniana jednym zapisem
/// referencji, więc wątek dźwiękowy nie czeka na blokadę i niczego nie przydziela.
/// </para>
/// <para>
/// Pułapka: skok współczynników filtru słychać jako trzask. Częstotliwość i dobroć idą do celu
/// w skali logarytmicznej, wzmocnienie w decybelach, ze stałą czasową 50 ms i nowymi
/// współczynnikami co 32 ramki. Pasmo nowe zaczyna od 0 dB, usunięte i wyłączone schodzi do 0 dB.
/// Zmiana rodzaju filtru schodzi do 0 dB, gdzie każdy rodzaj jest przepustem, i dopiero tam
/// zamienia filtr.
/// </para>
/// </summary>
public sealed class ParametricEqualiser : IAudioProcessor
{
    public const int MaxBands = 20;
    public const double MinFrequency = 20;
    public const double MaxFrequency = 20000;
    public const double MinGain = -24;
    public const double MaxGain = 24;
    public const double MinQ = 0.1;
    public const double MaxQ = 20;
    public const double MinPreamp = -12;
    public const double MaxPreamp = 12;

    private const double SmoothingSeconds = 0.05;
    private const int SubBlock = 32;

    /// <summary>Poniżej tej odległości od celu wzmocnienie dochodzi do niego od razu.</summary>
    private const double GainSnapDb = 0.01;

    /// <summary>Odpowiednik dla logarytmu częstotliwości i dobroci (ok. 0,02 %).</summary>
    private const double LogSnap = 2e-4;

    /// <summary>
    /// Miejsca na pasma: obecne w konfiguracji i te, które jeszcze wygasają po usunięciu. Pasm
    /// w konfiguracji jest najwyżej 20, więc dla wygasających zostaje co najmniej drugie tyle.
    /// </summary>
    private const int Slots = 2 * MaxBands;

    private static readonly ParametricBand[] NoBands = [];

    private ParametricBand[] _config = NoBands;
    private ParametricBand[]? _applied;

    private readonly int[] _id = new int[Slots];
    private readonly bool[] _used = new bool[Slots];
    private readonly bool[] _assigned = new bool[Slots];
    private readonly bool[] _running = new bool[Slots];
    private readonly ParametricFilterType[] _type = new ParametricFilterType[Slots];
    private readonly ParametricFilterType[] _targetType = new ParametricFilterType[Slots];
    private readonly double[] _logFrequency = new double[Slots];
    private readonly double[] _targetLogFrequency = new double[Slots];
    private readonly double[] _logQ = new double[Slots];
    private readonly double[] _targetLogQ = new double[Slots];
    private readonly double[] _gain = new double[Slots];
    private readonly double[] _targetGain = new double[Slots];
    private Biquad[,] _sections = new Biquad[Slots, 2];

    private double _targetPreamp;
    private double _currentPreamp;
    private float _preampLinear = 1;

    private int _sampleRate = 48000;
    private int _channels = 2;
    private double _subBlockStep;

    /// <summary>Wyłączony schodzi płynnie do przepustu i dalej nie dotyka próbek.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Przedwzmacniacz trybu parametrycznego w decybelach, −12…+12.</summary>
    public double Preamp
    {
        get => _targetPreamp;
        set => _targetPreamp = double.IsFinite(value) ? Math.Clamp(value, MinPreamp, MaxPreamp) : 0;
    }

    /// <summary>Bieżąca konfiguracja pasm, po przycięciu do zakresów.</summary>
    public IReadOnlyList<ParametricBand> Bands => Volatile.Read(ref _config);

    /// <summary>
    /// Ustawia pasma. Wątek interfejsu; wartości spoza zakresów są przycinane, nadmiar ponad
    /// <see cref="MaxBands"/> pomijany.
    /// </summary>
    /// <exception cref="ArgumentException">Dwa pasma mają ten sam identyfikator.</exception>
    public void SetBands(IEnumerable<ParametricBand> bands)
    {
        var copy = bands.Take(MaxBands).Select(band => band.Clamped()).ToArray();

        for (var i = 0; i < copy.Length; i++)
        for (var j = i + 1; j < copy.Length; j++)
            if (copy[i].Id == copy[j].Id)
                throw new ArgumentException($"Pasmo {copy[i].Id} występuje dwa razy.", nameof(bands));

        Volatile.Write(ref _config, copy);
    }

    public static double ClampFrequency(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinFrequency, MaxFrequency) : 1000;

    public static double ClampGain(double value) => double.IsFinite(value) ? Math.Clamp(value, MinGain, MaxGain) : 0;

    public static double ClampQ(double value) => double.IsFinite(value) ? Math.Clamp(value, MinQ, MaxQ) : 1.41;

    public void Prepare(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _sections = new Biquad[Slots, channels];
        _subBlockStep = 1 - Math.Exp(-SubBlock / (sampleRate * SmoothingSeconds));

        // Nowe urządzenie zaczyna od stanu docelowego: nie ma czego wygładzać.
        Array.Clear(_used);
        Array.Clear(_running);
        _applied = null;
        Reconcile(Volatile.Read(ref _config));

        for (var slot = 0; slot < Slots; slot++)
        {
            if (!_used[slot]) continue;

            _type[slot] = _targetType[slot];
            _logFrequency[slot] = _targetLogFrequency[slot];
            _logQ[slot] = _targetLogQ[slot];
            _gain[slot] = Enabled ? _targetGain[slot] : 0;
            Design(slot);
        }

        _currentPreamp = Enabled ? _targetPreamp : 0;
        _preampLinear = (float)Math.Pow(10, _currentPreamp / 20);
    }

    public void Process(Span<float> buffer, int frames)
    {
        var config = Volatile.Read(ref _config);
        if (!ReferenceEquals(config, _applied)) Reconcile(config);

        var enabled = Enabled;
        var targetPreamp = enabled ? _targetPreamp : 0;

        if (_currentPreamp == targetPreamp && _preampLinear == 1f && !AnyBandAudible(enabled)) return;

        for (var start = 0; start < frames; start += SubBlock)
        {
            var count = Math.Min(SubBlock, frames - start);
            var step = count == SubBlock ? _subBlockStep : 1 - Math.Exp(-count / (_sampleRate * SmoothingSeconds));
            var block = buffer.Slice(start * _channels, count * _channels);

            for (var slot = 0; slot < Slots; slot++)
            {
                if (!_used[slot]) continue;

                Advance(slot, step, enabled);
                Run(slot, block, count);
            }

            var preampBefore = _preampLinear;
            if (_currentPreamp != targetPreamp)
            {
                _currentPreamp += (targetPreamp - _currentPreamp) * step;
                if (Math.Abs(targetPreamp - _currentPreamp) < GainSnapDb) _currentPreamp = targetPreamp;
                _preampLinear = (float)Math.Pow(10, _currentPreamp / 20);
            }

            ApplyPreamp(block, count, preampBefore, _preampLinear);
        }
    }

    public void Reset()
    {
        for (var slot = 0; slot < Slots; slot++)
        for (var channel = 0; channel < _channels; channel++)
            _sections[slot, channel].Reset();
    }

    /// <summary>
    /// Charakterystyka zestawu pasm z przedwzmacniaczem, w decybelach, dla podanych częstotliwości.
    /// Wyłączone pasma się nie liczą. Bez przydziałów; dla wykresu i testów.
    /// </summary>
    public static void Response(
        ReadOnlySpan<ParametricBand> bands, double preamp, double sampleRate,
        ReadOnlySpan<double> frequencies, Span<double> resultDb)
    {
        for (var i = 0; i < frequencies.Length; i++) resultDb[i] = preamp;

        var section = Biquad.Identity();
        foreach (var raw in bands)
        {
            if (!raw.Enabled) continue;

            var band = raw.Clamped();
            if (band.Gain == 0) continue;

            section.SetParametric(band.Type, band.Frequency, band.Q, band.Gain, sampleRate);
            for (var i = 0; i < frequencies.Length; i++)
                resultDb[i] += section.MagnitudeDb(2 * Math.PI * frequencies[i] / sampleRate);
        }
    }

    // ---------- wątek dźwiękowy ----------

    /// <summary>
    /// Dopasowuje miejsca do nowej konfiguracji po identyfikatorach pasm. Bez przydziałów:
    /// tablice miejsc mają stały rozmiar.
    /// </summary>
    private void Reconcile(ParametricBand[] config)
    {
        _applied = config;

        for (var slot = 0; slot < Slots; slot++)
        {
            if (!_used[slot]) continue;

            var found = false;
            foreach (var band in config)
            {
                if (band.Id != _id[slot]) continue;
                found = true;
                break;
            }

            _assigned[slot] = found;
            if (!found) _targetGain[slot] = 0;
        }

        foreach (var band in config)
        {
            var slot = SlotOf(band.Id);
            if (slot < 0)
            {
                slot = FreeSlot();
                _used[slot] = true;
                _running[slot] = false;
                _id[slot] = band.Id;
                _type[slot] = band.Type;
                _logFrequency[slot] = Math.Log(band.Frequency);
                _logQ[slot] = Math.Log(band.Q);
                _gain[slot] = 0;
                for (var channel = 0; channel < _channels; channel++) _sections[slot, channel] = Biquad.Identity();
            }

            _assigned[slot] = true;
            _targetType[slot] = band.Type;
            _targetLogFrequency[slot] = Math.Log(band.Frequency);
            _targetLogQ[slot] = Math.Log(band.Q);
            _targetGain[slot] = band.Enabled ? band.Gain : 0;
        }
    }

    private int SlotOf(int id)
    {
        for (var slot = 0; slot < Slots; slot++)
            if (_used[slot] && _id[slot] == id) return slot;
        return -1;
    }

    /// <summary>
    /// Wolne miejsce, a gdy wszystkie zajmują wygasające pasma (bardzo szybkie zmiany listy),
    /// to z wygasających najcichsze.
    /// </summary>
    private int FreeSlot()
    {
        var quietest = -1;
        for (var slot = 0; slot < Slots; slot++)
        {
            if (!_used[slot]) return slot;
            if (_assigned[slot]) continue;
            if (quietest < 0 || Math.Abs(_gain[slot]) < Math.Abs(_gain[quietest])) quietest = slot;
        }

        return quietest;
    }

    private bool AnyBandAudible(bool enabled)
    {
        for (var slot = 0; slot < Slots; slot++)
        {
            if (!_used[slot]) continue;
            if (_running[slot] || _gain[slot] != 0) return true;
            if (enabled && _targetGain[slot] != 0) return true;
        }

        return false;
    }

    /// <summary>Przesuwa parametry pasma o jeden krok wygładzania i przelicza współczynniki, gdy się zmieniły.</summary>
    private void Advance(int slot, double step, bool enabled)
    {
        var typeChanges = _type[slot] != _targetType[slot];

        // Przy zmianie rodzaju pasmo najpierw schodzi do zera.
        var target = enabled && _assigned[slot] && !typeChanges ? _targetGain[slot] : 0;
        var changed = false;

        if (_gain[slot] != target)
        {
            var next = _gain[slot] + (target - _gain[slot]) * step;
            if (Math.Abs(target - next) < GainSnapDb) next = target;
            _gain[slot] = next;
            changed = true;
        }

        if (_gain[slot] == 0)
        {
            // Przy 0 dB filtr każdego rodzaju jest przepustem, więc parametry mogą od razu
            // stanąć u celu, a pasmo, którego nie ma już w konfiguracji, zwalnia miejsce.
            if (typeChanges || _logFrequency[slot] != _targetLogFrequency[slot] || _logQ[slot] != _targetLogQ[slot])
            {
                _type[slot] = _targetType[slot];
                _logFrequency[slot] = _targetLogFrequency[slot];
                _logQ[slot] = _targetLogQ[slot];
                changed = true;
            }

            if (!_assigned[slot] && !_running[slot]) _used[slot] = false;
        }
        else
        {
            changed |= Approach(ref _logFrequency[slot], _targetLogFrequency[slot], step);
            changed |= Approach(ref _logQ[slot], _targetLogQ[slot], step);
        }

        if (changed) Design(slot);
    }

    private static bool Approach(ref double current, double target, double step)
    {
        if (current == target) return false;

        current += (target - current) * step;
        if (Math.Abs(target - current) < LogSnap) current = target;
        return true;
    }

    private void Design(int slot)
    {
        var frequency = Math.Exp(_logFrequency[slot]);
        var q = Math.Exp(_logQ[slot]);
        for (var channel = 0; channel < _channels; channel++)
            _sections[slot, channel].SetParametric(_type[slot], frequency, q, _gain[slot], _sampleRate);
    }

    private void Run(int slot, Span<float> block, int frames)
    {
        if (_gain[slot] == 0)
        {
            // Filtr przy 0 dB ma współczynniki przepustu; jeszcze jeden przebieg wypłukuje
            // z jego stanu resztę sprzed zejścia do zera, potem pasmo przestaje liczyć.
            if (!_running[slot]) return;
            _running[slot] = false;
        }
        else
        {
            _running[slot] = true;
        }

        for (var channel = 0; channel < _channels; channel++)
        {
            ref var section = ref _sections[slot, channel];
            for (var frame = 0; frame < frames; frame++)
            {
                var index = frame * _channels + channel;
                block[index] = section.Process(block[index]);
            }

            section.Sanitise();
            if (!_running[slot]) section.Reset();
        }
    }

    private void ApplyPreamp(Span<float> block, int frames, float from, float to)
    {
        if (from == to)
        {
            if (to == 1f) return;
            for (var i = 0; i < frames * _channels; i++) block[i] *= to;
            return;
        }

        var step = (to - from) / frames;
        for (var frame = 0; frame < frames; frame++)
        {
            var gain = from + step * (frame + 1);
            var baseIndex = frame * _channels;
            for (var c = 0; c < _channels; c++) block[baseIndex + c] *= gain;
        }
    }
}
