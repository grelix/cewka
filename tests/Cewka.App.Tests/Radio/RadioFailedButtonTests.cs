using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Po błędzie stacji główny przycisk pokazuje „Odtwórz”, a jego naciśnięcie ponawia stację.</summary>
[Collection(UiCollection.Name)]
public sealed class RadioFailedButtonTests
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

    [Fact]
    public void PoBleduStacjiPrzyciskPokazujeOdtworzIPonawia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(radioCatalog: Catalog);
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
        h.Player.Radio.Play(h.Player.Radio.Rows.First(r => r.Station.Id == "pl-a").Station);
        h.Player.SyncPlayingState();
        Assert.True(h.Player.ShowsStopIcon);
        var opened = h.Sessions.Count;

        h.Player.Radio.OnLiveFailed(h.Sessions[0].Source, new LiveFailure(LiveFailureReason.Stalled));

        Assert.False(h.Player.IsPlaying);
        Assert.False(h.Player.ShowsStopIcon);
        Assert.True(h.Player.IsPaused);
        Assert.Equal(Strings.Current["TooltipPlay"], h.Player.PlayPauseTooltip);

        h.Player.TogglePlay();

        Assert.Equal(opened + 1, h.Sessions.Count);
        Assert.True(h.Player.IsPlaying);
        Assert.True(h.Player.ShowsStopIcon);
    });
}
