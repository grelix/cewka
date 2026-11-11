using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Czas klatek sceny: obniżanie i podnoszenie rozdzielczości bufora oraz jedna pętla na okno.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneFrameBudgetTests
{
    private static void Feed(FrameBudget budget, int frames, double seconds)
    {
        for (var i = 0; i < frames; i++) budget.Observe(seconds);
    }

    [Fact]
    public void TrzydziesciSpoznionychKlatekWPieciuSekundachObnizaPoziom()
    {
        var budget = new FrameBudget();

        Feed(budget, FrameBudget.LateFramesToDegrade - 1, 0.06);
        Assert.Equal(0, budget.Level);

        Assert.True(budget.Observe(0.06));
        Assert.Equal(1, budget.Level);

        // Dalsze przeciążenie obniża kolejne poziomy, ale nie poniżej ostatniego.
        Feed(budget, FrameBudget.LateFramesToDegrade, 0.06);
        Assert.Equal(2, budget.Level);
        Feed(budget, FrameBudget.LateFramesToDegrade * 3, 0.06);
        Assert.Equal(FrameBudget.MaxLevel, budget.Level);
        Assert.True(budget.Scale < 1);
    }

    [Fact]
    public void RzadkieSpoznieniaNieObnizajaPoziomu()
    {
        var budget = new FrameBudget();

        // Jedna spóźniona klatka na sekundę przez dwie minuty: nigdy 30 w 5 s.
        for (var second = 0; second < 120; second++)
        {
            budget.Observe(0.08);
            Feed(budget, 59, 1.0 / 60);
        }

        Assert.Equal(0, budget.Level);
    }

    [Fact]
    public void PoziomWracaPoMinucieSpokojuKrokPoKroku()
    {
        var budget = new FrameBudget();
        Feed(budget, FrameBudget.LateFramesToDegrade * 2, 0.06);
        Assert.Equal(2, budget.Level);

        // Mniej niż minuta spokoju nic nie zmienia.
        Feed(budget, (int)(50 / 0.016), 0.016);
        Assert.Equal(2, budget.Level);

        Feed(budget, (int)(11 / 0.016), 0.016);
        Assert.Equal(1, budget.Level);

        Feed(budget, (int)(61 / 0.016), 0.016);
        Assert.Equal(0, budget.Level);
        Assert.Equal(1.0, budget.Scale);
    }

    [Fact]
    public void SpoznionaKlatkaWSpokojuZeruujeOdliczanieDoPowrotu()
    {
        var budget = new FrameBudget();
        Feed(budget, FrameBudget.LateFramesToDegrade, 0.06);
        Assert.Equal(1, budget.Level);

        Feed(budget, (int)(50 / 0.016), 0.016);
        budget.Observe(0.07);
        Feed(budget, (int)(50 / 0.016), 0.016);

        Assert.Equal(1, budget.Level);
    }

    [Fact]
    public void PrzerwaWOknieINiepoprawneCzasyNieLiczaSieJakoPrzeciazenie()
    {
        var budget = new FrameBudget();

        // Okno zminimalizowane na sekundy, potem wznowione: długie przerwy to nie obciążenie.
        Feed(budget, 100, 2.0);
        Feed(budget, 100, double.NaN);
        Feed(budget, 100, -1);
        Feed(budget, 100, 0);

        Assert.Equal(0, budget.Level);
    }

    [Fact]
    public void RozmiarBuforaNieJestWiekszyOd960Na540IMalejeZPoziomem()
    {
        Assert.Equal(new PixelSize(960, 540), LiveBackdrop.BufferSize(1920, 1080, 1));
        Assert.Equal(new PixelSize(640, 360), LiveBackdrop.BufferSize(1920, 1080, 2));
        Assert.Equal(new PixelSize(480, 270), LiveBackdrop.BufferSize(1920, 1080, 3));
        Assert.Equal(new PixelSize(960, 540), LiveBackdrop.BufferSize(3840, 2160, 1));

        // Małe okno ma bufor nie większy od siebie.
        Assert.Equal(new PixelSize(400, 300), LiveBackdrop.BufferSize(400, 300, 1));
        Assert.Equal(new PixelSize(200, 150), LiveBackdrop.BufferSize(400, 300, 3));
    }

    [Fact]
    public void TloRysujeWprostPotemWBuforzeIWracaDoRysowaniaWprost() => Ui.Run(() =>
    {
        var backdrop = new LiveBackdrop
        {
            BaseBrush = Brushes.Black,
            Palette = [Colors.SteelBlue, Colors.Goldenrod, Colors.SeaGreen, Colors.IndianRed],
        };

        ControlPixels.Render(backdrop, 400, 300);
        Assert.Null(backdrop.BufferPixels);

        Feed(backdrop.Budget, FrameBudget.LateFramesToDegrade, 0.06);
        var pixels = ControlPixels.Render(backdrop, 400, 300);
        Assert.Equal(new PixelSize(400, 300), backdrop.BufferPixels);
        Assert.True(pixels[200, 150].A == 255, "bufor nie jest nieprzezroczysty");

        Feed(backdrop.Budget, FrameBudget.LateFramesToDegrade * 2, 0.06);
        ControlPixels.Render(backdrop, 400, 300);
        Assert.Equal(new PixelSize(200, 150), backdrop.BufferPixels);

        // Po trzech minutach spokoju poziom wraca do zera i tło znów rysuje wprost.
        Feed(backdrop.Budget, (int)(190 / 0.016), 0.016);
        Assert.Equal(0, backdrop.Budget.Level);
        var direct = ControlPixels.Render(backdrop, 400, 300);
        Assert.True(direct[200, 150].A == 255);
    });

    [Fact]
    public void PetleOknaDzielaJednoZamowienieKlatki() => Ui.Run(() =>
    {
        var views = new SceneAudioView[3];
        var panel = new StackPanel();
        for (var i = 0; i < views.Length; i++)
        {
            views[i] = new SceneAudioView { Source = new FakeSceneAudio(), Width = 200, Height = 100 };
            views[i].Mode = SceneAudioMode.Spectrum;
            panel.Children.Add(views[i]);
        }

        var window = new Window { Width = 400, Height = 400, Content = panel };
        var owners = 0;

        void Count(Visual owner)
        {
            if (views.Contains(owner)) owners++;
        }

        RenderLoop.FrameRequested += Count;
        try
        {
            window.Show();
            var hub = RenderLoop.FrameHub.For(window);
            var hubsBefore = hub.Requests;
            foreach (var view in views) view.SetActive(true);

            for (var i = 0; i < 12; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            var hubs = hub.Requests - hubsBefore;
            Assert.True(hubs > 0, "okno nie zamówiło klatki");
            Assert.True(owners >= 2 * hubs, $"{owners} zamówień pętli na {hubs} zamówień okna");

            foreach (var view in views) view.SetActive(false);
        }
        finally
        {
            RenderLoop.FrameRequested -= Count;
            window.Close();
        }
    });
}
