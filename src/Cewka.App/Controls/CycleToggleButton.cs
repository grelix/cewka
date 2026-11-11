using Avalonia.Controls.Primitives;

namespace Cewka.App.Controls;

/// <summary>
/// Przełącznik, który sam się nie przestawia: kliknięcie zgłasza tylko <c>Click</c>, a stan
/// włączenia przychodzi z wiązania. Dla przycisku, który przechodzi przez więcej trybów niż dwa,
/// jak powtarzanie (wyłączone, kolejka, utwór): przejście z kolejki do utworu zostawia go
/// włączonym, a zwykły przełącznik zgasiłby się sam i wiązanie nie miałoby czego poprawić.
/// </summary>
public sealed class CycleToggleButton : ToggleButton
{
    // Wygląd i style przełącznika, łącznie z :checked i automatyzacją stanu.
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle()
    {
    }
}
