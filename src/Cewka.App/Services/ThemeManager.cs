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

    public ThemeMode Mode { get; private set; }

    /// <summary>
    /// The variant actually in force right now.
    /// <para>
    /// Read from the application rather than recomputed from <see cref="Mode"/>: anything
    /// that sets <c>RequestedThemeVariant</c> directly — the snapshot tool does — would
    /// otherwise get an answer that disagrees with what is on screen.
    /// </para>
    /// </summary>
    public bool IsDark => _application.ActualThemeVariant == ThemeVariant.Dark;

    public void SetMode(ThemeMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Apply();
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

    private void Apply()
    {
        _application.RequestedThemeVariant = ResolveVariant();
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
