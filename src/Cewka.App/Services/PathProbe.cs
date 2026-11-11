namespace Cewka.App.Services;

/// <summary>
/// Sprawdzanie, czy ścieżka istnieje. Niedostępny dysk sieciowy odpowiada dopiero po kilkunastu
/// sekundach, więc woła się je wyłącznie poza wątkiem interfejsu.
/// </summary>
internal sealed record PathProbe(Func<string, bool> FileExists, Func<string, bool> DirectoryExists)
{
    public static PathProbe Disk { get; } = new(File.Exists, Directory.Exists);
}
