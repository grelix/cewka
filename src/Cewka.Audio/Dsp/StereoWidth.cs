namespace Cewka.Audio.Dsp;

/// <summary>
/// Poszerzenie bazy stereo macierzą środek–boki.
///
/// <para><b>Jak.</b> Sygnał rozkładany jest na środek — to, co wspólne obu kanałom — i boki,
/// czyli ich różnicę. Wzmacniane są wyłącznie boki. Środek pozostaje nietknięty, a to znaczy,
/// że <b>suma monofoniczna nie zmienia się wcale</b>: odsłuch na jednym głośniku brzmi tak samo
/// przy każdym ustawieniu. Poszerzanie opóźnieniem, spotykane w prostszych rozwiązaniach, tej
/// własności nie ma i potrafi wydrążyć nagranie odtworzone monofonicznie.</para>
///
/// <para><b>Niskie tony zostają nietknięte.</b> Poszerzanie basu rozjeżdża fazę między
/// głośnikami i odbiera nagraniu podstawę, a kierunku najniższych tonów i tak nie słyszymy.
/// Boki są więc rozdzielane na część niską i wysoką, i tylko ta druga jest wzmacniana. Nie
/// sprowadzam przy tym basu do monofonii — to, co nagrano, zostaje takie, jakie było.</para>
/// </summary>
public sealed class StereoWidth : IAudioProcessor
{
    /// <summary>Granica między basem zostawionym w spokoju a pasmem, które wolno poszerzać.</summary>
    private const double SplitFrequencyHz = 200;

    /// <summary>
    /// Najszersze dopuszczalne ustawienie. Powyżej mniej więcej tej wartości boki zaczynają
    /// przeważać nad środkiem i nagranie brzmi, jakby wyjęto z niego wokal.
    /// </summary>
    private const double MaximumWidth = 1.8;

    /// <summary>
    /// Jeden filtr, nie dwa: pracuje na sygnale boku, który jest już pojedynczym przebiegiem.
    /// </summary>
    private Biquad _sideLowPass = Biquad.Identity();

    private int _channels = 2;
    private double _strength;
    private float _width = 1;

    /// <summary>Gdy wyłączone, sygnał przechodzi bez zmiany ani jednej próbki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Siła działania w procentach: 0 to brak poszerzenia, 100 to ustawienie najszersze.</summary>
    public double Strength
    {
        get => _strength;
        set
        {
            _strength = Math.Clamp(value, 0, 100);
            _width = (float)(1 + (MaximumWidth - 1) * (_strength / 100.0));
        }
    }

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;

        // Filtr dolnoprzepustowy o dobroci Butterwortha: przy takiej dobroci część niska
        // i wysoka sumują się z powrotem bez garbu w okolicy podziału.
        _sideLowPass = Biquad.Identity();
        _sideLowPass.SetLowPass(SplitFrequencyHz, 0.7071, sampleRate);
    }

    public void Process(Span<float> buffer, int frames)
    {
        if (!Enabled || _channels != 2 || _strength <= 0) return;

        var width = _width;

        for (var frame = 0; frame < frames; frame++)
        {
            var index = frame * 2;
            var left = buffer[index];
            var right = buffer[index + 1];

            var middle = (left + right) * 0.5f;
            var side = (left - right) * 0.5f;

            // Bok rozdzielony na to, co poniżej granicy, i resztę. Filtr pracuje na sygnale
            // boku, a nie na kanałach osobno — inaczej stan filtrów rozjechałby się między nimi.
            var sideLow = _sideLowPass.Process(side);
            var sideHigh = side - sideLow;

            var widened = sideLow + sideHigh * width;

            buffer[index] = middle + widened;
            buffer[index + 1] = middle - widened;
        }
    }

    public void Reset() => _sideLowPass.Reset();
}
