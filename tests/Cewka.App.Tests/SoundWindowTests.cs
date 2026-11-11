using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okno „Korektor i efekty” zamiast pasa w oknie głównym oraz ten sam panel w Ustawieniach.</summary>
[Collection(UiCollection.Name)]
public sealed class SoundWindowTests
{
    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (MainWindow Window, PlayerHarness Harness) Open()
    {
        App.Settings.Current.Window = null;
        App.Settings.Current.SoundWindow = null;
        var h = new PlayerHarness(s => s.WindowControls = WindowControlsPosition.Right);
        var window = new MainWindow(false, h.Player) { Width = 1100, Height = 700 };
        window.Show();
        Settle();
        return (window, h);
    }

    private static void Press(Window window, Key key)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

        // Okno zamknięte naciśnięciem klawisza nie przyjmie już jego puszczenia.
        if (!closed)
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void OknoGlowneNieMaJuzKorektoraAQOtwieraIZamykaOknoKorektora() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            // Po uruchomieniu okno korektora jest zamknięte, a okno główne nie ma suwaków korektora.
            Assert.False(h.Player.PanelOpen);
            Assert.Null(window.OpenSoundWindow);
            Assert.Empty(window.GetVisualDescendants().OfType<EqFader>());

            Press(window, Key.Q);
            Settle();
            var sound = Assert.IsType<SoundWindow>(window.OpenSoundWindow);
            Assert.True(sound.IsVisible);
            Assert.Same(window, sound.Owner);
            Assert.Equal(11, sound.GetVisualDescendants().OfType<EqFader>().Count());
            Assert.Equal(h.Player.SoundEffects.Count, sound.GetVisualDescendants().OfType<TrackSlider>().Count());

