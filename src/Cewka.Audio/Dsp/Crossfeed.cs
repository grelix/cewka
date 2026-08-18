namespace Cewka.Audio.Dsp;

/// <summary>
/// Crossfeed: domieszka sygnału z kanału przeciwnego, opóźniona i przytłumiona w górze pasma.
///
/// <para><b>Po co.</b> Nagrania stereo miksuje się z myślą o głośnikach, przy których każde ucho
/// słyszy oba kanały — z opóźnieniem i przytłumieniem wysokich tonów, bo dźwięk musi obejść
/// głowę. W słuchawkach ta droga nie istnieje i rozdział kanałów jest zupełny. Partie
/// spanoramowane do skraju — częste w nagraniach z lat sześćdziesiątych i siedemdziesiątych —
/// siedzą wtedy „w głowie" i po dłuższym słuchaniu męczą. Ten stopień przywraca brakującą drogę.</para>
///
/// <para><b>Jak.</b> Do każdego kanału dodawany jest sygnał drugiego, opóźniony o czas obejścia
/// głowy i przepuszczony przez filtr dolnoprzepustowy odpowiadający jej cieniowi akustycznemu.
/// Wysokie tony pozostają więc rozdzielone, a niskie — w których i tak nie słyszymy kierunku —
/// stają się niemal wspólne.</para>
///
/// <para>Nastawy odpowiadają zakresowi używanemu przez bibliotekę bs2b Borisa Michajłowa
/// (licencja MIT): częstotliwość graniczna 650–700 Hz przy domieszce od 4,5 do 9,5 dB.
/// Sam kod jest napisany od nowa, bez zaciągania biblioteki natywnej.</para>
/// </summary>
public sealed class Crossfeed : IAudioProcessor
{
    /// <summary>
    /// Czas obejścia głowy dla dźwięku padającego z boku. Odpowiada różnicy dróg do obu uszu
    /// przy typowym obwodzie głowy — stąd wartość rzędu jednej trzeciej milisekundy.
    /// </summary>
    private const double DelaySeconds = 0.00031;

    /// <summary>Siła w procentach: 0 wyłącza domieszkę, 100 daje najmocniejszą z nastaw.</summary>
    private const double MinimumFeedDb = 4.5;
    private const double MaximumFeedDb = 9.5;

    /// <summary>Częstotliwość graniczna filtru na drodze skrośnej, przy skrajnych siłach.</summary>
    private const double MinimumCutoffHz = 700;
    private const double MaximumCutoffHz = 650;

    private float[] _delayLeft = [];
    private float[] _delayRight = [];
    private int _delayFrames;
    private int _cursor;

    private int _channels = 2;
    private int _sampleRate = 48000;

    private double _lowpassCoefficient;
    private double _stateLeft;
    private double _stateRight;

    private float _direct = 1;
    private float _cross;

    private double _strength = 50;

    /// <summary>Gdy wyłączony, sygnał przechodzi bez zmiany ani jednej próbki.</summary>
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

        _delayFrames = Math.Max(1, (int)Math.Round(sampleRate * DelaySeconds));
        _delayLeft = new float[_delayFrames];
        _delayRight = new float[_delayFrames];
        _cursor = 0;

        _stateLeft = 0;
        _stateRight = 0;

        UpdateMix();
    }

    private void UpdateMix()
    {
        var fraction = _strength / 100.0;

        // Filtr jednobiegunowy. Przy mocniejszej domieszce granica schodzi niżej, tak jak
        // w nastawach bs2b: więcej sygnału skrośnego wymaga węższego pasma, inaczej obraz
        // stereo zapadłby się do środka.
        var cutoff = MinimumCutoffHz + (MaximumCutoffHz - MinimumCutoffHz) * fraction;
        _lowpassCoefficient = 1 - Math.Exp(-2 * Math.PI * cutoff / _sampleRate);

        var feedDb = MinimumFeedDb + (MaximumFeedDb - MinimumFeedDb) * fraction;
        var cross = Math.Pow(10, -feedDb / 20) * fraction;

        // Suma obu dróg utrzymana na jedności: bez tego włączenie crossfeedu podnosiłoby
        // głośność, a różnicę słychać jako „lepiej", choć to tylko głośniej.
        _cross = (float)(cross / (1 + cross));
        _direct = (float)(1.0 / (1 + cross));
    }

    public void Process(Span<float> buffer, int frames)
    {
        // Efekt ma sens wyłącznie dla dwóch kanałów. Przy monofonii nie ma czego mieszać,
        // przy większej liczbie kanałów pojęcie kanału przeciwnego przestaje być określone.
        if (!Enabled || _channels != 2 || _strength <= 0) return;

        for (var frame = 0; frame < frames; frame++)
        {
            var index = frame * 2;
            var left = buffer[index];
            var right = buffer[index + 1];

            // Z linii wychodzi próbka sprzed czasu obejścia głowy; na jej miejsce wchodzi bieżąca.
            var delayedLeft = _delayLeft[_cursor];
            var delayedRight = _delayRight[_cursor];
            _delayLeft[_cursor] = left;
            _delayRight[_cursor] = right;

            _cursor++;
            if (_cursor >= _delayFrames) _cursor = 0;

            _stateLeft += (delayedLeft - _stateLeft) * _lowpassCoefficient;
            _stateRight += (delayedRight - _stateRight) * _lowpassCoefficient;

            buffer[index] = left * _direct + (float)_stateRight * _cross;
            buffer[index + 1] = right * _direct + (float)_stateLeft * _cross;
        }
    }

    public void Reset()
    {
        Array.Clear(_delayLeft);
        Array.Clear(_delayRight);
        _cursor = 0;
        _stateLeft = 0;
        _stateRight = 0;
    }
}
