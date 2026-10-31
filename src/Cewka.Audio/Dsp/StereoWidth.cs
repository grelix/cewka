namespace Cewka.Audio.Dsp;

/// <summary>
/// Poszerzenie bazy stereo macierzą środek–boki. Wzmacniane są tylko boki, więc suma
/// monofoniczna zostaje bez zmian.
///
/// <para>Boki wzmacnia półka górna o dobroci 0,7071 (przejście monotoniczne), żeby bas prawie
/// nie zmieniał szerokości.</para>
/// </summary>
public sealed class StereoWidth : IAudioProcessor
{
    /// <summary>Środek przejścia półki: poniżej boki zostają prawie bez zmian.</summary>
    private const double ShelfFrequencyHz = 300;

    /// <summary>
    /// Najszersze dopuszczalne ustawienie. Powyżej mniej więcej tej wartości boki zaczynają
    /// przeważać nad środkiem i nagranie brzmi, jakby wyjęto z niego wokal.
    /// </summary>
    private const double MaximumWidth = 1.8;

    private const double SwitchSeconds = 0.020;

    /// <summary>
    /// Filtr sygnału boku ustawiony na pełną szerokość; słabsze ustawienia mieszają jego wynik
    /// z bokiem bez zmian.
    /// </summary>
    private Biquad _sideShelf = Biquad.Identity();

    private int _channels = 2;
    private double _strength;
    private double _switchCoefficient;
    private double _amount;
    private bool _running;

    /// <summary>Po wyłączeniu poszerzenie wygasa płynnie, a potem sygnał przechodzi bez zmiany ani jednej próbki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Siła działania w procentach: 0 to brak poszerzenia, 100 to ustawienie najszersze.</summary>
    public double Strength
    {
        get => _strength;
        set => _strength = Math.Clamp(value, 0, 100);
    }

    private double TargetAmount => Enabled && _channels == 2 ? _strength / 100.0 : 0;

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;

        _sideShelf = Biquad.Identity();
        _sideShelf.SetHighShelf(ShelfFrequencyHz, 0.7071, 20 * Math.Log10(MaximumWidth), sampleRate);

        _switchCoefficient = 1 - Math.Exp(-1.0 / (SwitchSeconds * sampleRate));
        _amount = TargetAmount;
        _running = _amount > 0;
    }

    public void Process(Span<float> buffer, int frames)
    {
        var target = TargetAmount;
        if (target == 0 && _amount == 0)
        {
            _running = false;
            return;
        }

        if (!_running) _sideShelf.Reset();
        _running = true;

        for (var frame = 0; frame < frames; frame++)
        {
            var index = frame * 2;
            var left = buffer[index];
            var right = buffer[index + 1];

            var middle = (left + right) * 0.5f;
            var side = (left - right) * 0.5f;

            // Jeden filtr na sygnale boku; dwa osobne filtry na kanałach mogłyby rozjechać się stanem.
            var shelved = _sideShelf.Process(side);

            _amount += (target - _amount) * _switchCoefficient;
            var widened = side + (float)_amount * (shelved - side);

            buffer[index] = middle + widened;
            buffer[index + 1] = middle - widened;
        }

        _sideShelf.Sanitise();
        if (Math.Abs(target - _amount) < 1e-4) _amount = target;
    }

    public void Reset() => _sideShelf.Reset();
}
