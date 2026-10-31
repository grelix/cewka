namespace Cewka.Audio.Dsp;

/// <summary>
/// Poszerzenie bazy stereo macierzą środek–boki.
///
/// <para>Sygnał rozkładany jest na środek — to, co wspólne obu kanałom — i boki,
/// czyli ich różnicę. Wzmacniane są wyłącznie boki. Środek pozostaje nietknięty, a to znaczy,
/// że suma monofoniczna nie zmienia się wcale: odsłuch na jednym głośniku brzmi tak samo
/// przy każdym ustawieniu. Poszerzanie opóźnieniem, spotykane w prostszych rozwiązaniach, tej
/// własności nie ma i potrafi wydrążyć nagranie odtworzone monofonicznie.</para>
///
/// <para>Boki wzmacnia półka górna, więc bas prawie nie zmienia szerokości: poszerzanie go
/// rozjeżdża fazę między głośnikami i odbiera nagraniu podstawę, a kierunku najniższych tonów
/// i tak nie słyszymy. Półka o dobroci 0,7071 przechodzi z jedności do pełnego wzmocnienia
/// monotonicznie, bez garbu i bez dołka. Nie sprowadzam przy tym basu do monofonii — to, co
/// nagrano, zostaje takie, jakie było.</para>
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
    /// Jeden filtr, nie dwa: pracuje na sygnale boku, który jest już pojedynczym przebiegiem.
    /// Ustawiony na pełną szerokość; słabsze ustawienia mieszają jego wynik z bokiem bez zmian.
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

            // Filtr pracuje na sygnale boku, a nie na kanałach osobno — inaczej stan filtrów
            // rozjechałby się między nimi.
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
