using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cewka.Platform.Linux;

/// <summary>What a media panel asked the player to do.</summary>
public enum MediaCommand
{
    Play,
    Pause,
    PlayPause,
    Stop,
    Next,
    Previous,
}

/// <summary>
/// The player as seen by a Linux desktop: <c>org.mpris.MediaPlayer2</c> on the session bus.
///
/// <para>MPRIS jest w Linuksie odpowiednikiem panelu multimediów systemu Windows, a przy okazji
/// jedyną drogą, którą środowiska graficzne przekazują klawisze multimedialne. Klawiatura trafia
/// najpierw do pulpitu, a ten woła po szynie odtwarzacz, który akurat gra — dlatego osobne
/// przechwytywanie klawiszy, jak w systemie Windows, jest tu niepotrzebne i byłoby szkodliwe.</para>
///
/// <para><b>Wątek i stan.</b> Połączenie należy w całości do jednego wątku utworzonego tutaj.
/// Interfejs nie sięga do niego wprost: podmienia niezmienne zdjęcie stanu, które wątek szyny
/// odczytuje, odpowiadając na pytania o właściwości. Dzięki temu nie ma ani jednej blokady
/// dzielonej między wątkiem interfejsu a szyną.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class MprisService : IMediaPanel
{
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string IntrospectableInterface = "org.freedesktop.DBus.Introspectable";

    /// <summary>Immutable picture of what is playing, swapped whole rather than edited in place.</summary>
    private sealed record Snapshot(
        string Title,
        string Artist,
        string Album,
        long LengthMicroseconds,
        string TrackId,
        MediaPanelStatus Status);

    private static readonly Snapshot Empty =
        new(string.Empty, string.Empty, string.Empty, 0, "/org/mpris/MediaPlayer2/TrackList/NoTrack",
            MediaPanelStatus.Stopped);

    private readonly nint _connection;
    private readonly string _identity;
    private readonly string _desktopEntry;
    private readonly GCHandle _self;
    private readonly Thread _thread;

    private volatile Snapshot _state = Empty;
    private volatile bool _running = true;
    private volatile bool _changed;
    private long _positionMicroseconds;
    private int _trackCounter;

    private MprisService(nint connection, string identity, string desktopEntry)
    {
        _connection = connection;
        _identity = identity;
        _desktopEntry = desktopEntry;
        _self = GCHandle.Alloc(this);

        _thread = new Thread(Loop) { IsBackground = true, Name = "Cewka.Mpris" };
    }

    /// <summary>Raised on the bus thread when a desktop asks the player to do something.</summary>
    public event Action<MediaCommand>? CommandReceived;

    /// <summary>Raised when a desktop asks for the window to be brought forward.</summary>
    public event Action? RaiseRequested;

    /// <summary>Raised when a desktop asks the player to quit.</summary>
    public event Action? QuitRequested;

    /// <summary>Relative seek requested by a panel, positive or negative.</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>Absolute position requested by a panel.</summary>
    public event Action<TimeSpan>? PositionRequested;

    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>
    /// Publishes the player on the session bus. Returns null wherever there is no bus — a plain
    /// console session, a container, another operating system — and the player then simply runs
    /// without desktop integration.
    /// </summary>
    public static MprisService? TryStart(string identity, string desktopEntry)
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            return Start(identity, desktopEntry);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] MPRIS niedostępny: {ex.Message}");
            return null;
        }
    }

    [SupportedOSPlatform("linux")]
    private static MprisService? Start(string identity, string desktopEntry)
    {
        DBus.Error error;
        DBus.dbus_error_init(&error);

        var connection = DBus.dbus_bus_get(DBus.BusSession, &error);
        if (connection == nint.Zero)
        {
            Console.Error.WriteLine($"[cewka] brak szyny sesji D-Bus: {DBus.TakeMessage(&error)}.");
            return null;
        }

        // Rozłączenie z szyną nie może zakończyć procesu; odtwarzacz ma grać dalej.
        DBus.dbus_connection_set_exit_on_disconnect(connection, 0);

        var service = new MprisService(connection, identity, desktopEntry);

        if (!service.ClaimName() || !service.RegisterObject())
        {
            DBus.dbus_connection_unref(connection);
            return null;
        }

        service._thread.Start();
        return service;
    }

    [SupportedOSPlatform("linux")]
    private bool ClaimName()
    {
        // Nazwa zajęta bywa przez poprzednią kopię, która jeszcze się nie zamknęła; wtedy
        // wystarczy własny przyrostek, bo specyfikacja wprost na niego pozwala.
        foreach (var candidate in new[]
                 {
                     "org.mpris.MediaPlayer2.cewka",
                     $"org.mpris.MediaPlayer2.cewka.instance{Environment.ProcessId}",
                 })
        {
            DBus.Error error;
            DBus.dbus_error_init(&error);

            using var name = new DBus.Utf8(candidate);
            var result = DBus.dbus_bus_request_name(_connection, name.Pointer, DBus.NameFlags, &error);

            if (result == DBus.NameReplyPrimaryOwner) return true;

            if (DBus.dbus_error_is_set(&error) != 0)
                Console.Error.WriteLine($"[cewka] MPRIS: nazwa {candidate} — {DBus.TakeMessage(&error)}.");
        }

        Console.Error.WriteLine("[cewka] MPRIS: nie udało się zająć nazwy na szynie.");
        return false;
    }

    [SupportedOSPlatform("linux")]
    private bool RegisterObject()
    {
        var vtable = new DBus.ObjectPathVTable
        {
            Unregister = nint.Zero,
            Message = (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnMessage,
        };

        using var path = new DBus.Utf8(ObjectPath);
        if (DBus.dbus_connection_register_object_path(
                _connection, path.Pointer, &vtable, GCHandle.ToIntPtr(_self)) != 0)
            return true;

        Console.Error.WriteLine("[cewka] MPRIS: nie udało się zarejestrować obiektu na szynie.");
        return false;
    }

    // ---------- Stan pokazywany na szynie ----------

    public void SetTrack(string title, string? artist, string? album, TimeSpan length)
    {
        var id = $"/org/mpris/MediaPlayer2/Track/{Interlocked.Increment(ref _trackCounter)}";

        _state = _state with
        {
            Title = title,
            Artist = artist ?? string.Empty,
            Album = album ?? string.Empty,
            LengthMicroseconds = (long)Math.Max(0, length.TotalMilliseconds * 1000),
            TrackId = id,
        };

        _changed = true;
    }

    public void SetStatus(MediaPanelStatus status)
    {
        if (_state.Status == status) return;

        _state = _state with { Status = status };
        _changed = true;
    }

    /// <summary>
    /// Reports the playing position. Not announced as a change: MPRIS deliberately leaves
    /// <c>Position</c> out of the change signal, because a value moving forty times a second
    /// would flood the bus. Panels read it when they need it.
    /// </summary>
    public void SetPosition(TimeSpan position) =>
        Interlocked.Exchange(ref _positionMicroseconds, (long)Math.Max(0, position.TotalMilliseconds * 1000));

    public void Clear()
    {
        _state = Empty;
        _changed = true;
    }

    // ---------- Pętla szyny ----------

    private void Loop()
    {
        while (_running)
        {
            // Zwraca zero po rozłączeniu z szyną; wtedy nie ma po co dalej pytać.
            if (DBus.dbus_connection_read_write_dispatch(_connection, 200) == 0) break;

            if (!_changed) continue;

            _changed = false;
            EmitPropertiesChanged();
        }
    }

    private void EmitPropertiesChanged()
    {
        using var path = new DBus.Utf8(ObjectPath);
        using var iface = new DBus.Utf8(PropertiesInterface);
        using var member = new DBus.Utf8("PropertiesChanged");

        var signal = DBus.dbus_message_new_signal(path.Pointer, iface.Pointer, member.Pointer);
        if (signal == nint.Zero) return;

        try
        {
            DBus.MessageIter iter;
            DBus.dbus_message_iter_init_append(signal, &iter);

            AppendString(&iter, DBus.TypeString, PlayerInterface);

            using var entrySignature = new DBus.Utf8("{sv}");
            DBus.MessageIter changed;
            DBus.dbus_message_iter_open_container(&iter, DBus.TypeArray, entrySignature.Pointer, &changed);

            var state = _state;
            AppendEntry(&changed, "PlaybackStatus", PlayerInterface, state);
            AppendEntry(&changed, "Metadata", PlayerInterface, state);

            DBus.dbus_message_iter_close_container(&iter, &changed);

            // Lista właściwości unieważnionych, zawsze pusta: podajemy nowe wartości wprost.
            using var stringSignature = new DBus.Utf8("s");
            DBus.MessageIter invalidated;
            DBus.dbus_message_iter_open_container(&iter, DBus.TypeArray, stringSignature.Pointer, &invalidated);
            DBus.dbus_message_iter_close_container(&iter, &invalidated);

            uint serial;
            DBus.dbus_connection_send(_connection, signal, &serial);
            DBus.dbus_connection_flush(_connection);
        }
        finally
        {
            DBus.dbus_message_unref(signal);
        }
    }

    // ---------- Obsługa wiadomości ----------

    [UnmanagedCallersOnly]
    [SupportedOSPlatform("linux")]
    private static int OnMessage(nint connection, nint message, nint user)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not MprisService service) return DBus.NotHandled;
            return service.Dispatch(message);
        }
        catch
        {
            // Wyjątek wypuszczony do biblioteki z wywołania zwrotnego kończy proces.
            return DBus.NotHandled;
        }
    }

    private int Dispatch(nint message)
    {
        var iface = DBus.ReadString(DBus.dbus_message_get_interface(message)) ?? string.Empty;
        var member = DBus.ReadString(DBus.dbus_message_get_member(message)) ?? string.Empty;

        return iface switch
        {
            IntrospectableInterface when member == "Introspect" => ReplyIntrospection(message),
            PropertiesInterface => DispatchProperties(message, member),
            RootInterface => DispatchRoot(message, member),
            PlayerInterface => DispatchPlayer(message, member),
            _ => DBus.NotHandled,
        };
    }

    private int DispatchRoot(nint message, string member)
    {
        switch (member)
        {
            case "Raise": RaiseRequested?.Invoke(); break;
            case "Quit": QuitRequested?.Invoke(); break;
            default: return DBus.NotHandled;
        }

        return ReplyEmpty(message);
    }

    private int DispatchPlayer(nint message, string member)
    {
        switch (member)
        {
            case "Play": CommandReceived?.Invoke(MediaCommand.Play); break;
            case "Pause": CommandReceived?.Invoke(MediaCommand.Pause); break;
            case "PlayPause": CommandReceived?.Invoke(MediaCommand.PlayPause); break;
            case "Stop": CommandReceived?.Invoke(MediaCommand.Stop); break;
            case "Next": CommandReceived?.Invoke(MediaCommand.Next); break;
            case "Previous": CommandReceived?.Invoke(MediaCommand.Previous); break;

            case "Seek":
                if (TryReadInt64(message, 0, out var offset))
                    SeekRequested?.Invoke(TimeSpan.FromMilliseconds(offset / 1000.0));
                break;

            // Pierwszym argumentem jest ścieżka utworu, którego dotyczy żądanie; pomijamy ją,
            // bo kolejka wystawia na szynie tylko utwór odtwarzany.
            case "SetPosition":
                if (TryReadInt64(message, 1, out var position))
                    PositionRequested?.Invoke(TimeSpan.FromMilliseconds(position / 1000.0));
                break;

            default: return DBus.NotHandled;
        }

        return ReplyEmpty(message);
    }

    private int DispatchProperties(nint message, string member)
    {
        switch (member)
        {
            case "Get":
            {
                if (!TryReadString(message, 0, out var iface) || !TryReadString(message, 1, out var property))
                    return ReplyEmpty(message);

                return ReplyProperty(message, iface, property);
            }

            case "GetAll":
                return TryReadString(message, 0, out var target)
                    ? ReplyAllProperties(message, target)
                    : ReplyEmpty(message);

            // Zapis właściwości nie jest obsługiwany; wszystkie wystawione są tylko do odczytu.
            case "Set":
                return ReplyEmpty(message);

            default:
                return DBus.NotHandled;
        }
    }

    // ---------- Odpowiedzi ----------

    private int ReplyEmpty(nint message)
    {
        var reply = DBus.dbus_message_new_method_return(message);
        if (reply == nint.Zero) return DBus.Handled;

        Send(reply);
        return DBus.Handled;
    }

    private int ReplyIntrospection(nint message)
    {
        var reply = DBus.dbus_message_new_method_return(message);
        if (reply == nint.Zero) return DBus.Handled;

        DBus.MessageIter iter;
        DBus.dbus_message_iter_init_append(reply, &iter);
        AppendString(&iter, DBus.TypeString, Introspection);

        Send(reply);
        return DBus.Handled;
    }

    private int ReplyProperty(nint message, string iface, string property)
    {
        var reply = DBus.dbus_message_new_method_return(message);
        if (reply == nint.Zero) return DBus.Handled;

        DBus.MessageIter iter;
        DBus.dbus_message_iter_init_append(reply, &iter);
        WriteProperty(&iter, iface, property, _state);

        Send(reply);
        return DBus.Handled;
    }

    private int ReplyAllProperties(nint message, string iface)
    {
        var reply = DBus.dbus_message_new_method_return(message);
        if (reply == nint.Zero) return DBus.Handled;

        var state = _state;

        DBus.MessageIter iter;
        DBus.dbus_message_iter_init_append(reply, &iter);

        using var entrySignature = new DBus.Utf8("{sv}");
        DBus.MessageIter array;
        DBus.dbus_message_iter_open_container(&iter, DBus.TypeArray, entrySignature.Pointer, &array);

        foreach (var name in NamesOf(iface)) AppendEntry(&array, name, iface, state);

        DBus.dbus_message_iter_close_container(&iter, &array);

        Send(reply);
        return DBus.Handled;
    }

    private static string[] NamesOf(string iface) => iface switch
    {
        RootInterface =>
        [
            "CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry",
            "SupportedUriSchemes", "SupportedMimeTypes",
        ],
        PlayerInterface =>
        [
            "PlaybackStatus", "LoopStatus", "Rate", "MinimumRate", "MaximumRate", "Shuffle",
            "Volume", "Position", "Metadata",
            "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl",
        ],
        _ => [],
    };

    private void Send(nint reply)
    {
        uint serial;
        DBus.dbus_connection_send(_connection, reply, &serial);
        DBus.dbus_message_unref(reply);
    }

    // ---------- Zapis właściwości ----------

    private void AppendEntry(DBus.MessageIter* array, string name, string iface, Snapshot state)
    {
        DBus.MessageIter entry;
        DBus.dbus_message_iter_open_container(array, DBus.TypeDictEntry, null, &entry);

        AppendString(&entry, DBus.TypeString, name);
        WriteProperty(&entry, iface, name, state);

        DBus.dbus_message_iter_close_container(array, &entry);
    }

    private void WriteProperty(DBus.MessageIter* iter, string iface, string name, Snapshot state)
    {
        if (iface == RootInterface)
        {
            switch (name)
            {
                case "CanQuit": AppendVariantBoolean(iter, true); return;
                case "CanRaise": AppendVariantBoolean(iter, true); return;
                case "HasTrackList": AppendVariantBoolean(iter, false); return;
                case "Identity": AppendVariantString(iter, DBus.TypeString, _identity); return;
                case "DesktopEntry": AppendVariantString(iter, DBus.TypeString, _desktopEntry); return;
                case "SupportedUriSchemes": AppendVariantStringArray(iter, ["file"]); return;
                case "SupportedMimeTypes": AppendVariantStringArray(iter, SupportedMimeTypes); return;
            }
        }

        if (iface == PlayerInterface)
        {
            switch (name)
            {
                case "PlaybackStatus": AppendVariantString(iter, DBus.TypeString, StatusName(state.Status)); return;
                case "LoopStatus": AppendVariantString(iter, DBus.TypeString, "None"); return;
                case "Rate": AppendVariantDouble(iter, 1.0); return;
                case "MinimumRate": AppendVariantDouble(iter, 1.0); return;
                case "MaximumRate": AppendVariantDouble(iter, 1.0); return;
                case "Shuffle": AppendVariantBoolean(iter, false); return;
                case "Volume": AppendVariantDouble(iter, 1.0); return;
                case "Position": AppendVariantInt64(iter, Interlocked.Read(ref _positionMicroseconds)); return;
                case "Metadata": AppendMetadata(iter, state); return;

                case "CanGoNext":
                case "CanGoPrevious":
                case "CanPlay":
                case "CanPause":
                case "CanSeek":
                case "CanControl":
                    AppendVariantBoolean(iter, true);
                    return;
            }
        }

        // Nieznana właściwość: pusty napis zamiast błędu. Panele pytają czasem o rzeczy spoza
        // tego, co wystawiamy, a odmowa bywa u nich traktowana jak awaria całego odtwarzacza.
        AppendVariantString(iter, DBus.TypeString, string.Empty);
    }

    private static string StatusName(MediaPanelStatus status) => status switch
    {
        MediaPanelStatus.Playing => "Playing",
        MediaPanelStatus.Paused => "Paused",
        _ => "Stopped",
    };

    private static readonly string[] SupportedMimeTypes =
    [
        "audio/mpeg", "audio/flac", "audio/x-flac", "audio/wav", "audio/x-wav",
        "audio/ogg", "audio/x-vorbis+ogg", "audio/opus", "audio/x-opus+ogg",
        "audio/mp4", "audio/aac", "audio/x-m4a",
    ];

    private static void AppendMetadata(DBus.MessageIter* iter, Snapshot state)
    {
        using var variantSignature = new DBus.Utf8("a{sv}");
        DBus.MessageIter variant;
        DBus.dbus_message_iter_open_container(iter, DBus.TypeVariant, variantSignature.Pointer, &variant);

        using var entrySignature = new DBus.Utf8("{sv}");
        DBus.MessageIter array;
        DBus.dbus_message_iter_open_container(&variant, DBus.TypeArray, entrySignature.Pointer, &array);

        AppendMetadataPath(&array, "mpris:trackid", state.TrackId);
        AppendMetadataInt64(&array, "mpris:length", state.LengthMicroseconds);

        if (state.Title.Length > 0) AppendMetadataString(&array, "xesam:title", state.Title);
        if (state.Album.Length > 0) AppendMetadataString(&array, "xesam:album", state.Album);
        if (state.Artist.Length > 0) AppendMetadataStringArray(&array, "xesam:artist", state.Artist);

        DBus.dbus_message_iter_close_container(&variant, &array);
        DBus.dbus_message_iter_close_container(iter, &variant);
    }

    private static void AppendMetadataString(DBus.MessageIter* array, string key, string value)
    {
        DBus.MessageIter entry;
        DBus.dbus_message_iter_open_container(array, DBus.TypeDictEntry, null, &entry);
        AppendString(&entry, DBus.TypeString, key);
        AppendVariantString(&entry, DBus.TypeString, value);
        DBus.dbus_message_iter_close_container(array, &entry);
    }

    private static void AppendMetadataPath(DBus.MessageIter* array, string key, string value)
    {
        DBus.MessageIter entry;
        DBus.dbus_message_iter_open_container(array, DBus.TypeDictEntry, null, &entry);
        AppendString(&entry, DBus.TypeString, key);
        AppendVariantString(&entry, DBus.TypeObjectPath, value);
        DBus.dbus_message_iter_close_container(array, &entry);
    }

    private static void AppendMetadataInt64(DBus.MessageIter* array, string key, long value)
    {
        DBus.MessageIter entry;
        DBus.dbus_message_iter_open_container(array, DBus.TypeDictEntry, null, &entry);
        AppendString(&entry, DBus.TypeString, key);
        AppendVariantInt64(&entry, value);
        DBus.dbus_message_iter_close_container(array, &entry);
    }

    private static void AppendMetadataStringArray(DBus.MessageIter* array, string key, string value)
    {
        DBus.MessageIter entry;
        DBus.dbus_message_iter_open_container(array, DBus.TypeDictEntry, null, &entry);
        AppendString(&entry, DBus.TypeString, key);
        AppendVariantStringArray(&entry, [value]);
        DBus.dbus_message_iter_close_container(array, &entry);
    }

    // ---------- Zapis wartości podstawowych ----------

    private static void AppendString(DBus.MessageIter* iter, int type, string value)
    {
        using var text = new DBus.Utf8(value);

        // append_basic dla napisów przyjmuje wskaźnik na wskaźnik, nie sam napis.
        var pointer = (nint)text.Pointer;
        DBus.dbus_message_iter_append_basic(iter, type, &pointer);
    }

    private static void AppendVariantString(DBus.MessageIter* iter, int type, string value)
    {
        using var signature = new DBus.Utf8(type == DBus.TypeObjectPath ? "o" : "s");

        DBus.MessageIter variant;
        DBus.dbus_message_iter_open_container(iter, DBus.TypeVariant, signature.Pointer, &variant);
        AppendString(&variant, type, value);
        DBus.dbus_message_iter_close_container(iter, &variant);
    }

    private static void AppendVariantBoolean(DBus.MessageIter* iter, bool value)
    {
        using var signature = new DBus.Utf8("b");

        DBus.MessageIter variant;
        DBus.dbus_message_iter_open_container(iter, DBus.TypeVariant, signature.Pointer, &variant);

        // dbus_bool_t ma cztery bajty, a nie jeden.
        var raw = value ? 1 : 0;
        DBus.dbus_message_iter_append_basic(&variant, DBus.TypeBoolean, &raw);

        DBus.dbus_message_iter_close_container(iter, &variant);
    }

    private static void AppendVariantInt64(DBus.MessageIter* iter, long value)
    {
        using var signature = new DBus.Utf8("x");

        DBus.MessageIter variant;
        DBus.dbus_message_iter_open_container(iter, DBus.TypeVariant, signature.Pointer, &variant);
        DBus.dbus_message_iter_append_basic(&variant, DBus.TypeInt64, &value);
        DBus.dbus_message_iter_close_container(iter, &variant);
    }

    private static void AppendVariantDouble(DBus.MessageIter* iter, double value)
    {
        using var signature = new DBus.Utf8("d");

        DBus.MessageIter variant;
        DBus.dbus_message_iter_open_container(iter, DBus.TypeVariant, signature.Pointer, &variant);
        DBus.dbus_message_iter_append_basic(&variant, DBus.TypeDouble, &value);
        DBus.dbus_message_iter_close_container(iter, &variant);
    }

    private static void AppendVariantStringArray(DBus.MessageIter* iter, string[] values)
    {
        using var variantSignature = new DBus.Utf8("as");
        DBus.MessageIter variant;
        DBus.dbus_message_iter_open_container(iter, DBus.TypeVariant, variantSignature.Pointer, &variant);

        using var itemSignature = new DBus.Utf8("s");
        DBus.MessageIter array;
        DBus.dbus_message_iter_open_container(&variant, DBus.TypeArray, itemSignature.Pointer, &array);

        foreach (var value in values) AppendString(&array, DBus.TypeString, value);

        DBus.dbus_message_iter_close_container(&variant, &array);
        DBus.dbus_message_iter_close_container(iter, &variant);
    }

    // ---------- Odczyt argumentów ----------

    private static bool TryReadString(nint message, int index, out string value)
    {
        value = string.Empty;

        DBus.MessageIter iter;
        if (DBus.dbus_message_iter_init(message, &iter) == 0) return false;

        for (var i = 0; i < index; i++)
            if (DBus.dbus_message_iter_next(&iter) == 0) return false;

        var type = DBus.dbus_message_iter_get_arg_type(&iter);
        if (type != DBus.TypeString && type != DBus.TypeObjectPath) return false;

        nint pointer;
        DBus.dbus_message_iter_get_basic(&iter, &pointer);
        value = DBus.ReadString(pointer) ?? string.Empty;
        return true;
    }

    private static bool TryReadInt64(nint message, int index, out long value)
    {
        value = 0;

        DBus.MessageIter iter;
        if (DBus.dbus_message_iter_init(message, &iter) == 0) return false;

        for (var i = 0; i < index; i++)
            if (DBus.dbus_message_iter_next(&iter) == 0) return false;

        if (DBus.dbus_message_iter_get_arg_type(&iter) != DBus.TypeInt64) return false;

        long raw;
        DBus.dbus_message_iter_get_basic(&iter, &raw);
        value = raw;
        return true;
    }

    // ---------- Opis obiektu ----------

    /// <summary>
    /// Machine-readable description of the object, returned to whoever asks. Desktops mostly
    /// read the properties directly, but every diagnostic tool starts here.
    /// </summary>
    private const string Introspection = """
        <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN"
        "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
        <node>
          <interface name="org.freedesktop.DBus.Introspectable">
            <method name="Introspect"><arg name="xml" type="s" direction="out"/></method>
          </interface>
          <interface name="org.freedesktop.DBus.Properties">
            <method name="Get">
              <arg name="interface" type="s" direction="in"/>
              <arg name="property" type="s" direction="in"/>
              <arg name="value" type="v" direction="out"/>
            </method>
            <method name="GetAll">
              <arg name="interface" type="s" direction="in"/>
              <arg name="properties" type="a{sv}" direction="out"/>
            </method>
            <method name="Set">
              <arg name="interface" type="s" direction="in"/>
              <arg name="property" type="s" direction="in"/>
              <arg name="value" type="v" direction="in"/>
            </method>
            <signal name="PropertiesChanged">
              <arg name="interface" type="s"/>
              <arg name="changed" type="a{sv}"/>
              <arg name="invalidated" type="as"/>
            </signal>
          </interface>
          <interface name="org.mpris.MediaPlayer2">
            <method name="Raise"/>
            <method name="Quit"/>
            <property name="CanQuit" type="b" access="read"/>
            <property name="CanRaise" type="b" access="read"/>
            <property name="HasTrackList" type="b" access="read"/>
            <property name="Identity" type="s" access="read"/>
            <property name="DesktopEntry" type="s" access="read"/>
            <property name="SupportedUriSchemes" type="as" access="read"/>
            <property name="SupportedMimeTypes" type="as" access="read"/>
          </interface>
          <interface name="org.mpris.MediaPlayer2.Player">
            <method name="Next"/>
            <method name="Previous"/>
            <method name="Pause"/>
            <method name="PlayPause"/>
            <method name="Stop"/>
            <method name="Play"/>
            <method name="Seek"><arg name="offset" type="x" direction="in"/></method>
            <method name="SetPosition">
              <arg name="track" type="o" direction="in"/>
              <arg name="position" type="x" direction="in"/>
            </method>
            <property name="PlaybackStatus" type="s" access="read"/>
            <property name="LoopStatus" type="s" access="read"/>
            <property name="Rate" type="d" access="read"/>
            <property name="Shuffle" type="b" access="read"/>
            <property name="Metadata" type="a{sv}" access="read"/>
            <property name="Volume" type="d" access="read"/>
            <property name="Position" type="x" access="read"/>
            <property name="MinimumRate" type="d" access="read"/>
            <property name="MaximumRate" type="d" access="read"/>
            <property name="CanGoNext" type="b" access="read"/>
            <property name="CanGoPrevious" type="b" access="read"/>
            <property name="CanPlay" type="b" access="read"/>
            <property name="CanPause" type="b" access="read"/>
            <property name="CanSeek" type="b" access="read"/>
            <property name="CanControl" type="b" access="read"/>
          </interface>
        </node>
        """;

    public void Dispose()
    {
        if (!_running) return;
        _running = false;

        _thread.Join(1000);

        if (OperatingSystem.IsLinux())
        {
            using var path = new DBus.Utf8(ObjectPath);
            DBus.dbus_connection_unregister_object_path(_connection, path.Pointer);
            DBus.dbus_connection_unref(_connection);
        }

        if (_self.IsAllocated) _self.Free();
    }
}
