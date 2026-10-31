using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Tekst drugorzędny zachowuje kontrast 4,5:1 na każdym tle, na jakie pozwala paleta.</summary>
[Collection(UiCollection.Name)]
public sealed class ContrastTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TekstDrugorzednyMaKontrastNaGranicyJasnosciTla(bool dark)
    {
        Ui.Run(() =>
        {
            var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Color Token(string key) =>
                Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush
                    ? brush.Color
                    : throw new InvalidOperationException(key);

            // Najgorsza plama: dokładnie na granicy luminancji, w pełnym kryciu, pod przyciemnieniem.
            var limit = dark ? Contrast.DarkBackdropCeiling : Contrast.LightBackdropFloor;
            var grey = GreyWithLuminance(limit);
            var background = Contrast.Over(Token("ScrimBrush"), grey);
            var glass = Contrast.Over(Token("GlassBrush"), background);

            foreach (var surface in new[] { background, glass })
            {
                foreach (var key in new[] { "Text2Brush", "Text3Brush" })
                {
                    var text = Contrast.Over(Token(key), surface);
                    var ratio = Contrast.Ratio(text, surface);
                    Assert.True(ratio >= 4.5, $"{key} w motywie {(dark ? "ciemnym" : "jasnym")}: {ratio:F2}:1");
                }
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PaletaTlaTrzymaSieGranicyLuminancji(bool dark)
    {
        for (var hue = 0; hue < 360; hue += 15)
        {
            foreach (var lightness in new[] { 0.1, 0.5, 0.9 })
            {
                var colour = Contrast.LimitForBackdrop(HslColor.ToRgb(hue, 0.9, lightness), dark);
                var luminance = Contrast.RelativeLuminance(colour);

                if (dark) Assert.True(luminance <= Contrast.DarkBackdropCeiling, $"{hue}/{lightness}: {luminance:F3}");
                else Assert.True(luminance >= Contrast.LightBackdropFloor, $"{hue}/{lightness}: {luminance:F3}");
            }
        }
    }

    private static Color GreyWithLuminance(double luminance)
    {
        for (var v = 0; v <= 255; v++)
        {
            var grey = Color.FromRgb((byte)v, (byte)v, (byte)v);
            if (Contrast.RelativeLuminance(grey) >= luminance) return grey;
        }

        return Colors.White;
    }
}
