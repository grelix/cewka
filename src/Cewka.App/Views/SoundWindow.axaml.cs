using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cewka.App.Controls;
using Cewka.App.ViewModels;

namespace Cewka.App.Views;

/// <summary>
/// Okno „Korektor i efekty”: korektor, efekty dźwiękowe i dwa przełączniki przetwarzania. Niemodalne,
/// za pierwszym razem staje na środku okna głównego, potem pamięta położenie oraz rozmiar; otwiera je
/// przycisk w pasku tytułu okna głównego i klawisz Q. Zamknięcie okna zeruje <see cref="MainViewModel.PanelOpen"/>.
/// </summary>
public partial class SoundWindow : AuxiliaryWindow
{
    private readonly MainViewModel? _player;

    /// <summary>Wymagany przez wczytywanie XAML; program tworzy okno zawsze z modelem odtwarzacza.</summary>
    public SoundWindow()
    {
        InitializeComponent();
        UseChrome(TitleBar, Shell, ControlsLeft, ControlsRight,
            () => App.Settings.Current.SoundWindow, geometry => App.Settings.Current.SoundWindow = geometry,
            restoreSize: true);
        ContentScrollBar.Target = ContentArea;
        ContentArea.LayoutUpdated += (_, _) => FitHeightToContent();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
    }

    // ================= Wysokość według treści =================

    /// <summary>
    /// Wysokość okna to wysokość treści: korektor, efekty i przełączniki widać w całości bez przewijania,
    /// także po zmianie języka, w którym opisy zajmują więcej wierszy. Najniższa wysokość jest taka sama,
    /// więc okna nie da się zmniejszyć tak, żeby coś zniknęło. Na niskim ekranie okno kończy się na
    /// obszarze roboczym, a resztę treści przewija pasek przewijania.
    /// <para>
    /// Pułapka: miarą jest wysokość, jakiej chce treść, a nie zasięg przewijania. Treść rozciąga się do
    /// wysokości okna, więc zasięg rośnie razem z oknem i nie maleje, gdy treść się skurczy (lista pasm
    /// korektora parametrycznego dopasowuje się po przebiegu układu), a liczony od widoku sprzed zmiany
    /// rozmiaru kazał oknu skakać między dwiema wysokościami bez końca.
    /// </para>
    /// </summary>
    private void FitHeightToContent()
    {
        if (ContentArea.Content is not Control content || double.IsNaN(Height)) return;

        var extent = content.DesiredSize.Height;
        var chrome = TitleBar.Bounds.Height + Shell.BorderThickness.Top + Shell.BorderThickness.Bottom
                     + Shell.Padding.Top + Shell.Padding.Bottom;
        if (extent <= 0 || TitleBar.Bounds.Height <= 0) return;

        var needed = Math.Ceiling(chrome + extent);
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null) needed = Math.Min(needed, screen.WorkingArea.Height / screen.Scaling);

        if (Math.Abs(MinHeight - needed) > 0.5) MinHeight = needed;
        if (Math.Abs(Height - needed) <= 0.5) return;
        Height = needed;

        // Okno na środku zostaje na środku okna głównego, a żadne nie wychodzi dołem poza obszar roboczy.
        KeepPlacementAfterResize();
    }

    // ================= Klawiatura jak w oknie głównym =================

    /// <summary>
    /// Q zamyka okno jak w oknie głównym.
    /// Spacja i strzałki sterują odtwarzaniem, chyba że kontrolka z fokusem sama ich używa: przełącznik
    /// z fokusem z klawiatury bierze Spację, suwaki korektora i efektów biorą strzałki. Po kliknięciu
    /// przełącznika myszą Spacja znów steruje odtwarzaniem, jak w oknie głównym.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Q otwiera okno z okna głównego i przenosi tu fokus, więc tu też je zamyka.
        if (!e.Handled && e.Key == Key.Q && e.KeyModifiers == KeyModifiers.None && _player is not null)
        {
            _player.PanelOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Handled || e.Key is not (Key.Space or Key.Left or Key.Right or Key.Up or Key.Down)) return;

        var focused = FocusManager?.GetFocusedElement();
        if (KeepsArrowKeys(focused) || e.Key == Key.Space && KeepsSpace(focused)) return;

        PlaybackShortcuts?.Invoke(e);
    }

    /// <summary>Puszczenie Spacji też nie dociera do przycisku, bo ten klika właśnie przy puszczeniu.</summary>
    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !KeepsArrowKeys(focused) && !KeepsSpace(focused))
            e.Handled = true;
    }

    /// <summary>Pole liczbowe pasma korektora bierze Spację i strzałki zawsze: pisze się w nim i przesuwa kursor.</summary>
    private static bool KeepsSpace(IInputElement? focused) =>
        focused is TextBox
        || focused is Button or ToggleButton or GlassSwitch
        && ((StyledElement)focused).Classes.Contains(":focus-visible");

    private static bool KeepsArrowKeys(IInputElement? focused) =>
        focused is EqFader or TextBox || focused is TrackSlider { Focusable: true };

    public SoundWindow(MainViewModel player) : this()
    {
        _player = player;
        DataContext = player;
        player.PropertyChanged += OnPlayerChanged;
    }

    /// <summary>Panel korektora i efektów, dla testów.</summary>
    internal SoundPanel Panel => Sound;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Położenie przycisków okna zmienia się w Ustawieniach; to okno ma się przestawić od razu.
        if (e.PropertyName == nameof(MainViewModel.WindowControls)) ApplyControlsPosition();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Najpierw zdarzenie Closed: okno główne zapomina wtedy to okno, więc zerowanie stanu niżej
        // nie zamyka go drugi raz.
        base.OnClosed(e);

        if (_player is not null)
        {
            _player.PropertyChanged -= OnPlayerChanged;
            _player.PanelOpen = false;
        }
    }
}
