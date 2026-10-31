namespace Cewka.Platform;

/// <summary>Paths named on the command line, made independent of where the program was started.</summary>
public static class CommandLinePaths
{
    /// <summary>
    /// Turns every relative path into a full one, resolved against <paramref name="baseDirectory"/>.
    ///
    /// <para>Pułapka: ścieżka względna znaczy coś tylko w katalogu, z którego uruchomiono
    /// program. Kopia działająca w innym katalogu rozwiązałaby ją po swojemu, a w zapisanej
    /// kolejce przestałaby działać przy następnym uruchomieniu z innego miejsca.</para>
    /// </summary>
    /// <param name="arguments">Argumenty wiersza poleceń.</param>
    /// <param name="baseDirectory">Katalog bieżący; domyślnie katalog bieżący procesu.</param>
    public static string[] ToAbsolute(IReadOnlyList<string> arguments, string? baseDirectory = null)
    {
        var root = baseDirectory ?? Environment.CurrentDirectory;
        var result = new string[arguments.Count];

        for (var i = 0; i < arguments.Count; i++) result[i] = ToAbsolute(arguments[i], root);

        return result;
    }

    private static string ToAbsolute(string argument, string root)
    {
        // Adresy w rodzaju file:// zostają bez zmian, tak samo jak ścieżki już pełne.
        if (string.IsNullOrWhiteSpace(argument) || argument.Contains("://", StringComparison.Ordinal))
            return argument;

        try
        {
            return Path.IsPathFullyQualified(argument) ? argument : Path.GetFullPath(argument, root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return argument;
        }
    }
}
