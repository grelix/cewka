using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Cewka.Platform;

/// <summary>Playback state as the system media panel understands it.</summary>
public enum MediaPanelStatus
{
    Closed = 0,
    Changing = 1,
    Stopped = 2,
    Playing = 3,
    Paused = 4,
}

/// <summary>
/// The Windows media panel: the overlay that appears on pressing a media key, and the entry in
/// the volume flyout showing what is playing.
///
/// <para><b>Dlaczego interop pisany ręcznie.</b> Zwykłą drogą do tego interfejsu jest zmiana
/// platformy docelowej na wariant z sufiksem <c>-windows</c>, co wciąga pakiet referencyjny
/// Windows SDK — kilkadziesiąt megabajtów zależności dla jednej funkcji w programie, który poza
/// nią jest w pełni wieloplatformowy. Tutaj wywołania idą wprost przez binarny interfejs WinRT,
/// tak samo jak dekoder Media Foundation w warstwie dźwięku.</para>
///
/// <para><b>Skąd identyfikatory i kolejność metod.</b> Odczytane z metadanych systemowych
/// (<c>C:\Windows\System32\WinMetadata\Windows.Media.winmd</c>), nie z pamięci ani z przykładów
/// w sieci. Pomyłka w numerze pozycji w tablicy wirtualnej nie daje błędu kompilacji — wywołuje
/// zupełnie inną funkcję, więc każda z nich jest tu wypisana razem z numerem, pod którym stoi.</para>
///
/// <para>Jedyną wartością, której nie ma w metadanych, jest identyfikator interfejsu pomostowego
/// <c>ISystemMediaTransportControlsInterop</c> — istnieje wyłącznie w nagłówkach języka C. Jego
/// poprawność sprawdza się sama: przy złym identyfikatorze zapytanie o interfejs zwraca błąd,
/// panel po prostu nie powstaje, a program działa dalej bez niego.</para>
/// </summary>
public sealed unsafe class SystemMediaControls : IMediaPanel
{
    // ---------- Identyfikatory interfejsów ----------

    private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");

    /// <summary>Only value here not present in the metadata; see the class remarks.</summary>
    private static readonly Guid IidInterop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");

    private static readonly Guid IidControls = new("99fa3ff4-1742-42a6-902e-087d41f965ec");
    private static readonly Guid IidDisplayUpdater = new("8abbc53e-fa55-4ecf-ad8e-c984e5dd1550");
    private static readonly Guid IidMusicProperties = new("6bbf0c59-d0a0-4d26-92a0-f978e1d18e7b");
    private static readonly Guid IidButtonArgs = new("b7f47116-a56f-4dc8-9e11-92031f4a87c2");

    // ---------- Pozycje w tablicach wirtualnych ----------
    //
    // Interfejsy WinRT dziedziczą po IInspectable, więc własne metody zaczynają się od pozycji 6
    // (QueryInterface, AddRef, Release, GetIids, GetRuntimeClassName, GetTrustLevel). Delegaty
    // dziedziczą po IUnknown i zaczynają się od pozycji 3.

    private const int Inspectable = 6;

    private const int SlotGetForWindow = Inspectable + 0;

    private const int SlotPutPlaybackStatus = Inspectable + 1;
    private const int SlotGetDisplayUpdater = Inspectable + 2;
    private const int SlotPutIsEnabled = Inspectable + 5;
    private const int SlotPutIsPlayEnabled = Inspectable + 7;
    private const int SlotPutIsStopEnabled = Inspectable + 9;
    private const int SlotPutIsPauseEnabled = Inspectable + 11;
    private const int SlotPutIsPreviousEnabled = Inspectable + 19;
    private const int SlotPutIsNextEnabled = Inspectable + 21;
    private const int SlotAddButtonPressed = Inspectable + 26;
    private const int SlotRemoveButtonPressed = Inspectable + 27;

