using System.Runtime.Versioning;

namespace Cewka.Platform.Linux;

/// <summary>
/// Odpowiedź usługi: pierwszy argument (napis albo liczba, inny typ jako null) albo nazwa
/// i opis błędu.
/// </summary>
internal sealed record DBusReply(object? Value, string? ErrorName = null, string ErrorMessage = "")
{
    public bool IsError => ErrorName is not null;

    public static DBusReply Error(string name, string message) => new(null, name, message);
}

/// <summary>
/// Prywatne połączenie blokady z szyną sesji. Wydzielone, żeby decyzje
/// <see cref="LinuxDisplayWakeLock"/> dało się sprawdzić z atrapą zamiast szyny.
/// </summary>
internal interface IInhibitTransport
{
    bool IsConnected { get; }

    /// <summary>Żywe połączenie, w razie potrzeby nowe; false, gdy szyny sesji nie ma.</summary>
    bool Connect();

    /// <summary>Zamyka i porzuca połączenie. Usługi zdejmują wtedy wszystkie jego blokady.</summary>
    void Drop();

    DBusReply Call(DBusCall call);

    /// <summary>
    /// Kod sygnału <c>Response</c> żądania portalu o danej ścieżce albo null, gdy sygnał nie
    /// przyszedł w danym czasie.
    /// </summary>
    uint? WaitForResponse(string handle, int timeoutMilliseconds);
}

/// <summary>
/// Połączenie przez <c>libdbus</c>. Nie jest bezpieczne wątkowo; wywołuje je tylko
/// <see cref="LinuxDisplayWakeLock"/> pod swoją blokadą.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class DBusInhibitTransport : IInhibitTransport
{
    /// <summary>Krótko: wątek interfejsu czeka na odpowiedź przy przejściu w pełny ekran.</summary>
    private const int TimeoutMilliseconds = 1000;

    private nint _connection;

    public bool IsConnected => _connection != 0 && DBus.dbus_connection_get_is_connected(_connection) != 0;

    /// <summary>Połączenie zerwane, np. po restarcie szyny, zastępuje nowe.</summary>
    public bool Connect()
    {
        if (IsConnected) return true;

        Drop();

        DBus.Error error;
        DBus.dbus_error_init(&error);

        var connection = DBus.dbus_bus_get_private(DBus.BusSession, &error);
        if (connection == 0)
        {
            Console.Error.WriteLine($"[cewka] blokada wygaszania ekranu: brak szyny sesji D-Bus: {DBus.TakeMessage(&error)}.");
            return false;
        }

        // Rozłączenie z szyną nie może zakończyć procesu.
        DBus.dbus_connection_set_exit_on_disconnect(connection, 0);
        _connection = connection;

        // Reguła trafia na szynę przed pierwszym wywołaniem portalu, więc obejmie jego sygnały.
        if (UniqueName is { } unique)
        {
            using var rule = new DBus.Utf8(DisplayInhibitRules.PortalResponseRule(unique));
            DBus.dbus_bus_add_match(connection, rule.Pointer, null);
            DBus.dbus_connection_flush(connection);
        }

        return true;
    }

    /// <summary>Nazwa unikalna połączenia na szynie, np. <c>:1.42</c>.</summary>
    internal string? UniqueName =>
        _connection == 0 ? null : DBus.ReadString(DBus.dbus_bus_get_unique_name(_connection));

    public void Drop()
    {
        if (_connection == 0) return;

        DBus.dbus_connection_close(_connection);
        DBus.dbus_connection_unref(_connection);
        _connection = 0;
    }

    public DBusReply Call(DBusCall call)
    {
        var message = call.ToMessage();
        if (message == 0) return DBusReply.Error(DisplayInhibitRules.ErrorNoMemory, "brak pamięci");

        try
        {
            DBus.Error raw;
            DBus.dbus_error_init(&raw);

            var reply = DBus.dbus_connection_send_with_reply_and_block(
                _connection, message, TimeoutMilliseconds, &raw);

            if (reply == 0)
            {
                var (name, text) = DBus.TakeError(&raw);
                return DBusReply.Error(name, text);
            }

            try
            {
                return new DBusReply(ReadFirst(reply));
            }
            finally
            {
                DBus.dbus_message_unref(reply);
            }
        }
        finally
        {
            DBus.dbus_message_unref(message);
        }
    }

    /// <summary>
    /// Czyta kolejkę przychodzących aż do sygnału albo upływu czasu. Inne wiadomości (np.
    /// <c>NameAcquired</c>) nikogo tu nie interesują, więc są odrzucane. Zero czasu też raz
    /// odczytuje gniazdo, bez czekania.
    /// </summary>
    public uint? WaitForResponse(string handle, int timeoutMilliseconds)
    {
        if (_connection == 0) return null;

        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        using var iface = new DBus.Utf8(DisplayInhibitRules.RequestInterface);
        using var member = new DBus.Utf8(DisplayInhibitRules.ResponseMember);
        var read = false;

        while (true)
        {
            nint message;
            while ((message = DBus.dbus_connection_pop_message(_connection)) != 0)
            {
                try
                {
                    if (DBus.dbus_message_is_signal(message, iface.Pointer, member.Pointer) != 0
                        && DBus.ReadString(DBus.dbus_message_get_path(message)) == handle)
                    {
                        // Nieczytelny kod nie potwierdza blokady, więc liczy się jak odmowa.
                        return ReadFirst(message) as uint? ?? uint.MaxValue;
                    }
                }
                finally
                {
                    DBus.dbus_message_unref(message);
                }
            }

            var left = Math.Max(0, deadline - Environment.TickCount64);
            if (left == 0 && read) return null;
            read = true;

            // Zero znaczy zamknięte połączenie: sygnał już nie przyjdzie.
            if (DBus.dbus_connection_read_write(_connection, (int)left) == 0) return null;
        }
    }

    private static object? ReadFirst(nint reply)
    {
        DBus.MessageIter iter;
        if (DBus.dbus_message_iter_init(reply, &iter) == 0) return null;

        switch (DBus.dbus_message_iter_get_arg_type(&iter))
        {
            case DBus.TypeObjectPath or DBus.TypeString:
                nint pointer;
                DBus.dbus_message_iter_get_basic(&iter, &pointer);
                return DBus.ReadString(pointer);

            case DBus.TypeUInt32:
                uint value;
                DBus.dbus_message_iter_get_basic(&iter, &value);
                return value;

            default:
                return null;
        }
    }
}
