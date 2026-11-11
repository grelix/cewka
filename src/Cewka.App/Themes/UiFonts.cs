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
    private const string Folder = "avares://Cewka/Assets/Fonts#";

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
        new(string.Join(", ", Families(language).Select(f => Folder + f)));

    /// <summary>
    /// Ustawienia fontów dla Avalonii. Rodzina domyślna jest osadzona, bo w systemie bez fontów
    /// (goły kontener) Avalonia przerywa działanie komunikatem „Default font family name can't be null
    /// or empty”. Rodziny zastępcze obsługują tekst w kontrolkach, które nie biorą stylu interfejsu.
    /// </summary>
    public static FontManagerOptions Options => new()
    {
        DefaultFamilyName = Folder + Primary,
        FontFallbacks = [.. Families(null).Skip(1).Select(f => new FontFallback { FontFamily = new FontFamily(Folder + f) })],
    };

    private static string[] HanOrder(string? language) =>
        string.Equals(language, "ja", StringComparison.OrdinalIgnoreCase)
            ? ["Noto Sans JP", "Noto Sans SC"]
            : ["Noto Sans SC", "Noto Sans JP"];
}