    private const int SlotPutType = Inspectable + 1;
    private const int SlotGetMusicProperties = Inspectable + 6;
    private const int SlotClearAll = Inspectable + 10;
    private const int SlotUpdate = Inspectable + 11;

    private const int SlotPutTitle = Inspectable + 1;
    private const int SlotPutAlbumArtist = Inspectable + 3;
    private const int SlotPutArtist = Inspectable + 5;

    private const int SlotGetButton = Inspectable + 0;

    /// <summary>Media type reported to the panel; 1 is Music.</summary>
    private const int MediaTypeMusic = 1;

    private const int Ok = 0;

    private nint _controls;
    private nint _updater;
    private nint _musicProperties;
    private nint _handler;
    private long _token;
    private bool _disposed;

    private SystemMediaControls(nint controls, nint updater, nint musicProperties)
    {
        _controls = controls;
        _updater = updater;
        _musicProperties = musicProperties;
    }

    /// <summary>Raised when a button on the system panel is pressed. Fires on a system thread.</summary>
    public event Action<MediaKey>? ButtonPressed;

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Attaches the panel to a window. Returns null on any system that does not provide it, or
    /// when the platform refuses — the player then simply runs without the overlay.
    /// </summary>
    public static SystemMediaControls? TryCreate(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero) return null;

        try
        {
            return Create(windowHandle);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] panel systemowy multimediów niedostępny: {ex.Message}");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SystemMediaControls? Create(nint windowHandle)
    {
        // RoInitialize może zwrócić S_FALSE (już zainicjowane) albo RPC_E_CHANGED_MODE, gdy
        // wątek jest już apartamentem jednowątkowym — oba są tu w porządku.
        Native.RoInitialize(0);

        var interop = GetActivationFactory("Windows.Media.SystemMediaTransportControls", IidInterop);
        if (interop == nint.Zero) return null;

        nint controls;
        var iid = IidControls;

        try
        {
            var result = ((delegate* unmanaged<nint, nint, Guid*, nint*, int>)
                Vtable(interop)[SlotGetForWindow])(interop, windowHandle, &iid, &controls);

            if (result != Ok || controls == nint.Zero)
            {
                Console.Error.WriteLine($"[cewka] panel systemowy: GetForWindow zwrócił 0x{result:X8}.");
                return null;
            }
        }
        finally
        {
            Release(interop);
        }

        var updater = GetInterface(controls, SlotGetDisplayUpdater);

        // Rodzaj treści przed pobraniem opisu, nie po: właściwości utworu muzycznego istnieją
        // dopiero wtedy, gdy panel wie, że chodzi o muzykę, a nie o film czy obraz.
        if (updater != nint.Zero) SetInt32(updater, SlotPutType, MediaTypeMusic);

        var music = updater == nint.Zero ? nint.Zero : GetInterface(updater, SlotGetMusicProperties);

        if (updater == nint.Zero || music == nint.Zero)
        {
            Console.Error.WriteLine(
                $"[cewka] panel systemowy: brak dostępu do opisu utworu " +
                $"(aktualizator={updater != nint.Zero}, właściwości={music != nint.Zero}).");

            Release(music);
            Release(updater);
            Release(controls);
            return null;
        }

        var panel = new SystemMediaControls(controls, updater, music);
        panel.Configure();
        return panel;
    }

    [SupportedOSPlatform("windows")]
    private void Configure()
    {
        SetBoolean(_controls, SlotPutIsEnabled, true);
        SetBoolean(_controls, SlotPutIsPlayEnabled, true);
        SetBoolean(_controls, SlotPutIsPauseEnabled, true);
        SetBoolean(_controls, SlotPutIsStopEnabled, true);
        SetBoolean(_controls, SlotPutIsNextEnabled, true);
        SetBoolean(_controls, SlotPutIsPreviousEnabled, true);

        SetInt32(_updater, SlotPutType, MediaTypeMusic);

        // Przyciski bez podłączonego zdarzenia byłyby ozdobą, która nic nie robi; gdy
        // rejestracja się nie uda, panel zostaje, ale wyłącznie jako podgląd.
        if (!TryAttachButtonHandler())
        {
            SetBoolean(_controls, SlotPutIsPlayEnabled, false);
            SetBoolean(_controls, SlotPutIsPauseEnabled, false);
            SetBoolean(_controls, SlotPutIsStopEnabled, false);
            SetBoolean(_controls, SlotPutIsNextEnabled, false);
            SetBoolean(_controls, SlotPutIsPreviousEnabled, false);
        }
    }

