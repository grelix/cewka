namespace Cewka.Audio.Tests;

/// <summary>Pomiar przydziałów pamięci wątku, odporny na zbieranie pamięci wywołane z innego wątku.</summary>
internal static class Allocations
{
    /// <summary>
    /// Najmniejsza liczba bajtów przydzielonych przez <paramref name="measured"/> w jednej z prób.
    /// Licznik wątku rośnie też wtedy, gdy zbieranie pamięci z innego wątku zabiera mu niewykorzystaną
    /// resztę przydzielonego kawałka (do około 8 KB), więc pojedynczy pomiar bywa niezerowy bez winy
    /// mierzonego kodu. Prawdziwy przydział pojawia się w każdej próbie, więc najmniejsza różnica go
    /// nie ukrywa. <paramref name="prepare"/> działa poza pomiarem.
    /// </summary>
    internal static long Smallest(int attempts, Action prepare, Action measured)
    {
        var smallest = long.MaxValue;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            prepare();
            var before = GC.GetAllocatedBytesForCurrentThread();
            measured();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < smallest) smallest = allocated;
            if (smallest == 0) break;
        }

        return smallest;
    }
}
