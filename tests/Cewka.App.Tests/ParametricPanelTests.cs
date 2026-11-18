using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Korektor parametryczny w panelu dźwięku: nazwy dla czytnika ekranu, klawiatura, pola liczbowe,
/// wykres i rozmiar okna „Korektor i efekty”.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ParametricPanelTests
{
    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static SoundWindow OpenWindow(PlayerHarness h, Func<KeyEventArgs, bool>? shortcuts = null)
    {
        App.Settings.Current.SoundWindow = null;
        var window = new SoundWindow(h.Player) { PlaybackShortcuts = shortcuts };
        ToolTip.SetServiceEnabled(window, false);
        window.Show();
        Settle();
        return window;
    }

    private static PlayerHarness WithBands(int count)
    {
        var h = new PlayerHarness();
        h.Player.EqualiserMode = EqualiserMode.Parametric;
        for (var i = 0; i < count; i++) h.Player.Parametric.AddBand();
        return h;
    }

    private static List<Control> Focusables(Visual root) =>
        [.. root.GetVisualDescendants().OfType<Control>().Where(c => c.Focusable && c.IsEffectivelyVisible && c is not ScrollViewer)];

    private static string? NameOf(Control control) => ControlAutomationPeer.CreatePeerForElement(control).GetName();

    [Fact]
    public void KazdaKontrolkaTrybuParametrycznegoMaNazwe() => Ui.Run(() =>
    {
        using var h = WithBands(3);
        var window = OpenWindow(h);
        try
        {
            var unnamed = Focusables(window.Panel)
                .Where(c => string.IsNullOrWhiteSpace(NameOf(c)))
                .Select(c => c.GetType().Name)
                .ToList();
            Assert.True(unnamed.Count == 0, string.Join(", ", unnamed));

            var chart = window.Panel.GetVisualDescendants().OfType<EqResponseChart>().Single();
            var peer = ControlAutomationPeer.CreatePeerForElement(chart);
            Assert.Equal(Strings.Current["EqualiserResponseName"], peer.GetName());
            Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());

            var bar = window.Panel.GetVisualDescendants().OfType<SegmentBar>().Single();
            Assert.Equal(Strings.Current["SettingEqualiserMode"], AutomationProperties.GetName(bar));
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Każda kontrolka wiersza z fokusem (przełącznik, rodzaje filtru, pola, usunięcie) ma w nazwie numer
    /// swojego pasma, także po usunięciu pasma ze środka listy, kiedy numery się przesuwają. Nazwy
    /// w panelu się nie powtarzają: przy wielu pasmach czytnik nie ogłasza dwudziestu „Usuń pasmo”.
    /// </summary>
    [Fact]
    public void KazdaKontrolkaWierszaMaNumerPasma() => Ui.Run(() =>
    {
        using var h = WithBands(3);
        var window = OpenWindow(h);
        try
        {
            void Check(int expectedControls)
            {
                var controls = Focusables(window.Panel).Where(c => c.DataContext is ParametricBandViewModel).ToList();
                Assert.Equal(expectedControls, controls.Count);

                foreach (var control in controls)
                {
                    var band = (ParametricBandViewModel)control.DataContext!;
                    var number = h.Player.Parametric.Bands.IndexOf(band) + 1;
                    var prefix = string.Format(Strings.Current["EqualiserBandName"], number);
                    var name = NameOf(control) ?? string.Empty;
                    Assert.True(name == prefix || name.StartsWith(prefix + ",", StringComparison.Ordinal),
                        $"{control.GetType().Name}: „{name}” bez „{prefix}”");
                }

                var names = controls.Select(NameOf).ToList();
                Assert.Equal(names.Count, names.Distinct().Count());
            }

            Check(3 * 8);
            Assert.Contains(string.Format(Strings.Current["EqualiserBandName"], 2) + ", " + Strings.Current["TooltipEqualiserRemoveBand"],
                Focusables(window.Panel).Select(NameOf));
            Assert.Contains(string.Format(Strings.Current["EqualiserBandName"], 3) + ", " + Strings.Current["FilterLowShelf"],
                Focusables(window.Panel).Select(NameOf));

            h.Player.Parametric.RemoveBand(h.Player.Parametric.Bands[1]);
            Settle();
            Check(2 * 8);
        }
        finally { window.Close(); }
    });

    /// <summary>Pola wiersza mają w nazwie numer pasma, a pola różnych wierszy różne nazwy.</summary>
    [Fact]
    public void NazwyPolPodajaNumerPasma() => Ui.Run(() =>
    {
        using var h = WithBands(2);
        var window = OpenWindow(h);
        try
        {
            var names = window.Panel.GetVisualDescendants().OfType<NumberField>().Select(NameOf).ToList();
            Assert.Equal(
            [
                string.Format(Strings.Current["EqualiserBandFrequencyName"], 1),
                string.Format(Strings.Current["EqualiserBandGainName"], 1),
                string.Format(Strings.Current["EqualiserBandQName"], 1),
                string.Format(Strings.Current["EqualiserBandFrequencyName"], 2),
                string.Format(Strings.Current["EqualiserBandGainName"], 2),
                string.Format(Strings.Current["EqualiserBandQName"], 2),
            ], names);

            var switches = window.Panel.GetVisualDescendants().OfType<GlassSwitch>()
                .Where(s => s.DataContext is ParametricBandViewModel).Select(NameOf);
            Assert.Equal([string.Format(Strings.Current["EqualiserBandName"], 1), string.Format(Strings.Current["EqualiserBandName"], 2)], switches);
        }
        finally { window.Close(); }
    });

    /// <summary>Tabulator przechodzi wierszami: przełącznik, trzy rodzaje filtru, trzy pola, usunięcie, potem następny wiersz.</summary>
    [Fact]
    public void TabulatorIdzieWierszami() => Ui.Run(() =>
    {
        using var h = WithBands(2);
        var window = OpenWindow(h);
        try
        {
            var rows = window.Panel.GetVisualDescendants().OfType<GlassSwitch>()
                .Where(s => s.DataContext is ParametricBandViewModel).ToList();

            var order = new List<Control>();
            rows[0].Focus(NavigationMethod.Tab);
            for (var i = 0; i < 16; i++)
            {
                order.Add((Control)window.FocusManager!.GetFocusedElement()!);
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
            }

            string Describe(Control c) => c switch
            {
                GlassSwitch => "przełącznik",
                OptionButton => "filtr",
                NumberField field => field.Kind.ToString(),
                Button => "usuń",
                _ => c.GetType().Name,
            };

            string[] row = ["przełącznik", "filtr", "filtr", "filtr", "Frequency", "Gain", "Q", "usuń"];
            Assert.Equal([.. row, .. row], order.Select(Describe));
            Assert.All(order.Take(8), c => Assert.Same(h.Player.Parametric.Bands[0], c.DataContext));
            Assert.All(order.Skip(8), c => Assert.Same(h.Player.Parametric.Bands[1], c.DataContext));
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Pole liczbowe z klawiatury: Enter zatwierdza, strzałki zmieniają o krok, wartość spoza zakresu
    /// jest przycinana, tekst nieliczbowy wraca do poprzedniej wartości przy wyjściu z pola.
    /// </summary>
    [Fact]
    public void PoleLiczboweZKlawiatury() => Ui.Run(() =>
    {
        using var h = WithBands(1);
        var window = OpenWindow(h);
        try
        {
            var band = h.Player.Parametric.Bands[0];
            var fields = window.Panel.GetVisualDescendants().OfType<NumberField>().ToList();
            var (frequency, gain, q) = (fields[0], fields[1], fields[2]);

            void Type(NumberField field, string text, Key key)
            {
                field.Focus();
                field.SelectAll();
                window.KeyTextInput(text);
                window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
            }

            Type(gain, "2,5", Key.Enter);
            Assert.Equal(2.5, band.Gain);
            Assert.Equal("+2,5 dB", gain.Text);
            Assert.Equal(2.5, Assert.Single(h.Engine.Graph.Parametric.Bands).Gain);

            Type(gain, "-3.5 dB", Key.Enter);
            Assert.Equal(-3.5, band.Gain);
            Assert.Equal("−3,5 dB", gain.Text);

            Type(gain, "99", Key.Enter);
            Assert.Equal(24, band.Gain);

            Type(gain, "abc", Key.Tab);
            Assert.Equal(24, band.Gain);
            Assert.Equal("+24,0 dB", gain.Text);

            gain.Focus();
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal(23.5, band.Gain);

            frequency.Focus();
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal(1000 * Math.Pow(2, 1.0 / 12), band.Frequency, 6);
            Assert.Equal("1059 Hz", frequency.Text);

            // Przejście przez pole bez zmiany tekstu nie zaokrągla wartości ustawionej strzałką.
            gain.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1000 * Math.Pow(2, 1.0 / 12), band.Frequency, 6);

            Type(q, "0,01", Key.Enter);
            Assert.Equal(0.1, band.Q);
            q.Focus();
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal(0.15, band.Q, 9);

            Type(frequency, "25000", Key.Enter);
            Assert.Equal(20000, band.Frequency);
        }
        finally { window.Close(); }
    });

    /// <summary>Strzałki w polu nie przewijają utworu: okno korektora oddaje je polu, nie skrótom odtwarzania.</summary>
    [Fact]
    public void StrzalkiWPoluNieSterujaOdtwarzaniem() => Ui.Run(() =>
    {
        using var h = WithBands(1);
        var shortcuts = 0;
        var window = OpenWindow(h, _ =>
        {
            shortcuts++;
            return true;
        });
        try
        {
            var gain = window.Panel.GetVisualDescendants().OfType<NumberField>().First(f => f.Kind == NumberFieldKind.Gain);
            gain.Focus();
            foreach (var key in (Key[])[Key.Up, Key.Left, Key.Right, Key.Space, Key.M, Key.Home])
            {
                window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
            }

            Assert.Equal(0, shortcuts);
            Assert.Equal(0.5, h.Player.Parametric.Bands[0].Gain);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ZapisLiczbIdzieZaJezykiem() => Ui.Run(() =>
    {
        var previous = Strings.Current.CurrentCode;
        try
        {
            Strings.Current.SetLanguage("en");
            Assert.Equal("1.41", NumberField.Format(NumberFieldKind.Q, 1.41, Strings.Current.Numbers));
            Assert.Equal("−2.5 dB", NumberField.Format(NumberFieldKind.Gain, -2.5, Strings.Current.Numbers));

            Strings.Current.SetLanguage("pl");
            Assert.Equal("1,41", NumberField.Format(NumberFieldKind.Q, 1.41, Strings.Current.Numbers));
            Assert.Equal("62,5 Hz", NumberField.Format(NumberFieldKind.Frequency, 62.5, Strings.Current.Numbers));
            Assert.Equal("16000 Hz", NumberField.Format(NumberFieldKind.Frequency, 16000, Strings.Current.Numbers));

            Assert.True(NumberField.TryParse("1,5", out var comma) && comma == 1.5);
            Assert.True(NumberField.TryParse("1.5", out var point) && point == 1.5);
            Assert.True(NumberField.TryParse("−2,5 dB", out var minus) && minus == -2.5);
            Assert.True(NumberField.TryParse(" 440 Hz ", out var hertz) && hertz == 440);
            Assert.False(NumberField.TryParse("abc", out _));
            Assert.False(NumberField.TryParse("", out _));
            Assert.False(NumberField.TryParse("NaN", out _));
        }
        finally
        {
            Strings.Current.SetLanguage(previous);
        }
    });

    [Fact]
    public void PrzyciskiFiltruIUsunieciaDzialaja() => Ui.Run(() =>
    {
        using var h = WithBands(2);
        var window = OpenWindow(h);
        try
        {
            var first = h.Player.Parametric.Bands[0];
            var shelf = window.Panel.GetVisualDescendants().OfType<OptionButton>()
                .First(b => b.DataContext == first && Equals(b.Tag, ParametricFilterType.LowShelf));

            ControlAutomationPeer.CreatePeerForElement(shelf).GetProvider<Avalonia.Automation.Provider.IInvokeProvider>()!.Invoke();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ParametricFilterType.LowShelf, first.Type);
            Assert.True(shelf.IsSelected);

            var remove = window.Panel.GetVisualDescendants().OfType<Button>()
                .First(b => b.DataContext == first && b is not OptionButton);
            remove.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Settle();

            var left = Assert.Single(h.Player.Parametric.Bands);
            Assert.NotSame(first, left);

            // Fokus przeszedł do przycisku usunięcia wiersza, który zajął miejsce usuniętego.
            var focused = window.FocusManager?.GetFocusedElement() as Control;
            Assert.Same(left, focused?.DataContext);
        }
        finally { window.Close(); }
    });

    /// <summary>Pusta lista pokazuje opis, „Dodaj pasmo” jest nieaktywne przy 20 pasmach, „Eksportuj” przy pustej liście.</summary>
    [Fact]
    public void PustaListaIGranicaPasm() => Ui.Run(() =>
    {
        using var h = WithBands(0);
        var window = OpenWindow(h);
        try
        {
            var panel = window.Panel;
            var empty = panel.FindControl<TextBlock>("NoBands")!;
            var add = panel.FindControl<Button>("AddBandButton")!;
            var export = panel.FindControl<Button>("ExportButton")!;

            Assert.True(empty.IsEffectivelyVisible);
            Assert.Equal(Strings.Current["EqualiserNoBands"], empty.Text);
            Assert.False(export.IsEffectivelyEnabled);

            add.Focus();
            for (var i = 0; i < 20; i++)
            {
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                add.Focus();
            }

            Settle();
            Assert.Equal(20, h.Player.Parametric.Bands.Count);
            Assert.False(empty.IsEffectivelyVisible);
            Assert.False(add.IsEffectivelyEnabled);
            Assert.True(export.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Okno „Korektor i efekty” w trybie parametrycznym: przy dziesięciu pasmach sześć wierszy widać
    /// w całości, a reszta się przewija. Okno jest wyższe niż w trybie graficznym i wraca do dawnej
    /// wysokości po przełączeniu z powrotem. Karta efektów rozciąga się do karty korektora bez zmiany
    /// odstępów między efektami.
    /// </summary>
    [Theory]
    [InlineData(900.0, "pl")]
    [InlineData(760.0, "pl")]
    [InlineData(760.0, "de")]
    public void OknoKorektoraPokazujeSzescPasm(double width, string language) => Ui.Run(() =>
    {
        var previous = Strings.Current.CurrentCode;
        Strings.Current.SetLanguage(language);
        using var h = new PlayerHarness();
        var window = OpenWindow(h);
        try
        {
            window.Width = width;
            Settle();
            var graphicHeight = window.Height;
            var effects = window.Panel.FindControl<ItemsControl>("EffectList")!;
            var effectsTop = effects.TranslatePoint(default, window.Panel)!.Value.Y;
            var effectsHeight = effects.Bounds.Height;

            h.Player.EqualiserMode = EqualiserMode.Parametric;
            for (var i = 0; i < 10; i++) h.Player.Parametric.AddBand();
            h.Player.ExportEqualiser(Path.Combine(h.Directory, "zestaw.txt"));
            Assert.True(h.Player.Parametric.HasMessage);
            Settle();

            var parametricHeight = window.Height;
            Assert.True(parametricHeight > graphicHeight, $"{parametricHeight} wobec {graphicHeight}");

            var scroll = window.Panel.FindControl<ScrollViewer>("BandScroll")!;
            Assert.Equal(SoundPanel.SideBySideRows, FullyVisibleRows(window.Panel, scroll));
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);

            // Karta efektów ma wysokość karty korektora, a efekty stoją tam, gdzie stały.
            var equaliserCard = window.Panel.FindControl<Border>("EqualiserCard")!;
            var effectsCard = window.Panel.FindControl<Border>("EffectsCard")!;
            Assert.Equal(equaliserCard.Bounds.Height, effectsCard.Bounds.Height, 1);
            Assert.Equal(effectsTop, effects.TranslatePoint(default, window.Panel)!.Value.Y, 1);
            Assert.Equal(effectsHeight, effects.Bounds.Height, 1);

            // Mniej niż sześć pasm i brak pasm: okno nie skacze.
            while (h.Player.Parametric.Bands.Count > 2) h.Player.Parametric.RemoveBand(h.Player.Parametric.Bands[^1]);
            Settle();
            Assert.Equal(parametricHeight, window.Height);
            h.Player.Parametric.Reset();
            Settle();
            Assert.Equal(parametricHeight, window.Height);

            h.Player.EqualiserMode = EqualiserMode.Graphic;
            Settle();
            Assert.Equal(graphicHeight, window.Height);
        }
        finally
        {
            window.Close();
            Strings.Current.SetLanguage(previous);
        }
    });

    /// <summary>Wiersze listy pasm, które w polu widoku listy stoją w całości.</summary>
    private static int FullyVisibleRows(Visual panel, ScrollViewer scroll)
    {
        var top = scroll.TranslatePoint(default, panel)!.Value.Y;
        var bottom = top + scroll.Viewport.Height;
        return scroll.GetVisualDescendants().OfType<Grid>()
            .Where(row => row.DataContext is ParametricBandViewModel && row.Parent is Avalonia.Controls.Presenters.ContentPresenter)
            .Select(row => row.TranslatePoint(default, panel)!.Value.Y is var y ? (y, y + row.Bounds.Height) : default)
            .Count(row => row.Item1 >= top - 0.5 && row.Item2 <= bottom + 0.5);
    }

    /// <summary>W Ustawieniach lista rośnie z liczbą pasm do dziesięciu wierszy, bez stałej wysokości.</summary>
    [Fact]
    public void WUstawieniachListaJestWyzsza() => Ui.Run(() =>
    {
        var panel = new SoundPanel { Stacked = true };
        Assert.Equal(SoundPanel.StackedListHeight, panel.FindControl<ScrollViewer>("BandScroll")!.MaxHeight);
        Assert.True(double.IsNaN(panel.FindControl<Panel>("BandArea")!.Height));

        panel.Stacked = false;
        Assert.Equal(
            SoundPanel.SideBySideRows * SoundPanel.BandRowHeight + (SoundPanel.SideBySideRows - 1) * SoundPanel.BandRowSpacing,
            panel.FindControl<Panel>("BandArea")!.Height);
    });

    [Fact]
    public void WykresPoszerzaOsPrzyDuzychWzmocnieniach() => Ui.Run(() =>
    {
        using var h = WithBands(1);
        var window = OpenWindow(h);
        try
        {
            var chart = window.Panel.GetVisualDescendants().OfType<EqResponseChart>().Single();
            Assert.Equal(12, chart.RangeDb);

            h.Player.Parametric.Bands[0].Gain = 18;
            Settle();
            Assert.Equal(24, chart.RangeDb);
            Assert.Same(h.Player.Parametric.Curve, chart.Bands);

            h.Player.Parametric.Bands[0].Enabled = false;
            Settle();
            Assert.Equal(12, chart.RangeDb);

            // Przedwzmacniacz przesuwa krzywą.
            var result = new double[1];
            EqResponseChart.Compute([new ParametricBand(1, ParametricFilterType.Peak, 1000, 6, 1)], -6, [1000.0], result);
            Assert.Equal(0, result[0], 6);
        }
        finally { window.Close(); }
    });

    /// <summary>Pod tytułem karty stoi urządzenie, do którego należą ustawienia, gdy opcja jest włączona.</summary>
    [Fact]
    public void NaglowekKartyPokazujeUrzadzenieProfilu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a")]);
        var window = OpenWindow(h);
        try
        {
            var device = window.Panel.FindControl<TextBlock>("EqDevice")!;
            Assert.False(device.IsEffectivelyVisible);

            h.Player.SoundProfilesEnabled = true;
            Settle();
            Assert.True(device.IsEffectivelyVisible);
            Assert.Equal(string.Format(Strings.Current["SoundProfileDevice"], PlayerHarness.DefaultDevice), device.Text);
        }
        finally { window.Close(); }
    });

    /// <summary>Przełącznik „Osobne ustawienia dla urządzeń” stoi na końcu karty „Przetwarzanie”.</summary>
    [Fact]
    public void PrzelacznikProfiliNaKoncuKartyPrzetwarzania() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();
        try
        {
            settings.SelectSection(settings.Processing);
            Settle();

            var toggle = window.GetVisualDescendants().OfType<GlassSwitch>().Single(s => s.Name == "SoundProfilesSwitch");
            Assert.Equal(Strings.Current["SettingSoundProfiles"], NameOf(toggle));

            var card = toggle.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("section"));
            var lastSwitch = card.GetVisualDescendants().OfType<GlassSwitch>().Last();
            Assert.Same(toggle, lastSwitch);

            toggle.IsChecked = true;
            Assert.True(h.Player.SoundProfilesEnabled);
            Assert.True(settings.SoundProfilesEnabled);
        }
        finally { window.Close(); }
    });
}
