using Cewka.App.Models;

namespace Cewka.App.ViewModels;

/// <summary>
/// Most między oknem Ustawienia a oknem głównym dla ustawień sceny, które zależą od ekranów:
/// ekran, na którym stanie scena, lista ekranów i polecenie dopasowania krawędzi.
/// </summary>
public sealed class SceneSettingsHost
{
    /// <summary>Klucz ekranu, na którym stanie scena; <c>null</c>, gdy nie da się go ustalić.</summary>
    public Func<string?> ScreenKey { get; init; } = () => null;

    /// <summary>Ekrany podłączone w tej chwili.</summary>
    public Func<IReadOnlyList<ScreenInfo>> Screens { get; init; } = () => [];

    /// <summary>Zamyka Ustawienia i otwiera scenę z ramką dopasowania krawędzi.</summary>
    public Action FitEdges { get; init; } = () => { };
}