            Press(window, Key.Q);
            Settle();
            Assert.Null(window.OpenSoundWindow);
            Assert.False(sound.IsVisible);
        }
        finally { window.Close(); }
    });

    /// <summary>Po otwarciu fokus jest w oknie korektora, więc i tam Q je zamyka, a Ctrl+Q nie.</summary>
    [Fact]
    public void QWOknieKorektoraZamykaJe() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            Press(window, Key.Q);
            Settle();
            var sound = window.OpenSoundWindow!;

            sound.KeyPress(Key.Q, RawInputModifiers.Control, PhysicalKey.None, null);
            sound.KeyRelease(Key.Q, RawInputModifiers.Control, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(h.Player.PanelOpen);

            Press(sound, Key.Q);
            Settle();
            Assert.False(h.Player.PanelOpen);
            Assert.Null(window.OpenSoundWindow);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ZamkniecieOknaKorektoraZerujeStanIZapamietujePolozenie() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Player.PanelOpen = true;
            Settle();
            var sound = window.OpenSoundWindow!;
            sound.Width = 950;

            Press(sound, Key.Escape);
            Settle();

            Assert.False(h.Player.PanelOpen);
            Assert.Null(window.OpenSoundWindow);
            var saved = Assert.IsType<WindowGeometry>(App.Settings.Current.SoundWindow);
            Assert.Equal(950, saved.Width);

            // Ponowne otwarcie wraca z zapamiętanym rozmiarem.
            h.Player.PanelOpen = true;
            Settle();
            Assert.Equal(950, window.OpenSoundWindow!.Width);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Przycisk „Zamknij” w oknie korektora zamyka je raz. Wcześniej zerowanie stanu po zamknięciu
    /// zamykało to samo okno drugi raz, a drugi zapis położenia był już z zamkniętego okna (0, 0),
    /// więc następne otwarcie stawało w lewym górnym rogu ekranu.
    /// </summary>
    [Fact]
    public void ZamkniecieWlasnymPrzyciskiemZapisujePolozenieRaz() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Player.PanelOpen = true;
            Settle();
            var sound = window.OpenSoundWindow!;
            sound.Position = new PixelPoint(321, 234);
            var closings = 0;
            sound.Closing += (_, _) => closings++;

            sound.Close();
            Settle();

            Assert.Equal(1, closings);
            Assert.False(h.Player.PanelOpen);
            Assert.Null(window.OpenSoundWindow);
            var saved = Assert.IsType<WindowGeometry>(App.Settings.Current.SoundWindow);
            Assert.Equal((321, 234), (saved.X, saved.Y));
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Okno korektora pokazuje całą treść bez przewijania: korektor, efekty i przełączniki
    /// przetwarzania. Sprawdzane w szerokości domyślnej i najmniejszej, w każdym języku.
    /// </summary>
    [Theory]
    [InlineData(900)]
    [InlineData(760)]
    public void OknoKorektoraMiesciCalaTresc(double width) => Ui.Run(() =>
    {
        var (window, h) = Open();
        var language = Cewka.App.Localisation.Strings.Current.CurrentCode;
        try
        {
            foreach (var code in Cewka.App.Localisation.Strings.Available.Select(l => l.Code))
            {
                Cewka.App.Localisation.Strings.Current.SetLanguage(code);
                h.Player.PanelOpen = true;
                Settle();
                var sound = window.OpenSoundWindow!;
                sound.Width = width;
                Settle();

                var content = sound.FindControl<ScrollViewer>("ContentArea")!;
                Assert.True(content.Extent.Height <= content.Viewport.Height + 0.5,
                    $"{code}, {width}: treść {content.Extent.Height:0} > widoczne {content.Viewport.Height:0}");

                h.Player.PanelOpen = false;
                Settle();
            }
        }
        finally
        {
            Cewka.App.Localisation.Strings.Current.SetLanguage(language);
            window.Close();
        }
    });

    /// <summary>
    /// Środek okna korektora leży na środku okna głównego, a okno jest w całości w obszarze roboczym,
    /// także po dopasowaniu wysokości do treści, które następuje już po pokazaniu.
    /// </summary>
    private static void AssertCentredOver(MainWindow window, SoundWindow sound)
    {
        var scaling = sound.RenderScaling;
        var height = (int)Math.Ceiling(sound.Height * scaling);
        var width = (int)Math.Ceiling(sound.Width * scaling);
        var ownerX = window.Position.X + window.ClientSize.Width * window.RenderScaling / 2;
        var ownerY = window.Position.Y + window.ClientSize.Height * window.RenderScaling / 2;

        var detail = $"okno {sound.Position} {width}×{height}, główne {window.Position} "
                     + $"{window.ClientSize.Width:0}×{window.ClientSize.Height:0}, środek {ownerX:0}×{ownerY:0}";
        Assert.True(Math.Abs(sound.Position.X + width / 2.0 - ownerX) <= 1, detail);
        Assert.True(Math.Abs(sound.Position.Y + height / 2.0 - ownerY) <= 1, detail);

        var area = (sound.Screens.ScreenFromWindow(window) ?? sound.Screens.Primary)!.WorkingArea;
        Assert.True(area.Contains(new PixelRect(sound.Position, new PixelSize(width, height))), $"{sound.Position}, {width}×{height}");
    }

    [Fact]
    public void OknoKorektoraOtwieraSieNaSrodkuGlownego() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            window.Position = new PixelPoint(250, 180);
            Settle();

            h.Player.PanelOpen = true;
            Settle();

            AssertCentredOver(window, window.OpenSoundWindow!);
        }
        finally { window.Close(); }
    });

    /// <summary>Okno korektora otwarte w czasie sceny pokazuje się po niej na środku okna głównego.</summary>
    [Fact]
    public void OknoKorektoraOtwarteWScenieStajePoNiejNaSrodkuGlownego() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            window.EnterScene();
            Settle();
            h.Player.PanelOpen = true;
            Settle();
            Assert.False(window.OpenSoundWindow!.IsVisible);

            Press(window, Key.Escape);
            Settle();

            var sound = window.OpenSoundWindow!;
            Assert.True(sound.IsVisible);
            AssertCentredOver(window, sound);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Okno przesunięte ręcznie po pierwszym otwarciu zostaje na miejscu po scenie: scena je chowa,
    /// a po niej pokazuje znowu, co ponownie woła OnOpened.
    /// </summary>
    [Fact]
    public void PrzesunieteOknoKorektoraZostajeNaMiejscuPoScenie() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Player.PanelOpen = true;
            Settle();
            var sound = window.OpenSoundWindow!;
            AssertCentredOver(window, sound);

            var moved = new PixelPoint(sound.Position.X + 140, sound.Position.Y + 60);
            sound.Position = moved;
            Settle();

            window.EnterScene();
            Settle();
            Assert.False(sound.IsVisible);

            Press(window, Key.Escape);
            Settle();

            Assert.True(sound.IsVisible);
            Assert.Equal(moved, sound.Position);
        }
        finally { window.Close(); }
    });

    /// <summary>Okno przesunięte ręcznie nie wraca na środek, gdy zmienia się jego wysokość.</summary>
    [Fact]
    public void PrzesunieteOknoKorektoraNieWracaNaSrodek() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Player.PanelOpen = true;
            Settle();
            var sound = window.OpenSoundWindow!;

            sound.Position = new PixelPoint(321, 234);
            sound.Height -= 60;
            Settle();

            Assert.Equal(new PixelPoint(321, 234), sound.Position);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ScenaChowaOknoKorektoraAPoJejZamknieciuWraca() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Player.PanelOpen = true;
            Settle();
            var sound = window.OpenSoundWindow!;

            window.EnterScene();
            Settle();
            Assert.False(sound.IsVisible);
            Assert.True(h.Player.PanelOpen);

            Press(window, Key.Escape);
            Settle();
            Assert.False(window.IsSceneOpen);
            Assert.True(sound.IsVisible);
            Assert.Same(sound, window.OpenSoundWindow);
        }
        finally { window.Close(); }
    });

    /// <summary>Ten sam panel w Ustawieniach: karty jedna pod drugą, opisy efektów widoczne, zmiana działa w obu miejscach.</summary>
    [Fact]
    public void UstawieniaPokazujaKorektorIEfektyJednaPodDruga() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();
        try
        {
            settings.SelectSection(settings.Processing);
            Settle();

            var panel = window.GetVisualDescendants().OfType<SoundPanel>().Single();
            Assert.True(panel.IsEffectivelyVisible);
            Assert.True(panel.Stacked && panel.ShowDescriptions);
            Assert.Same(h.Player, panel.DataContext);

            var equaliser = panel.EqualiserCard.Bounds;
            var effects = panel.EffectsCard.Bounds;
            Assert.True(effects.Y >= equaliser.Bottom, $"efekty {effects} pod korektorem {equaliser}");
            Assert.Equal(equaliser.Width, effects.Width, 0.5);

            var description = h.Player.SoundEffects[0].Description;
            Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == description && t.IsEffectivelyVisible);

            h.Player.Bands[4].Gain = 5;
            Settle();
            Assert.True(panel.EqResetButton.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    });

    /// <summary>W oknie korektora karty stoją obok siebie, a opisy efektów są tylko w podpowiedziach.</summary>
    [Fact]
    public void OknoKorektoraMaKartyObokSiebieBezOpisow() => Ui.Run(() =>
    {
        var (window, h) = Open();
        try
        {
            h.Player.PanelOpen = true;
            Settle();
            var panel = window.OpenSoundWindow!.Panel;

            Assert.False(panel.Stacked);
            Assert.True(panel.EffectsCard.Bounds.X >= panel.EqualiserCard.Bounds.Right);
            Assert.Equal(panel.EqualiserCard.Bounds.Y, panel.EffectsCard.Bounds.Y, 0.5);

            var description = h.Player.SoundEffects[0].Description;
            Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == description && t.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    });
}
