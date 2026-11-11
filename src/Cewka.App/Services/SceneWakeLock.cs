using Cewka.Platform;

namespace Cewka.App.Services;

/// <summary>
/// Kiedy scena trzyma blokadę wygaszania ekranu: tylko przy otwartej scenie, grającej muzyce
/// i oknie, które nie jest zminimalizowane. W każdym innym stanie blokada jest zwolniona,
/// także wtedy, gdy przejęcie albo zwolnienie skończyło się wyjątkiem.
/// </summary>
internal sealed class SceneWakeLock : IDisposable
{
    private readonly Func<IDisplayWakeLock> _create;
    private IDisplayWakeLock? _lock;
    private bool _disposed;

    /// <param name="create">Tworzy blokadę przy pierwszej potrzebie; w testach atrapa.</param>
    public SceneWakeLock(Func<IDisplayWakeLock> create)
    {
        _create = create;
    }

    public bool IsHeld => _lock?.IsHeld == true;

    /// <param name="keepAwake">Ustawienie użytkownika; wyłączone zwalnia blokadę mimo grającej sceny.</param>
    public void Update(bool sceneOpen, bool playing, bool minimised, bool keepAwake = true)
    {
        if (_disposed) return;

        if (sceneOpen && playing && !minimised && keepAwake) Acquire();
        else Release();
    }

    private void Acquire()
    {
        try
        {
            _lock ??= _create();
            _lock.Acquire();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] blokada wygaszania: {ex.Message}");
            Release();
        }
    }

    private void Release()
    {
        try
        {
            _lock?.Release();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] zwolnienie blokady wygaszania: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Release();
        _disposed = true;

        try
        {
            _lock?.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] zamknięcie blokady wygaszania: {ex.Message}");
        }

        _lock = null;
    }
}
