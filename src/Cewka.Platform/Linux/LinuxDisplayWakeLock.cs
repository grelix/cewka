using System.Runtime.Versioning;

namespace Cewka.Platform.Linux;

/// <summary>
/// Blokada wygaszania ekranu na szynie sesji: przez portal albo przez
/// <c>org.freedesktop.ScreenSaver</c>. Kolejność i treść wywołań: <see cref="DisplayInhibitRules"/>.
///
/// <para>Każda blokada ma własne prywatne połączenie, trzymane do <see cref="Dispose"/>. Obie
/// usługi zdejmują blokadę, gdy jej właściciel rozłączy się z szyną, więc zamknięcie połączenia,
/// także przy upadku procesu, zwalnia ją niezależnie od <see cref="Release"/>.</para>
///
/// <para>Gdy nie wiadomo, co stało się z blokadą po stronie usługi (brak odpowiedzi w czasie,
/// odpowiedź bez uchwytu, nieudane zwolnienie), połączenie jest zamykane i porzucane, a dalsze
/// wywołania idą nowym. Żadna blokada nie przeżywa więc utraty wiedzy o niej.</para>
///
/// <para>Uchwyt z portalu nie znaczy jeszcze, że blokada działa: zakłada ją backend, a odmowę
/// zgłasza sygnałem <c>Response</c> albo, przy pustym identyfikatorze programu, wcale. Sygnał
/// bywa spóźniony, więc po przejęciu blokada nasłuchuje go jeszcze przez pół minuty.
/// W piaskownicy po odmowie blokadę przejmuje ScreenSaver; poza nią portal jest pytany dopiero
/// wtedy, gdy ScreenSavera nie ma, więc po jego odmowie blokady nie ma.</para>
///
/// <para>Połączenie nie jest bezpieczne wątkowo, więc każde wywołanie idzie pod blokadą.</para>
/// </summary>
internal sealed class LinuxDisplayWakeLock : IDisplayWakeLock
{
    private readonly object _gate = new();
    private readonly string _reason;
    private readonly IInhibitTransport _transport;
    private readonly TimeProvider _time;
    private readonly bool _sandboxed;

    private bool _unavailable;
    private bool _skipPortal;
    private bool _disposed;

    private string? _portalHandle;
    private uint? _cookie;

    /// <summary>Nasłuch spóźnionej odmowy portalu; każde przejęcie ma własny znacznik.</summary>
    private ITimer? _watch;
    private object? _watchToken;
    private int _watchTicks;

    [SupportedOSPlatform("linux")]
    public LinuxDisplayWakeLock(string reason)
        : this(reason, new DBusInhibitTransport(), TimeProvider.System,
            DisplayInhibitRules.IsSandboxed(File.Exists, Environment.GetEnvironmentVariable))
    {
    }

    internal LinuxDisplayWakeLock(string reason, IInhibitTransport transport, TimeProvider time, bool sandboxed = true)
    {
        _reason = reason;
        _transport = transport;
        _time = time;
        _sandboxed = sandboxed;
    }

    public bool IsHeld { get; private set; }

    /// <summary>Czy program działa w piaskownicy Flatpak albo Snap.</summary>
    internal bool IsSandboxed => _sandboxed;

