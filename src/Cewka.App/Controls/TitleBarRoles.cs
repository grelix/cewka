using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Cewka.App.Controls;

/// <summary>
/// Role paska tytułu dla okna z ramką systemową Windows.
/// <para>
/// Avalonia odpowiada na WM_NCHITTEST rolą najbliższego elementu z rolą, idąc od trafionego
/// elementu w górę drzewa wizualnego. Pasek z rolą <see cref="WindowDecorationsElementRole.TitleBar"/>
/// oddaje więc HTCAPTION także nad przyciskami bez własnej roli i system bierze kliknięcie za
/// chwycenie paska. Każdy przycisk paska dostaje rolę <see cref="WindowDecorationsElementRole.User"/>
/// (HTCLIENT, zwykłe kliknięcie), a maksymalizacja rolę przycisku systemu (HTMAXBUTTON), od której
/// zależy plansza układów przyciągania.
/// </para>
/// </summary>
internal static class TitleBarRoles
{
    public static void Apply(Control titleBar)
    {
        var descendants = titleBar.GetLogicalDescendants().ToList();

        foreach (var buttons in descendants.OfType<WindowButtons>())
        foreach (var maximise in buttons.MaximiseButtons)
            WindowDecorationProperties.SetElementRole(maximise, WindowDecorationsElementRole.MaximizeButton);

        // Button obejmuje też ToggleButton.
        foreach (var button in descendants.OfType<Button>())
        {
            if (WindowDecorationProperties.GetElementRole(button) == WindowDecorationsElementRole.None)
                WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        }
    }
}
