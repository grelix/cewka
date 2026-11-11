using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Wygląd sceny z ustawień: napisy, wielkość płyty, cykl V i efekty tła w barwach okładki.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneLookTests
{
    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public SceneView Scene => Window.Scene;

        public MainViewModel Player => Harness.Player;

        public void Dispose() => Window.Close();
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Rig Open(SceneVisual visual = SceneVisual.Disc, Action<AppSettings>? configure = null,
        EffectsMode effects = EffectsMode.Full)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.Effects = effects;
            s.SceneVisual = visual;
            s.SceneOledCare = false;
            configure?.Invoke(s);
        });
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        window.Show();
        h.OpenPaths(Enumerable.Range(0, 2).Select(i => h.Track("w" + i)));
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && !string.IsNullOrEmpty(h.Player.CurrentFilePath)));
        window.EnterScene();
        Settle();
        return new Rig { Window = window, Harness = h };
    }

    [Fact]
    public void DomyslnieScenaWygladaJakDotad() => Ui.Run(() =>
    {
        using var rig = Open();
        var scene = rig.Scene;

        Assert.True(scene.Backdrop.IsVisible);
        Assert.False(scene.TintLayer.IsVisible);
        Assert.False(scene.Particles.IsVisible);
        Assert.False(scene.ArtGlow.IsVisible);
        Assert.Equal(1, ((ScaleTransform)scene.DiscHost.RenderTransform!).ScaleX);
        Assert.Equal(VerticalAlignment.Center, scene.InfoColumn.VerticalAlignment);
        Assert.Equal(880, scene.InfoColumn.Width);
        Assert.Equal(TextAlignment.Left, scene.SceneTitle.TextAlignment);
    });

    [Fact]
    public void EfektyTlaWlaczajaSieWPlycieIOkladceAWWidmieNie() => Ui.Run(() =>
    {
        using var rig = Open();
        var scene = rig.Scene;
        var player = rig.Player;

        player.SceneGlowOn = true;
        player.SceneTintOn = true;
        player.SceneParticlesOn = true;
        player.SceneBlobsOn = false;
        Settle();

        Assert.True(scene.ArtGlow.IsVisible);
        Assert.True(scene.TintLayer.IsVisible);
        Assert.True(scene.Particles.IsVisible);
        Assert.True(scene.Particles.IsActive);
        Assert.False(scene.Backdrop.IsVisible);

        // Poświata ma środek w środku płyty (x = 160 + 320, y = 300 + 320).
        var glow = scene.ArtGlow;
        Assert.Equal(480, glow.Margin.Left + glow.Width / 2, 0.5);
        Assert.Equal(620, glow.Margin.Top + glow.Height / 2, 0.5);

        player.SceneVisual = SceneVisual.Cover;
        Settle();
        Assert.True(scene.ArtGlow.IsVisible && scene.TintLayer.IsVisible && scene.Particles.IsVisible);

        player.SceneVisual = SceneVisual.Spectrum;
        Settle();
        Assert.False(scene.ArtGlow.IsVisible);
        Assert.False(scene.TintLayer.IsVisible);
        Assert.False(scene.Particles.IsVisible);
        Assert.True(scene.Backdrop.IsVisible);
    });

    [Fact]
    public void WPauzieIPrzyOgraniczonymRuchuEfektyStoja() => Ui.Run(() =>
    {
        using var rig = Open(configure: s =>
        {
            s.SceneParticles = true;
            s.SceneGlow = true;
        });
        var scene = rig.Scene;
        Assert.True(scene.Particles.IsActive);

        rig.Player.Pause();
        Settle();
        Assert.False(scene.Particles.IsActive);
        Assert.False(scene.Particles.IsRunning);
    });

    [Fact]
    public void ZamrozenieDoZrzutuZatrzymujePoswiateIDrobinki() => Ui.Run(() =>
    {
        using var rig = Open(configure: s =>
        {
            s.SceneParticles = true;
            s.SceneGlow = true;
        });
        var scene = rig.Scene;
        Assert.True(scene.Particles.IsActive);
        Assert.True(scene.GlowRunning);

        scene.FreezeForCapture();

        Assert.False(scene.Particles.IsActive);
        Assert.False(scene.GlowRunning);
    });

    /// <summary>Siła plam z ustawień dotyczy Płyty i Okładki; Widmo zostaje przy sile dotychczasowej.</summary>
    [Fact]
    public void SilaPlamDzialaTylkoWPlycieIOkladce() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Spectrum, s => s.SceneBlobsStrength = SceneEffectStrength.Strong);
        Assert.Equal(0.25, rig.Scene.Backdrop.MinimumStrength, 3);

        rig.Player.SceneVisual = SceneVisual.Disc;
        Settle();
        Assert.Equal(0.25 * 1.5, rig.Scene.Backdrop.MinimumStrength, 3);
    });

    [Fact]
    public void SilaZmieniaLiczbeDrobinekIPlamy() => Ui.Run(() =>
    {
        using var rig = Open(configure: s => s.SceneParticles = true);
        var scene = rig.Scene;
        Assert.Equal(40, scene.Particles.ParticleCount);

        rig.Player.SceneParticlesStrength = SceneEffectStrength.Strong;
        rig.Player.SceneBlobsStrength = SceneEffectStrength.Subtle;
        Settle();
        Assert.Equal(64, scene.Particles.ParticleCount);
        Assert.Equal(0.25 * 0.6, scene.Backdrop.MinimumStrength, 3);
    });

    [Fact]
    public void DrobinkiUnosaSieIWracajaDolem()
    {
        var particles = new SceneParticles { Count = 3 };
        for (var i = 0; i < 2000; i++) particles.Step(0.1);
        Assert.Equal(3, particles.ParticleCount);
    }

    [Theory]
    [InlineData(SceneArtSize.Small, 0.85)]
    [InlineData(SceneArtSize.Normal, 1.0)]
    [InlineData(SceneArtSize.Large, 1.1)]
    public void WielkoscPlytyIOkladkiSkalujeWokolSrodka(SceneArtSize size, double scale) => Ui.Run(() =>
    {
        using var rig = Open(configure: s => s.SceneArtSize = size);
        var scene = rig.Scene;

        Assert.Equal(scale, ((ScaleTransform)scene.DiscHost.RenderTransform!).ScaleX, 3);
        Assert.Equal(scale, ((ScaleTransform)scene.RadioStageHost.RenderTransform!).ScaleY, 3);
        Assert.Equal(scale, ((TransformGroup)scene.CoverArtHost.RenderTransform!).Children.OfType<ScaleTransform>().First().ScaleX, 3);

        // Powiększona płyta nie wchodzi na kolumnę napisów (x = 920).
        var right = 160 + 320 + 320 * scale;
        Assert.True(right < 920, $"płyta do {right}");
    });

    [Fact]
    public void NapisyUDoluPrzenoszaKarteNastepnyIKoniecKolejkiNaGore() => Ui.Run(() =>
    {
        using var rig = Open(configure: s => s.SceneTextPosition = SceneTextPosition.Bottom);
        var scene = rig.Scene;

        Assert.Equal(VerticalAlignment.Bottom, scene.InfoColumn.VerticalAlignment);
        Assert.Equal(VerticalAlignment.Top, scene.NextCard.VerticalAlignment);
        Assert.Equal(VerticalAlignment.Top, scene.QueueEndBlock.VerticalAlignment);

        rig.Player.SceneTextPosition = SceneTextPosition.Top;
        Settle();
        Assert.Equal(VerticalAlignment.Top, scene.InfoColumn.VerticalAlignment);
        Assert.Equal(VerticalAlignment.Bottom, scene.NextCard.VerticalAlignment);
    });

    [Fact]
    public void WyrownanieIMarginesyNapisow() => Ui.Run(() =>
    {
        using var rig = Open();
        var scene = rig.Scene;

        rig.Player.SceneTextCentred = true;
        rig.Player.SceneTextInset = SceneTextInset.Wide;
        Settle();

        Assert.Equal(TextAlignment.Center, scene.SceneTitle.TextAlignment);
        Assert.Equal(TextAlignment.Center, scene.SceneArtist.TextAlignment);
        Assert.Equal(760, scene.InfoColumn.Width);
        Assert.Equal(980, scene.InfoColumn.Margin.Left);
    });

    [Fact]
    public void KlawiszVPomijaWylaczoneWizualizacjeAPlytyNieDaSieWylaczyc() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var player = h.Player;

        player.SetInCycle(SceneVisual.Cover, false);
        player.SetInCycle(SceneVisual.Disc, false);
        Assert.True(player.IsInCycle(SceneVisual.Disc));
        Assert.False(player.IsInCycle(SceneVisual.Cover));

        player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Night, player.SceneVisual);
        player.CycleSceneVisual(-1);
        Assert.Equal(SceneVisual.Disc, player.SceneVisual);
        Assert.Equal([SceneVisual.Cover], h.Settings.Current.SceneCycleOff);
    });

    [Fact]
    public void UstawieniaSceny_PrzelacznikiCykluIEfektowSterujaOdtwarzaczem() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);

        Assert.Equal(6, settings.SceneCycleOptions.Count);
        Assert.DoesNotContain(settings.SceneCycleOptions, o => o.Visual == SceneVisual.Disc);
        settings.SceneCycleOptions.Single(o => o.Visual == SceneVisual.Waveform).IsOn = false;
        Assert.False(h.Player.IsInCycle(SceneVisual.Waveform));

        Assert.Equal(["Blobs", "Glow", "Tint", "Particles"], settings.SceneEffectOptions.Select(o => o.Key));
        var glow = settings.SceneEffectOptions.Single(o => o.Key == "Glow");
        Assert.False(glow.IsOn);
        glow.IsOn = true;
        glow.Strength.Choose(glow.Strength.Options.Single(o => Equals(o.Value, SceneEffectStrength.Strong)));
        Assert.True(h.Player.SceneGlowOn);
        Assert.Equal(SceneEffectStrength.Strong, h.Player.SceneGlowStrength);

        settings.SceneArtSizeChoice.Choose(settings.SceneArtSizeChoice.Options.Single(o => Equals(o.Value, SceneArtSize.Large)));
        settings.SceneTextAlignChoice.Choose(settings.SceneTextAlignChoice.Options.Single(o => Equals(o.Value, true)));
        Assert.Equal(SceneArtSize.Large, h.Player.SceneArtSize);
        Assert.True(h.Player.SceneTextCentred);
    });
}