    public void Acquire()
    {
        lock (_gate)
        {
            if (IsHeld || _disposed || _unavailable) return;

            try
            {
                IsHeld = Inhibit();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // Bez libdbus nie ma czego próbować przy następnym przejęciu.
                _unavailable = true;
                Console.Error.WriteLine($"[cewka] blokada wygaszania ekranu niedostępna: {ex.Message}");
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            StopWatch();

            if (!IsHeld) return;
            IsHeld = false;

            var call = _portalHandle is { } handle ? DisplayInhibitRules.PortalRelease(handle)
                : _cookie is { } cookie ? DisplayInhibitRules.ScreenSaverRelease(cookie)
                : null;

            _portalHandle = null;
            _cookie = null;

            // Zerwane połączenie zdjęło blokadę już po stronie usługi.
            if (call is null || !_transport.IsConnected) return;

            var reply = _transport.Call(call);
            if (!reply.IsError) return;

            Console.Error.WriteLine($"[cewka] nie udało się zwolnić blokady wygaszania ekranu: {reply.ErrorMessage}");

            // Uchwytu już nie ma, a blokada mogła zostać; zdejmie ją rozłączenie.
            _transport.Drop();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            Release();
            _disposed = true;
            _transport.Drop();
        }
    }

    private bool Inhibit()
    {
        if (!Connect()) return false;

        // Poza piaskownicą najpierw ScreenSaver, a portal tylko wtedy, gdy ScreenSavera nie ma.
        if (!_sandboxed)
        {
            if (TryScreenSaver(out var missing)) return true;
            return missing && !_skipPortal && Connect() && TryPortal(out _);
        }

        var backendRefused = false;
        if (!_skipPortal)
        {
            if (TryPortal(out backendRefused)) return true;
            if (!Connect()) return false;
        }

        if (!TryScreenSaver(out _)) return false;

        // Backend odmówi znów z tego samego powodu, a ScreenSaver podaje nazwę wprost i działa.
        if (backendRefused) _skipPortal = true;
        return true;
    }

    /// <returns>Czy portal trzyma blokadę.</returns>
    /// <param name="refused">Portal dał uchwyt, ale backend odmówił blokady.</param>
    private bool TryPortal(out bool refused)
    {
        refused = false;

        var reply = _transport.Call(DisplayInhibitRules.PortalInhibit(_reason));
        if (!reply.IsError && reply.Value is string handle)
        {
            var response = _transport.WaitForResponse(handle, DisplayInhibitRules.PortalResponseMilliseconds);
            if (DisplayInhibitRules.IsPortalHeld(response))
            {
                _portalHandle = handle;
                if (response is null) WatchPortal();
                return true;
            }

            Console.Error.WriteLine($"[cewka] portal odmówił blokady wygaszania ekranu (kod {response}).");
            refused = true;
        }
        else if (reply.IsError && DisplayInhibitRules.IsPortalMissing(reply.ErrorName!))
        {
            _skipPortal = true;
        }
        else if (DisplayInhibitRules.MayHoldUnknownInhibit(reply))
        {
            // Spóźniony portal mógł założyć blokadę bez znanego uchwytu; zdejmie ją rozłączenie.
            _transport.Drop();
        }

        return false;
    }

    /// <summary>
    /// Brak sygnału w czasie oczekiwania nie przesądza o blokadzie: wolny backend może odmówić
    /// później. Do końca nasłuchu blokada liczy się jako przejęta.
    /// </summary>
    private void WatchPortal()
    {
        var token = new object();
        _watchToken = token;
        _watchTicks = 0;
        _watch = _time.CreateTimer(CheckPortal, token,
            DisplayInhibitRules.PortalWatchInterval, DisplayInhibitRules.PortalWatchInterval);
    }

    private void StopWatch()
    {
        _watch?.Dispose();
        _watch = null;
        _watchToken = null;
    }

    /// <summary>Jedno sprawdzenie bez czekania; odmowa przekazuje blokadę ScreenSaver jak przy przejęciu.</summary>
    private void CheckPortal(object? token)
    {
        lock (_gate)
        {
            // Odliczenie zaczęte przed zwolnieniem albo przed nowym przejęciem.
            if (token is null || token != _watchToken || _portalHandle is not { } handle) return;

            try
            {
                var response = _transport.IsConnected ? _transport.WaitForResponse(handle, 0) : null;
                if (response is null)
                {
                    if (!_transport.IsConnected || ++_watchTicks >= DisplayInhibitRules.PortalWatchTicks) StopWatch();
                    return;
                }

                StopWatch();
                if (DisplayInhibitRules.IsPortalHeld(response)) return;

                Console.Error.WriteLine($"[cewka] portal odmówił blokady wygaszania ekranu po czasie (kod {response}).");
                _portalHandle = null;

                IsHeld = Connect() && TryScreenSaver(out _);
                if (IsHeld) _skipPortal = true;
            }
            catch (Exception ex)
            {
                // Wątek licznika nie ma nikogo, kto przejąłby wyjątek; zostaje blokada, jaka jest.
                StopWatch();
                Console.Error.WriteLine($"[cewka] sprawdzenie blokady wygaszania ekranu: {ex.Message}");
            }
        }
    }

    /// <returns>Czy ScreenSaver trzyma blokadę.</returns>
    /// <param name="missing">Usługi ScreenSaver nie ma na tej szynie.</param>
    private bool TryScreenSaver(out bool missing)
    {
        missing = false;

        var reply = _transport.Call(DisplayInhibitRules.ScreenSaverInhibit(_reason));
        if (!reply.IsError && reply.Value is uint cookie)
        {
            _cookie = cookie;
            return true;
        }

        missing = reply.IsError && DisplayInhibitRules.IsServiceMissing(reply.ErrorName!);

        Console.Error.WriteLine(reply.IsError
            ? $"[cewka] blokada wygaszania ekranu przez ScreenSaver niedostępna: {reply.ErrorMessage}"
            : "[cewka] blokada wygaszania ekranu: nieoczekiwana odpowiedź usługi.");

        if (DisplayInhibitRules.MayHoldUnknownInhibit(reply)) _transport.Drop();
        return false;
    }

    private bool Connect()
    {
        if (_transport.Connect()) return true;

        // Bez szyny sesji (konsola, kontener) nie pojawi się ona już w trakcie pracy.
        _unavailable = true;
        return false;
    }
}
