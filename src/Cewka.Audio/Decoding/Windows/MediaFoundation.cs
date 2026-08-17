using System.Runtime.InteropServices;

namespace Cewka.Audio.Decoding.Windows;

/// <summary>
/// The slice of Media Foundation needed to decode AAC, M4A and ALAC on Windows.
///
/// <para><b>Dlaczego tak.</b> Interfejsy COM Media Foundation są duże, a kolejność metod
/// w tablicy wirtualnej ma znaczenie — pomyłka nie daje błędu kompilacji, tylko wywołanie
/// zupełnie innej funkcji. Metody nieużywane są tu zadeklarowane jako puste miejsca
/// o właściwej sygnaturze, żeby przesunięcia pozostałych się zgadzały. To standardowa
/// technika przy ręcznym opisywaniu interfejsów COM.</para>
/// </summary>
internal static class MediaFoundation
{
    // ---------- stale ----------

    internal const int MF_VERSION = 0x00020070;
    internal const int MFSTARTUP_NOSOCKET = 1;

    internal const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    internal const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    internal const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;

    internal const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    internal const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x10;

    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    internal static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    internal static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    internal static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71");

    internal static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    internal static readonly Guid GUID_NULL = Guid.Empty;

    // ---------- funkcje ----------

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int MFCreateSourceReaderFromURL(
        string url, IntPtr attributes, out IMFSourceReader reader);

    // ---------- interfejsy ----------

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        // Kolejnosc metod odpowiada tablicy wirtualnej; nieuzywane sa miejscami rezerwowymi.
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr written);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr destination);
    }

    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        // Metody odziedziczone musza zostac powtorzone, zeby zachowac przesuniecia.
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IntPtr theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out long value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
        [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr written);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, long value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IntPtr destination);

        [PreserveSig] int GetMajorType(out Guid type);
        [PreserveSig] int IsCompressedFormat(out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out int flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr data);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr data);
    }

    [ComImport]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint streamIndex, out bool selected);
        [PreserveSig] int SetStreamSelection(uint streamIndex, bool selected);
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint mediaTypeIndex, out IMFMediaType mediaType);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref PropVariant position);
        [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex,
            out uint streamFlags, out long timestamp, out IMFSample? sample);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid riid, out IntPtr unknown);
        [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute, out PropVariant value);
    }

    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample : IMFAttributes
    {
        // Ponownie: wszystkie metody IMFAttributes, zeby przesuniecia sie zgadzaly.
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IntPtr theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out long value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
        [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr written);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, long value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IntPtr destination);

        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    /// <summary>
    /// Only the shape needed here: a type tag and a 64-bit payload. Media Foundation returns
    /// durations and takes seek positions through this structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Type;
        private readonly ushort _reserved1;
        private readonly ushort _reserved2;
        private readonly ushort _reserved3;
        public long Value;
        private readonly long _padding;

        internal const ushort VT_EMPTY = 0;
        internal const ushort VT_I8 = 20;
        internal const ushort VT_UI8 = 21;

        internal static PropVariant FromHundredNanoseconds(long value) => new()
        {
            Type = VT_I8,
            Value = value,
        };
    }
}
