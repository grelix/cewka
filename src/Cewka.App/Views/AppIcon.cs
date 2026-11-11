using Avalonia.Controls;
using Avalonia.Platform;

namespace Cewka.App.Views;

/// <summary>Ikona okien programu, wspólna dla okna głównego i okna ustawień.</summary>
internal static class AppIcon
{
    private static WindowIcon? _window;

    /// <summary>
    /// W Windows plik ICO: pasek zadań i Alt+Tab biorą z niego obraz w swoim rozmiarze, do 32 px
    /// z rysunku uproszczonego. Inne systemy dostają PNG 256 px, bo dekodowanie ICO zależy tam
    /// od zaplecza graficznego.
    /// </summary>
    public static Uri SourceFor(bool windows) =>
        new(windows ? "avares://Cewka/Assets/cewka.ico" : "avares://Cewka/Assets/cewka.png");

    /// <summary>Plik ikony okna dla bieżącego systemu.</summary>
    public static Uri Source => SourceFor(OperatingSystem.IsWindows());

    /// <summary>Ikona okna dla bieżącego systemu, wczytana raz na cały program.</summary>
    public static WindowIcon Window => _window ??= new WindowIcon(AssetLoader.Open(Source));
}
