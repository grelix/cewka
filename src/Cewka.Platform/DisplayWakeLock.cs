namespace Cewka.Platform;

/// <summary>
/// Nie pozwala systemowi wygasić ekranu ani uśpić komputera z powodu bezczynności, dopóki
/// jest przejęta. Pełny ekran trzyma ją tylko wtedy, gdy gra muzyka.
/// </summary>
public interface IDisplayWakeLock : IDisposable
{
    /// <summary>Czy system przyjął blokadę; gdy jej odmówił, przejęcie zostawia tu false.</summary>
    bool IsHeld { get; }

    /// <summary>Przejmuje blokadę; wołane drugi raz nic nie zmienia. Błąd systemu nie wychodzi poza nią.</summary>
    void Acquire();

    /// <summary>Zwalnia blokadę; bez przejęcia nic nie robi.</summary>
    void Release();
}

/// <summary>Blokada wygaszania ekranu dla bieżącego systemu.</summary>
public static class DisplayWakeLock
{
    /// <param name="reason">Powód widoczny w narzędziach systemu, np. <c>powercfg /requests</c>.</param>
    public static IDisplayWakeLock Create(string reason)
    {
        if (OperatingSystem.IsWindows()) return new WindowsDisplayWakeLock(reason);
        if (OperatingSystem.IsLinux()) return new Linux.LinuxDisplayWakeLock(reason);
        return new NoDisplayWakeLock();
    }
}

/// <summary>System bez obsługiwanej blokady: zapamiętuje tylko stan.</summary>
internal sealed class NoDisplayWakeLock : IDisplayWakeLock
{
    public bool IsHeld { get; private set; }

    public void Acquire() => IsHeld = true;

    public void Release() => IsHeld = false;

    public void Dispose() => IsHeld = false;
}
