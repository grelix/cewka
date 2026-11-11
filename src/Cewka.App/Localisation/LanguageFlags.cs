using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Cewka.App.Localisation;

/// <summary>
/// Małe flagi przy językach w ustawieniach. Pliki PNG 64 × 48 z paczki flag-icons (MIT), zamienione
/// z SVG przy dodaniu, bo program nie ma dekodera wektorów. Flaga oznacza kraj macierzysty języka;
/// angielski ma flagę Wielkiej Brytanii, portugalski — Portugalii, a portugalski brazylijski — Brazylii.
/// </summary>
public static class LanguageFlags
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    /// <summary>Flaga języka o kodzie <paramref name="code"/>; null dla „zgodnie z systemem” i języka bez flagi.</summary>
    public static Bitmap? For(string code)
    {
        if (Cache.TryGetValue(code, out var cached)) return cached;

        Bitmap? flag = null;
        var uri = new Uri($"avares://Cewka/Assets/Flags/{code}.png");
        if (AssetLoader.Exists(uri))
        {
            using var stream = AssetLoader.Open(uri);
            flag = new Bitmap(stream);
        }

        Cache[code] = flag;
        return flag;
    }
}
