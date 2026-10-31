namespace Cewka.Audio.Dsp;

/// <summary>
/// Crossfeed: domieszka sygnału z kanału przeciwnego, przytłumiona w górze pasma.
///
/// <para>Nagrania stereo miksuje się z myślą o głośnikach, przy których każde ucho
/// słyszy oba kanały — z opóźnieniem i przytłumieniem wysokich tonów, bo dźwięk musi obejść
/// głowę. W słuchawkach ta droga nie istnieje i rozdział kanałów jest zupełny. Partie
/// spanoramowane do skraju — częste w nagraniach z lat sześćdziesiątych i siedemdziesiątych —
/// siedzą wtedy „w głowie" i po dłuższym słuchaniu męczą. Ten stopień przywraca brakującą drogę.</para>
///
/// <para>Do każdego kanału dodawana jest różnica kanałów przepuszczona przez filtr
/// dolnoprzepustowy odpowiadający cieniowi akustycznemu głowy; opóźnienie fazowe tego filtru,
/// około ćwierć milisekundy, odpowiada czasowi obejścia głowy. Niskie tony stają się więc
/// w części wspólne, wysokie pozostają rozdzielone. Materiał wspólny obu kanałom nie ma różnicy,
/// więc przechodzi bez zmiany: wokal na środku nie traci góry ani poziomu.</para>
///
/// <para>Nastawy odpowiadają bibliotece bs2b Borisa Michajłowa (licencja MIT): częstotliwość
/// graniczna 650–700 Hz i różnica poziomów w basie między drogą bezpośrednią a skrośną od 15 dB
/// przy słabym ustawieniu do 4,5 dB przy pełnym, najmocniejszej z nastaw bs2b. Sam kod jest
/// napisany od nowa, bez zaciągania biblioteki natywnej.</para>
/// </summary>
public sealed class Crossfeed : IAudioProcessor
{
    /// <summary>Różnica poziomów w basie przy sile bliskiej zera i przy pełnej.</summary>
    private const double WeakestFeedDb = 15;
    private const double StrongestFeedDb = 4.5;

    /// <summary>Częstotliwość graniczna filtru na drodze skrośnej, przy skrajnych siłach.</summary>
    private const double MinimumCutoffHz = 700;
    private const double MaximumCutoffHz = 650;

    private const double SwitchSeconds = 0.020;

    private int _channels = 2;
    private int _sampleRate = 48000;

    private double _lowpassCoefficient;
    private double _difference;

    private double _switchCoefficient;
    private double _targetFeed;
    private double _feed;
    private bool _running;

    private double _strength = 50;

    /// <summary>Po wyłączeniu domieszka wygasa płynnie, a potem sygnał przechodzi bez zmiany ani jednej próbki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Siła działania w procentach, 0–100.</summary>
    public double Strength
    {
        get => _strength;
        set
        {
            _strength = Math.Clamp(value, 0, 100);
            UpdateMix();
        }
    }

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;
        _sampleRate = sampleRate;
        _difference = 0;
        _switchCoefficient = 1 - Math.Exp(-1.0 / (SwitchSeconds * sampleRate));

        UpdateMix();
        _feed = TargetFeed;
        _running = _feed > 0;
    }

    private void UpdateMix()
    {
        var fraction = _strength / 100.0;

        // Filtr jednobiegunowy. Przy mocniejszej domieszce granica schodzi niżej, tak jak
        // w nastawach bs2b: więcej sygnału skrośnego wymaga węższego pasma, inaczej obraz
        // stereo zapadłby się do środka.
        var cutoff = MinimumCutoffHz + (MaximumCutoffHz - MinimumCutoffHz) * fraction;
        _lowpassCoefficient = 1 - Math.Exp(-2 * Math.PI * cutoff / _sampleRate);

        // Domieszka k daje w basie drogę bezpośrednią 1 − k i skrośną k; ich stosunek to
        // różnica poziomów z nastaw bs2b.
        var feedDb = WeakestFeedDb + (StrongestFeedDb - WeakestFeedDb) * fraction;
        _targetFeed = _strength > 0 ? 1 / (1 + Math.Pow(10, feedDb / 20)) : 0;
    }

    private double TargetFeed => Enabled && _channels == 2 ? _targetFeed : 0;

    public void Process(Span<float> buffer, int frames)
    {
        // Efekt ma sens wyłącznie dla dwóch kanałów. Przy monofonii nie ma czego mieszać,
        // przy większej liczbie kanałów pojęcie kanału przeciwnego przestaje być określone.
        var target = TargetFeed;
        if (target == 0 && _feed == 0)
        {
            _running = false;
            return;
        }

        if (!_running) Reset();
        _running = true;

        var coefficient = _lowpassCoefficient;

        for (var frame = 0; frame < frames; frame++)
        {
            var index = frame * 2;
            var left = buffer[index];
            var right = buffer[index + 1];

            _difference += (right - left - _difference) * coefficient;
            _feed += (target - _feed) * _switchCoefficient;

            var shift = (float)(_feed * _difference);
            buffer[index] = left + shift;
            buffer[index + 1] = right - shift;
        }

        if (Math.Abs(target - _feed) < 1e-5) _feed = target;
        if (!double.IsFinite(_difference) || Math.Abs(_difference) < 1e-25) _difference = 0;
    }

    public void Reset() => _difference = 0;
}
