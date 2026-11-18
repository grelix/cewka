using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Osobne ustawienia korektora i efektów dla urządzeń wyjściowych: przełączanie urządzeń,
/// dziedziczenie przy pierwszym użyciu, zapis i odczyt, limit profili.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SoundProfileTests
{
    private const string Headphones = "Słuchawki";

    [Fact]
    public void DomyslnieWylaczone() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();

        Assert.False(h.Player.SoundProfilesEnabled);
        Assert.False(h.Player.ShowSoundProfileDevice);
        Assert.Empty(h.Settings.Current.SoundProfiles);
    });

    /// <summary>Włączenie przy grającym urządzeniu: bieżące wartości stają się jego profilem.</summary>
    [Fact]
    public void WlaczenieZapisujeProfilBiezacegoUrzadzenia() => Ui.Run(() =>
    {
        using var h = Playing();
        h.Player.Bands[4].Gain = 3;

        h.Player.SoundProfilesEnabled = true;

        var profile = Assert.Single(h.Settings.Current.SoundProfiles);
        Assert.Equal(PlayerHarness.DefaultDevice, profile.Device);
        Assert.Equal(3, profile.EqualiserGains[4]);
        Assert.True(h.Player.ShowSoundProfileDevice);
        Assert.Equal(string.Format(Strings.Current["SoundProfileDevice"], PlayerHarness.DefaultDevice), h.Player.SoundProfileDeviceText);
    });

    /// <summary>
    /// Urządzenie widziane pierwszy raz dziedziczy bieżące wartości; powrót na poprzednie przywraca
    /// jego wartości razem z efektami i korektorem parametrycznym, w modelu i w torze.
    /// </summary>
    [Fact]
    public void PrzelaczanieUrzadzenPrzywracaIchUstawienia() => Ui.Run(() =>
    {
        using var h = Playing();
        h.Player.SoundProfilesEnabled = true;

        h.Player.Bands[0].Gain = 6;
        h.Player.SoundEffects[0].Enabled = true;
        h.Player.SoundEffects[0].Strength = 0.8;

        h.Player.SetOutputDevice(Headphones);
        Assert.Equal(Headphones, h.Engine.DeviceName);

        // Pierwszy raz: nic się nie zmienia skokowo.
        Assert.Equal(6, h.Player.Bands[0].Gain);
        Assert.True(h.Player.SoundEffects[0].Enabled);
        Assert.Equal(string.Format(Strings.Current["SoundProfileDevice"], Headphones), h.Player.SoundProfileDeviceText);

        // Na słuchawkach inne ustawienia.
        h.Player.Bands[0].Gain = -4;
        h.Player.SoundEffects[0].Enabled = false;
        h.Player.Parametric.AddBand();
        h.Player.Parametric.Bands[0].Gain = 5;
        h.Player.EqualiserMode = EqualiserMode.Parametric;

        h.Player.SetOutputDevice(null);
        Assert.Equal(PlayerHarness.DefaultDevice, h.Engine.DeviceName);

        Assert.Equal(6, h.Player.Bands[0].Gain);
        Assert.Equal(6, h.Engine.Graph.Equaliser.GetGain(0));
        Assert.True(h.Player.SoundEffects[0].Enabled);
        Assert.Equal(0.8, h.Player.SoundEffects[0].Strength);
        Assert.True(h.Engine.Graph.Crossfeed.Enabled);
        Assert.Equal(EqualiserMode.Graphic, h.Player.EqualiserMode);
        Assert.Empty(h.Player.Parametric.Bands);
        Assert.False(h.Engine.Graph.Parametric.Enabled);
        Assert.Equal(6, h.Settings.Current.EqualiserGains[0]);

        h.Player.SetOutputDevice(Headphones);

        Assert.Equal(-4, h.Player.Bands[0].Gain);
        Assert.False(h.Player.SoundEffects[0].Enabled);
        Assert.Equal(EqualiserMode.Parametric, h.Player.EqualiserMode);
        Assert.Equal(5, Assert.Single(h.Player.Parametric.Bands).Gain);
        Assert.True(h.Engine.Graph.Parametric.Enabled);
        Assert.Equal(5, Assert.Single(h.Engine.Graph.Parametric.Bands).Gain);

        Assert.Equal([Headphones, PlayerHarness.DefaultDevice], h.Settings.Current.SoundProfiles.Select(profile => profile.Device));
    });

    /// <summary>Ogranicznik i normalizacja nie należą do profilu.</summary>
    [Fact]
    public void OgranicznikINormalizacjaSaWspolne() => Ui.Run(() =>
    {
        using var h = Playing();
        h.Player.SoundProfilesEnabled = true;
        h.Player.SetOutputDevice(Headphones);

        h.Player.LimiterEnabled = false;
        h.Player.NormalisationEnabled = false;
        h.Player.SetOutputDevice(null);

        Assert.False(h.Player.LimiterEnabled);
        Assert.False(h.Player.NormalisationEnabled);
    });

    /// <summary>Po wyłączeniu bieżące wartości zostają wspólnymi, a profile zostają w pliku.</summary>
    [Fact]
    public void WylaczenieZostawiaBiezaceWartosciIProfile() => Ui.Run(() =>
    {
        using var h = Playing();
        h.Player.SoundProfilesEnabled = true;
        h.Player.SetOutputDevice(Headphones);
        h.Player.Bands[1].Gain = 2;

        h.Player.SoundProfilesEnabled = false;
        h.Player.SetOutputDevice(null);

        Assert.Equal(2, h.Player.Bands[1].Gain);
        Assert.Equal(2, h.Settings.Current.SoundProfiles.Count);
        Assert.False(h.Player.ShowSoundProfileDevice);
    });

    /// <summary>
    /// Włączenie, zanim cokolwiek zagrało: bieżące wartości dostaje urządzenie, które otworzy się
    /// pierwsze, nawet gdy w pliku stoi jego starszy profil.
    /// </summary>
    [Fact]
    public void WlaczenieBezOtwartegoUrzadzeniaPrzejmujePierwszeOtwarte() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.SoundProfiles =
            [new SoundProfile { Device = PlayerHarness.DefaultDevice, Preamp = -9 }]);

        h.Player.Preamp.Gain = 2;
        h.Player.SoundProfilesEnabled = true;
        Assert.False(h.Player.ShowSoundProfileDevice);

        h.OpenPaths([h.Track("a")]);

        Assert.Equal(2, h.Player.Preamp.Gain);
        Assert.Equal(2, Assert.Single(h.Settings.Current.SoundProfiles).Preamp);
        Assert.True(h.Player.ShowSoundProfileDevice);
    });

    /// <summary>
    /// Po ponownym uruchomieniu wspólne wartości należą do urządzenia, na którym grano ostatnio:
    /// to samo urządzenie niczego nie zmienia, inne wczytuje swój profil.
    /// </summary>
    [Fact]
    public void ProfileWracajaPoPonownymUruchomieniu() => Ui.Run(() =>
    {
        string directory;
        using (var h = Playing())
        {
            directory = h.Directory;
            h.Player.SoundProfilesEnabled = true;
            h.Player.Preamp.Gain = -3;
            h.Player.SetOutputDevice(Headphones);
            h.Player.Preamp.Gain = 4;
            h.Settings.SaveNow();
        }

        using (var again = new PlayerHarness(directory: directory))
        {
            Assert.True(again.Player.SoundProfilesEnabled);
            Assert.Equal(4, again.Player.Preamp.Gain);
            Assert.Equal(string.Format(Strings.Current["SoundProfileDevice"], Headphones), again.Player.SoundProfileDeviceText);

            again.OpenPaths([again.Track("a")]);
            Assert.Equal(Headphones, again.Engine.DeviceName);
            Assert.Equal(4, again.Player.Preamp.Gain);

            again.Player.SetOutputDevice(null);
            Assert.Equal(-3, again.Player.Preamp.Gain);
            again.Settings.SaveNow();
        }

        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        Assert.Equal([PlayerHarness.DefaultDevice, Headphones], store.Current.SoundProfiles.Select(profile => profile.Device));
        Assert.Equal(4, store.Current.SoundProfiles[1].Preamp);
    });

    /// <summary>Najwyżej 32 profile; nowe urządzenie wypycha najdawniej używane.</summary>
    [Fact]
    public void NajdawniejUzywanyProfilWypada() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s =>
        {
            s.SoundProfilesEnabled = true;
            s.SoundProfiles = [.. Enumerable.Range(1, SoundProfile.MaxProfiles).Select(i => new SoundProfile { Device = $"Urządzenie {i}" })];
        });

        h.OpenPaths([h.Track("a")]);

        var devices = h.Settings.Current.SoundProfiles.Select(profile => profile.Device).ToList();
        Assert.Equal(SoundProfile.MaxProfiles, devices.Count);
        Assert.Equal(PlayerHarness.DefaultDevice, devices[0]);
        Assert.Equal("Urządzenie 1", devices[1]);
        Assert.DoesNotContain($"Urządzenie {SoundProfile.MaxProfiles}", devices);
    });

    /// <summary>
    /// Urządzenie zastępcze (wybrane odmówiło otwarcia) i ponowne otwarcie po utracie przechodzą
    /// tą samą drogą: profil należy do urządzenia, które faktycznie gra.
    /// </summary>
    [Fact]
    public void ProfilNalezyDoUrzadzeniaKtoreFaktycznieGra() => Ui.Run(() =>
    {
        using var h = Playing();
        h.Player.SoundProfilesEnabled = true;
        h.Player.SetOutputDevice(Headphones);
        h.Player.Preamp.Gain = 5;

        h.FailingDevice = "Głośniki";
        h.Player.SetOutputDevice("Głośniki");

        Assert.Equal(PlayerHarness.DefaultDevice, h.Engine.DeviceName);
        Assert.Equal(0, h.Player.Preamp.Gain);
        Assert.DoesNotContain(h.Settings.Current.SoundProfiles, profile => profile.Device == "Głośniki");
    });

    /// <summary>Ręcznie poprawiony plik: powtórzone urządzenia, puste nazwy, wartości spoza zakresów, za dużo profili.</summary>
    [Fact]
    public void ProfileSaPoprawianePrzyOdczycie() => Ui.Run(() =>
    {
        var profiles = string.Join(",", Enumerable.Range(0, 40).Select(i => $$"""{ "device": "U{{i}}" }"""));
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, $$"""
            {
              "soundProfiles": [
                { "device": "A", "preamp": 40, "equaliserGains": [30], "crossfeedStrength": 7,
                  "parametricBands": [ { "frequency": 1, "gain": -99, "q": 99 } ] },
                { "device": "A", "preamp": 1 },
                { "device": "" },
                null,
                {{profiles}}
              ]
            }
            """);

        var store = new SettingsStore(path);
        var list = store.Current.SoundProfiles;

        Assert.Equal(SoundProfile.MaxProfiles, list.Count);
        Assert.Single(list, profile => profile.Device == "A");
        Assert.Equal(12, list[0].Preamp);
        Assert.Equal(10, list[0].EqualiserGains.Length);
        Assert.Equal(12, list[0].EqualiserGains[0]);
        Assert.Equal(1, list[0].CrossfeedStrength);
        Assert.Equal((20.0, -24.0, 20.0), (list[0].ParametricBands[0].Frequency, list[0].ParametricBands[0].Gain, list[0].ParametricBands[0].Q));
    });

    /// <summary>Plik ustawień sprzed profili wczytuje się z wyłączoną opcją i bez profili.</summary>
    [Fact]
    public void StarszyPlikNieMaProfili() => Ui.Run(() =>
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, """{ "equaliserEnabled": true, "preamp": 1 }""");

        var store = new SettingsStore(path);

        Assert.False(store.Current.SoundProfilesEnabled);
        Assert.Empty(store.Current.SoundProfiles);
        Assert.Equal(EqualiserMode.Graphic, store.Current.EqualiserMode);
        Assert.Empty(store.Current.ParametricBands);
        Assert.Equal(ParametricFilterType.Peak, new ParametricBandSettings().Type);
    });

    private static PlayerHarness Playing()
    {
        var h = new PlayerHarness();
        h.OpenPaths([h.Track("a")]);
        Assert.True(h.Engine.IsDeviceOpen);
        Assert.Equal(PlayerHarness.DefaultDevice, h.Engine.DeviceName);
        return h;
    }
}
