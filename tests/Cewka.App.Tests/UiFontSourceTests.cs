using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Localisation;
using Cewka.App.Themes;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Każda rodzina pisma interfejsu ma własne źródło, więc wczytuje się osobno i dopiero wtedy, gdy
/// jest potrzebna. Przy wspólnym folderze pierwszy napis po łacinie wczytywał wszystkie fonty,
/// także 26 MB znaków chińskich i japońskich.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class UiFontSourceTests
{
    private static readonly string[] AllFamilies = [.. UiFonts.Families("pl").Union(UiFonts.Families("ja"))];

    public static TheoryData<string> Families => new(AllFamilies);

    /// <summary>
    /// Zbiór fontów, który Avalonia buduje ze źródła rodziny, zawiera tę rodzinę i żadnej innej.
    /// Sprawdzane jej własnym zbiorem, a nie odtworzeniem reguły dopasowania nazw plików.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public void ZrodloRodzinyWczytujeTylkoTeRodzine(string family) => Ui.Run(() =>
    {
        var source = new FontFamily(UiFonts.Source(family)).Key!.Source;
        using var collection = new EmbeddedFontCollection(source, source);

        var names = collection.Select(found => found.Name).Distinct().ToArray();

        // Odmiana cienka nosi w pliku nazwę „Noto Sans Light”; inna rodzina łańcucha to już błąd.
        Assert.Contains(family, names);
        Assert.All(names, name => Assert.True(
            name == family || (name.StartsWith(family + " ", StringComparison.Ordinal) && !AllFamilies.Contains(name)),
            $"źródło rodziny {family} wczytuje też {name}"));
    });

    /// <summary>Żaden plik fontu nie został bez rodziny: nowy font w folderze wymaga wpisu w łańcuchu.</summary>
    [Fact]
    public void KazdyPlikFontuNalezyDoJednejRodziny()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var files = Directory.GetFiles(Path.Combine(root, "src", "Cewka.App", "Assets", "Fonts"))
            .Where(file => file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var owners = AllFamilies.Where(family => Matches(UiFonts.Source(family), file!)).ToArray();
            Assert.True(owners.Length == 1, $"{file} pasuje do {owners.Length} rodzin: {string.Join(", ", owners)}");
        }
    }

    /// <summary>Reguła Avalonii dla źródła ze wzorcem: początek nazwy do gwiazdki i rozszerzenie.</summary>
    private static bool Matches(string source, string file)
    {
        var pattern = source[(source.LastIndexOf('/') + 1)..source.IndexOf('#')];
        var star = pattern.IndexOf('*');

        return file.StartsWith(pattern[..star], StringComparison.Ordinal)
               && file.EndsWith(pattern[(star + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Znak spoza rodziny podstawowej nadal trafia do właściwej rodziny zastępczej, w kolejności
    /// zależnej od języka interfejsu: znaki Han po japońsku rysuje Noto Sans JP, poza tym Noto Sans SC.
    /// </summary>
    [Theory]
    [InlineData("pl", "Zażółć gęślą jaźń", "Noto Sans")]
    [InlineData("pl", "Привет", "Noto Sans")]
    [InlineData("pl", "直", "Noto Sans SC")]
    [InlineData("zh-hans", "直", "Noto Sans SC")]
    [InlineData("ja", "直", "Noto Sans JP")]
    [InlineData("ja", "かな", "Noto Sans JP")]
    [InlineData("pl", "かな", "Noto Sans SC")]
    [InlineData("pl", "שלום", "Noto Sans Hebrew")]
    [InlineData("pl", "مرحبا", "Noto Sans Arabic")]
    [InlineData("pl", "नमस्ते", "Noto Sans Devanagari")]
    [InlineData("pl", "สวัสดี", "Noto Sans Thai")]
    public void ZnakTrafiaDoWlasciwejRodziny(string language, string text, string expected) => Ui.Run(() =>
    {
        var layout = new TextLayout(text, new Typeface(UiFonts.For(language)), 14, Brushes.Black);

        var families = layout.TextLines[0].TextRuns.OfType<ShapedTextRun>()
            .Select(run => run.GlyphRun.GlyphTypeface.FamilyName)
            .Distinct()
            .ToArray();

        Assert.Equal([expected], families);
    });

    /// <summary>
    /// Tekst bez stylu interfejsu (rodzina domyślna Avalonii) też dochodzi do rodzin zastępczych
    /// pism alfabetycznych. Znaki Han bierze wtedy z fontu systemu, więc tu ich nie ma.
    /// </summary>
    [Theory]
    [InlineData("Zażółć", "Noto Sans")]
    [InlineData("שלום", "Noto Sans Hebrew")]
    [InlineData("สวัสดี", "Noto Sans Thai")]
    public void RodzinaDomyslnaDochodziDoZastepczych(string text, string expected) => Ui.Run(() =>
    {
        var layout = new TextLayout(text, new Typeface(FontFamily.Default), 14, Brushes.Black);

        var families = layout.TextLines[0].TextRuns.OfType<ShapedTextRun>()
            .Select(run => run.GlyphRun.GlyphTypeface.FamilyName)
            .Distinct()
            .ToArray();

        Assert.Equal([expected], families);
    });

    /// <summary>Odmiany rodziny podstawowej leżą w jednym źródle: pogrubienie i cienkie pismo to prawdziwe pliki.</summary>
    [Theory]
    [InlineData(FontWeight.Light)]
    [InlineData(FontWeight.Normal)]
    [InlineData(FontWeight.Bold)]
    public void RodzinaPodstawowaMaWszystkieOdmiany(FontWeight weight) => Ui.Run(() =>
    {
        var layout = new TextLayout("Cewka", new Typeface(UiFonts.For("pl"), FontStyle.Normal, weight), 14, Brushes.Black);
        var typeface = layout.TextLines[0].TextRuns.OfType<ShapedTextRun>().First().GlyphRun.GlyphTypeface;

        Assert.StartsWith("Noto Sans", typeface.FamilyName);
        Assert.DoesNotContain(typeface.FamilyName, AllFamilies.Skip(1));
        Assert.Equal(weight, typeface.Weight);
    });

    /// <summary>
    /// Okienka wyskakujące dostają od motywu Fluent własną rodzinę pisma, a pozycja menu składa
    /// nagłówek kontrolką, której styl napisów interfejsu nie obejmuje. Znaki Han mają tam ten sam
    /// osadzony krój co w oknie, a nie font systemu, którego w Linuksie może nie być.
    /// </summary>
    [Theory]
    [InlineData("ja", "再生", "Noto Sans JP")]
    [InlineData("zh-hans", "播放", "Noto Sans SC")]
    [InlineData("pl", "再生", "Noto Sans SC")]
    public void MenuKontekstoweRysujeZnakiHanOsadzonymKrojem(string language, string text, string expected) => Ui.Run(() =>
    {
        Strings.Current.SetLanguage(language);

        var item = new MenuItem { Header = text };
        var menu = new ContextMenu();
        menu.Items.Add(item);
        var target = new Border { Width = 60, Height = 60, ContextMenu = menu };
        var window = new Window { Width = 300, Height = 200, Content = target };

        try
        {
            window.Show();
            menu.Open(target);
            Dispatcher.UIThread.RunJobs();

            var label = item.GetVisualDescendants().OfType<TextBlock>().First(found => found.Text == text);
            var families = label.TextLayout.TextLines[0].TextRuns.OfType<ShapedTextRun>()
                .Select(run => run.GlyphRun.GlyphTypeface.FamilyName)
                .Distinct()
                .ToArray();

            Assert.Equal([expected], families);
        }
        finally
        {
            menu.Close();
            window.Close();
            Strings.Current.SetLanguage("pl");
        }
    });
}