    // ---------- Zawartość panelu ----------

    /// <summary>
    /// Reports what is playing. Empty values are simply left out of the panel.
    /// <para>
    /// Długość nagrania nie jest tu używana: panel systemu Windows przyjmuje ją osobnym
    /// interfejsem opisującym oś czasu, a bez niego pokazuje sam opis utworu.
    /// </para>
    /// </summary>
    public void SetTrack(string title, string? artist, string? album, TimeSpan length)
    {
        if (_disposed || !OperatingSystem.IsWindows()) return;

        Check(SetString(_musicProperties, SlotPutTitle, title), "tytuł");
        Check(SetString(_musicProperties, SlotPutArtist, artist ?? string.Empty), "wykonawca");
        Check(SetString(_musicProperties, SlotPutAlbumArtist, album ?? string.Empty), "album");

        // Bez Update panel pokazuje poprzedni utwór: właściwości trafiają do bufora, a dopiero
        // to wywołanie przekazuje je systemowi.
        Check(Call(_updater, SlotUpdate), "odświeżenie");
    }

    public void SetStatus(MediaPanelStatus status)
    {
        if (_disposed || !OperatingSystem.IsWindows()) return;
        Check(SetInt32(_controls, SlotPutPlaybackStatus, (int)status), "stan odtwarzania");
    }

    /// <summary>
    /// Reports a failed call once per kind. A panel that silently stops updating would be
    /// indistinguishable from one that works, so each kind of failure gets said out loud —
    /// but only the first time, because these calls happen on every track.
    /// </summary>
    private void Check(int result, string what)
    {
        if (result == Ok || !_reported.Add(what)) return;
        Console.Error.WriteLine($"[cewka] panel systemowy: {what} — 0x{result:X8}.");
    }

    private readonly HashSet<string> _reported = [];

    /// <summary>Empties the panel, used when the queue is cleared.</summary>
    public void Clear()
    {
        if (_disposed || !OperatingSystem.IsWindows()) return;

        Call(_updater, SlotClearAll);
        SetInt32(_updater, SlotPutType, MediaTypeMusic);
        Call(_updater, SlotUpdate);
    }

    // ---------- Zdarzenie przycisków ----------

    [SupportedOSPlatform("windows")]
    private bool TryAttachButtonHandler()
    {
        _handler = ButtonHandler.Create(this);
        if (_handler == nint.Zero) return false;

        long token;
        var result = ((delegate* unmanaged<nint, nint, long*, int>)
            Vtable(_controls)[SlotAddButtonPressed])(_controls, _handler, &token);

        if (result != Ok)
        {
            Console.Error.WriteLine(
                $"[cewka] panel systemowy: rejestracja przycisków zwróciła 0x{result:X8}; " +
                "panel pozostaje wyłącznie podglądem.");

            Release(_handler);
            _handler = nint.Zero;
            return false;
        }

        _token = token;
        return true;
    }

    /// <summary>Called from the handler object with the raw button number from the panel.</summary>
    private void OnButton(int button)
    {
        // Numeracja z Windows.Media.SystemMediaTransportControlsButton.
        var key = button switch
        {
            0 or 1 => (MediaKey?)MediaKey.PlayPause,
            2 => MediaKey.Stop,
            6 => MediaKey.Next,
            7 => MediaKey.Previous,
            _ => null,
        };

        if (key is not null) ButtonPressed?.Invoke(key.Value);
    }

    // ---------- Pomocnicze wywołania binarnego interfejsu ----------

    private static void** Vtable(nint instance) => *(void***)instance;

