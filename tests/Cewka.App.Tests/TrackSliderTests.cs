using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cewka.App.Controls;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Wygląd uchwytu suwaka na pikselach i tekst wartości dla czytnika ekranu. Suwak 200 × 30
/// z wartością 0,5: uchwyt o środku (100, 15) i promieniu 10, tor od y = 13 do 17.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TrackSliderTests
{
    private static readonly Color Fill = Color.FromRgb(0xF5, 0xF5, 0xF7);
    private static readonly Color Track = Color.FromRgb(0x40, 0x40, 0x48);

    private static TrackSlider Slider() => new()
    {
        Value = 0.5,
        TrackThickness = 4,
        ThumbDiameter = 20,
        TrackBrush = new SolidColorBrush(Track),
        FillBrush = new SolidColorBrush(Fill),
    };

    private static ControlPixels Render(TrackSlider slider) => ControlPixels.Render(slider, 200, 30);

    [Fact]
    public void UchwytDomyslnieKryjacyWBarwieWypelnieniaBezObrysuICienia() => Ui.Run(() =>
    {
        var slider = Slider();
        Assert.Equal(1, slider.ThumbOpacity);
        Assert.Null(slider.ThumbBrush);
        Assert.Null(slider.ThumbOutlineBrush);
        Assert.Null(slider.ThumbShadowBrush);
        Assert.Null(slider.ValueText);

        var pixels = Render(slider);

        Assert.Equal(Fill, pixels[100, 7]);
        Assert.Equal(Fill, pixels[50, 15]);
        Assert.Equal(Track, pixels[150, 15]);
        Assert.True(pixels[100, 26].A < 8, $"{pixels[100, 26]}");
    });

    [Fact]
    public void ZgaszonyUchwytZnikaATorIWypelnienieZostaja() => Ui.Run(() =>
    {
        var slider = Slider();
        slider.ThumbOpacity = 0;

        var hidden = Render(slider);
        Assert.Equal(0, hidden[100, 7].A);
        Assert.Equal(Fill, hidden[50, 15]);
        Assert.Equal(Track, hidden[150, 15]);

        // Krycie pośrednie, np. w trakcie przenikania, dotyczy tylko uchwytu.
        slider.ThumbOpacity = 0.5;
        var half = Render(slider);
        Assert.InRange(half[100, 7].A, 120, 135);
        Assert.InRange(half[100, 7].R, Fill.R - 2, Fill.R + 2);
        Assert.Equal(Fill, half[50, 15]);
        Assert.Equal(Track, half[150, 15]);
    });

    [Fact]
    public void KrycieUchwytuPrzenikaPrzejsciem() => Ui.Run(() =>
    {
        var slider = Slider();
        slider.Transitions =
        [
            new DoubleTransition { Property = TrackSlider.ThumbOpacityProperty, Duration = TimeSpan.FromSeconds(1) },
        ];
        var window = new Window { Width = 300, Height = 100, Content = slider };
        window.Show();

        try
        {
            slider.ThumbOpacity = 0;

            // Przejście przejmuje zmianę: zanim zegar animacji ruszy, uchwyt ma jeszcze krycie
            // początkowe, a docelowe czeka jako wartość bazowa. Nic tu nie zależy od upływu czasu.
            Assert.Equal(1, slider.ThumbOpacity);
            Assert.Equal(0, slider.GetBaseValue(TrackSlider.ThumbOpacityProperty).Value);

            // Bez przejść zostaje od razu wartość docelowa.
            slider.Transitions = null;
            Assert.Equal(0, slider.ThumbOpacity);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UchwytMaWlasnaBarwe() => Ui.Run(() =>
    {
        var slider = Slider();
        slider.ThumbBrush = Brushes.Red;

        var pixels = Render(slider);
        Assert.Equal(Colors.Red, pixels[100, 7]);
        Assert.Equal(Fill, pixels[50, 15]);

        // Uchwyt we własnej barwie rysuje się także bez wypełnienia.
        slider.FillBrush = null;
        pixels = Render(slider);
        Assert.Equal(Colors.Red, pixels[100, 7]);
        Assert.Equal(Track, pixels[50, 15]);
    });

    [Fact]
    public void ObrysUchwytuLezyTuzZaJegoKrawedzia() => Ui.Run(() =>
    {
        var slider = new TrackSlider { Value = 0.5, ThumbDiameter = 20, FillBrush = Brushes.Black };

        // Górna krawędź uchwytu na y = 5: piksel od 5 do 6 należy do uchwytu, piksel od 4 do 5 nie.
        var plain = Render(slider);
        Assert.True(plain[100, 5].A > 220, $"{plain[100, 5]}");
        Assert.Equal(0, plain[100, 5].G);
        Assert.True(plain[100, 4].A < 8, $"{plain[100, 4]}");

        // Obrys 0,5 jak box-shadow 0 0 0 0.5px: zajmuje połowę piksela nad krawędzią,
        // a uchwyt zachowuje swoją średnicę. Wygładzanie cienkiej linii sięga najwyżej skraju
        // piksela pod krawędzią.
        slider.ThumbOutlineBrush = Brushes.Lime;
        var outlined = Render(slider);

        Assert.True(outlined[100, 4] is { G: > 200, R: < 30, A: > 50 and < 170 }, $"{outlined[100, 4]}");
        Assert.True(outlined[100, 5] is { A: > 220, G: < 80 }, $"{outlined[100, 5]}");
        Assert.Equal(Colors.Black, outlined[100, 9]);
        Assert.True(outlined[100, 3].A < 8, $"{outlined[100, 3]}");
    });

    [Fact]
    public void CienUchwytuPadaPonizejANieNadNim() => Ui.Run(() =>
    {
        var slider = new TrackSlider { Value = 0.5, ThumbDiameter = 20, FillBrush = Brushes.White };

        var plain = Render(slider);
        Assert.True(plain[100, 25].A < 8, $"{plain[100, 25]}");

        slider.ThumbShadowBrush = Brushes.Black;
        var shadowed = Render(slider);

        // Cień przesunięty o 1 w dół i rozmyty o 1,5: tuż pod uchwytem jest, nad nim go nie ma.
        Assert.True(shadowed[100, 25].A > 100, $"{shadowed[100, 25]}");
        Assert.True(shadowed[100, 25].R < 40, $"{shadowed[100, 25]}");
        Assert.True(shadowed[100, 28].A < 8, $"{shadowed[100, 28]}");
        Assert.True(shadowed[100, 4].A < 20, $"{shadowed[100, 4]}");
        Assert.Equal(Colors.White, shadowed[100, 15]);

        // Zgaszony uchwyt gasi też swój cień.
        slider.ThumbOpacity = 0;
        Assert.Equal(0, Render(slider)[100, 25].A);
    });

    [Fact]
    public void CienPrzebarwionyWMiejscuZmieniaBarwePrzyKolejnymRysowaniu() => Ui.Run(() =>
    {
        var shadow = new SolidColorBrush(Colors.Black);
        var slider = new TrackSlider { Value = 0.5, ThumbDiameter = 20, FillBrush = Brushes.White, ThumbShadowBrush = shadow };

        Assert.True(Render(slider)[100, 25] is { A: > 100, R: < 40 }, "czarny cień");

        // Pędzel zmienia barwę bez zmiany właściwości suwaka.
        shadow.Color = Colors.Red;
        var after = Render(slider)[100, 25];
        Assert.True(after is { A: > 100, R: > 200, G: < 40 }, $"{after}");
    });

    [Fact]
    public void PrzygaszonyUchwytNiePrzepuszczaSwojegoCienia() => Ui.Run(() =>
    {
        var slider = Slider();
        slider.ThumbOpacity = 0.5;
        var bare = Render(slider);

        slider.ThumbShadowBrush = Brushes.Black;
        var shadowed = Render(slider);

        // Uchwyt z cieniem gaśnie jako całość: wewnątrz uchwytu cień pod nim nic nie zmienia,
        // ani nad przezroczystym tłem, ani nad torem.
        foreach (var (x, y) in new[] { (100, 8), (100, 15), (105, 21) })
        {
            var expected = bare[x, y];
            var actual = shadowed[x, y];
            Assert.True(Math.Abs(expected.R - actual.R) <= 3 && Math.Abs(expected.A - actual.A) <= 3,
                $"({x}, {y}): {expected} → {actual}");
        }

        // Poniżej uchwytu cień jest, przygaszony razem z nim.
        Assert.True(shadowed[100, 25] is { A: > 40, R: < 40 }, $"{shadowed[100, 25]}");
    });

    /// <summary>Środek uchwytu: średnie x kryjących pikseli w jego barwie, czerwieni.</summary>
    private static double ThumbCentre(ControlPixels pixels)
    {
        double sum = 0;
        var count = 0;

        for (var y = 0; y < pixels.Height; y++)
        for (var x = 0; x < pixels.Width; x++)
        {
            if (pixels[x, y] is not { A: > 200, R: > 200, G: < 60, B: < 60 }) continue;
            sum += x + 0.5;
            count++;
        }

        Assert.True(count > 0, "uchwytu nie widać");
        return sum / count;
    }

    private static TrackSlider InTrack(double value, double diameter = 20) => new()
    {
        Value = value,
        TrackThickness = 4,
        ThumbDiameter = diameter,
        ThumbInTrack = true,
        ThumbBrush = Brushes.Red,
    };

    /// <summary>
    /// Zasięg uchwytu w poziomie: suwak 200 × 30 rysowany z marginesem 20, żeby uchwyt wystający
    /// poza suwak też było widać. Wynik w układzie suwaka.
    /// </summary>
    private static (double Left, double Right) ThumbSpan(TrackSlider slider)
    {
        slider.Width = 200;
        slider.Height = 30;
        var pixels = ControlPixels.Render(new Border { Padding = new Thickness(20), Child = slider }, 240, 70);

        var left = double.MaxValue;
        var right = double.MinValue;
        for (var y = 0; y < pixels.Height; y++)
        for (var x = 0; x < pixels.Width; x++)
        {
            if (pixels[x, y] is not { A: > 200, R: > 200, G: < 60, B: < 60 }) continue;
            left = Math.Min(left, x - 20);
            right = Math.Max(right, x + 1 - 20);
        }

        Assert.True(left < right, "uchwytu nie widać");
        return (left, right);
    }

    /// <summary>
    /// Uchwyt w torze: przy 0 i 1 jego brzeg stoi na końcu toru, a środek idzie od promienia do
    /// szerokości minus promień, także gdy uchwyt rośnie, jak w regulacji paska. W połowie toru
    /// środek nie zależy od średnicy.
    /// </summary>
    [Fact]
    public void UchwytWTorzeNieWystajePrzyZeroIJedenTakzePowiekszony() => Ui.Run(() =>
    {
        Assert.False(new TrackSlider().ThumbInTrack);

        Assert.InRange(ThumbCentre(Render(InTrack(0))), 9.8, 10.2);
        Assert.InRange(ThumbCentre(Render(InTrack(1))), 189.8, 190.2);
        Assert.InRange(ThumbCentre(Render(InTrack(0.163))), 10 + 0.163 * 180 - 0.2, 10 + 0.163 * 180 + 0.2);

        foreach (var diameter in new[] { 20.0, 28.0 })
        {
            var (left, right) = ThumbSpan(InTrack(0, diameter));
            Assert.InRange(left, -0.01, 0.5);
            Assert.InRange(right, diameter - 0.5, diameter + 0.01);

            (left, right) = ThumbSpan(InTrack(1, diameter));
            Assert.InRange(left, 200 - diameter - 0.01, 200 - diameter + 0.5);
            Assert.InRange(right, 199.5, 200.01);

            Assert.InRange(ThumbCentre(Render(InTrack(0.5, diameter))), 99.8, 100.2);
        }

        // Bez przełącznika środek stoi na końcu wypełnienia, jak w oknie.
        var plain = InTrack(0);
        plain.ThumbInTrack = false;
        Assert.InRange(ThumbSpan(plain).Left, -10.01, -9.5);
    });

    /// <summary>
    /// Pasek sceny: 880 jednostek od x = 920 kadru 1920 × 1080, położenie 0:39 z 3:59. Suwak range
    /// w przeglądarce stawia tam środek uchwytu na x ≈ 1070, a wypełnienie kończy na 16,3% toru.
    /// </summary>
    [Fact]
    public void UchwytPaskaScenyStoiJakSuwakPrzegladarki() => Ui.Run(() =>
    {
        var slider = InTrack(39.0 / 239);
        slider.FillBrush = Brushes.Lime;
        var pixels = ControlPixels.Render(slider, 880, 20);

        Assert.InRange(920 + ThumbCentre(pixels), 1069.5, 1071);

        // Wypełnienie bez zmian: kończy się na 16,3% z 880, czyli na x ≈ 143,6 paska, pod uchwytem.
        slider.ThumbOpacity = 0;
        var fill = ControlPixels.Render(slider, 880, 20);
        Assert.Equal(Colors.Lime, fill[142, 10]);
        Assert.Equal(0, fill[145, 10].A);
    });

    /// <summary>
    /// Wskaźnik ustawia wartość tak, żeby uchwyt stał pod nim: przy uchwycie 20 i szerokości 200
    /// x = 10 to 0, x = 190 to 1, a poza tym zakresem wartość się przycina. Uchwyt 28 przesuwa
    /// skrajne położenia do 14 i 186.
    /// </summary>
    [Fact]
    public void PrzeciaganieIKlikniecieWTorUstawiajaWartoscPodUchwytem() => Ui.Run(() =>
    {
        var slider = InTrack(0.5);
        slider.Width = 200;
        slider.Height = 20;
        var window = new Window
        {
            Width = 300,
            Height = 100,
            Content = new StackPanel { Margin = new Thickness(20), Children = { slider } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Point At(double x) => slider.TranslatePoint(new Point(x, 10), window)!.Value;

        try
        {
            window.MouseDown(At(64), MouseButton.Left);
            Assert.Equal(0.3, slider.Value, 6);

            window.MouseMove(At(154));
            Assert.Equal(0.8, slider.Value, 6);

            window.MouseMove(At(4));
            Assert.Equal(0, slider.Value);

            window.MouseMove(At(198));
            Assert.Equal(1, slider.Value);
            window.MouseUp(At(198), MouseButton.Left);

            // Kliknięcie w środek toru to połowa, jak bez wcięcia.
            window.MouseDown(At(100), MouseButton.Left);
            window.MouseUp(At(100), MouseButton.Left);
            Assert.Equal(0.5, slider.Value, 6);

            window.MouseDown(At(37), MouseButton.Left);
            window.MouseUp(At(37), MouseButton.Left);
            Assert.Equal(0.15, slider.Value, 6);

            slider.ThumbDiameter = 28;
            window.MouseDown(At(14), MouseButton.Left);
            Assert.Equal(0, slider.Value, 6);
            window.MouseMove(At(186));
            Assert.Equal(1, slider.Value, 6);
            window.MouseMove(At(100));
            Assert.Equal(0.5, slider.Value, 6);
            window.MouseMove(At(57));
            Assert.Equal(0.25, slider.Value, 6);
            window.MouseUp(At(57), MouseButton.Left);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Zakończenie przeciągania z kodu działa jak puszczenie: jedno zatwierdzenie bieżącej wartości,
    /// a dalszy ruch i puszczenie suwaka już nie przesuwają.
    /// </summary>
    [Fact]
    public void ZakonczeniePrzeciaganiaZatwierdzaWartoscIZwalniaWskaznik() => Ui.Run(() =>
    {
        var slider = InTrack(0.5);
        slider.Width = 200;
        slider.Height = 20;
        var committed = new List<double>();
        slider.Committed += (_, value) => committed.Add(value);
        var window = new Window
        {
            Width = 300,
            Height = 100,
            Content = new StackPanel { Margin = new Thickness(20), Children = { slider } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Point At(double x) => slider.TranslatePoint(new Point(x, 10), window)!.Value;

        try
        {
            window.MouseDown(At(64), MouseButton.Left);
            window.MouseMove(At(100));
            slider.EndDrag();
            Assert.Equal(0.5, Assert.Single(committed), 6);

            window.MouseMove(At(154));
            window.MouseUp(At(154), MouseButton.Left);
            Assert.Equal(0.5, slider.Value, 6);
            Assert.Single(committed);

            // Bez przeciągania nic się nie dzieje.
            slider.EndDrag();
            Assert.Single(committed);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CzytnikEkranuCzytaTekstWartosciObokZakresu() => Ui.Run(() =>
    {
        var slider = new TrackSlider { Width = 200, Height = 16, Value = 0.163, KeyboardStep = 0.02 };
        var window = new Window { Width = 300, Height = 100, Content = slider };
        window.Show();

        try
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(slider);

            // Bez tekstu wartości czytnik dostaje sam zakres.
            Assert.Null(peer.GetProvider<IValueProvider>());
            Assert.NotNull(peer.GetProvider<IRangeValueProvider>());

            var changes = new List<AutomationPropertyChangedEventArgs>();
            peer.PropertyChanged += (_, e) => changes.Add(e);
            slider.ValueText = "0:39 z 3:59";

            var text = peer.GetProvider<IValueProvider>();
            Assert.NotNull(text);
            Assert.Equal("0:39 z 3:59", text.Value);

            // Wzorzec wartości nie oznacza suwaka jako tylko do odczytu, choć tekstu nie da się wpisać.
            Assert.False(text.IsReadOnly);
            Assert.Throws<InvalidOperationException>(() => text.SetValue("1:00"));
            Assert.Contains(changes, e => e.Property == ValuePatternIdentifiers.ValueProperty
                                          && (string?)e.NewValue == "0:39 z 3:59");

            // Zakres działa dalej: czytnik może przesunąć suwak.
            var range = peer.GetProvider<IRangeValueProvider>();
            Assert.NotNull(range);
            Assert.False(range.IsReadOnly);
            Assert.Equal(16.3, range.Value, 6);
            range.SetValue(50);
            Assert.Equal(0.5, slider.Value, 6);

            // Wyłączony suwak jest tylko do odczytu w obu wzorcach.
            slider.IsEnabled = false;
            Assert.True(range.IsReadOnly);
            Assert.True(text.IsReadOnly);
            slider.IsEnabled = true;

            // Usunięty tekst chowa wzorzec wartości.
            slider.ValueText = null;
            Assert.Null(peer.GetProvider<IValueProvider>());
        }
        finally { window.Close(); }
    });
}
