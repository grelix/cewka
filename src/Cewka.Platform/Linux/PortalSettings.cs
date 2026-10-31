using System.Runtime.Versioning;

namespace Cewka.Platform.Linux;

/// <summary>
/// Odczyt ustawień pulpitu przez portal <c>org.freedesktop.portal.Settings</c> na szynie sesji.
/// Portal działa także w piaskownicy (Flatpak), gdzie dconf nie jest dostępny wprost.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe class PortalSettings
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string Interface = "org.freedesktop.portal.Settings";

    /// <summary>Krótko: brak portalu nie może wstrzymać wątku, który pyta.</summary>
    private const int TimeoutMilliseconds = 500;

    private static readonly object Gate = new();

    /// <summary>
    /// Jedno połączenie na cały czas pracy: odczyt powtarza się co kilka sekund, a każde nowe
    /// połączenie to rejestracja na szynie. Zerwane zastępuje się nowym przy następnym odczycie.
    /// </summary>
    private static nint _connection;

    /// <summary>
    /// Wartość logiczna albo <c>null</c>, gdy szyny, portalu lub klucza nie ma.
    /// </summary>
    public static bool? ReadBoolean(string scope, string key)
    {
        lock (Gate)
        {
            if (_connection != 0 && DBus.dbus_connection_get_is_connected(_connection) == 0)
            {
                DBus.dbus_connection_close(_connection);
                DBus.dbus_connection_unref(_connection);
                _connection = 0;
            }

            if (_connection == 0)
            {
                DBus.Error error;
                DBus.dbus_error_init(&error);

                var connection = DBus.dbus_bus_get_private(DBus.BusSession, &error);
                if (connection == 0)
                {
                    DBus.TakeMessage(&error);
                    return null;
                }

                DBus.dbus_connection_set_exit_on_disconnect(connection, 0);
                _connection = connection;
            }

            return Read(_connection, scope, key);
        }
    }

    private static bool? Read(nint connection, string scope, string key)
    {
        using var destination = new DBus.Utf8(Destination);
        using var path = new DBus.Utf8(ObjectPath);
        using var iface = new DBus.Utf8(Interface);
        using var method = new DBus.Utf8("Read");

        var call = DBus.dbus_message_new_method_call(
            destination.Pointer, path.Pointer, iface.Pointer, method.Pointer);
        if (call == 0) return null;

        try
        {
            DBus.MessageIter arguments;
            DBus.dbus_message_iter_init_append(call, &arguments);
            AppendString(&arguments, scope);
            AppendString(&arguments, key);

            DBus.Error error;
            DBus.dbus_error_init(&error);

            var reply = DBus.dbus_connection_send_with_reply_and_block(
                connection, call, TimeoutMilliseconds, &error);

            if (reply == 0)
            {
                DBus.TakeMessage(&error);
                return null;
            }

            try
            {
                DBus.MessageIter iter;
                if (DBus.dbus_message_iter_init(reply, &iter) == 0) return null;
                return UnwrapBoolean(iter);
            }
            finally
            {
                DBus.dbus_message_unref(reply);
            }
        }
        finally
        {
            DBus.dbus_message_unref(call);
        }
    }

    /// <summary>
    /// Metoda <c>Read</c> zwraca wartość opakowaną w wariant, a starsze wersje portalu
    /// w wariant podwójny, więc warianty zdejmowane są aż do wartości.
    /// </summary>
    private static bool? UnwrapBoolean(DBus.MessageIter iter)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            var type = DBus.dbus_message_iter_get_arg_type(&iter);

            if (type == DBus.TypeBoolean)
            {
                uint value = 0;
                DBus.dbus_message_iter_get_basic(&iter, &value);
                return value != 0;
            }

            if (type != DBus.TypeVariant) return null;

            DBus.MessageIter inner;
            DBus.dbus_message_iter_recurse(&iter, &inner);
            iter = inner;
        }

        return null;
    }

    private static void AppendString(DBus.MessageIter* iter, string value)
    {
        using var text = new DBus.Utf8(value);
        var pointer = text.Pointer;
        DBus.dbus_message_iter_append_basic(iter, DBus.TypeString, &pointer);
    }
}
