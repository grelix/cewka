using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Cewka.Platform;

/// <summary>
/// Blokada wygaszania ekranu przez żądanie zasilania (<c>PowerCreateRequest</c>). W odróżnieniu
/// od <c>SetThreadExecutionState</c> nie zależy od wątku, który ją przejął, a powód widać
/// w <c>powercfg /requests</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDisplayWakeLock : IDisplayWakeLock
{
    /// <summary><c>PowerRequestDisplayRequired</c>: ekran nie gaśnie mimo braku ruchu myszy i klawiatury.</summary>
    internal const int DisplayRequired = 0;

    private const uint ReasonContextVersion = 0;
    private const uint ReasonContextSimpleString = 0x1;

    private readonly object _gate = new();
    private readonly string _reason;
    private readonly int _requestType;

    private PowerRequestHandle? _request;
    private bool _disposed;

    public WindowsDisplayWakeLock(string reason) : this(reason, DisplayRequired)
    {
    }

    /// <summary>Rodzaj żądania podawany wprost tylko w testach, żeby wywołać błąd API.</summary>
    internal WindowsDisplayWakeLock(string reason, int requestType)
    {
        _reason = reason;
        _requestType = requestType;
    }

    public bool IsHeld { get; private set; }

    public void Acquire()
    {
        lock (_gate)
        {
            if (IsHeld || _disposed) return;

            // Uchwyt powstaje dopiero przy pierwszym przejęciu; nieudane utworzenie ponawia następne.
            _request ??= CreateRequest(_reason);
            if (_request is null) return;

            if (!PowerSetRequest(_request, _requestType))
            {
                Report("PowerSetRequest");
                return;
            }

            IsHeld = true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (!IsHeld || _request is null) return;
            IsHeld = false;

            if (!PowerClearRequest(_request, _requestType)) Report("PowerClearRequest");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            Release();
            _disposed = true;

            // Zamknięcie uchwytu zdejmuje też żądanie, gdyby zwolnienie się nie udało.
            _request?.Dispose();
            _request = null;
        }
    }

    private static PowerRequestHandle? CreateRequest(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext
            {
                Version = ReasonContextVersion,
                Flags = ReasonContextSimpleString,
                SimpleReasonString = text,
            };

            var handle = PowerCreateRequest(ref context);
            if (!handle.IsInvalid) return handle;

            Report("PowerCreateRequest");
            handle.Dispose();
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static void Report(string function) =>
        Console.Error.WriteLine(
            $"[cewka] blokada wygaszania ekranu: {function} nie powiodło się (błąd {Marshal.GetLastPInvokeError()}).");

    /// <summary>
    /// <c>REASON_CONTEXT</c>. Unia za polem <see cref="Flags"/> w wariancie szczegółowym jest
    /// dłuższa niż sam wskaźnik na napis; zapas wyrównuje rozmiar struktury.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        public nint Reserved1;
        public nint Reserved2;
    }

    private sealed class PowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PowerRequestHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern PowerRequestHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(PowerRequestHandle request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(PowerRequestHandle request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
