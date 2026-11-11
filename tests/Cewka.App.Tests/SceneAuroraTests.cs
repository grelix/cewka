using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Dsp;
using SkiaSharp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Zorza: shader, rysowanie bez okna i parametry wyglądu (raster, bez GPU).</summary>
public sealed class SceneAuroraRendererTests
{
    private static readonly SKColor Blue = new(80, 130, 220);
    private static readonly SKColor Amber = new(240, 180, 90);

    internal static byte[] Paint(int width, int height, float time, float low, float mid, float high, float level,
        SKColor first, SKColor second, float brightness = 1, float ceiling = AuroraRenderer.DefaultCeiling)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new AuroraRenderer { Ceiling = ceiling }.Draw(canvas, width, height, time, low, mid, high, level, first, second, brightness);
        return bitmap.GetPixelSpan().ToArray();
    }

    [Fact]
    public void ShaderKompilujeSieBezBledow()
    {
        var effect = SKRuntimeEffect.CreateShader(AuroraRenderer.Source, out var errors);

        Assert.True(effect is not null, "Shader Zorzy nie skompilował się: " + errors);
        Assert.True(string.IsNullOrEmpty(errors), errors);
        effect.Dispose();
    }

    [Fact]
    public void DwieKlatkiWRoznymCzasieSieRoznia()
    {
        var first = Paint(240, 135, 0, 0.5f, 0.5f, 0.5f, 0.6f, Blue, Amber);
        var second = Paint(240, 135, 7.5f, 0.5f, 0.5f, 0.5f, 0.6f, Blue, Amber);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TenSamCzasDajeTenSamObraz()
    {
        var first = Paint(240, 135, 3.25f, 0.4f, 0.7f, 0.9f, 0.5f, Blue, Amber);
        var second = Paint(240, 135, 3.25f, 0.4f, 0.7f, 0.9f, 0.5f, Blue, Amber);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f, 20, 20, 20, 20)]
    [InlineData(1f, 1f, 1f, 1f, 250, 250, 255, 255)]
    [InlineData(1f, 1f, 1f, 1f, 0, 0, 0, 0)]
    [InlineData(0.5f, 0.2f, 0.9f, 0.7f, 255, 255, 255, 255)]
    public void KadrNieMaCzystejCzerniAniBieli(float low, float mid, float high, float level, byte r1, byte g1, byte b1, byte r2)
    {
        var pixels = Paint(320, 180, 2f, low, mid, high, level, new SKColor(r1, g1, b1), new SKColor(r2, r2, r2));

        var darkest = 255;
        var brightest = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            darkest = Math.Min(darkest, Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])));
            brightest = Math.Max(brightest, Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])));
        }

        Assert.True(darkest >= 5, $"Najciemniejszy piksel ma kanał {darkest}: tło nie może być czarne");
        Assert.True(brightest <= 200, $"Najjaśniejszy piksel ma kanał {brightest}: wstęgi trzymają się motywu ciemnego");
    }

    [Fact]
    public void WstegiLezaWDolnychDwochTrzecichKadru()
    {
        const int width = 320;
        const int height = 180;
        var dark = Paint(width, height, 4f, 1f, 1f, 1f, 1f, Blue, Amber, brightness: 0);
        var lit = Paint(width, height, 4f, 1f, 1f, 1f, 1f, Blue, Amber, brightness: 1);

        var top = (int)(height * 0.33) * width * 4;
        Assert.True(dark.AsSpan(0, top).SequenceEqual(lit.AsSpan(0, top)), "wstęgi sięgają górnej trzeciej części kadru");
        Assert.False(dark.AsSpan(top).SequenceEqual(lit.AsSpan(top)), "wstęgi nie pojawiają się w dolnej części kadru");
    }

    [Fact]
    public void BasPodnosiWstegi()
    {
        const int width = 320;
        const int height = 180;
        var quiet = Centroid(Paint(width, height, 4f, 0f, 0.5f, 0.5f, 1f, Blue, Amber), width, height);
        var loud = Centroid(Paint(width, height, 4f, 1f, 0.5f, 0.5f, 1f, Blue, Amber), width, height);

        Assert.True(loud < quiet - 0.01, $"środek wstęg: cisza {quiet:F3}, bas {loud:F3}");
    }

    /// <summary>Wysokość środka ciężkości jasności ponad tłem, jako ułamek kadru (0 góra).</summary>
    private static double Centroid(byte[] pixels, int width, int height)
    {
        double weight = 0;
        double moment = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var value = Math.Max(0, Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) - 20);
                weight += value;
                moment += value * y;
            }
        }

        return moment / weight / height;
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(220, 30, 30)]
    [InlineData(255, 40, 90)]
    public void NasycenieCzerwieniNiePrzekraczaSzesciuDziesiatych(byte r, byte g, byte b)
    {
        var red = new SKColor(r, g, b);
        var pixels = Paint(320, 180, 2f, 0.8f, 0.5f, 0.5f, 1f, red, red);

        var checkedPixels = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var blue = pixels[i];
            var green = pixels[i + 1];
            var rd = pixels[i + 2];
            var max = Math.Max(rd, Math.Max(green, blue));
            var min = Math.Min(rd, Math.Min(green, blue));

            // Ciemne piksele zaokrągla się zbyt grubo, żeby nasycenie miało sens.
            if (max < 70) continue;

            var hue = Hue(rd, green, blue, max, min);
            if (hue is >= 20 and <= 340) continue;

            checkedPixels++;
            var saturation = (max - min) / (double)max;
            Assert.True(saturation <= 0.6 + 0.03, $"nasycenie czerwieni {saturation:F3} w pikselu {i / 4}");
        }

        Assert.True(checkedPixels > 100, "kadr nie zawiera czerwonych wstęg do sprawdzenia");
    }

    private static double Hue(int r, int g, int b, int max, int min)
    {
        if (max == min) return 0;

        var span = (double)(max - min);
        var hue = max == r ? 60 * ((g - b) / span) : max == g ? 60 * (2 + (b - r) / span) : 60 * (4 + (r - g) / span);
        return hue < 0 ? hue + 360 : hue;
    }
}

