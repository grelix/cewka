using System.Runtime.Versioning;

namespace Cewka.Platform.Linux;

/// <summary>Argument wywołania metody na szynie; <see cref="Signature"/> to jego zapis w protokole.</summary>
internal abstract record DBusArgument(string Signature);

internal sealed record DBusString(string Value) : DBusArgument("s");

internal sealed record DBusUInt32(uint Value) : DBusArgument("u");

/// <summary>Słownik <c>a{sv}</c>, w którym każda wartość jest napisem.</summary>
internal sealed record DBusStringOptions(IReadOnlyList<KeyValuePair<string, string>> Entries)
    : DBusArgument("a{sv}");

/// <summary>
/// Wywołanie metody opisane bez <c>libdbus</c>, żeby jego treść dało się sprawdzić na każdym
/// systemie. Na wiadomość zamienia je dopiero <see cref="ToMessage"/>.
/// </summary>
internal sealed record DBusCall(
    string Destination,
    string Path,
    string Interface,
    string Member,
    IReadOnlyList<DBusArgument> Arguments)
{
    public string Signature => string.Concat(Arguments.Select(argument => argument.Signature));

    /// <summary>
    /// Nowa wiadomość, którą zwalnia wywołujący (<c>dbus_message_unref</c>), albo zero, gdy
    /// zabrakło pamięci.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public unsafe nint ToMessage()
    {
        using var destination = new DBus.Utf8(Destination);
        using var path = new DBus.Utf8(Path);
        using var iface = new DBus.Utf8(Interface);
        using var member = new DBus.Utf8(Member);

        var message = DBus.dbus_message_new_method_call(
            destination.Pointer, path.Pointer, iface.Pointer, member.Pointer);
        if (message == 0) return 0;

        DBus.MessageIter iter;
        DBus.dbus_message_iter_init_append(message, &iter);

        foreach (var argument in Arguments)
        {
            switch (argument)
            {
                case DBusString text:
                    AppendString(&iter, text.Value);
                    break;

                case DBusUInt32 number:
                    var value = number.Value;
                    DBus.dbus_message_iter_append_basic(&iter, DBus.TypeUInt32, &value);
                    break;

                case DBusStringOptions options:
                    AppendOptions(&iter, options.Entries);
                    break;
            }
        }

        return message;
    }

    [SupportedOSPlatform("linux")]
    private static unsafe void AppendOptions(DBus.MessageIter* iter, IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        using var entrySignature = new DBus.Utf8("{sv}");
        DBus.MessageIter array;
        DBus.dbus_message_iter_open_container(iter, DBus.TypeArray, entrySignature.Pointer, &array);

        foreach (var (key, value) in entries)
        {
            DBus.MessageIter entry;
            DBus.dbus_message_iter_open_container(&array, DBus.TypeDictEntry, null, &entry);
            AppendString(&entry, key);

            using var variantSignature = new DBus.Utf8("s");
            DBus.MessageIter variant;
            DBus.dbus_message_iter_open_container(&entry, DBus.TypeVariant, variantSignature.Pointer, &variant);
            AppendString(&variant, value);
            DBus.dbus_message_iter_close_container(&entry, &variant);

            DBus.dbus_message_iter_close_container(&array, &entry);
        }

        DBus.dbus_message_iter_close_container(iter, &array);
    }

    [SupportedOSPlatform("linux")]
    private static unsafe void AppendString(DBus.MessageIter* iter, string value)
    {
        using var text = new DBus.Utf8(value);

        // append_basic dla napisów przyjmuje wskaźnik na wskaźnik, nie sam napis.
        var pointer = (nint)text.Pointer;
        DBus.dbus_message_iter_append_basic(iter, DBus.TypeString, &pointer);
    }
}
