using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Wyłączenie logo przerywa pobrania w kolejce i w toku; po ponownym włączeniu logo znów się pobierają.</summary>
[Collection(UiCollection.Name)]
public sealed class RadioLogoCancelTests
{
    private static RadioCatalog Catalog() => new(
    [
        Station("pl-a", "Alfa FM"),
        Station("pl-b", "Beta FM"),
        Station("pl-c", "Gamma FM"),
        Station("pl-d", "Delta FM"),
        Station("pl-e", "Epsilon FM"),
    ]);

    private static RadioStation Station(string id, string name)
    {
        var station = RadioTestData.Station(id, name, "PL");
        station.Logos = [$"https://logo.example.org/{id}.png"];
        return station;
    }

    private sealed class BlockingServer
    {
        private int _started;
        private int _cancelled;
        public int Started => Volatile.Read(ref _started);
        public int Cancelled => Volatile.Read(ref _cancelled);

        public async Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }

            return new ImageFetchResult(null, ImageFetchProblem.Unavailable);
        }
    }

    [Fact]
    public void WylaczenieLogoAnulujePobraniaWKolejceIWToku() => Ui.RunAsync(async () =>
    {
        var server = new BlockingServer();
        var logos = new RadioLogoService(TestEnvironment.NewDirectory(), server.Fetch);
        using var h = new PlayerHarness(s => s.RadioPrivacyAcknowledged = true, radioCatalog: Catalog, radioLogos: logos);
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
        foreach (var row in h.Player.Radio.Rows) row.RequestLogo();

        // Logo całej listy leżą na jednym serwerze, więc naraz idzie tyle pobrań, ile wolno z jednego serwera.
        const int atOnce = RadioLogoService.MaxPerHost;
        Assert.True(await Ui.UntilAsync(() => server.Started == atOnce, 5000));

        h.Settings.Current.RadioLogos = false;
        h.Player.Radio.ApplyLogoPolicy();

        Assert.True(await Ui.UntilAsync(() => server.Cancelled >= atOnce, 5000));
        await Task.Delay(200);
        Assert.Equal(atOnce, server.Started);
        Assert.Equal(atOnce, server.Cancelled);

        h.Settings.Current.RadioLogos = true;
        h.Player.Radio.ApplyLogoPolicy();

        Assert.True(await Ui.UntilAsync(() => server.Started >= 2 * atOnce, 5000));
    });

    /// <summary>
    /// Wiersz, który nie dostał logo, pyta ponownie, gdy znów trafia na ekran. Bez tego po chwilowej
    /// awarii serwera albo starcie bez sieci lista zostawała bez logo do przebudowy.
    /// </summary>
    [Fact]
    public void WierszBezLogoPytaPonownieGdyZnowTrafiaNaEkran() => Ui.RunAsync(async () =>
    {
        var down = true;
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct) => Task.FromResult(down
            ? new ImageFetchResult(null, ImageFetchProblem.Unavailable)
            : new ImageFetchResult(TestLogos.Png(64, 64), ImageFetchProblem.None, 200));

        var logos = new RadioLogoService(TestEnvironment.NewDirectory(), Fetch, () => now);
        using var h = new PlayerHarness(s => s.RadioPrivacyAcknowledged = true, radioCatalog: Catalog, radioLogos: logos);
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
        var rows = h.Player.Radio.Rows.ToList();

        foreach (var row in rows) row.RequestLogo();
        Assert.True(await Ui.UntilAsync(() => rows.All(r => !r.LogoStarted), 5000));
        Assert.All(rows, r => Assert.Null(r.Logo));

        // Serwer wraca, a przerwa mija. Dwa adresy, których pobranie trwało w chwili awarii, mają
        // zapamiętaną porażkę; pozostałe wiersze dostają logo przy ponownym pytaniu.
        down = false;
        now += RadioLogoService.HostDownFor + TimeSpan.FromSeconds(1);
        foreach (var row in rows) row.RequestLogo();

        Assert.True(await Ui.UntilAsync(() => rows.Count(r => r.Logo is not null) >= rows.Count - RadioLogoService.MaxPerHost, 5000));
    });
}