    [SupportedOSPlatform("windows")]
    private static nint GetActivationFactory(string classId, Guid iid)
    {
        nint handle = default;

        fixed (char* name = classId)
        {
            var created = Native.WindowsCreateString(name, (uint)classId.Length, &handle);
            if (created != Ok)
            {
                Console.Error.WriteLine($"[cewka] panel systemowy: WindowsCreateString 0x{created:X8}.");
                return nint.Zero;
            }
        }

        try
        {
            nint factory;
            var result = Native.RoGetActivationFactory(handle, &iid, &factory);

            if (result == Ok) return factory;

            Console.Error.WriteLine($"[cewka] panel systemowy: brak fabryki {classId} (0x{result:X8}).");
            return nint.Zero;
        }
        finally
        {
            Native.WindowsDeleteString(handle);
        }
    }

    private static nint GetInterface(nint instance, int slot)
    {
        nint value;
        var result = ((delegate* unmanaged<nint, nint*, int>)Vtable(instance)[slot])(instance, &value);
        return result == Ok ? value : nint.Zero;
    }

    private static int SetBoolean(nint instance, int slot, bool value) =>
        ((delegate* unmanaged<nint, byte, int>)Vtable(instance)[slot])(instance, value ? (byte)1 : (byte)0);

    private static int SetInt32(nint instance, int slot, int value) =>
        ((delegate* unmanaged<nint, int, int>)Vtable(instance)[slot])(instance, value);

    private static int Call(nint instance, int slot) =>
        ((delegate* unmanaged<nint, int>)Vtable(instance)[slot])(instance);

    [SupportedOSPlatform("windows")]
    private static int SetString(nint instance, int slot, string value)
    {
        nint handle = default;
        int created;

        fixed (char* text = value)
        {
            created = Native.WindowsCreateString(text, (uint)value.Length, &handle);
        }

        if (created != Ok) return created;

        try
        {
            return ((delegate* unmanaged<nint, nint, int>)Vtable(instance)[slot])(instance, handle);
        }
        finally
        {
            Native.WindowsDeleteString(handle);
        }
    }

    private static void Release(nint instance)
    {
        if (instance == nint.Zero) return;
        ((delegate* unmanaged<nint, uint>)Vtable(instance)[2])(instance);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!OperatingSystem.IsWindows()) return;

        if (_handler != nint.Zero && _controls != nint.Zero)
        {
            ((delegate* unmanaged<nint, long, int>)
                Vtable(_controls)[SlotRemoveButtonPressed])(_controls, _token);

            Release(_handler);
            _handler = nint.Zero;
        }

        if (_controls != nint.Zero) SetBoolean(_controls, SlotPutIsEnabled, false);

        Release(_musicProperties);
        Release(_updater);
        Release(_controls);

