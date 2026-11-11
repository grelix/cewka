using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Wielkość tekstu sceny: mnożnik pisma i brak nachodzenia na wizualizację w każdym układzie.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneTextSizeTests
{
    private const string LongTitle =
        "Bardzo długi tytuł utworu, który nie mieści się w jednym wierszu i musi zostać zawinięty, a potem uciążliwie ucięty wielokropkiem na końcu drugiego wiersza";

    private const string LongArtist = "Wykonawca o wyjątkowo długiej nazwie wraz z całym zespołem towarzyszącym i gośćmi specjalnymi";

    private const string LongAlbum = "Album o równie długiej nazwie, który także nie może wyjść poza swoją strefę";

    private static readonly Rect Canvas = new(0, 0, 1920, 1080);

    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public SceneView Scene => Window.Scene;

        public MainViewModel Player => Harness.Player;

        public void Dispose() => Window.Close();
    }

    private static void Render()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Rig Open(SceneVisual visual, SceneTextSize size)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.Effects = EffectsMode.Full;
            s.SceneVisual = visual;
            s.SceneTextSize = size;
            s.SceneOledCare = false;
        });
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        var rig = new Rig { Window = window, Harness = h };
        window.Show();

        h.OpenPaths(Enumerable.Range(0, 3).Select(i => h.TaggedTrack("t" + i, LongTitle + i, LongArtist, LongAlbum)));
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero && !string.IsNullOrEmpty(h.Player.CurrentFilePath)));
        h.Player.ReadLargeCover = (_, _) => MainViewModel.DecodeCover(CoverTests.Png(8, 8, Colors.Red))!.Display;

        window.EnterScene();
        Render();
        return rig;
    }

    /// <summary>Prostokąt kontrolki w jednostkach kadru 1920 × 1080.</summary>
    private static Rect InPlane(SceneView scene, Control control) =>
        new(control.TranslatePoint(default, scene.Plane) ?? default, control.Bounds.Size);

    private static void ShowNextCard(Rig rig)
    {
        SceneTests.SeekTo(rig.Harness, 50);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.NextUpCoverTask.IsCompleted));
        Render();
        Assert.True(rig.Scene.NextCard.IsVisible);
    }

    // ---------- Mnożnik ----------

    [Fact]
    public void MnoznikiToJedenJedenPietnascieIJedenTrzy()
    {
        Assert.Equal(1.0, SceneText.Factor(SceneTextSize.Normal));
        Assert.Equal(1.15, SceneText.Factor(SceneTextSize.Large));
        Assert.Equal(1.3, SceneText.Factor(SceneTextSize.ExtraLarge));
    }

    [Fact]
    public void WielkoscDzialaOdRazuWOtwartejScenie() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc, SceneTextSize.Normal);
        var scene = rig.Scene;
        var title = scene.SceneTitle.FontSize;
        var line = scene.SceneTitle.LineHeight;
        var artist = scene.SceneArtist.FontSize;
        var card = scene.NextCardTitle.FontSize;
        var night = scene.NightTitle.FontSize;
        var height = InPlane(scene, scene.TitleBlock).Height;

        rig.Player.SceneTextSize = SceneTextSize.ExtraLarge;
        Render();

        Assert.Equal(title * 1.3, scene.SceneTitle.FontSize, 6);
        Assert.Equal(line * 1.3, scene.SceneTitle.LineHeight, 6);
        Assert.Equal(artist * 1.3, scene.SceneArtist.FontSize, 6);
        Assert.Equal(card * 1.3, scene.NextCardTitle.FontSize, 6);
        Assert.Equal(night * 1.3, scene.NightTitle.FontSize, 6);
        Assert.True(InPlane(scene, scene.TitleBlock).Height > height * 1.2);

        rig.Player.SceneTextSize = SceneTextSize.Large;
        Render();
        Assert.Equal(title * 1.15, scene.SceneTitle.FontSize, 6);

        rig.Player.SceneTextSize = SceneTextSize.Normal;
        Render();
        Assert.Equal(title, scene.SceneTitle.FontSize, 6);
        Assert.Equal(height, InPlane(scene, scene.TitleBlock).Height, 3);
    });

    /// <summary>
    /// Nad wizualizacją w całą szerokość duża i największa wielkość tekstu mieszczą jeden wiersz tytułu;
    /// przy piśmie Noto Sans dwa wiersze w dużej wielkości wchodziły na wizualizację o 3 px.
    /// </summary>
    [Fact]
    public void WUkladzieSzerokimDuzaINajwyzszaWielkoscSkracajaTytulDoJednegoWiersza() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Spectrum, SceneTextSize.Large);
        Assert.Equal(1, rig.Scene.SceneTitle.MaxLines);

        rig.Player.SceneTextSize = SceneTextSize.ExtraLarge;
        Render();
        Assert.Equal(1, rig.Scene.SceneTitle.MaxLines);

        rig.Player.SceneTextSize = SceneTextSize.Normal;
        Render();
        Assert.Equal(2, rig.Scene.SceneTitle.MaxLines);
    });

    // ---------- Brak nachodzenia ----------

    [Theory]
    [InlineData(SceneVisual.Spectrum, SceneTextSize.Normal)]
    [InlineData(SceneVisual.Spectrum, SceneTextSize.Large)]
    [InlineData(SceneVisual.Spectrum, SceneTextSize.ExtraLarge)]
    [InlineData(SceneVisual.Waveform, SceneTextSize.ExtraLarge)]
    [InlineData(SceneVisual.Horizon, SceneTextSize.ExtraLarge)]
    public void WUkladachSzerokichTekstJestNadWizualizacja(SceneVisual visual, SceneTextSize size) => Ui.Run(() =>
    {
        using var rig = Open(visual, size);
        var scene = rig.Scene;
        var column = InPlane(scene, scene.InfoColumn);
        var stage = InPlane(scene, scene.AudioView);

        Assert.True(scene.AudioView.IsVisible);
        Assert.True(column.Top >= 112 - 0.01, $"kolumna zaczyna się na {column.Top}");
        Assert.True(column.Bottom <= stage.Top, $"kolumna kończy się na {column.Bottom}, wizualizacja zaczyna na {stage.Top}");
        Assert.True(Canvas.Contains(column.TopLeft) && column.Right <= Canvas.Right && column.Bottom <= Canvas.Bottom);
        Assert.False(column.Intersects(stage));
    });

    [Theory]
    [InlineData(SceneVisual.Disc, SceneTextSize.ExtraLarge)]
    [InlineData(SceneVisual.Cover, SceneTextSize.ExtraLarge)]
    [InlineData(SceneVisual.Disc, SceneTextSize.Large)]
    public void WUkladachStrefowychTekstNieNachodziNaPlyteAniOkladke(SceneVisual visual, SceneTextSize size) => Ui.Run(() =>
    {
        using var rig = Open(visual, size);
        var scene = rig.Scene;

        // Przed zmianą strony i po niej.
        for (var track = 0; track < 2; track++)
        {
            var column = InPlane(scene, scene.InfoColumn);
            var obstacle = visual switch
            {
                SceneVisual.Disc => InPlane(scene, scene.DiscHost),
                SceneVisual.Cover => InPlane(scene, scene.CoverArtHost),
                _ => InPlane(scene, scene.AudioView),
            };

            Assert.True(Canvas.Contains(column.TopLeft), $"kolumna {column}");
            Assert.True(column.Bottom <= Canvas.Bottom && column.Right <= Canvas.Right, $"kolumna {column}");
            Assert.False(column.Intersects(obstacle), $"kolumna {column} nachodzi na {obstacle}");

            rig.Player.Next();
            Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex == track + 1));
            Render();
        }
    });

    [Theory]
    [InlineData(SceneVisual.Disc)]
    [InlineData(SceneVisual.Cover)]
    public void KartaNastepnyBardzoDuzaNieNachodziNaKolumneAniPlyte(SceneVisual visual) => Ui.Run(() =>
    {
        using var rig = Open(visual, SceneTextSize.ExtraLarge);
        var scene = rig.Scene;
        ShowNextCard(rig);

        var card = InPlane(scene, scene.NextCard);
        var column = InPlane(scene, scene.InfoColumn);
        var obstacle = visual switch
        {
            SceneVisual.Disc => InPlane(scene, scene.DiscHost),
            SceneVisual.Cover => InPlane(scene, scene.CoverArtHost),
            _ => InPlane(scene, scene.AudioView),
        };

        Assert.True(Canvas.Contains(card.TopLeft) && card.Right <= Canvas.Right && card.Bottom <= Canvas.Bottom, $"karta {card}");
        Assert.False(card.Intersects(column), $"karta {card} nachodzi na kolumnę {column}");
        Assert.False(card.Intersects(obstacle), $"karta {card} nachodzi na {obstacle}");
    });

    /// <summary>
    /// Wyśrodkowana kolumna w największej wielkości tekstu sięga dołem do karty „Następny”, więc karta
    /// staje u góry; zmiana wielkości przy otwartej scenie przestawia ją od razu.
    /// </summary>
    [Fact]
    public void KartaNastepnyPrzechodziDoGoryPrzyBardzoDuzymTekscieNaSrodku() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc, SceneTextSize.Normal);
        var scene = rig.Scene;
        ShowNextCard(rig);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, scene.NextCard.VerticalAlignment);

        rig.Player.SceneTextSize = SceneTextSize.ExtraLarge;
        Render();

        Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, scene.NextCard.VerticalAlignment);
        Assert.False(InPlane(scene, scene.NextCard).Intersects(InPlane(scene, scene.InfoColumn)));
    });

    /// <summary>
    /// Napisy sceny ze stałą wysokością wiersza nie ucinają dolnych wydłużeń liter (g, j, p, y, ą, ę).
    /// Wiersz układa się jak w CSS: naturalna wysokość pisma (wzniesienie + zejście) staje na środku
    /// wiersza, a nadwyżka albo niedobór dzieli się po równo nad i pod nią. Za ciasny wiersz przesuwa
    /// linię bazową tak nisko, że dół liter wychodzi poza pole napisu i znika. Noto Sans ma głębsze
    /// zejście niż Cantarell, więc tytuł z wierszem 1,125 wysokości pisma tracił dół liter.
    /// </summary>
    [Theory]
    [InlineData(SceneTextSize.Normal)]
    [InlineData(SceneTextSize.ExtraLarge)]
    public void NapisyScenyNieUcinajaDolnychWydluzenLiter(SceneTextSize size) => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc, size);
        var fonts = Path.Combine(AppContext.BaseDirectory, "Fonts");
        var tight = new List<string>();

        foreach (var text in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rig.Scene).OfType<TextBlock>()
                     .Where(t => !double.IsNaN(t.LineHeight) && t.LineHeight > 0))
        {
            var bold = text.FontWeight >= FontWeight.SemiBold;
            var file = Path.Combine(fonts, bold ? "NotoSans-Bold.ttf" : "NotoSans-Regular.ttf");
            if (!File.Exists(file)) file = Path.Combine(fonts, "NotoSans-Regular.ttf");

            using var typeface = SkiaSharp.SKTypeface.FromFile(file);
            using var font = new SkiaSharp.SKFont(typeface, (float)text.FontSize);
            var metrics = font.Metrics;
            font.MeasureText("gjpyąęŻ", out var ink);

            var natural = metrics.Descent - metrics.Ascent;
            var baseline = -metrics.Ascent + (text.LineHeight - natural) / 2;
            var bottom = baseline + ink.Bottom;

            // Pole napisu kończy się na dole wiersza powiększonym o dopełnienie od dołu.
            if (bottom > text.LineHeight + text.Padding.Bottom + 0.5)
                tight.Add($"{text.Name ?? text.Text}: {text.FontSize:0.#}/{text.LineHeight:0.#}, dół liter {bottom:0.#}");
        }

        Assert.True(tight.Count == 0, string.Join("; ", tight));
    });

    [Fact]
    public void BlokNocyBardzoDuzyMiesciSieWKadrzeWKazdymMiejscuIOmijaSterowanie() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night, SceneTextSize.ExtraLarge);
        var scene = rig.Scene;

        for (var spot = 0; spot < 4; spot++)
        {
            var block = InPlane(scene, scene.NightBlock);
            Assert.True(Canvas.Contains(block.TopLeft) && block.Right <= Canvas.Right && block.Bottom <= Canvas.Bottom, $"blok {block}");
            Assert.False(block.Intersects(InPlane(scene, scene.ProgressBlock)), $"blok {block} nachodzi na pasek postępu");
            Assert.False(block.Intersects(InPlane(scene, scene.TransportRow)), $"blok {block} nachodzi na rząd przycisków");

            scene.FinishNightMove();
            Render();
        }
    });
}
