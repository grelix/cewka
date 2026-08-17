using Cewka.App.Models;

namespace Cewka.App.Services;

/// <summary>
/// Pilnuje, żeby przy ustawieniu losowym para barw domyślnej okładki była wybierana **raz na
/// utwór**, a nie przy każdym przerysowaniu.
///
/// <para><b>Skąd to się wzięło.</b> Okładka jest rysowana od nowa za każdym razem, gdy silnik
/// zgłosi zmianę utworu — a zgłasza to również po przewinięciu w obrębie tego samego utworu,
/// bo z jego punktu widzenia przewinięcie i przejście do innej pozycji kolejki to jedna
/// czynność. Losowanie w miejscu rysowania oznaczało więc, że przesunięcie suwaka postępu
/// zmieniało barwy. Tak samo przełączenie motywu, które również przerysowuje okładkę.</para>
///
/// <para>Tożsamością utworu jest jego ścieżka. Ten sam plik wczytany ponownie później dostanie
/// nowe losowanie, bo do tego czasu pamięć zostanie nadpisana przez inny utwór — a to właśnie
/// znaczy „losowanie co utwór".</para>
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
    /// ustawienia: wybranie losowania ma dać widoczny skutek od razu, a nie dopiero przy
    /// następnym utworze.
    /// </summary>
    public void Forget()
    {
        _hasDrawn = false;
        _drawnFor = null;
    }
}
