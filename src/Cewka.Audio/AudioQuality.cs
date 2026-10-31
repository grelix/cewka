namespace Cewka.Audio;

/// <summary>
/// Jakość konwersji wybrana w ustawieniach. Stan wspólny dla procesu, bo dekodery powstają
/// w głębi <see cref="Decoding.DecoderFactory"/>, a gra jeden utwór na jednym urządzeniu.
///
/// <para>Zmiana działa od następnego otwarcia dekodera: resampler ma historię filtru i podmiana
/// rzędu w trakcie utworu dałaby trzask.</para>
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
