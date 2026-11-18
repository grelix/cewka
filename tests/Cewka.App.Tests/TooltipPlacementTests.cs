using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>
/// Podpowiedź nie może leżeć pod kursorem. Domyślnie Avalonia stawia ją przy kursorze, a przy
/// krawędzi ekranu dosuwa z powrotem na kursor: w zmaksymalizowanym oknie podpowiedź przycisku
/// w stopce panelu kolejki zasłaniała sam przycisk i zabierała pierwsze kliknięcie.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TooltipPlacementTests
{
    [Theory]
    [InlineData(HorizontalAlignment.Right, VerticalAlignment.Bottom)]
    [InlineData(HorizontalAlignment.Left, VerticalAlignment.Bottom)]
    [InlineData(HorizontalAlignment.Center, VerticalAlignment.Bottom)]
    [InlineData(HorizontalAlignment.Right, VerticalAlignment.Top)]
    [InlineData(HorizontalAlignment.Left, VerticalAlignment.Top)]
    [InlineData(HorizontalAlignment.Right, VerticalAlignment.Center)]
    [InlineData(HorizontalAlignment.Center, VerticalAlignment.Center)]
    public void PodpowiedzNieZaslaniaPrzyciskuKtoryOpisuje(HorizontalAlignment horizontal, VerticalAlignment vertical) => Ui.Run(() =>
    {
        var button = new Button
        {
            Width = 30,
            Height = 30,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = new Thickness(6),
        };
        ToolTip.SetTip(button, "Przypnij panel (L)");
        var window = new Window { Width = 420, Height = 300, Content = button };

        try
        {
            window.Show();
            Settle();

            var centre = button.TranslatePoint(new Point(15, 15), window)!.Value;
            window.MouseMove(centre);
            ToolTip.SetIsOpen(button, true);
            Settle();

            Assert.True(ToolTip.GetIsOpen(button));
            Assert.Single(window.GetVisualDescendants().OfType<ToolTip>());

            var hit = window.InputHitTest(centre) as Visual;

            Assert.NotNull(hit);
            Assert.True(
                ReferenceEquals(hit, button) || button.IsVisualAncestorOf(hit),
                $"pod kursorem leży {hit.GetType().Name}, nie przycisk");
        }
        finally
        {
            ToolTip.SetIsOpen(button, false);
            window.Close();
        }
    });

    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
