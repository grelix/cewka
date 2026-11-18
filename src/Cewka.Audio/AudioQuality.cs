namespace Cewka.Audio;

/// <summary>Poziom konwersji częstotliwości próbkowania: długość i tłumienie filtru.</summary>
public enum ResamplingLevel
{
    /// <summary>Krótki filtr: pasmo płaskie do 16 kHz, składowe obce poniżej −70 dB.</summary>
    Economy,

    /// <summary>Pasmo płaskie do 20 kHz, składowe obce poniżej −95 dB.</summary>
    Standard,

    /// <summary>Pasmo płaskie do 20 kHz, składowe obce poniżej −120 dB.</summary>
    High,
}

/// <summary>
/// Jakość konwersji wybrana w ustawieniach. Stan wspólny dla procesu, bo dekodery powstają
/// w głębi <see cref="Decoding.DecoderFactory"/>, a gra jeden utwór na jednym urządzeniu.
///
/// <para>Zmiana działa od następnego otwarcia dekodera: filtr ma historię i podmiana w trakcie
/// utworu dałaby trzask.</para>
/// </summary>
public static class AudioQuality
{
    private static volatile ResamplingLevel _resampling = ResamplingLevel.Standard;

    [ThreadStatic] private static ResamplingLevel? _threadLevel;

    /// <summary>
    /// Poziom konwersji dla plików o częstotliwości innej niż urządzenie wyjściowe; przy zgodnych
    /// częstotliwościach konwersja nie powstaje wcale.
    /// </summary>
    public static ResamplingLevel Resampling
    {
        get => _resampling;
        set => _resampling = Enum.IsDefined(value) ? value : ResamplingLevel.Standard;
    }

    /// <summary>Poziom, z jakim powstaje dekoder otwierany w tym wątku.</summary>
    internal static ResamplingLevel Current => _threadLevel ?? _resampling;

    /// <summary>
    /// Dekodery otwierane w tym wątku do końca zakresu dostają wskazany poziom niezależnie od
    /// ustawienia. Analiza w tle nie słyszy różnicy, a długi filtr spowolniłby ją kilkakrotnie.
    /// </summary>
    internal static LevelScope UseInThisThread(ResamplingLevel level)
    {
        var previous = _threadLevel;
        _threadLevel = level;
        return new LevelScope(previous);
    }

    internal readonly struct LevelScope(ResamplingLevel? previous) : IDisposable
    {
        public void Dispose() => _threadLevel = previous;
    }
}
