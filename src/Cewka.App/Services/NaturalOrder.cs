namespace Cewka.App.Services;

/// <summary>
/// Porządek naturalny nazw plików: ciągi cyfr porównywane jako liczby, reszta bez względu na
/// wielkość liter. „2 - Intro” wypada przed „10 - Finał”, jak w menedżerze plików.
/// <para>
/// Program działa bez danych kolacji (niezmienna globalizacja), więc litery porównywane są
/// porządkiem kodów znaków bez względu na wielkość. Dla nazw utworów w obrębie albumu wystarcza,
/// bo o kolejności decydują w nich numery.
/// </para>
/// </summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static NaturalOrder Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                var numberX = x.AsSpan(startX, i - startX).TrimStart('0');
                var numberY = y.AsSpan(startY, j - startY).TrimStart('0');

                if (numberX.Length != numberY.Length) return numberX.Length.CompareTo(numberY.Length);

                var digits = numberX.SequenceCompareTo(numberY);
                if (digits != 0) return digits;
                continue;
            }

            var letters = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (letters != 0) return letters;

            i++;
            j++;
        }

        var lengths = (x.Length - i).CompareTo(y.Length - j);
        return lengths != 0 ? lengths : string.CompareOrdinal(x, y);
    }
}
