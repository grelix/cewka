using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Horyzont: obwiednia w bitmapie, głowica, dostępność w radiu oraz pomiar błysków na obrazie.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneHorizonTests
{
    private static readonly Color Low = Color.FromRgb(80, 130, 220);
    private static readonly Color High = Color.FromRgb(240, 180, 90);

    private static SceneAudioView Make(SceneAudioMode mode, FakeSceneAudio? source = null)
    {
        var view = new SceneAudioView { Source = source ?? new FakeSceneAudio() };
        view.Mode = mode;
        view.SetColours(Low, High);
        return view;
    }

    /// <summary>Połowa obwiedni głośna (szczyt 1, RMS około 0,6), druga połowa cicha.</summary>
    private static FileEnvelope LoudThenSilent()
    {
        var peak = new byte[FileEnvelope.TargetPoints];
        var rms = new byte[FileEnvelope.TargetPoints];
        for (var i = 0; i < peak.Length / 2; i++)
        {
            peak[i] = 255;
            rms[i] = 200;
        }

        return new FileEnvelope(peak, rms);
    }

    [Fact]
    public void ObwiedniaJestRysowanaRazAWKlatkachTylkoGlowica() => Ui.Run(() =>
    {
        var view = Make(SceneAudioMode.Horizon);
        view.Envelope = LoudThenSilent();
        var progress = 0.0;
        view.ProgressProvider = () => progress;

        for (var frame = 0; frame < 60; frame++)
        {
            progress = frame / 60.0;
            view.Step(1.0 / 30);
            ControlPixels.Render(view, 400, 120);
        }

        Assert.Equal(1, view.HorizonRebuilds);

        view.Envelope = LoudThenSilent();
        ControlPixels.Render(view, 400, 120);
        Assert.Equal(2, view.HorizonRebuilds);

        // Zmiana rozmiaru i barw także odświeża bitmapy, ale tylko raz na zmianę.
        ControlPixels.Render(view, 300, 100);
        ControlPixels.Render(view, 300, 100);
        Assert.Equal(3, view.HorizonRebuilds);
        view.SetColours(Color.FromRgb(20, 200, 120), Color.FromRgb(200, 240, 160));
        ControlPixels.Render(view, 300, 100);
        Assert.Equal(4, view.HorizonRebuilds);
    });

    [Fact]
    public void ObwiedniaMaKsztaltUtworuACzescOdtworzonaJestJasniejsza() => Ui.Run(() =>
    {
        var view = Make(SceneAudioMode.Horizon);
        view.Envelope = LoudThenSilent();
        view.ProgressProvider = () => 0.5;
        view.Step(1.0 / 30);

        var pixels = ControlPixels.Render(view, 400, 120);

        // Głośna połowa sięga daleko od osi, cicha zostawia tylko cienką linię.
        Assert.True(pixels[42, 30].A > 0, "brak słupka w głośnej części");
        Assert.Equal(0, pixels[362, 30].A);
        Assert.True(pixels[362, 60].A > 0, "brak linii w cichej części");

        // Linia po obu stronach głowicy: odtworzona (lewa) jaśniejsza niż nieodtworzona (prawa).
        static double Light(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) * c.A / 255.0;
        Assert.True(Light(pixels[302, 60]) < Light(pixels[102, 60]));
    });

    [Fact]
    public void PrzedPoliczeniemObwiedniJestPlaskaLiniaZPostepemBezWskaznika() => Ui.Run(() =>
    {
        var view = Make(SceneAudioMode.Horizon);
        view.ProgressProvider = () => 0.25;
        view.Step(1.0 / 30);

        var pixels = ControlPixels.Render(view, 400, 120);

        Assert.Null(view.Envelope);
        Assert.Equal(0.25, view.HorizonProgress, 6);
        Assert.True(pixels[202, 60].A > 0, "brak płaskiej linii");
        Assert.Equal(0, pixels[202, 20].A);

        // Część odtworzona (do 25%) jaśniejsza od reszty także na płaskiej linii.
        static double Light(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) * c.A / 255.0;
        Assert.True(Light(pixels[42, 60]) > Light(pixels[252, 60]));
    });

    [Fact]
    public void PostepWychodziPozaZakresLubNieJestLiczbaToBezBledu() => Ui.Run(() =>
    {
        var view = Make(SceneAudioMode.Horizon);
        var value = 7.0;
        view.ProgressProvider = () => value;
        view.Step(1.0 / 30);
        Assert.Equal(1, view.HorizonProgress);

        value = double.NaN;
        view.Step(1.0 / 30);
        Assert.Equal(0, view.HorizonProgress);

        view.ProgressProvider = null;
        view.Step(1.0 / 30);
        Assert.Equal(0, view.HorizonProgress);
        ControlPixels.Render(view, 400, 120);
    });

    [Fact]
    public void PoswiataGlowicyNarastaPoOsiemdziesiatMilisekundachIOpadaPoDwustuPiecdziesieciu() => Ui.Run(() =>
    {
        var source = new FakeSceneAudio { Levels = new Cewka.Audio.Dsp.ChannelLevels(0.3f, 0.3f, 0.5f, 0.5f) };
        var view = Make(SceneAudioMode.Horizon, source);

        view.Step(1.0 / 30);
        Assert.InRange(view.HeadLevel, 0.4, 0.43);
        view.Step(1.0 / 30);
        view.Step(1.0 / 30);
        Assert.InRange(view.HeadLevel, 0.89, 0.91);

        source.Levels = default;
        view.Step(1.0 / 30);
        view.Step(1.0 / 30);
        view.Step(1.0 / 30);
        Assert.InRange(view.HeadLevel, 0.49, 0.51);

        // Ograniczony ruch: głowica idzie dalej, ale bez poświaty od dźwięku.
        source.Levels = new Cewka.Audio.Dsp.ChannelLevels(0.3f, 0.3f, 0.5f, 0.5f);
        view.CalmHead = true;
        for (var i = 0; i < 20; i++) view.Step(1.0 / 30);
        Assert.Equal(0, view.HeadLevel);
    });

    [Fact]
    public void ObwiedniaZJednymPunktemIBezDzwiekuSieRysuje() => Ui.Run(() =>
    {
        var view = Make(SceneAudioMode.Horizon);
        view.Envelope = new FileEnvelope([255], [0]);
        view.ProgressProvider = () => 1;
        view.Step(1.0 / 30);

        var pixels = ControlPixels.Render(view, 400, 120);

        Assert.True(pixels[2, 60].A > 0);
    });

    [Fact]
    public void ModelPobieraObwiedniaPoPolicznieIZwalniaJaPoZmianieWizualizacji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.SceneVisual = SceneVisual.Horizon);
        var envelope = LoudThenSilent();
        h.Measurement = (_, _) => new LoudnessMeasurement(-18, 0.9, envelope);
        h.OpenPaths([h.TaggedTrack("horyzont", "Utwór", "Wykonawca", "Album", seconds: 30)]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying));

        // Zamknięta scena niczego nie liczy ani nie trzyma.
        Assert.Null(h.Player.SceneEnvelope);

        h.Player.IsSceneOpen = true;
        Assert.True(h.PumpUntil(() => h.Player.SceneEnvelope is not null), "obwiednia nie dotarła do modelu");
        Assert.Equal(envelope.Peak, h.Player.SceneEnvelope!.Peak);

        h.Player.SceneVisual = SceneVisual.Disc;
        Assert.Null(h.Player.SceneEnvelope);
    });

    // ---- pomiar błysków na wyrenderowanym obrazie ----

    /// <summary>Strefa wizualizacji w kadrze 1920 x 1080 (x, y, szerokość, wysokość), zgodna z widokiem sceny.</summary>
    private static (double X, double Y, double Width, double Height) Zone(SceneAudioMode mode) => mode switch
    {
        SceneAudioMode.Ring => (160, 200, 680, 680),
        SceneAudioMode.Horizon => (160, 656, 1600, 200),
        SceneAudioMode.Spectrum or SceneAudioMode.Waveform => (160, 590, 1600, 410),
        _ => (140, 320, 760, 600),
    };

    /// <summary>
    /// Wizualizacja we właściwym miejscu kadru 1920 x 1080 zmniejszonego 4 razy, na czarnym tle: tak samo
    /// rozłożona luminancja jak w scenie. Mniejszy bufor mieszałby wąskie słupki z tłem w przestrzeni gamma
    /// i zaniżał zmierzoną jasność.
    /// </summary>
    private static byte[] RenderFrame(SceneAudioView view, SceneAudioMode mode)
    {
        var zone = Zone(mode);
        view.Width = zone.Width;
        view.Height = zone.Height;
        view.HorizontalAlignment = HorizontalAlignment.Left;
        view.VerticalAlignment = VerticalAlignment.Top;
        view.Margin = new Thickness(zone.X, zone.Y, 0, 0);

        if (view.Parent is not Panel stage)
        {
            stage = new Panel
            {
                Width = 1920,
                Height = 1080,
                Background = Brushes.Black,
                RenderTransform = new ScaleTransform(1.0 / FrameDivisor, 1.0 / FrameDivisor),
                RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
                Children = { view },
            };
            _ = new Canvas { Children = { stage } };
        }

        var canvas = (Canvas)stage.Parent!;
        return ControlPixels.Render(canvas, 1920 / FrameDivisor, 1080 / FrameDivisor).PackedBgraOverBlack();
    }

    private const int FrameDivisor = 4;

    [Theory]
    [InlineData(SceneAudioMode.Spectrum)]
    [InlineData(SceneAudioMode.Ring)]
    [InlineData(SceneAudioMode.Waveform)]
    [InlineData(SceneAudioMode.Horizon)]
    public void LuminancjaWyrenderowanegoObrazuNieBlyskaCzesciejNizTrzyRazyNaSekunde(SceneAudioMode mode) => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        var view = Make(mode, source);
        view.SetColours(Color.FromRgb(230, 230, 240), Color.FromRgb(255, 250, 235));
        view.Envelope = LoudThenSilent();
        var frame = 0;
        view.ProgressProvider = () => frame / 240.0;

        // Najgorszy przypadek: impulsy basu 3 Hz, pół okresu pełnej skali, pół ciszy.
        var series = new List<double>();
        for (frame = 0; frame < 240; frame++)
        {
            var on = (frame / 10) % 2 == 0;
            source.Fill(on ? 1f : 0f);
            source.Gain = on ? 1.5f : 0f;
            source.Levels = on ? new ChannelLevels(1, 1, 1, 1) : default;
            view.Step(1.0 / 60);
            series.Add(FrameLuminance.Measure(RenderFrame(view, mode)));
        }

        Assert.True(series.Max() > 0, $"{mode}: obraz jest czarny");
        Assert.True(CountTransitions(series) <= 12, $"{mode}: {CountTransitions(series)} przejść w 4 s");
    });

    /// <summary>Szybkie impulsy na wyrenderowanym obrazie: zmiana o co najmniej 10% najwyżej 3 razy w każdej sekundzie.</summary>
    [Theory]
    [InlineData(SceneAudioMode.Spectrum, 1.25)]
    [InlineData(SceneAudioMode.Spectrum, 1.6)]
    [InlineData(SceneAudioMode.Spectrum, 2)]
    [InlineData(SceneAudioMode.Spectrum, 2.5)]
    [InlineData(SceneAudioMode.Spectrum, 3)]
    [InlineData(SceneAudioMode.Spectrum, 4)]
    [InlineData(SceneAudioMode.Spectrum, 6)]
    [InlineData(SceneAudioMode.Spectrum, 10)]
    [InlineData(SceneAudioMode.Ring, 6)]
    [InlineData(SceneAudioMode.Waveform, 6)]
    [InlineData(SceneAudioMode.Horizon, 6)]
    public void SzybkieImpulsyNieDajaWiecejNizTrzechPrzejscNaSekunde(SceneAudioMode mode, double hertz) => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        var view = Make(mode, source);
        view.SetColours(Color.FromRgb(230, 230, 240), Color.FromRgb(255, 250, 235));
        view.Envelope = LoudThenSilent();
        var frame = 0;
        view.ProgressProvider = () => frame / 240.0;

        var series = new List<double>();
        for (frame = 0; frame < 240; frame++)
        {
            var on = (int)(frame / 60.0 * 2 * hertz) % 2 == 0;
            source.Fill(on ? 1f : 0f);
            source.Gain = on ? 1.5f : 0f;
            source.Levels = on ? new ChannelLevels(1, 1, 1, 1) : default;
            view.Step(1.0 / 60);
            series.Add(FrameLuminance.Measure(RenderFrame(view, mode)));
        }

        Assert.True(series.Max() > 0, $"{mode}: obraz jest czarny");
        var most = MostTransitionsPerSecond(series, 60);
        Assert.True(most <= FlashLimiter.MaxTransitions, $"{mode} {hertz} Hz: {most} przejść w sekundzie, jasność {series.Min():F3}..{series.Max():F3}");
    });

    /// <summary>Nieregularne impulsy o losowej sile i długości, z zapisanym ziarnem: ta sama granica przejść.</summary>
    [Theory]
    [InlineData(SceneAudioMode.Spectrum, 1)]
    [InlineData(SceneAudioMode.Spectrum, 2)]
    [InlineData(SceneAudioMode.Spectrum, 3)]
    [InlineData(SceneAudioMode.Spectrum, 4)]
    [InlineData(SceneAudioMode.Waveform, 1)]
    [InlineData(SceneAudioMode.Horizon, 1)]
    public void LosoweImpulsyNieDajaWiecejNizTrzechPrzejscNaSekunde(SceneAudioMode mode, int seed) => Ui.Run(() =>
    {
        var random = new Random(seed);
        var source = new FakeSceneAudio();
        var view = Make(mode, source);
        view.SetColours(Color.FromRgb(230, 230, 240), Color.FromRgb(255, 250, 235));
        view.Envelope = LoudThenSilent();
        var frame = 0;
        view.ProgressProvider = () => frame / 600.0;

        var on = false;
        var left = 0;
        var level = 0f;
        var series = new List<double>();
        for (frame = 0; frame < 600; frame++)
        {
            if (left-- <= 0)
            {
                on = !on;
                left = random.Next(6, 45);
                level = on ? (float)(0.3 + 0.7 * random.NextDouble()) : 0f;
            }

            source.Fill(level);
            source.Gain = level * 1.5f;
            source.Levels = new ChannelLevels(level, level, level, level);
            view.Step(1.0 / 60);
            series.Add(FrameLuminance.Measure(RenderFrame(view, mode)));
        }

        var most = MostTransitionsPerSecond(series, 60);
        Assert.True(most <= FlashLimiter.MaxTransitions, $"{mode} ziarno {seed}: {most} przejść w sekundzie, jasność {series.Min():F3}..{series.Max():F3}");
    });

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

    /// <summary>Liczy przejścia jasności jak test ogranicznika: zmiana o ponad 5% maksimum w przeciwnym kierunku.</summary>
    private static int CountTransitions(IReadOnlyList<double> series)
    {
        var max = series.Max();
        var min = series.Min();
        if (max - min <= 0.1 * max) return 0;

        var transitions = 0;
        var mid = (max + min) / 2;
        var high = series[0] > mid;
        foreach (var value in series)
        {
            if (!high && value > mid + 0.05 * max) { high = true; transitions++; }
            else if (high && value < mid - 0.05 * max) high = false;
        }

        return transitions;
    }
}
