using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cewka.App.Controls;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Wygląd płyty na pikselach. Płyta 640 ma środek w (320, 320); piksel (320 + k, 320) leży
/// w odległości od k do k + 1 od środka, więc linia o grubości 1 na promieniu r zajmuje po
/// połowie piksele k = r − 1 i r.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class VinylDiscTests
{
    private static readonly Color Grey = Color.FromRgb(0x80, 0x80, 0x80);

    private static SolidColorBrush Solid(Color colour) => new(colour);

    /// <summary>Płyta jednej barwy, bez rowków i otworu: tło dla jednej sprawdzanej warstwy.</summary>
    private static VinylDisc Plain(Color colour) => new()
    {
        CoreBrush = Solid(colour),
        BodyBrush = Solid(colour),
        DesignDiameter = 640,
    };

    private static Color Along(ControlPixels pixels, int k) => pixels[pixels.Width / 2 + k, pixels.Height / 2];

    [Fact]
    public void DomyslnyWygladToPlytaOkna() => Ui.Run(() =>
    {
        var disc = new VinylDisc();

        // Stałe płyty okna: środek do 0,26 promienia, korpus od 0,264, rowki co 4 piksele
        // od 2 pikseli od krawędzi do naklejki 0,40, otwór 5,5 na płycie 300.
        Assert.Equal(0.26, disc.CoreFraction);
        Assert.Equal(0.26 + 0.004, disc.BodyFraction);
        Assert.Null(disc.RimBrush);
        Assert.Null(disc.OutlineBrush);
        Assert.Equal(0, disc.DesignDiameter);
        Assert.Equal(4, disc.GroovePitch);
        Assert.Equal(2, disc.GrooveInset);
        Assert.Equal(0.40, disc.GrooveInnerFraction);
        Assert.Null(disc.SeparatorBrush);
        Assert.Null(disc.SeparatorFractions);
        Assert.Equal(0.40, disc.LabelFraction);
        Assert.Null(disc.LabelOutlineBrush);
        Assert.Equal(5.5 / 150, disc.HoleFraction);
        Assert.Null(disc.HoleOutlineBrush);
    });

    [Fact]
    public void TalerzPrzechodziZeSrodkaWKorpusIBarweBrzegu() => Ui.Run(() =>
    {
        var disc = new VinylDisc
        {
            CoreBrush = Solid(Colors.Red),
            BodyBrush = Solid(Colors.Lime),
            RimBrush = Solid(Colors.Blue),
            CoreFraction = 132 / 320.0,
            BodyFraction = 134 / 320.0,
        };

        var pixels = ControlPixels.Render(disc, 640, 640);

        Assert.Equal(Colors.Red, Along(pixels, 100));
        Assert.Equal(Colors.Red, Along(pixels, 130));

        var body = Along(pixels, 140);
        Assert.True(body is { G: > 220, B: < 30, R: < 5 }, $"{body}");

        var rim = Along(pixels, 316);
        Assert.True(rim is { B: > 220, G: < 30 }, $"{rim}");

        // Bez barwy brzegu korpus ma jedną barwę aż do krawędzi.
        disc.RimBrush = null;
        Assert.Equal(Colors.Lime, Along(ControlPixels.Render(disc, 640, 640), 316));
    });

    [Fact]
    public void PedzlePrzebarwioneWMiejscuZmieniajaPlytePrzyKolejnymRysowaniu() => Ui.Run(() =>
    {
        var core = Solid(Colors.Red);
        var body = Solid(Colors.Red);
        var separator = Solid(Colors.White);
        var disc = new VinylDisc
        {
            CoreBrush = core,
            BodyBrush = body,
            SeparatorBrush = separator,
            SeparatorFractions = [200.5 / 320],
            DesignDiameter = 640,
        };

        var before = ControlPixels.Render(disc, 640, 640);
        Assert.Equal(Colors.Red, Along(before, 50));
        Assert.Equal(Colors.Red, Along(before, 150));
        Assert.True(Along(before, 200).G > 240, $"{Along(before, 200)}");

        // Pędzel zmienia barwę bez zmiany właściwości płyty, np. przy przenikaniu barw.
        core.Color = Colors.Blue;
        body.Color = Colors.Lime;
        separator.Color = Colors.Black;

        var after = ControlPixels.Render(disc, 640, 640);
        Assert.Equal(Colors.Blue, Along(after, 50));
        Assert.Equal(Colors.Lime, Along(after, 150));
        Assert.True(Along(after, 200).G < 15, $"{Along(after, 200)}");
    });

    [Fact]
    public void ObrysTalerzaLezyTuzZaKrawedziaISkalujeSieZPlyta() => Ui.Run(() =>
    {
        // Płyta 600 na czarnym tle 640, żeby obrys za krawędzią zmieścił się w obrazie.
        var disc = Plain(Grey);
        disc.DesignDiameter = 600;
        disc.OutlineBrush = Solid(Colors.White);
        var host = new Avalonia.Controls.Border { Background = Brushes.Black, Padding = new Thickness(20), Child = disc };

        var full = ControlPixels.Render(host, 640, 640);
        Assert.Equal(Grey, Along(full, 298));
        Assert.True(Along(full, 300).R > 240, $"{Along(full, 300)}");
        Assert.Equal(Colors.Black, Along(full, 302));

        // Na płycie o połowę mniejszej linia ma pół piksela.
        var half = ControlPixels.Render(host, 340, 340);
        Assert.InRange(Along(half, 150).R, 80, 180);

        // Bez średnicy wzorcowej linia ma zawsze piksel, jak w oknie.
        disc.DesignDiameter = 0;
        Assert.True(Along(ControlPixels.Render(host, 340, 340), 150).R > 240);
    });

    /// <summary>Białe rowki na czarnej płycie wzorcowej 640: co 3 jednostki, 6 od krawędzi, do 142.</summary>
    private static VinylDisc Grooved()
    {
        var disc = Plain(Colors.Black);
        disc.GrooveBrush = Solid(Colors.White);
        disc.GroovePitch = 3;
        disc.GrooveInset = 6;
        disc.GrooveInnerFraction = 142 / 320.0;
        return disc;
    }

    /// <summary>Jasne odcinki wzdłuż promienia: ich liczba oraz pierwszy i ostatni jasny piksel.</summary>
    private static (int Count, int? First, int? Last) Grooves(ControlPixels pixels)
    {
        var count = 0;
        int? first = null, last = null;
        var lit = false;
        for (var k = 0; k < pixels.Width / 2; k++)
        {
            var now = Along(pixels, k).R > 40;
            if (now)
            {
                first ??= k;
                last = k;
                if (!lit) count++;
            }

            lit = now;
        }

        return (count, first, last);
    }

    [Fact]
    public void RowkiCoTrzyJednostkiTylkoWPierscieniu142Do314() => Ui.Run(() =>
    {
        var (count, first, last) = Grooves(ControlPixels.Render(Grooved(), 640, 640));

        // Promienie 314, 311, …, 143.
        Assert.Equal(58, count);
        Assert.Equal(142, first);
        Assert.Equal(314, last);
    });

    [Fact]
    public void OdstepIPolozenieRowkowRosnaZPlyta() => Ui.Run(() =>
    {
        // Płyta dwa razy większa od wzorcowej: rowki co 6 pikseli od 12 pikseli od krawędzi,
        // linią 2, więc rowek na promieniu r zajmuje piksele r − 1 i r.
        var pixels = ControlPixels.Render(Grooved(), 1280, 1280);
        var (count, first, last) = Grooves(pixels);

        // Promienie 628, 622, …, 286; granica rowków na 284.
        Assert.Equal(58, count);
        Assert.Equal(285, first);
        Assert.Equal(628, last);
        Assert.True(Along(pixels, 625).R < 10, $"{Along(pixels, 625)}");
    });

    [Fact]
    public void RozdzielaczeSciezekPrzyciemniajaPlyteNaSwoichPromieniach() => Ui.Run(() =>
    {
        var disc = Plain(Grey);
        disc.SeparatorBrush = Solid(Colors.Black);
        disc.SeparatorFractions = [197 / 320.0, 255 / 320.0];

        var pixels = ControlPixels.Render(disc, 640, 640);

        foreach (var radius in new[] { 197, 255 })
        {
            Assert.True(Along(pixels, radius - 1).R < 100, $"{radius}: {Along(pixels, radius - 1)}");
            Assert.True(Along(pixels, radius).R < 100, $"{radius}: {Along(pixels, radius)}");
            Assert.Equal(Grey, Along(pixels, radius - 4));
            Assert.Equal(Grey, Along(pixels, radius + 3));
        }

        // Rozdzielacze należą do warstwy rowków.
        disc.ShowGrooves = false;
        Assert.Equal(Grey, Along(ControlPixels.Render(disc, 640, 640), 197));
    });

    [Fact]
    public void NaklejkaIOtworMajaWlasneWymiaryIObrysy() => Ui.Run(() =>
    {
        using var cover = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        using (var context = cover.CreateDrawingContext())
            context.FillRectangle(Brushes.White, new Rect(0, 0, 64, 64));

        var disc = Plain(Grey);
        disc.Cover = cover;
        disc.LabelFraction = 130 / 320.0;
        disc.LabelOutlineBrush = Solid(Colors.Black);
        disc.HoleBrush = Solid(Color.Parse("#1C1C1E"));
        disc.HoleFraction = 7 / 320.0;
        disc.HoleOutlineBrush = Solid(Colors.Red);

        var pixels = ControlPixels.Render(disc, 640, 640);

        // Naklejka o średnicy 260 z obrysem tuż za jej brzegiem, jak box-shadow 0 0 0 1px we wzorze.
        Assert.Equal(Colors.White, Along(pixels, 128));
        Assert.True(Along(pixels, 130).R < 20, $"{Along(pixels, 130)}");
        Assert.Equal(Grey, Along(pixels, 132));

        // Otwór o średnicy 14 z obrysem tuż za brzegiem.
        Assert.Equal(Color.Parse("#1C1C1E"), Along(pixels, 0));
        Assert.Equal(Color.Parse("#1C1C1E"), Along(pixels, 5));
        Assert.True(Along(pixels, 7) is { R: > 200, G: < 30 }, $"{Along(pixels, 7)}");
        Assert.Equal(Colors.White, Along(pixels, 9));

        // Na płycie 320 otwór ma średnicę 7.
        var half = ControlPixels.Render(disc, 320, 320);
        Assert.Equal(Color.Parse("#1C1C1E"), Along(half, 1));
        Assert.Equal(Colors.White, Along(half, 5));
    });
}
