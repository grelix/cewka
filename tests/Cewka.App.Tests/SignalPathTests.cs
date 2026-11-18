using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio;
using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;
using Cewka.Audio.Tests.Engine;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Tor sygnału: okienko otwierane z opisu parametrów pliku i jakości stacji. Pokazuje źródło, urządzenie
/// i tylko te etapy między nimi, które w tej chwili zmieniają sygnał.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SignalPathTests
{
    private const string Nbsp = " ";

    private static readonly string SharedMode = OperatingSystem.IsWindows() ? " · tryb współdzielony" : string.Empty;

    /// <summary>Stałe wzmocnienie dla każdego pliku.</summary>
    private sealed class FixedGain(TrackGain gain) : ITrackGainSource
    {
        public TrackGain? GetTrackGain(QueueEntry entry) => gain;
    }

    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Model widoku po polsku, z jednym grającym plikiem; konfiguracja przed otwarciem pliku.</summary>
    private static PlayerHarness Playing(Action<PlayerHarness>? configure = null, int sourceRate = PlayerHarness.Rate, int sourceChannels = PlayerHarness.Channels)
    {
        var h = new PlayerHarness(s =>
        {
            s.Language = "pl";
            s.QueuePinned = false;
            s.RadioPrivacyAcknowledged = true;
            s.WindowControls = WindowControlsPosition.Right;
        });

        Strings.Current.SetLanguage("pl");
        h.Engine.OpenDecoder = (_, rate, channels) =>
            new SyntheticDecoder((long)(60 * rate), 0.1f, channels, rate) { SourceSampleRate = sourceRate, SourceChannels = sourceChannels };

        h.Player.Volume = 1;
        h.Player.LimiterEnabled = false;
        configure?.Invoke(h);

        h.OpenPaths([h.Track("a")]);
        Assert.True(h.PumpUntil(() => h.Engine.SignalPath.Entry is not null && h.Player.HasFormatBadge));
        h.Player.Pause();
        return h;
    }

    private static List<(string Label, string Value)> Open(PlayerHarness h)
    {
        h.Player.IsSignalPathOpen = true;
        AssertUnitsStayWithNumbers(h.Player.SignalPathStages);
        return h.Player.SignalPathStages.Select(stage => (stage.Label, stage.Value)).ToList();
    }

    /// <summary>Liczba ze zwykłą spacją przed jednostką, jak w <c>TextStandardTests</c>, także kbps, bit i procent.</summary>
    private static readonly Regex SplitUnit = new(@"[\d}] (?:dB|LUFS|kHz|Hz|ms|kbps|bit|%)(?!\p{L})");

    /// <summary>W wąskim okienku jednostka nie może przejść do nowego wiersza bez swojej liczby.</summary>
    private static void AssertUnitsStayWithNumbers(IEnumerable<SignalPathStage> stages)
    {
        var split = stages.Where(stage => SplitUnit.IsMatch(stage.Value)).Select(stage => $"{stage.Label}: „{stage.Value}”").ToList();
        Assert.True(split.Count == 0, $"Zwykła spacja między liczbą a jednostką: {string.Join("; ", split)}");
    }

    [Fact]
    public void WzorzecRozdzielonejJednostkiLapieZwykleSpacje()
    {
        foreach (var text in new[] { "+2,5 dB", "44,1 kHz", "320 kbps", "16 bit", "80 %" })
            Assert.Matches(SplitUnit, text);

        foreach (var text in new[] { $"+2,5{Nbsp}dB", $"44,1{Nbsp}kHz → 48{Nbsp}kHz", $"320{Nbsp}kbps", $"16{Nbsp}bit", "80%" })
            Assert.DoesNotMatch(SplitUnit, text);
    }

    /// <summary>
    /// Test w wątku interfejsu, a potem język sprzed testu. Teksty czyta się dopiero tam: pierwszy odczyt
    /// przed startem Avalonii zostawiłby słownik pusty na cały przebieg.
    /// </summary>
    private static void InPolish(Action test) => Ui.Run(() =>
    {
        var language = Strings.Current.CurrentCode;
        try { test(); }
        finally { Strings.Current.SetLanguage(language); }
    });

    // ---------- Zawartość ----------

    [Fact]
    public void PlikWFormacieUrzadzeniaBezPrzetwarzaniaMaTylkoZrodloIUrzadzenie() => InPolish(() =>
    {
        using var h = Playing(h => h.Player.EqualiserEnabled = false);

        var stages = Open(h);

        Assert.Equal(
        [
            ("Źródło", $"WAV · 48{Nbsp}kHz · 16{Nbsp}bit · stereo · bezstratne"),
            ("Urządzenie", $"Atrapa · 48{Nbsp}kHz · stereo{SharedMode}"),
        ], stages);
        Assert.True(h.Player.SignalPathUnchanged);
    });

    [Fact]
    public void KazdyEtapZmieniajacySygnalStoiWKolejnosciToru() => InPolish(() =>
    {
        var level = AudioQuality.Resampling;
        try
        {
            using var h = Playing(h =>
            {
                h.Player.SetResamplerQuality(ResamplerQuality.High);
                h.Engine.Graph.GainSource = new FixedGain(new TrackGain(-4.5, -14, TrackGainKind.Album));
                h.Player.EqualiserMode = EqualiserMode.Graphic;
                h.Player.EqualiserEnabled = true;
                h.Player.Bands[3].Gain = 4;
                h.Player.SoundEffects.First(e => e.Key == "Crossfeed").Enabled = true;
                h.Player.SoundEffects.First(e => e.Key == "StereoWidth").Enabled = true;
                h.Player.Volume = 0.5;
                h.Player.LimiterEnabled = true;
            }, sourceRate: 44100, sourceChannels: 1);

            var stages = Open(h);

            Assert.Equal(
            [
                ("Źródło", $"WAV · 44,1{Nbsp}kHz · 16{Nbsp}bit · mono · bezstratne"),
                ("Konwersja kanałów", "mono → stereo"),
                ("Konwersja częstotliwości próbkowania", $"44,1{Nbsp}kHz → 48{Nbsp}kHz · Wysoka"),
                ("Normalizacja", $"−4,5{Nbsp}dB, według albumu"),
                ("Korektor", "graficzny"),
                ("Efekty", "Crossfeed, Szerokość stereo"),
                ("Głośność", "50%"),
                ("Ogranicznik", $"próg −1{Nbsp}dB"),
                ("Urządzenie", $"Atrapa · 48{Nbsp}kHz · stereo{SharedMode}"),
            ], stages);
            Assert.False(h.Player.SignalPathUnchanged);
        }
        finally
        {
            AudioQuality.Resampling = level;
        }
    });

    [Fact]
    public void KorektorParametrycznyPodajeLiczbeWlaczonychPasm() => InPolish(() =>
    {
        using var h = Playing(h =>
        {
            h.Player.EqualiserEnabled = true;
            h.Player.EqualiserMode = EqualiserMode.Parametric;
            h.Player.Parametric.Replace(
            [
                new ParametricBandSettings { Type = ParametricFilterType.Peak, Frequency = 100, Gain = 3, Q = 1, Enabled = true },
                new ParametricBandSettings { Type = ParametricFilterType.Peak, Frequency = 1000, Gain = -2, Q = 1, Enabled = true },
                new ParametricBandSettings { Type = ParametricFilterType.Peak, Frequency = 5000, Gain = 6, Q = 1, Enabled = false },
            ], 0);
        });

        Assert.Contains(("Korektor", "parametryczny, pasma: 2"), Open(h));
    });

    [Fact]
    public void KorektorPlaskiAlboWylaczonyNieJestEtapem() => InPolish(() =>
    {
        using var h = Playing(h =>
        {
            h.Player.EqualiserMode = EqualiserMode.Graphic;
            h.Player.EqualiserEnabled = true;
            h.Player.ResetEqualiser();
        });

        Assert.DoesNotContain(Open(h), stage => stage.Label == "Korektor");

        h.Player.Bands[0].Gain = 2;
        h.Player.EqualiserEnabled = false;
        h.Player.RefreshSignalPath();
        Assert.DoesNotContain(h.Player.SignalPathStages, stage => stage.Label == "Korektor");
    });

    [Fact]
    public void WyciszenieToGlosnoscZero() => InPolish(() =>
    {
        using var h = Playing();
        h.Player.IsMuted = true;

        Assert.Contains(("Głośność", "0%"), Open(h));
    });

    [Fact]
    public void WzmocnienieUtworuIBrakEtapuPrzyWylaczonejNormalizacji() => InPolish(() =>
    {
        using var h = Playing(h => h.Engine.Graph.GainSource = new FixedGain(new TrackGain(2.25, -20, TrackGainKind.Track)));

        Assert.Contains(("Normalizacja", $"+2,3{Nbsp}dB, według utworu"), Open(h));

        h.Player.NormalisationEnabled = false;
        h.Player.RefreshSignalPath();
        Assert.DoesNotContain(h.Player.SignalPathStages, stage => stage.Label == "Normalizacja");
    });

    [Fact]
    public void StacjaMaStrumienZDanymiZDekodera() => InPolish(() =>
    {
        using var h = Playing();
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
        h.Player.Radio.Play(h.Player.Radio.Rows.First(r => r.Station.Id == "pl-a").Station);
        h.Player.Radio.OnLiveState(h.Sessions[^1].Source, LiveState.Playing);
        Assert.True(h.PumpUntil(() => h.Engine.SignalPath.IsLive));

        var stages = Open(h);

        Assert.Equal(("Źródło", $"strumień · MP3 · 128{Nbsp}kbps · 48{Nbsp}kHz · stereo"), stages[0]);
        Assert.Equal("Urządzenie", stages[^1].Label);
    });

    // ---------- Odświeżanie na żywo ----------

    [Fact]
    public void OtwarteOkienkoNadazaZaUstawieniamiIJezykiem() => InPolish(() =>
    {
        using var h = Playing(h => h.Player.EqualiserEnabled = false);
        Open(h);
        Assert.True(h.Player.SignalPathUnchanged);

        h.Player.Volume = 0.25;
        h.Player.LimiterEnabled = true;
        h.Player.RefreshForCapture();
        Assert.Contains(new SignalPathStage("Głośność", "25%"), h.Player.SignalPathStages);
        Assert.Contains(new SignalPathStage("Ogranicznik", $"próg −1{Nbsp}dB"), h.Player.SignalPathStages);
        Assert.False(h.Player.SignalPathUnchanged);

        AssertUnitsStayWithNumbers(h.Player.SignalPathStages);

        h.Player.SetLanguage("en");
        AssertUnitsStayWithNumbers(h.Player.SignalPathStages);
        Assert.Equal("Source", h.Player.SignalPathStages[0].Label);
        Assert.Contains(new SignalPathStage("Volume", "25%"), h.Player.SignalPathStages);
        Assert.Contains(new SignalPathStage("Limiter", $"threshold −1{Nbsp}dB"), h.Player.SignalPathStages);
    });

    [Fact]
    public void ZamknieteOkienkoNieSkladaToru() => InPolish(() =>
    {
        using var h = Playing();

        h.Player.RefreshForCapture();
        Assert.Empty(h.Player.SignalPathStages);
    });

    // ---------- Okno ----------

    private static MainWindow Show(PlayerHarness h)
    {
        App.Settings.Current.Window = null;
        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760, AttachDesktopIntegration = false };
        window.Show();
        Settle();
        return window;
    }

    private static void Key(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Settle();
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle();
    }

    private static Point Centre(Visual control, Visual root) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    [Fact]
    public void OpisParametrowJestPrzyciskiemZNazwaIPodpowiedzia() => InPolish(() =>
    {
        using var h = Playing();
        var window = Show(h);
        try
        {
            var button = window.TrackQualityButton;
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.Focusable);
            Assert.Equal("Tor sygnału", AutomationProperties.GetName(button));
            Assert.Equal("Tor sygnału", ToolTip.GetTip(button));
            Assert.Equal("Tor sygnału", AutomationProperties.GetName(window.LiveQualityButton));
            Assert.Equal(AutomationControlType.Button, ControlAutomationPeer.CreatePeerForElement(button).GetAutomationControlType());

            // Przy wyłączonym „Parametry pliku” przycisku nie ma.
            h.Player.SetShowFormatBadge(false);
            Settle();
            Assert.False(button.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EnterOtwieraEscZamykaAFokusZostajeNaOpisie() => InPolish(() =>
    {
        using var h = Playing();
        var window = Show(h);
        try
        {
            var button = window.TrackQualityButton;
            Assert.True(button.Focus(NavigationMethod.Tab));

            Key(window, Avalonia.Input.Key.Enter);
            Assert.True(window.SignalPathPopup.IsOpen);
            Assert.True(window.SignalPathCard.IsEffectivelyVisible);
            Assert.Contains("open", button.Classes);

            Key(window, Avalonia.Input.Key.Escape);
            Assert.False(window.SignalPathPopup.IsOpen);
            Assert.False(h.Player.IsSignalPathOpen);
            Assert.True(button.IsFocused);

            // Esc przy zamkniętym okienku robi to, co robił dotąd.
            Key(window, Avalonia.Input.Key.Escape);
            Assert.False(window.SignalPathPopup.IsOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SpacjaNaOpisieOtwieraOkienkoZamiastSterowacOdtwarzaniem() => InPolish(() =>
    {
        using var h = Playing();
        var window = Show(h);
        try
        {
            Assert.False(h.Player.IsPlaying);
            Assert.True(window.TrackQualityButton.Focus(NavigationMethod.Tab));

            Key(window, Avalonia.Input.Key.Space);
            Assert.True(window.SignalPathPopup.IsOpen);
            Assert.False(h.Player.IsPlaying);

            Key(window, Avalonia.Input.Key.Space);
            Assert.False(window.SignalPathPopup.IsOpen);
            Assert.False(h.Player.IsPlaying);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void KlikniecieOtwieraAKlikniecieObokZamyka() => InPolish(() =>
    {
        using var h = Playing();
        var window = Show(h);
        try
        {
            Click(window, Centre(window.TrackQuality, window));
            Assert.True(window.SignalPathPopup.IsOpen);

            // Okienko stoi pod opisem, a jego tekst w jednej linii z opisem.
            var card = window.SignalPathCard.TranslatePoint(new Point(0, 0), window)!.Value;
            var label = window.TrackQuality.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.True(card.Y >= label.Y + window.TrackQuality.Bounds.Height, $"{card.Y} wobec {label.Y}");
            Assert.Equal(label.X, card.X + 19, 2.5);
            Assert.Equal(380, window.SignalPathCard.Bounds.Width, 0.5);

            // Kliknięcie w samo okienko go nie zamyka.
            Click(window, Centre(window.SignalPathCard, window));
            Assert.True(window.SignalPathPopup.IsOpen);

            Click(window, new Point(window.Bounds.Width / 2, window.Bounds.Height - 30));
            Assert.False(window.SignalPathPopup.IsOpen);
            Assert.False(h.Player.IsSignalPathOpen);

            // Ponowne kliknięcie opisu przy otwartym okienku je zamyka.
            Click(window, Centre(window.TrackQuality, window));
            Assert.True(window.SignalPathPopup.IsOpen);
            Click(window, Centre(window.TrackQuality, window));
            Assert.False(window.SignalPathPopup.IsOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CzytnikWidziTytulIEtapyOkienka() => InPolish(() =>
    {
        using var h = Playing();
        var window = Show(h);
        try
        {
            h.Player.IsSignalPathOpen = true;
            Settle();

            var names = new List<string>();

            void Walk(AutomationPeer peer)
            {
                if (peer.GetName() is { Length: > 0 } name) names.Add(name);
                foreach (var child in peer.GetChildren()) Walk(child);
            }

            Walk(ControlAutomationPeer.CreatePeerForElement(window.SignalPathCard));

            Assert.Contains("Tor sygnału", names);
            Assert.Contains("Źródło", names);
            Assert.Contains("Urządzenie", names);
            Assert.Contains(h.Player.SignalPathStages[0].Value, names);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WylaczenieOpisuIScenaZamykajaOkienko() => InPolish(() =>
    {
        using var h = Playing();
        var window = Show(h);
        try
        {
            h.Player.IsSignalPathOpen = true;
            Settle();
            h.Player.SetShowFormatBadge(false);
            Settle();
            Assert.False(window.SignalPathPopup.IsOpen);

            h.Player.SetShowFormatBadge(true);
            h.Player.IsSignalPathOpen = true;
            Settle();
            h.Player.IsSceneOpen = true;
            Settle();
            Assert.False(window.SignalPathPopup.IsOpen);
            h.Player.IsSceneOpen = false;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WRadiuOkienkoStoiPodOpisemStacji() => InPolish(() =>
    {
        using var h = Playing();
        var window = Show(h);
        try
        {
            h.Player.SetSource(SourceMode.Radio);
            h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
            h.Player.Radio.Play(h.Player.Radio.Rows.First(r => r.Station.Id == "pl-a").Station);
            h.Player.Radio.OnLiveState(h.Sessions[^1].Source, LiveState.Playing);
            h.Player.SyncPlayingState();
            Assert.True(h.PumpUntil(() =>
            {
                h.Player.Radio.RefreshBadge();
                return h.Player.HasFormatBadge;
            }));
            Settle();

            Assert.Same(window.LiveQualityButton, window.SignalPathPopup.PlacementTarget);
            Click(window, Centre(window.LiveQuality, window));
            Assert.True(window.SignalPathPopup.IsOpen);
            Assert.StartsWith("strumień", h.Player.SignalPathStages[0].Value);

            // Powrót do plików zabiera opis stacji spod okienka.
            h.Player.SetSource(SourceMode.Files);
            Settle();
            Assert.False(window.SignalPathPopup.IsOpen);
            Assert.Same(window.TrackQualityButton, window.SignalPathPopup.PlacementTarget);
        }
        finally { window.Close(); }
    });
}
