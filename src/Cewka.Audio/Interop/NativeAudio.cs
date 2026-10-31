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

    /// <summary>Zmienna środowiskowa włączająca puste wyjście; wartość „1” je włącza.</summary>
    internal const string NullOutputVariable = "CEWKA_NULL_AUDIO";

    // ---------- zgodność ----------

    /// <summary>
    /// Wersja zestawu funkcji, której oczekuje ten kod; ta sama stała jest w
    /// <c>native/cewka_audio.c</c>. Zmienia się przy każdej zmianie sygnatur lub znaczenia eksportów.
    /// </summary>
    internal const int AbiVersion = 2;

    [LibraryImport(Library, EntryPoint = "cewka_abi_version")]
    private static partial int NativeAbiVersion();

    private static readonly Lazy<string?> AbiMismatch = new(DescribeAbiMismatch);

    /// <summary>
    /// Biblioteka natywna nie jest w repozytorium i bywa starsza od kodu. Wywołanie funkcji
    /// o innej sygnaturze kończy się awarią procesu, więc niezgodność trzeba wykryć wcześniej.
    /// </summary>
    internal static void EnsureCompatible()
    {
        if (AbiMismatch.Value is { } message) throw new AudioException(message);
    }

    private static string? DescribeAbiMismatch()
    {
        int found;
        try
        {
            found = NativeAbiVersion();
        }
        catch (EntryPointNotFoundException)
        {
            found = 0;
        }

        return found == AbiVersion
            ? null
            : $"Biblioteka {FileName} ma wersję interfejsu {found}, a program wymaga {AbiVersion}; trzeba ją przebudować.";
    }

    // ---------- zaplecze ----------

    [LibraryImport(Library, EntryPoint = "cewka_use_null_backend")]
    private static partial int UseNullBackend(int on);

    private static readonly Lock BackendLock = new();
    private static bool _nullBackend = Environment.GetEnvironmentVariable(NullOutputVariable) == "1";
    private static bool _backendApplied;

    internal static bool NullBackend
    {
        get { lock (BackendLock) return _nullBackend; }
    }

    /// <summary>
    /// Przestawia zaplecze. Kontekst miniaudio powstaje przy pierwszym wyliczeniu urządzeń
    /// albo otwarciu urządzenia i od tej chwili zaplecza zmienić się nie da.
    /// </summary>
    internal static void SetNullBackend(bool on)
    {
        EnsureCompatible();

        lock (BackendLock)
        {
            if (UseNullBackend(on ? 1 : 0) != Success)
                throw new InvalidOperationException("Wyjście puste trzeba wybrać przed pierwszym użyciem urządzeń dźwiękowych.");

            _nullBackend = on;
            _backendApplied = true;
        }
    }

    /// <summary>Przekazuje wybór zaplecza do biblioteki przed pierwszym utworzeniem kontekstu.</summary>
    internal static void EnsureBackend()
    {
        EnsureCompatible();

        lock (BackendLock)
        {
            if (_backendApplied) return;

            ThrowIfFailed(UseNullBackend(_nullBackend ? 1 : 0), "Wybór zaplecza dźwięku");
            _backendApplied = true;
        }
    }

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

    /// <summary>
    /// Zdarzenie urządzenia zgłaszane przez <see cref="DeviceSetNotify"/>: urządzenie zatrzymało
    /// się bez polecenia. Może to być utrata albo przekierowanie, po którym ruszy samo.
    /// </summary>
    internal const int DeviceStoppedUnrequestedEvent = 1;

    [LibraryImport(Library, EntryPoint = "cewka_device_set_notify")]
    internal static partial int DeviceSetNotify(nint handle, delegate* unmanaged<void*, int, void> notify);

    [LibraryImport(Library, EntryPoint = "cewka_device_start")]
    internal static partial int DeviceStart(nint handle);

    [LibraryImport(Library, EntryPoint = "cewka_device_stop")]
    internal static partial int DeviceStop(nint handle);

    /// <summary>1, gdy urządzenie faktycznie pracuje.</summary>
    [LibraryImport(Library, EntryPoint = "cewka_device_is_started")]
    internal static partial int DeviceIsStarted(nint handle);

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
        delegate* unmanaged<void*, int, long> tell,
        void* user, uint outRate, uint outChannels, uint lpfOrder,
        out nint handle, out ulong lengthFrames, out uint sourceRate, out uint sourceChannels);

    [LibraryImport(Library, EntryPoint = "cewka_decoder_read")]
    internal static partial ulong DecoderRead(nint handle, float* output, ulong frameCount);

    [LibraryImport(Library, EntryPoint = "cewka_decoder_seek")]
    internal static partial int DecoderSeek(nint handle, ulong frameIndex);

    [LibraryImport(Library, EntryPoint = "cewka_decoder_cursor")]
    internal static partial ulong DecoderCursor(nint handle);

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
