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
        ContentArea.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty) FitHeightToContent();
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
    }

    // ================= Wysokość według treści =================

    /// <summary>
    /// Wysokość okna to wysokość treści: korektor, efekty i przełączniki widać w całości bez przewijania,
    /// także po zmianie języka, w którym opisy zajmują więcej wierszy. Najniższa wysokość jest taka sama,
    /// więc okna nie da się zmniejszyć tak, żeby coś zniknęło. Na niskim ekranie okno kończy się na
    /// obszarze roboczym, a resztę treści przewija pasek przewijania.
    /// </summary>
    private void FitHeightToContent()
    {
        var extent = ContentArea.Extent.Height;
        var viewport = ContentArea.Viewport.Height;
        if (extent <= 0 || viewport <= 0 || double.IsNaN(Height)) return;

        var needed = Math.Ceiling(Height - viewport + extent);
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

    private static bool KeepsSpace(IInputElement? focused) =>
        focused is Button or ToggleButton or GlassSwitch
        && ((StyledElement)focused).Classes.Contains(":focus-visible");

    private static bool KeepsArrowKeys(IInputElement? focused) =>
        focused is EqFader || focused is TrackSlider { Focusable: true };

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
