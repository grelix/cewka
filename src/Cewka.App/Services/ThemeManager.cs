using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Cewka.App.Models;

namespace Cewka.App.Services;

/// <summary>
/// Keeps <see cref="Application.RequestedThemeVariant"/> in step with the chosen
/// <see cref="ThemeMode"/>. In <see cref="ThemeMode.System"/> it mirrors the operating
/// system and reacts to changes without a restart.
/// </summary>
public sealed class ThemeManager
{
    private readonly Application _application;
    private IPlatformSettings? _platformSettings;

    public ThemeManager(Application application, ThemeMode initialMode)
    {
        _application = application;
        Mode = initialMode;

        _platformSettings = application.PlatformSettings;
        if (_platformSettings is not null)
            _platformSettings.ColorValuesChanged += OnSystemColorsChanged;

        Apply();
    }

    /// <summary>Raised after the effective variant changes, whatever the cause.</summary>
    public event EventHandler? Changed;

    /// <summary>Wybrany tryb się zmienił, nawet gdy wariant na ekranie został ten sam.</summary>
    public event EventHandler? ModeChanged;

    public ThemeMode Mode { get; private set; }

    /// <summary>
    /// Wariant obowiązujący w tej chwili, odczytany z aplikacji, bo narzędzie zrzutów ustawia
    /// <c>RequestedThemeVariant</c> wprost z pominięciem <see cref="Mode"/>.
    /// </summary>
    public bool IsDark => _application.ActualThemeVariant == ThemeVariant.Dark;

    public void SetMode(ThemeMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Apply();
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Advances the mode for the header button. The cycle deliberately starts from
    /// what is on screen, so the first press always visibly flips the palette.
    /// </summary>
    public ThemeMode Cycle()
    {
        var next = Mode switch
        {
            ThemeMode.System => IsDark ? ThemeMode.Light : ThemeMode.Dark,
            ThemeMode.Dark => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.System,
            _ => ThemeMode.System,
        };

        SetMode(next);
        return next;
    }

    private void OnSystemColorsChanged(object? sender, PlatformColorValues e)
    {
        if (Mode != ThemeMode.System) return;

        // The platform may raise this off the UI thread.
        Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>
    /// Zgłasza zmianę tylko przy innym wariancie. Platforma zgłasza zmianę barw także przy samym
    /// akcencie systemu albo kontraście, a każde zgłoszenie rysuje okładkę i paletę od nowa.
    /// </summary>
    private void Apply()
    {
        var next = ResolveVariant();
        if (_application.RequestedThemeVariant == next) return;

        _application.RequestedThemeVariant = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ThemeVariant ResolveVariant()
    {
        if (Mode == ThemeMode.Light) return ThemeVariant.Light;
        if (Mode == ThemeMode.Dark) return ThemeVariant.Dark;

        _platformSettings ??= _application.PlatformSettings;
        var system = _platformSettings?.GetColorValues().ThemeVariant;

        // No platform answer means an unknown desktop; the design is dark-first.
        return system == PlatformThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }
}
