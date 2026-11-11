using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Atrapa danych dźwięku: stałe widmo, poziomy i przebieg sinusa o zadanej częstotliwości.</summary>
internal sealed class FakeSceneAudio : ISceneAudioSource
{
    public float[] Spectrum { get; } = new float[SpectrumBars.Count];

    public ChannelLevels Levels { get; set; }

    public double Frequency { get; set; } = 441;

    public double PhaseFrames { get; set; }

    public bool Mono { get; set; } = true;

    /// <summary>Mnożnik amplitudy przebiegu; 0 to cisza.</summary>
    public float Gain { get; set; } = 1;

    public int SampleRate => 44100;

    public bool ReadWaveform(Span<float> left, Span<float> right)
    {
        for (var i = 0; i < left.Length; i++)
        {
            var value = (float)(0.5 * Math.Sin(2 * Math.PI * Frequency * (PhaseFrames + i) / SampleRate));
            left[i] = value;
            right[i] = Mono ? value : (float)(0.5 * Math.Sin(2 * Math.PI * Frequency * (PhaseFrames + i) / SampleRate + 1.1) * 0.8 + 0.1 * Math.Sin(2 * Math.PI * Frequency * 3 * (PhaseFrames + i) / SampleRate));
        }

        if (Gain != 1)
        {
            for (var i = 0; i < left.Length; i++)
            {
                left[i] *= Gain;
                right[i] *= Gain;
            }
        }

        return true;
    }

    public ChannelLevels ReadLevels(int frames) => Levels;

    public ReadOnlySpan<float> ReadSpectrum() => Spectrum;

    public void Fill(float amplitude) => Array.Fill(Spectrum, amplitude);
}

[Collection(UiCollection.Name)]
public sealed class SceneAudioVisualsTests
{
    [Fact]
    public void PodniesienieSlupkowZachowujeProporcjeINiePrzekraczaPelnejWysokosci()
    {
        var bars = new SpectrumBars();
        var levels = new float[SpectrumBars.Count];
        Array.Fill(levels, 0.1f);
        levels[0] = 1f;
        bars.Update(levels, 1);
        var low = bars.Bars[1];

        bars.Lift(1.5f);

        Assert.Equal(1f, bars.Bars[0]);
        Assert.Equal(low * 1.5f, bars.Bars[1], 3);
    }

    [Fact]
    public void SlupkiNarastajaIOpadajaWedlugBalistykiASzczytyStoja()
    {
        var bars = new SpectrumBars();
        var loud = new float[SpectrumBars.Count];
        Array.Fill(loud, 0.5f);
        var silence = new float[SpectrumBars.Count];

        bars.Update(loud, 0.02);
        Assert.Equal(0.5f, bars.Bars[0], 2);
        bars.Update(loud, 0.02);
        Assert.Equal(1f, bars.Bars[0], 2);

        // Opadanie od pełnej skali trwa 300 ms: po 150 ms słupek stoi w połowie.
        for (var i = 0; i < 15; i++) bars.Update(silence, 0.01);
        Assert.Equal(0.5f, bars.Bars[0], 1);

        // Szczyt stoi 0,8 s od ostatniego wzrostu, potem opada.
        for (var i = 0; i < 25; i++) bars.Update(silence, 0.02);
        Assert.Equal(1f, bars.Peaks[0], 2);
        for (var i = 0; i < 20; i++) bars.Update(silence, 0.02);
        Assert.True(bars.Peaks[0] < 0.95f);
    }

    [Fact]
    public void CichyZapisNieJestMartwyDziekiNormalizacjiZakresu()
    {
        var bars = new SpectrumBars();
        var quiet = new float[SpectrumBars.Count];
        Array.Fill(quiet, 0.001f);

        for (var i = 0; i < 300; i++) bars.Update(quiet, 1.0 / 60);

        Assert.InRange(bars.Bars[10], 0.4f, 1f);
    }

