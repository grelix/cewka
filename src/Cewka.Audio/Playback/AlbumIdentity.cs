namespace Cewka.Audio.Playback;

/// <summary>
/// Czy wpisy kolejki należą do jednego albumu i czy jeden jest następcą drugiego na płycie.
///
/// <para>Rozstrzyga wyłącznie na podstawie znaczników już odczytanych: wpis, którego metadanych
/// jeszcze nie ma, nie należy do żadnego albumu. Nie czyta plików, więc można ją wołać z wątku
/// dekodującego.</para>
/// </summary>
public static class AlbumIdentity
{
    /// <summary>Ścieżki porównuje się tak, jak porównuje je system plików.</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Wpis ma niepusty znacznik albumu albo pochodzi z arkusza CUE.</summary>
    public static bool HasAlbum(QueueEntry entry) => entry.Sheet is not null || Album(entry) is not null;

    /// <summary>
    /// Oba wpisy mają ten sam niepusty znacznik albumu (bez wielkości liter, po przycięciu) oraz
    /// tego samego wykonawcę albumu. Gdy któremuś wykonawcy albumu brak, muszą leżeć w jednym
    /// katalogu: inaczej każda składanka „Greatest Hits” byłaby jednym albumem. Utwory arkusza CUE
    /// tworzą album z utworami tego samego arkusza i z żadnymi innymi.
    /// </summary>
    public static bool SameAlbum(QueueEntry first, QueueEntry second)
    {
        if (first.IsLive || second.IsLive) return false;

        if (first.Sheet is not null || second.Sheet is not null)
            return first.Sheet is { } a && second.Sheet is { } b && string.Equals(a.SheetPath, b.SheetPath, PathComparison);

        var album = Album(first);
        if (album is null || !string.Equals(album, Album(second), StringComparison.OrdinalIgnoreCase)) return false;

        var firstArtist = Clean(first.Metadata?.AlbumArtist);
        var secondArtist = Clean(second.Metadata?.AlbumArtist);
        if (firstArtist is not null && secondArtist is not null)
            return string.Equals(firstArtist, secondArtist, StringComparison.OrdinalIgnoreCase);

        return string.Equals(Directory(first), Directory(second), PathComparison);
    }

    /// <summary>
    /// <paramref name="next"/> stoi na albumie zaraz po <paramref name="previous"/>: ten sam album
    /// i numer ścieżki o jeden większy na tej samej płycie albo pierwsza ścieżka płyty następnej.
    /// Album bez numerów ścieżek (u obu wpisów) uznaje się za grany po kolei. W arkuszu CUE następcą
    /// jest ścieżka o numerze o jeden większym.
    /// </summary>
    public static bool IsSuccessor(QueueEntry previous, QueueEntry next)
    {
        if (!SameAlbum(previous, next)) return false;
        if (previous.Sheet is { } earlier && next.Sheet is { } later) return later.Number == earlier.Number + 1;

        var before = previous.Metadata!.TrackNumber;
        var after = next.Metadata!.TrackNumber;
        if (before is null && after is null) return true;
        if (before is null || after is null) return false;

        // Brak numeru płyty to płyta pierwsza: tak tagują wydania jednopłytowe.
        var discBefore = previous.Metadata.DiscNumber ?? 1;
        var discAfter = next.Metadata.DiscNumber ?? 1;

        if (discAfter == discBefore) return after == before + 1;
        return discAfter == discBefore + 1 && after == 1;
    }

    private static string? Album(QueueEntry entry) => entry.IsLive ? null : Clean(entry.Metadata?.Album);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Directory(QueueEntry entry) => System.IO.Path.GetDirectoryName(entry.Path) ?? string.Empty;
}
