namespace Cewka.App.Localisation;

/// <summary>
/// Nazwy klawiszy ze słownika, wspólne dla listy skrótów i menu, bo na klawiaturach w innych
/// językach te same klawisze są podpisane inaczej (Strg, Entf, Maj).
/// </summary>
public static class KeyNames
{
    public static string Ctrl => Strings.Current["KeyCtrl"];

    public static string Shift => Strings.Current["KeyShift"];

    public static string Alt => Strings.Current["KeyAlt"];

    public static string Enter => Strings.Current["KeyEnter"];

    public static string Escape => Strings.Current["KeyEscape"];

    public static string Delete => Strings.Current["KeyDelete"];

    public static string Space => Strings.Current["KeySpace"];

    /// <summary>Kombinacja klawiszy zapisana z plusem, np. „Strg+Z”.</summary>
    public static string Chord(params string[] keys) => string.Join("+", keys);
}
