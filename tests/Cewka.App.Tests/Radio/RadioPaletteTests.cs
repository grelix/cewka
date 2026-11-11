using Avalonia.Media;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Tło okna w trybie radia bierze barwy z logo stacji, a nie z poprzedniego stanu.</summary>
[Collection(UiCollection.Name)]
public sealed class RadioPaletteTests
{
    private const string LogoRed = "https://logo.example.org/czerwone.png";
    private const string LogoBlue = "https://logo.example.org/niebieskie.png";

    private static RadioCatalog Catalog()
    {
        RadioStation Make(string id, string name, string? logo)
        {
            var station = RadioTestData.Station(id, name, "PL");
            if (logo is not null) station.Logos = [logo];
            return station;
        }

        return new RadioCatalog(
        [
            Make("red", "Czerwone FM", LogoRed),
            Make("blue", "Niebieskie FM", LogoBlue),
            Make("none", "Bez logo FM", null),
        ], null);
    }

    /// <summary>Serwer logo, w którym wybrany adres czeka na zwolnienie.</summary>
    private sealed class GatedServer
    {
        public readonly Dictionary<string, TaskCompletionSource> Gates = [];

        public async Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            if (Gates.TryGetValue(url.AbsoluteUri, out var gate))
                await gate.Task.WaitAsync(ct);

            var argb = url.AbsoluteUri == LogoRed ? 0xFFCC2222u : 0xFF2233CCu;
            return new ImageFetchResult(TestLogos.Png(64, 64, argb), ImageFetchProblem.None, 200);
        }
    }

    private static (PlayerHarness Harness, GatedServer Server) Create()
    {
        var server = new GatedServer();
        var logos = new RadioLogoService(TestEnvironment.NewDirectory(), server.Fetch);
        var h = new PlayerHarness(s => s.RadioPrivacyAcknowledged = true, radioCatalog: Catalog, radioLogos: logos);
        h.Player.SetSource(SourceMode.Radio);
        return (h, server);
    }

    private static RadioStation Station(PlayerHarness h, string id) =>
        h.Player.Radio.Rows.First(r => r.Station.Id == id).Station;

    private static bool IsReddish(Color c) => c.ToHsl().H is < 25 or > 335;

    private static bool IsBluish(Color c) => c.ToHsl().H is > 205 and < 245;

    private static bool IsDefault(IReadOnlyList<Color> palette) =>
        palette.SequenceEqual(CoverPalette.Extract(null, App.Theme.IsDark));

    [Fact]
    public void LogoStacjiBarwiTloOkna() => Ui.RunAsync(async () =>
    {
        var (h, _) = Create();
        using var _ = h;

        h.Player.Radio.Play(Station(h, "red"));

        Assert.True(await Ui.UntilAsync(() => h.Player.Radio.HasStageLogo));
        Assert.Contains(h.Player.Palette, IsReddish);
        Assert.DoesNotContain(h.Player.Palette, IsBluish);
    });

    [Fact]
    public void StacjaBezLogoMaPaleteDomyslnaNiePoPoprzedniej() => Ui.RunAsync(async () =>
    {
        var (h, _) = Create();
        using var _ = h;
        h.Player.Radio.Play(Station(h, "red"));
        Assert.True(await Ui.UntilAsync(() => h.Player.Radio.HasStageLogo));

        h.Player.Radio.Play(Station(h, "none"));

        Assert.True(await Ui.UntilAsync(() => !h.Player.Radio.HasStageLogo));
        Assert.True(IsDefault(h.Player.Palette));
    });

    [Fact]
    public void ZmianaStacjiZOpoznionymLogoNieZostawiaBarwPoprzedniej() => Ui.RunAsync(async () =>
    {
        var (h, server) = Create();
        using var _ = h;
        h.Player.Radio.Play(Station(h, "red"));
        Assert.True(await Ui.UntilAsync(() => h.Player.Radio.HasStageLogo));
        server.Gates[LogoBlue] = new TaskCompletionSource();

        h.Player.Radio.Play(Station(h, "blue"));
        await Task.Delay(150);

        Assert.DoesNotContain(h.Player.Palette, IsReddish);

        server.Gates[LogoBlue].SetResult();
        Assert.True(await Ui.UntilAsync(() => h.Player.Palette.Any(IsBluish)));
    });

    [Fact]
    public void SpoznioneLogoStarszejStacjiNieNadpisujePaletyNowszej() => Ui.RunAsync(async () =>
    {
        var (h, server) = Create();
        using var _ = h;
        server.Gates[LogoRed] = new TaskCompletionSource();

        h.Player.Radio.Play(Station(h, "red"));
        h.Player.Radio.Play(Station(h, "blue"));
        Assert.True(await Ui.UntilAsync(() => h.Player.Palette.Any(IsBluish)));

        server.Gates[LogoRed].SetResult();
        await Task.Delay(300);

        Assert.Contains(h.Player.Palette, IsBluish);
        Assert.DoesNotContain(h.Player.Palette, IsReddish);
    });

    [Fact]
    public void PowrotDoPlikowPrzywracaPaleteOkladkiPliku() => Ui.RunAsync(async () =>
    {
        var (h, _) = Create();
        using var _ = h;
        h.Player.SetSource(SourceMode.Files);
        var files = h.Player.Palette.ToArray();
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.Play(Station(h, "red"));
        Assert.True(await Ui.UntilAsync(() => h.Player.Palette.Any(IsReddish)));

        h.Player.SetSource(SourceMode.Files);

        Assert.Equal(files, h.Player.Palette.ToArray());
    });
}