/// <summary>Zorza: tempo falowania, ogranicznik błysków na obrazie 240 x 136, pauza i bufor.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneAuroraSafetyTests
{
    /// <summary>
    /// Bufor 240 x 136, około połowy najmniejszego poziomu programu (480 x 270) w każdym wymiarze. Shader rysowany
    /// na procesorze kosztuje w linuksowej bibliotece Skia około 0,7 s na klatkę 480 x 270, a testy mierzą tysiące
    /// klatek. Że mniejszy obraz ma te same jasności, sprawdza <see cref="MalyBuforMaJasnosciNajmniejszegoPoziomuProgramu"/>.
    /// </summary>
    private const double TestScale = 0.25;

    private static readonly (int Width, int Height) Size = AuroraBuffer.SizeFor(TestScale);

    private static AuroraView Make(FakeSceneAudio source, float ceiling = AuroraRenderer.DefaultCeiling, double? scale = TestScale)
    {
        var view = new AuroraView { Source = source, Ceiling = ceiling, BufferScale = scale };
        view.SetColours(Color.FromRgb(230, 230, 240), Color.FromRgb(255, 250, 235));

        // Istniejące obniżanie rozdzielczości: po trzech poziomach bufor programu ma 480 x 270.
        for (var i = 0; i < 100 && view.Budget.Level < FrameBudget.MaxLevel; i++) view.Budget.Observe(0.06);
        return view;
    }

    private static double Measure(AuroraView view) => Measure(view, Size.Width, Size.Height);

    private static double Measure(AuroraView view, int width, int height) =>
        FrameLuminance.Measure(ControlPixels.Render(view, width, height).PackedBgraOverBlack());

    /// <summary>
    /// Obraz testów i obraz najmniejszego poziomu programu, rysowane z tych samych pasm, mają jasność
    /// zgodną z dokładnością do ułamka progu przejścia: w całym zakresie fazy shadera, jaki przechodzą testy
    /// bezpieczeństwa (360 klatek), przy impulsach i szumie, w dwóch paletach i przy obrazie jaśniejszym od rzeczywistego.
    /// </summary>
    [Theory]
    [InlineData("impulsy, paleta jasna", false, false, AuroraRenderer.DefaultCeiling)]
    [InlineData("szum, paleta jasna, sufit 2", true, false, 2f)]
    [InlineData("szum, paleta nasycona", true, true, AuroraRenderer.DefaultCeiling)]
    public void MalyBuforMaJasnosciNajmniejszegoPoziomuProgramu(string name, bool noise, bool saturated, float ceiling) => Ui.Run(() =>
    {
        var (programWidth, programHeight) = AuroraBuffer.SizeFor(FrameBudget.ScaleFor(FrameBudget.MaxLevel));
        var small = new FakeSceneAudio();
        var program = new FakeSceneAudio();
        var testView = Make(small, ceiling);
        var programView = Make(program, ceiling, scale: null);
        if (saturated)
        {
            testView.SetColours(Colors.DarkOrange, Colors.Teal);
            programView.SetColours(Colors.DarkOrange, Colors.Teal);
        }

        var random = new Random(11);
        var largest = 0.0;
        var span = (Min: double.MaxValue, Max: 0.0);
        for (var frame = 0; frame < 360; frame++)
        {
            var level = noise ? (float)random.NextDouble() : frame % 40 < 20 ? 1f : 0.1f;
            Feed(small, level);
            Feed(program, level);
            if (noise)
            {
                for (var i = 0; i < SpectrumBars.Count; i++)
                    small.Spectrum[i] = program.Spectrum[i] = (float)random.NextDouble();
            }

            testView.Step(1.0 / 60);
            programView.Step(1.0 / 60);
            if (frame % 24 != 23) continue;

            var expected = Measure(programView, programWidth, programHeight);
            largest = Math.Max(largest, Math.Abs(Measure(testView) - expected));
            span = (Math.Min(span.Min, expected), Math.Max(span.Max, expected));
        }

        Assert.True(span.Max > 0, $"{name}: obraz jest czarny");
        if (!noise) Assert.True(span.Max - span.Min > FlashLimiter.Threshold / 2, $"{name}: jasność stoi w miejscu");
        Assert.True(largest <= FlashLimiter.Threshold / 10,
            $"{name}: największa różnica jasności {largest:F4}, próg przejścia {FlashLimiter.Threshold}, jasność {span.Min:F3}..{span.Max:F3}");
    });

    private static void Feed(FakeSceneAudio source, float level)
    {
        source.Fill(level);
        source.Gain = level * 1.5f;
        source.Levels = new ChannelLevels(level, level, level, level);
    }

    /// <summary>Największa liczba przejść w oknie 1 s; przejście to odwrócenie o co najmniej próg ogranicznika.</summary>
    private static int MostTransitionsPerSecond(IReadOnlyList<double> series, int framesPerSecond)
    {
        var at = new List<int>();
        var extreme = series[0];
        var direction = 0;
        for (var i = 1; i < series.Count; i++)
        {
            var value = series[i];
            if (direction == 0)
            {
                if (Math.Abs(value - extreme) < FlashLimiter.Threshold) continue;
                direction = Math.Sign(value - extreme);
                extreme = value;
                at.Add(i);
            }
            else if ((value - extreme) * direction > 0) extreme = value;
            else if ((extreme - value) * direction >= FlashLimiter.Threshold)
            {
                direction = -direction;
                extreme = value;
                at.Add(i);
            }
        }

        var most = 0;
        for (var first = 0; first < at.Count; first++)
            most = Math.Max(most, at.Skip(first).TakeWhile(i => i - at[first] < framesPerSecond).Count());
        return most;
    }

    private static void AssertSafe(string name, AuroraView view, List<double> series)
    {
        Assert.True(series.Max() > 0, $"{name}: obraz jest czarny");
        var most = MostTransitionsPerSecond(series, 60);
        Assert.True(most <= FlashLimiter.MaxTransitions,
            $"{name}: {most} przejść w sekundzie, jasność {series.Min():F3}..{series.Max():F3}, najniższy mnożnik {view.LimiterGain:F2}");
    }

    [Fact]
    public void TempoFalowaniaToCalkaBezSkokowPrzyZmianiePasma() => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        var view = Make(source);
        var previous = view.Phase;
        var largest = 0.0;
        for (var frame = 0; frame < 240; frame++)
        {
            // Raz bas, raz środek, raz cisza: tempo zmienia się wraz z pasmem środkowym.
            Array.Clear(source.Spectrum);
            var band = (frame / 40) % 3;
            if (band == 1) for (var i = 8; i < 28; i++) source.Spectrum[i] = 1f;
            if (band == 0) for (var i = 0; i < 8; i++) source.Spectrum[i] = 1f;
            source.Levels = new ChannelLevels(0.5f, 0.5f, 0.5f, 0.5f);
            view.Step(1.0 / 60);

            var step = view.Phase - previous;
            Assert.True(step >= 0, "czas shadera cofnął się");
            largest = Math.Max(largest, step);
            previous = view.Phase;
        }

        Assert.True(largest <= AuroraView.MaxRate / 60 + 1e-9, $"największy przyrost {largest}");
        Assert.True(largest > AuroraView.BaseRate / 60, "środek pasma nie przyspiesza falowania");
    });

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.6)]
    [InlineData(2)]
    [InlineData(2.5)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(10)]
    public void SzybkieImpulsyNieDajaWiecejNizTrzechPrzejscNaSekunde(double hertz) => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        var view = Make(source);
        var series = new List<double>();
        for (var frame = 0; frame < 240; frame++)
        {
            Feed(source, (int)(frame / 60.0 * 2 * hertz) % 2 == 0 ? 1f : 0f);
            view.Step(1.0 / 60);
            series.Add(Measure(view));
        }

        AssertSafe($"Zorza {hertz} Hz", view, series);
    });

    [Fact]
    public void SzumNieDajeWiecejNizTrzechPrzejscNaSekunde() => Ui.Run(() =>
    {
        var random = new Random(7);
        var source = new FakeSceneAudio();
        var view = Make(source);
        var series = new List<double>();
        for (var frame = 0; frame < 240; frame++)
        {
            Feed(source, (float)random.NextDouble());
            for (var i = 0; i < SpectrumBars.Count; i++) source.Spectrum[i] = (float)random.NextDouble();
            view.Step(1.0 / 60);
            series.Add(Measure(view));
        }

        AssertSafe("Zorza, szum", view, series);
    });

    [Fact]
    public void SkokZCiszyDoPelnejSkaliNieDajeWiecejNizTrzechPrzejscNaSekunde() => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        var view = Make(source);
        var series = new List<double>();
        for (var frame = 0; frame < 240; frame++)
        {
            Feed(source, frame < 90 || frame >= 150 ? 0f : 1f);
            view.Step(1.0 / 60);
            series.Add(Measure(view));
        }

        AssertSafe("Zorza, skok cisza-pełna skala", view, series);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LosoweImpulsyNieDajaWiecejNizTrzechPrzejscNaSekunde(int seed) => Ui.Run(() =>
    {
        var random = new Random(seed);
        var source = new FakeSceneAudio();
        var view = Make(source);
        var on = false;
        var left = 0;
        var level = 0f;
        var series = new List<double>();
        for (var frame = 0; frame < 360; frame++)
        {
            if (left-- <= 0)
            {
                on = !on;
                left = random.Next(6, 45);
                level = on ? (float)(0.3 + 0.7 * random.NextDouble()) : 0f;
            }

            Feed(source, level);
            view.Step(1.0 / 60);
            series.Add(Measure(view));
        }

        AssertSafe($"Zorza, ziarno {seed}", view, series);
    });

    /// <summary>
    /// Obraz jaśniejszy od rzeczywistego (sufit wstęg 2,0 przy skali jak dla 0,55): tu wahnięcia surowej luminancji przekraczają próg
    /// 10%, więc test naprawdę ćwiczy ogranicznik i podtrzymanie. Wyłączenie ogranicznika w
    /// <c>AuroraView.Step</c> czerwieni każdy wariant. Impulsy 6 Hz i szybsze tłumi już samo wygładzanie basu,
    /// a 1,25 Hz mieści się w limicie bez ogranicznika, więc te częstotliwości niczego by tu nie sprawdzały.
    /// </summary>
    [Theory]
    [InlineData(1.6)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void ObrazJasniejszyOdRzeczywistegoRowniezNieDajeWiecejNizTrzechPrzejsc(double hertz) => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        var view = Make(source, ceiling: 2f);
        var series = new List<double>();
        var rawSpan = (Min: double.MaxValue, Max: 0.0);
        for (var frame = 0; frame < 240; frame++)
        {
            Feed(source, (int)(frame / 60.0 * 2 * hertz) % 2 == 0 ? 1f : 0f);
            view.Step(1.0 / 60);
            rawSpan = (Math.Min(rawSpan.Min, view.EstimatedLuminance), Math.Max(rawSpan.Max, view.EstimatedLuminance));
            series.Add(Measure(view));
        }

        // Bez rozmachu surowej luminancji powyżej progu test niczego by nie sprawdzał.
        Assert.True(rawSpan.Max - rawSpan.Min > FlashLimiter.Threshold,
            $"surowa luminancja {rawSpan.Min:F3}..{rawSpan.Max:F3} nie przekracza progu przejścia");
        AssertSafe($"Zorza jaśniejsza, {hertz} Hz", view, series);
    });

    [Fact]
    public void PodtrzymanieUnosiBasTylkoGdyOgranicznikWstrzymujeOpadanie() => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        var view = Make(source, ceiling: 2f);
        var held = false;
        for (var frame = 0; frame < 240; frame++)
        {
            Feed(source, (int)(frame / 60.0 * 2 * 3) % 2 == 0 ? 1f : 0f);
            view.Step(1.0 / 60);
            if (view.HoldLift > 0)
            {
                held = true;
                Assert.True(view.Frame.Low >= (float)view.Low, "podtrzymanie obniżyło bas");
                Assert.Equal(1.0, view.LimiterGain);
            }
        }

        Assert.True(held, "ogranicznik ani razu nie wstrzymał opadania");
    });

    [Fact]
    public void BuforWewnetrznyPowstajeRazIWRuchuNieOdNowa() => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        Feed(source, 0.5f);

        // Rozmiar bufora z obniżania rozdzielczości, jak w programie (480 x 270), bez nadpisania testów.
        var view = Make(source, scale: null);
        view.SetColours(Color.FromRgb(80, 130, 220), Color.FromRgb(240, 180, 90));
        for (var frame = 0; frame < 20; frame++)
        {
            view.Step(1.0 / 60);
            ControlPixels.Render(view, 320, 180);
        }

        Assert.Equal(1, view.Buffer.Rebuilds);
        Assert.Equal((960, 540), AuroraBuffer.SizeFor(1.0));
        Assert.Equal((480, 270), AuroraBuffer.SizeFor(0.5));
    });

    [Fact]
    public void PauzaNieZamawiaKlatek() => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        source.Fill(0.2f);
        var view = new AuroraView { Source = source };
        var window = new Window { Width = 400, Height = 300, Content = view };
        var requests = 0;

        void Count(Avalonia.Visual owner)
        {
            if (ReferenceEquals(owner, view)) requests++;
        }

        void Pump()
        {
            for (var i = 0; i < 10; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
        }

        RenderLoop.FrameRequested += Count;
        try
        {
            window.Show();
            view.SetActive(true);
            Pump();
            Assert.True(requests > 0, "Zorza nie zamawiała klatek przy graniu");

            view.SetActive(false);
            Pump();
            requests = 0;
            Pump();
            Assert.Equal(0, requests);
            Assert.False(view.IsRunning);
        }
        finally
        {
            RenderLoop.FrameRequested -= Count;
            window.Close();
        }
    });
}

