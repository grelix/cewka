using Avalonia.Media.Imaging;

namespace Cewka.App.Services;

/// <summary>Okładka gotowa do pokazania: obraz w rozdzielczości płyty i mała próbka pikseli do palety.</summary>
/// <param name="Pixels">Piksele BGRA próbki do <see cref="CoverPalette.FromPixels"/>; null, gdy próbki nie dało się pobrać.</param>
public sealed record DecodedCover(Bitmap Display, byte[]? Pixels);

/// <summary>
/// Odczyt okładki bieżącego utworu poza wątkiem interfejsu. Wynik trafia dalej tylko wtedy,
/// gdy w międzyczasie nie zamówiono innej okładki ani nie unieważniono odczytu; wynik
/// spóźniony oddawany jest do <paramref name="discard"/>, żeby nie trzymać zdekodowanego obrazu.
///
/// <para>Pułapka: odczyt, który skończy się po zamknięciu okna, utworzyłby obraz na zwolnionym
/// modelu widoku i nikt by go już nie zwolnił, dlatego zamknięcie też unieważnia odczyt.</para>
/// </summary>
public sealed class CoverLoader<T>(Func<string, T?> read, Action<T>? discard = null) where T : class
{
    private int _generation;

    /// <summary>Czyta okładkę i przekazuje ją <paramref name="apply"/>, jeśli wciąż jest aktualna.</summary>
    public async Task LoadAsync(string path, Action<T?> apply)
    {
        var generation = Interlocked.Increment(ref _generation);
        var result = await Task.Run(() => read(path));

        if (generation == Volatile.Read(ref _generation)) apply(result);
        else if (result is not null) discard?.Invoke(result);
    }

    /// <summary>Odrzuca wynik trwającego odczytu.</summary>
    public void Cancel() => Interlocked.Increment(ref _generation);
}