    [Fact]
    public void WyzwalanieDajeTenSamObrazSinusaPrzyKazdymPrzesunieciu()
    {
        float[] Render(double offset)
        {
            var samples = new float[3000];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (float)(0.6 * Math.Sin(2 * Math.PI * 441 * (offset + i) / 44100));

            var start = ScopeTrigger.Find(samples, 1024);
            var picture = new float[200];
            for (var k = 0; k < picture.Length; k++) picture[k] = ScopeTrigger.Sample(samples, start + k);
            return picture;
        }

        var reference = Render(0);
        foreach (var offset in new[] { 17.0, 41.5, 77.25, 1234.0 })
        {
            var other = Render(offset);
            for (var k = 0; k < reference.Length; k++) Assert.InRange(Math.Abs(reference[k] - other[k]), 0, 0.01);
        }
    }

    [Fact]
    public void RegulacjaWzmocnieniaJestWolnaIOgraniczona()
    {
        var trigger = new ScopeTrigger();
        trigger.Adapt(0.1f, 0.016);
        Assert.InRange(trigger.Gain, 1.0, 1.1);

        for (var i = 0; i < 2000; i++) trigger.Adapt(0.1f, 0.016);
        Assert.InRange(trigger.Gain, 7.5, ScopeTrigger.MaxGain);
    }

    [Fact]
    public void NasyconaCzerwienJestOgraniczonaDoSzescdziesieciuProcent()
    {
        var limited = SceneAudioView.LimitRed(Avalonia.Media.Color.FromRgb(255, 0, 0));
        Assert.True(limited.ToHsv().S <= 0.601);

        var blue = Avalonia.Media.Color.FromRgb(20, 60, 255);
        Assert.Equal(blue, SceneAudioView.LimitRed(blue));
    }

    private static SceneAudioView Prepare(SceneAudioMode mode, FakeSceneAudio source, WaveformKind kind = WaveformKind.Line)
    {
        var view = new SceneAudioView { Source = source, Kind = kind };
        view.Mode = mode;
        view.SetColours(Avalonia.Media.Color.FromRgb(80, 130, 220), Avalonia.Media.Color.FromRgb(240, 180, 90));
        return view;
    }

    [Theory]
    [InlineData(SceneAudioMode.Spectrum)]
    [InlineData(SceneAudioMode.Ring)]
    [InlineData(SceneAudioMode.Waveform)]
    [InlineData(SceneAudioMode.Horizon)]
    public void OgranicznikBlyskowDzialaWKazdejWizualizacjiReagujacejNaDzwiek(SceneAudioMode mode) => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        var view = Prepare(mode, source);

        // Impulsy basu 3 Hz: pół okresu pełnej skali, pół ciszy.
        var series = new List<double>();
        for (var frame = 0; frame < 240; frame++)
        {
            var on = (frame / 10) % 2 == 0;
            source.Fill(on ? 1f : 0f);
            source.Levels = on ? new ChannelLevels(1, 1, 1, 1) : default;
            view.Step(1.0 / 60);
            series.Add(view.Limiter.Value);
            Assert.InRange(view.LimiterGain, 0, 1);
        }

        Assert.Equal(240, view.FramesRendered);

        var max = series.Max();
        var min = series.Min();
        var transitions = 0;
        if (max - min > 0.1 * max)
        {
            var mid = (max + min) / 2;
            var high = series[0] > mid;
            foreach (var value in series)
            {
                if (!high && value > mid + 0.05 * max) { high = true; transitions++; }
                else if (high && value < mid - 0.05 * max) high = false;
            }
        }

