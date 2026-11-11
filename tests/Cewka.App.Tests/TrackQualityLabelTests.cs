using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Streaming;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>
/// Etykieta jakości źródła w oknie głównym: przy plikach zaraz za wierszem wykonawcy, mniejszym i cieńszym
/// pismem i przygaszona; w radiu zaraz za napisem „Na żywo”, przygaszona mocniej niż napis. Dawnej plakietki
/// nad tytułem nie ma, ustawienie „Parametry pliku” dalej ją włącza i wyłącza.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TrackQualityLabelTests
{
    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public MainViewModel Player => Harness.Player;

        public QualityRow Line => Window.TrackLine;

        public TextBlock Artist => Window.TrackArtist;

        public TextBlock Quality => Window.TrackQuality;

        public void Dispose() => Window.Close();
    }

    private static void Render()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Rig Open(double width = 1300)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.RadioPrivacyAcknowledged = true;
        });

        var window = new MainWindow(false, h.Player) { Width = width, Height = 760 };
        window.Show();
        Render();
        return new Rig { Window = window, Harness = h };
    }

    /// <summary>Okno z jednym plikiem WAV; <paramref name="artist"/> pusty daje plik bez tagów.</summary>
    private static Rig OpenFile(string? artist, double width = 1300)
    {
        var rig = Open(width);
        var path = artist is null ? rig.Harness.Track("a") : rig.Harness.TaggedTrack("a", "Tytuł", artist, "Album");
        rig.Harness.OpenPaths([path]);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.HasFormatBadge), $"opis formatu: „{rig.Player.FormatBadge}”");
        if (artist is not null) Assert.True(rig.Harness.PumpUntil(() => rig.Player.HasShownLine));
        Render();
        return rig;
    }

    /// <summary>Okno ze stacją, która gra i ma już pomiar kodeka i przepływności.</summary>
    private static Rig OpenRadio()
    {
        var rig = Open();
        var h = rig.Harness;
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
        h.Player.Radio.Play(h.Player.Radio.Rows.First(r => r.Station.Id == "pl-a").Station);
        h.Player.Radio.OnLiveState(h.Sessions[^1].Source, LiveState.Playing);
        h.Player.SyncPlayingState();

        Assert.True(h.PumpUntil(() =>
        {
            h.Player.Radio.RefreshBadge();
            return h.Player.HasFormatBadge;
        }), $"opis strumienia: „{h.Player.FormatBadge}”");

        Render();
        return rig;
    }

    private static Color Token(string key, ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException(key);

    private static Color Ink(TextBlock text) => Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color;

    /// <summary>Linia bazowa tekstu we współrzędnych okna.</summary>
    private static double Baseline(TextBlock text, Visual root)
    {
        var top = text.TranslatePoint(new Point(0, 0), root)!.Value.Y;
        return top + text.Padding.Top + text.TextLayout.TextLines[0].Baseline;
    }

    private static double Left(Visual visual, Visual root) => visual.TranslatePoint(new Point(0, 0), root)!.Value.X;

    private static double Right(Visual visual, Visual root) => Left(visual, root) + visual.Bounds.Width;

    /// <summary>Szerokość, jaką tekst zająłby bez przycinania.</summary>
    private static double NaturalWidth(TextBlock text)
    {
        var probe = new TextBlock
        {
            Text = text.Text,
            FontSize = text.FontSize,
            FontFamily = text.FontFamily,
            FontWeight = text.FontWeight,
            LetterSpacing = text.LetterSpacing,
        };

        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    /// <summary>Nazwy elementów w kolejności, w jakiej czyta je czytnik ekranu.</summary>
    private static List<string> ReadingOrder(Control root)
    {
        var names = new List<string>();

        void Walk(AutomationPeer peer)
        {
            // Ukryte elementy (np. etykieta drugiego trybu) czytnik pomija.
            if (peer is ControlAutomationPeer { Owner: var owner } && !owner.IsEffectivelyVisible) return;

            var name = peer.GetName();
            if (!string.IsNullOrEmpty(name)) names.Add(name);
            foreach (var child in peer.GetChildren()) Walk(child);
        }

        Walk(ControlAutomationPeer.CreatePeerForElement(root));
        return names;
    }

    /// <summary>Krój, którym tekst jest naprawdę złożony (pierwszy przebieg glifów).</summary>
    private static GlyphTypeface Rendered(TextBlock text) =>
        text.TextLayout.TextLines[0].TextRuns.OfType<Avalonia.Media.TextFormatting.ShapedTextRun>().First()
            .GlyphRun.GlyphTypeface;

    // ---------- Pliki ----------

    /// <summary>
    /// Opis jakości składa się naprawdę cięciem Noto Sans Light, a nie zwykłym, pogrubionym albo
    /// sztucznie odchudzonym: sama właściwość FontWeight nie wystarczy, gdy w paczce brakuje pliku.
    /// </summary>
    [Fact]
    public void OpisJakosciSkladaSieKrojemNotoSansLight() => Ui.Run(() =>
    {
        using var rig = OpenFile("Wykonawca");

        var quality = Rendered(rig.Quality);
        var artist = Rendered(rig.Artist);

        Assert.Equal(FontWeight.Light, quality.Weight);
        Assert.Equal(FontWeight.Normal, artist.Weight);
        Assert.Contains("Noto Sans", quality.FamilyName);
        Assert.NotSame(artist, quality);
    });

    [Fact]
    public void PlikZWykonawcaMaEtykieteZaWykonawcaWTymSamymWierszuMniejszymICienszymPismemIPrzygaszona() => Ui.Run(() =>
    {
        using var rig = OpenFile("Wykonawca");
        var artist = rig.Artist;
        var quality = rig.Quality;
        var variant = rig.Window.ActualThemeVariant;

        Assert.Equal("Wykonawca — Album", artist.Text);
        Assert.Equal(rig.Player.FormatBadge, quality.Text);
        Assert.StartsWith("WAV", quality.Text);
        Assert.True(artist.IsEffectivelyVisible);
        Assert.True(quality.IsEffectivelyVisible);
        Assert.True(rig.Line.ShowsLabel);

        // Ten sam wiersz: wspólny rodzic, wspólna linia bazowa, etykieta zaraz za wykonawcą z odstępem.
        Assert.Same(rig.Line, artist.Parent);
        Assert.Same(rig.Line, quality.Parent);
        Assert.Equal(Baseline(artist, rig.Window), Baseline(quality, rig.Window), 1.0);
        var gap = Left(quality, rig.Window) - Right(artist, rig.Window);
        Assert.Equal(QualityRow.GapPerEm * quality.FontSize, gap, 1.0);

        // Pismo mniejsze i cieńsze niż wykonawcy, barwa o stopień ciemniejsza (Text3 wobec Text2).
        Assert.True(quality.FontSize < artist.FontSize, $"{quality.FontSize} wobec {artist.FontSize}");
        Assert.True(quality.FontWeight < artist.FontWeight, $"{quality.FontWeight} wobec {artist.FontWeight}");
        Assert.Equal(MainWindow.QualityFontSize(artist.FontSize), quality.FontSize);
        Assert.Equal(Token("Text2Brush", variant), Ink(artist));
        Assert.Equal(Token("Text3Brush", variant), Ink(quality));
        Assert.Equal(TextTrimming.None, quality.TextTrimming);
        Assert.Equal(TextWrapping.NoWrap, quality.TextWrapping);

        // Etykieta w całości.
        Assert.True(quality.Bounds.Width >= NaturalWidth(quality) - 0.5);
    });

    [Fact]
    public void PlikBezWykonawcyPokazujeWWierszuSamaEtykieteJakosci() => Ui.Run(() =>
    {
        using var rig = OpenFile(null);

        Assert.False(rig.Player.HasShownLine);
        Assert.False(rig.Artist.IsVisible);
        Assert.True(rig.Line.IsEffectivelyVisible);
        Assert.True(rig.Quality.IsEffectivelyVisible);
        Assert.True(rig.Line.ShowsLabel);
        Assert.Equal(Left(rig.Line, rig.Window), Left(rig.Quality, rig.Window), 0.5);
        Assert.True(rig.Quality.Bounds.Width >= NaturalWidth(rig.Quality) - 0.5);
    });

    [Fact]
    public void DlugiWykonawcaSkracaSieWielokropkiemAEtykietaZostajeWCalosci() => Ui.Run(() =>
    {
        var name = string.Join(" ", Enumerable.Repeat("Bardzo długie nazwisko wykonawcy", 12));
        using var rig = OpenFile(name, width: 900);
        var artist = rig.Artist;
        var quality = rig.Quality;

        Assert.True(rig.Line.ShowsLabel);
        Assert.True(NaturalWidth(artist) > artist.Bounds.Width + 50, "wykonawca powinien się skracać");
        Assert.True(artist.Bounds.Width >= QualityRow.LeadMinEm * artist.FontSize);
        Assert.True(quality.Bounds.Width >= NaturalWidth(quality) - 0.5, $"etykieta ma {quality.Bounds.Width:F1} z {NaturalWidth(quality):F1} px");

        // Etykieta mieści się w wierszu, a wiersz w opisie utworu.
        Assert.True(Right(quality, rig.Window) <= Right(rig.Line, rig.Window) + 0.5);
        Assert.True(Right(rig.Line, rig.Window) <= Right(rig.Window.TrackInfo, rig.Window) + 0.5);
        Assert.Equal(Baseline(artist, rig.Window), Baseline(quality, rig.Window), 1.0);

        // Zmiana na krótki tekst oddaje miejsce: wykonawca bez wielokropka, etykieta zaraz za nim.
        rig.Harness.OpenPaths([rig.Harness.TaggedTrack("b", "Tytuł", "Krótki", "Album")]);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.ShownLine.StartsWith("Krótki", StringComparison.Ordinal)));
        Render();
        Assert.True(artist.Bounds.Width >= NaturalWidth(artist) - 0.5);
        Assert.Equal(QualityRow.GapPerEm * quality.FontSize, Left(quality, rig.Window) - Right(artist, rig.Window), 1.0);
    });

    [Fact]
    public void WBardzoWaskimWierszuEtykietaZnikaWCalosciZamiastSieUcinac() => Ui.Run(() =>
    {
        var artist = new TextBlock { Text = "Wykonawca o bardzo długiej nazwie — Album", FontSize = 17, TextTrimming = TextTrimming.CharacterEllipsis };
        var quality = new TextBlock { Text = "WAV 16 bit / 1536 kbps / 48 kHz", FontSize = 13.6 };
        var row = new QualityRow { Children = { artist, quality }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var host = new Border { Width = 150, Child = row, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var window = new Window { Width = 400, Height = 200, Content = host };
        window.Show();

        try
        {
            Render();
            Assert.False(row.ShowsLabel);
            Assert.Equal(0, quality.Bounds.Width);
            Assert.True(quality.ClipToBounds);
            Assert.Equal(150, artist.Bounds.Width, 0.5);

            // Bez wykonawcy etykieta mieszcząca się sama stoi w całości.
            artist.IsVisible = false;
            host.Width = 260;
            Render();
            Assert.True(row.ShowsLabel);
            Assert.True(quality.Bounds.Width >= NaturalWidth(quality) - 0.5);

            // Szerszy wiersz: etykieta wraca za wykonawcą.
            artist.IsVisible = true;
            host.Width = 400;
            Render();
            Assert.True(row.ShowsLabel);
            Assert.True(quality.Bounds.Width >= NaturalWidth(quality) - 0.5);
            Assert.True(quality.Bounds.Left >= artist.Bounds.Right);
        }
        finally { window.Close(); }
    });

    // ---------- Radio ----------

    [Fact]
    public void WRadiuEtykietaStoiZaNapisemNaZywoZOdstepemIJestBardziejPrzygaszona() => Ui.Run(() =>
    {
        using var rig = OpenRadio();
        var caption = rig.Window.LiveCaption;
        var quality = rig.Window.LiveQuality;
        var variant = rig.Window.ActualThemeVariant;

        Assert.Equal(Strings.Current["RadioLive"], caption.Text);
        Assert.Equal(rig.Player.FormatBadge, quality.Text);
        Assert.StartsWith("MP3", quality.Text);
        Assert.True(caption.IsEffectivelyVisible);
        Assert.True(quality.IsEffectivelyVisible);
        Assert.True(rig.Window.LiveLine.ShowsLabel);

        // Kropka zostaje tuż przed napisem; etykieta w tym samym wierszu, za napisem, z odstępem.
        Assert.Same(rig.Window.LiveDot.Parent, caption.Parent);
        Assert.Same(rig.Window.LiveLine, quality.Parent);
        Assert.Equal(Baseline(caption, rig.Window), Baseline(quality, rig.Window), 1.0);
        Assert.Equal(QualityRow.GapPerEm * quality.FontSize, Left(quality, rig.Window) - Right(caption, rig.Window), 1.0);

        // „Na żywo” w barwie Text2, etykieta o stopień ciemniej (Text3): mniejsze krycie przy tej samej barwie.
        Assert.Equal(Token("Text2Brush", variant), Ink(caption));
        Assert.Equal(Token("Text3Brush", variant), Ink(quality));
        Assert.True(Ink(quality).A < Ink(caption).A, $"{Ink(quality)} wobec {Ink(caption)}");

        // W radiu wiersz wykonawcy opisuje stację; etykiety w nim nie ma.
        Assert.False(rig.Quality.IsVisible);
        Assert.False(rig.Player.ShowsTrackQuality);
    });

    [Fact]
    public void PowrotDoPlikowPrzenosiEtykieteDoWierszaWykonawcy() => Ui.Run(() =>
    {
        using var rig = OpenRadio();
        rig.Harness.OpenPaths([rig.Harness.TaggedTrack("a", "Tytuł", "Wykonawca", "Album")]);

        rig.Player.SetSource(SourceMode.Files);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.ShowsTrackQuality), $"opis formatu: „{rig.Player.FormatBadge}”");
        Render();

        Assert.True(rig.Quality.IsEffectivelyVisible);
        Assert.StartsWith("WAV", rig.Quality.Text);
        Assert.False(rig.Window.LiveQuality.IsEffectivelyVisible);
    });

    // ---------- Ustawienie i dawne miejsce ----------

    [Fact]
    public void WylaczoneUstawienieChowaEtykieteWOknieIWRadiu() => Ui.Run(() =>
    {
        using var rig = OpenFile("Wykonawca");

        rig.Player.SetShowFormatBadge(false);
        Render();
        Assert.False(rig.Quality.IsEffectivelyVisible);
        Assert.False(rig.Line.ShowsLabel);
        Assert.True(rig.Artist.IsEffectivelyVisible);
        Assert.True(rig.Artist.Bounds.Width >= NaturalWidth(rig.Artist) - 0.5);

        rig.Player.SetShowFormatBadge(true);
        Render();
        Assert.True(rig.Quality.IsEffectivelyVisible);
        Assert.True(rig.Line.ShowsLabel);

        rig.Player.SetShowFormatBadge(false);
        rig.Player.SetSource(SourceMode.Radio);
        rig.Player.Radio.SelectedCountry = rig.Player.Radio.Countries.First(c => c.Key == "PL");
        rig.Player.Radio.Play(rig.Player.Radio.Rows.First(r => r.Station.Id == "pl-a").Station);
        rig.Player.Radio.OnLiveState(rig.Harness.Sessions[^1].Source, LiveState.Playing);
        rig.Harness.PumpUntil(() =>
        {
            rig.Player.Radio.RefreshBadge();
            return rig.Player.FormatBadge.Length > 0;
        });
        Render();
        Assert.True(rig.Window.LiveCaption.IsEffectivelyVisible);
        Assert.False(rig.Window.LiveQuality.IsEffectivelyVisible);
    });

    [Fact]
    public void BezPlikuWierszWykonawcyZnikaRazemZEtykieta() => Ui.Run(() =>
    {
        using var rig = Open();

        Assert.False(rig.Player.HasFormatBadge);
        Assert.False(rig.Line.IsVisible);
    });

    [Fact]
    public void NadTytulemNieMaJuzPlakietkiZOpisemFormatu() => Ui.Run(() =>
    {
        using var rig = OpenFile("Wykonawca");
        var badge = rig.Player.FormatBadge;

        var shown = rig.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == badge && t.IsEffectivelyVisible)
            .ToList();
        Assert.Same(rig.Quality, Assert.Single(shown));

        // Tytuł jest pierwszym widocznym elementem opisu utworu, a etykieta stoi pod nim.
        Assert.Same(rig.Window.TrackTitle, rig.Window.TrackInfo.Children.First(c => c.IsVisible));
        Assert.True(Left(rig.Quality, rig.Window) > Left(rig.Window.TrackTitle, rig.Window));
        Assert.True(rig.Quality.TranslatePoint(new Point(0, 0), rig.Window)!.Value.Y
                    > rig.Window.TrackTitle.TranslatePoint(new Point(0, 0), rig.Window)!.Value.Y);
    });

    // ---------- Dostępność ----------

    [Fact]
    public void CzytnikCzytaWykonawcePotemJakosc() => Ui.Run(() =>
    {
        using var rig = OpenFile("Wykonawca");
        var order = ReadingOrder(rig.Window.TrackInfo);

        var artist = order.IndexOf("Wykonawca — Album");
        var quality = order.IndexOf(rig.Player.FormatBadge);
        Assert.True(artist >= 0, string.Join(" | ", order));
        Assert.Equal(artist + 1, quality);
    });

    [Fact]
    public void CzytnikCzytaNaZywoPotemJakosc() => Ui.Run(() =>
    {
        using var rig = OpenRadio();
        var order = ReadingOrder(rig.Window.TrackInfo);

        var live = order.IndexOf(Strings.Current["RadioLive"]);
        Assert.Equal(live, order.LastIndexOf(Strings.Current["RadioLive"]));
        var quality = order.IndexOf(rig.Player.FormatBadge);
        Assert.True(live >= 0, string.Join(" | ", order));
        Assert.True(live + 1 == quality, string.Join(" | ", order));
    });
}
