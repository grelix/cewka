using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Opóźniony krok Wstecz i Dalej nie przeżywa zmiany źródła, wyłączenia radia ani Stop.</summary>
[Collection(UiCollection.Name)]
public sealed class RadioPendingStepTests
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

    private sealed class ManualScheduler
    {
        private readonly List<(Action Action, bool[] Cancelled)> _items = [];

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var cancelled = new bool[1];
            _items.Add((action, cancelled));
            return new Cancel(cancelled);
        }

        public void RunPending()
        {
            foreach (var (action, cancelled) in _items.ToList())
            {
                if (cancelled[0]) continue;
                cancelled[0] = true;
                action();
            }
        }

        private sealed class Cancel(bool[] flag) : IDisposable
        {
            public void Dispose() => flag[0] = true;
        }
    }

    private static PlayerHarness Start(ManualScheduler scheduler)
    {
        var h = new PlayerHarness(radioCatalog: Catalog);
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
        h.Player.Radio.StepDelay = RadioViewModel.DefaultStepDelay;
        h.Player.Radio.Scheduler = scheduler.Schedule;
        h.Player.Radio.Play(h.Player.Radio.Rows.First(r => r.Station.Id == "pl-a").Station);
        h.Player.SyncPlayingState();
        return h;
    }

    [Fact]
    public void PrzelaczenieNaPlikiAnulujeOczekujacyKrok() => Ui.Run(() =>
    {
        var scheduler = new ManualScheduler();
        using var h = Start(scheduler);
        var opened = h.Sessions.Count;

        h.Player.Next();
        h.Player.SetSource(SourceMode.Files);
        scheduler.RunPending();

        Assert.False(h.Player.Radio.HasPendingStep);
        Assert.Equal(opened, h.Sessions.Count);
        Assert.False(h.Engine.IsLive);
    });

    [Fact]
    public void WylaczenieRadiaAnulujeOczekujacyKrok() => Ui.Run(() =>
    {
        var scheduler = new ManualScheduler();
        using var h = Start(scheduler);
        var opened = h.Sessions.Count;

        h.Player.Next();
        h.Player.SetRadioEnabled(false);
        scheduler.RunPending();

        Assert.Equal(opened, h.Sessions.Count);
        Assert.False(h.Engine.IsLive);
    });

    [Fact]
    public void StopAnulujeOczekujacyKrok() => Ui.Run(() =>
    {
        var scheduler = new ManualScheduler();
        using var h = Start(scheduler);
        var opened = h.Sessions.Count;

        h.Player.Next();
        h.Player.TogglePlay();
        scheduler.RunPending();

        Assert.False(h.Player.Radio.HasPendingStep);
        Assert.Equal(opened, h.Sessions.Count);
        Assert.False(h.Player.IsPlaying);
    });

    [Fact]
    public void PlayNieRusza_GdyRadioJestWylaczone() => Ui.Run(() =>
    {
        var scheduler = new ManualScheduler();
        using var h = Start(scheduler);
        var opened = h.Sessions.Count;

        h.Player.Next();
        h.Settings.Current.RadioEnabled = false;
        scheduler.RunPending();

        Assert.Equal(opened, h.Sessions.Count);
        Assert.False(h.Player.Radio.Play(h.Player.Radio.Rows.First(r => r.Station.Id == "pl-b").Station));
        Assert.Equal(opened, h.Sessions.Count);
    });
}
