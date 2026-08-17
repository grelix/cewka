using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cewka.Audio.Interop;

/// <summary>
/// Declarations of the <c>cewka_audio</c> shim built from <c>native/cewka_audio.c</c>.
/// Every entry point takes only primitives and opaque pointers, so nothing here depends
/// on miniaudio's struct layout.
/// </summary>
internal static unsafe partial class NativeAudio
{
    internal const string Library = "cewka_audio";

    // ---------- urządzenia ----------

    [LibraryImport(Library, EntryPoint = "cewka_devices_refresh")]
    internal static partial int DevicesRefresh(out int count);

    [LibraryImport(Library, EntryPoint = "cewka_devices_name")]
    internal static partial int DevicesName(int index, byte* buffer, int bufferSize);

    [LibraryImport(Library, EntryPoint = "cewka_devices_is_default")]
    internal static partial int DevicesIsDefault(int index);

    [LibraryImport(Library, EntryPoint = "cewka_device_create")]
    internal static partial int DeviceCreate(
        int deviceIndex, uint sampleRate, uint channels, uint periodSizeInFrames,
        delegate* unmanaged<void*, float*, uint, void> callback, void* user, out nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_device_start")]
    internal static partial int DeviceStart(nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_device_stop")]
    internal static partial int DeviceStop(nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_device_destroy")]
    internal static partial void DeviceDestroy(nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_device_sample_rate")]
    internal static partial uint DeviceSampleRate(nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_device_channels")]
    internal static partial uint DeviceChannels(nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_device_period")]
    internal static partial uint DevicePeriod(nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_device_name")]
    internal static partial int DeviceName(nint handle, byte* buffer, int bufferSize);

    // ---------- dekoder ----------

    [LibraryImport(Library, EntryPoint = "cewka_decoder_open")]
    internal static partial int DecoderOpen(
        delegate* unmanaged<void*, void*, nuint, nuint> read,
        delegate* unmanaged<void*, long, int, int> seek,
        void* user, uint outRate, uint outChannels, uint lpfOrder,
        out nint handle, out ulong lengthFrames, out uint sourceRate, out uint sourceChannels);

    [LibraryImport(Library, EntryPoint = "cewka_decoder_read")]
    internal static partial ulong DecoderRead(nint handle, float* output, ulong frameCount);

    [LibraryImport(Library, EntryPoint = "cewka_decoder_seek")]
    internal static partial int DecoderSeek(nint handle, ulong frameIndex);

    [LibraryImport(Library, EntryPoint = "cewka_decoder_close")]
    internal static partial void DecoderClose(nint handle);

    // ---------- resampler ----------

    [LibraryImport(Library, EntryPoint = "cewka_resampler_create")]
    internal static partial int ResamplerCreate(
        uint channels, uint rateIn, uint rateOut, uint lpfOrder, out nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_resampler_process")]
    internal static partial int ResamplerProcess(
        nint handle, float* input, ref ulong frameCountIn, float* output, ref ulong frameCountOut);

    [LibraryImport(Library, EntryPoint = "cewka_resampler_required_input")]
    internal static partial ulong ResamplerRequiredInput(nint handle, ulong outputFrameCount);

    [LibraryImport(Library, EntryPoint = "cewka_resampler_destroy")]
    internal static partial void ResamplerDestroy(nint handle);

    // ---------- diagnostyka ----------

    [LibraryImport(Library, EntryPoint = "cewka_version")]
    internal static partial nint VersionPointer();

    [LibraryImport(Library, EntryPoint = "cewka_result_description")]
    internal static partial nint ResultDescriptionPointer(int result);

    /// <summary>Version string of the bundled miniaudio build.</summary>
    internal static string Version() => Marshal.PtrToStringUTF8(VersionPointer()) ?? "?";

    /// <summary>Human-readable form of a miniaudio result code.</summary>
    internal static string Describe(int result) =>
        Marshal.PtrToStringUTF8(ResultDescriptionPointer(result)) ?? $"kod {result}";

    /// <summary>miniaudio returns zero on success; every other value is an error.</summary>
    internal const int Success = 0;

    internal static void ThrowIfFailed(int result, string operation)
    {
        if (result == Success) return;
        throw new AudioException($"{operation} — {Describe(result)} (kod {result}).");
    }

    // ---------- odnajdywanie biblioteki ----------

    // A type initialiser rather than [ModuleInitializer]: the first call into any member
    // below triggers it, which is exactly when the resolver has to be in place, and it
    // avoids forcing initialisation on assemblies that never touch audio.
    static NativeAudio() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeAudio).Assembly, Resolve);

    /// <summary>
    /// Looks for the shim next to the application first, then in the RID-specific layout
    /// used during development. The default probing logic handles neither reliably: a
    /// single-file build extracts the library beside the executable, while an ordinary
    /// build leaves it under <c>runtimes/{rid}/native</c>.
    /// </summary>
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return nint.Zero;

        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Fall back to the platform loader, which searches PATH and the system directories.
        return NativeLibrary.TryLoad(FileName, assembly, path, out var fallback) ? fallback : nint.Zero;
    }

    private static string FileName => OperatingSystem.IsWindows()
        ? "cewka_audio.dll"
        : "libcewka_audio.so";

    private static string Rid => OperatingSystem.IsWindows()
        ? RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64"
        : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

    private static IEnumerable<string> CandidatePaths()
    {
        var baseDirectory = AppContext.BaseDirectory;

        yield return Path.Combine(baseDirectory, FileName);
        yield return Path.Combine(baseDirectory, "runtimes", Rid, "native", FileName);

        // Uruchomienie z katalogu projektu podczas prac deweloperskich.
        //
        // W wydaniu jednoplikowym Assembly.Location zwraca pusty napis i analizator slusznie
        // o tym uprzedza; ta sciezka jest tu wylacznie dla zwyklej kompilacji, a pusty wynik
        // jest obslugiwany linijke nizej.
#pragma warning disable IL3000
        var assemblyDirectory = Path.GetDirectoryName(typeof(NativeAudio).Assembly.Location);
#pragma warning restore IL3000

        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, FileName);
            yield return Path.Combine(assemblyDirectory, "runtimes", Rid, "native", FileName);
        }
    }
}
