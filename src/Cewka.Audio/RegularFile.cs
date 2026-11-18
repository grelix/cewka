namespace Cewka.Audio;

/// <summary>
/// Odróżnia zwykły plik od potoku nazwanego i urządzenia, które w Linuksie leżą w folderach obok
/// plików i mogą nosić rozszerzenie dźwiękowe. Otwarcie potoku bez piszącego nie wraca nigdy,
/// a urządzenie w rodzaju <c>/dev/zero</c> oddaje dane bez końca; jedno i drugie wieszało wątek,
/// który po nie sięgnął.
/// </summary>
internal static class RegularFile
{
    /// <summary>
    /// Potoki i urządzenia zgłaszają długość zero. Pusty zwykły plik też
    /// odpada, ale w nim i tak nie ma czego czytać. W Windowsie takich wpisów w folderach nie ma.
    /// </summary>
    public static bool Is(string path)
    {
        if (OperatingSystem.IsWindows()) return true;

        try
        {
            FileSystemInfo info = new FileInfo(path);

            // Długość dowiązania to długość jego własnego zapisu, więc liczy się to, na co wskazuje.
            if (info.LinkTarget is not null) info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;

            // Brak pliku zgłosi zwykłą drogą ten, kto spróbuje go otworzyć.
            return info is not FileInfo { Exists: true } file || file.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
