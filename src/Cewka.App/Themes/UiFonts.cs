using Avalonia.Media;

namespace Cewka.App.Themes;

/// <summary>
/// Pismo interfejsu: Noto Sans (SIL OFL 1.1) osadzony w pliku wykonywalnym, z rodziną Noto dla pism,
/// których Noto Sans nie ma. Wszystkie rodziny Noto mają wspólny projekt, więc tekst w kilku pismach
/// (tytuł utworu po japońsku w polskim interfejsie) wygląda spójnie, a nowy język interfejsu nie wymaga
/// wymiany fontu.
///
/// <para>Chiński i japoński dzielą znaki Han, ale rysują je różnie. Pierwszeństwo ma wariant języka
/// interfejsu: w japońskim Noto Sans JP, w pozostałych Noto Sans SC.</para>
/// </summary>
public static class UiFonts
{
    private const string Folder = "avares://Cewka/Assets/Fonts/";

    /// <summary>
    /// Źródło jednej rodziny: wzorzec nazw jej plików zamiast całego folderu. Avalonia wczytuje
    /// wszystkie fonty źródła przy pierwszym sięgnięciu po którykolwiek z nich, więc wspólny folder
    /// oznaczał 26 MB znaków chińskich i japońskich w pamięci od startu, także wtedy, gdy na ekranie
    /// jest sama łacina. Z osobnym źródłem rodzina wczytuje się dopiero przy pierwszym znaku,
    /// którego nie mają rodziny przed nią.
    /// </summary>
    public static string Source(string family)
    {
        var file = family.Replace(" ", string.Empty);
        var extension = family is "Noto Sans SC" or "Noto Sans JP" ? "otf" : "ttf";
        return $"{Folder}{file}-*.{extension}#{family}";
    }

    /// <summary>Rodzina podstawowa: łacina z rozszerzeniami (także wietnamski), greka i cyrylica.</summary>
    public const string Primary = "Noto Sans";

    /// <summary>Pisma alfabetyczne spoza Noto Sans, w kolejności zastępowania.</summary>
    private static readonly string[] OtherScripts =
        ["Noto Sans Arabic", "Noto Sans Hebrew", "Noto Sans Devanagari", "Noto Sans Thai"];

    /// <summary>Rodziny w kolejności zastępowania dla języka interfejsu o kodzie <paramref name="language"/>.</summary>
    public static IReadOnlyList<string> Families(string? language) =>
        [Primary, .. HanOrder(language), .. OtherScripts];

    /// <summary>Łańcuch rodzin dla stylu tekstu w danym języku interfejsu.</summary>
    public static FontFamily For(string? language) =>
        new(string.Join(", ", Families(language).Select(Source)));

    /// <summary>
    /// Ustawienia fontów dla Avalonii. Rodzina domyślna jest osadzona, bo w systemie bez fontów
    /// (goły kontener) Avalonia przerywa działanie komunikatem „Default font family name can't be null
    /// or empty”. Rodziny zastępcze obsługują tekst w kontrolkach, które nie biorą stylu interfejsu.
    ///
    /// <para>Rodzin ze znakami Han tu nie ma. Avalonia pyta rodziny zastępcze przed łańcuchem stylu
    /// interfejsu, a ta lista powstaje raz, przed wyborem języka: z Noto Sans SC na liście znaki Han
    /// po japońsku rysował krój chiński, bez względu na kolejność w <see cref="Families"/>. Tekst
    /// poza stylem interfejsu bierze znaki Han z fontu systemu.</para>
    /// </summary>
    public static FontManagerOptions Options => new()
    {
        DefaultFamilyName = Source(Primary),
        FontFallbacks = [.. OtherScripts.Select(f => new FontFallback { FontFamily = new FontFamily(Source(f)) })],
    };

    private static string[] HanOrder(string? language) =>
        string.Equals(language, "ja", StringComparison.OrdinalIgnoreCase)
            ? ["Noto Sans JP", "Noto Sans SC"]
            : ["Noto Sans SC", "Noto Sans JP"];
}
