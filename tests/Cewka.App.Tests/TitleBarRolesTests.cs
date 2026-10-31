using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Cewka.App.Controls;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Role paska tytułu, z których Avalonia w Windows składa odpowiedź na WM_NCHITTEST. Przycisk bez
/// własnej roli dziedziczy rolę paska i staje się dla systemu miejscem do chwycenia okna.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TitleBarRolesTests
{
    private static WindowDecorationsElementRole Role(Control control) => WindowDecorationProperties.GetElementRole(control);

    private static Control Named(Control scope, string name) =>
        scope.GetLogicalDescendants().OfType<Control>().First(c => c.Name == name);

    [Fact]
    public void RamkaSystemowaDajePrzyciskomPaskaWlasneRole()
    {
        Ui.Run(() =>
        {
            App.Settings.Current.Window = null;
            var window = new MainWindow(systemFrame: true) { Width = 1100, Height = 600 };
            window.Show();

            try
            {
                Assert.Equal(WindowDecorationsElementRole.TitleBar, Role(window.TitleBar));

                foreach (var button in new Control[] { window.PanelToggle, window.QueueToggle, window.ThemeButton, window.SettingsButton })
                    Assert.Equal(WindowDecorationsElementRole.User, Role(button));

                foreach (var buttons in window.GetLogicalDescendants().OfType<WindowButtons>())
                {
                    Assert.Equal(WindowDecorationsElementRole.MaximizeButton, Role(Named(buttons, "SquareMaximise")));
                    Assert.Equal(WindowDecorationsElementRole.User, Role(Named(buttons, "SquareMinimise")));
                    Assert.Equal(WindowDecorationsElementRole.User, Role(Named(buttons, "SquareClose")));
                }

                // Na pełnym ekranie pasek nie przesuwa okna, a przyciski działają dalej.
                window.WindowState = WindowState.FullScreen;
                Assert.Equal(WindowDecorationsElementRole.None, Role(window.TitleBar));
                Assert.Equal(WindowDecorationsElementRole.User, Role(window.PanelToggle));

                window.WindowState = WindowState.Normal;
                Assert.Equal(WindowDecorationsElementRole.TitleBar, Role(window.TitleBar));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void BezRamkiSystemowejPasekNieMaRol()
    {
        Ui.Run(() =>
        {
            App.Settings.Current.Window = null;
            var window = new MainWindow(systemFrame: false) { Width = 1100, Height = 600 };
            window.Show();

            try
            {
                Assert.Equal(WindowDecorationsElementRole.None, Role(window.TitleBar));
                Assert.All(window.TitleBar.GetLogicalDescendants().OfType<Button>(),
                    button => Assert.Equal(WindowDecorationsElementRole.None, Role(button)));
            }
            finally { window.Close(); }
        });
    }
}
