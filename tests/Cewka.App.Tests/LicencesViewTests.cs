using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Widok „Licencje składników”: lista, szczegóły w tym samym miejscu i powrót z fokusem
/// na wierszu. Okno jest wysokie, żeby lista nie wirtualizowała wierszy i dało się je policzyć.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LicencesViewTests
{
    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Window Host(LicencesView view)
    {
        var window = new Window { Width = 700, Height = 4000, Content = view };
        window.Show();
        Settle();
        return window;
    }

    private static void Key(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Settle();
    }

    private static ListBox ListOf(LicencesView view) => view.FindControl<ListBox>("LicenceList")!;

    private static Button BackOf(LicencesView view) => view.FindControl<Button>("BackButton")!;

    private static IReadOnlyList<ListBoxItem> Rows(LicencesView view) =>
        ListOf(view).GetVisualDescendants().OfType<ListBoxItem>().ToList();

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    /// <summary>Otwiera szczegóły wiersza klawiszami: fokus na liście, strzałka w dół do wiersza, Enter.</summary>
    private static void OpenByKeyboard(Window window, LicencesView view, int index)
    {
        var list = ListOf(view);
        list.SelectedIndex = 0;
        Settle();
        (list.ContainerFromIndex(0) as Control)?.Focus(NavigationMethod.Tab);
        Settle();

        for (var i = 0; i < index; i++) Key(window, Avalonia.Input.Key.Down);
        Key(window, Avalonia.Input.Key.Enter);
    }

    [Fact]
    public void WidokMaTyleWierszyIleSkladnikow()
    {
        Ui.Run(() =>
        {
            var components = ThirdPartyNotices.Load();
            var view = new LicencesView(new LicencesViewModel(components));
            var window = Host(view);

            try
            {
                Assert.NotEmpty(components);
                Assert.Equal(components.Count, ListOf(view).ItemCount);
                Assert.Equal(components.Count, Rows(view).Count);
                Assert.True(ListOf(view).IsEffectivelyVisible);
                Assert.False(BackOf(view).IsEffectivelyVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WierszMaNazweDlaCzytnikaEkranuNazwaPrzecinekLicencja()
    {
        Ui.Run(() =>
        {
            var view = new LicencesView(new LicencesViewModel(ThirdPartyNotices.Load()));
            var window = Host(view);

            try
            {
                var rows = Rows(view);
                var first = (LicenceItem)rows[0].DataContext!;

                Assert.Equal("Avalonia, MIT", AutomationProperties.GetName(rows[0]));
                Assert.All(rows, row =>
                {
                    var item = (LicenceItem)row.DataContext!;
                    Assert.Equal(item.AccessibleName, AutomationProperties.GetName(row));
                    Assert.True(row.Focusable);
                });
                Assert.Equal("Avalonia", first.Name);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EnterPokazujeSzczegolyZNazwaLicencjiITekstem()
    {
        Ui.Run(() =>
        {
            var model = new LicencesViewModel(ThirdPartyNotices.Load());
            var view = new LicencesView(model);
            var window = Host(view);

            try
            {
                OpenByKeyboard(window, view, 2);

                var item = model.Items[2];
                Assert.Same(item, model.Current);
                Assert.False(ListOf(view).IsEffectivelyVisible);
                Assert.True(BackOf(view).IsEffectivelyVisible);

                var licenceText = view.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Single(block => block.IsEffectivelyVisible);
                Assert.Equal(item.LicenceText, licenceText.Text);

                var texts = view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block.IsEffectivelyVisible && block is not SelectableTextBlock)
                    .Select(block => block.Text)
                    .ToList();
                Assert.Contains(item.Name, texts);
                Assert.Contains(item.Licence, texts);
                Assert.Contains(item.Component.Project, view.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.IsEffectivelyVisible).Select(button => button.Content as string));
                Assert.Same(BackOf(view), Focused(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KlikniecieWWierszOtwieraSzczegoly()
    {
        Ui.Run(() =>
        {
            var model = new LicencesViewModel(ThirdPartyNotices.Load());
            var view = new LicencesView(model);
            var window = Host(view);

            try
            {
                var row = Rows(view)[1];
                var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

                window.MouseDown(point, Avalonia.Input.MouseButton.Left);
                window.MouseUp(point, Avalonia.Input.MouseButton.Left);
                Settle();

                Assert.Same(model.Items[1], model.Current);
                Assert.True(BackOf(view).IsEffectivelyVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EscWracaDoListyZFokusemNaWybranymWierszu()
    {
        Ui.Run(() =>
        {
            var model = new LicencesViewModel(ThirdPartyNotices.Load());
            var view = new LicencesView(model);
            var window = Host(view);
            var reachedWindow = new List<Key>();
            window.AddHandler(InputElement.KeyDownEvent, (_, e) => reachedWindow.Add(e.Key), handledEventsToo: false);

            try
            {
                OpenByKeyboard(window, view, 3);
                Assert.Same(model.Items[3], model.Current);

                Key(window, Avalonia.Input.Key.Escape);

                Assert.Null(model.Current);
                Assert.True(ListOf(view).IsEffectivelyVisible);
                Assert.False(BackOf(view).IsEffectivelyVisible);
                Assert.DoesNotContain(Avalonia.Input.Key.Escape, reachedWindow);

                var focused = Assert.IsType<ListBoxItem>(Focused(window));
                Assert.Same(model.Items[3], focused.DataContext);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EscNaListeZostajeDlaOknaUstawien()
    {
        Ui.Run(() =>
        {
            var view = new LicencesView(new LicencesViewModel(ThirdPartyNotices.Load()));
            var window = Host(view);
            var reachedWindow = new List<Key>();
            window.AddHandler(InputElement.KeyDownEvent, (_, e) => reachedWindow.Add(e.Key), handledEventsToo: false);

            try
            {
                (ListOf(view).ContainerFromIndex(0) as Control)?.Focus(NavigationMethod.Tab);
                Settle();

                Key(window, Avalonia.Input.Key.Escape);

                Assert.Contains(Avalonia.Input.Key.Escape, reachedWindow);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PrzyciskWsteczWracaDoListy()
    {
        Ui.Run(() =>
        {
            var model = new LicencesViewModel(ThirdPartyNotices.Load());
            var view = new LicencesView(model);
            var window = Host(view);

            try
            {
                OpenByKeyboard(window, view, 1);
                Assert.Same(model.Items[1], model.Current);

                BackOf(view).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();

                Assert.Null(model.Current);
                Assert.True(ListOf(view).IsEffectivelyVisible);
                Assert.Equal(model.Items.Count, Rows(view).Count);
                Assert.Same(model.Items[1], Assert.IsType<ListBoxItem>(Focused(window)).DataContext);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void BrakNotPokazujeKomunikatZamiastListy()
    {
        Ui.Run(() =>
        {
            var model = new LicencesViewModel([]);
            var view = new LicencesView(model);
            var window = Host(view);

            try
            {
                Assert.True(model.IsEmpty);
                Assert.False(model.IsListVisible);
                Assert.False(ListOf(view).IsEffectivelyVisible);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.IsEffectivelyVisible && block.Text == Cewka.App.Localisation.Strings.Current["LicencesEmpty"]);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ZasobWbudowanyDajeTeSamoCoPlikWRepozytorium()
    {
        Ui.Run(() =>
        {
            var fromResource = ThirdPartyNotices.Load();
            var fromFile = ThirdPartyNotices.Parse(ThirdPartyNoticesParserTests.RealFile());

            Assert.NotEmpty(fromResource);
            Assert.Equal(fromFile.Count, fromResource.Count);
            for (var i = 0; i < fromFile.Count; i++)
            {
                Assert.Equal(fromFile[i].Name, fromResource[i].Name);
                Assert.Equal(fromFile[i].LicenceText, fromResource[i].LicenceText);
            }
        });
    }

    [Fact]
    public void WidokBezModeluTworzyWlasnyZNotamiProgramu()
    {
        Ui.Run(() =>
        {
            var view = new LicencesView();

            var model = Assert.IsType<LicencesViewModel>(view.DataContext);
            Assert.False(model.IsEmpty);
        });
    }
}
