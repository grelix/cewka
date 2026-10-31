namespace Cewka.App.Services;

/// <summary>
/// Odczyt okładki bieżącego utworu poza wątkiem interfejsu. Wynik trafia dalej tylko wtedy,
/// gdy w międzyczasie nie zamówiono innej okładki ani nie unieważniono odczytu.
///
/// <para>Pułapka: odczyt, który skończy się po zamknięciu okna, utworzyłby obraz na zwolnionym
/// modelu widoku i nikt by go już nie zwolnił, dlatego zamknięcie też unieważnia odczyt.</para>
/// </summary>
public sealed class CoverLoader(Func<string, byte[]?> read)
{
    private int _generation;

    /// <summary>Czyta okładkę i przekazuje ją <paramref name="apply"/>, jeśli wciąż jest aktualna.</summary>
    public async Task LoadAsync(string path, Action<byte[]?> apply)
    {
        var generation = Interlocked.Increment(ref _generation);
        var bytes = await Task.Run(() => read(path));

        if (generation == Volatile.Read(ref _generation)) apply(bytes);
    }

    /// <summary>Odrzuca wynik trwającego odczytu.</summary>
    public void Cancel() => Interlocked.Increment(ref _generation);
}
