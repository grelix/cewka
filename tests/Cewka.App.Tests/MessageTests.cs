using Cewka.App.Localisation;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Komunikaty okna głównego: pominięte pliki, awaria wyjścia, utrata urządzenia.</summary>
[Collection(UiCollection.Name)]
public sealed class MessageTests
{
    [Fact]
    public void PominietyPlikMaNazweIPrzyczyneWJezykuInterfejsu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var missing = Path.Combine(h.Directory, "zniknal.wav");
        File.Copy(h.Track("wzor"), missing);

        h.Player.OpenPaths([missing, h.Track("b")]);

        Assert.True(h.PumpUntil(() => h.Player.HasNotice));
        var reason = Strings.Current["TrackFailureFileMissing"];
        Assert.Contains("zniknal.wav", h.Player.Notice);
        Assert.Contains(reason, h.Player.Notice);
        Assert.Equal(reason, h.Player.Queue[0].Tooltip);
        Assert.DoesNotContain("Brak pliku", h.Player.Notice);
    });

    [Fact]
    public void SeriaPominietychPlikowDajeJednoPodsumowanie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var first = Path.Combine(h.Directory, "x1.wav");
        var second = Path.Combine(h.Directory, "x2.wav");
        File.Copy(h.Track("wzor"), first);
        File.Copy(h.Track("wzor2"), second);

        h.Player.OpenPaths([first, second, h.Track("c")]);

        var summary = string.Format(Strings.Current["TracksSkipped"], 2);
        Assert.True(h.PumpUntil(() => h.Player.Notice == summary), h.Player.Notice);
    });

    [Fact]
    public void AwariaWyjsciaNiePokazujeTekstuWyjatku() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.FailAll = true;

        h.Player.OpenPaths([h.Track("a")]);

        Assert.True(h.Player.HasAudioFailure);
        Assert.Equal(Strings.Current["AudioFailureDevice"], h.Player.AudioFailure);
        Assert.DoesNotContain("formatu", h.Player.AudioFailure);
    });

    [Fact]
    public void PonownaProbaPoAwariiUruchamiaOdtwarzanie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.FailAll = true;
        h.Player.OpenPaths([h.Track("a")]);
        Assert.False(h.Player.IsPlaying);

        h.FailAll = false;
        h.Player.RetryAudio();

        Assert.False(h.Player.HasAudioFailure);
        Assert.True(h.Player.IsPlaying);
        Assert.True(h.Player.IsOutputOpen);
    });

    [Fact]
    public void WyborUrzadzeniaZKomunikatuPrzekazujeProsbeOknu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var asked = false;
        h.Player.DeviceSelectionRequested += () => asked = true;

        h.Player.RequestDeviceSelection();

        Assert.True(asked);
    });

    [Fact]
    public void UtrataUrzadzeniaZatrzymujeIInformuje() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a")]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));

        h.Outputs[^1].Lose();

        Assert.True(h.PumpUntil(() => h.Player.Notice == Strings.Current["DeviceLostNotice"]));
        Assert.False(h.Player.IsPlaying);
    });

    [Fact]
    public void OpoznieniePodaneZSeparatoremJezyka() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a")]);
        var previous = Strings.Current.CurrentCode;

        try
        {
            Strings.Current.SetLanguage("pl");
            Assert.Contains("21,3", h.Player.LatencyDescription);

            Strings.Current.SetLanguage("en");
            Assert.Contains("21.3", h.Player.LatencyDescription);
        }
        finally
        {
            Strings.Current.SetLanguage(previous);
        }
    });
}
