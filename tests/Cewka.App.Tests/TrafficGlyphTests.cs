using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Models;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace Cewka.App.Tests;

/// <summary>Znaki na krążkach wariantu macOS stoją w środku krążków, a nie przesunięte zaokrąglaniem układu.</summary>
[Collection(UiCollection.Name)]
public sealed class TrafficGlyphTests
{
    private const int Width = 120;
    private const int Height = 40;

    /// <summary>Powiększenie rysunku: środek ciężkości znaku liczony z dokładnością do ułamka piksela.</summary>
    private const int Zoom = 8;

    private const double Dot = 12;

    [Fact]
    public void ZnakiStojaWSrodkuKrazkow() => Ui.Run(() =>
    {
        var buttons = new WindowButtons { Position = WindowControlsPosition.MacOs };
        var window = new Window { Content = buttons, Width = Width, Height = Height };
        window.Show();
        try
        {
            // Znaki widać pod kursorem; tu są pokazane od razu, bez przejścia.
            foreach (var glyph in buttons.GetVisualDescendants().OfType<Path>().Where(p => p.Classes.Contains("traffic-glyph")))
            {
                glyph.Transitions = null;
                glyph.Opacity = 1;
            }

            window.UpdateLayout();
            var (pixels, stride) = Render(buttons);

            var dots = buttons.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("dot")).ToArray();
            Assert.Equal(3, dots.Length);
            foreach (var dot in dots)
            {
                var corner = dot.TranslatePoint(default, buttons)!.Value;
                var (x, y) = GlyphCentre(pixels, stride, corner);
                var dx = x - (corner.X + Dot / 2);
                var dy = y - (corner.Y + Dot / 2);
                Assert.True(Math.Abs(dx) <= 0.1 && Math.Abs(dy) <= 0.1,
                    $"{dot.Parent?.Classes.LastOrDefault()}: znak przesunięty o {dx:F2}, {dy:F2} px");
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static (byte[] Pixels, int Stride) Render(Control control)
    {
        using var target = new RenderTargetBitmap(new PixelSize(Width * Zoom, Height * Zoom), new Vector(96 * Zoom, 96 * Zoom));
        target.Render(control);
        using var readable = new WriteableBitmap(target.PixelSize, target.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var buffer = readable.Lock();
        target.CopyPixels(buffer);
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return (bytes, buffer.RowBytes);
    }

    /// <summary>Środek ciężkości ciemnych pikseli znaku w obrębie krążka, w pikselach kontrolki.</summary>
    private static (double X, double Y) GlyphCentre(byte[] pixels, int stride, Point corner)
    {
        double sumX = 0, sumY = 0, total = 0;
        for (var y = (int)(corner.Y * Zoom); y < (corner.Y + Dot) * Zoom; y++)
        {
            for (var x = (int)(corner.X * Zoom); x < (corner.X + Dot) * Zoom; x++)
            {
                var i = y * stride + x * 4;
                if (pixels[i + 3] < 200) continue;

                // Znak jest ciemny na jasnym krążku; krawędź krążka (półprzezroczysta) jest pominięta.
                var weight = Math.Max(0, 110 - (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0);
                sumX += (x + 0.5) * weight;
                sumY += (y + 0.5) * weight;
                total += weight;
            }
        }

        Assert.True(total > 0, "na krążku nie ma znaku");
        return (sumX / total / Zoom, sumY / total / Zoom);
    }
}
