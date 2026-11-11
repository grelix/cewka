using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Cewka.App.Localisation;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Wyszukiwanie w ustawieniach: pole nad paskiem bocznym, wyniki ze ścieżką i przejście do ustawienia.</summary>
[Collection(UiCollection.Name)]
public sealed class SettingsSearchTests
{
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Run(Action<SettingsWindow, SettingsViewModel> check) => SceneTests.InLanguage("pl", () =>
    {
        using var h = new PlayerHarness();
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();
        try
        {
            Settle();
            check(window, settings);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SzukanieBezWzgleduNaWielkoscLiterIPolskieZnaki() => Run((_, settings) =>
    {
        foreach (var query in new[] { "poświata", "POSWIATA", "posw" })
        {
            settings.Search.Text = query;
            var result = Assert.Single(settings.Search.Results, r => r.Label == Strings.Current["EffectGlow"]);
            Assert.Same(settings.SceneVisuals, result.Section);
            Assert.Equal($"{Strings.Current["SectionScene"]} › {Strings.Current["SectionSceneVisuals"]}", result.Path);
        }

        // Każde słowo musi pasować; nazwa języka w jego własnym zapisie też jest hasłem.
        settings.Search.Text = "deutsch";
        Assert.Contains(settings.Search.Results, r => r.Section == settings.LanguageSection);

        settings.Search.Text = "zzz nieistniejące";
        Assert.Empty(settings.Search.Results);
        Assert.True(settings.Search.HasNoResults);

        settings.Search.Text = "  ";
        Assert.False(settings.Search.IsActive);
        Assert.Empty(settings.Search.Results);
    });

    [Fact]
    public void NazwaPodkategoriiStoiPrzedJejUstawieniami() => Run((_, settings) =>
    {
        settings.Search.Text = "korektor";
        var first = settings.Search.Results.First(r => r.Section == settings.Processing);
        Assert.Equal(Strings.Current["SectionEqualiser"], first.Label);
    });

    [Fact]
    public void WynikiZastepujaKategorieAWybranyPrzewijaDoUstawienia() => Run((window, settings) =>
    {
        window.SettingsSearchBox.Text = Strings.Current["OptionLimiter"];
        Settle();
        Assert.True(window.SearchScroll.IsVisible);
        Assert.False(window.SidebarScroll.IsVisible);

        var result = settings.Search.Results.First(r => r.Section == settings.Processing && r.Target is TextBlock);
        window.ShowResult(result);
        Settle();

        Assert.True(settings.Processing.IsSelected);
        var hit = Assert.IsType<TextBlock>(window.Hit);
        Assert.Equal(Strings.Current["OptionLimiter"], hit.Text);
        Assert.Contains("search-hit", hit.Classes);
        Assert.True(hit.IsEffectivelyVisible);
    });

    /// <summary>Pozycja listy z modelu (efekt tła) prowadzi do swojej kontrolki także przed pierwszym pokazaniem strony.</summary>
    [Fact]
    public void WynikZListyModeluPrzewijaDoSwojejKontrolki() => Run((window, settings) =>
    {
        settings.Search.Text = Strings.Current["EffectGlow"];
        var result = settings.Search.Results.First(r => r.Label == Strings.Current["EffectGlow"]);
        Assert.IsNotType<TextBlock>(result.Target);

        window.ShowResult(result);
        Settle();

        Assert.True(settings.SceneVisuals.IsSelected);
        var hit = Assert.IsType<TextBlock>(window.Hit);
        Assert.Equal(Strings.Current["EffectGlow"], hit.Text);
        Assert.True(hit.IsEffectivelyVisible);
    });

    /// <summary>Po zmianie języka otwarte wyniki mają już nowe teksty.</summary>
    [Fact]
    public void PoZmianieJezykaWynikiMajaNoweTeksty() => Run((_, settings) =>
    {
        // „ef” pasuje do „efekty” i do „effects”, a wyników jest mniej niż limit.
        settings.Search.Text = "ef";
        var polish = Strings.Current["SectionEqualiser"];
        Assert.Contains(settings.Search.Results, r => r.Label == polish);

        settings.Language.Choose(settings.Language.Options.Single(o => Equals(o.Value, "en")));
        Settle();

        Assert.Contains(settings.Search.Results, r => r.Label == "Equaliser and effects");
        Assert.DoesNotContain(settings.Search.Results, r => r.Label == polish);
    });

    [Fact]
    public void CtrlFPrzenosiFokusAEscCzysciPoleBezZamykaniaOkna() => Run((window, _) =>
    {
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.None, null);
        window.KeyRelease(Key.F, RawInputModifiers.Control, PhysicalKey.None, null);
        Settle();
        Assert.True(window.SettingsSearchBox.IsFocused);

        window.SettingsSearchBox.Text = "radio";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        Settle();

        Assert.True(string.IsNullOrEmpty(window.SettingsSearchBox.Text));
        Assert.True(window.IsVisible);
    });
}
