using Avalonia.Media;

namespace Cewka.App.Services;

/// <summary>
/// Luminancja względna i kontrast według WCAG 2.x oraz granice jasności tła, przy których
/// tekst drugorzędny zachowuje kontrast 4,5:1.
/// </summary>
public static class Contrast
{
    /// <summary>
    /// Najjaśniejsza plama tła w motywie ciemnym i najciemniejsza w jasnym, jako luminancja
    /// względna barwy plamy przed przyciemnieniem. Kolor tekstu <c>Text3Brush</c> dobrany jest
    /// do tych granic, a test pilnuje, że oba zostają w zgodzie.
    /// </summary>
    public const double DarkBackdropCeiling = 0.14;

    public const double LightBackdropFloor = 0.40;

    public static double RelativeLuminance(Color colour) =>
        0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);

    public static double Ratio(Color first, Color second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Barwa półprzezroczysta nałożona na nieprzezroczystą, tak jak składa je renderer.</summary>
    public static Color Over(Color top, Color bottom)
    {
        var alpha = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)),
            (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
            (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));
    }

    /// <summary>
    /// Przesuwa jasność barwy (przy tym samym odcieniu i nasyceniu) tak, żeby jej luminancja
    /// nie przekroczyła granicy motywu: w ciemnym w dół, w jasnym w górę.
    /// </summary>
    public static Color LimitForBackdrop(Color colour, bool darkTheme)
    {
        var luminance = RelativeLuminance(colour);
        if (darkTheme ? luminance <= DarkBackdropCeiling : luminance >= LightBackdropFloor) return colour;

        var hsl = colour.ToHsl();
        double low = darkTheme ? 0 : hsl.L, high = darkTheme ? hsl.L : 1;

        for (var i = 0; i < 24; i++)
        {
            var middle = (low + high) / 2;
            var candidate = RelativeLuminance(HslColor.ToRgb(hsl.H, hsl.S, middle));
            var allowed = darkTheme ? candidate <= DarkBackdropCeiling : candidate >= LightBackdropFloor;

            if (darkTheme == allowed) low = middle;
            else high = middle;
        }

        return HslColor.ToRgb(hsl.H, hsl.S, darkTheme ? low : high);
    }

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
