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
        Assert.True(await Ui.UntilAsync(() => server.Started == 3, 5000));

        h.Settings.Current.RadioLogos = false;
        h.Player.Radio.ApplyLogoPolicy();

        Assert.True(await Ui.UntilAsync(() => server.Cancelled >= 3, 5000));
        await Task.Delay(200);
        Assert.Equal(3, server.Started);
        Assert.Equal(3, server.Cancelled);

        h.Settings.Current.RadioLogos = true;
        h.Player.Radio.ApplyLogoPolicy();

        Assert.True(await Ui.UntilAsync(() => server.Started >= 6, 5000));
    });
}
