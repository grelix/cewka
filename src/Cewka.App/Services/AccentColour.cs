using Avalonia.Media;

namespace Cewka.App.Services;

/// <summary>
/// Barwa akcentu wyprowadzona z okładki — ta, którą świecą przełączniki, suwaki i wypełnienia.
///
/// <para><b>Dlaczego osobno od palety tła.</b> Plamy tła mają być tłem: ich nasycenie skaluje
/// ustawienie intensywności barw, a jasność jest sprowadzana do pasma, na którym da się czytać
/// tekst. Akcent służy do czego innego — ma być widoczny i ma mówić, że coś jest włączone.
/// Dlatego bierze z okładki wyłącznie odcień i nasycenie, zawsze w pełnej sile, niezależnie od
/// tego, jak ustawiono intensywność.</para>
///
/// <para><b>Jasność jest narzucana, i to celowo.</b> Okładka niemal czarna dałaby akcent niemal
/// czarny, czyli przełączniki nie do odróżnienia od tła. Odcień i nasycenie pochodzą z okładki,
/// jasność z motywu — inaczej barwa okładki bywałaby nie do zobaczenia.</para>
/// </summary>
public static class AccentColour
{
    /// <summary>
    /// Poniżej tego nasycenia okładka nie ma barwy, którą warto pokazać — zdjęcie czarno-białe,
    /// skan szarej wkładki. Wymuszanie akcentu z takiego materiału dałoby odcień przypadkowy.
    /// </summary>
    private const double MinimumUsableSaturation = 0.12;

    /// <summary>Jasność akcentu w każdym z motywów, dobrana tak jak w tokenach.</summary>
    private const double DarkThemeLightness = 0.72;
    private const double LightThemeLightness = 0.45;

    /// <summary>Dolna granica nasycenia wyniku: barwa ma być widoczna jako barwa.</summary>
    private const double MinimumResultSaturation = 0.42;

    /// <summary>
    /// Wartości domyślne z <c>Themes/Tokens.axaml</c>. Powielone tutaj świadomie: po nadpisaniu
    /// zasobu nie da się już odczytać tego, co było pod spodem, a przy okładce bez barwy trzeba
    /// mieć dokąd wrócić.
    /// </summary>
    public static Color Default(bool darkTheme) =>
        darkTheme ? Color.Parse("#FF7FB6EF") : Color.Parse("#FF2A75BA");

    /// <summary>
    /// Akcent dla podanej palety, albo wartość domyślna motywu, gdy w okładce nie ma barwy.
    /// </summary>
    public static Color From(IReadOnlyList<Color> palette, bool darkTheme)
    {
        if (palette.Count == 0) return Default(darkTheme);

        // Najbardziej nasycona z plam, a nie pierwsza lepsza: pierwsza bywa przygaszonym tłem
        // okładki, a akcent ma pokazywać to, co w niej najbardziej rzuca się w oczy.
        var best = palette[0].ToHsl();
        foreach (var colour in palette)
        {
            var hsl = colour.ToHsl();
            if (hsl.S > best.S) best = hsl;
        }

        if (best.S < MinimumUsableSaturation) return Default(darkTheme);

        var saturation = Math.Clamp(best.S * 1.15, MinimumResultSaturation, 1.0);
        var lightness = darkTheme ? DarkThemeLightness : LightThemeLightness;

        return new HslColor(1.0, best.H, saturation, lightness).ToRgb();
    }
}
