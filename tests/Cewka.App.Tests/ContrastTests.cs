using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;
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

            // Tekst przygaszony na podświetlonym wierszu kolejki przechodzi w Text2, więc tam
            // sprawdzany jest tylko Text2.
            var surfaces = new (string Name, Color Colour, string[] Texts)[]
            {
                ("tło", background, ["Text2Brush", "Text3Brush"]),
                ("szkło", glass, ["Text2Brush", "Text3Brush"]),
                ("komunikat na tle", Contrast.Over(Token("Glass2Brush"), background), ["Text2Brush"]),
                ("wiersz kolejki", Contrast.Over(Token("Glass2Brush"), glass), ["Text2Brush"]),
            };

            foreach (var (name, surface, texts) in surfaces)
            {
                foreach (var key in texts)
                {
                    var text = Contrast.Over(Token(key), surface);
                    var ratio = Contrast.Ratio(text, surface);
                    Assert.True(ratio >= 4.5, $"{key}, {name}, motyw {(dark ? "ciemny" : "jasny")}: {ratio:F2}:1");
                }
            }
        });
    }

    [Fact]
    public void TekstPrzygaszonyWZaznaczonymWierszuKolejkiJestJasniejszy()
    {
        Ui.Run(() =>
        {
            var faint = new TextBlock { Classes = { "faint" }, Text = "1" };
            var list = new ListBox { Classes = { "queue" }, ItemsSource = new[] { "a", "b" },
                                     ItemTemplate = new FuncDataTemplate<string>((_, _) => new TextBlock { Classes = { "faint" } }) };
            var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Children = { faint, list } } };
            window.Show();

            try
            {
                list.SelectedIndex = 0;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                var selected = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("faint"));
                var other = list.ContainerFromIndex(1)!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("faint"));

                Assert.True(window.TryFindResource("Text2Brush", window.ActualThemeVariant, out var text2));
                Assert.True(window.TryFindResource("Text3Brush", window.ActualThemeVariant, out var text3));
                Assert.Same(text2, selected.Foreground);
                Assert.Same(text3, other.Foreground);
                Assert.Same(text3, faint.Foreground);
            }
            finally { window.Close(); }
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