        _musicProperties = _updater = _controls = nint.Zero;
    }

    /// <summary>
    /// The object handed to the system as the button handler.
    ///
    /// <para>WinRT expects a delegate of type
    /// <c>TypedEventHandler&lt;SystemMediaTransportControls, …ButtonPressedEventArgs&gt;</c>. Such a
    /// delegate has no fixed identifier: it is derived from the type arguments by the algorithm
    /// described for parameterised types, which is what <see cref="ComputeHandlerIid"/> does.
    /// A wrong result is harmless — the system asks the object for exactly that identifier,
    /// gets a refusal, and registration fails visibly instead of silently doing nothing.</para>
    /// </summary>
    private static class ButtonHandler
    {
        private static readonly Guid HandlerIid = ComputeHandlerIid();
        private static void** _vtable;

        [StructLayout(LayoutKind.Sequential)]
        private struct Instance
        {
            public void** Vtable;
            public int References;
            public nint Owner;
        }

        public static nint Create(SystemMediaControls owner)
        {
            EnsureVtable();

            var instance = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
            instance->Vtable = _vtable;
            instance->References = 1;
            instance->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(owner, GCHandleType.Weak));

            return (nint)instance;
        }

        private static void EnsureVtable()
        {
            if (_vtable is not null) return;

            var table = (void**)NativeMemory.Alloc(4, (nuint)sizeof(nint));
            table[0] = (delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
            table[1] = (delegate* unmanaged<nint, uint>)&AddReference;
            table[2] = (delegate* unmanaged<nint, uint>)&ReleaseReference;
            table[3] = (delegate* unmanaged<nint, nint, nint, int>)&Invoke;

            _vtable = table;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterface(nint self, Guid* iid, nint* result)
        {
            if (*iid != IidUnknown && *iid != HandlerIid)
            {
                *result = nint.Zero;
                return unchecked((int)0x80004002); // E_NOINTERFACE
            }

            Interlocked.Increment(ref ((Instance*)self)->References);
            *result = self;
            return Ok;
        }

        [UnmanagedCallersOnly]
        private static uint AddReference(nint self) =>
            (uint)Interlocked.Increment(ref ((Instance*)self)->References);

        [UnmanagedCallersOnly]
        private static uint ReleaseReference(nint self)
        {
            var remaining = Interlocked.Decrement(ref ((Instance*)self)->References);
            if (remaining > 0) return (uint)remaining;

            var handle = GCHandle.FromIntPtr(((Instance*)self)->Owner);
            if (handle.IsAllocated) handle.Free();

            NativeMemory.Free((void*)self);
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int Invoke(nint self, nint sender, nint arguments)
        {
            try
            {
                if (GCHandle.FromIntPtr(((Instance*)self)->Owner).Target is not SystemMediaControls owner)
                    return Ok;

                int button;
                var result = ((delegate* unmanaged<nint, int*, int>)
                    Vtable(arguments)[SlotGetButton])(arguments, &button);

                if (result == Ok) owner.OnButton(button);
            }
            catch
            {
                // Wyjątek wypuszczony do systemu z wywołania zwrotnego kończy proces.
            }

            return Ok;
        }

        /// <summary>
        /// Derives the identifier of the parameterised handler type: a version 5 UUID built from
        /// the WinRT namespace and the textual signature of the instantiated type.
        /// </summary>
        private static Guid ComputeHandlerIid()
        {
            const string signature =
                "pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};" +
                "rc(Windows.Media.SystemMediaTransportControls;{99fa3ff4-1742-42a6-902e-087d41f965ec});" +
                "rc(Windows.Media.SystemMediaTransportControlsButtonPressedEventArgs;" +
                "{b7f47116-a56f-4dc8-9e11-92031f4a87c2}))";

            var space = new Guid("11f47ad5-7b73-42c0-abae-878b1e16adee").ToByteArray();
            ToNetworkOrder(space);

            var text = Encoding.UTF8.GetBytes(signature);
            var input = new byte[space.Length + text.Length];
            space.CopyTo(input, 0);
            text.CopyTo(input, space.Length);

            var digest = SHA1.HashData(input);
            var value = digest[..16];

            value[6] = (byte)((value[6] & 0x0F) | 0x50); // wersja 5
            value[8] = (byte)((value[8] & 0x3F) | 0x80); // wariant RFC 4122

            ToNetworkOrder(value);
            return new Guid(value);
        }

        /// <summary>
        /// Swaps the three leading fields between the layout <see cref="Guid"/> uses and the
        /// byte order the hashing rule is defined in. The operation is its own inverse.
        /// </summary>
        private static void ToNetworkOrder(byte[] value)
        {
            Array.Reverse(value, 0, 4);
            Array.Reverse(value, 4, 2);
            Array.Reverse(value, 6, 2);
        }
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        [DllImport("combase.dll")]
        public static extern int RoInitialize(int type);

        [DllImport("combase.dll")]
        public static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);

        [DllImport("combase.dll")]
        public static extern int WindowsCreateString(char* source, uint length, nint* value);

        [DllImport("combase.dll")]
        public static extern int WindowsDeleteString(nint value);
    }
}
