using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

[Collection(UiCollection.Name)]
public sealed class MotionTests
{
    [Fact]
    public void TrybAutomatycznyOgraniczaRuchNaProsbeSystemu()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness(s => s.Effects = EffectsMode.Auto);

            h.Player.AutomaticReduction = () => true;
            h.Player.UpdateEffects();
            Assert.True(h.Player.ReducedEffects);

            h.Player.AutomaticReduction = () => false;
            h.Player.UpdateEffects();
            Assert.False(h.Player.ReducedEffects);
        });
    }

    [Fact]
    public void TrybyJawneNiezalezneOdSystemu()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness(s => s.Effects = EffectsMode.Full);
            h.Player.AutomaticReduction = () => true;

            h.Player.UpdateEffects();
            Assert.False(h.Player.ReducedEffects);

            h.Player.SetEffects(EffectsMode.Reduced);
            Assert.True(h.Player.ReducedEffects);
        });
    }

    [Fact]
    public void OdczytWTleOddajeOstatniaZnanaWartoscBezCzekania()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var queued = new List<Action>();
        bool? answer = false;
        var reads = 0;

        var value = new PolledValue(true, () => { reads++; return answer; }, TimeSpan.FromSeconds(5),
                                    () => now, queued.Add);

        // Pierwsze pytanie zleca odczyt i od razu oddaje wartość początkową.
        Assert.True(value.Value);
        Assert.Single(queued);

        // Odczyt w toku nie zleca drugiego.
        Assert.True(value.Value);
        Assert.Single(queued);

        queued[0]();
        Assert.False(value.Value);
        Assert.Single(queued);

        // Kolejny odczyt dopiero po upływie odstępu.
        now = now.AddSeconds(5);
        answer = null;
        Assert.False(value.Value);
        Assert.Equal(2, queued.Count);

        // Brak odpowiedzi zostawia ostatnią znaną wartość.
        queued[1]();
        Assert.False(value.Value);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void BladOdczytuZostawiaWartoscIPozwalaPonowic()
    {
        var now = DateTime.UtcNow;
        var queued = new List<Action>();
        var value = new PolledValue(false, () => throw new DllNotFoundException(), TimeSpan.FromSeconds(5),
                                    () => now, queued.Add);

        Assert.False(value.Value);
        queued[0]();
        Assert.False(value.Value);

        now = now.AddSeconds(6);
        _ = value.Value;
        Assert.Equal(2, queued.Count);
    }

    [Fact]
    public void FalaStajeWPauzieIRuszaPrzyOdtwarzaniu()
    {
        Ui.Run(() =>
        {
            var wave = new WaveformView { IsActive = false, Stroke = Brushes.White, Width = 300, Height = 40 };
            var window = new Window { Width = 320, Height = 60, Content = wave };
            window.Show();

            try
            {
                Tick(200);
                Assert.False(wave.IsLooping);

                wave.IsActive = true;
                Assert.True(wave.IsLooping);

                wave.IsReduced = true;
                Tick(200);
                Assert.False(wave.IsLooping);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TloWPauzieNiePrzerysowujeSieBezKonca()
    {
        Ui.Run(() =>
        {
            var backdrop = new LiveBackdrop { IsActive = false, BaseBrush = Brushes.Black };
            var window = new Window { Width = 320, Height = 200, Content = backdrop };
            window.Show();

            try
            {
                Tick(50);
                Assert.False(backdrop.IsLooping);

                backdrop.IsActive = true;
                Assert.True(backdrop.IsLooping);

                backdrop.IsReduced = true;
                Tick(400);
                Assert.False(backdrop.IsLooping);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PetlaCzekaPrzyZminimalizowanymOknie()
    {
        Ui.Run(() =>
        {
            var frames = 0;
            var host = new Border();
            var window = new Window { Width = 100, Height = 100, Content = host };
            window.Show();

            try
            {
                var loop = new RenderLoop(host, _ => frames++);
                window.WindowState = WindowState.Minimized;
                loop.Start();

                Tick(20);
                Assert.True(loop.IsParked);
                Assert.Equal(0, frames);

                window.WindowState = WindowState.Normal;
                Tick(5);
                Assert.False(loop.IsParked);
                Assert.True(frames > 0);

                loop.Stop();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PlytaKreciSieTylkoPrzyGraniuIWracaDoPionuWTrybieOgraniczonym()
    {
        Ui.Run(() =>
        {
            var disc = new VinylDisc();
            var spinner = new DiscSpinner(disc);

            spinner.Update(playing: true, reduced: false);
            spinner.Advance(0.25);
            spinner.Advance(0.2);

            // Obrót na dziewięć sekund: 40° na sekundę.
            Assert.InRange(disc.Angle, 17.99, 18.01);

            // Ograniczenie efektów: płyta wytraca obrót i staje okładką prosto, zawsze w przód.
            spinner.Update(playing: true, reduced: true);
            var before = disc.Angle;
            spinner.Advance(0.1);
            Assert.True(disc.Angle > before, $"{disc.Angle}");
            for (var i = 0; i < 10; i++) spinner.Advance(0.1);
            Assert.Equal(0, disc.Angle);

            // W pauzie płyta stoi tam, gdzie była.
            spinner.Update(playing: false, reduced: false);
            spinner.Advance(0.2);
            Assert.Equal(0, disc.Angle);
        });
    }

    [Fact]
    public void PlytaObracaSieRownymKrokiemBezSkokuNaPelnymObrocie()
    {
        Ui.Run(() =>
        {
            var disc = new VinylDisc();
            var spinner = new DiscSpinner(disc);

            spinner.Update(playing: true, reduced: false);
            spinner.Advance(0.25);
            spinner.Advance(0.2);
            Assert.InRange(disc.Angle, 17.99, 18.01);

            // Przez granicę pełnego obrotu kąt idzie równym krokiem i zostaje w przedziale [0, 360).
            var previous = disc.Angle;
            var wrapped = false;
            for (var i = 0; i < 200; i++)
            {
                spinner.Advance(0.05);
                Assert.InRange((disc.Angle - previous + 360) % 360, 1.99, 2.01);
                Assert.True(disc.Angle is >= 0 and < 360, $"{disc.Angle}");
                wrapped |= disc.Angle < previous;
                previous = disc.Angle;
            }

            Assert.True(wrapped);
            Assert.InRange(disc.Angle, 57.99, 58.01);

            // Powrót do pionu: w przód i do zera.
            spinner.Update(playing: true, reduced: true);
            spinner.Advance(0.1);
            Assert.True(disc.Angle > 58, $"{disc.Angle}");
            for (var i = 0; i < 10; i++) spinner.Advance(0.1);
            Assert.Equal(0, disc.Angle);
        });
    }

    [Fact]
    public void PetlaNieZamawiaKlatekDlaUkrytegoWlasciciela()
    {
        Ui.Run(() =>
        {
            var requests = 0;
            var host = new Border();
            var cover = new Panel { Children = { host } };
            var window = new Window { Width = 100, Height = 100, Content = cover };
            window.Show();

            void Count(Avalonia.Visual owner)
            {
                if (ReferenceEquals(owner, host)) requests++;
            }

            RenderLoop.FrameRequested += Count;
            var loop = new RenderLoop(host, _ => host.InvalidateVisual());

            try
            {
                cover.IsVisible = false;
                loop.Start();
                Tick(10);
                Assert.True(loop.IsParked);
                Assert.Equal(0, requests);

                // Odsłonięcie przodka budzi pętlę bez udziału właściciela.
                cover.IsVisible = true;
                window.UpdateLayout();
                Tick(3);
                Assert.False(loop.IsParked);
                Assert.True(requests > 0);
            }
            finally
            {
                loop.Stop();
                RenderLoop.FrameRequested -= Count;
                window.Close();
            }
        });
    }

    [Fact]
    public void TloZCzasemWygaszaniaStajeWSekundePoPauzie()
    {
        Ui.Run(() =>
        {
            var backdrop = new LiveBackdrop { IsActive = true, StopSeconds = 1, Level = 0.8 };
            for (var i = 0; i < 100; i++) backdrop.Advance(0.05);

            backdrop.IsActive = false;
            var stopped = backdrop.DriftTime;
            for (var i = 0; i < 20; i++) backdrop.Advance(0.05);
            var after = backdrop.DriftTime;

            // Po sekundzie dryf już się nie posuwa.
            backdrop.Advance(0.05);
            Assert.Equal(after, backdrop.DriftTime);
            Assert.True(after > stopped);
        });
    }

    [Fact]
    public void TempoTlaSpowalniaDryf()
    {
        Ui.Run(() =>
        {
            var full = new LiveBackdrop { IsActive = true };
            var half = new LiveBackdrop { IsActive = true, Tempo = 0.5 };

            for (var i = 0; i < 200; i++)
            {
                full.Advance(0.05);
                half.Advance(0.05);
            }

            Assert.InRange(half.DriftTime / full.DriftTime, 0.49, 0.51);
        });
    }

    private static void Tick(int count)
    {
        for (var i = 0; i < count; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }
}
