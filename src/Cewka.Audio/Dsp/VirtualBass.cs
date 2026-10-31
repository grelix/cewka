namespace Cewka.Audio.Dsp;

/// <summary>
/// Bas wirtualny: dokłada harmoniczne najniższych tonów, z których słuch odtwarza podstawową
/// tam, gdzie głośnik (np. laptopa) jej nie wypromieniuje. Suma kanałów z pasma 35–90 Hz jest
/// prostowana, harmoniczne wybierane pasmem 90–250 Hz i domieszane monofonicznie do obu kanałów.
/// Oryginalnego basu nie odcina.
/// </summary>
public sealed class VirtualBass : IAudioProcessor
{
    /// <summary>Pasmo, z którego powstają harmoniczne.</summary>
    private const double SourceLowHz = 35;
    private const double SourceHighHz = 90;

    /// <summary>Pasmo, do którego trafiają harmoniczne.</summary>
    private const double HarmonicLowHz = 90;
    private const double HarmonicHighHz = 250;

    /// <summary>Stała czasowa obwiedni, dość wolna, żeby nie modulować pojedynczych okresów.</summary>
    private const double EnvelopeSeconds = 0.030;

    /// <summary>Próg bramki, około −80 dBFS; poniżej bramka zamyka się łagodnie.</summary>
    private const double GateFloor = 1e-4;

    private const double SwitchSeconds = 0.020;

    private Biquad _sourceHighPass = Biquad.Identity();
    private Biquad _sourceLowPass = Biquad.Identity();
    private Biquad _harmonicHighPass = Biquad.Identity();
    private Biquad _harmonicLowPass = Biquad.Identity();

    private double _envelope;
    private double _envelopeCoefficient;
    private double _rectifierMean;

    private int _channels = 2;
    private double _strength = 50;
    private double _switchCoefficient;
    private double _mix;
    private bool _running;

    /// <summary>Po wyłączeniu domieszka wygasa płynnie, a potem sygnał przechodzi bez zmiany ani jednej próbki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Siła działania w procentach, 0–100.</summary>
    public double Strength
    {
        get => _strength;
        set => _strength = Math.Clamp(value, 0, 100);
    }

    // Nawet na maksimum skromna, żeby harmoniczne nie zamieniły się w warkot.
    private double TargetMix => Enabled ? _strength / 100.0 * 1.6 : 0;

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;

        _sourceHighPass = Biquad.Identity();
        _sourceLowPass = Biquad.Identity();
        _harmonicHighPass = Biquad.Identity();
        _harmonicLowPass = Biquad.Identity();

        _sourceHighPass.SetHighPass(SourceLowHz, 0.7071, sampleRate);
        _sourceLowPass.SetLowPass(SourceHighHz, 0.7071, sampleRate);
        _harmonicHighPass.SetHighPass(HarmonicLowHz, 0.7071, sampleRate);
        _harmonicLowPass.SetLowPass(HarmonicHighHz, 0.7071, sampleRate);

        _envelopeCoefficient = 1 - Math.Exp(-1.0 / (EnvelopeSeconds * sampleRate));
        _switchCoefficient = 1 - Math.Exp(-1.0 / (SwitchSeconds * sampleRate));
        _envelope = 0;
        _rectifierMean = 0;

        _mix = TargetMix;
        _running = _mix > 0;
    }

    public void Process(Span<float> buffer, int frames)
    {
        var targetMix = TargetMix;
        if (targetMix == 0 && _mix == 0)
        {
            _running = false;
            return;
        }

        // Filtry i obwiednie zatrzymane przy wyłączeniu opisują materiał sprzed wielu buforów.
        if (!_running) Reset();
        _running = true;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            // Średnia, żeby poziom nie zależał od liczby kanałów.
            var sum = 0f;
            for (var c = 0; c < _channels; c++) sum += buffer[baseIndex + c];
            sum /= _channels;

            var source = _sourceLowPass.Process(_sourceHighPass.Process(sum));

            var magnitude = Math.Abs(source);
            _envelope += (magnitude - _envelope) * _envelopeCoefficient;

            // Prostowanie wytwarza harmoniczne parzyste i składową stałą, którą trzeba odjąć,
            // żeby nie poszła dalej torem.
            var rectified = Math.Abs(source);
            _rectifierMean += (rectified - _rectifierMean) * _envelopeCoefficient;
            var harmonics = rectified - _rectifierMean;

            var shaped = _harmonicLowPass.Process(_harmonicHighPass.Process((float)harmonics));

            // Pułapka: obwiednia działa jako bramka dla materiału bez basu. Harmoniczne rosną
            // już proporcjonalnie do sygnału; mnożenie przez obwiednię dałoby zależność
            // kwadratową i efekt znikałby przy cichszej muzyce.
            var gate = _envelope / (_envelope + GateFloor);
            _mix += (targetMix - _mix) * _switchCoefficient;
            var added = (float)(shaped * gate * _mix);

            for (var c = 0; c < _channels; c++) buffer[baseIndex + c] += added;
        }

        if (Math.Abs(targetMix - _mix) < 1e-5) _mix = targetMix;
        Sanitise();
    }

    /// <summary>
    /// W ciszy cyfrowej obwiednie i stan filtrów wygasają w stronę liczb subnormalnych, kosztownych
    /// w każdej operacji; NaN zostałby w nich na zawsze.
    /// </summary>
    private void Sanitise()
    {
        _sourceHighPass.Sanitise();
        _sourceLowPass.Sanitise();
        _harmonicHighPass.Sanitise();
        _harmonicLowPass.Sanitise();

        if (!double.IsFinite(_envelope) || _envelope < 1e-25) _envelope = 0;
        if (!double.IsFinite(_rectifierMean) || _rectifierMean < 1e-25) _rectifierMean = 0;
    }

    public void Reset()
    {
        _sourceHighPass.Reset();
        _sourceLowPass.Reset();
        _harmonicHighPass.Reset();
        _harmonicLowPass.Reset();
        _envelope = 0;
        _rectifierMean = 0;
    }
}
