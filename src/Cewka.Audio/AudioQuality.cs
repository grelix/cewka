namespace Cewka.Audio;

/// <summary>
/// Conversion quality, as chosen by the user in the settings window.
///
/// <para><b>Dlaczego stan wspólny dla całej biblioteki.</b> Wartość odczytywana jest w chwili
/// tworzenia dekodera, a dekodery powstają w głębi <see cref="Decoding.DecoderFactory"/>.
/// Przeprowadzenie jej parametrem oznaczałoby dopisanie preferencji użytkownika do sygnatury
/// sześciu konstruktorów dekoderów i narzędzia diagnostycznego — czyli wpisanie ustawienia
/// interfejsu w miejsca, które o interfejsie nic nie wiedzą. Odtwarzany jest jeden utwór na
/// jednym urządzeniu, więc jedna wartość na proces opisuje stan rzeczy bez straty.</para>
///
/// <para><b>Kiedy zmiana zaczyna działać.</b> Od następnego otwarcia dekodera, czyli od
/// następnego utworu. Resampler jest stanowy — ma historię filtru — i podmiana jego rzędu
/// w trakcie utworu dałaby trzask dokładnie tam, gdzie zmiana miała poprawić brzmienie.</para>
/// </summary>
public static class AudioQuality
{
    /// <summary>Rząd filtru przyjęty przez miniaudio, gdy nie wskazano innego.</summary>
    public const int DefaultFilterOrder = 4;

    /// <summary>MA_MAX_FILTER_ORDER z miniaudio.</summary>
    public const int MaximumFilterOrder = 8;

    private static int _resamplerFilterOrder = DefaultFilterOrder;

    /// <summary>
    /// Rząd filtru dolnoprzepustowego resamplera: 0 wyłącza filtrowanie, 8 to maksimum.
    /// Ma znaczenie wyłącznie dla plików o częstotliwości innej niż urządzenie wyjściowe;
    /// przy zgodnych częstotliwościach resampler nie powstaje wcale.
    /// </summary>
    public static int ResamplerFilterOrder
    {
        get => _resamplerFilterOrder;
        set => _resamplerFilterOrder = Math.Clamp(value, 0, MaximumFilterOrder);
    }
}
