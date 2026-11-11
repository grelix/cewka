using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Views;
using Cewka.App.Radio;
using Cewka.App.ViewModels;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>
/// Okno główne w trybie radia na modelu widoku: przełączanie źródła, transport, lista stacji, ulubione i stan
/// „teraz gra”. Sieci nie ma: silnik dostaje atrapy sesji stacji.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RadioWindowTests
{
    private static RadioStation Find(PlayerHarness h, string id) =>
        h.Player.Radio.Rows.First(r => r.Station.Id == id).Station;

    private static LiveSource SourceOf(PlayerHarness h, int session) => h.Sessions[session].Source;

    private static void StartRadio(PlayerHarness h)
    {
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
    }

    // ---------- Przełącznik źródła ----------

    [Fact]
    public void PierwszeWejscieBezStacjiPokazujeListeKrajuDomyslnegoIBezGry() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.SetSource(SourceMode.Radio);

        Assert.True(h.Player.IsRadioMode);
        Assert.False(h.Player.IsFilesMode);
        Assert.False(h.Player.IsPlaying);
        Assert.False(h.Player.Radio.HasStation);
        Assert.Equal(Strings.Current["RadioNoStation"], h.Player.ShownTitle);
        Assert.NotEmpty(h.Player.Radio.Rows);
        Assert.Empty(h.Sessions);
    });

    [Fact]
    public void ZrodloIKrajZapisujaSieWUstawieniach() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "GB");
        h.Player.Radio.SelectedCategory = h.Player.Radio.Categories.First(c => c.Key == RadioCategories.Hits);

        Assert.Equal(SourceMode.Radio, h.Settings.Current.Source);
        Assert.Equal("GB", h.Settings.Current.RadioCountry);
        Assert.Equal(RadioCategories.Hits, h.Settings.Current.RadioCategory);
        Assert.Equal(["gb-a"], h.Player.Radio.Rows.Select(r => r.Station.Id));
    });

    [Fact]
    public void StartWTrybieRadiaWracaBezGrania() => Ui.Run(() =>
    {
        var directory = TestEnvironment.NewDirectory();
        var state = new RadioState();
        state.PushRecent(PlayerHarness.DefaultRadioCatalog().Find("pl-b")!);
        RadioStateStore.Save(state, Path.Combine(directory, "radio.json"));

        using var h = new PlayerHarness(s => s.Source = SourceMode.Radio, directory);

        Assert.True(h.Player.IsRadioMode);
        Assert.False(h.Player.IsPlaying);
        Assert.Equal("pl-b", h.Player.Radio.CurrentStation?.Id);
        Assert.Empty(h.Sessions);
    });

    [Fact]
    public void PlikGraZRadiaWracaTenSamUtworOdZapamietanejPozycji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a"), h.Track("b")]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying));
        h.Player.SetSource(SourceMode.Radio);

        // Bez wybranej stacji nie ma czego grać: plik pauzuje.
        Assert.False(h.Player.IsPlaying);

        Assert.True(h.Player.Radio.Play(Find(h, "pl-a")));
        h.Player.SyncPlayingState();
        Assert.True(h.Engine.IsLive);
        Assert.True(h.Player.IsPlaying);

        h.Player.SetSource(SourceMode.Files);

        Assert.False(h.Engine.IsLive);
        Assert.True(h.Player.IsPlaying);
        Assert.Equal(0, h.Player.CurrentIndex);
        Assert.Equal(1, h.Sessions[0].Stopped);
    });

    [Fact]
    public void PrzelaczenieZPauzyZmieniaTylkoWidok() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a")]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying));
        h.Player.Pause();

        h.Player.SetSource(SourceMode.Radio);
        h.Player.SetSource(SourceMode.Files);

        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Sessions);
    });

    [Fact]
    public void WRadiuStacjaGraPoPrzelaczeniuZPlikowZZapamietanymWyborem() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        h.Player.Radio.Play(Find(h, "pl-b"));
        h.Player.SetSource(SourceMode.Files);

        h.OpenPaths([h.Track("a")]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying));

        h.Player.SetSource(SourceMode.Radio);
        h.Player.SyncPlayingState();

        Assert.True(h.Engine.IsLive);
        Assert.True(h.Player.IsPlaying);
        Assert.Equal("pl-b", h.Engine.CurrentLive!.StationId);
    });

    [Fact]
    public void OtwarcieZewnetrznegoPlikuWRadiuPrzelaczaNaPliki() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);

        h.OpenFromOutside([h.Track("a")]);

        Assert.True(h.Player.IsFilesMode);
        Assert.Equal(SourceMode.Files, h.Settings.Current.Source);
    });

    [Fact]
    public void DopisanieDoKolejkiWRadiuZostawiaRadioZKomunikatem() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);

        h.OpenPaths([h.Track("a")], replace: false);

        Assert.True(h.Player.IsRadioMode);
        Assert.Equal(Strings.Current["RadioFilesQueuedNotice"], h.Player.Notice);
    });

    // ---------- Transport ----------

    [Fact]
    public void PauzaWRadiuToRozlaczenieAOdtworzLaczyOdNowa() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        h.Player.Radio.Play(Find(h, "pl-a"));
        h.Player.SyncPlayingState();
        Assert.True(h.Player.IsPlaying);
        Assert.Equal(Strings.Current["TooltipStopLive"], h.Player.PlayPauseTooltip);
        Assert.True(h.Player.ShowsStopIcon);
        Assert.False(h.Player.ShowsPauseIcon);

        h.Player.TogglePlay();

        Assert.False(h.Player.IsPlaying);
        Assert.True(h.Engine.IsLive);
        Assert.Equal(1, h.Sessions[0].Stopped);

        h.Player.TogglePlay();

        Assert.True(h.Player.IsPlaying);
        Assert.Equal(2, h.Sessions.Count);
    });

    [Fact]
    public void DalejIWsteczPrzelaczajaStacjeZListyIZawijaja() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        h.Player.Radio.Play(Find(h, "pl-a"));

        h.Player.Next();
        Assert.Equal("pl-b", h.Engine.CurrentLive!.StationId);

        h.Player.Next();
        Assert.Equal("pl-a", h.Engine.CurrentLive!.StationId);

        h.Player.Previous();
        Assert.Equal("pl-b", h.Engine.CurrentLive!.StationId);
        Assert.Equal(Strings.Current["CountryPL"] is { } pl ? $"Beta FM ({pl})" : string.Empty, h.Player.Radio.StationName);
    });

    [Fact]
    public void LosowoPowtarzanieIPrzewijanieWRadiuNicNieRobia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a", 120)]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying));
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.Play(h.Player.Radio.Rows.First().Station);

        Assert.Equal(0, h.Player.FilesOnlyOpacity);
        var before = h.Engine.Position;
        h.Player.SeekRelative(30);

        Assert.Equal(before, h.Engine.Position);
        h.Player.SetSource(SourceMode.Files);
        Assert.Equal(1, h.Player.FilesOnlyOpacity);
    });

    // ---------- Zdarzenia silnika ----------

    [Fact]
    public void SpoznioneZdarzenieStaraStacjiNieZmieniaStanuNowej() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        h.Player.Radio.Play(Find(h, "pl-a"));
        h.Player.Radio.Play(Find(h, "pl-b"));
        var old = SourceOf(h, 0);
        var current = SourceOf(h, 1);

        h.Player.Radio.OnLiveFailed(old, new LiveFailure(LiveFailureReason.ConnectionFailed));
        h.Player.Radio.OnLiveTitle(old, "Stary - Utwór");
        h.Player.Radio.OnLiveState(old, LiveState.Reconnecting);

        Assert.False(h.Player.Radio.HasError);
        Assert.Equal(Strings.Current["RadioStateConnecting"], h.Player.Radio.StateText);
        Assert.DoesNotContain("Utwór", h.Player.ShownTitle);

        h.Player.Radio.OnLiveFailed(current, new LiveFailure(LiveFailureReason.ConnectionFailed));
        Assert.Equal(Strings.Current["RadioFailureNetwork"], h.Player.Radio.ErrorText);
    });

    [Fact]
    public void TytulZMetadanychWykonawcaIStacjaORazCzyszczeniePrzyZmianieStacji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        var radio = h.Player.Radio;
        radio.Play(Find(h, "pl-a"));
        var source = SourceOf(h, 0);

        radio.OnLiveTitle(source, "Queen - Radio Ga Ga");
        Assert.Equal("Radio Ga Ga", h.Player.ShownTitle);
        Assert.StartsWith("Queen · ", h.Player.ShownLine);
        Assert.Contains(radio.StationName, h.Player.ShownLine);

        // Trzy części nie dzielą się na wykonawcę i tytuł.
        radio.OnLiveTitle(source, "A - B - C");
        Assert.Equal("A - B - C", h.Player.ShownTitle);

        // Pusty tytuł, nazwa stacji i znacznik reklamy są pomijane: zostaje nazwa stacji.
        foreach (var skipped in new[] { "", "  ", "Alfa FM", "Spot - x adw_ad='true'" })
        {
            radio.OnLiveTitle(source, skipped);
            Assert.Equal(radio.StationName, h.Player.ShownTitle);
        }

        radio.OnLiveTitle(source, "Stary tytuł");
        radio.Play(Find(h, "pl-b"));
        Assert.Equal(radio.StationName, h.Player.ShownTitle);
    });

    [Fact]
    public void StanPolaczeniaMaTekstIZnikaPoZagraniu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        var radio = h.Player.Radio;
        radio.Play(Find(h, "pl-a"));
        var source = SourceOf(h, 0);

        Assert.Equal(Strings.Current["RadioStateConnecting"], radio.StateText);
        Assert.Equal(Strings.Current["RadioStateConnecting"], h.Player.ShownLine);

        radio.OnLiveState(source, LiveState.Buffering);
        Assert.Equal(Strings.Current["RadioStateBuffering"], radio.StateText);

        radio.OnLiveState(source, LiveState.Reconnecting);
        Assert.Equal(Strings.Current["RadioStateReconnecting"], radio.StateText);

        // Grająca stacja bez tytułu nie powtarza „Na żywo” pod nazwą: ten napis stoi już na dole z kropką.
        radio.OnLiveState(source, LiveState.Playing);
        Assert.Equal(string.Empty, radio.StateText);
        Assert.Equal(string.Empty, h.Player.ShownLine);
        Assert.False(h.Player.HasShownLine);
    });

    /// <summary>W oknie głównym grająca stacja bez tytułu ma pod nazwą pusty wiersz, a „Na żywo” tylko raz, na dole.</summary>
    [Fact]
    public void StacjaBezTytuluNieMaNapisuNaZywoPodNazwa() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.RadioPrivacyAcknowledged = true);
        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
        window.Show();
        try
        {
            StartRadio(h);
            var radio = h.Player.Radio;
            radio.Play(Find(h, "pl-a"));
            radio.OnLiveState(SourceOf(h, 0), LiveState.Playing);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.False(window.TrackArtist.IsEffectivelyVisible);
            var live = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Text == Strings.Current["RadioLive"])
                .ToList();
            Assert.Single(live);
            Assert.Same(window.LiveCaption, live[0]);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BladStacjiMaTekstIPonowienieLaczyOdNowa() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        var radio = h.Player.Radio;
        radio.Play(Find(h, "pl-a"));

        radio.OnLiveFailed(SourceOf(h, 0), new LiveFailure(LiveFailureReason.Stalled));

        Assert.True(radio.HasError);
        Assert.True(radio.CanRetry);
        Assert.Equal(Strings.Current["RadioFailureNoResponse"], radio.ErrorText);

        radio.Retry();

        Assert.False(radio.HasError);
        Assert.Equal(2, h.Sessions.Count);
    });

    [Fact]
    public void BladBezSensuPonowieniaNieMaPrzyciskuPonow() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        var radio = h.Player.Radio;
        radio.Play(Find(h, "pl-a"));

        radio.OnLiveFailed(SourceOf(h, 0), new LiveFailure(LiveFailureReason.HlsNotSupportedYet));

        Assert.True(radio.HasError);
        Assert.False(radio.CanRetry);
        Assert.Equal(Strings.Current["RadioFailureHls"], radio.ErrorText);
    });

    // ---------- Lista, wyszukiwanie ----------

    [Fact]
    public void NazwaStacjiMaKrajZawszeWNawiasie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);

        Assert.All(h.Player.Radio.Rows, row => Assert.EndsWith(")", row.Name));
        Assert.Contains(h.Player.Radio.Rows, r => r.Name == $"Beta FM ({Strings.Current["CountryPL"]})");
    });

    [Fact]
    public void WyszukiwanieSzukaWeWszystkichKrajachAWybranyIdziePierwszy() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        h.Player.Radio.SearchText = "alfa";

        Assert.Equal(["pl-a", "us-a"], h.Player.Radio.Rows.Select(r => r.Station.Id));

        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "US");
        Assert.Equal(["us-a", "pl-a"], h.Player.Radio.Rows.Select(r => r.Station.Id));

        h.Player.Radio.SearchText = "zzz";
        Assert.True(h.Player.Radio.IsEmpty);
        Assert.Equal(Strings.Current["RadioEmptyResults"], h.Player.Radio.EmptyText);
    });

    [Fact]
    public void KategoriaZawezaListeAKrajWszystkieNieZawezaPoKategorii() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.SetSource(SourceMode.Radio);
        var radio = h.Player.Radio;
        radio.SelectedCountry = radio.Countries.First(c => c.Key is null);
        radio.SelectedCategory = radio.Categories.First(c => c.Key == RadioCategories.Hits);

        Assert.Equal(["gb-a", "pl-a"], radio.Rows.Select(r => r.Station.Id));
        Assert.Equal(string.Format(Strings.Current["RadioResultCount"], 2), radio.Summary);
    });

    // ---------- Ulubione, ostatnie, cofanie ----------

    [Fact]
    public void UlubioneZapisujaSieIMoznaJeCofnacIPrzesunac() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        var radio = h.Player.Radio;
        radio.ToggleFavourite(Find(h, "pl-a"));
        radio.ToggleFavourite(Find(h, "pl-b"));
        Assert.True(radio.Rows.All(r => r.IsFavourite));
        Assert.Equal(["pl-a", "pl-b"], RadioStateStore.Load(h.RadioFile).Favourites.Select(f => f.Id));

        radio.SetView(RadioListContext.ViewFavourites);
        Assert.Equal(["pl-a", "pl-b"], radio.Rows.Select(r => r.Station.Id));

        Assert.True(radio.MoveFavourite(radio.Rows[1].Station, -1));
        Assert.Equal(["pl-b", "pl-a"], radio.Rows.Select(r => r.Station.Id));

        radio.ToggleFavourite(radio.Rows[0].Station);
        Assert.Equal(["pl-a"], radio.Rows.Select(r => r.Station.Id));
        Assert.True(radio.CanUndo);

        Assert.True(radio.Undo());
        Assert.Equal(["pl-b", "pl-a"], radio.Rows.Select(r => r.Station.Id));
        Assert.False(radio.CanUndo);
        Assert.Equal(["pl-b", "pl-a"], RadioStateStore.Load(h.RadioFile).Favourites.Select(f => f.Id));
    });

    [Fact]
    public void OstatnioSluchaneMajaNajwyzejDziesiecIZapisujaOstatniaStacje() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        var radio = h.Player.Radio;
        radio.Play(Find(h, "pl-a"));
        radio.Play(Find(h, "pl-b"));
        radio.Play(Find(h, "pl-a"));

        radio.SetView(RadioListContext.ViewRecent);

        Assert.Equal(["pl-a", "pl-b"], radio.Rows.Select(r => r.Station.Id));
        Assert.Equal("pl-a", RadioStateStore.Load(h.RadioFile).LastStationId);
    });

    [Fact]
    public void PustyWidokUlubionychMaOpis() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        h.Player.Radio.SetView(RadioListContext.ViewFavourites);

        Assert.True(h.Player.Radio.IsEmpty);
        Assert.Equal(Strings.Current["RadioEmptyFavourites"], h.Player.Radio.EmptyText);
        Assert.False(h.Player.Radio.ShowsFilters);
    });

    // ---------- Własny adres, import i eksport ----------

    [Fact]
    public void WlasnyAdresTrafiDoUlubionychIGra() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);

        Assert.True(h.Player.Radio.AddCustom("Moja stacja", "http://127.0.0.1:8000/live"));

        Assert.True(h.Engine.IsLive);
        Assert.Single(h.Player.Radio.State.Favourites);
        Assert.Equal("Moja stacja", h.Player.Radio.CurrentStation!.Name);
        Assert.True(h.Player.Radio.CurrentStation.AllowLocalAddress);
    });

    [Fact]
    public void AdresZListyBlokadIZlyAdresNieSaDodawaneIMajaKomunikat() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);

        Assert.False(h.Player.Radio.AddCustom("X", "http://stream.example.ru/live"));
        Assert.Equal(Strings.Current["RadioAddressBlocked"], h.Player.Notice);

        Assert.False(h.Player.Radio.AddCustom("X", "ftp://example.com/live"));
        Assert.Equal(Strings.Current["RadioAddressInvalid"], h.Player.Notice);

        Assert.Empty(h.Player.Radio.State.Favourites);
        Assert.Empty(h.Sessions);
    });

    [Fact]
    public void ImportDopisujeDoUlubionychAEksportMaKrajPrzyNazwie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        var radio = h.Player.Radio;

        var added = radio.Import("#EXTM3U\n#EXTINF:-1,Jedna\nhttps://one.example.com/a\nhttps://two.example.com/b\nhttp://x.example.ru/c\n");

        Assert.Equal(2, added);
        Assert.Equal(string.Format(Strings.Current["RadioImportedSkipped"], 2, 1), h.Player.Notice);

        radio.ToggleFavourite(Find(h, "pl-a"));
        var text = radio.ExportText();
        Assert.Contains($"#EXTINF:-1,Alfa FM ({Strings.Current["CountryPL"]})", text);
        Assert.Contains("Jedna", text);

        var again = radio.Import(text);
        Assert.Equal(0, again);
    });

    // ---------- Stacje niedozwolone ----------

    [Fact]
    public void UlubionaStacjaZListyBlokadZostajeAleSieNieOdtwarza() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        StartRadio(h);
        var blocked = RadioState.CreateCustom("Zablokowana", "http://stream.example.ru/live", "PL", typed: false);
        h.Player.Radio.State.AddFavourite(blocked);

        Assert.False(h.Player.Radio.Play(blocked));

        Assert.Equal(Strings.Current["RadioStationBlocked"], h.Player.Radio.ErrorText);
        Assert.Empty(h.Sessions);
        Assert.False(h.Engine.IsLive);
    });

    // ---------- Teksty błędów ----------

    public static TheoryData<LiveFailureReason> AllReasons => new(Enum.GetValues<LiveFailureReason>());

    [Theory]
    [MemberData(nameof(AllReasons))]
    public void KazdaPrzyczynaPorazkiMaTekstWKazdymJezyku(LiveFailureReason reason)
    {
        var key = RadioFailureText.KeyFor(reason);
        Assert.StartsWith("RadioFailure", key);

        foreach (var code in new[] { "pl", "en", "cs", "de", "el", "es", "fr", "hu", "id", "it", "nl", "pt", "ro", "ru", "tr", "uk", "vi" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Languages", code + ".json");
            var texts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
            Assert.True(texts.TryGetValue(key, out var text) && text.Length > 0, $"{code}: brak {key}");
        }
    }

    [Fact]
    public void BrakDekoderaMaOsobneTekstyDlaWindowsILinuksaZNazwaPakietu()
    {
        Assert.Equal("gstreamer1-libav", RadioFailureText.DecoderPackage("ID=fedora\nVERSION_ID=41"));
        Assert.Equal("gstreamer1.0-libav", RadioFailureText.DecoderPackage("ID=ubuntu\nID_LIKE=debian"));
        Assert.Equal("gstreamer1.0-libav", RadioFailureText.DecoderPackage("ID=zorin\nID_LIKE=\"ubuntu debian\""));
        Assert.Equal("gst-libav", RadioFailureText.DecoderPackage("ID=arch"));
        Assert.Equal("gst-libav", RadioFailureText.DecoderPackage(null));

        var linux = string.Format(Strings.Current["RadioFailureCodecLinux"], "gst-libav");
        Assert.Contains("gst-libav", linux);
        Assert.Contains("Media Feature Pack", Strings.Current["RadioFailureCodecWindows"]);
    }

    [Fact]
    public void PonowienieMaSensTylkoGdyPrzyczynaMozeZniknac()
    {
        Assert.True(RadioFailureText.CanRetry(LiveFailureReason.ConnectionFailed));
        Assert.True(RadioFailureText.CanRetry(LiveFailureReason.StreamEnded));
        Assert.False(RadioFailureText.CanRetry(LiveFailureReason.HlsNotSupportedYet));
        Assert.False(RadioFailureText.CanRetry(LiveFailureReason.CodecUnavailable));
        Assert.False(RadioFailureText.CanRetry(LiveFailureReason.UrlBlocked));
    }
}
