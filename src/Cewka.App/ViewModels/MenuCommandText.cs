namespace Cewka.App.ViewModels;

/// <summary>
/// Nagłówek polecenia menu ze skrótem zapisanym nazwami klawiszy ze słownika. Gest wbudowany
/// w menu podpisuje klawisze zawsze po angielsku, inaczej niż lista skrótów.
/// </summary>
public sealed record MenuCommandText(string Text, string Gesture)
{
    public override string ToString() => Text;
}
