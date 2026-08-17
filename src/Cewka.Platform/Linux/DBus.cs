using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Cewka.Platform.Linux;

/// <summary>
/// The part of libdbus needed to expose one object on the session bus.
///
/// <para><b>Dlaczego bezpośrednio.</b> To samo rozstrzygnięcie, co przy Media Foundation,
/// panelu multimediów systemu Windows i rejestrze: biblioteka <c>libdbus-1</c> jest obecna
/// wszędzie tam, gdzie MPRIS w ogóle ma znaczenie, czyli na każdym pulpicie Linuksa. Wciąganie
/// zależności dla jednego obiektu na szynie byłoby nieproporcjonalne.</para>
///
/// <para><b>Wątek.</b> Połączenie nie jest bezpieczne wątkowo. Wszystkie wywołania poniżej
/// wykonuje wyłącznie wątek utworzony przez <see cref="MprisService"/>; stan przekazywany jest
/// do niego przez podmianę niezmiennego zdjęcia, a nie przez blokady.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe class DBus
{
    private const string Library = "libdbus-1.so.3";

    /// <summary>Kody typów zapisane w protokole jako pojedyncze znaki.</summary>
    internal const int TypeInvalid = 0;
    internal const int TypeBoolean = 'b';
    internal const int TypeInt32 = 'i';
    internal const int TypeInt64 = 'x';
    internal const int TypeDouble = 'd';
    internal const int TypeString = 's';
    internal const int TypeObjectPath = 'o';
    internal const int TypeVariant = 'v';
    internal const int TypeArray = 'a';
    internal const int TypeDictEntry = 'e';

    internal const int BusSession = 0;

    /// <summary><c>DBUS_NAME_FLAG_REPLACE_EXISTING | DBUS_NAME_FLAG_DO_NOT_QUEUE</c>.</summary>
    internal const uint NameFlags = 2 | 4;

    /// <summary><c>DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER</c>.</summary>
    internal const int NameReplyPrimaryOwner = 1;

    /// <summary><c>DBUS_HANDLER_RESULT_HANDLED</c> i <c>..._NOT_YET_HANDLED</c>.</summary>
    internal const int Handled = 0;
    internal const int NotHandled = 1;

    [DllImport(Library)]
    internal static extern void dbus_error_init(Error* error);

    [DllImport(Library)]
    internal static extern int dbus_error_is_set(Error* error);

    [DllImport(Library)]
    internal static extern void dbus_error_free(Error* error);

    [DllImport(Library)]
    internal static extern nint dbus_bus_get(int type, Error* error);

    [DllImport(Library)]
    internal static extern int dbus_bus_request_name(nint connection, byte* name, uint flags, Error* error);

    [DllImport(Library)]
    internal static extern int dbus_connection_register_object_path(
        nint connection, byte* path, ObjectPathVTable* vtable, nint userData);

    [DllImport(Library)]
    internal static extern int dbus_connection_unregister_object_path(nint connection, byte* path);

    [DllImport(Library)]
    internal static extern int dbus_connection_read_write_dispatch(nint connection, int timeoutMilliseconds);

    [DllImport(Library)]
    internal static extern void dbus_connection_set_exit_on_disconnect(nint connection, int exit);

    [DllImport(Library)]
    internal static extern int dbus_connection_send(nint connection, nint message, uint* serial);

    [DllImport(Library)]
    internal static extern void dbus_connection_flush(nint connection);

    [DllImport(Library)]
    internal static extern void dbus_connection_unref(nint connection);

    [DllImport(Library)]
    internal static extern nint dbus_message_new_method_return(nint methodCall);

    [DllImport(Library)]
    internal static extern nint dbus_message_new_error(nint reply, byte* errorName, byte* errorMessage);

    [DllImport(Library)]
    internal static extern nint dbus_message_new_signal(byte* path, byte* iface, byte* name);

    [DllImport(Library)]
    internal static extern void dbus_message_unref(nint message);

    [DllImport(Library)]
    internal static extern nint dbus_message_get_interface(nint message);

    [DllImport(Library)]
    internal static extern nint dbus_message_get_member(nint message);

    [DllImport(Library)]
    internal static extern void dbus_message_iter_init_append(nint message, MessageIter* iter);

    [DllImport(Library)]
    internal static extern int dbus_message_iter_init(nint message, MessageIter* iter);

    [DllImport(Library)]
    internal static extern int dbus_message_iter_get_arg_type(MessageIter* iter);

    [DllImport(Library)]
    internal static extern void dbus_message_iter_get_basic(MessageIter* iter, void* value);

    [DllImport(Library)]
    internal static extern int dbus_message_iter_next(MessageIter* iter);

    [DllImport(Library)]
    internal static extern int dbus_message_iter_append_basic(MessageIter* iter, int type, void* value);

    [DllImport(Library)]
    internal static extern int dbus_message_iter_open_container(
        MessageIter* iter, int type, byte* containedSignature, MessageIter* sub);

    [DllImport(Library)]
    internal static extern int dbus_message_iter_close_container(MessageIter* iter, MessageIter* sub);

    /// <summary>
    /// Mirrors <c>DBusError</c>. Only the two leading pointers are ever read; the rest is the
    /// library's own bookkeeping and exists here so the structure is the right size.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Error
    {
        public nint Name;
        public nint Message;
        public uint Bits;
        public uint Padding;
        public nint Reserved;
    }

    /// <summary>
    /// Mirrors <c>DBusMessageIter</c>: an opaque block the library writes into. Its declared
    /// size matters, its contents do not.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MessageIter
    {
        public nint Dummy1, Dummy2;
        public uint Dummy3, Dummy4, Dummy5, Dummy6, Dummy7, Dummy8, Dummy9, Dummy10;
        public int Pad1;
        public nint Pad2, Pad3;
    }

    /// <summary>Mirrors <c>DBusObjectPathVTable</c>: two handlers followed by reserved slots.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ObjectPathVTable
    {
        public nint Unregister;
        public nint Message;
        public nint Reserved1, Reserved2, Reserved3, Reserved4;
    }

    /// <summary>
    /// A null-terminated UTF-8 copy of a string, kept alive for as long as the caller needs it.
    /// The library takes plain <c>const char*</c> everywhere and copies what it is given, but
    /// only during the call — so the buffer has to outlive the call and nothing more.
    /// </summary>
    internal readonly struct Utf8 : IDisposable
    {
        private readonly nint _pointer;

        public Utf8(string value)
        {
            var bytes = Encoding.UTF8.GetByteCount(value);
            _pointer = Marshal.AllocHGlobal(bytes + 1);

            var span = new Span<byte>((void*)_pointer, bytes + 1);
            Encoding.UTF8.GetBytes(value, span);
            span[bytes] = 0;
        }

        public byte* Pointer => (byte*)_pointer;

        public void Dispose() => Marshal.FreeHGlobal(_pointer);
    }

    internal static string? ReadString(nint pointer) => Marshal.PtrToStringUTF8(pointer);

    /// <summary>Reads and clears an error, returning its message.</summary>
    internal static string TakeMessage(Error* error)
    {
        if (dbus_error_is_set(error) == 0) return "brak szczegółów";

        var message = ReadString(error->Message) ?? "brak szczegółów";
        dbus_error_free(error);
        return message;
    }
}
