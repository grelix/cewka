using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cewka.Audio.Decoding.Linux;

/// <summary>
/// The handful of GStreamer entry points the player needs.
///
/// <para>Istniejące wiązania GStreamera
/// dla platformy .NET obejmują całą bibliotekę i wymagają generowania kodu z plików GIR.
/// Tutaj potrzeba kilkunastu funkcji do zbudowania jednego potoku i wyciągania z niego próbek —
/// to samo rozstrzygnięcie, co przy Media Foundation w warstwie dla systemu Windows.</para>
///
/// <para>Funkcje takie jak <c>gst_sample_unref</c> są w nagłówkach funkcjami wplatanymi
/// i nie istnieją jako symbole biblioteki; zwalnianie idzie więc przez
/// <c>gst_mini_object_unref</c>, na które one się sprowadzają.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class Gst
{
    private const string Core = "libgstreamer-1.0.so.0";
    private const string App = "libgstapp-1.0.so.0";
    private const string Object = "libgobject-2.0.so.0";
    private const string Glib = "libglib-2.0.so.0";

    /// <summary>Values of <c>GstState</c>.</summary>
    internal const int StateNull = 1;
    internal const int StatePaused = 3;
    internal const int StatePlaying = 4;

    /// <summary>Values of <c>GstStateChangeReturn</c>.</summary>
    internal const int ChangeFailure = 0;
    internal const int ChangeSuccess = 1;
    internal const int ChangeNoPreroll = 3;

    /// <summary>Values of <c>GstMessageType</c>.</summary>
    internal const int MessageEos = 1 << 0;
    internal const int MessageError = 1 << 1;

    /// <summary>Codes of <c>GstStreamError</c> and <c>GstCoreError</c> that the decoder tells apart.</summary>
    internal const int StreamErrorTypeNotFound = 4;
    internal const int StreamErrorCodecNotFound = 6;
    internal const int CoreErrorMissingPlugin = 12;

    /// <summary>Value of <c>GST_FORMAT_TIME</c>.</summary>
    internal const int FormatTime = 3;

    /// <summary><c>GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_KEY_UNIT</c>.</summary>
    internal const int SeekFlush = 1 << 0;
    internal const int SeekKeyUnit = 1 << 2;

    /// <summary><c>GST_MAP_READ</c>.</summary>
    internal const int MapRead = 1;

    /// <summary>Blocking wait expressed in nanoseconds; <c>GST_CLOCK_TIME_NONE</c> would never time out.</summary>
    internal const ulong TenSeconds = 10UL * 1000 * 1000 * 1000;

    internal const ulong TwoHundredMilliseconds = 200UL * 1000 * 1000;

    [LibraryImport(Core, EntryPoint = "gst_init")]
    internal static partial void Init(nint argc, nint argv);

    [LibraryImport(Core, EntryPoint = "gst_parse_launch", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ParseLaunch(string description, out nint error);

    [LibraryImport(Core, EntryPoint = "gst_bin_get_by_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint BinGetByName(nint bin, string name);

    [LibraryImport(Core, EntryPoint = "gst_element_set_state")]
    internal static partial int SetState(nint element, int state);

    [LibraryImport(Core, EntryPoint = "gst_element_get_state")]
    internal static partial int GetState(nint element, out int state, out int pending, ulong timeoutNanoseconds);

    [LibraryImport(Core, EntryPoint = "gst_element_query_duration")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool QueryDuration(nint element, int format, out long duration);

    [LibraryImport(Core, EntryPoint = "gst_element_seek_simple")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SeekSimple(nint element, int format, int flags, long position);

    [LibraryImport(Core, EntryPoint = "gst_object_unref")]
    internal static partial void ObjectUnref(nint obj);

    [LibraryImport(Core, EntryPoint = "gst_mini_object_unref")]
    internal static partial void MiniObjectUnref(nint obj);

    [LibraryImport(Core, EntryPoint = "gst_sample_get_buffer")]
    internal static partial nint SampleGetBuffer(nint sample);

    [LibraryImport(Core, EntryPoint = "gst_sample_get_caps")]
    internal static partial nint SampleGetCaps(nint sample);

    [LibraryImport(Core, EntryPoint = "gst_caps_get_structure")]
    internal static partial nint CapsGetStructure(nint caps, uint index);

    [LibraryImport(Core, EntryPoint = "gst_structure_get_int", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool StructureGetInt(nint structure, string field, out int value);

    [LibraryImport(Core, EntryPoint = "gst_buffer_map")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool BufferMap(nint buffer, out MapInfo info, int flags);

    [LibraryImport(Core, EntryPoint = "gst_buffer_unmap")]
    internal static partial void BufferUnmap(nint buffer, ref MapInfo info);

    /// <summary>Returns null after the timeout as well as at the end of the stream; <see cref="AppSinkIsEos"/> tells them apart.</summary>
    [LibraryImport(App, EntryPoint = "gst_app_sink_try_pull_sample")]
    internal static partial nint AppSinkTryPullSample(nint appsink, ulong timeoutNanoseconds);

    [LibraryImport(Core, EntryPoint = "gst_element_get_bus")]
    internal static partial nint ElementGetBus(nint element);

    [LibraryImport(Core, EntryPoint = "gst_bus_pop_filtered")]
    internal static partial nint BusPopFiltered(nint bus, int types);

    [LibraryImport(Core, EntryPoint = "gst_message_parse_error")]
    internal static partial void MessageParseError(nint message, out nint error, out nint debug);

    [LibraryImport(Core, EntryPoint = "gst_stream_error_quark")]
    internal static partial uint StreamErrorQuark();

    [LibraryImport(Core, EntryPoint = "gst_core_error_quark")]
    internal static partial uint CoreErrorQuark();

    [LibraryImport(Glib, EntryPoint = "g_free")]
    internal static partial void Free(nint memory);

    [LibraryImport(App, EntryPoint = "gst_app_sink_is_eos")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool AppSinkIsEos(nint appsink);

    /// <summary>
    /// Sets one string property. The real function takes a variable argument list terminated
    /// by a null name; a signature fixed at exactly one pair matches that layout, and one pair
    /// is all this decoder ever needs — the path of the file to open.
    /// </summary>
    [LibraryImport(Object, EntryPoint = "g_object_set", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void ObjectSetString(nint obj, string name, string value, nint terminator);

    [LibraryImport(Object, EntryPoint = "g_error_free")]
    internal static partial void ErrorFree(nint error);

    /// <summary>
    /// Mirrors <c>GstMapInfo</c>. The trailing arrays are padding reserved by the library;
    /// they are never read here, but their size is part of the structure the caller allocates.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MapInfo
    {
        public nint Memory;
        public int Flags;
        public nint Data;
        public nuint Size;
        public nuint MaxSize;
        public nint UserData0, UserData1, UserData2, UserData3;
        public nint Reserved0, Reserved1, Reserved2, Reserved3;
    }

    /// <summary>Mirrors <c>GError</c>, read only to report why a pipeline refused to start.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Error
    {
        public uint Domain;
        public int Code;
        public nint Message;
    }

    /// <summary>Reads and frees a <c>GError</c> returned by the library.</summary>
    internal static string TakeMessage(nint error) => TakeError(error).Message;

    /// <summary>Reads and frees a <c>GError</c>, keeping its domain and code.</summary>
    internal static (uint Domain, int Code, string Message) TakeError(nint error)
    {
        if (error == nint.Zero) return (0, 0, "brak szczegółów");

        try
        {
            var value = Marshal.PtrToStructure<Error>(error);
            return (value.Domain, value.Code, Marshal.PtrToStringUTF8(value.Message) ?? "brak szczegółów");
        }
        finally
        {
            ErrorFree(error);
        }
    }

    /// <summary>
    /// Takes the oldest error message waiting on the bus, or null when there is none.
    /// The debug string is freed unread; the message itself is enough for the log.
    /// </summary>
    internal static (uint Domain, int Code, string Message)? PopError(nint bus)
    {
        if (bus == nint.Zero) return null;

        var message = BusPopFiltered(bus, MessageError);
        if (message == nint.Zero) return null;

        try
        {
            MessageParseError(message, out var error, out var debug);
            if (debug != nint.Zero) Free(debug);
            return TakeError(error);
        }
        finally
        {
            MiniObjectUnref(message);
        }
    }
}