/// <summary>Dostępność Zorzy: GPU, bateria, tryb ograniczony i ukrycie w Ustawieniach.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneAuroraAvailabilityTests
{
    private static PlayerHarness Harness(bool? gpu, bool battery = false)
    {
        var h = new PlayerHarness(s => s.Effects = EffectsMode.Full);
        h.Player.BatteryProbe = () => battery;
        h.Player.RefreshBattery();
        if (gpu is { } known) h.Player.PinGpu(known);
        return h;
    }

    private static double Saturation(byte r, byte g, byte b)
    {
        var top = Math.Max(r, Math.Max(g, b));
        return top == 0 ? 0 : (top - Math.Min(r, Math.Min(g, b))) / (double)top;
    }

    [Fact]
    public void WstegiMajaBarwyOkladkiANiePasteleTla() => Ui.Run(() =>
    {
        using var h = Harness(true);
        h.Player.SceneVisual = SceneVisual.Aurora;
        h.Player.SceneAudioData = new FakeSceneAudio();
        var window = new MainWindow(false, h.Player) { Width = 1280, Height = 720 };
        window.Show();
        try
        {
            h.OpenPaths([h.TaggedTrack("barwy", "Utwór", "Wykonawca", "Album", seconds: 120)]);
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying));
            Assert.True(Ui.WaitUntil(() => h.Player.CoverTask.IsCompleted));
            h.Player.ShowCover(MainViewModel.DecodeCover(SceneAudioShotsTests.TwoColourCover(Colors.DarkOrange, Colors.Teal)));
            window.OpenSceneForCapture(showControls: false);

            var frame = window.Scene.AuroraView.Frame;
            Assert.True(Saturation(frame.Colour1.Red, frame.Colour1.Green, frame.Colour1.Blue) >= 0.5, $"barwa {frame.Colour1}");
            Assert.True(Saturation(frame.Colour2.Red, frame.Colour2.Green, frame.Colour2.Blue) >= 0.5, $"barwa {frame.Colour2}");

            // Obraz: dwie barwy dopełniające nie mogą zlać się w szarość tam, gdzie wstęgi są jasne.
            const int width = 320, height = 180;
            var pixels = SceneAuroraRendererTests.Paint(width, height, 2, 0.3f, 0.3f, 0.3f, 0.5f, frame.Colour1, frame.Colour2);
            var total = 0.0;
            var count = 0;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) < 60) continue;
                total += Saturation(pixels[i + 2], pixels[i + 1], pixels[i]);
                count++;
            }

            Assert.True(count > 0, "brak jasnych wstęg");
            Assert.True(total / count >= 0.35, $"średnie nasycenie wstęg {total / count:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void BezGpuAniJegoPoznaniaCyklPomijaZorzeAZapisanaWartoscZostaje() => Ui.Run(() =>
    {
        foreach (bool? gpu in new bool?[] { null, false })
        {
            using var h = Harness(gpu);
            h.Player.SceneVisual = SceneVisual.Horizon;

            h.Player.CycleSceneVisual(1);
            Assert.Equal(SceneVisual.Disc, h.Player.SceneVisual);
            h.Player.CycleSceneVisual(-1);
            Assert.Equal(SceneVisual.Horizon, h.Player.SceneVisual);

            h.Player.SceneVisual = SceneVisual.Aurora;
            Assert.Equal(SceneVisual.Disc, h.Player.ActiveSceneVisual);
            Assert.Equal(SceneVisual.Aurora, h.Player.SceneVisual);
            Assert.Equal(SceneVisual.Aurora, h.Settings.Current.SceneVisual);
        }
    });

    [Fact]
    public void ZGpuCyklDochodziDoZorzyPoHoryzoncie() => Ui.Run(() =>
    {
        using var h = Harness(gpu: true);
        h.Player.SceneVisual = SceneVisual.Horizon;

        h.Player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Aurora, h.Player.SceneVisual);
        Assert.Equal(SceneVisual.Aurora, h.Player.ActiveSceneVisual);

        h.Player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Disc, h.Player.SceneVisual);
        h.Player.CycleSceneVisual(-1);
        Assert.Equal(SceneVisual.Aurora, h.Player.SceneVisual);
    });

    [Fact]
    public void NaBateriiZorzaZastepujePlytaAZapisanaWartoscZostaje() => Ui.Run(() =>
    {
        using var h = Harness(gpu: true, battery: true);
        h.Player.SceneVisual = SceneVisual.Aurora;

        Assert.Equal(SceneVisual.Disc, h.Player.ActiveSceneVisual);
        Assert.Equal(SceneVisual.Aurora, h.Player.SceneVisual);
        Assert.Equal(SceneVisual.Aurora, h.Settings.Current.SceneVisual);

        h.Player.SceneVisual = SceneVisual.Horizon;
        h.Player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Disc, h.Player.SceneVisual);

        h.Player.SceneVisual = SceneVisual.Aurora;
        h.Player.BatteryProbe = () => false;
        h.Player.RefreshBattery();
        Assert.Equal(SceneVisual.Aurora, h.Player.ActiveSceneVisual);
    });

    [Fact]
    public void OtwarcieScenyOdczytujeZasilanie() => Ui.Run(() =>
    {
        using var h = Harness(gpu: true);
        var reads = 0;
        h.Player.BatteryProbe = () =>
        {
            reads++;
            return true;
        };
        h.Player.SceneVisual = SceneVisual.Aurora;
        Assert.Equal(SceneVisual.Aurora, h.Player.ActiveSceneVisual);

        h.Player.IsSceneOpen = true;

        Assert.Equal(1, reads);
        Assert.Equal(SceneVisual.Disc, h.Player.ActiveSceneVisual);
    });

    [Fact]
    public void TrybOgraniczonyZastepujeZorzePlytaTakJakWidmo() => Ui.Run(() =>
    {
        using var h = Harness(gpu: true);
        h.Player.SetEffects(EffectsMode.Reduced);

        foreach (var visual in new[] { SceneVisual.Spectrum, SceneVisual.Aurora })
        {
            h.Player.SceneVisual = visual;
            Assert.Equal(SceneVisual.Disc, h.Player.ActiveSceneVisual);
            Assert.Equal(visual, h.Settings.Current.SceneVisual);
        }

        h.Player.SceneVisual = SceneVisual.Horizon;
        h.Player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Disc, h.Player.SceneVisual);
    });

    [Fact]
    public void KartaZorzyWUstawieniachIstniejeTylkoPrzyWykrytymGpu() => Ui.Run(() =>
    {
        using var h = Harness(gpu: null);
        var settings = new SettingsViewModel(h.Settings, h.Player);
        bool Has() => settings.SceneVisualReactiveChoice.Options.Any(o => Equals(o.Value, SceneVisual.Aurora));

        Assert.False(Has());

        h.Player.PinGpu(true);
        Assert.True(Has());

        h.Player.PinGpu(false);
        Assert.False(Has());
        settings.Dispose();
    });

    [Fact]
    public void ZapisanaZorzaJestZaznaczonaPoPojawieniuSieKarty() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.SceneVisual = SceneVisual.Aurora);
        var settings = new SettingsViewModel(h.Settings, h.Player);

        h.Player.PinGpu(true);

        Assert.True(settings.SceneVisualReactiveChoice.Options.Single(o => Equals(o.Value, SceneVisual.Aurora)).IsSelected);
        Assert.Equal(SceneVisual.Aurora, h.Settings.Current.SceneVisual);
        settings.Dispose();
    });
}
