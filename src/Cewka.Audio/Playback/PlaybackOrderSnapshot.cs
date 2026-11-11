namespace Cewka.Audio.Playback;

/// <summary>
/// Kolejność odtwarzania w chwili odczytu: własna kopia, której silnik już nie zmienia.
/// </summary>
/// <param name="Entries">Wpisy w kolejności, w jakiej zagrają.</param>
/// <param name="CurrentPosition">Miejsce bieżącego wpisu w <paramref name="Entries"/>; −1, gdy żaden z nich nie jest bieżący.</param>
/// <param name="Version">Wersja kolejności z <see cref="PlaybackEngine.PlaybackOrderVersion"/>.</param>
public sealed record PlaybackOrderSnapshot(IReadOnlyList<QueueEntry> Entries, int CurrentPosition, long Version);
