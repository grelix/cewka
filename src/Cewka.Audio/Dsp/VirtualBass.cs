namespace Cewka.Audio.Dsp;

/// <summary>
/// Bas wirtualny: dokłada harmoniczne najniższych tonów, żeby były słyszalne tam, gdzie
/// przetwornik nie potrafi ich wytworzyć.
///
/// <para><b>Po co.</b> Na głośnikach laptopa podbicie 45 Hz korektorem nie daje nic — membrana
/// tej częstotliwości nie wypromieniuje, a energia idzie w ciepło i zniekształcenia. Słuch
/// odtwarza jednak wysokość tonu z układu jego harmonicznych: słysząc 90 i 135 Hz, ucho
/// dopowiada sobie brakującą podstawową 45 Hz, której w powietrzu nie ma. Zjawisko nosi nazwę
/// brakującej podstawowej i jest znane od XIX wieku; ten stopień korzysta z niego wprost.</para>
///
/// <para><b>Jak.</b> Suma obu kanałów przepuszczana jest przez pasmo najniższych tonów, poddana
/// prostowaniu — które wytwarza harmoniczne parzyste — a wynik wybierany jest pasmem leżącym
/// wyżej i domieszany do obu kanałów. Poziom harmonicznych podąża za obwiednią pasma
/// wejściowego, więc pojawiają się wyłącznie wtedy, gdy jest z czego je zrobić; bez tego
/// stopień dudniłby przez cały czas.</para>
///
/// <para><b>Czego nie robi.</b> Nie usuwa oryginalnych niskich częstotliwości. Rozwiązania
/// komercyjne zwykle je odcinają, żeby odciążyć głośnik, ale na zestawie, który bas odtwarza,
/// byłoby to pogorszeniem. Domieszka jest monofoniczna — kierunku tak niskich tonów i tak
/// nie słyszymy.</para>
/// </summary>
public sealed class VirtualBass : IAudioProcessor
{
    /// <summary>Pasmo, z którego powstają harmoniczne — poniżej możliwości małych głośników.</summary>
    private const double SourceLowHz = 35;
    private const double SourceHighHz = 90;

    /// <summary>Pasmo, w którym harmoniczne mają wylądować, żeby dały się usłyszeć.</summary>
    private const double HarmonicLowHz = 90;
    private const double HarmonicHighHz = 250;

    /// <summary>Stała czasowa detektora obwiedni. Dość wolna, żeby nie modulować pojedynczych okresów.</summary>
    private const double EnvelopeSeconds = 0.030;

    /// <summary>
    /// Próg bramki, mniej więcej −80 dBFS. Powyżej niego bramka jest praktycznie otwarta,
    /// poniżej — zamyka się łagodnie, zamiast przełączać skokowo.
    /// </summary>
    private const double GateFloor = 1e-4;

    private Biquad _sourceHighPass = Biquad.Identity();
    private Biquad _sourceLowPass = Biquad.Identity();
    private Biquad _harmonicHighPass = Biquad.Identity();
    private Biquad _harmonicLowPass = Biquad.Identity();

    private double _envelope;
    private double _envelopeCoefficient;
    private double _rectifierMean;

    private int _channels = 2;
    private double _strength = 50;
    private float _mix;

    /// <summary>Gdy wyłączony, sygnał przechodzi bez zmiany ani jednej próbki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Siła działania w procentach, 0–100.</summary>
    public double Strength
    {
        get => _strength;
        set
        {
            _strength = Math.Clamp(value, 0, 100);

            // Domieszka celowo skromna nawet na maksimum: harmoniczne mają uzupełniać bas,
            // a nie zastępować go warkotem.
            _mix = (float)(_strength / 100.0 * 1.6);
        }
    }

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
        _envelope = 0;
        _rectifierMean = 0;

        Strength = _strength;
    }

    public void Process(Span<float> buffer, int frames)
    {
        if (!Enabled || _strength <= 0) return;

        var mix = _mix;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            // Suma kanałów, a nie każdy osobno: bas jest domieszany monofonicznie, więc i wytwarzać
            // go trzeba raz. Dzieli się przez liczbę kanałów, żeby poziom nie zależał od ich liczby.
            var sum = 0f;
            for (var c = 0; c < _channels; c++) sum += buffer[baseIndex + c];
            sum /= _channels;

            var source = _sourceLowPass.Process(_sourceHighPass.Process(sum));

            // Obwiednia pasma wejściowego: mówi, ile basu naprawdę jest w tej chwili.
            var magnitude = Math.Abs(source);
            _envelope += (magnitude - _envelope) * _envelopeCoefficient;

            // Prostowanie pełnookresowe wytwarza harmoniczne parzyste. Składowa stała, która
            // przy tym powstaje, jest odejmowana — inaczej wędrowałaby przez cały łańcuch.
            var rectified = Math.Abs(source);
            _rectifierMean += (rectified - _rectifierMean) * _envelopeCoefficient;
            var harmonics = rectified - _rectifierMean;

            var shaped = _harmonicLowPass.Process(_harmonicHighPass.Process((float)harmonics));

            // Obwiednia pracuje jako bramka, a nie jako wzmocnienie. Prostownik jest jednorodny
            // stopnia pierwszego, więc harmoniczne i tak rosną wprost proporcjonalnie do sygnału;
            // domnożenie ich jeszcze przez obwiednię dałoby zależność kwadratową i efekt
            // znikałby przy cichszej muzyce. Bramka służy do czego innego: ma nie dokładać
            // harmonicznych do materiału, który basu nie ma wcale.
            var gate = _envelope / (_envelope + GateFloor);
            var added = (float)(shaped * gate) * mix;

            for (var c = 0; c < _channels; c++) buffer[baseIndex + c] += added;
        }
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
