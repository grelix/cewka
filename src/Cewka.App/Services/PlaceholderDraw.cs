using Cewka.App.Models;

namespace Cewka.App.Services;

/// <summary>
/// Pilnuje, żeby przy ustawieniu losowym para barw domyślnej okładki była losowana raz na utwór
/// (rozpoznawany po ścieżce).
///
/// <para>Pułapka: silnik zgłasza zmianę utworu także po przewinięciu, a okładkę przerysowuje też
/// zmiana motywu. Losowanie przy rysowaniu zmieniałoby barwy przy każdym przesunięciu suwaka.</para>
/// </summary>
public sealed class PlaceholderDraw
{
    private PlaceholderPalette _drawn = PlaceholderPalette.BlueViolet;
    private string? _drawnFor;
    private bool _hasDrawn;

    /// <summary>
    /// Para barw do narysowania. Dla wyborów o ustalonych barwach zwraca po prostu wybór;
    /// dla losowego — wartość wylosowaną dla tego utworu.
    /// </summary>
    public PlaceholderPalette For(PlaceholderPalette chosen, string? trackPath)
    {
        if (chosen != PlaceholderPalette.Random) return chosen;

        if (!_hasDrawn || !string.Equals(_drawnFor, trackPath, StringComparison.Ordinal))
        {
            _drawn = CoilCover.Resolve(PlaceholderPalette.Random);
            _drawnFor = trackPath;
            _hasDrawn = true;
        }

        return _drawn;
    }

    /// <summary>
    /// Unieważnia pamięć, żeby najbliższe rysowanie losowało od nowa. Wołane po zmianie
    /// ustawienia, żeby skutek był widoczny od razu.
    /// </summary>
    public void Forget()
    {
        _hasDrawn = false;
        _drawnFor = null;
    }
}