        // Cztery sekundy, najwyżej 3 przejścia na sekundę.
        Assert.True(transitions <= 12, $"{mode}: {transitions} przejść w 4 s");
    });

    [Theory]
    [InlineData(SceneAudioMode.Spectrum)]
    [InlineData(SceneAudioMode.Ring)]
    [InlineData(SceneAudioMode.Waveform)]
    [InlineData(SceneAudioMode.Horizon)]
    public void KlatkaNieAlokujePamieci(SceneAudioMode mode) => Ui.Run(() =>
    {
        var source = new FakeSceneAudio { Levels = new ChannelLevels(0.1f, 0.1f, 0.3f, 0.3f) };
        source.Fill(0.2f);
        var view = Prepare(mode, source);
        for (var i = 0; i < 50; i++) view.Step(1.0 / 60);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++) view.Step(1.0 / 60);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4096, $"{mode}: {allocated} B na 200 klatek");
    });

    [Fact]
    public void FalaStereoWykrywaNagranieMono() => Ui.Run(() =>
    {
        var source = new FakeSceneAudio { Mono = true };
        var view = Prepare(SceneAudioMode.Waveform, source, WaveformKind.Stereo);
        var reports = new List<bool>();
        view.MonoChanged += reports.Add;

        view.Step(1.0 / 60);
        Assert.True(view.IsMono);

        source.Mono = false;
        view.Step(1.0 / 60);
        Assert.False(view.IsMono);
        Assert.Equal([true, false], reports);
    });

    [Fact]
    public void PauzaNieZamawiaKlatek() => Ui.Run(() =>
    {
        var source = new FakeSceneAudio();
        source.Fill(0.2f);
        var view = Prepare(SceneAudioMode.Spectrum, source);
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
            Assert.True(requests > 0, "wizualizacja nie zamawiała klatek przy graniu");

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

    [Fact]
    public void WizualizacjeSaWKolejnosciWUstawieniuIPrzechodzaPrzezV() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var player = h.Player;
        var order = new List<SceneVisual>();
        for (var i = 0; i < 6; i++)
        {
            order.Add(player.SceneVisual);
            player.CycleSceneVisual(1);
        }

        Assert.Equal(
            [SceneVisual.Disc, SceneVisual.Cover, SceneVisual.Night, SceneVisual.Spectrum, SceneVisual.Waveform, SceneVisual.Horizon],
            order);
        Assert.Equal(SceneVisual.Disc, player.SceneVisual);

        player.CycleSceneVisual(-1);
        Assert.Equal(SceneVisual.Horizon, player.SceneVisual);
        Assert.Equal(SceneVisual.Horizon, h.Settings.Current.SceneVisual);
    });

    [Fact]
    public void WariantyIPierscienSaZapisywaneWUstawieniach() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var player = h.Player;
        Assert.Equal(SpectrumShape.Bars, player.SceneSpectrumShape);
        Assert.Equal(WaveformKind.Line, player.SceneWaveformKind);
        Assert.False(player.SceneDiscRing);

        player.SceneSpectrumShape = SpectrumShape.Curve;
        player.SceneWaveformKind = WaveformKind.Stereo;
        player.SceneDiscRing = true;

        Assert.Equal(SpectrumShape.Curve, h.Settings.Current.SpectrumShape);
        Assert.Equal(WaveformKind.Stereo, h.Settings.Current.WaveformKind);
        Assert.True(h.Settings.Current.DiscRing);
    });

    [Fact]
    public void TrybOgraniczonyPomijaWizualizacjeReagujaceNaDzwiek() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.Effects = EffectsMode.Reduced);
        var player = h.Player;
        Assert.True(player.ReducedEffects);

        var order = new List<SceneVisual>();
        for (var i = 0; i < 4; i++)
        {
            player.CycleSceneVisual(1);
            order.Add(player.SceneVisual);
        }

        Assert.Equal([SceneVisual.Cover, SceneVisual.Night, SceneVisual.Horizon, SceneVisual.Disc], order);

        // Zapamiętany wybór zostaje, ale rysowana jest Płyta.
        player.SceneVisual = SceneVisual.Spectrum;
        Assert.Equal(SceneVisual.Spectrum, player.SceneVisual);
        Assert.Equal(SceneVisual.Disc, player.ActiveSceneVisual);
    });
}
